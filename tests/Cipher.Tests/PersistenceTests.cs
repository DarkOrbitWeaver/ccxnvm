using Cipher;
using System.Security.Cryptography;

namespace Cipher.Tests;

public class PersistenceTests {
    [Fact]
    public void ContactsAndGroupsRoundTripCorrectly() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);
        var contact = CreateContact();
        var group = CreateGroup(contact.UserId);

        using (var vault = new Vault()) {
            vault.Open(vaultPath, key);
            vault.SaveContact(contact);
            vault.SaveGroup(group);
        }

        using var reopened = new Vault();
        reopened.Open(vaultPath, key);

        var contacts = reopened.LoadContacts();
        var groups = reopened.LoadGroups();

        Assert.Single(contacts);
        Assert.Equal(contact.UserId, contacts[0].UserId);
        Assert.Equal(contact.DisplayName, contacts[0].DisplayName);
        Assert.Equal(contact.ConversationId, contacts[0].ConversationId);

        Assert.Single(groups);
        Assert.Equal(group.GroupId, groups[0].GroupId);
        Assert.Equal(group.Name, groups[0].Name);
        Assert.Equal(group.MemberIds, groups[0].MemberIds);
        Assert.Equal(Convert.ToBase64String(group.GroupKey), Convert.ToBase64String(groups[0].GroupKey));
    }

    [Fact]
    public void NextSeqNumPersistsAcrossReopen() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);
        const string convId = "dm:alice:bob";

        using (var vault = new Vault()) {
            vault.Open(vaultPath, key);
            Assert.Equal(1, vault.NextSeqNum(convId));
            Assert.Equal(2, vault.NextSeqNum(convId));
        }

        using var reopened = new Vault();
        reopened.Open(vaultPath, key);

        Assert.Equal(3, reopened.NextSeqNum(convId));
    }

    [Fact]
    public void GroupOutboxRoundTripPreservesRecipients() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);
        var members = new List<string> { "alice", "bob", "charlie" };

        using var vault = new Vault();
        vault.Open(vaultPath, key);
        vault.EnqueueOutbox(
            "group-out-1",
            "group-1",
            "payload",
            "sig",
            42,
            ConversationType.Group,
            "group-1",
            members);

        var queued = vault.LoadOutbox();

        Assert.Single(queued);
        Assert.Equal(ConversationType.Group, queued[0].ConvType);
        Assert.Equal("group-1", queued[0].GroupId);
        Assert.Equal(members, queued[0].MemberIds);
    }

    static Contact CreateContact() {
        var (_, signPub) = Crypto.GenerateSigningKeys();
        var (_, dhPub) = Crypto.GenerateDhKeys();

        return new Contact {
            UserId = Crypto.DeriveUserId(signPub),
            DisplayName = "Bob",
            SignPubKey = signPub,
            DhPubKey = dhPub,
            ConversationId = "dm:alice:bob",
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    static GroupInfo CreateGroup(string memberId) => new() {
        GroupId = Guid.NewGuid().ToString("N"),
        Name = "Friends",
        MemberIds = new List<string> { "me", memberId },
        GroupKey = RandomNumberGenerator.GetBytes(32),
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };

    static string CreateTempDirectory() {
        var path = Path.Combine(Path.GetTempPath(), "cipher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
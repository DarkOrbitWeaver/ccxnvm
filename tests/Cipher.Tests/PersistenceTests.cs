using Cipher;
using Microsoft.Data.Sqlite;
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
        Assert.True(contacts[0].IsVerified);
        Assert.Equal(Convert.ToBase64String(contact.PendingSignPubKey), Convert.ToBase64String(contacts[0].PendingSignPubKey));
        Assert.Equal(Convert.ToBase64String(contact.PendingDhPubKey), Convert.ToBase64String(contacts[0].PendingDhPubKey));
        Assert.Equal(contact.KeyChangedAt, contacts[0].KeyChangedAt);

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

    [Fact]
    public void OpeningVaultRepairsInvalidOutboxRowsAndCreatesBackup() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);

        using (var vault = new Vault()) {
            vault.Open(vaultPath, key);
            vault.SaveMessage(new Message {
                Id = "seed-msg",
                ConversationId = "dm:a:b",
                SenderId = "a",
                Content = "seed",
                Timestamp = 1,
                SeqNum = 1
            });
        }

        using (var raw = new SqliteConnection($"Data Source={vaultPath};")) {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO outbox(id, recipient_id_enc, payload_enc, sig_enc, seq_num, conv_type, created_at, attempts)
                VALUES ('broken-1', X'', X'', X'', 0, 0, 1, 0)";
            cmd.ExecuteNonQuery();
        }

        using var reopened = new Vault();
        reopened.Open(vaultPath, key);

        Assert.Empty(reopened.LoadOutbox());
        Assert.NotNull(reopened.LastMaintenanceBackupPath);
        Assert.True(File.Exists(reopened.LastMaintenanceBackupPath!));
        Assert.Contains(reopened.LastMaintenanceActions, action => action.Contains("startup repair", StringComparison.OrdinalIgnoreCase));
    }

    static Contact CreateContact() {
        var (_, signPub) = Crypto.GenerateSigningKeys();
        var (_, dhPub) = Crypto.GenerateDhKeys();

        return new Contact {
            UserId = Crypto.DeriveUserId(signPub),
            DisplayName = "Bob",
            SignPubKey = signPub,
            DhPubKey = dhPub,
            PendingSignPubKey = RandomNumberGenerator.GetBytes(91),
            PendingDhPubKey = RandomNumberGenerator.GetBytes(91),
            IsVerified = true,
            KeyChangedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
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

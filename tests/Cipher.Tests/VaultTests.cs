using Cipher;
using System.Security.Cryptography;

namespace Cipher.Tests;

public class VaultTests {
    [Fact]
    public void IdentityRequiresCorrectKey() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var correctKey = RandomNumberGenerator.GetBytes(32);
        var wrongKey = RandomNumberGenerator.GetBytes(32);
        var user = CreateUser("https://cipher-relay.onrender.com", "alice");

        using (var vault = new Vault()) {
            vault.Open(vaultPath, correctKey);
            vault.SaveIdentity(user);
        }

        using (var opened = new Vault()) {
            opened.Open(vaultPath, correctKey);
            var loaded = opened.LoadIdentity();
            Assert.NotNull(loaded);
            Assert.Equal(user.UserId, loaded!.UserId);
        }

        using (var openedWrong = new Vault()) {
            openedWrong.Open(vaultPath, wrongKey);
            Assert.Null(openedWrong.LoadIdentity());
        }
    }

    [Fact]
    public void MessagePagingSupportsLazyLoadingScenarios() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);
        const string conversationId = "dm:alpha:beta";

        using var vault = new Vault();
        vault.Open(vaultPath, key);

        for (var i = 1; i <= 120; i++) {
            vault.SaveMessage(new Message {
                Id = $"msg-{i:D3}",
                ConversationId = conversationId,
                SenderId = i % 2 == 0 ? "alpha" : "beta",
                Content = $"message {i}",
                Timestamp = i,
                SeqNum = i,
                IsMine = i % 2 == 0,
                Status = MessageStatus.Delivered
            });
        }

        var newestPage = vault.LoadMessages(conversationId, 0, 50);
        var olderPage = vault.LoadMessages(conversationId, 50, 50);
        var oldestPage = vault.LoadMessages(conversationId, 100, 50);

        Assert.Equal(120, vault.GetMessageCount(conversationId));
        Assert.Equal(50, newestPage.Count);
        Assert.Equal("msg-071", newestPage.First().Id);
        Assert.Equal("msg-120", newestPage.Last().Id);
        Assert.Equal("msg-021", olderPage.First().Id);
        Assert.Equal("msg-070", olderPage.Last().Id);
        Assert.Equal(20, oldestPage.Count);
        Assert.Equal("msg-001", oldestPage.First().Id);
        Assert.Equal("msg-020", oldestPage.Last().Id);
    }

    [Fact]
    public void OutboxRoundTripPersistsQueuedMessages() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);

        using var vault = new Vault();
        vault.Open(vaultPath, key);
        vault.EnqueueOutbox("out-1", "bob", "payload", "sig", 12);

        var queued = vault.LoadOutbox();

        Assert.Single(queued);
        Assert.Equal("out-1", queued[0].Id);
        Assert.Equal("bob", queued[0].RecipientId);
        Assert.Equal(12, queued[0].SeqNum);
    }

    static LocalUser CreateUser(string serverUrl, string displayName) {
        var (signPriv, signPub) = Crypto.GenerateSigningKeys();
        var (dhPriv, dhPub) = Crypto.GenerateDhKeys();

        return new LocalUser {
            UserId = Crypto.DeriveUserId(signPub),
            DisplayName = displayName,
            SignPrivKey = signPriv,
            SignPubKey = signPub,
            DhPrivKey = dhPriv,
            DhPubKey = dhPub,
            ServerUrl = serverUrl,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    static string CreateTempDirectory() {
        var path = Path.Combine(Path.GetTempPath(), "cipher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

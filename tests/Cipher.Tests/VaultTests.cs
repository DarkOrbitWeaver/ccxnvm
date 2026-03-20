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
        var user = CreateUser(AppBranding.DefaultRelayUrl, "alice");

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

    [Fact]
    public void MaintenanceBackupCreatesPortableVaultCopy() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);

        using var vault = new Vault();
        vault.Open(vaultPath, key);
        vault.SaveMessage(new Message {
            Id = "msg-1",
            ConversationId = "dm:a:b",
            SenderId = "a",
            Content = "hello",
            Timestamp = 1,
            SeqNum = 1
        });

        var backupPath = vault.CreateMaintenanceBackup("unit-test");

        Assert.True(File.Exists(backupPath));
        Assert.False(string.IsNullOrWhiteSpace(vault.LastMaintenanceBackupPath));
    }

    [Fact]
    public void GroupCanBePersistedWithOnlyCreatorMember() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);
        var ownerId = "owner-001";

        using var vault = new Vault();
        vault.Open(vaultPath, key);
        vault.SaveGroup(new GroupInfo {
            GroupId = "grp-solo",
            Name = "solo",
            MemberIds = new List<string> { ownerId },
            GroupKey = RandomNumberGenerator.GetBytes(32),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        var groups = vault.LoadGroups();
        Assert.Single(groups);
        Assert.Equal("grp-solo", groups[0].GroupId);
        Assert.Single(groups[0].MemberIds);
        Assert.Equal(ownerId, groups[0].MemberIds[0]);
    }

    [Fact]
    public void GroupOwnerPersistsAcrossReload() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);

        using (var vault = new Vault()) {
            vault.Open(vaultPath, key);
            vault.SaveGroup(new GroupInfo {
                GroupId = "grp-owned",
                Name = "owned",
                MemberIds = new List<string> { "owner-001", "friend-002" },
                GroupKey = RandomNumberGenerator.GetBytes(32),
                OwnerId = "owner-001",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        using var reopened = new Vault();
        reopened.Open(vaultPath, key);

        var groups = reopened.LoadGroups();
        Assert.Single(groups);
        Assert.Equal("owner-001", groups[0].OwnerId);
    }

    [Fact]
    public void MessageReceiptsPersistAndRemainMonotonic() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);

        using (var vault = new Vault()) {
            vault.Open(vaultPath, key);
            vault.UpsertMessageReceipt("msg-1", "bob", MessageStatus.Delivered, 10);
            vault.UpsertMessageReceipt("msg-1", "bob", MessageStatus.Seen, 20);
            vault.UpsertMessageReceipt("msg-1", "bob", MessageStatus.Delivered, 30); // Must not downgrade.
        }

        using var reopened = new Vault();
        reopened.Open(vaultPath, key);

        var receipts = reopened.LoadMessageReceipts(new[] { "msg-1" });
        Assert.Single(receipts);
        Assert.Equal("msg-1", receipts[0].MessageId);
        Assert.Equal("bob", receipts[0].UserId);
        Assert.Equal(MessageStatus.Seen, receipts[0].Status);
        Assert.Equal(20, receipts[0].UpdatedAt);
    }

    [Fact]
    public void LoadMessageReceiptsRespectsRequestedIds() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);

        using var vault = new Vault();
        vault.Open(vaultPath, key);

        vault.UpsertMessageReceipt("msg-a", "alice", MessageStatus.Delivered, 1);
        vault.UpsertMessageReceipt("msg-b", "bob", MessageStatus.Seen, 2);

        var onlyB = vault.LoadMessageReceipts(new[] { "msg-b" });

        Assert.Single(onlyB);
        Assert.Equal("msg-b", onlyB[0].MessageId);
        Assert.Equal("bob", onlyB[0].UserId);
        Assert.Equal(MessageStatus.Seen, onlyB[0].Status);
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

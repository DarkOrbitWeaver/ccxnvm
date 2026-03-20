using System.Security.Cryptography;
using System.Text;
using Cipher;
using Microsoft.Data.Sqlite;

namespace Cipher.Tests;

public class SecurityRegressionTests {
    [Fact]
    public void IdentityDisplayNameIsEncryptedAtRest() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);
        var user = CreateUser(AppBranding.DefaultRelayUrl, "alice");

        using (var vault = new Vault()) {
            vault.Open(vaultPath, key);
            vault.SaveIdentity(user);
        }

        using var raw = new SqliteConnection($"Data Source={vaultPath};Mode=ReadOnly;");
        raw.Open();

        using var columnCmd = raw.CreateCommand();
        columnCmd.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('identity')
            WHERE name = 'display_name'
            """;
        Assert.Equal(0L, Convert.ToInt64(columnCmd.ExecuteScalar()));

        using var valueCmd = raw.CreateCommand();
        valueCmd.CommandText = "SELECT display_name_enc FROM identity WHERE id=1";
        var displayNameEnc = Assert.IsType<byte[]>(valueCmd.ExecuteScalar());

        Assert.NotEmpty(displayNameEnc);
        Assert.NotEqual(user.DisplayName.Length, displayNameEnc.Length);
    }

    [Fact]
    public void OpeningLegacyVaultMigratesIdentityDisplayNameStorage() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);
        var user = CreateUser(AppBranding.DefaultRelayUrl, "alice");

        using (var raw = new SqliteConnection($"Data Source={vaultPath};")) {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE identity (
                    id INTEGER PRIMARY KEY,
                    user_id TEXT NOT NULL,
                    display_name TEXT NOT NULL,
                    sign_priv BLOB NOT NULL,
                    sign_pub TEXT NOT NULL,
                    dh_priv BLOB NOT NULL,
                    dh_pub TEXT NOT NULL,
                    server_url TEXT NOT NULL,
                    created_at INTEGER NOT NULL
                );

                INSERT INTO identity
                (id, user_id, display_name, sign_priv, sign_pub, dh_priv, dh_pub, server_url, created_at)
                VALUES
                (1, $uid, $name, $sp, $spub, $dp, $dpub, $srv, $ts);
                """;
            cmd.Parameters.AddWithValue("$uid", user.UserId);
            cmd.Parameters.AddWithValue("$name", user.DisplayName);
            cmd.Parameters.AddWithValue("$sp", Crypto.EncryptField(key, user.SignPrivKey));
            cmd.Parameters.AddWithValue("$spub", Convert.ToBase64String(user.SignPubKey));
            cmd.Parameters.AddWithValue("$dp", Crypto.EncryptField(key, user.DhPrivKey));
            cmd.Parameters.AddWithValue("$dpub", Convert.ToBase64String(user.DhPubKey));
            cmd.Parameters.AddWithValue("$srv", user.ServerUrl);
            cmd.Parameters.AddWithValue("$ts", user.CreatedAt);
            cmd.ExecuteNonQuery();
        }

        using (var vault = new Vault()) {
            vault.Open(vaultPath, key);
            var loaded = vault.LoadIdentity();

            Assert.NotNull(loaded);
            Assert.Equal(user.DisplayName, loaded!.DisplayName);
            Assert.Contains(vault.LastMaintenanceActions, action => action.Contains("migrated schema", StringComparison.OrdinalIgnoreCase));
        }

        using var migrated = new SqliteConnection($"Data Source={vaultPath};Mode=ReadOnly;");
        migrated.Open();

        using var oldColumnCmd = migrated.CreateCommand();
        oldColumnCmd.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('identity')
            WHERE name = 'display_name'
            """;
        Assert.Equal(0L, Convert.ToInt64(oldColumnCmd.ExecuteScalar()));

        using var newColumnCmd = migrated.CreateCommand();
        newColumnCmd.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('identity')
            WHERE name = 'display_name_enc'
            """;
        Assert.Equal(1L, Convert.ToInt64(newColumnCmd.ExecuteScalar()));
    }

    [Fact]
    public void GroupMembershipMetadataIsEncryptedAtRest() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);

        using (var vault = new Vault()) {
            vault.Open(vaultPath, key);
            vault.SaveGroup(new GroupInfo {
                GroupId = "grp-1",
                Name = "Secret Group",
                MemberIds = ["alice", "bob"],
                GroupKey = RandomNumberGenerator.GetBytes(32),
                OwnerId = "alice",
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
        }

        using var raw = new SqliteConnection($"Data Source={vaultPath};Mode=ReadOnly;");
        raw.Open();

        using var columnCmd = raw.CreateCommand();
        columnCmd.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('groups')
            WHERE name IN ('member_ids_enc', 'owner_id_enc')
            """;
        Assert.Equal(2L, Convert.ToInt64(columnCmd.ExecuteScalar()));

        using var valueCmd = raw.CreateCommand();
        valueCmd.CommandText = """
            SELECT member_ids, member_ids_enc, owner_id, owner_id_enc
            FROM groups
            WHERE group_id = 'grp-1'
            """;
        using var reader = valueCmd.ExecuteReader();
        Assert.True(reader.Read());

        Assert.Equal("", reader.GetString(0));
        Assert.Equal("", reader.GetString(2));

        var memberIdsEnc = Assert.IsType<byte[]>(reader["member_ids_enc"]);
        var ownerIdEnc = Assert.IsType<byte[]>(reader["owner_id_enc"]);
        Assert.False(memberIdsEnc.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("alice,bob")));
        Assert.False(ownerIdEnc.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("alice")));
    }

    [Fact]
    public async Task NextSeqNumReturnsUniqueValuesUnderConcurrency() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);
        const string convId = "dm:alice:bob";
        const int workers = 32;

        using var vault = new Vault();
        vault.Open(vaultPath, key);

        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, workers)
            .Select(_ => Task.Run(() => {
                start.Wait();
                return vault.NextSeqNum(convId);
            }))
            .ToArray();

        start.Set();
        var values = await Task.WhenAll(tasks);

        Assert.Equal(workers, values.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, workers).Select(i => (long)i), values.Order());
    }

    [Fact]
    public void NukeDeletesSqliteSidecarFiles() {
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

        Assert.True(SpinWait.SpinUntil(() => File.Exists(vaultPath + "-wal"), TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => File.Exists(vaultPath + "-shm"), TimeSpan.FromSeconds(2)));

        vault.Nuke();

        Assert.False(File.Exists(vaultPath));
        Assert.False(File.Exists(vaultPath + "-wal"));
        Assert.False(File.Exists(vaultPath + "-shm"));
    }

    [Fact]
    public void ParseVerifiedKeyBundleRejectsMismatchedDerivedUserId() {
        var (_, signPub) = Crypto.GenerateSigningKeys();
        var (_, dhPub) = Crypto.GenerateDhKeys();

        var parsed = NetworkClient.ParseVerifiedKeyBundle(
            "some-other-user",
            "some-other-user",
            Convert.ToBase64String(signPub),
            Convert.ToBase64String(dhPub));

        Assert.Null(parsed);
    }

    [Fact]
    public void ParseVerifiedKeyBundleRejectsRelayUserIdMismatch() {
        var (_, signPub) = Crypto.GenerateSigningKeys();
        var (_, dhPub) = Crypto.GenerateDhKeys();
        var userId = Crypto.DeriveUserId(signPub);

        var parsed = NetworkClient.ParseVerifiedKeyBundle(
            userId,
            "relay-claimed-user",
            Convert.ToBase64String(signPub),
            Convert.ToBase64String(dhPub));

        Assert.Null(parsed);
    }

    [Fact]
    public void ParseVerifiedKeyBundleRejectsMalformedDhKey() {
        var (_, signPub) = Crypto.GenerateSigningKeys();
        var userId = Crypto.DeriveUserId(signPub);

        var parsed = NetworkClient.ParseVerifiedKeyBundle(
            userId,
            userId,
            Convert.ToBase64String(signPub),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

        Assert.Null(parsed);
    }

    [Fact]
    public void ParseVerifiedKeyBundleRejectsMalformedSigningKey() {
        var (_, dhPub) = Crypto.GenerateDhKeys();

        var parsed = NetworkClient.ParseVerifiedKeyBundle(
            "some-user-id",
            "some-user-id",
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
            Convert.ToBase64String(dhPub));

        Assert.Null(parsed);
    }

    [Fact]
    public void ParseVerifiedKeyBundleAcceptsMatchingIdentity() {
        var (_, signPub) = Crypto.GenerateSigningKeys();
        var (_, dhPub) = Crypto.GenerateDhKeys();
        var userId = Crypto.DeriveUserId(signPub);

        var parsed = NetworkClient.ParseVerifiedKeyBundle(
            userId,
            userId,
            Convert.ToBase64String(signPub),
            Convert.ToBase64String(dhPub));

        Assert.NotNull(parsed);
        Assert.Equal(Convert.ToBase64String(signPub), Convert.ToBase64String(parsed?.signPub ?? []));
        Assert.Equal(Convert.ToBase64String(dhPub), Convert.ToBase64String(parsed?.dhPub ?? []));
    }

    [Fact]
    public void OpeningLegacyVaultMigratesOutboxStorageToEncryptedColumns() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);

        using (var raw = new SqliteConnection($"Data Source={vaultPath};")) {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                INSERT INTO settings(key, value) VALUES ('schema_version', '3');

                CREATE TABLE outbox (
                    id TEXT PRIMARY KEY,
                    recipient_id TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    sig TEXT NOT NULL,
                    seq_num INTEGER NOT NULL,
                    conv_type INTEGER NOT NULL DEFAULT 0,
                    group_id TEXT,
                    member_ids TEXT,
                    created_at INTEGER NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0
                );

                INSERT INTO outbox
                (id, recipient_id, payload, sig, seq_num, conv_type, group_id, member_ids, created_at, attempts)
                VALUES
                ('legacy-out-1', 'group-1', 'payload-json', 'signature-b64', 42, 1, 'group-1', 'alice,bob', 1234, 2);
                """;
            cmd.ExecuteNonQuery();
        }

        using (var vault = new Vault()) {
            vault.Open(vaultPath, key);
            var queued = vault.LoadOutbox();

            Assert.Single(queued);
            Assert.Equal("legacy-out-1", queued[0].Id);
            Assert.Equal("group-1", queued[0].RecipientId);
            Assert.Equal("payload-json", queued[0].Payload);
            Assert.Equal("signature-b64", queued[0].Sig);
            Assert.Equal(ConversationType.Group, queued[0].ConvType);
            Assert.Equal("group-1", queued[0].GroupId);
            Assert.Equal(["alice", "bob"], queued[0].MemberIds);
            Assert.Contains(vault.LastMaintenanceActions, action => action.Contains("migrated schema", StringComparison.OrdinalIgnoreCase));
        }

        using var migrated = new SqliteConnection($"Data Source={vaultPath};Mode=ReadOnly;");
        migrated.Open();

        using var oldColumnCmd = migrated.CreateCommand();
        oldColumnCmd.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('outbox')
            WHERE name = 'payload'
            """;
        Assert.Equal(0L, Convert.ToInt64(oldColumnCmd.ExecuteScalar()));

        using var newColumnCmd = migrated.CreateCommand();
        newColumnCmd.CommandText = """
            SELECT COUNT(*)
            FROM pragma_table_info('outbox')
            WHERE name = 'payload_enc'
            """;
        Assert.Equal(1L, Convert.ToInt64(newColumnCmd.ExecuteScalar()));

        using var valueCmd = migrated.CreateCommand();
        valueCmd.CommandText = """
            SELECT recipient_id_enc, payload_enc, sig_enc, member_ids_enc
            FROM outbox
            WHERE id = 'legacy-out-1'
            """;
        using var reader = valueCmd.ExecuteReader();
        Assert.True(reader.Read());

        var recipientEnc = Assert.IsType<byte[]>(reader["recipient_id_enc"]);
        var payloadEnc = Assert.IsType<byte[]>(reader["payload_enc"]);
        var sigEnc = Assert.IsType<byte[]>(reader["sig_enc"]);
        var memberIdsEnc = Assert.IsType<byte[]>(reader["member_ids_enc"]);

        Assert.False(recipientEnc.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("group-1")));
        Assert.False(payloadEnc.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("payload-json")));
        Assert.False(sigEnc.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("signature-b64")));
        Assert.False(memberIdsEnc.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes("alice,bob")));
    }

    [Fact]
    public void OpeningLegacyVaultMigratesGroupMetadataToEncryptedColumns() {
        var tempDir = CreateTempDirectory();
        var vaultPath = Path.Combine(tempDir, "vault.db");
        var key = RandomNumberGenerator.GetBytes(32);

        using (var raw = new SqliteConnection($"Data Source={vaultPath};")) {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                INSERT INTO settings(key, value) VALUES ('schema_version', '4');

                CREATE TABLE groups (
                    group_id TEXT PRIMARY KEY,
                    name_enc BLOB NOT NULL,
                    member_ids TEXT NOT NULL,
                    group_key_enc BLOB NOT NULL,
                    owner_id TEXT NOT NULL DEFAULT '',
                    created_at INTEGER NOT NULL
                );

                INSERT INTO groups
                (group_id, name_enc, member_ids, group_key_enc, owner_id, created_at)
                VALUES
                ('legacy-group', $name, 'alice,bob', $groupKey, 'alice', 1234);
                """;
            cmd.Parameters.AddWithValue("$name", Crypto.EncryptStr(key, "Legacy Group"));
            cmd.Parameters.AddWithValue("$groupKey", Crypto.EncryptField(key, RandomNumberGenerator.GetBytes(32)));
            cmd.ExecuteNonQuery();
        }

        using (var vault = new Vault()) {
            vault.Open(vaultPath, key);
            var groups = vault.LoadGroups();

            Assert.Single(groups);
            Assert.Equal(["alice", "bob"], groups[0].MemberIds);
            Assert.Equal("alice", groups[0].OwnerId);
            Assert.Contains(vault.LastMaintenanceActions, action => action.Contains("migrated schema", StringComparison.OrdinalIgnoreCase));
        }

        using var migrated = new SqliteConnection($"Data Source={vaultPath};Mode=ReadOnly;");
        migrated.Open();

        using var valueCmd = migrated.CreateCommand();
        valueCmd.CommandText = """
            SELECT member_ids, member_ids_enc, owner_id, owner_id_enc
            FROM groups
            WHERE group_id = 'legacy-group'
            """;
        using var reader = valueCmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("", reader.GetString(0));
        Assert.Equal("", reader.GetString(2));
        Assert.IsType<byte[]>(reader["member_ids_enc"]);
        Assert.IsType<byte[]>(reader["owner_id_enc"]);
    }

    [Theory]
    [InlineData("owner-1", "owner-1", "owner-1", true)]
    [InlineData("owner-1", "", "owner-1", true)]
    [InlineData("member-2", "owner-1", "owner-1", false)]
    [InlineData("owner-1", "owner-1", "member-2", false)]
    [InlineData("owner-1", "owner-1", "", false)]
    public void GroupInviteOwnerTrustRequiresSenderAndOwnerToMatch(
        string senderUserId,
        string existingOwnerId,
        string inviteOwnerId,
        bool expected) {
        var trusted = MainWindow.IsTrustedGroupInviteOwner(senderUserId, existingOwnerId, inviteOwnerId);
        Assert.Equal(expected, trusted);
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

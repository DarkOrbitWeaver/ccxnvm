using System.IO;
using Microsoft.Data.Sqlite;

namespace Cipher;

public sealed class VaultRecoveryException : Exception {
    public VaultRecoveryException(string message, string? backupPath = null, Exception? inner = null)
        : base(message, inner) {
        BackupPath = backupPath;
    }

    public string? BackupPath { get; }
}

public partial class Vault {
    const int CurrentSchemaVersion = 9;
    readonly List<string> _maintenanceActions = [];

    public string? LastMaintenanceBackupPath { get; private set; }
    public IReadOnlyList<string> LastMaintenanceActions => _maintenanceActions;

    void ConfigureConnection() {
        Exec("PRAGMA foreign_keys=ON;");
        TryExec("PRAGMA journal_mode=WAL;");
        TryExec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA busy_timeout=5000;");
    }

    void RunStartupMaintenance() {
        _maintenanceActions.Clear();
        EnsureSchemaVersion();
        RunIntegrityChecksAndRepair();
    }

    void EnsureSchemaVersion() {
        var versionText = GetSetting("schema_version");
        if (!int.TryParse(versionText, out var version)) {
            if (!HasExistingVaultData()) {
                SetSetting("schema_version", CurrentSchemaVersion.ToString());
                return;
            }

            version = 1;
        }

        if (version >= CurrentSchemaVersion) return;

        var backupPath = CreateMaintenanceBackup($"pre-schema-v{version}");
        try {
            if (version < 2) {
                TryExec("ALTER TABLE contacts ADD COLUMN is_verified INTEGER NOT NULL DEFAULT 0");
                TryExec("ALTER TABLE contacts ADD COLUMN pending_sign_pub TEXT");
                TryExec("ALTER TABLE contacts ADD COLUMN pending_dh_pub TEXT");
                TryExec("ALTER TABLE contacts ADD COLUMN key_changed_at INTEGER NOT NULL DEFAULT 0");
            }
            if (version < 3) {
                MigrateIdentityDisplayNameStorage();
            }
            if (version < 4) {
                MigrateOutboxStorage();
            }
            if (version < 5) {
                TryExec("ALTER TABLE groups ADD COLUMN owner_id TEXT NOT NULL DEFAULT ''");
                MigrateGroupMetadataStorage();
            }
            if (version < 6) {
                MigrateMetadataEncryptionStorage();
            }
            if (version < 7) {
                TryExec("ALTER TABLE incoming_seq_state ADD COLUMN chain_key_enc BLOB");
            }
            if (version < 8) {
                TryExec("ALTER TABLE contacts ADD COLUMN is_archived INTEGER NOT NULL DEFAULT 0");
                TryExec("ALTER TABLE contacts ADD COLUMN archived_at INTEGER NOT NULL DEFAULT 0");
                TryExec("""
                    CREATE TABLE IF NOT EXISTS skipped_dm_keys (
                        conversation_id TEXT NOT NULL,
                        sender_id TEXT NOT NULL,
                        seq_num INTEGER NOT NULL,
                        message_key_enc BLOB NOT NULL,
                        created_at INTEGER NOT NULL,
                        PRIMARY KEY (conversation_id, sender_id, seq_num)
                    )
                    """);
            }
            if (version < 9) {
                TryExec("""
                    CREATE TABLE IF NOT EXISTS dm_sessions (
                        conversation_id TEXT PRIMARY KEY,
                        remote_user_id TEXT NOT NULL,
                        session_id TEXT NOT NULL,
                        root_key_enc BLOB NOT NULL,
                        last_send_seq INTEGER NOT NULL DEFAULT 0,
                        last_recv_seq INTEGER NOT NULL DEFAULT 0
                    )
                    """);
            }
            SetSetting("schema_version", CurrentSchemaVersion.ToString());
            _maintenanceActions.Add($"migrated schema v{version} -> v{CurrentSchemaVersion}");
        } catch (Exception ex) {
            RestoreBackup(backupPath);
            throw new VaultRecoveryException(
                "vault migration failed; the previous backup was restored",
                backupPath,
                ex);
        }
    }

    void MigrateIdentityDisplayNameStorage() {
        if (!HasColumn("identity", "display_name")) return;

        var rows = new List<(long Id, string UserId, string DisplayName, byte[] SignPriv, string SignPub, byte[] DhPriv, string DhPub, string ServerUrl, long CreatedAt)>();
        using (var select = _db!.CreateCommand()) {
            select.CommandText = @"
                SELECT id, user_id, display_name, sign_priv, sign_pub, dh_priv, dh_pub, server_url, created_at
                FROM identity";
            using var reader = select.ExecuteReader();
            while (reader.Read()) {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    (byte[])reader["sign_priv"],
                    reader.GetString(4),
                    (byte[])reader["dh_priv"],
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetInt64(8)));
            }
        }

        using var tx = _db!.BeginTransaction(deferred: false);
        ExecTx(tx, "ALTER TABLE identity RENAME TO identity_legacy");
        ExecTx(tx, @"
            CREATE TABLE identity (
                id INTEGER PRIMARY KEY,
                user_id TEXT NOT NULL,
                display_name_enc BLOB NOT NULL,
                sign_priv BLOB NOT NULL,
                sign_pub TEXT NOT NULL,
                dh_priv BLOB NOT NULL,
                dh_pub TEXT NOT NULL,
                server_url TEXT NOT NULL,
                created_at INTEGER NOT NULL
            )");

        foreach (var row in rows) {
            ExecParamTx(tx, @"
                INSERT INTO identity
                (id, user_id, display_name_enc, sign_priv, sign_pub, dh_priv, dh_pub, server_url, created_at)
                VALUES (@id, @uid, @name, @sp, @spub, @dp, @dpub, @srv, @ts)",
                ("id", row.Id),
                ("uid", row.UserId),
                ("name", Crypto.EncryptStr(_key, row.DisplayName)),
                ("sp", row.SignPriv),
                ("spub", row.SignPub),
                ("dp", row.DhPriv),
                ("dpub", row.DhPub),
                ("srv", row.ServerUrl),
                ("ts", row.CreatedAt));
        }

        ExecTx(tx, "DROP TABLE identity_legacy");
        tx.Commit();
    }

    void MigrateOutboxStorage() {
        if (!HasColumn("outbox", "payload")) return;

        var rows = new List<(string Id, string RecipientId, string Payload, string Sig, long SeqNum, int ConvType, string? GroupId, string? MemberIds, long CreatedAt, int Attempts)>();
        using (var select = _db!.CreateCommand()) {
            select.CommandText = """
                SELECT id, recipient_id, payload, sig, seq_num, conv_type, group_id, member_ids, created_at, attempts
                FROM outbox
                """;
            using var reader = select.ExecuteReader();
            while (reader.Read()) {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.GetInt64(8),
                    reader.GetInt32(9)));
            }
        }

        using var tx = _db!.BeginTransaction(deferred: false);
        ExecTx(tx, "ALTER TABLE outbox RENAME TO outbox_legacy");
        ExecTx(tx, """
            CREATE TABLE outbox (
                id TEXT PRIMARY KEY,
                recipient_id_enc BLOB NOT NULL,
                payload_enc BLOB NOT NULL,
                sig_enc BLOB NOT NULL,
                seq_num INTEGER NOT NULL,
                conv_type INTEGER NOT NULL DEFAULT 0,
                group_id TEXT,
                group_id_enc BLOB,
                group_id_hmac TEXT,
                member_ids_enc BLOB,
                created_at INTEGER NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0
            )
            """);

        var skipped = 0;
        foreach (var row in rows) {
            if (string.IsNullOrWhiteSpace(row.RecipientId) ||
                string.IsNullOrEmpty(row.Payload) ||
                string.IsNullOrEmpty(row.Sig) ||
                row.SeqNum < 1) {
                skipped++;
                continue;
            }

            ExecParamTx(tx, """
                INSERT INTO outbox
                (id, recipient_id_enc, payload_enc, sig_enc, seq_num, conv_type, group_id, group_id_enc, group_id_hmac, member_ids_enc, created_at, attempts)
                VALUES (@id, @rid, @payload, @sig, @seq, @ctype, '', @gid_enc, @gid_hmac, @mids, @created, @attempts)
                """,
                ("id", row.Id),
                ("rid", Crypto.EncryptStr(_key, row.RecipientId)),
                ("payload", Crypto.EncryptStr(_key, row.Payload)),
                ("sig", Crypto.EncryptStr(_key, row.Sig)),
                ("seq", row.SeqNum),
                ("ctype", row.ConvType),
                ("gid_enc", row.GroupId != null ? Crypto.EncryptStr(_key, row.GroupId) : DBNull.Value),
                ("gid_hmac", row.GroupId != null ? ComputeMetadataIndex("outbox-group", row.GroupId) : DBNull.Value),
                ("mids", row.MemberIds != null ? Crypto.EncryptStr(_key, row.MemberIds) : DBNull.Value),
                ("created", row.CreatedAt),
                ("attempts", row.Attempts));
        }

        ExecTx(tx, "DROP TABLE outbox_legacy");
        tx.Commit();

        if (skipped > 0) {
            _maintenanceActions.Add($"removed {skipped} invalid legacy outbox item(s)");
        }
    }

    void MigrateGroupMetadataStorage() {
        TryExec("ALTER TABLE groups ADD COLUMN member_ids_enc BLOB");
        TryExec("ALTER TABLE groups ADD COLUMN owner_id_enc BLOB");

        var rows = new List<(string GroupId, string MemberIds, string OwnerId)>();
        using (var select = _db!.CreateCommand()) {
            select.CommandText = """
                SELECT group_id, member_ids, owner_id
                FROM groups
                WHERE COALESCE(member_ids, '') <> ''
                   OR COALESCE(owner_id, '') <> ''
                """;
            using var reader = select.ExecuteReader();
            while (reader.Read()) {
                rows.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2)));
            }
        }

        if (rows.Count == 0) {
            return;
        }

        using var tx = _db!.BeginTransaction(deferred: false);
        foreach (var row in rows) {
            ExecParamTx(tx, """
                UPDATE groups
                SET member_ids = '',
                    member_ids_enc = @memberIdsEnc,
                    owner_id = '',
                    owner_id_enc = @ownerIdEnc
                WHERE group_id = @groupId
                """,
                ("groupId", row.GroupId),
                ("memberIdsEnc", string.IsNullOrWhiteSpace(row.MemberIds) ? DBNull.Value : Crypto.EncryptStr(_key, row.MemberIds)),
                ("ownerIdEnc", string.IsNullOrWhiteSpace(row.OwnerId) ? DBNull.Value : Crypto.EncryptStr(_key, row.OwnerId)));
        }
        tx.Commit();
    }

    void MigrateMetadataEncryptionStorage() {
        TryExec("ALTER TABLE contacts ADD COLUMN sign_pub_enc BLOB");
        TryExec("ALTER TABLE contacts ADD COLUMN dh_pub_enc BLOB");
        TryExec("ALTER TABLE contacts ADD COLUMN conversation_id_enc BLOB");
        TryExec("ALTER TABLE contacts ADD COLUMN conversation_id_hmac TEXT");
        TryExec("ALTER TABLE messages ADD COLUMN conversation_id_enc BLOB");
        TryExec("ALTER TABLE messages ADD COLUMN conversation_id_hmac TEXT");
        TryExec("ALTER TABLE messages ADD COLUMN sender_id_enc BLOB");
        TryExec("ALTER TABLE messages ADD COLUMN timestamp_enc BLOB");
        TryExec("ALTER TABLE outbox ADD COLUMN group_id_enc BLOB");
        TryExec("ALTER TABLE outbox ADD COLUMN group_id_hmac TEXT");
        TryExec("DROP INDEX IF EXISTS idx_msg_conv");
        TryExec("CREATE INDEX IF NOT EXISTS idx_msg_conv ON messages(conversation_id_hmac, id)");

        using var tx = _db!.BeginTransaction(deferred: false);

        using (var select = _db.CreateCommand()) {
            select.Transaction = tx;
            select.CommandText = """
                SELECT user_id, sign_pub, dh_pub, conversation_id
                FROM contacts
                """;
            using var reader = select.ExecuteReader();
            var rows = new List<(string UserId, string SignPub, string DhPub, string ConversationId)>();
            while (reader.Read()) {
                rows.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3)));
            }

            foreach (var row in rows) {
                ExecParamTx(tx, """
                    UPDATE contacts
                    SET sign_pub = '',
                        dh_pub = '',
                        conversation_id = '',
                        sign_pub_enc = @sp_enc,
                        dh_pub_enc = @dp_enc,
                        conversation_id_enc = @conv_enc,
                        conversation_id_hmac = @conv_hmac
                    WHERE user_id = @uid
                    """,
                    ("uid", row.UserId),
                    ("sp_enc", Crypto.EncryptStr(_key, row.SignPub)),
                    ("dp_enc", Crypto.EncryptStr(_key, row.DhPub)),
                    ("conv_enc", Crypto.EncryptStr(_key, row.ConversationId)),
                    ("conv_hmac", ComputeMetadataIndex("contact-conversation", row.ConversationId)));
            }
        }

        using (var select = _db.CreateCommand()) {
            select.Transaction = tx;
            select.CommandText = """
                SELECT id, conversation_id, sender_id, timestamp
                FROM messages
                """;
            using var reader = select.ExecuteReader();
            var rows = new List<(string Id, string ConversationId, string SenderId, long Timestamp)>();
            while (reader.Read()) {
                rows.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt64(3)));
            }

            foreach (var row in rows) {
                ExecParamTx(tx, """
                    UPDATE messages
                    SET conversation_id = '',
                        sender_id = '',
                        timestamp = 0,
                        conversation_id_enc = @conv_enc,
                        conversation_id_hmac = @conv_hmac,
                        sender_id_enc = @sid_enc,
                        timestamp_enc = @ts_enc
                    WHERE id = @id
                    """,
                    ("id", row.Id),
                    ("conv_enc", Crypto.EncryptStr(_key, row.ConversationId)),
                    ("conv_hmac", ComputeMetadataIndex("message-conversation", row.ConversationId)),
                    ("sid_enc", Crypto.EncryptStr(_key, row.SenderId)),
                    ("ts_enc", Crypto.EncryptField(_key, BitConverter.GetBytes(row.Timestamp))));
            }
        }

        using (var select = _db.CreateCommand()) {
            select.Transaction = tx;
            select.CommandText = """
                SELECT id, group_id
                FROM outbox
                WHERE group_id IS NOT NULL AND group_id <> ''
                """;
            using var reader = select.ExecuteReader();
            var rows = new List<(string Id, string GroupId)>();
            while (reader.Read()) {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }

            foreach (var row in rows) {
                ExecParamTx(tx, """
                    UPDATE outbox
                    SET group_id = '',
                        group_id_enc = @gid_enc,
                        group_id_hmac = @gid_hmac
                    WHERE id = @id
                    """,
                    ("id", row.Id),
                    ("gid_enc", Crypto.EncryptStr(_key, row.GroupId)),
                    ("gid_hmac", ComputeMetadataIndex("outbox-group", row.GroupId)));
            }
        }

        tx.Commit();
    }

    bool HasColumn(string tableName, string columnName) {
        if (!IsSafeSqlIdentifier(tableName)) {
            throw new ArgumentException($"Invalid table name: {tableName}", nameof(tableName));
        }

        using var cmd = _db!.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) {
            if (string.Equals(reader.GetString(reader.GetOrdinal("name")), columnName, StringComparison.Ordinal)) {
                return true;
            }
        }
        return false;
    }

    static bool IsSafeSqlIdentifier(string identifier) {
        if (string.IsNullOrWhiteSpace(identifier)) return false;
        var first = identifier[0];
        var firstIsAsciiLetter = (first >= 'A' && first <= 'Z') || (first >= 'a' && first <= 'z');
        if (!firstIsAsciiLetter && first != '_') return false;

        for (var i = 1; i < identifier.Length; i++) {
            var ch = identifier[i];
            var isAsciiLetter = (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');
            var isDigit = ch >= '0' && ch <= '9';
            if (!isAsciiLetter && !isDigit && ch != '_') {
                return false;
            }
        }

        return true;
    }

    void RunIntegrityChecksAndRepair() {
        var integrityResult = ExecuteScalarString("PRAGMA integrity_check(1);");
        if (!string.Equals(integrityResult, "ok", StringComparison.OrdinalIgnoreCase)) {
            var backupPath = CreateMaintenanceBackup("integrity-check-failed");
            throw new VaultRecoveryException(
                $"vault integrity check failed: {integrityResult}",
                backupPath);
        }

        var repairActions = CollectRepairActions();
        if (repairActions.Count == 0) return;

        var maintenanceBackup = CreateMaintenanceBackup("pre-repair");
        try {
            using var tx = _db!.BeginTransaction(deferred: false);
            foreach (var action in repairActions) {
                action(tx);
            }
            tx.Commit();
            _maintenanceActions.Add($"applied {repairActions.Count} startup repair(s)");
        } catch (Exception ex) {
            try {
                RestoreBackup(maintenanceBackup);
            } catch {
            }
            throw new VaultRecoveryException(
                "vault repair failed; the previous backup was restored",
                maintenanceBackup,
                ex);
        }
    }

    public string CreateMaintenanceBackup(string reason) {
        lock (_gate) {
            AppPaths.EnsureCreated();

            var safeReason = SanitizeFileSegment(reason);
            var backupPath = Path.Combine(
                AppPaths.BackupsDir,
                $"vault-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}-{safeReason}-{Guid.NewGuid():N}.db");

            using (var cmd = _db!.CreateCommand()) {
                cmd.CommandText = $"VACUUM INTO '{backupPath.Replace("'", "''")}';";
                cmd.ExecuteNonQuery();
            }

            var saltBackup = backupPath + ".salt";
            if (File.Exists(SaltPath)) {
                File.Copy(SaltPath, saltBackup, overwrite: true);
            }

            LastMaintenanceBackupPath = backupPath;
            PruneMaintenanceBackups();
            AppLog.Info("vault", $"created maintenance backup: {backupPath}");
            return backupPath;
        }
    }

    void RestoreBackup(string backupPath) {
        _db?.Close();
        _db?.Dispose();
        _db = null;
        IsOpen = false;

        DeleteIfExists(_path + "-wal");
        DeleteIfExists(_path + "-shm");
        File.Copy(backupPath, _path, overwrite: true);

        var saltBackup = backupPath + ".salt";
        if (File.Exists(saltBackup)) {
            Directory.CreateDirectory(Path.GetDirectoryName(SaltPath)!);
            File.Copy(saltBackup, SaltPath, overwrite: true);
        }
    }

    List<Action<SqliteTransaction>> CollectRepairActions() {
        var actions = new List<Action<SqliteTransaction>>();

        var brokenOutbox = ExecuteScalarInt(@"
            SELECT COUNT(*) FROM outbox
            WHERE recipient_id_enc IS NULL OR length(recipient_id_enc) = 0
               OR payload_enc IS NULL OR length(payload_enc) = 0
               OR sig_enc IS NULL OR length(sig_enc) = 0
               OR seq_num < 1");
        if (brokenOutbox > 0) {
            actions.Add(tx => {
                ExecTx(tx, @"
                    DELETE FROM outbox
                    WHERE recipient_id_enc IS NULL OR length(recipient_id_enc) = 0
                       OR payload_enc IS NULL OR length(payload_enc) = 0
                       OR sig_enc IS NULL OR length(sig_enc) = 0
                       OR seq_num < 1");
                _maintenanceActions.Add($"removed {brokenOutbox} invalid outbox item(s)");
            });
        }

        var user = LoadIdentity();
        if (user != null) {
            var missingContactConversations = ExecuteScalarInt(@"
                SELECT COUNT(*) FROM contacts
                WHERE conversation_id_hmac IS NULL OR conversation_id_hmac = ''");
            if (missingContactConversations > 0) {
                actions.Add(tx => {
                    using var select = _db!.CreateCommand();
                    select.Transaction = tx;
                    select.CommandText = @"
                        SELECT user_id
                        FROM contacts
                        WHERE conversation_id_hmac IS NULL OR conversation_id_hmac = ''";
                    using var reader = select.ExecuteReader();
                    var userIds = new List<string>();
                    while (reader.Read()) {
                        userIds.Add(reader.GetString(0));
                    }

                    foreach (var userId in userIds) {
                        var ids = new[] { user.UserId, userId }
                            .OrderBy(x => x, StringComparer.Ordinal)
                            .ToArray();
                        var convId = $"dm:{ids[0]}:{ids[1]}";
                        ExecParamTx(tx,
                            "UPDATE contacts SET conversation_id='', conversation_id_enc=@conv_enc, conversation_id_hmac=@conv_hmac WHERE user_id=@uid",
                            ("conv_enc", Crypto.EncryptStr(_key, convId)),
                            ("conv_hmac", ComputeMetadataIndex("contact-conversation", convId)),
                            ("uid", userId));
                    }

                    _maintenanceActions.Add(
                        $"repaired {missingContactConversations} contact conversation id(s)");
                });
            }
        }

        var missingConvStateRows = ExecuteScalarInt(@"
            SELECT COUNT(*) FROM (
                SELECT DISTINCT m.conversation_id
                FROM messages m
                WHERE m.conversation_id IS NOT NULL AND m.conversation_id <> ''
                  AND NOT EXISTS (
                      SELECT 1
                      FROM conv_state c
                      WHERE c.conversation_id = m.conversation_id
                  )
            )");
        if (missingConvStateRows > 0) {
            actions.Add(tx => {
                var inserted = ExecuteNonQueryTx(tx, @"
                INSERT OR IGNORE INTO conv_state(conversation_id, last_seq, secret_enc)
                SELECT conversation_id, MAX(seq_num), NULL
                FROM messages
                WHERE conversation_id IS NOT NULL AND conversation_id <> ''
                GROUP BY conversation_id;");
                if (inserted > 0) {
                    _maintenanceActions.Add($"repaired {inserted} missing conversation state row(s)");
                }
            });
        }

        return actions;
    }

    void PruneMaintenanceBackups() {
        var backups = Directory.GetFiles(AppPaths.BackupsDir, "vault-*.db")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(5)
            .ToList();

        foreach (var file in backups) {
            DeleteIfExists(file.FullName);
            DeleteIfExists(file.FullName + ".salt");
        }
    }

    string ExecuteScalarString(string sql) {
        lock (_gate) {
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToString(cmd.ExecuteScalar()) ?? "";
        }
    }

    int ExecuteScalarInt(string sql) {
        lock (_gate) {
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    int ExecuteNonQueryTx(SqliteTransaction tx, string sql) {
        using var cmd = _db!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    void ExecTx(SqliteTransaction tx, string sql) {
        using var cmd = _db!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    void ExecParamTx(SqliteTransaction tx, string sql, params (string name, object? val)[] parms) {
        using var cmd = _db!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, val) in parms)
            cmd.Parameters.AddWithValue("@" + name.TrimStart('@'), val ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    static string SanitizeFileSegment(string input) {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(input
            .Select(ch => invalid.Contains(ch) ? '-' : ch)
            .ToArray())
            .Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "backup" : cleaned;
    }

    static void DeleteIfExists(string path) {
        if (!File.Exists(path)) return;
        try {
            File.Delete(path);
        } catch {
        }
    }

    bool HasExistingVaultData() =>
        ExecuteScalarInt("SELECT COUNT(*) FROM identity") > 0 ||
        ExecuteScalarInt("SELECT COUNT(*) FROM contacts") > 0 ||
        ExecuteScalarInt("SELECT COUNT(*) FROM groups") > 0 ||
        ExecuteScalarInt("SELECT COUNT(*) FROM messages") > 0 ||
        ExecuteScalarInt("SELECT COUNT(*) FROM outbox") > 0;
}

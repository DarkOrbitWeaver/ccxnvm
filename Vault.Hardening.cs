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
    const int CurrentSchemaVersion = 2;
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
            TryExec("ALTER TABLE contacts ADD COLUMN is_verified INTEGER NOT NULL DEFAULT 0");
            TryExec("ALTER TABLE contacts ADD COLUMN pending_sign_pub TEXT");
            TryExec("ALTER TABLE contacts ADD COLUMN pending_dh_pub TEXT");
            TryExec("ALTER TABLE contacts ADD COLUMN key_changed_at INTEGER NOT NULL DEFAULT 0");
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
            using var tx = _db!.BeginTransaction();
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
            WHERE recipient_id IS NULL OR recipient_id = ''
               OR payload IS NULL OR payload = ''
               OR sig IS NULL OR sig = ''
               OR seq_num < 1");
        if (brokenOutbox > 0) {
            actions.Add(tx => {
                ExecTx(tx, @"
                    DELETE FROM outbox
                    WHERE recipient_id IS NULL OR recipient_id = ''
                       OR payload IS NULL OR payload = ''
                       OR sig IS NULL OR sig = ''
                       OR seq_num < 1");
                _maintenanceActions.Add($"removed {brokenOutbox} invalid outbox item(s)");
            });
        }

        var user = LoadIdentity();
        if (user != null) {
            var missingContactConversations = ExecuteScalarInt(@"
                SELECT COUNT(*) FROM contacts
                WHERE conversation_id IS NULL OR conversation_id = ''");
            if (missingContactConversations > 0) {
                actions.Add(tx => {
                    using var select = _db!.CreateCommand();
                    select.Transaction = tx;
                    select.CommandText = @"
                        SELECT user_id
                        FROM contacts
                        WHERE conversation_id IS NULL OR conversation_id = ''";
                    using var reader = select.ExecuteReader();
                    var userIds = new List<string>();
                    while (reader.Read()) {
                        userIds.Add(reader.GetString(0));
                    }

                    foreach (var userId in userIds) {
                        var ids = new[] { user.UserId, userId }
                            .OrderBy(x => x, StringComparer.Ordinal)
                            .ToArray();
                        ExecParamTx(tx,
                            "UPDATE contacts SET conversation_id=@conv WHERE user_id=@uid",
                            ("conv", $"dm:{ids[0]}:{ids[1]}"),
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
                var inserted = ExecuteScalarIntTx(tx, @"
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
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    int ExecuteScalarInt(string sql) {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    int ExecuteScalarIntTx(SqliteTransaction tx, string sql) {
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

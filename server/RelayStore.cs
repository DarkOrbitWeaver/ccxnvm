using Microsoft.Data.Sqlite;
using System.Data.Common;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

public enum RelayMessageKind {
    Direct = 0,
    Group = 1
}

public record PendingRelayMessage(
    RelayMessageKind Kind,
    string RecipientId,
    string SenderId,
    string Payload,
    string Sig,
    long SeqNum,
    long Ts,
    string? GroupId = null);

public sealed class RelayStoreOptions {
    public string Backend { get; init; } = "";
    public string SqlitePath { get; init; } = "";
    public string TursoUrl { get; init; } = "";
    public string TursoAuthToken { get; init; } = "";
    public int PendingTtlDays { get; init; } = 30;
    public int MaxPendingPerRecipient { get; init; } = 200;

    public static RelayStoreOptions FromEnvironment() {
        var backend = (Environment.GetEnvironmentVariable("RELAY_STORE") ?? "").Trim();
        var tursoUrl = NormalizeTursoUrl(Environment.GetEnvironmentVariable("TURSO_DATABASE_URL"));
        var tursoAuthToken = (Environment.GetEnvironmentVariable("TURSO_AUTH_TOKEN") ?? "").Trim();
        var sqlitePath = (Environment.GetEnvironmentVariable("RELAY_SQLITE_PATH") ?? "").Trim();
        var ttlDays = ParsePositiveInt(Environment.GetEnvironmentVariable("RELAY_PENDING_TTL_DAYS"), 30);
        var maxPending = ParsePositiveInt(Environment.GetEnvironmentVariable("RELAY_MAX_PENDING_PER_RECIPIENT"), 200);

        if (string.IsNullOrEmpty(backend)) {
            backend = !string.IsNullOrEmpty(tursoUrl) && !string.IsNullOrEmpty(tursoAuthToken)
                ? "turso"
                : "sqlite";
        }

        if (string.IsNullOrEmpty(sqlitePath)) {
            sqlitePath = Path.Combine(AppContext.BaseDirectory, "relay-data", "relay.db");
        }

        return new RelayStoreOptions {
            Backend = backend,
            SqlitePath = sqlitePath,
            TursoUrl = tursoUrl,
            TursoAuthToken = tursoAuthToken,
            PendingTtlDays = ttlDays,
            MaxPendingPerRecipient = maxPending
        };
    }

    static int ParsePositiveInt(string? raw, int fallback) =>
        int.TryParse(raw, out var value) && value > 0 ? value : fallback;

    static string NormalizeTursoUrl(string? rawUrl) {
        var url = (rawUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url)) return "";
        if (url.StartsWith("libsql://", StringComparison.OrdinalIgnoreCase)) {
            url = "https://" + url["libsql://".Length..];
        }
        return url;
    }
}

public interface IRelayStore {
    string Name { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task CleanupAsync(long nowMs, CancellationToken cancellationToken = default);
    Task<bool> AllowRequestAsync(string bucket, int limit, TimeSpan window, long nowMs, CancellationToken cancellationToken = default);
    Task UpsertKeyBundleAsync(KeyBundle bundle, CancellationToken cancellationToken = default);
    Task<KeyBundle?> GetKeyBundleAsync(string userId, CancellationToken cancellationToken = default);
    Task SetPublicDisplayNameAsync(string userId, string displayName, CancellationToken cancellationToken = default);
    Task<bool> TryStoreDirectAsync(string recipientId, string senderId, string payload, string sig, long seqNum, long ts, long expiresAt, CancellationToken cancellationToken = default);
    Task<bool> TryStoreGroupAsync(string groupId, IReadOnlyList<string> recipientIds, string senderId, string payload, string sig, long seqNum, long ts, long expiresAt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PendingRelayMessage>> GetPendingMessagesAsync(string recipientId, int limit, long nowMs, CancellationToken cancellationToken = default);
    Task AckDirectAsync(string recipientId, string senderId, long seqNum, CancellationToken cancellationToken = default);
    Task AckGroupAsync(string recipientId, string groupId, string senderId, long seqNum, CancellationToken cancellationToken = default);
}

public static class RelayStoreFactory {
    public static IRelayStore Create(RelayStoreOptions options, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory) {
        return options.Backend.Equals("turso", StringComparison.OrdinalIgnoreCase)
            ? new TursoRelayStore(options, httpClientFactory.CreateClient(nameof(TursoRelayStore)), loggerFactory.CreateLogger<TursoRelayStore>())
            : new SqliteRelayStore(options, loggerFactory.CreateLogger<SqliteRelayStore>());
    }
}

abstract class RelayStoreBase : IRelayStore {
    protected readonly RelayStoreOptions Options;
    protected readonly ILogger Logger;

    protected RelayStoreBase(RelayStoreOptions options, ILogger logger) {
        Options = options;
        Logger = logger;
    }

    public abstract string Name { get; }
    public abstract Task InitializeAsync(CancellationToken cancellationToken = default);
    public abstract Task CleanupAsync(long nowMs, CancellationToken cancellationToken = default);
    public abstract Task<bool> AllowRequestAsync(string bucket, int limit, TimeSpan window, long nowMs, CancellationToken cancellationToken = default);
    public abstract Task UpsertKeyBundleAsync(KeyBundle bundle, CancellationToken cancellationToken = default);
    public abstract Task<KeyBundle?> GetKeyBundleAsync(string userId, CancellationToken cancellationToken = default);
    public abstract Task SetPublicDisplayNameAsync(string userId, string displayName, CancellationToken cancellationToken = default);
    public abstract Task<bool> TryStoreDirectAsync(string recipientId, string senderId, string payload, string sig, long seqNum, long ts, long expiresAt, CancellationToken cancellationToken = default);
    public abstract Task<bool> TryStoreGroupAsync(string groupId, IReadOnlyList<string> recipientIds, string senderId, string payload, string sig, long seqNum, long ts, long expiresAt, CancellationToken cancellationToken = default);
    public abstract Task<IReadOnlyList<PendingRelayMessage>> GetPendingMessagesAsync(string recipientId, int limit, long nowMs, CancellationToken cancellationToken = default);
    public abstract Task AckDirectAsync(string recipientId, string senderId, long seqNum, CancellationToken cancellationToken = default);
    public abstract Task AckGroupAsync(string recipientId, string groupId, string senderId, long seqNum, CancellationToken cancellationToken = default);

    protected static string SchemaSql => """
        CREATE TABLE IF NOT EXISTS key_bundles (
            user_id TEXT PRIMARY KEY,
            sign_pub_key TEXT NOT NULL,
            dh_pub_key TEXT NOT NULL,
            registered_at INTEGER NOT NULL,
            display_name TEXT NOT NULL DEFAULT ''
        );

        CREATE TABLE IF NOT EXISTS direct_seq_state (
            recipient_id TEXT NOT NULL,
            sender_id TEXT NOT NULL,
            last_seq INTEGER NOT NULL,
            PRIMARY KEY (recipient_id, sender_id)
        );

        CREATE TABLE IF NOT EXISTS group_seq_state (
            group_id TEXT NOT NULL,
            sender_id TEXT NOT NULL,
            last_seq INTEGER NOT NULL,
            PRIMARY KEY (group_id, sender_id)
        );

        CREATE TABLE IF NOT EXISTS pending_messages (
            kind INTEGER NOT NULL,
            recipient_id TEXT NOT NULL,
            sender_id TEXT NOT NULL,
            payload TEXT NOT NULL,
            sig TEXT NOT NULL,
            seq_num INTEGER NOT NULL,
            ts INTEGER NOT NULL,
            group_id TEXT,
            expires_at INTEGER NOT NULL,
            inserted_at INTEGER NOT NULL,
            PRIMARY KEY (kind, recipient_id, sender_id, seq_num, group_id)
        );

        CREATE INDEX IF NOT EXISTS idx_pending_recipient ON pending_messages(recipient_id, inserted_at);
        CREATE INDEX IF NOT EXISTS idx_pending_expiry ON pending_messages(expires_at);

        CREATE TABLE IF NOT EXISTS rate_limit_events (
            bucket TEXT NOT NULL,
            seen_at INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_rate_limit_bucket ON rate_limit_events(bucket, seen_at);
        """;

    protected async Task PruneRecipientQueueAsync(Func<string, IReadOnlyList<SqlArg>, CancellationToken, Task> executeAsync, string recipientId, CancellationToken cancellationToken) {
        await executeAsync(
            """
            DELETE FROM pending_messages
            WHERE recipient_id = @recipient_id
              AND rowid IN (
                  SELECT rowid
                  FROM pending_messages
                  WHERE recipient_id = @recipient_id
                  ORDER BY inserted_at ASC
                  LIMIT (
                      SELECT CASE
                          WHEN COUNT(*) > @max_pending THEN COUNT(*) - @max_pending
                          ELSE 0
                      END
                      FROM pending_messages
                      WHERE recipient_id = @recipient_id
                  )
              )
            """,
            [SqlArg.Text("recipient_id", recipientId), SqlArg.Integer("max_pending", Options.MaxPendingPerRecipient)],
            cancellationToken);
    }
}

readonly record struct SqlArg(string Name, string Type, object? Value, string? Base64 = null) {
    public static SqlArg Text(string name, string value) => new(name, "text", value);
    public static SqlArg Integer(string name, long value) => new(name, "integer", value.ToString(CultureInfo.InvariantCulture));
    public static SqlArg Null(string name) => new(name, "null", null);

    public object ToDbValue() => Type == "null" ? DBNull.Value : Value ?? DBNull.Value;
    public object ToTursoValue() => Type == "blob"
        ? new Dictionary<string, object?> { ["type"] = Type, ["base64"] = Base64 }
        : new Dictionary<string, object?> { ["type"] = Type, ["value"] = Value };
}

sealed class SqliteRelayStore : RelayStoreBase {
    readonly string _connectionString;

    public SqliteRelayStore(RelayStoreOptions options, ILogger<SqliteRelayStore> logger)
        : base(options, logger) {
        var fullPath = Path.GetFullPath(options.SqlitePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
    }

    public override string Name => "sqlite";

    public override async Task InitializeAsync(CancellationToken cancellationToken = default) {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = SchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureDisplayNameColumnAsync(connection, cancellationToken);
        Logger.LogInformation("Relay store ready with local SQLite at {Path}", Options.SqlitePath);
    }

    public override async Task CleanupAsync(long nowMs, CancellationToken cancellationToken = default) {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection,
            "DELETE FROM pending_messages WHERE expires_at <= @now_ms",
            [SqlArg.Integer("now_ms", nowMs)],
            cancellationToken);
        await ExecuteNonQueryAsync(connection,
            "DELETE FROM rate_limit_events WHERE seen_at < @threshold",
            [SqlArg.Integer("threshold", nowMs - (long)TimeSpan.FromHours(1).TotalMilliseconds)],
            cancellationToken);
    }

    public override async Task<bool> AllowRequestAsync(string bucket, int limit, TimeSpan window, long nowMs, CancellationToken cancellationToken = default) {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var threshold = nowMs - (long)window.TotalMilliseconds;

        await ExecuteNonQueryAsync(connection,
            "DELETE FROM rate_limit_events WHERE bucket = @bucket AND seen_at < @threshold",
            [SqlArg.Text("bucket", bucket), SqlArg.Integer("threshold", threshold)],
            cancellationToken);
        await ExecuteNonQueryAsync(connection,
            "INSERT INTO rate_limit_events(bucket, seen_at) VALUES (@bucket, @seen_at)",
            [SqlArg.Text("bucket", bucket), SqlArg.Integer("seen_at", nowMs)],
            cancellationToken);

        var count = await ExecuteScalarLongAsync(connection,
            "SELECT COUNT(*) FROM rate_limit_events WHERE bucket = @bucket AND seen_at >= @threshold",
            [SqlArg.Text("bucket", bucket), SqlArg.Integer("threshold", threshold)],
            cancellationToken);

        return count <= limit;
    }

    public override async Task UpsertKeyBundleAsync(KeyBundle bundle, CancellationToken cancellationToken = default) {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection,
            """
            INSERT INTO key_bundles(user_id, sign_pub_key, dh_pub_key, registered_at, display_name)
            VALUES (@user_id, @sign_pub_key, @dh_pub_key, @registered_at, @display_name)
            ON CONFLICT(user_id) DO UPDATE SET
                sign_pub_key = excluded.sign_pub_key,
                dh_pub_key = excluded.dh_pub_key,
                registered_at = excluded.registered_at,
                display_name = CASE
                    WHEN length(excluded.display_name) > 0 THEN excluded.display_name
                    ELSE key_bundles.display_name
                END
            """,
            [
                SqlArg.Text("user_id", bundle.UserId),
                SqlArg.Text("sign_pub_key", bundle.SignPubKey),
                SqlArg.Text("dh_pub_key", bundle.DhPubKey),
                SqlArg.Integer("registered_at", bundle.RegisteredAt),
                SqlArg.Text("display_name", bundle.DisplayName)
            ],
            cancellationToken);
    }

    public override async Task<KeyBundle?> GetKeyBundleAsync(string userId, CancellationToken cancellationToken = default) {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT user_id, sign_pub_key, dh_pub_key, registered_at, display_name
            FROM key_bundles
            WHERE user_id = @user_id
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@user_id", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }

        return new KeyBundle(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4));
    }

    public override async Task SetPublicDisplayNameAsync(string userId, string displayName, CancellationToken cancellationToken = default) {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection,
            """
            UPDATE key_bundles
            SET display_name = @display_name
            WHERE user_id = @user_id
            """,
            [
                SqlArg.Text("user_id", userId),
                SqlArg.Text("display_name", displayName)
            ],
            cancellationToken);
    }

    static async Task EnsureDisplayNameColumnAsync(SqliteConnection connection, CancellationToken cancellationToken) {
        try {
            await ExecuteNonQueryAsync(connection,
                "ALTER TABLE key_bundles ADD COLUMN display_name TEXT NOT NULL DEFAULT ''",
                [],
                cancellationToken);
        } catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) {
        }
    }

    public override async Task<bool> TryStoreDirectAsync(string recipientId, string senderId, string payload, string sig, long seqNum, long ts, long expiresAt, CancellationToken cancellationToken = default) {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var advanced = await TryAdvanceDirectSeqAsync(connection, transaction, recipientId, senderId, seqNum, cancellationToken);
        if (!advanced) {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await InsertPendingAsync(connection, transaction, new PendingRelayMessage(
            RelayMessageKind.Direct,
            recipientId,
            senderId,
            payload,
            sig,
            seqNum,
            ts), expiresAt, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        await PruneRecipientQueueAsync((sql, args, ct) => ExecuteNonQueryAsync(connection, sql, args, ct), recipientId, cancellationToken);
        return true;
    }

    public override async Task<bool> TryStoreGroupAsync(string groupId, IReadOnlyList<string> recipientIds, string senderId, string payload, string sig, long seqNum, long ts, long expiresAt, CancellationToken cancellationToken = default) {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var advanced = await TryAdvanceGroupSeqAsync(connection, transaction, groupId, senderId, seqNum, cancellationToken);
        if (!advanced) {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        foreach (var recipientId in recipientIds.Distinct(StringComparer.Ordinal)) {
            await InsertPendingAsync(connection, transaction, new PendingRelayMessage(
                RelayMessageKind.Group,
                recipientId,
                senderId,
                payload,
                sig,
                seqNum,
                ts,
                groupId), expiresAt, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        foreach (var recipientId in recipientIds.Distinct(StringComparer.Ordinal)) {
            await PruneRecipientQueueAsync((sql, args, ct) => ExecuteNonQueryAsync(connection, sql, args, ct), recipientId, cancellationToken);
        }
        return true;
    }

    public override async Task<IReadOnlyList<PendingRelayMessage>> GetPendingMessagesAsync(string recipientId, int limit, long nowMs, CancellationToken cancellationToken = default) {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection,
            "DELETE FROM pending_messages WHERE expires_at <= @now_ms",
            [SqlArg.Integer("now_ms", nowMs)],
            cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, recipient_id, sender_id, payload, sig, seq_num, ts, group_id
            FROM pending_messages
            WHERE recipient_id = @recipient_id
              AND expires_at > @now_ms
            ORDER BY inserted_at ASC
            LIMIT @limit
            """;
        command.Parameters.AddWithValue("@recipient_id", recipientId);
        command.Parameters.AddWithValue("@now_ms", nowMs);
        command.Parameters.AddWithValue("@limit", limit);

        var list = new List<PendingRelayMessage>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            list.Add(new PendingRelayMessage(
                (RelayMessageKind)reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return list;
    }

    public override async Task AckDirectAsync(string recipientId, string senderId, long seqNum, CancellationToken cancellationToken = default) {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection,
            """
            DELETE FROM pending_messages
            WHERE kind = @kind
              AND recipient_id = @recipient_id
              AND sender_id = @sender_id
              AND seq_num = @seq_num
            """,
            [
                SqlArg.Integer("kind", (long)RelayMessageKind.Direct),
                SqlArg.Text("recipient_id", recipientId),
                SqlArg.Text("sender_id", senderId),
                SqlArg.Integer("seq_num", seqNum)
            ],
            cancellationToken);
    }

    public override async Task AckGroupAsync(string recipientId, string groupId, string senderId, long seqNum, CancellationToken cancellationToken = default) {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection,
            """
            DELETE FROM pending_messages
            WHERE kind = @kind
              AND recipient_id = @recipient_id
              AND sender_id = @sender_id
              AND seq_num = @seq_num
              AND group_id = @group_id
            """,
            [
                SqlArg.Integer("kind", (long)RelayMessageKind.Group),
                SqlArg.Text("recipient_id", recipientId),
                SqlArg.Text("sender_id", senderId),
                SqlArg.Integer("seq_num", seqNum),
                SqlArg.Text("group_id", groupId)
            ],
            cancellationToken);
    }

    static async Task<bool> TryAdvanceDirectSeqAsync(SqliteConnection connection, DbTransaction transaction, string recipientId, string senderId, long seqNum, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO direct_seq_state(recipient_id, sender_id, last_seq)
            VALUES (@recipient_id, @sender_id, @seq_num)
            ON CONFLICT(recipient_id, sender_id) DO UPDATE SET last_seq = excluded.last_seq
            WHERE excluded.last_seq > direct_seq_state.last_seq
            """;
        command.Parameters.AddWithValue("@recipient_id", recipientId);
        command.Parameters.AddWithValue("@sender_id", senderId);
        command.Parameters.AddWithValue("@seq_num", seqNum);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var changesCommand = connection.CreateCommand();
        changesCommand.Transaction = (SqliteTransaction)transaction;
        changesCommand.CommandText = "SELECT changes()";
        var raw = await changesCommand.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(raw, CultureInfo.InvariantCulture) > 0;
    }

    static async Task<bool> TryAdvanceGroupSeqAsync(SqliteConnection connection, DbTransaction transaction, string groupId, string senderId, long seqNum, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO group_seq_state(group_id, sender_id, last_seq)
            VALUES (@group_id, @sender_id, @seq_num)
            ON CONFLICT(group_id, sender_id) DO UPDATE SET last_seq = excluded.last_seq
            WHERE excluded.last_seq > group_seq_state.last_seq
            """;
        command.Parameters.AddWithValue("@group_id", groupId);
        command.Parameters.AddWithValue("@sender_id", senderId);
        command.Parameters.AddWithValue("@seq_num", seqNum);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using var changesCommand = connection.CreateCommand();
        changesCommand.Transaction = (SqliteTransaction)transaction;
        changesCommand.CommandText = "SELECT changes()";
        var raw = await changesCommand.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(raw, CultureInfo.InvariantCulture) > 0;
    }

    static async Task InsertPendingAsync(SqliteConnection connection, DbTransaction transaction, PendingRelayMessage message, long expiresAt, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO pending_messages
            (kind, recipient_id, sender_id, payload, sig, seq_num, ts, group_id, expires_at, inserted_at)
            VALUES (@kind, @recipient_id, @sender_id, @payload, @sig, @seq_num, @ts, @group_id, @expires_at, @inserted_at)
            """;
        command.Parameters.AddWithValue("@kind", (long)message.Kind);
        command.Parameters.AddWithValue("@recipient_id", message.RecipientId);
        command.Parameters.AddWithValue("@sender_id", message.SenderId);
        command.Parameters.AddWithValue("@payload", message.Payload);
        command.Parameters.AddWithValue("@sig", message.Sig);
        command.Parameters.AddWithValue("@seq_num", message.SeqNum);
        command.Parameters.AddWithValue("@ts", message.Ts);
        command.Parameters.AddWithValue("@group_id", (object?)message.GroupId ?? DBNull.Value);
        command.Parameters.AddWithValue("@expires_at", expiresAt);
        command.Parameters.AddWithValue("@inserted_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, IReadOnlyList<SqlArg> args, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddArgs(command, args);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    static async Task<long> ExecuteScalarLongAsync(SqliteConnection connection, string sql, IReadOnlyList<SqlArg> args, CancellationToken cancellationToken) {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddArgs(command, args);
        var raw = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(raw, CultureInfo.InvariantCulture);
    }

    static void AddArgs(SqliteCommand command, IReadOnlyList<SqlArg> args) {
        foreach (var arg in args) {
            command.Parameters.AddWithValue("@" + arg.Name, arg.ToDbValue());
        }
    }
}

sealed class TursoRelayStore : RelayStoreBase {
    readonly HttpClient _httpClient;

    public TursoRelayStore(RelayStoreOptions options, HttpClient httpClient, ILogger<TursoRelayStore> logger)
        : base(options, logger) {
        _httpClient = httpClient;
        if (string.IsNullOrEmpty(options.TursoUrl) || string.IsNullOrEmpty(options.TursoAuthToken)) {
            throw new InvalidOperationException("Turso relay store requires TURSO_DATABASE_URL and TURSO_AUTH_TOKEN.");
        }
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.TursoAuthToken);
    }

    public override string Name => "turso";

    public override async Task InitializeAsync(CancellationToken cancellationToken = default) {
        await ExecuteSequenceAsync(SchemaSql, cancellationToken);
        await EnsureDisplayNameColumnAsync(cancellationToken);
        Logger.LogInformation("Relay store ready with Turso at {Url}", Options.TursoUrl);
    }

    public override Task CleanupAsync(long nowMs, CancellationToken cancellationToken = default) =>
        ExecuteSequenceAsync(
            $"""
            DELETE FROM pending_messages WHERE expires_at <= {nowMs};
            DELETE FROM rate_limit_events WHERE seen_at < {nowMs - (long)TimeSpan.FromHours(1).TotalMilliseconds};
            """,
            cancellationToken);

    public override async Task<bool> AllowRequestAsync(string bucket, int limit, TimeSpan window, long nowMs, CancellationToken cancellationToken = default) {
        var threshold = nowMs - (long)window.TotalMilliseconds;
        await ExecuteNonQueryAsync(
            "DELETE FROM rate_limit_events WHERE bucket = @bucket AND seen_at < @threshold",
            [SqlArg.Text("bucket", bucket), SqlArg.Integer("threshold", threshold)],
            cancellationToken);
        await ExecuteNonQueryAsync(
            "INSERT INTO rate_limit_events(bucket, seen_at) VALUES (@bucket, @seen_at)",
            [SqlArg.Text("bucket", bucket), SqlArg.Integer("seen_at", nowMs)],
            cancellationToken);
        var count = await ExecuteScalarLongAsync(
            "SELECT COUNT(*) FROM rate_limit_events WHERE bucket = @bucket AND seen_at >= @threshold",
            [SqlArg.Text("bucket", bucket), SqlArg.Integer("threshold", threshold)],
            cancellationToken);
        return count <= limit;
    }

    public override Task UpsertKeyBundleAsync(KeyBundle bundle, CancellationToken cancellationToken = default) =>
        ExecuteNonQueryAsync(
            """
            INSERT INTO key_bundles(user_id, sign_pub_key, dh_pub_key, registered_at, display_name)
            VALUES (@user_id, @sign_pub_key, @dh_pub_key, @registered_at, @display_name)
            ON CONFLICT(user_id) DO UPDATE SET
                sign_pub_key = excluded.sign_pub_key,
                dh_pub_key = excluded.dh_pub_key,
                registered_at = excluded.registered_at,
                display_name = CASE
                    WHEN length(excluded.display_name) > 0 THEN excluded.display_name
                    ELSE key_bundles.display_name
                END
            """,
            [
                SqlArg.Text("user_id", bundle.UserId),
                SqlArg.Text("sign_pub_key", bundle.SignPubKey),
                SqlArg.Text("dh_pub_key", bundle.DhPubKey),
                SqlArg.Integer("registered_at", bundle.RegisteredAt),
                SqlArg.Text("display_name", bundle.DisplayName)
            ],
            cancellationToken);

    public override async Task<KeyBundle?> GetKeyBundleAsync(string userId, CancellationToken cancellationToken = default) {
        var rows = await QueryRowsAsync(
            """
            SELECT user_id, sign_pub_key, dh_pub_key, registered_at, display_name
            FROM key_bundles
            WHERE user_id = @user_id
            LIMIT 1
            """,
            [SqlArg.Text("user_id", userId)],
            cancellationToken);
        if (rows.Count == 0) return null;
        var row = rows[0];
        return new KeyBundle(
            AsString(row[0])!,
            AsString(row[1])!,
            AsString(row[2])!,
            AsLong(row[3]),
            AsString(row[4]) ?? "");
    }

    public override Task SetPublicDisplayNameAsync(string userId, string displayName, CancellationToken cancellationToken = default) =>
        ExecuteNonQueryAsync(
            """
            UPDATE key_bundles
            SET display_name = @display_name
            WHERE user_id = @user_id
            """,
            [
                SqlArg.Text("user_id", userId),
                SqlArg.Text("display_name", displayName)
            ],
            cancellationToken);

    async Task EnsureDisplayNameColumnAsync(CancellationToken cancellationToken) {
        try {
            await ExecuteNonQueryAsync(
                "ALTER TABLE key_bundles ADD COLUMN display_name TEXT NOT NULL DEFAULT ''",
                [],
                cancellationToken);
        } catch (InvalidOperationException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase) ||
                                                     ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)) {
        }
    }

    public override async Task<bool> TryStoreDirectAsync(string recipientId, string senderId, string payload, string sig, long seqNum, long ts, long expiresAt, CancellationToken cancellationToken = default) {
        await using var session = new TursoSession(_httpClient, Options.TursoUrl);
        await session.ExecuteNonQueryAsync("BEGIN IMMEDIATE", [], cancellationToken);
        var advanced = await session.ExecuteUpsertAndCheckChangesAsync(
            """
            INSERT INTO direct_seq_state(recipient_id, sender_id, last_seq)
            VALUES (@recipient_id, @sender_id, @seq_num)
            ON CONFLICT(recipient_id, sender_id) DO UPDATE SET last_seq = excluded.last_seq
            WHERE excluded.last_seq > direct_seq_state.last_seq
            """,
            [
                SqlArg.Text("recipient_id", recipientId),
                SqlArg.Text("sender_id", senderId),
                SqlArg.Integer("seq_num", seqNum)
            ],
            cancellationToken);
        if (!advanced) {
            await session.ExecuteNonQueryAsync("ROLLBACK", [], cancellationToken);
            return false;
        }

        await session.ExecuteNonQueryAsync(
            """
            INSERT OR IGNORE INTO pending_messages
            (kind, recipient_id, sender_id, payload, sig, seq_num, ts, group_id, expires_at, inserted_at)
            VALUES (@kind, @recipient_id, @sender_id, @payload, @sig, @seq_num, @ts, NULL, @expires_at, @inserted_at)
            """,
            [
                SqlArg.Integer("kind", (long)RelayMessageKind.Direct),
                SqlArg.Text("recipient_id", recipientId),
                SqlArg.Text("sender_id", senderId),
                SqlArg.Text("payload", payload),
                SqlArg.Text("sig", sig),
                SqlArg.Integer("seq_num", seqNum),
                SqlArg.Integer("ts", ts),
                SqlArg.Integer("expires_at", expiresAt),
                SqlArg.Integer("inserted_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            ],
            cancellationToken);

        await session.ExecuteNonQueryAsync("COMMIT", [], cancellationToken);
        await PruneRecipientQueueAsync(ExecuteNonQueryAsync, recipientId, cancellationToken);
        return true;
    }

    public override async Task<bool> TryStoreGroupAsync(string groupId, IReadOnlyList<string> recipientIds, string senderId, string payload, string sig, long seqNum, long ts, long expiresAt, CancellationToken cancellationToken = default) {
        await using var session = new TursoSession(_httpClient, Options.TursoUrl);
        await session.ExecuteNonQueryAsync("BEGIN IMMEDIATE", [], cancellationToken);
        var advanced = await session.ExecuteUpsertAndCheckChangesAsync(
            """
            INSERT INTO group_seq_state(group_id, sender_id, last_seq)
            VALUES (@group_id, @sender_id, @seq_num)
            ON CONFLICT(group_id, sender_id) DO UPDATE SET last_seq = excluded.last_seq
            WHERE excluded.last_seq > group_seq_state.last_seq
            """,
            [
                SqlArg.Text("group_id", groupId),
                SqlArg.Text("sender_id", senderId),
                SqlArg.Integer("seq_num", seqNum)
            ],
            cancellationToken);
        if (!advanced) {
            await session.ExecuteNonQueryAsync("ROLLBACK", [], cancellationToken);
            return false;
        }

        foreach (var recipientId in recipientIds.Distinct(StringComparer.Ordinal)) {
            await session.ExecuteNonQueryAsync(
                """
                INSERT OR IGNORE INTO pending_messages
                (kind, recipient_id, sender_id, payload, sig, seq_num, ts, group_id, expires_at, inserted_at)
                VALUES (@kind, @recipient_id, @sender_id, @payload, @sig, @seq_num, @ts, @group_id, @expires_at, @inserted_at)
                """,
                [
                    SqlArg.Integer("kind", (long)RelayMessageKind.Group),
                    SqlArg.Text("recipient_id", recipientId),
                    SqlArg.Text("sender_id", senderId),
                    SqlArg.Text("payload", payload),
                    SqlArg.Text("sig", sig),
                    SqlArg.Integer("seq_num", seqNum),
                    SqlArg.Integer("ts", ts),
                    SqlArg.Text("group_id", groupId),
                    SqlArg.Integer("expires_at", expiresAt),
                    SqlArg.Integer("inserted_at", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                ],
                cancellationToken);
        }

        await session.ExecuteNonQueryAsync("COMMIT", [], cancellationToken);
        foreach (var recipientId in recipientIds.Distinct(StringComparer.Ordinal)) {
            await PruneRecipientQueueAsync(ExecuteNonQueryAsync, recipientId, cancellationToken);
        }
        return true;
    }

    public override async Task<IReadOnlyList<PendingRelayMessage>> GetPendingMessagesAsync(string recipientId, int limit, long nowMs, CancellationToken cancellationToken = default) {
        await ExecuteNonQueryAsync(
            "DELETE FROM pending_messages WHERE expires_at <= @now_ms",
            [SqlArg.Integer("now_ms", nowMs)],
            cancellationToken);

        var rows = await QueryRowsAsync(
            """
            SELECT kind, recipient_id, sender_id, payload, sig, seq_num, ts, group_id
            FROM pending_messages
            WHERE recipient_id = @recipient_id
              AND expires_at > @now_ms
            ORDER BY inserted_at ASC
            LIMIT @limit
            """,
            [
                SqlArg.Text("recipient_id", recipientId),
                SqlArg.Integer("now_ms", nowMs),
                SqlArg.Integer("limit", limit)
            ],
            cancellationToken);

        return rows.Select(row => new PendingRelayMessage(
            (RelayMessageKind)AsLong(row[0]),
            AsString(row[1])!,
            AsString(row[2])!,
            AsString(row[3])!,
            AsString(row[4])!,
            AsLong(row[5]),
            AsLong(row[6]),
            AsString(row[7]))).ToArray();
    }

    public override Task AckDirectAsync(string recipientId, string senderId, long seqNum, CancellationToken cancellationToken = default) =>
        ExecuteNonQueryAsync(
            """
            DELETE FROM pending_messages
            WHERE kind = @kind
              AND recipient_id = @recipient_id
              AND sender_id = @sender_id
              AND seq_num = @seq_num
            """,
            [
                SqlArg.Integer("kind", (long)RelayMessageKind.Direct),
                SqlArg.Text("recipient_id", recipientId),
                SqlArg.Text("sender_id", senderId),
                SqlArg.Integer("seq_num", seqNum)
            ],
            cancellationToken);

    public override Task AckGroupAsync(string recipientId, string groupId, string senderId, long seqNum, CancellationToken cancellationToken = default) =>
        ExecuteNonQueryAsync(
            """
            DELETE FROM pending_messages
            WHERE kind = @kind
              AND recipient_id = @recipient_id
              AND sender_id = @sender_id
              AND seq_num = @seq_num
              AND group_id = @group_id
            """,
            [
                SqlArg.Integer("kind", (long)RelayMessageKind.Group),
                SqlArg.Text("recipient_id", recipientId),
                SqlArg.Text("sender_id", senderId),
                SqlArg.Integer("seq_num", seqNum),
                SqlArg.Text("group_id", groupId)
            ],
            cancellationToken);

    Task ExecuteSequenceAsync(string sql, CancellationToken cancellationToken) =>
        ExecutePipelineAsync([new PipelineRequest("sequence", Sql: sql), new PipelineRequest("close")], cancellationToken);

    Task ExecuteNonQueryAsync(string sql, IReadOnlyList<SqlArg> args, CancellationToken cancellationToken) =>
        ExecutePipelineAsync(
            [new PipelineRequest("execute", Stmt: new PipelineStmt(sql, args)), new PipelineRequest("close")],
            cancellationToken);

    async Task<long> ExecuteScalarLongAsync(string sql, IReadOnlyList<SqlArg> args, CancellationToken cancellationToken) {
        var rows = await QueryRowsAsync(sql, args, cancellationToken);
        return rows.Count == 0 ? 0 : AsLong(rows[0][0]);
    }

    async Task<List<object?[]>> QueryRowsAsync(string sql, IReadOnlyList<SqlArg> args, CancellationToken cancellationToken) {
        var response = await ExecutePipelineAsync(
            [new PipelineRequest("execute", Stmt: new PipelineStmt(sql, args)), new PipelineRequest("close")],
            cancellationToken);
        return response.GetRows(0);
    }

    async Task<TursoPipelineResponse> ExecutePipelineAsync(IReadOnlyList<PipelineRequest> requests, CancellationToken cancellationToken) {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{Options.TursoUrl}/v2/pipeline");
        httpRequest.Content = JsonContent.Create(new { baton = (string?)null, requests });
        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TursoPipelineResponse>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Turso returned an empty pipeline response.");
        payload.ThrowIfError();
        return payload;
    }

    public static long AsLong(object? value) => value switch {
        null => 0,
        long l => l,
        int i => i,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
        JsonElement element => element.ValueKind switch {
            JsonValueKind.Number => element.GetInt64(),
            JsonValueKind.String when long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        },
        _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
    };

    public static string? AsString(object? value) => value switch {
        null => null,
        string s => s,
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };
}

sealed class TursoSession : IAsyncDisposable {
    readonly HttpClient _httpClient;
    string _baseUrl;
    string? _baton;
    bool _closed;

    public TursoSession(HttpClient httpClient, string baseUrl) {
        _httpClient = httpClient;
        _baseUrl = baseUrl;
    }

    public async Task ExecuteNonQueryAsync(string sql, IReadOnlyList<SqlArg> args, CancellationToken cancellationToken) {
        var response = await SendAsync([new PipelineRequest("execute", Stmt: new PipelineStmt(sql, args))], cancellationToken);
        response.ThrowIfError();
    }

    public async Task<bool> ExecuteUpsertAndCheckChangesAsync(string sql, IReadOnlyList<SqlArg> args, CancellationToken cancellationToken) {
        var response = await SendAsync([
            new PipelineRequest("execute", Stmt: new PipelineStmt(sql, args)),
            new PipelineRequest("execute", Stmt: new PipelineStmt("SELECT changes()", []))
        ], cancellationToken);
        response.ThrowIfError();
        var rows = response.GetRows(1);
        return rows.Count > 0 && TursoRelayStore.AsLong(rows[0][0]) > 0;
    }

    async Task<TursoPipelineResponse> SendAsync(IReadOnlyList<PipelineRequest> requests, CancellationToken cancellationToken) {
        if (_closed) throw new ObjectDisposedException(nameof(TursoSession));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v2/pipeline");
        request.Content = JsonContent.Create(new { baton = _baton, requests });
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<TursoPipelineResponse>(stream, cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Turso returned an empty pipeline response.");
        if (!string.IsNullOrEmpty(payload.BaseUrl)) {
            _baseUrl = payload.BaseUrl;
        }
        _baton = payload.Baton;
        return payload;
    }

    public async ValueTask DisposeAsync() {
        if (_closed) return;
        _closed = true;
        if (_baton == null) return;

        try {
            await SendAsync([new PipelineRequest("close")], CancellationToken.None);
        } catch {
        }
    }
}

sealed record PipelineRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("stmt")] PipelineStmt? Stmt = null,
    [property: JsonPropertyName("sql")] string? Sql = null);

sealed record PipelineStmt(
    [property: JsonPropertyName("sql")] string Sql,
    [property: JsonIgnore] IReadOnlyList<SqlArg> Args) {
    [JsonPropertyName("args")]
    public object[]? args => null;

    [JsonPropertyName("named_args")]
    public object[] named_args => Args.Select(arg => new Dictionary<string, object?> {
        ["name"] = arg.Name,
        ["value"] = arg.ToTursoValue()
    }).Cast<object>().ToArray();
}

sealed class TursoPipelineResponse {
    [JsonPropertyName("baton")]
    public string? Baton { get; set; }

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("results")]
    public List<TursoPipelineResult> Results { get; set; } = [];

    public void ThrowIfError() {
        var error = Results.FirstOrDefault(result => string.Equals(result.Type, "error", StringComparison.OrdinalIgnoreCase));
        if (error?.Error is { } errorDetails) {
            throw new InvalidOperationException(errorDetails.Message ?? "Turso pipeline failed.");
        }
    }

    public List<object?[]> GetRows(int resultIndex) {
        var result = Results[resultIndex];
        var rowElements = result.Response?.Result?.Rows;
        if (rowElements == null) return [];

        var rows = new List<object?[]>();
        foreach (var row in rowElements) {
            var values = new object?[row.GetArrayLength()];
            for (var i = 0; i < row.GetArrayLength(); i++) {
                values[i] = ParseTursoValue(row[i]);
            }
            rows.Add(values);
        }
        return rows;
    }

    static object? ParseTursoValue(JsonElement element) {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("type", out var typeElement)) {
            var type = typeElement.GetString();
            return type switch {
                "null" => null,
                "text" => element.GetProperty("value").GetString(),
                "integer" => element.GetProperty("value").GetString(),
                "float" => element.GetProperty("value").GetString(),
                "blob" => element.GetProperty("base64").GetString(),
                _ => element.ToString()
            };
        }

        return element.ValueKind switch {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetInt64(),
            _ => element.ToString()
        };
    }
}

sealed class TursoPipelineResult {
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("response")]
    public TursoPipelineResponseBody? Response { get; set; }

    [JsonPropertyName("error")]
    public TursoPipelineError? Error { get; set; }
}

sealed class TursoPipelineResponseBody {
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("result")]
    public TursoStatementResult? Result { get; set; }
}

sealed class TursoStatementResult {
    [JsonPropertyName("cols")]
    public JsonElement[]? Cols { get; set; }

    [JsonPropertyName("rows")]
    public JsonElement[]? Rows { get; set; }
}

sealed class TursoPipelineError {
    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

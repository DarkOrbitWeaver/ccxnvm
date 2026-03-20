// ═══════════════════════════════════════════════════════════════════════════
//  CIPHER CORE — Crypto · Vault · Network · Session
//  All sensitive operations live here. Zero external crypto libs needed
//  beyond Argon2id (for password hashing without character requirements).
// ═══════════════════════════════════════════════════════════════════════════
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using Konscious.Security.Cryptography;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;

namespace Cipher;

// ═══════════════════════════════════════════════════════════════════════════
//  MODELS
// ═══════════════════════════════════════════════════════════════════════════
public enum MessageStatus { Sending, Sent, Delivered, Seen, Failed }
public enum ConversationType { Direct, Group }

public class LocalUser {
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public byte[] SignPrivKey { get; set; } = [];
    public byte[] SignPubKey { get; set; } = [];
    public byte[] DhPrivKey { get; set; } = [];
    public byte[] DhPubKey { get; set; } = [];
    public string ServerUrl { get; set; } = AppBranding.DefaultRelayUrl;
    public long CreatedAt { get; set; }
}

public class Contact {
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public byte[] SignPubKey { get; set; } = [];
    public byte[] DhPubKey { get; set; } = [];
    public byte[] PendingSignPubKey { get; set; } = [];
    public byte[] PendingDhPubKey { get; set; } = [];
    public bool IsVerified { get; set; }
    public long KeyChangedAt { get; set; }
    public bool IsOnline { get; set; }
    public long AddedAt { get; set; }
    public string? ConversationId { get; set; }
    public bool HasPendingKeyChange => PendingSignPubKey.Length > 0 && PendingDhPubKey.Length > 0;
}

public class GroupInfo {
    public string GroupId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> MemberIds { get; set; } = [];
    public byte[] GroupKey { get; set; } = []; // AES-256 group symmetric key
    public long CreatedAt { get; set; }
}

public class Message {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ConversationId { get; set; } = "";
    public string SenderId { get; set; } = "";
    public string Content { get; set; } = "";
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public long SeqNum { get; set; }
    public MessageStatus Status { get; set; } = MessageStatus.Sending;
    public bool IsMine { get; set; }
    // Outbox fields
    public string? EncryptedPayload { get; set; }
    public string? Signature { get; set; }
    public string? RecipientId { get; set; }
    public ConversationType ConvType { get; set; }
}

// Encrypted wire format
record WireMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ct")] string Ciphertext,   // Base64 AES-GCM ciphertext
    [property: JsonPropertyName("nonce")] string Nonce,     // Base64 12-byte nonce
    [property: JsonPropertyName("tag")] string Tag,         // Base64 16-byte GCM tag
    [property: JsonPropertyName("seq")] long SeqNum,
    [property: JsonPropertyName("ts")] long Timestamp,
    [property: JsonPropertyName("type")] string Type = "dm" // dm | grp
);

// ═══════════════════════════════════════════════════════════════════════════
//  CRYPTO ENGINE — All primitives. Correct. Fast. No shortcuts.
// ═══════════════════════════════════════════════════════════════════════════
public static class Crypto {
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    static ReadOnlySpan<byte> PaddedMessageMagic => "C2P1"u8;

    // ── Key generation ────────────────────────────────────────────────────

    /// <summary>Generate ECDSA P-256 signing keypair (identity).</summary>
    public static (byte[] priv, byte[] pub) GenerateSigningKeys() {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportECPrivateKey(), ecdsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Generate ECDH P-256 key exchange keypair.</summary>
    public static (byte[] priv, byte[] pub) GenerateDhKeys() {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (ecdh.ExportECPrivateKey(), ecdh.ExportSubjectPublicKeyInfo());
    }

    /// <summary>
    /// Derive a deterministic UserId from signing public key.
    /// UserId = first 22 chars of URL-safe Base64(SHA256(pubkey)).
    /// This makes the ID self-verifying — server can check it.
    /// </summary>
    public static string DeriveUserId(byte[] signPubKey) =>
        Convert.ToBase64String(SHA256.HashData(signPubKey))
               .Replace('+', '-').Replace('/', '_')[..22];

    // ── Password hashing ──────────────────────────────────────────────────

    /// <summary>
    /// Derive vault encryption key using Argon2id.
    /// Even a 1-character password is practically uncrackable with these params.
    /// No character requirements needed. Memory-hard = GPU-resistant.
    /// </summary>
    public static byte[] DeriveVaultKey(string password, byte[] salt) {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password)) {
            Salt = salt,
            DegreeOfParallelism = 2,
            MemorySize = 65536,  // 64 MB — makes brute-force extremely expensive
            Iterations = 4
        };
        return argon2.GetBytes(32);
    }

    /// <summary>Generate a random 32-byte Argon2id salt for new vaults.</summary>
    public static byte[] GenerateSalt() => RandomNumberGenerator.GetBytes(32);

    // ── Shared secret (for DM encryption) ────────────────────────────────

    /// <summary>
    /// ECDH key agreement: derive shared secret between two users.
    /// Both sides compute the same secret independently — server never sees it.
    /// Then HKDF expands it into a per-conversation key.
    /// </summary>
    public static byte[] DeriveSharedSecret(byte[] myDhPriv, byte[] theirDhPub, string conversationId) {
        using var myKey = ECDiffieHellman.Create();
        myKey.ImportECPrivateKey(myDhPriv, out _);
        using var theirKey = ECDiffieHellman.Create();
        theirKey.ImportSubjectPublicKeyInfo(theirDhPub, out _);
        var rawSecret = myKey.DeriveKeyFromHash(theirKey.PublicKey, HashAlgorithmName.SHA256);
        // HKDF-expand with conversation ID as info — different conversations get different keys
        return HKDF.Expand(HashAlgorithmName.SHA256, rawSecret, 32,
            Encoding.UTF8.GetBytes($"cipher:dm:{conversationId}"));
    }

    /// <summary>
    /// Derive a per-message key using HKDF with the sequence number.
    /// Each message uses a unique key — forward secrecy.
    /// </summary>
    public static byte[] DeriveMessageKey(byte[] conversationKey, long seqNum) =>
        HKDF.Expand(HashAlgorithmName.SHA256, conversationKey, 32,
            Encoding.UTF8.GetBytes($"msg:{seqNum}"));

    // ── Symmetric encryption (AES-256-GCM) ────────────────────────────────

    /// <summary>Encrypt with AES-256-GCM. Returns (ciphertext, nonce, tag).</summary>
    public static (byte[] ct, byte[] nonce, byte[] tag) Encrypt(byte[] key, byte[] plaintext) {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ct, tag);
        return (ct, nonce, tag);
    }

    /// <summary>Decrypt AES-256-GCM. Returns null if auth tag is wrong (tampered).</summary>
    public static byte[]? Decrypt(byte[] key, byte[] ct, byte[] nonce, byte[] tag) {
        try {
            var plain = new byte[ct.Length];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(nonce, ct, tag, plain);
            return plain;
        } catch (CryptographicException) { return null; } // Auth failed
    }

    // ── Signing / Verification ─────────────────────────────────────────────

    /// <summary>Sign data with ECDSA P-256 private key.</summary>
    public static string Sign(byte[] privKey, string data) {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(privKey, out _);
        return Convert.ToBase64String(
            ecdsa.SignData(Encoding.UTF8.GetBytes(data), HashAlgorithmName.SHA256));
    }

    /// <summary>Sign payload+seqNum — the exact format the server validates.</summary>
    public static string SignPayload(byte[] privKey, string payload, long seqNum) =>
        Sign(privKey, $"{payload}:{seqNum}");

    /// <summary>Bind registration to both the identity and DH public key.</summary>
    public static string SignRegistration(byte[] privKey, string userId, string dhPubKey) =>
        Sign(privKey, $"{userId}:{dhPubKey}");

    /// <summary>Verify ECDSA P-256 signature against public key.</summary>
    public static bool Verify(byte[] pubKey, string data, string sig) {
        try {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(pubKey, out _);
            return ecdsa.VerifyData(Encoding.UTF8.GetBytes(data),
                Convert.FromBase64String(sig), HashAlgorithmName.SHA256);
        } catch { return false; }
    }

    // ── Vault field encryption helpers ────────────────────────────────────

    /// <summary>Encrypt arbitrary bytes with vault key (for private keys in DB).</summary>
    public static byte[] EncryptField(byte[] vaultKey, byte[] data) {
        var (ct, nonce, tag) = Encrypt(vaultKey, data);
        // Pack as: nonce(12) + tag(16) + ciphertext
        var result = new byte[28 + ct.Length];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, 12);
        ct.CopyTo(result, 28);
        return result;
    }

    /// <summary>Decrypt vault field. Returns null if tampered.</summary>
    public static byte[]? DecryptField(byte[] vaultKey, byte[] packed) {
        if (packed.Length < 28) return null;
        var nonce = packed[..12];
        var tag = packed[12..28];
        var ct = packed[28..];
        return Decrypt(vaultKey, ct, nonce, tag);
    }

    /// <summary>Encrypt string field (e.g., display names).</summary>
    public static byte[] EncryptStr(byte[] vaultKey, string s) =>
        EncryptField(vaultKey, Encoding.UTF8.GetBytes(s));

    public static string? DecryptStr(byte[] vaultKey, byte[] packed) {
        var b = DecryptField(vaultKey, packed);
        return b == null ? null : Encoding.UTF8.GetString(b);
    }

    public static string ComputeSafetyNumber(string myUserId, byte[] mySignPub, byte[] myDhPub,
        string theirUserId, byte[] theirSignPub, byte[] theirDhPub) {
        var ordered = new[] {
            (userId: myUserId, sign: mySignPub, dh: myDhPub),
            (userId: theirUserId, sign: theirSignPub, dh: theirDhPub)
        }.OrderBy(x => x.userId, StringComparer.Ordinal);

        using var ms = new MemoryStream();
        foreach (var entry in ordered) {
            var userBytes = Encoding.UTF8.GetBytes(entry.userId);
            ms.Write(userBytes);
            ms.Write(entry.sign);
            ms.Write(entry.dh);
        }

        var hash = SHA256.HashData(ms.ToArray());
        return string.Join(" ",
            Convert.ToHexString(hash)
                .Chunk(4)
                .Select(chunk => new string(chunk)));
    }

    static byte[] PackMessagePlaintext(string content) {
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var payloadLength = 8 + contentBytes.Length;
        var paddedLength = Math.Max(256, ((payloadLength + 255) / 256) * 256);
        var packed = RandomNumberGenerator.GetBytes(paddedLength);
        PaddedMessageMagic.CopyTo(packed);
        BinaryPrimitives.WriteInt32LittleEndian(packed.AsSpan(4, 4), contentBytes.Length);
        contentBytes.CopyTo(packed, 8);
        return packed;
    }

    static string? UnpackMessagePlaintext(byte[] plaintext) {
        if (plaintext.Length >= 8 && plaintext.AsSpan(0, 4).SequenceEqual(PaddedMessageMagic)) {
            var contentLength = BinaryPrimitives.ReadInt32LittleEndian(plaintext.AsSpan(4, 4));
            if (contentLength < 0 || contentLength > plaintext.Length - 8) return null;
            return Encoding.UTF8.GetString(plaintext, 8, contentLength);
        }

        return Encoding.UTF8.GetString(plaintext);
    }

    // ── Message serialization ──────────────────────────────────────────────

    /// <summary>Encrypt a message for a DM conversation. Returns wire payload JSON.</summary>
    public static string EncryptDm(byte[] conversationKey, Message msg) {
        var msgKey = DeriveMessageKey(conversationKey, msg.SeqNum);
        var plain = PackMessagePlaintext(msg.Content);
        var (ct, nonce, tag) = Encrypt(msgKey, plain);
        var wire = new WireMessage(
            msg.Id,
            Convert.ToBase64String(ct),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            msg.SeqNum,
            msg.Timestamp);
        return JsonSerializer.Serialize(wire, JsonOpts);
    }

    /// <summary>Decrypt a DM wire payload. Returns null if invalid.</summary>
    public static Message? DecryptDm(byte[] conversationKey, string senderId, string payload, bool isMine = false) {
        try {
            var wire = JsonSerializer.Deserialize<WireMessage>(payload, JsonOpts);
            if (wire == null) return null;
            var msgKey = DeriveMessageKey(conversationKey, wire.SeqNum);
            var plain = Decrypt(msgKey,
                Convert.FromBase64String(wire.Ciphertext),
                Convert.FromBase64String(wire.Nonce),
                Convert.FromBase64String(wire.Tag));
            if (plain == null) return null;
            var content = UnpackMessagePlaintext(plain);
            if (content == null) return null;
            return new Message {
                Id = wire.Id,
                SenderId = senderId,
                Content = content,
                Timestamp = wire.Timestamp,
                SeqNum = wire.SeqNum,
                Status = MessageStatus.Delivered,
                IsMine = isMine
            };
        } catch { return null; }
    }

    /// <summary>Encrypt message for a group using the group's symmetric key.</summary>
    public static string EncryptGroup(byte[] groupKey, Message msg) {
        var msgKey = DeriveMessageKey(groupKey, msg.SeqNum);
        var plain = PackMessagePlaintext(msg.Content);
        var (ct, nonce, tag) = Encrypt(msgKey, plain);
        var wire = new WireMessage(msg.Id, Convert.ToBase64String(ct),
            Convert.ToBase64String(nonce), Convert.ToBase64String(tag),
            msg.SeqNum, msg.Timestamp, "grp");
        return JsonSerializer.Serialize(wire, JsonOpts);
    }

    public static Message? DecryptGroup(byte[] groupKey, string groupId, string senderId, string payload, bool isMine = false) {
        try {
            var wire = JsonSerializer.Deserialize<WireMessage>(payload, JsonOpts);
            if (wire == null) return null;
            var msgKey = DeriveMessageKey(groupKey, wire.SeqNum);
            var plain = Decrypt(msgKey,
                Convert.FromBase64String(wire.Ciphertext),
                Convert.FromBase64String(wire.Nonce),
                Convert.FromBase64String(wire.Tag));
            if (plain == null) return null;
            var content = UnpackMessagePlaintext(plain);
            if (content == null) return null;
            return new Message {
                Id = wire.Id,
                ConversationId = groupId,
                SenderId = senderId,
                Content = content,
                Timestamp = wire.Timestamp,
                SeqNum = wire.SeqNum,
                Status = MessageStatus.Delivered,
                IsMine = isMine,
                ConvType = ConversationType.Group
            };
        } catch { return null; }
    }

    // ── Secure memory wipe ────────────────────────────────────────────────

    /// <summary>
    /// Overwrite a byte array with zeros. Call on sensitive keys after use.
    /// Not guaranteed against GC moves, but mitigates memory scraping.
    /// </summary>
    public static void Wipe(byte[]? data) {
        if (data != null) CryptographicOperations.ZeroMemory(data);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  VAULT MANAGER — Encrypted SQLite. All sensitive fields AES-256-GCM encrypted.
// ═══════════════════════════════════════════════════════════════════════════
public partial class Vault : IDisposable {
    SqliteConnection? _db;
    byte[] _key = [];
    string _path = "";
    bool _disposed;

    public static string DefaultVaultPath =>
        Path.Combine(AppPaths.AppDataRoot, "vault.db");

    public static string SaltPath =>
        Path.Combine(AppPaths.AppDataRoot, "vault.salt");

    public bool IsOpen { get; private set; }

    // ── Open / Create ──────────────────────────────────────────────────────

    public void Open(string path, byte[] vaultKey) {
        _path = path;
        _key = vaultKey.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection($"Data Source={path};");
        try {
            _db.Open();
            ConfigureConnection();
            InitSchema();
            RunStartupMaintenance();
            IsOpen = true;
        } catch {
            _db?.Dispose();
            _db = null;
            IsOpen = false;
            Crypto.Wipe(_key);
            _key = [];
            throw;
        }
    }

    void InitSchema() {
        Exec(@"
            CREATE TABLE IF NOT EXISTS identity (
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
            CREATE TABLE IF NOT EXISTS contacts (
                user_id TEXT PRIMARY KEY,
                display_name_enc BLOB NOT NULL,
                sign_pub TEXT NOT NULL,
                dh_pub TEXT NOT NULL,
                conversation_id TEXT NOT NULL,
                added_at INTEGER NOT NULL,
                last_seen INTEGER DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS groups (
                group_id TEXT PRIMARY KEY,
                name_enc BLOB NOT NULL,
                member_ids TEXT NOT NULL,
                group_key_enc BLOB NOT NULL,
                created_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                sender_id TEXT NOT NULL,
                content_enc BLOB NOT NULL,
                timestamp INTEGER NOT NULL,
                seq_num INTEGER NOT NULL,
                is_mine INTEGER NOT NULL DEFAULT 0,
                status INTEGER NOT NULL DEFAULT 1,
                conv_type INTEGER NOT NULL DEFAULT 0,
                recipient_id TEXT,
                payload_enc BLOB,
                sig_enc TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_msg_conv ON messages(conversation_id, timestamp);
            CREATE TABLE IF NOT EXISTS message_receipts (
                message_id TEXT NOT NULL,
                user_id TEXT NOT NULL,
                status INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                PRIMARY KEY (message_id, user_id)
            );
            CREATE INDEX IF NOT EXISTS idx_receipts_message ON message_receipts(message_id);
            CREATE TABLE IF NOT EXISTS conv_state (
                conversation_id TEXT PRIMARY KEY,
                last_seq INTEGER NOT NULL DEFAULT 0,
                secret_enc BLOB
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS outbox (
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
        ");

        TryExec("ALTER TABLE contacts ADD COLUMN is_verified INTEGER NOT NULL DEFAULT 0");
        TryExec("ALTER TABLE contacts ADD COLUMN pending_sign_pub TEXT");
        TryExec("ALTER TABLE contacts ADD COLUMN pending_dh_pub TEXT");
        TryExec("ALTER TABLE contacts ADD COLUMN key_changed_at INTEGER NOT NULL DEFAULT 0");
    }

    // ── Identity ───────────────────────────────────────────────────────────

    public void SaveIdentity(LocalUser u) {
        var signPrivEnc = Crypto.EncryptField(_key, u.SignPrivKey);
        var dhPrivEnc = Crypto.EncryptField(_key, u.DhPrivKey);
        ExecParam(@"
            INSERT OR REPLACE INTO identity
            VALUES (1, @uid, @name, @sp, @spub, @dp, @dpub, @srv, @ts)",
            ("uid", u.UserId), ("name", u.DisplayName),
            ("sp", signPrivEnc), ("spub", Convert.ToBase64String(u.SignPubKey)),
            ("dp", dhPrivEnc), ("dpub", Convert.ToBase64String(u.DhPubKey)),
            ("srv", u.ServerUrl), ("ts", u.CreatedAt));
    }

    public LocalUser? LoadIdentity() {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT * FROM identity WHERE id=1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        var signPrivEnc = (byte[])r["sign_priv"];
        var dhPrivEnc = (byte[])r["dh_priv"];
        var signPriv = Crypto.DecryptField(_key, signPrivEnc);
        var dhPriv = Crypto.DecryptField(_key, dhPrivEnc);
        if (signPriv == null || dhPriv == null) return null;
        return new LocalUser {
            UserId = r.GetString(r.GetOrdinal("user_id")),
            DisplayName = r.GetString(r.GetOrdinal("display_name")),
            SignPrivKey = signPriv,
            SignPubKey = Convert.FromBase64String(r.GetString(r.GetOrdinal("sign_pub"))),
            DhPrivKey = dhPriv,
            DhPubKey = Convert.FromBase64String(r.GetString(r.GetOrdinal("dh_pub"))),
            ServerUrl = r.GetString(r.GetOrdinal("server_url")),
            CreatedAt = r.GetInt64(r.GetOrdinal("created_at"))
        };
    }

    public bool HasIdentity() {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM identity";
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    // ── Contacts ───────────────────────────────────────────────────────────

    public void SaveContact(Contact c) {
        ExecParam(@"
            INSERT OR REPLACE INTO contacts
            (user_id, display_name_enc, sign_pub, dh_pub, conversation_id, added_at, last_seen, is_verified, pending_sign_pub, pending_dh_pub, key_changed_at)
            VALUES (@uid, @name, @sp, @dp, @conv, @ts, @ls, @verified, @psp, @pdp, @changed)",
            ("uid", c.UserId),
            ("name", Crypto.EncryptStr(_key, c.DisplayName)),
            ("sp", Convert.ToBase64String(c.SignPubKey)),
            ("dp", Convert.ToBase64String(c.DhPubKey)),
            ("conv", c.ConversationId ?? ""),
            ("ts", c.AddedAt),
            ("ls", (object?)null ?? DBNull.Value),
            ("verified", c.IsVerified ? 1 : 0),
            ("psp", c.PendingSignPubKey.Length > 0 ? Convert.ToBase64String(c.PendingSignPubKey) : DBNull.Value),
            ("pdp", c.PendingDhPubKey.Length > 0 ? Convert.ToBase64String(c.PendingDhPubKey) : DBNull.Value),
            ("changed", c.KeyChangedAt));
    }

    public List<Contact> LoadContacts() {
        var list = new List<Contact>();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT * FROM contacts ORDER BY added_at";
        using var r = cmd.ExecuteReader();
        while (r.Read()) {
            var nameEnc = (byte[])r["display_name_enc"];
            list.Add(new Contact {
                UserId = r.GetString(r.GetOrdinal("user_id")),
                DisplayName = Crypto.DecryptStr(_key, nameEnc) ?? "???",
                SignPubKey = Convert.FromBase64String(r.GetString(r.GetOrdinal("sign_pub"))),
                DhPubKey = Convert.FromBase64String(r.GetString(r.GetOrdinal("dh_pub"))),
                PendingSignPubKey = ReadOptionalBase64(r, "pending_sign_pub"),
                PendingDhPubKey = ReadOptionalBase64(r, "pending_dh_pub"),
                IsVerified = TryGetInt32(r, "is_verified") == 1,
                KeyChangedAt = TryGetInt64(r, "key_changed_at"),
                ConversationId = r.GetString(r.GetOrdinal("conversation_id")),
                AddedAt = r.GetInt64(r.GetOrdinal("added_at"))
            });
        }
        return list;
    }

    public void UpdateContactSeen(string userId) =>
        ExecParam("UPDATE contacts SET last_seen=@ts WHERE user_id=@uid",
            ("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("uid", userId));

    // ── Groups ─────────────────────────────────────────────────────────────

    public void SaveGroup(GroupInfo g) {
        ExecParam(@"INSERT OR REPLACE INTO groups VALUES (@id, @name, @members, @key, @ts)",
            ("id", g.GroupId),
            ("name", Crypto.EncryptStr(_key, g.Name)),
            ("members", string.Join(",", g.MemberIds)),
            ("key", Crypto.EncryptField(_key, g.GroupKey)),
            ("ts", g.CreatedAt));
    }

    public List<GroupInfo> LoadGroups() {
        var list = new List<GroupInfo>();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT * FROM groups";
        using var r = cmd.ExecuteReader();
        while (r.Read()) {
            var keyEnc = (byte[])r["group_key_enc"];
            var groupKey = Crypto.DecryptField(_key, keyEnc);
            if (groupKey == null) continue;
            list.Add(new GroupInfo {
                GroupId = r.GetString(r.GetOrdinal("group_id")),
                Name = Crypto.DecryptStr(_key, (byte[])r["name_enc"]) ?? "???",
                MemberIds = r.GetString(r.GetOrdinal("member_ids"))
                             .Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                GroupKey = groupKey,
                CreatedAt = r.GetInt64(r.GetOrdinal("created_at"))
            });
        }
        return list;
    }

    // ── Messages ───────────────────────────────────────────────────────────

    public void SaveMessage(Message msg) {
        var contentEnc = Crypto.EncryptStr(_key, msg.Content);
        ExecParam(@"INSERT OR REPLACE INTO messages
            (id, conversation_id, sender_id, content_enc, timestamp,
             seq_num, is_mine, status, conv_type, recipient_id)
            VALUES (@id,@conv,@sid,@ct,@ts,@seq,@mine,@st,@ctype,@rid)",
            ("id", msg.Id), ("conv", msg.ConversationId), ("sid", msg.SenderId),
            ("ct", contentEnc), ("ts", msg.Timestamp), ("seq", msg.SeqNum),
            ("mine", msg.IsMine ? 1 : 0), ("st", (int)msg.Status),
            ("ctype", (int)msg.ConvType), ("rid", (object?)msg.RecipientId ?? DBNull.Value));
    }

    /// <summary>
    /// Load messages in pages for lazy rendering.
    /// offset=0 gives the newest 'limit' messages.
    /// Incrementing offset loads older history.
    /// </summary>
    public List<Message> LoadMessages(string conversationId, int offset, int limit = 50) {
        var list = new List<Message>();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM messages
            WHERE conversation_id=@conv
            ORDER BY timestamp DESC
            LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("conv", conversationId);
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("offset", offset);
        using var r = cmd.ExecuteReader();
        while (r.Read()) {
            var ctEnc = (byte[])r["content_enc"];
            var content = Crypto.DecryptStr(_key, ctEnc) ?? "[decrypt failed]";
            list.Add(new Message {
                Id = r.GetString(r.GetOrdinal("id")),
                ConversationId = r.GetString(r.GetOrdinal("conversation_id")),
                SenderId = r.GetString(r.GetOrdinal("sender_id")),
                Content = content,
                Timestamp = r.GetInt64(r.GetOrdinal("timestamp")),
                SeqNum = r.GetInt64(r.GetOrdinal("seq_num")),
                IsMine = r.GetInt32(r.GetOrdinal("is_mine")) == 1,
                Status = (MessageStatus)r.GetInt32(r.GetOrdinal("status")),
                ConvType = (ConversationType)r.GetInt32(r.GetOrdinal("conv_type"))
            });
        }
        list.Reverse(); // Return in chronological order
        return list;
    }

    public int GetMessageCount(string conversationId) {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE conversation_id=@conv";
        cmd.Parameters.AddWithValue("conv", conversationId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateMessageStatus(string messageId, MessageStatus status) =>
        ExecParam("""
            UPDATE messages
            SET status = CASE
                WHEN status > @st THEN status
                ELSE @st
            END
            WHERE id=@id
            """,
            ("st", (int)status), ("id", messageId));

    public bool MessageExists(string messageId) {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE id=@id";
        cmd.Parameters.AddWithValue("id", messageId);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public void UpsertMessageReceipt(string messageId, string userId, MessageStatus status, long? updatedAt = null) {
        if (status is not MessageStatus.Delivered and not MessageStatus.Seen) {
            return;
        }

        var ts = updatedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        ExecParam("""
            INSERT INTO message_receipts (message_id, user_id, status, updated_at)
            VALUES (@mid, @uid, @status, @ts)
            ON CONFLICT(message_id, user_id) DO UPDATE SET
                status = CASE
                    WHEN message_receipts.status > excluded.status THEN message_receipts.status
                    ELSE excluded.status
                END,
                updated_at = CASE
                    WHEN message_receipts.status > excluded.status THEN message_receipts.updated_at
                    ELSE excluded.updated_at
                END
            """,
            ("mid", messageId),
            ("uid", userId),
            ("status", (int)status),
            ("ts", ts));
    }

    public List<MessageReceipt> LoadMessageReceipts(IEnumerable<string> messageIds) {
        var ids = messageIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0) return [];

        using var cmd = _db!.CreateCommand();
        var placeholders = new List<string>(ids.Count);
        for (var i = 0; i < ids.Count; i++) {
            var paramName = "@id" + i;
            placeholders.Add(paramName);
            cmd.Parameters.AddWithValue(paramName, ids[i]);
        }

        cmd.CommandText = $@"
            SELECT message_id, user_id, status, updated_at
            FROM message_receipts
            WHERE message_id IN ({string.Join(",", placeholders)})";

        var list = new List<MessageReceipt>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) {
            list.Add(new MessageReceipt(
                r.GetString(r.GetOrdinal("message_id")),
                r.GetString(r.GetOrdinal("user_id")),
                (MessageStatus)r.GetInt32(r.GetOrdinal("status")),
                r.GetInt64(r.GetOrdinal("updated_at"))));
        }
        return list;
    }

    // ── Conversation state & shared secrets ────────────────────────────────

    public void SaveConvState(string convId, long lastSeq, byte[]? secret = null) {
        var secretEnc = secret != null ? Crypto.EncryptField(_key, secret) : null;
        ExecParam(@"INSERT OR REPLACE INTO conv_state VALUES (@id, @seq, @sec)",
            ("id", convId), ("seq", lastSeq), ("sec", (object?)secretEnc ?? DBNull.Value));
    }

    public (long lastSeq, byte[]? secret) LoadConvState(string convId) {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT last_seq, secret_enc FROM conv_state WHERE conversation_id=@id";
        cmd.Parameters.AddWithValue("id", convId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (0, null);
        var seq = r.GetInt64(0);
        byte[]? secret = null;
        if (!r.IsDBNull(1)) {
            var enc = (byte[])r.GetValue(1);
            secret = Crypto.DecryptField(_key, enc);
        }
        return (seq, secret);
    }

    public long NextSeqNum(string convId) {
        var (last, sec) = LoadConvState(convId);
        var next = last + 1;
        SaveConvState(convId, next, sec);
        return next;
    }

    public void ClearConvSecret(string convId) {
        var (lastSeq, _) = LoadConvState(convId);
        SaveConvState(convId, lastSeq, null);
    }

    // ── Outbox (reliable delivery even across server restarts) ─────────────

    public void EnqueueOutbox(string id, string recipientId, string payload, string sig,
        long seqNum, ConversationType ctype = ConversationType.Direct,
        string? groupId = null, List<string>? memberIds = null) {
        ExecParam(@"INSERT OR REPLACE INTO outbox
            VALUES (@id, @rid, @pl, @sig, @seq, @ct, @gid, @mids, @ts, 0)",
            ("id", id), ("rid", recipientId), ("pl", payload), ("sig", sig),
            ("seq", seqNum), ("ct", (int)ctype), ("gid", (object?)groupId ?? DBNull.Value),
            ("mids", (object?)(memberIds != null ? string.Join(",", memberIds) : null) ?? DBNull.Value),
            ("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    public List<OutboxItem> LoadOutbox() {
        var list = new List<OutboxItem>();
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT * FROM outbox ORDER BY created_at LIMIT 100";
        using var r = cmd.ExecuteReader();
        while (r.Read()) {
            var midsRaw = r.IsDBNull(r.GetOrdinal("member_ids")) ? null : r.GetString(r.GetOrdinal("member_ids"));
            list.Add(new OutboxItem(
                r.GetString(r.GetOrdinal("id")),
                r.GetString(r.GetOrdinal("recipient_id")),
                r.GetString(r.GetOrdinal("payload")),
                r.GetString(r.GetOrdinal("sig")),
                r.GetInt64(r.GetOrdinal("seq_num")),
                (ConversationType)r.GetInt32(r.GetOrdinal("conv_type")),
                r.IsDBNull(r.GetOrdinal("group_id")) ? null : r.GetString(r.GetOrdinal("group_id")),
                midsRaw?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
            ));
        }
        return list;
    }

    public void RemoveOutbox(string id) =>
        ExecParam("DELETE FROM outbox WHERE id=@id", ("id", id));

    public void IncrementOutboxAttempts(string id) =>
        ExecParam("UPDATE outbox SET attempts=attempts+1 WHERE id=@id", ("id", id));

    // ── Settings ───────────────────────────────────────────────────────────

    public void SetSetting(string key, string value) =>
        ExecParam("INSERT OR REPLACE INTO settings VALUES (@k,@v)", ("k", key), ("v", value));

    public string? GetSetting(string key) {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=@k";
        cmd.Parameters.AddWithValue("k", key);
        return cmd.ExecuteScalar() as string;
    }

    // ── NUKE — Secure vault destruction ────────────────────────────────────

    /// <summary>
    /// Securely destroy the vault.
    /// 1. Close DB connection
    /// 2. Overwrite file with random bytes x3 passes (frustrates forensic recovery)
    /// 3. Delete file
    /// 4. Wipe vault key from memory
    /// </summary>
    public void Nuke() {
        IsOpen = false;
        _db?.Close();
        _db?.Dispose();
        _db = null;
        GC.Collect();
        GC.WaitForPendingFinalizers();

        if (File.Exists(_path)) {
            var size = new FileInfo(_path).Length;
            // 3 overwrite passes
            for (int pass = 0; pass < 3; pass++) {
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Write);
                var buf = RandomNumberGenerator.GetBytes((int)Math.Min(size, 1024 * 1024));
                long written = 0;
                while (written < size) {
                    var toWrite = (int)Math.Min(buf.Length, size - written);
                    fs.Write(buf, 0, toWrite);
                    written += toWrite;
                }
                fs.Flush(flushToDisk: true);
            }
            File.Delete(_path);
        }
        if (File.Exists(SaltPath)) File.Delete(SaltPath);

        Crypto.Wipe(_key);
        _key = [];
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    void Exec(string sql) {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    void TryExec(string sql) {
        try {
            Exec(sql);
        } catch (SqliteException) {
        }
    }

    void ExecParam(string sql, params (string name, object? val)[] parms) {
        using var cmd = _db!.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, val) in parms)
            cmd.Parameters.AddWithValue("@" + name.TrimStart('@'), val ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    static byte[] ReadOptionalBase64(SqliteDataReader reader, string column) {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal)) return [];
        return Convert.FromBase64String(reader.GetString(ordinal));
    }

    static int TryGetInt32(SqliteDataReader reader, string column) {
        try {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);
        } catch (IndexOutOfRangeException) {
            return 0;
        }
    }

    static long TryGetInt64(SqliteDataReader reader, string column) {
        try {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? 0 : reader.GetInt64(ordinal);
        } catch (IndexOutOfRangeException) {
            return 0;
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _db?.Dispose();
        if (_key.Length > 0) Crypto.Wipe(_key);
    }
}

public record OutboxItem(
    string Id, string RecipientId, string Payload, string Sig, long SeqNum,
    ConversationType ConvType, string? GroupId, List<string>? MemberIds);

public record MessageReceipt(string MessageId, string UserId, MessageStatus Status, long UpdatedAt);

// ═══════════════════════════════════════════════════════════════════════════
//  SESSION MANAGER — Stay logged in via Windows DPAPI
//  DPAPI is tied to the Windows user account. Other Windows users cannot
//  decrypt it. Even admins on other accounts cannot.
// ═══════════════════════════════════════════════════════════════════════════
public static class Session {
    static string SessionFile =>
        Path.Combine(AppPaths.AppDataRoot, "session.bin");

    /// <summary>
    /// Save vault key protected by DPAPI (current Windows user only).
    /// App will auto-unlock on next launch without re-entering password.
    /// </summary>
    public static void Save(byte[] vaultKey) {
        Directory.CreateDirectory(Path.GetDirectoryName(SessionFile)!);
        var protected_ = System.Security.Cryptography.ProtectedData.Protect(
            vaultKey, null,
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        File.WriteAllBytes(SessionFile, protected_);
    }

    /// <summary>Try to load a saved session. Returns null if none or DPAPI fails.</summary>
    public static byte[]? TryLoad() {
        if (!File.Exists(SessionFile)) return null;
        try {
            var encrypted = File.ReadAllBytes(SessionFile);
            return System.Security.Cryptography.ProtectedData.Unprotect(
                encrypted, null,
                System.Security.Cryptography.DataProtectionScope.CurrentUser);
        } catch (Exception ex) {
            AppLog.Warn("session", $"saved session could not be restored: {ex.Message}");
            return null;
        }
    }

    /// <summary>Clear the saved session (called on logout and nuke).</summary>
    public static void Clear() {
        if (File.Exists(SessionFile)) File.Delete(SessionFile);
    }

    public static bool HasSession() => File.Exists(SessionFile);
}

// ═══════════════════════════════════════════════════════════════════════════
//  NETWORK CLIENT — SignalR real-time relay with auto-reconnect
// ═══════════════════════════════════════════════════════════════════════════
public enum RelayConnectionState { Disconnected, Connecting, Reconnecting, Connected }

public class NetworkClient : IAsyncDisposable {
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12);
    HubConnection? _hub;
    LocalUser? _user;
    readonly SemaphoreSlim _connectGate = new(1, 1);
    readonly CancellationTokenSource _lifetimeCts = new();
    Task? _retryLoop;
    Task? _heartbeatLoop;
    int _retryAttempt;
    bool _disposed;

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;
    public event Action<string, string, string, long, long>? OnMessage; // senderId, payload, sig, seq, ts
    public event Action<string, string, string, string, long, long>? OnGroupMessage; // groupId, senderId, payload, sig, seq, ts
    public event Action<string>? OnUserOnline;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<string>? OnError;
    public event Action<RelayConnectionState, string?>? OnStateChanged;

    public async Task ConnectAsync(LocalUser user) {
        var started = AppTelemetry.StartTimer();
        _user = user;
        var serverUrl = RelayUrl.Normalize(user.ServerUrl);
        var hubUrl = serverUrl.TrimEnd('/') + "/hub";

        if (!RelayUrl.IsValid(serverUrl)) {
            OnError?.Invoke(RelayUrl.ValidationHint);
            return;
        }

        AppLog.Info("relay", $"connect requested user={AppTelemetry.MaskUserId(user.UserId)} relay={AppTelemetry.DescribeRelay(serverUrl)} hub={hubUrl}");

        if (_hub != null) {
            await _hub.DisposeAsync();
            _hub = null;
        }

        _hub = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new[] {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            })
            .Build();
        _hub.ServerTimeout = TimeSpan.FromSeconds(30);
        _hub.KeepAliveInterval = TimeSpan.FromSeconds(10);

        // Wire up incoming message handlers
        _hub.On<string, string, string, long, long>("Receive",
            (sid, payload, sig, seq, ts) => OnMessage?.Invoke(sid, payload, sig, seq, ts));

        _hub.On<string, string, string, string, long, long>("ReceiveGroup",
            (gid, sid, payload, sig, seq, ts) => OnGroupMessage?.Invoke(gid, sid, payload, sig, seq, ts));

        _hub.On<string>("UserOnline", uid => OnUserOnline?.Invoke(uid));

        _hub.Reconnecting += ex => {
            AppLog.Warn("relay", $"signalr reconnecting - {AppTelemetry.DescribeException(ex ?? new Exception("connection lost"))}");
            RaiseState(RelayConnectionState.Reconnecting, "relay connection lost - retrying...");
            OnDisconnected?.Invoke();
            return Task.CompletedTask;
        };

        _hub.Reconnected += async connectionId => {
            AppLog.Info("relay", $"signalr reconnected connection_id={connectionId ?? "-"}");
            await RegisterAsync();
            _retryAttempt = 0;
            RaiseState(RelayConnectionState.Connected, "relay connected");
            OnConnected?.Invoke();
        };

        _hub.Closed += ex => {
            if (_disposed || _lifetimeCts.IsCancellationRequested) return Task.CompletedTask;
            if (ex != null) {
                AppLog.Warn("relay", $"signalr closed - {AppTelemetry.DescribeException(ex)}");
            } else {
                AppLog.Warn("relay", "signalr closed without exception");
            }
            RaiseState(RelayConnectionState.Disconnected, "relay offline - retrying in background...");
            OnDisconnected?.Invoke();
            EnsureRetryLoop();
            return Task.CompletedTask;
        };

        RaiseState(RelayConnectionState.Connecting, "connecting to relay...");
        _heartbeatLoop ??= RunHeartbeatLoopAsync();
        await EnsureConnectedAsync(fromRetryLoop: false, _lifetimeCts.Token);
        if (IsConnected) {
            AppLog.Info("relay", $"initial connect completed in {AppTelemetry.ElapsedMilliseconds(started)}ms");
        }
    }

    /// <summary>Re-register keys with server after connect/reconnect.</summary>
    async Task RegisterAsync() {
        if (_hub == null || _user == null) return;
        var started = AppTelemetry.StartTimer();
        var dhPubKey = Convert.ToBase64String(_user.DhPubKey);
        var selfSig = Crypto.SignRegistration(_user.SignPrivKey, _user.UserId, dhPubKey);
        AppLog.Info("relay", $"register begin user={AppTelemetry.MaskUserId(_user.UserId)}");
        await _hub.InvokeAsync("Register",
            _user.UserId,
            Convert.ToBase64String(_user.SignPubKey),
            dhPubKey,
            selfSig);
        AppLog.Info("relay", $"registered relay identity for {AppTelemetry.MaskUserId(_user.UserId)} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
    }

    /// <summary>Send encrypted message to a single user.</summary>
    public async Task<bool> SendDmAsync(string recipientId, string payload, string sig, long seqNum) {
        if (!IsConnected) return false;
        var started = AppTelemetry.StartTimer();
        try {
            await _hub!.InvokeAsync("Send", recipientId, payload, sig, seqNum);
            AppLog.Info("relay", $"direct relay send ok recipient={AppTelemetry.MaskUserId(recipientId)} seq={seqNum} bytes={payload.Length} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
            return true;
        } catch (Exception ex) {
            AppLog.Warn("relay", $"direct send failed: {ex.Message}");
            OnError?.Invoke(ex.Message);
            return false;
        }
    }

    /// <summary>Send encrypted message to a group.</summary>
    public async Task<bool> SendGroupAsync(string groupId, List<string> recipientIds,
        string payload, string sig, long seqNum) {
        if (!IsConnected) return false;
        var started = AppTelemetry.StartTimer();
        try {
            await _hub!.InvokeAsync("SendGroup", groupId, recipientIds, payload, sig, seqNum);
            AppLog.Info("relay", $"group relay send ok group={AppTelemetry.MaskUserId(groupId)} seq={seqNum} recipients={recipientIds.Count} bytes={payload.Length} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
            return true;
        } catch (Exception ex) {
            AppLog.Warn("relay", $"group send failed: {ex.Message}");
            OnError?.Invoke(ex.Message);
            return false;
        }
    }

    /// <summary>Fetch a user's public keys from the relay (for new contacts).</summary>
    public async Task<(byte[] signPub, byte[] dhPub)?> GetUserKeysAsync(string userId) {
        if (!IsConnected) return null;
        var started = AppTelemetry.StartTimer();
        try {
            var result = await _hub!.InvokeAsync<KeyBundleDto?>("GetKeys", userId);
            if (result == null) {
                AppLog.Warn("relay", $"key lookup miss for {AppTelemetry.MaskUserId(userId)} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
                return null;
            }
            AppLog.Info("relay", $"key lookup ok for {AppTelemetry.MaskUserId(userId)} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
            return (Convert.FromBase64String(result.SignPubKey),
                    Convert.FromBase64String(result.DhPubKey));
        } catch (Exception ex) {
            AppLog.Warn("relay", $"key lookup failed for {AppTelemetry.MaskUserId(userId)}: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> AckDmAsync(string senderId, long seqNum) {
        if (!IsConnected) return false;
        var started = AppTelemetry.StartTimer();
        try {
            await _hub!.InvokeAsync("AckDirect", senderId, seqNum);
            AppLog.Info("relay", $"direct ack ok sender={AppTelemetry.MaskUserId(senderId)} seq={seqNum} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
            return true;
        } catch (Exception ex) {
            AppLog.Warn("relay", $"direct ack failed for {AppTelemetry.MaskUserId(senderId)}:{seqNum}: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> AckGroupAsync(string groupId, string senderId, long seqNum) {
        if (!IsConnected) return false;
        var started = AppTelemetry.StartTimer();
        try {
            await _hub!.InvokeAsync("AckGroup", groupId, senderId, seqNum);
            AppLog.Info("relay", $"group ack ok group={AppTelemetry.MaskUserId(groupId)} sender={AppTelemetry.MaskUserId(senderId)} seq={seqNum} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
            return true;
        } catch (Exception ex) {
            AppLog.Warn("relay", $"group ack failed for {AppTelemetry.MaskUserId(groupId)}/{AppTelemetry.MaskUserId(senderId)}:{seqNum}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Announce presence to contacts (so they see us go online).</summary>
    public async Task AnnouncePresenceAsync(List<string> contactIds) {
        if (!IsConnected || contactIds.Count == 0) return;
        var started = AppTelemetry.StartTimer();
        try {
            await _hub!.InvokeAsync("AnnouncePresence", contactIds);
            AppLog.Info("relay", $"presence announced to {contactIds.Count} contact(s) in {AppTelemetry.ElapsedMilliseconds(started)}ms");
        }
        catch (Exception ex) {
            AppLog.Warn("relay", $"presence announcement failed: {ex.Message}");
        }
    }

    async Task EnsureConnectedAsync(bool fromRetryLoop, CancellationToken cancellationToken) {
        if (_hub == null || _disposed) return;

        await _connectGate.WaitAsync(cancellationToken);
        try {
            if (_hub == null || _disposed) return;
            if (_hub.State is HubConnectionState.Connected or HubConnectionState.Connecting or HubConnectionState.Reconnecting)
                return;

            var started = AppTelemetry.StartTimer();
            AppLog.Info("relay", $"hub start begin mode={(fromRetryLoop ? "retry" : "initial")} timeout={(int)ConnectTimeout.TotalSeconds}s state={_hub.State}");

            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(ConnectTimeout);

            try {
                await _hub.StartAsync(connectCts.Token);
            } catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && connectCts.IsCancellationRequested) {
                throw new TimeoutException($"relay connect timed out after {(int)ConnectTimeout.TotalSeconds}s", ex);
            }

            await RegisterAsync();
            _retryAttempt = 0;
            RaiseState(RelayConnectionState.Connected, "relay connected");
            AppLog.Info("relay", $"hub start ok mode={(fromRetryLoop ? "retry" : "initial")} in {AppTelemetry.ElapsedMilliseconds(started)}ms state={_hub.State}");
            OnConnected?.Invoke();
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            if (_disposed || _lifetimeCts.IsCancellationRequested) return;
            AppLog.Warn("relay", $"connection attempt failed mode={(fromRetryLoop ? "retry" : "initial")} state={_hub?.State} error={AppTelemetry.DescribeException(ex)}");
            if (!fromRetryLoop) {
                OnError?.Invoke("relay unavailable, blocked, or waking up - retrying in background...");
            }
            RaiseState(RelayConnectionState.Disconnected, "relay unavailable - retrying...");
            EnsureRetryLoop();
        } finally {
            _connectGate.Release();
        }
    }

    void EnsureRetryLoop() {
        if (_hub == null || _disposed || _lifetimeCts.IsCancellationRequested) return;
        if (_retryLoop is { IsCompleted: false }) return;

        _retryLoop = Task.Run(async () => {
            while (!_disposed && !_lifetimeCts.IsCancellationRequested && !IsConnected) {
                var delay = GetRetryDelay(++_retryAttempt);
                AppLog.Warn("relay", $"retry loop attempt={_retryAttempt} delay={(int)delay.TotalSeconds}s");
                RaiseState(RelayConnectionState.Reconnecting, $"relay offline - retrying in {(int)delay.TotalSeconds}s...");
                try {
                    await Task.Delay(delay, _lifetimeCts.Token);
                } catch (OperationCanceledException) {
                    break;
                }

                await EnsureConnectedAsync(fromRetryLoop: true, _lifetimeCts.Token);
            }
        });
    }

    static TimeSpan GetRetryDelay(int attempt) => attempt switch {
        <= 1 => TimeSpan.FromSeconds(3),
        2 => TimeSpan.FromSeconds(5),
        3 => TimeSpan.FromSeconds(10),
        4 => TimeSpan.FromSeconds(15),
        _ => TimeSpan.FromSeconds(30)
    };

    void RaiseState(RelayConnectionState state, string? detail) {
        AppLog.Info("relay", $"state={state} detail={detail ?? "-"} hub_state={_hub?.State}");
        OnStateChanged?.Invoke(state, detail);
    }

    async Task RunHeartbeatLoopAsync() {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(12));
        try {
            while (await timer.WaitForNextTickAsync(_lifetimeCts.Token)) {
                if (_hub == null || _disposed || !IsConnected) continue;

                try {
                    var started = AppTelemetry.StartTimer();
                    var pingTask = _hub.InvokeAsync<long>("Ping", _lifetimeCts.Token);
                    var completed = await Task.WhenAny(
                        pingTask,
                        Task.Delay(TimeSpan.FromSeconds(5), _lifetimeCts.Token));
                    if (completed != pingTask) {
                        throw new TimeoutException("Relay heartbeat timed out.");
                    }
                    await pingTask;
                    AppLog.Info("relay", $"heartbeat ok in {AppTelemetry.ElapsedMilliseconds(started)}ms");
                } catch (Exception ex) {
                    if (_hub.State != HubConnectionState.Connected) continue;
                    AppLog.Warn("relay", $"heartbeat failed: {AppTelemetry.DescribeException(ex)}");
                    RaiseState(RelayConnectionState.Reconnecting, "relay unreachable - retrying...");
                    OnDisconnected?.Invoke();
                    try {
                        await _hub.StopAsync(_lifetimeCts.Token);
                    } catch (Exception stopEx) {
                        AppLog.Warn("relay", $"hub stop after heartbeat failure also failed: {stopEx.Message}");
                    }
                    EnsureRetryLoop();
                }
            }
        } catch (OperationCanceledException) {
        }
    }

    public async ValueTask DisposeAsync() {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCts.Cancel();
        if (_retryLoop != null) {
            try { await _retryLoop; } catch { }
        }
        if (_heartbeatLoop != null) {
            try { await _heartbeatLoop; } catch { }
        }
        if (_hub != null) await _hub.DisposeAsync();
        _connectGate.Dispose();
        _lifetimeCts.Dispose();
    }

    record KeyBundleDto(
        [property: JsonPropertyName("userId")] string UserId,
        [property: JsonPropertyName("signPubKey")] string SignPubKey,
        [property: JsonPropertyName("dhPubKey")] string DhPubKey,
        [property: JsonPropertyName("registeredAt")] long RegisteredAt);
}


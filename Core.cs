// ═══════════════════════════════════════════════════════════════════════════
//  CIPHER CORE — Crypto · Vault · Network · Session
//  All sensitive operations live here. Zero external crypto libs needed
//  beyond Argon2id (for password hashing without character requirements).
// ═══════════════════════════════════════════════════════════════════════════
using System.IO;
using System.Net.Http;
using System.Net.Security;
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
    public long LastSeen { get; set; }
    public bool IsArchived { get; set; }
    public long ArchivedAt { get; set; }
    public string? ConversationId { get; set; }
    public bool HasPendingKeyChange => PendingSignPubKey.Length > 0 && PendingDhPubKey.Length > 0;
}

public class GroupInfo {
    public string GroupId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> MemberIds { get; set; } = [];
    public byte[] GroupKey { get; set; } = []; // AES-256 group symmetric key
    public string OwnerId { get; set; } = "";
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

// Encrypted wire format (legacy v1)
record WireMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ct")] string Ciphertext,   // Base64 AES-GCM ciphertext
    [property: JsonPropertyName("nonce")] string Nonce,     // Base64 12-byte nonce
    [property: JsonPropertyName("tag")] string Tag,         // Base64 16-byte GCM tag
    [property: JsonPropertyName("seq")] long SeqNum,
    [property: JsonPropertyName("ts")] long Timestamp,
    [property: JsonPropertyName("type")] string Type = "dm" // dm | grp
);

// Encrypted wire format v2 (required).
record WireMessageV2(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("sid")] string SessionId,
    [property: JsonPropertyName("mt")] string MessageType,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("ct")] string Ciphertext,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("seq")] long SeqNum,
    [property: JsonPropertyName("ts")] long Timestamp,
    [property: JsonPropertyName("sent_at")] long SentAt
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

    public static byte[] DeriveRatchetSeed(byte[] sharedSecret, string direction, string conversationId) =>
        HKDF.Expand(HashAlgorithmName.SHA256, sharedSecret, 32,
            Encoding.UTF8.GetBytes($"dm-ratchet:{direction}:{conversationId}"));

    public static byte[] DeriveRatchetMessageKey(byte[] chainKey, long seqNum) =>
        HKDF.Expand(HashAlgorithmName.SHA256, chainKey, 32,
            Encoding.UTF8.GetBytes($"dm-msg:{seqNum}"));

    public static byte[] DeriveRatchetNextChainKey(byte[] chainKey) =>
        HKDF.Expand(HashAlgorithmName.SHA256, chainKey, 32, "dm-next"u8.ToArray());

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

    public static string ComputeGroupAuthToken(byte[] groupKey, string groupId) {
        var info = Encoding.UTF8.GetBytes($"cipher:group-auth:{groupId}");
        using var hmac = new HMACSHA256(groupKey);
        var mac = hmac.ComputeHash(info);
        return Convert.ToBase64String(mac)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    static byte[] PackMessagePlaintext(string content) {
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var payloadLength = 8 + contentBytes.Length;
        var paddedLength = 256;
        while (paddedLength < payloadLength && paddedLength < 4096) {
            paddedLength *= 2;
        }
        if (paddedLength < payloadLength) {
            paddedLength = ((payloadLength + 4095) / 4096) * 4096;
        }
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

    const int WireMessageVersion = 2;

    static bool HasRequiredWireFields(WireMessage? wire, string expectedType) =>
        wire != null &&
        !string.IsNullOrWhiteSpace(wire.Id) &&
        !string.IsNullOrWhiteSpace(wire.Ciphertext) &&
        !string.IsNullOrWhiteSpace(wire.Nonce) &&
        !string.IsNullOrWhiteSpace(wire.Tag) &&
        string.Equals(wire.Type, expectedType, StringComparison.Ordinal);

    static bool HasRequiredWireFieldsV2(WireMessageV2? wire, string expectedType) =>
        wire != null &&
        wire.Version == WireMessageVersion &&
        !string.IsNullOrWhiteSpace(wire.SessionId) &&
        !string.IsNullOrWhiteSpace(wire.Id) &&
        !string.IsNullOrWhiteSpace(wire.Ciphertext) &&
        !string.IsNullOrWhiteSpace(wire.Nonce) &&
        !string.IsNullOrWhiteSpace(wire.Tag) &&
        wire.SentAt > 0 &&
        string.Equals(wire.MessageType, expectedType, StringComparison.Ordinal);

    // ── Message serialization ──────────────────────────────────────────────

    /// <summary>Encrypt a message for a DM conversation. Returns wire payload JSON.</summary>
    public static string EncryptDm(byte[] conversationKey, Message msg) {
        var msgKey = DeriveMessageKey(conversationKey, msg.SeqNum);
        return EncryptDmWithMessageKey(msgKey, msg);
    }

    public static string EncryptDmWithMessageKey(byte[] msgKey, Message msg) {
        var plain = PackMessagePlaintext(msg.Content);
        var (ct, nonce, tag) = Encrypt(msgKey, plain);
        var sessionId = string.IsNullOrWhiteSpace(msg.ConversationId) ? "dm:legacy" : msg.ConversationId;
        var wire = new WireMessageV2(
            WireMessageVersion,
            sessionId,
            "dm",
            msg.Id,
            Convert.ToBase64String(ct),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            msg.SeqNum,
            msg.Timestamp,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return JsonSerializer.Serialize(wire, JsonOpts);
    }

    /// <summary>Decrypt a DM wire payload. Returns null if invalid.</summary>
    public static Message? DecryptDm(byte[] conversationKey, string senderId, string payload, bool isMine = false) {
        try {
            var wireV2 = JsonSerializer.Deserialize<WireMessageV2>(payload, JsonOpts);
            if (!HasRequiredWireFieldsV2(wireV2, "dm")) return null;
            var frame = wireV2!;
            var msgKey = DeriveMessageKey(conversationKey, frame.SeqNum);
            return DecryptDmWithMessageKey(msgKey, senderId, payload, isMine);
        } catch { return null; }
    }

    public static Message? DecryptDmWithMessageKey(byte[] msgKey, string senderId, string payload, bool isMine = false) {
        try {
            var wire = JsonSerializer.Deserialize<WireMessageV2>(payload, JsonOpts);
            if (!HasRequiredWireFieldsV2(wire, "dm")) return null;
            var frame = wire!;
            var plain = Decrypt(msgKey,
                Convert.FromBase64String(frame.Ciphertext),
                Convert.FromBase64String(frame.Nonce),
                Convert.FromBase64String(frame.Tag));
            if (plain == null) return null;
            var content = UnpackMessagePlaintext(plain);
            if (content == null) return null;
            return new Message {
                Id = frame.Id,
                SenderId = senderId,
                Content = content,
                Timestamp = frame.Timestamp,
                SeqNum = frame.SeqNum,
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
        var sessionId = string.IsNullOrWhiteSpace(msg.ConversationId) ? "grp:legacy" : msg.ConversationId;
        var wire = new WireMessageV2(
            WireMessageVersion,
            sessionId,
            "grp",
            msg.Id,
            Convert.ToBase64String(ct),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(tag),
            msg.SeqNum,
            msg.Timestamp,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return JsonSerializer.Serialize(wire, JsonOpts);
    }

    public static Message? DecryptGroup(byte[] groupKey, string groupId, string senderId, string payload, bool isMine = false) {
        try {
            var wire = JsonSerializer.Deserialize<WireMessageV2>(payload, JsonOpts);
            if (!HasRequiredWireFieldsV2(wire, "grp")) return null;
            var frame = wire!;
            if (!string.Equals(frame.SessionId, groupId, StringComparison.Ordinal)) return null;
            var msgKey = DeriveMessageKey(groupKey, frame.SeqNum);
            var plain = Decrypt(msgKey,
                Convert.FromBase64String(frame.Ciphertext),
                Convert.FromBase64String(frame.Nonce),
                Convert.FromBase64String(frame.Tag));
            if (plain == null) return null;
            var content = UnpackMessagePlaintext(plain);
            if (content == null) return null;
            return new Message {
                Id = frame.Id,
                ConversationId = groupId,
                SenderId = senderId,
                Content = content,
                Timestamp = frame.Timestamp,
                SeqNum = frame.SeqNum,
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
    const int MaxSkippedDmKeysPerSender = 256;
    const int MaxSkippedDmAdvanceWindow = 1024;
    SqliteConnection? _db;
    readonly object _gate = new();
    byte[] _key = [];
    GCHandle _keyHandle;
    string _path = "";
    bool _disposed;
    // When true, ReadEncryptedOrLegacyString falls back to the unencrypted legacy
    // column if decryption fails. Enable only during an explicit migration pass,
    // then disable immediately. Default is false (fail closed).
    bool _legacyMigrationMode;

    public static string DefaultVaultPath =>
        Path.Combine(AppPaths.AppDataRoot, "vault.db");

    public static string SaltPath =>
        Path.Combine(AppPaths.AppDataRoot, "vault.salt");

    public bool IsOpen { get; private set; }

    // ── Open / Create ──────────────────────────────────────────────────────

    public void Open(string path, byte[] vaultKey) {
        lock (_gate) {
            _path = path;
            SetKey(vaultKey);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _db = new SqliteConnection($"Data Source={path};Pooling=False;");
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
                WipeAndReleaseKey();
                throw;
            }
        }
    }

    void InitSchema() {
        Exec(@"
            CREATE TABLE IF NOT EXISTS identity (
                id INTEGER PRIMARY KEY,
                user_id TEXT NOT NULL,
                display_name_enc BLOB NOT NULL,
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
                sign_pub_enc BLOB,
                dh_pub_enc BLOB,
                conversation_id_enc BLOB,
                conversation_id_hmac TEXT,
                added_at INTEGER NOT NULL,
                last_seen INTEGER DEFAULT 0,
                is_verified INTEGER NOT NULL DEFAULT 0,
                pending_sign_pub TEXT,
                pending_dh_pub TEXT,
                key_changed_at INTEGER NOT NULL DEFAULT 0,
                is_archived INTEGER NOT NULL DEFAULT 0,
                archived_at INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS groups (
                group_id TEXT PRIMARY KEY,
                name_enc BLOB NOT NULL,
                member_ids TEXT NOT NULL DEFAULT '',
                member_ids_enc BLOB,
                group_key_enc BLOB NOT NULL,
                owner_id TEXT NOT NULL DEFAULT '',
                owner_id_enc BLOB,
                created_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL,
                sender_id TEXT NOT NULL,
                content_enc BLOB NOT NULL,
                timestamp INTEGER NOT NULL,
                conversation_id_enc BLOB,
                conversation_id_hmac TEXT,
                sender_id_enc BLOB,
                timestamp_enc BLOB,
                seq_num INTEGER NOT NULL,
                is_mine INTEGER NOT NULL DEFAULT 0,
                status INTEGER NOT NULL DEFAULT 1,
                conv_type INTEGER NOT NULL DEFAULT 0,
                recipient_id TEXT,
                payload_enc BLOB,
                sig_enc TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_msg_conv ON messages(conversation_id_hmac, id);
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
            CREATE TABLE IF NOT EXISTS incoming_seq_state (
                conversation_id TEXT NOT NULL,
                sender_id TEXT NOT NULL,
                last_seq INTEGER NOT NULL DEFAULT 0,
                chain_key_enc BLOB,
                PRIMARY KEY (conversation_id, sender_id)
            );
            CREATE TABLE IF NOT EXISTS skipped_dm_keys (
                conversation_id TEXT NOT NULL,
                sender_id TEXT NOT NULL,
                seq_num INTEGER NOT NULL,
                message_key_enc BLOB NOT NULL,
                created_at INTEGER NOT NULL,
                PRIMARY KEY (conversation_id, sender_id, seq_num)
            );
            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS outbox (
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
            );
        ");

    }

    // ── Identity ───────────────────────────────────────────────────────────

    public void SaveIdentity(LocalUser u) {
        lock (_gate) {
            var signPrivEnc = Crypto.EncryptField(_key, u.SignPrivKey);
            var dhPrivEnc = Crypto.EncryptField(_key, u.DhPrivKey);
            var displayNameEnc = Crypto.EncryptStr(_key, u.DisplayName);
            ExecParam(@"
                INSERT OR REPLACE INTO identity
                (id, user_id, display_name_enc, sign_priv, sign_pub, dh_priv, dh_pub, server_url, created_at)
                VALUES (1, @uid, @name, @sp, @spub, @dp, @dpub, @srv, @ts)",
                ("uid", u.UserId), ("name", displayNameEnc),
                ("sp", signPrivEnc), ("spub", Convert.ToBase64String(u.SignPubKey)),
                ("dp", dhPrivEnc), ("dpub", Convert.ToBase64String(u.DhPubKey)),
                ("srv", u.ServerUrl), ("ts", u.CreatedAt));
        }
    }

    public LocalUser? LoadIdentity() {
        lock (_gate) {
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = "SELECT * FROM identity WHERE id=1";
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var signPrivEnc = (byte[])r["sign_priv"];
            var dhPrivEnc = (byte[])r["dh_priv"];
            var displayNameEnc = (byte[])r["display_name_enc"];
            var signPriv = Crypto.DecryptField(_key, signPrivEnc);
            var dhPriv = Crypto.DecryptField(_key, dhPrivEnc);
            var displayName = Crypto.DecryptStr(_key, displayNameEnc);
            if (signPriv == null || dhPriv == null || displayName == null) {
                if (signPriv != null) Crypto.Wipe(signPriv);
                if (dhPriv != null) Crypto.Wipe(dhPriv);
                return null;
            }

            return new LocalUser {
                UserId = r.GetString(r.GetOrdinal("user_id")),
                DisplayName = displayName,
                SignPrivKey = signPriv,
                SignPubKey = Convert.FromBase64String(r.GetString(r.GetOrdinal("sign_pub"))),
                DhPrivKey = dhPriv,
                DhPubKey = Convert.FromBase64String(r.GetString(r.GetOrdinal("dh_pub"))),
                ServerUrl = r.GetString(r.GetOrdinal("server_url")),
                CreatedAt = r.GetInt64(r.GetOrdinal("created_at"))
            };
        }
    }

    public bool HasIdentity() {
        lock (_gate) {
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM identity";
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
    }

    // ── Contacts ───────────────────────────────────────────────────────────

    public void SaveContact(Contact c) {
        lock (_gate) {
            var conversationId = c.ConversationId ?? "";
            ExecParam(@"
                INSERT OR REPLACE INTO contacts
                (user_id, display_name_enc, sign_pub, dh_pub, conversation_id, sign_pub_enc, dh_pub_enc, conversation_id_enc, conversation_id_hmac, added_at, last_seen, is_verified, pending_sign_pub, pending_dh_pub, key_changed_at, is_archived, archived_at)
                VALUES (@uid, @name, '', '', '', @spEnc, @dpEnc, @convEnc, @convHmac, @ts, @ls, @verified, @psp, @pdp, @changed, @archived, @archivedAt)",
                ("uid", c.UserId),
                ("name", Crypto.EncryptStr(_key, c.DisplayName)),
                ("spEnc", Crypto.EncryptStr(_key, Convert.ToBase64String(c.SignPubKey))),
                ("dpEnc", Crypto.EncryptStr(_key, Convert.ToBase64String(c.DhPubKey))),
                ("convEnc", Crypto.EncryptStr(_key, conversationId)),
                ("convHmac", ComputeMetadataIndex("contact-conversation", conversationId)),
                ("ts", c.AddedAt),
                ("ls", c.LastSeen),
                ("verified", c.IsVerified ? 1 : 0),
                ("psp", c.PendingSignPubKey.Length > 0 ? Convert.ToBase64String(c.PendingSignPubKey) : DBNull.Value),
                ("pdp", c.PendingDhPubKey.Length > 0 ? Convert.ToBase64String(c.PendingDhPubKey) : DBNull.Value),
                ("changed", c.KeyChangedAt),
                ("archived", c.IsArchived ? 1 : 0),
                ("archivedAt", c.ArchivedAt));
        }
    }

    public List<Contact> LoadContacts() {
        lock (_gate) {
            var list = new List<Contact>();
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = "SELECT * FROM contacts ORDER BY added_at";
            using var r = cmd.ExecuteReader();
            while (r.Read()) {
                var nameEnc = (byte[])r["display_name_enc"];
                var signPubBase64 = ReadEncryptedOrLegacyString(r, "sign_pub_enc", "sign_pub", $"contact:{TryReadString(r, "user_id")}");
                var dhPubBase64 = ReadEncryptedOrLegacyString(r, "dh_pub_enc", "dh_pub", $"contact:{TryReadString(r, "user_id")}");
                var conversationId = ReadEncryptedOrLegacyString(r, "conversation_id_enc", "conversation_id", $"contact:{TryReadString(r, "user_id")}");
                list.Add(new Contact {
                    UserId = r.GetString(r.GetOrdinal("user_id")),
                    DisplayName = Crypto.DecryptStr(_key, nameEnc) ?? "???",
                    SignPubKey = Convert.FromBase64String(signPubBase64),
                    DhPubKey = Convert.FromBase64String(dhPubBase64),
                    PendingSignPubKey = ReadOptionalBase64(r, "pending_sign_pub"),
                    PendingDhPubKey = ReadOptionalBase64(r, "pending_dh_pub"),
                    IsVerified = TryGetInt32(r, "is_verified") == 1,
                    KeyChangedAt = TryGetInt64(r, "key_changed_at"),
                    IsArchived = TryGetInt32(r, "is_archived") == 1,
                    ArchivedAt = TryGetInt64(r, "archived_at"),
                    ConversationId = conversationId,
                    AddedAt = r.GetInt64(r.GetOrdinal("added_at")),
                    LastSeen = TryGetInt64(r, "last_seen")
                });
            }
            return list;
        }
    }

    public void UpdateContactSeen(string userId) =>
        ExecParam("UPDATE contacts SET last_seen=@ts WHERE user_id=@uid",
            ("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), ("uid", userId));

    public void SetContactArchived(string userId, bool isArchived) =>
        ExecParam(
            "UPDATE contacts SET is_archived=@archived, archived_at=@archived_at WHERE user_id=@uid",
            ("archived", isArchived ? 1 : 0),
            ("archived_at", isArchived ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : 0),
            ("uid", userId));

    // ── Groups ─────────────────────────────────────────────────────────────

    public void SaveGroup(GroupInfo g) {
        lock (_gate) {
            var memberIds = string.Join(",", g.MemberIds);
            ExecParam(@"
                INSERT OR REPLACE INTO groups
                (group_id, name_enc, member_ids, member_ids_enc, group_key_enc, owner_id, owner_id_enc, created_at)
                VALUES (@id, @name, @members, @membersEnc, @key, @owner, @ownerEnc, @ts)",
                ("id", g.GroupId),
                ("name", Crypto.EncryptStr(_key, g.Name)),
                ("members", ""),
                ("membersEnc", Crypto.EncryptStr(_key, memberIds)),
                ("key", Crypto.EncryptField(_key, g.GroupKey)),
                ("owner", ""),
                ("ownerEnc", string.IsNullOrWhiteSpace(g.OwnerId) ? DBNull.Value : Crypto.EncryptStr(_key, g.OwnerId)),
                ("ts", g.CreatedAt));
        }
    }

    public List<GroupInfo> LoadGroups() {
        lock (_gate) {
            var list = new List<GroupInfo>();
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = "SELECT * FROM groups";
            using var r = cmd.ExecuteReader();
            while (r.Read()) {
                var groupId = r.GetString(r.GetOrdinal("group_id"));
                var keyEnc = (byte[])r["group_key_enc"];
                var groupKey = Crypto.DecryptField(_key, keyEnc);
                if (groupKey == null) {
                    AppLog.Warn("vault", $"skipping group with unreadable key: {AppTelemetry.MaskUserId(groupId)}");
                    continue;
                }
                var memberIds = ReadEncryptedOrLegacyCsv(r, "member_ids_enc", "member_ids", $"group:{AppTelemetry.MaskUserId(groupId)}");
                var ownerId = ReadEncryptedOrLegacyString(r, "owner_id_enc", "owner_id", $"group:{AppTelemetry.MaskUserId(groupId)}");
                list.Add(new GroupInfo {
                    GroupId = groupId,
                    Name = Crypto.DecryptStr(_key, (byte[])r["name_enc"]) ?? "???",
                    MemberIds = memberIds.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    GroupKey = groupKey,
                    OwnerId = ownerId,
                    CreatedAt = r.GetInt64(r.GetOrdinal("created_at"))
                });
            }
            return list;
        }
    }

    public void DeleteGroupConversation(string groupId) {
        lock (_gate) {
            var groupHmac = ComputeMetadataIndex("message-conversation", groupId);
            using var tx = _db!.BeginTransaction(deferred: false);
            ExecParamTx(tx, @"
                DELETE FROM message_receipts
                WHERE message_id IN (
                    SELECT id FROM messages WHERE conversation_id_hmac=@gid
                )",
                ("gid", groupHmac));
            ExecParamTx(tx, "DELETE FROM messages WHERE conversation_id_hmac=@gid", ("gid", groupHmac));
            ExecParamTx(tx, "DELETE FROM conv_state WHERE conversation_id=@gid", ("gid", groupId));
            ExecParamTx(tx, "DELETE FROM outbox WHERE conv_type=@ctype AND group_id_hmac=@gid",
                ("ctype", (int)ConversationType.Group), ("gid", ComputeMetadataIndex("outbox-group", groupId)));
            ExecParamTx(tx, "DELETE FROM groups WHERE group_id=@gid", ("gid", groupId));
            tx.Commit();
        }
    }

    // ── Messages ───────────────────────────────────────────────────────────

    public void SaveMessage(Message msg) {
        lock (_gate) {
            var contentEnc = Crypto.EncryptStr(_key, msg.Content);
            var convId = msg.ConversationId;
            var senderId = msg.SenderId;
            ExecParam(@"INSERT OR REPLACE INTO messages
                (id, conversation_id, sender_id, content_enc, timestamp, conversation_id_enc, conversation_id_hmac, sender_id_enc, timestamp_enc,
                 seq_num, is_mine, status, conv_type, recipient_id)
                VALUES (@id,'','',@ct,0,@convEnc,@convHmac,@sidEnc,@tsEnc,@seq,@mine,@st,@ctype,@rid)",
                ("id", msg.Id),
                ("convEnc", Crypto.EncryptStr(_key, convId)),
                ("convHmac", ComputeMetadataIndex("message-conversation", convId)),
                ("sidEnc", Crypto.EncryptStr(_key, senderId)),
                ("ct", contentEnc),
                ("tsEnc", Crypto.EncryptField(_key, BitConverter.GetBytes(msg.Timestamp))),
                ("seq", msg.SeqNum),
                ("mine", msg.IsMine ? 1 : 0), ("st", (int)msg.Status),
                ("ctype", (int)msg.ConvType), ("rid", (object?)msg.RecipientId ?? DBNull.Value));
        }
    }

    /// <summary>
    /// Load messages in pages for lazy rendering.
    /// offset=0 gives the newest 'limit' messages.
    /// Incrementing offset loads older history.
    /// </summary>
    public List<Message> LoadMessages(string conversationId, int offset, int limit = 50) {
        lock (_gate) {
            var list = new List<Message>();
            var convHmac = ComputeMetadataIndex("message-conversation", conversationId);
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = @"
                SELECT * FROM messages
                WHERE conversation_id_hmac=@conv
                ORDER BY rowid DESC
                LIMIT @limit OFFSET @offset";
            cmd.Parameters.AddWithValue("conv", convHmac);
            cmd.Parameters.AddWithValue("limit", limit);
            cmd.Parameters.AddWithValue("offset", offset);
            using var r = cmd.ExecuteReader();
            while (r.Read()) {
                var ctEnc = (byte[])r["content_enc"];
                var content = Crypto.DecryptStr(_key, ctEnc) ?? "[decrypt failed]";
                var convId = ReadEncryptedOrLegacyString(r, "conversation_id_enc", "conversation_id", $"msg:{TryReadString(r, "id")}");
                var senderId = ReadEncryptedOrLegacyString(r, "sender_id_enc", "sender_id", $"msg:{TryReadString(r, "id")}");
                long timestamp;
                if (!r.IsDBNull(r.GetOrdinal("timestamp_enc"))) {
                    var tsBytes = Crypto.DecryptField(_key, (byte[])r["timestamp_enc"]);
                    timestamp = tsBytes != null && tsBytes.Length == sizeof(long)
                        ? BitConverter.ToInt64(tsBytes, 0)
                        : 0;
                } else {
                    timestamp = r.GetInt64(r.GetOrdinal("timestamp"));
                }
                list.Add(new Message {
                    Id = r.GetString(r.GetOrdinal("id")),
                    ConversationId = convId,
                    SenderId = senderId,
                    Content = content,
                    Timestamp = timestamp,
                    SeqNum = r.GetInt64(r.GetOrdinal("seq_num")),
                    IsMine = r.GetInt32(r.GetOrdinal("is_mine")) == 1,
                    Status = (MessageStatus)r.GetInt32(r.GetOrdinal("status")),
                    ConvType = (ConversationType)r.GetInt32(r.GetOrdinal("conv_type"))
                });
            }
            list.Reverse(); // Return in chronological order
            return list;
        }
    }

    public int GetMessageCount(string conversationId) {
        lock (_gate) {
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE conversation_id_hmac=@conv";
            cmd.Parameters.AddWithValue("conv", ComputeMetadataIndex("message-conversation", conversationId));
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
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
        lock (_gate) {
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE id=@id";
            cmd.Parameters.AddWithValue("id", messageId);
            return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
        }
    }

    public (string conversationId, ConversationType convType, bool isMine)? GetMessageMetadata(string messageId) {
        lock (_gate) {
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = """
                SELECT conversation_id_enc, conversation_id, conv_type, is_mine
                FROM messages
                WHERE id=@id
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("id", messageId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var conversationId = ReadEncryptedOrLegacyString(r, "conversation_id_enc", "conversation_id", $"msg:{messageId}");
            return (
                conversationId,
                (ConversationType)r.GetInt32(r.GetOrdinal("conv_type")),
                r.GetInt32(r.GetOrdinal("is_mine")) == 1
            );
        }
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
        lock (_gate) {
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
    }

    // ── Conversation state & shared secrets ────────────────────────────────

    public void SaveConvState(string convId, long lastSeq, byte[]? secret = null) {
        lock (_gate) {
            SaveConvStateCore(null, convId, lastSeq, secret);
        }
    }

    void SaveConvStateCore(SqliteTransaction? transaction, string convId, long lastSeq, byte[]? secret) {
        var secretEnc = secret != null ? Crypto.EncryptField(_key, secret) : null;
        using var cmd = _db!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"INSERT OR REPLACE INTO conv_state VALUES (@id, @seq, @sec)";
        cmd.Parameters.AddWithValue("@id", convId);
        cmd.Parameters.AddWithValue("@seq", lastSeq);
        cmd.Parameters.AddWithValue("@sec", (object?)secretEnc ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public (long lastSeq, byte[]? secret) LoadConvState(string convId) {
        lock (_gate) {
            return LoadConvStateCore(null, convId);
        }
    }

    (long lastSeq, byte[]? secret) LoadConvStateCore(SqliteTransaction? transaction, string convId) {
        using var cmd = _db!.CreateCommand();
        cmd.Transaction = transaction;
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

    public Dictionary<string, long> LoadIncomingSeqState(string convId) {
        lock (_gate) {
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = """
                SELECT sender_id, last_seq
                FROM incoming_seq_state
                WHERE conversation_id=@id
                """;
            cmd.Parameters.AddWithValue("@id", convId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) {
                result[reader.GetString(0)] = reader.GetInt64(1);
            }
            return result;
        }
    }

    public void SaveIncomingSeqState(string convId, string senderId, long lastSeq) =>
        ExecParam("""
            INSERT INTO incoming_seq_state(conversation_id, sender_id, last_seq)
            VALUES (@conv, @sender, @seq)
            ON CONFLICT(conversation_id, sender_id) DO UPDATE SET
                last_seq = CASE
                    WHEN excluded.last_seq > incoming_seq_state.last_seq THEN excluded.last_seq
                    ELSE incoming_seq_state.last_seq
                END
            """,
            ("conv", convId),
            ("sender", senderId),
            ("seq", lastSeq));

    public (long seq, byte[] messageKey)? AllocateOutgoingDmMessageKey(string convId, byte[] sharedSecret) {
        lock (_gate) {
            using var tx = _db!.BeginTransaction(deferred: false);
            var (lastSeq, chainKey) = LoadConvStateCore(tx, convId);
            chainKey ??= Crypto.DeriveRatchetSeed(sharedSecret, "send", convId);
            var nextSeq = lastSeq + 1;
            var messageKey = Crypto.DeriveRatchetMessageKey(chainKey, nextSeq);
            var nextChain = Crypto.DeriveRatchetNextChainKey(chainKey);
            SaveConvStateCore(tx, convId, nextSeq, nextChain);
            tx.Commit();
            Crypto.Wipe(chainKey);
            return (nextSeq, messageKey);
        }
    }

    public byte[]? ConsumeIncomingDmMessageKey(string convId, string senderId, long seq, byte[] sharedSecret) {
        lock (_gate) {
            var skipped = TakeSkippedDmMessageKeyCore(null, convId, senderId, seq);
            if (skipped != null) {
                return skipped;
            }

            using var tx = _db!.BeginTransaction(deferred: false);
            var (lastSeq, chainKey) = LoadIncomingChainStateCore(tx, convId, senderId);
            if (seq <= lastSeq) {
                if (chainKey != null) Crypto.Wipe(chainKey);
                return null;
            }
            if (seq - lastSeq > MaxSkippedDmAdvanceWindow) {
                if (chainKey != null) Crypto.Wipe(chainKey);
                return null;
            }

            chainKey ??= Crypto.DeriveRatchetSeed(sharedSecret, $"recv:{senderId}", convId);
            byte[]? messageKey = null;
            for (var step = lastSeq + 1; step <= seq; step++) {
                var stepKey = Crypto.DeriveRatchetMessageKey(chainKey, step);
                var nextChain = Crypto.DeriveRatchetNextChainKey(chainKey);
                Crypto.Wipe(chainKey);
                chainKey = nextChain;
                if (step == seq) {
                    messageKey = stepKey;
                } else {
                    SaveSkippedDmMessageKeyCore(tx, convId, senderId, step, stepKey);
                    Crypto.Wipe(stepKey);
                }
            }

            SaveIncomingChainStateCore(tx, convId, senderId, seq, chainKey);
            tx.Commit();
            Crypto.Wipe(chainKey);
            return messageKey;
        }
    }

    void SaveSkippedDmMessageKeyCore(SqliteTransaction? transaction, string convId, string senderId, long seq, byte[] messageKey) {
        var keyEnc = Crypto.EncryptField(_key, messageKey);
        using var insert = _db!.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR REPLACE INTO skipped_dm_keys(conversation_id, sender_id, seq_num, message_key_enc, created_at)
            VALUES (@conv, @sender, @seq, @key, @created)
            """;
        insert.Parameters.AddWithValue("@conv", convId);
        insert.Parameters.AddWithValue("@sender", senderId);
        insert.Parameters.AddWithValue("@seq", seq);
        insert.Parameters.AddWithValue("@key", keyEnc);
        insert.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        insert.ExecuteNonQuery();

        using var prune = _db!.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = """
            DELETE FROM skipped_dm_keys
            WHERE conversation_id=@conv
              AND sender_id=@sender
              AND rowid IN (
                SELECT rowid
                FROM skipped_dm_keys
                WHERE conversation_id=@conv AND sender_id=@sender
                ORDER BY seq_num ASC
                LIMIT (
                  SELECT CASE
                    WHEN COUNT(*) > @max THEN COUNT(*) - @max
                    ELSE 0
                  END
                  FROM skipped_dm_keys
                  WHERE conversation_id=@conv AND sender_id=@sender
                )
              )
            """;
        prune.Parameters.AddWithValue("@conv", convId);
        prune.Parameters.AddWithValue("@sender", senderId);
        prune.Parameters.AddWithValue("@max", MaxSkippedDmKeysPerSender);
        prune.ExecuteNonQuery();
    }

    byte[]? TakeSkippedDmMessageKeyCore(SqliteTransaction? transaction, string convId, string senderId, long seq) {
        using var cmd = _db!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT message_key_enc
            FROM skipped_dm_keys
            WHERE conversation_id=@conv AND sender_id=@sender AND seq_num=@seq
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@conv", convId);
        cmd.Parameters.AddWithValue("@sender", senderId);
        cmd.Parameters.AddWithValue("@seq", seq);
        var enc = cmd.ExecuteScalar() as byte[];
        if (enc == null) return null;

        using var delete = _db!.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM skipped_dm_keys
            WHERE conversation_id=@conv AND sender_id=@sender AND seq_num=@seq
            """;
        delete.Parameters.AddWithValue("@conv", convId);
        delete.Parameters.AddWithValue("@sender", senderId);
        delete.Parameters.AddWithValue("@seq", seq);
        delete.ExecuteNonQuery();
        return Crypto.DecryptField(_key, enc);
    }

    public long NextSeqNum(string convId) {
        lock (_gate) {
            using var tx = _db!.BeginTransaction(deferred: false);
            var (last, secret) = LoadConvStateCore(tx, convId);
            var next = last + 1;
            SaveConvStateCore(tx, convId, next, secret);
            tx.Commit();
            if (secret != null) Crypto.Wipe(secret);
            return next;
        }
    }

    public void ClearConvSecret(string convId) {
        lock (_gate) {
            var (lastSeq, secret) = LoadConvStateCore(null, convId);
            if (secret != null) Crypto.Wipe(secret);
            SaveConvStateCore(null, convId, lastSeq, null);
        }
    }

    (long lastSeq, byte[]? chainKey) LoadIncomingChainStateCore(SqliteTransaction? transaction, string convId, string senderId) {
        using var cmd = _db!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT last_seq, chain_key_enc
            FROM incoming_seq_state
            WHERE conversation_id=@conv AND sender_id=@sender
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@conv", convId);
        cmd.Parameters.AddWithValue("@sender", senderId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (0, null);

        var lastSeq = reader.GetInt64(0);
        byte[]? chainKey = null;
        if (!reader.IsDBNull(1)) {
            chainKey = Crypto.DecryptField(_key, (byte[])reader.GetValue(1));
        }
        return (lastSeq, chainKey);
    }

    void SaveIncomingChainStateCore(SqliteTransaction? transaction, string convId, string senderId, long lastSeq, byte[] chainKey) {
        var chainEnc = Crypto.EncryptField(_key, chainKey);
        using var cmd = _db!.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO incoming_seq_state(conversation_id, sender_id, last_seq, chain_key_enc)
            VALUES (@conv, @sender, @seq, @chain)
            ON CONFLICT(conversation_id, sender_id) DO UPDATE SET
                last_seq = CASE
                    WHEN excluded.last_seq > incoming_seq_state.last_seq THEN excluded.last_seq
                    ELSE incoming_seq_state.last_seq
                END,
                chain_key_enc = CASE
                    WHEN excluded.last_seq > incoming_seq_state.last_seq THEN excluded.chain_key_enc
                    ELSE incoming_seq_state.chain_key_enc
                END
            """;
        cmd.Parameters.AddWithValue("@conv", convId);
        cmd.Parameters.AddWithValue("@sender", senderId);
        cmd.Parameters.AddWithValue("@seq", lastSeq);
        cmd.Parameters.AddWithValue("@chain", chainEnc);
        cmd.ExecuteNonQuery();
    }

    // ── Outbox (reliable delivery even across server restarts) ─────────────

    public void EnqueueOutbox(string id, string recipientId, string payload, string sig,
        long seqNum, ConversationType ctype = ConversationType.Direct,
        string? groupId = null, List<string>? memberIds = null) {
        lock (_gate) {
            var recipientIdEnc = Crypto.EncryptStr(_key, recipientId);
            var payloadEnc = Crypto.EncryptStr(_key, payload);
            var sigEnc = Crypto.EncryptStr(_key, sig);
            var memberIdsEnc = memberIds != null
                ? Crypto.EncryptStr(_key, string.Join(",", memberIds))
                : null;

            ExecParam(@"
                INSERT OR REPLACE INTO outbox
                (id, recipient_id_enc, payload_enc, sig_enc, seq_num, conv_type, group_id, group_id_enc, group_id_hmac, member_ids_enc, created_at, attempts)
                VALUES (@id, @rid, @pl, @sig, @seq, @ct, '', @gidEnc, @gidHmac, @mids, @ts, 0)",
                ("id", id),
                ("rid", recipientIdEnc),
                ("pl", payloadEnc),
                ("sig", sigEnc),
                ("seq", seqNum),
                ("ct", (int)ctype),
                ("gidEnc", groupId != null ? Crypto.EncryptStr(_key, groupId) : DBNull.Value),
                ("gidHmac", groupId != null ? ComputeMetadataIndex("outbox-group", groupId) : DBNull.Value),
                ("mids", (object?)memberIdsEnc ?? DBNull.Value),
                ("ts", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
    }

    public List<OutboxItem> LoadOutbox() {
        lock (_gate) {
            var list = new List<OutboxItem>();
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = "SELECT * FROM outbox WHERE attempts < 100 ORDER BY created_at LIMIT 100";
            using var r = cmd.ExecuteReader();
            while (r.Read()) {
                var id = r.GetString(r.GetOrdinal("id"));
                var recipientId = Crypto.DecryptStr(_key, (byte[])r["recipient_id_enc"]);
                var payload = Crypto.DecryptStr(_key, (byte[])r["payload_enc"]);
                var sig = Crypto.DecryptStr(_key, (byte[])r["sig_enc"]);
                string? midsRaw = null;
                if (!r.IsDBNull(r.GetOrdinal("member_ids_enc"))) {
                    midsRaw = Crypto.DecryptStr(_key, (byte[])r["member_ids_enc"]);
                }

                if (recipientId == null || payload == null || sig == null ||
                    (!r.IsDBNull(r.GetOrdinal("member_ids_enc")) && midsRaw == null)) {
                    AppLog.Warn("outbox", $"skipping undecryptable queued item {AppTelemetry.MaskUserId(id)}");
                    continue;
                }

                list.Add(new OutboxItem(
                    id,
                    recipientId,
                    payload,
                    sig,
                    r.GetInt64(r.GetOrdinal("seq_num")),
                    (ConversationType)r.GetInt32(r.GetOrdinal("conv_type")),
                    NormalizeOptional(ReadEncryptedOrLegacyString(r, "group_id_enc", "group_id", $"outbox:{id}")),
                    midsRaw?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                ));
            }
            return list;
        }
    }

    public void RemoveOutbox(string id) =>
        ExecParam("DELETE FROM outbox WHERE id=@id", ("id", id));

    public void IncrementOutboxAttempts(string id) =>
        ExecParam("UPDATE outbox SET attempts=attempts+1 WHERE id=@id", ("id", id));

    // ── Settings ───────────────────────────────────────────────────────────

    public void SetSetting(string key, string value) {
        var enc = Crypto.EncryptStr(_key, value);
        var packed = Convert.ToBase64String(enc);
        ExecParam("INSERT OR REPLACE INTO settings VALUES (@k,@v)", ("k", key), ("v", packed));
    }

    public string? GetSetting(string key) {
        lock (_gate) {
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key=@k";
            cmd.Parameters.AddWithValue("k", key);
            var raw = cmd.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(raw)) return raw;
            try {
                var enc = Convert.FromBase64String(raw);
                return Crypto.DecryptStr(_key, enc);
            } catch {
                // Legacy plaintext fallback. Callers may rewrite to encrypted on next save.
                return raw;
            }
        }
    }

    // ── NUKE — Secure vault destruction ────────────────────────────────────

    /// <summary>
    /// Best-effort vault destruction.
    /// 1. Close DB connection
    /// 2. Overwrite SQLite files with random bytes x3 passes where possible
    /// 3. Delete file
    /// 4. Wipe vault key from memory
    ///
    /// NOTE ON SSDs: 3-pass overwrite is effective on HDDs and many SSD/NVMe
    /// configurations, but SSDs with wear-leveling may retain data in unmapped
    /// blocks that the OS cannot address — this is a hardware-level limitation.
    /// Full-disk encryption (BitLocker / FileVault) is the only reliable
    /// mitigation on SSDs, because the plaintext data is never written to disk
    /// unencrypted in the first place. This method remains worthwhile as
    /// defense-in-depth and handles the HDD / non-encrypted-drive case correctly.
    /// </summary>
    public void Nuke() {
        lock (_gate) {
            IsOpen = false;
            _db?.Close();
            _db?.Dispose();
            _db = null;
            WipeAndReleaseKey();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();

        lock (_gate) {
            BestEffortOverwriteAndDelete(_path);
            BestEffortOverwriteAndDelete(_path + "-wal");
            BestEffortOverwriteAndDelete(_path + "-shm");
            DeleteIfExists(SaltPath);
        }
    }

    static void BestEffortOverwriteAndDelete(string path) {
        if (!File.Exists(path)) return;

        for (var attempt = 0; attempt < 5; attempt++) {
            try {
                var size = new FileInfo(path).Length;
                if (size > 0) {
                    for (int pass = 0; pass < 3; pass++) {
                        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write,
                            FileShare.ReadWrite | FileShare.Delete);
                        var buf = RandomNumberGenerator.GetBytes((int)Math.Min(size, 1024 * 1024));
                        long written = 0;
                        while (written < size) {
                            var toWrite = (int)Math.Min(buf.Length, size - written);
                            fs.Write(buf, 0, toWrite);
                            written += toWrite;
                        }
                        fs.Flush(flushToDisk: true);
                    }
                }

                File.Delete(path);
                return;
            } catch (IOException) {
                if (attempt == 4) break;
                Thread.Sleep(50);
            } catch (UnauthorizedAccessException) {
                if (attempt == 4) break;
                Thread.Sleep(50);
            }
        }

        DeleteIfExists(path);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    void Exec(string sql) {
        lock (_gate) {
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    void TryExec(string sql) {
        try {
            Exec(sql);
        } catch (SqliteException) {
        }
    }

    void ExecParam(string sql, params (string name, object? val)[] parms) {
        lock (_gate) {
            using var cmd = _db!.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, val) in parms)
                cmd.Parameters.AddWithValue("@" + name.TrimStart('@'), val ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
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

    static string TryReadString(SqliteDataReader reader, string column) {
        try {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
        } catch {
            return "";
        }
    }

    static string TryGetString(SqliteDataReader reader, string column) {
        try {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? "" : reader.GetString(ordinal);
        } catch (IndexOutOfRangeException) {
            return "";
        }
    }

    /// <summary>
    /// Enable legacy plaintext fallback during a one-time migration pass.
    /// Call EnableLegacyMigration(), run migration, then call DisableLegacyMigration().
    /// Never leave this enabled during normal operation.
    /// </summary>
    public void EnableLegacyMigration() { lock (_gate) { _legacyMigrationMode = true; } }

    /// <summary>Disable legacy plaintext fallback. Call after migration is complete.</summary>
    public void DisableLegacyMigration() { lock (_gate) { _legacyMigrationMode = false; } }

    string ReadEncryptedOrLegacyString(SqliteDataReader reader, string encryptedColumn, string legacyColumn, string? recordId = null) {
        try {
            var encOrdinal = reader.GetOrdinal(encryptedColumn);
            if (!reader.IsDBNull(encOrdinal)) {
                var decrypted = Crypto.DecryptStr(_key, (byte[])reader[encryptedColumn]);
                if (decrypted != null) return decrypted;
                AppLog.Warn("vault", $"failed to decrypt {encryptedColumn} for {recordId ?? "record"}");
            }
        } catch (IndexOutOfRangeException) {
        } catch (InvalidCastException ex) {
            AppLog.Warn("vault", $"invalid storage type for {encryptedColumn} on {recordId ?? "record"}: {ex.Message}");
        }

        if (_legacyMigrationMode) {
            AppLog.Warn("vault", $"[migration] falling back to legacy plaintext for {encryptedColumn} on {recordId ?? "record"}");
            return TryGetString(reader, legacyColumn);
        }

        // Fail closed: return empty string rather than silently serving unencrypted data.
        AppLog.Error("vault", $"decryption failed for {encryptedColumn} on {recordId ?? "record"} — returning empty (legacy migration is disabled)");
        return "";
    }

    string ReadEncryptedOrLegacyCsv(SqliteDataReader reader, string encryptedColumn, string legacyColumn, string? recordId = null) =>
        ReadEncryptedOrLegacyString(reader, encryptedColumn, legacyColumn, recordId);

    static string? NormalizeOptional(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    string ComputeMetadataIndex(string scope, string value) {
        using var hmac = new HMACSHA256(_key);
        var bytes = Encoding.UTF8.GetBytes($"{scope}:{value}");
        var mac = hmac.ComputeHash(bytes);
        return Convert.ToBase64String(mac)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) return;
            _disposed = true;
            _db?.Dispose();
            WipeAndReleaseKey();
        }
    }

    void SetKey(byte[] key) {
        WipeAndReleaseKey();
        _key = key.ToArray();
        _keyHandle = GCHandle.Alloc(_key, GCHandleType.Pinned);
    }

    void WipeAndReleaseKey() {
        if (_key.Length > 0) {
            Crypto.Wipe(_key);
        }
        if (_keyHandle.IsAllocated) {
            _keyHandle.Free();
        }
        _key = [];
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
            vaultKey, GetDpapiEntropy(),
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        File.WriteAllBytes(SessionFile, protected_);
    }

    /// <summary>Try to load a saved session. Returns null if none or DPAPI fails.</summary>
    public static byte[]? TryLoad() {
        if (!File.Exists(SessionFile)) return null;
        try {
            var encrypted = File.ReadAllBytes(SessionFile);
            var entropy = GetDpapiEntropy();
            try {
                // Normal path: session was saved with app-bound entropy.
                return System.Security.Cryptography.ProtectedData.Unprotect(
                    encrypted, entropy,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
            } catch (System.Security.Cryptography.CryptographicException) {
                // Migration path: session was saved with the old null-entropy scheme.
                // Decrypt with null, then immediately re-save with the new entropy so
                // this migration only happens once. No password prompt required.
                AppLog.Info("session", "migrating session to app-bound DPAPI entropy");
                var key = System.Security.Cryptography.ProtectedData.Unprotect(
                    encrypted, null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                Save(key);
                return key;
            }
        } catch (Exception ex) {
            AppLog.Warn("session", $"saved session could not be restored: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Application-specific entropy for DPAPI. Binds the session blob to this app
    /// so other processes running as the same Windows user cannot trivially decrypt it.
    /// Derived from the assembly identity — stable across updates.
    /// </summary>
    static byte[] GetDpapiEntropy() {
        var token = typeof(Session).Assembly.GetName().GetPublicKeyToken();
        var tag = "cipher:session:v1"u8.ToArray();
        if (token is { Length: > 0 }) {
            var combined = new byte[tag.Length + token.Length];
            tag.CopyTo(combined, 0);
            token.CopyTo(combined, tag.Length);
            return SHA256.HashData(combined);
        }

        // Unsigned builds fallback: bind entropy to this machine/user/executable path.
        // This is still not as strong as signed-assembly binding, but avoids a global constant.
        var machine = Environment.MachineName;
        var user = Environment.UserName;
        var exePath = AppContext.BaseDirectory;
        var fallback = Encoding.UTF8.GetBytes($"{machine}|{user}|{exePath}");
        var all = new byte[tag.Length + fallback.Length];
        tag.CopyTo(all, 0);
        fallback.CopyTo(all, tag.Length);
        return SHA256.HashData(all);
    }

    /// <summary>Clear the saved session (called on logout and nuke).</summary>
    public static void Clear() {
        SecureDelete(SessionFile);
    }

    public static bool HasSession() => File.Exists(SessionFile);

    static void SecureDelete(string path) {
        if (!File.Exists(path)) return;

        try {
            var length = new FileInfo(path).Length;
            if (length > 0) {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var buffer = RandomNumberGenerator.GetBytes((int)Math.Min(length, 1024 * 1024));
                long written = 0;
                while (written < length) {
                    var toWrite = (int)Math.Min(buffer.Length, length - written);
                    stream.Write(buffer, 0, toWrite);
                    written += toWrite;
                }
                stream.Flush(flushToDisk: true);
            }
        } catch {
        }

        try {
            File.Delete(path);
        } catch {
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
//  NETWORK CLIENT — SignalR real-time relay with auto-reconnect
// ═══════════════════════════════════════════════════════════════════════════
public enum RelayConnectionState { Disconnected, Connecting, Reconnecting, Connected }

public readonly record struct PublicDirectoryProfile(byte[] SignPubKey, byte[] DhPubKey, string? DisplayName);

public class NetworkClient : IAsyncDisposable {
    static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(12);
    HubConnection? _hub;
    LocalUser? _user;
    readonly SemaphoreSlim _connectGate = new(1, 1);
    readonly SemaphoreSlim _registerGate = new(1, 1);
    readonly CancellationTokenSource _lifetimeCts = new();
    Task? _retryLoop;
    Task? _heartbeatLoop;
    int _retryAttempt;
    int _disconnectSignalSent;
    string? _lastRegisteredConnectionId;
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
        _lastRegisteredConnectionId = null;

        var pinnedThumb = RelayPinning.ResolvePinnedThumbprint(serverUrl);
        if (string.Equals(
                AppBranding.ResolveRelayUrl(null),
                AppBranding.ResolveRelayUrl(serverUrl),
                StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(pinnedThumb)) {
            throw new InvalidOperationException(
                "Certificate pinning is required for the default relay. " +
                $"Set {RelayPinning.RelayThumbprintEnvVar} or RelayPinning.PinnedThumbprint.");
        }
        _hub = new HubConnectionBuilder()
            .WithUrl(hubUrl, options => {
                // Certificate pinning: only active when PinnedThumbprint is set.
                // Leave RelayPinning.PinnedThumbprint = null to disable (default).
                if (!string.IsNullOrWhiteSpace(pinnedThumb)) {
                    options.HttpMessageHandlerFactory = _ => new HttpClientHandler {
                        ServerCertificateCustomValidationCallback = (_, cert, _, sslErrors) => {
                            // Require a clean chain first — no expired / untrusted certs.
                            if (sslErrors != SslPolicyErrors.None) return false;
                            if (cert == null) return false;
                            // Then verify the leaf certificate matches our pinned thumbprint.
                            var thumb = cert.GetCertHashString(HashAlgorithmName.SHA256);
                            var match = string.Equals(thumb, pinnedThumb, StringComparison.OrdinalIgnoreCase);
                            if (!match) {
                                AppLog.Error("relay",
                                    $"TLS certificate pinning failure — expected={pinnedThumb[..8]}... got={thumb[..8]}... " +
                                    "Connection rejected. If the relay cert was legitimately renewed, " +
                                    "update RelayPinning.PinnedThumbprint and redeploy.");
                            }
                            return match;
                        }
                    };
                }
            })
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
            NotifyDisconnectedOnce();
            return Task.CompletedTask;
        };

        _hub.Reconnected += async connectionId => {
            AppLog.Info("relay", $"signalr reconnected connection_id={connectionId ?? "-"}");
            await RegisterAsync();
            ResetRecoveryState();
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
            NotifyDisconnectedOnce();
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
        await _registerGate.WaitAsync(_lifetimeCts.Token);
        try {
            if (_hub == null || _user == null || !IsConnected) return;

            var connectionId = _hub.ConnectionId;
            if (!string.IsNullOrWhiteSpace(connectionId) &&
                string.Equals(connectionId, _lastRegisteredConnectionId, StringComparison.Ordinal)) {
                return;
            }

            var started = AppTelemetry.StartTimer();
            var dhPubKey = Convert.ToBase64String(_user.DhPubKey);
            var selfSig = Crypto.SignRegistration(_user.SignPrivKey, _user.UserId, dhPubKey);
            AppLog.Info("relay", $"register begin user={AppTelemetry.MaskUserId(_user.UserId)}");
            await _hub.InvokeAsync("RegisterV2",
                _user.UserId,
                Convert.ToBase64String(_user.SignPubKey),
                dhPubKey,
                selfSig,
                2);
            await TryPublishDisplayNameAsync();
            _lastRegisteredConnectionId = connectionId;
            AppLog.Info("relay", $"registered relay identity for {AppTelemetry.MaskUserId(_user.UserId)} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
        } finally {
            _registerGate.Release();
        }
    }

    async Task TryPublishDisplayNameAsync() {
        if (_hub == null || _user == null || !IsConnected) return;

        var displayName = NormalizePublicDisplayName(_user.DisplayName);
        if (displayName == null) return;

        try {
            await _hub.InvokeAsync("UpdatePublicDisplayName", displayName);
            AppLog.Info("relay", $"published public display name for {AppTelemetry.MaskUserId(_user.UserId)}");
        } catch (Exception ex) {
            AppLog.Warn("relay", $"public display name publish skipped: {ex.Message}");
        }
    }

    /// <summary>Send encrypted message to a single user.</summary>
    public async Task<bool> SendDmAsync(string recipientId, string payload, string sig, long seqNum) {
        if (!IsConnected) return false;
        var started = AppTelemetry.StartTimer();
        try {
            var wire = JsonSerializer.Deserialize<WireMessageV2>(payload);
            if (wire == null || wire.Version < 2 || wire.MessageType != "dm") {
                throw new InvalidOperationException("invalid dm wire envelope");
            }
            await _hub!.InvokeAsync("SendV2", recipientId, payload, sig, seqNum, wire.SessionId, wire.MessageType, wire.SentAt);
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
        string payload, string sig, long seqNum, string authToken) {
        if (!IsConnected) return false;
        var started = AppTelemetry.StartTimer();
        try {
            await _hub!.InvokeAsync("SendGroup", groupId, recipientIds, payload, sig, seqNum, authToken);
            AppLog.Info("relay", $"group relay send ok group={AppTelemetry.MaskUserId(groupId)} seq={seqNum} recipients={recipientIds.Count} bytes={payload.Length} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
            return true;
        } catch (Exception ex) {
            AppLog.Warn("relay", $"group send failed: {ex.Message}");
            OnError?.Invoke(ex.Message);
            return false;
        }
    }

    /// <summary>Fetch a user's public relay profile (keys + public display name).</summary>
    public async Task<PublicDirectoryProfile?> GetUserProfileAsync(string userId) {
        if (!IsConnected) return null;
        var started = AppTelemetry.StartTimer();
        try {
            if (_user == null) return null;
            var challenge = await _hub!.InvokeAsync<string>("RequestKeyLookupChallengeV2");
            var challengeSig = Crypto.Sign(_user.SignPrivKey, $"{_user.UserId}:{userId}:{challenge}");
            var result = await _hub!.InvokeAsync<KeyBundleDto?>("GetPrekeyBundle", userId, challenge, challengeSig);
            if (result == null) {
                AppLog.Warn("relay", $"key lookup miss for {AppTelemetry.MaskUserId(userId)} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
                return null;
            }
            var keys = ParseVerifiedKeyBundle(userId, result.UserId, result.SignPubKey, result.DhPubKey);
            if (keys == null) {
                AppLog.Warn("relay", $"key lookup rejected for {AppTelemetry.MaskUserId(userId)} due to identity mismatch");
                return null;
            }
            AppLog.Info("relay", $"key lookup ok for {AppTelemetry.MaskUserId(userId)} in {AppTelemetry.ElapsedMilliseconds(started)}ms");
            return new PublicDirectoryProfile(keys.Value.signPub, keys.Value.dhPub, NormalizePublicDisplayName(result.DisplayName));
        } catch (Exception ex) {
            AppLog.Warn("relay", $"key lookup failed for {AppTelemetry.MaskUserId(userId)}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Fetch a user's public keys from the relay (for new contacts).</summary>
    public async Task<(byte[] signPub, byte[] dhPub)?> GetUserKeysAsync(string userId) {
        var profile = await GetUserProfileAsync(userId);
        return profile == null ? null : (profile.Value.SignPubKey, profile.Value.DhPubKey);
    }

    public async Task<bool> AckDmAsync(string senderId, long seqNum, string sessionId) {
        if (!IsConnected) return false;
        var started = AppTelemetry.StartTimer();
        try {
            await _hub!.InvokeAsync("AckDirectV2", senderId, seqNum, sessionId);
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
            ResetRecoveryState();
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
                var attempt = Interlocked.Increment(ref _retryAttempt);
                var delay = GetRetryDelay(attempt);
                AppLog.Warn("relay", $"retry loop attempt={attempt} delay={(int)delay.TotalSeconds}s");
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

    void NotifyDisconnectedOnce() {
        _lastRegisteredConnectionId = null;
        if (Interlocked.Exchange(ref _disconnectSignalSent, 1) == 0) {
            OnDisconnected?.Invoke();
        }
    }

    void ResetRecoveryState() {
        Interlocked.Exchange(ref _retryAttempt, 0);
        Interlocked.Exchange(ref _disconnectSignalSent, 0);
    }

    async Task RunHeartbeatLoopAsync() {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(12));
        try {
            while (await timer.WaitForNextTickAsync(_lifetimeCts.Token)) {
                if (_hub == null || _disposed || !IsConnected) continue;

                try {
                    var started = AppTelemetry.StartTimer();
                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                    var pingTask = _hub.InvokeAsync<long>("Ping", pingCts.Token);
                    var completed = await Task.WhenAny(
                        pingTask,
                        Task.Delay(TimeSpan.FromSeconds(5), _lifetimeCts.Token));
                    if (completed != pingTask && !_lifetimeCts.IsCancellationRequested) {
                        pingCts.Cancel();
                        try {
                            await pingTask;
                        } catch {
                        }
                        throw new TimeoutException("Relay heartbeat timed out.");
                    }
                    if (_lifetimeCts.IsCancellationRequested) break;
                    await pingTask;
                    AppLog.Info("relay", $"heartbeat ok in {AppTelemetry.ElapsedMilliseconds(started)}ms");
                } catch (Exception ex) {
                    if (_hub.State == HubConnectionState.Reconnecting) continue;
                    if (_hub.State != HubConnectionState.Connected) continue;
                    AppLog.Warn("relay", $"heartbeat failed: {AppTelemetry.DescribeException(ex)}");
                    RaiseState(RelayConnectionState.Reconnecting, "relay unreachable - retrying...");
                    NotifyDisconnectedOnce();
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
        _registerGate.Dispose();
        _lifetimeCts.Dispose();
    }

    internal static (byte[] signPub, byte[] dhPub)? ParseVerifiedKeyBundle(
        string expectedUserId,
        string? bundleUserId,
        string signPubKey,
        string dhPubKey) {
        var signPub = Convert.FromBase64String(signPubKey);
        try {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(signPub, out _);
        } catch {
            return null;
        }
        var derivedUserId = Crypto.DeriveUserId(signPub);
        if (!string.Equals(derivedUserId, expectedUserId, StringComparison.Ordinal)) {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(bundleUserId) &&
            !string.Equals(bundleUserId, expectedUserId, StringComparison.Ordinal)) {
            return null;
        }

        var dhPub = Convert.FromBase64String(dhPubKey);
        try {
            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportSubjectPublicKeyInfo(dhPub, out _);
        } catch {
            return null;
        }

        return (signPub, dhPub);
    }

    static string? NormalizePublicDisplayName(string? displayName) {
        var normalized = (displayName ?? "").Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0) return null;
        var filtered = new string(normalized.Where(ch =>
            ch is not '\u200E' and not '\u200F' &&
            !(ch >= '\u202A' && ch <= '\u202E') &&
            !(ch >= '\u2066' && ch <= '\u2069')).ToArray());
        return filtered.Length == 0 ? null : filtered;
    }

    record KeyBundleDto(
        [property: JsonPropertyName("userId")] string UserId,
        [property: JsonPropertyName("signPubKey")] string SignPubKey,
        [property: JsonPropertyName("dhPubKey")] string DhPubKey,
        [property: JsonPropertyName("registeredAt")] long RegisteredAt,
        [property: JsonPropertyName("displayName")] string? DisplayName = null);
}

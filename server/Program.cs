using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var port)) {
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.Services.AddHttpClient();
builder.Services.AddSingleton(RelayStoreOptions.FromEnvironment());
builder.Services.AddSingleton<IRelayStore>(sp => RelayStoreFactory.Create(
    sp.GetRequiredService<RelayStoreOptions>(),
    sp.GetRequiredService<IHttpClientFactory>(),
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = 64 * 1024;
    options.EnableDetailedErrors = false;
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
});

var app = builder.Build();

using (var scope = app.Services.CreateScope()) {
    var store = scope.ServiceProvider.GetRequiredService<IRelayStore>();
    await store.InitializeAsync();
}

app.MapGet("/", () => "CIPHER RELAY ONLINE");
app.MapMethods("/", ["HEAD"], () => Results.Ok());
app.MapGet("/health", (IRelayStore store) => Results.Ok(new { status = "ok", storage = store.Name }));
app.MapHub<CipherHub>("/hub");
app.Run();

static class RelayState {
    public static readonly ConcurrentDictionary<string, string> Connections = new();
    public static readonly ConcurrentDictionary<string, string> ConnUsers = new();
}

public record KeyBundle(string UserId, string SignPubKey, string DhPubKey, long RegisteredAt, string DisplayName = "");

public class CipherHub : Hub {
    readonly IRelayStore _store;
    readonly RelayStoreOptions _options;

    public CipherHub(IRelayStore store, RelayStoreOptions options) {
        _store = store;
        _options = options;
    }

    public async Task Register(string userId, string signPubKey, string dhPubKey, string selfSig) {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _store.CleanupAsync(now);

        if (await IsRateLimitedAsync("register", 20, TimeSpan.FromMinutes(1))) {
            throw new HubException("RATE_LIMITED");
        }

        if (!VerifyRegistration(userId, signPubKey, dhPubKey, selfSig)) {
            throw new HubException("INVALID_SIGNATURE");
        }

        if (!IsValidRegistrationPayload(userId, signPubKey, dhPubKey)) {
            throw new HubException("INVALID_ID");
        }

        RelayState.Connections[userId] = Context.ConnectionId;
        RelayState.ConnUsers[Context.ConnectionId] = userId;

        await _store.UpsertKeyBundleAsync(new KeyBundle(
            userId,
            signPubKey,
            dhPubKey,
            now));

        var pending = await _store.GetPendingMessagesAsync(userId, 200, now);
        foreach (var msg in pending) {
            if (msg.Kind == RelayMessageKind.Group && !string.IsNullOrWhiteSpace(msg.GroupId)) {
                await Clients.Caller.SendAsync(
                    "ReceiveGroup",
                    msg.GroupId,
                    msg.SenderId,
                    msg.Payload,
                    msg.Sig,
                    msg.SeqNum,
                    msg.Ts);
            } else {
                await Clients.Caller.SendAsync(
                    "Receive",
                    msg.SenderId,
                    msg.Payload,
                    msg.Sig,
                    msg.SeqNum,
                    msg.Ts);
            }
        }
    }

    public async Task UpdatePublicDisplayName(string displayName) {
        var userId = GetCallerId();
        if (userId == null) throw new HubException("NOT_REGISTERED");
        if (await IsRateLimitedAsync($"profile:{userId}", 30, TimeSpan.FromMinutes(1)))
            throw new HubException("RATE_LIMITED");

        var normalized = NormalizePublicDisplayName(displayName);
        if (normalized == null)
            throw new HubException("INVALID_DISPLAY_NAME");

        await _store.SetPublicDisplayNameAsync(userId, normalized);
    }

    public async Task Send(string recipientId, string payload, string sig, long seqNum) {
        var senderId = GetCallerId();
        if (senderId == null) throw new HubException("NOT_REGISTERED");
        if (await IsRateLimitedAsync($"dm:{senderId}", 240, TimeSpan.FromMinutes(1)))
            throw new HubException("RATE_LIMITED");
        if (!IsValidMessageEnvelope(recipientId, payload, sig))
            throw new HubException("INVALID_PAYLOAD");
        if (!await VerifyMessageAsync(senderId, payload, sig, seqNum))
            throw new HubException("INVALID_SIGNATURE");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expiresAt = now + (long)TimeSpan.FromDays(_options.PendingTtlDays).TotalMilliseconds;
        var stored = await _store.TryStoreDirectAsync(recipientId, senderId, payload, sig, seqNum, now, expiresAt);
        if (!stored)
            throw new HubException("REPLAY_REJECTED");

        if (RelayState.Connections.TryGetValue(recipientId, out var connId)) {
            await Clients.Client(connId).SendAsync("Receive", senderId, payload, sig, seqNum, now);
        }
    }

    public async Task SendGroup(string groupId, List<string> recipientIds, string payload, string sig, long seqNum) {
        var senderId = GetCallerId();
        if (senderId == null) throw new HubException("NOT_REGISTERED");
        if (await IsRateLimitedAsync($"grp:{senderId}", 120, TimeSpan.FromMinutes(1)))
            throw new HubException("RATE_LIMITED");
        if (!IsValidGroupRequest(groupId, recipientIds, payload, sig))
            throw new HubException("INVALID_PAYLOAD");
        if (!await VerifyMessageAsync(senderId, payload, sig, seqNum))
            throw new HubException("INVALID_SIGNATURE");
        if (recipientIds.Count > 100) throw new HubException("TOO_MANY_RECIPIENTS");

        var uniqueRecipients = recipientIds
            .Where(id => id != senderId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expiresAt = now + (long)TimeSpan.FromDays(_options.PendingTtlDays).TotalMilliseconds;
        var stored = await _store.TryStoreGroupAsync(groupId, uniqueRecipients, senderId, payload, sig, seqNum, now, expiresAt);
        if (!stored)
            throw new HubException("REPLAY_REJECTED");

        foreach (var recipientId in uniqueRecipients) {
            if (RelayState.Connections.TryGetValue(recipientId, out var connId)) {
                await Clients.Client(connId).SendAsync("ReceiveGroup", groupId, senderId, payload, sig, seqNum, now);
            }
        }
    }

    public async Task AckDirect(string senderId, long seqNum) {
        var recipientId = GetCallerId();
        if (recipientId == null) throw new HubException("NOT_REGISTERED");
        if (!IsValidToken(senderId, 8, 128)) throw new HubException("INVALID_ID");
        await _store.AckDirectAsync(recipientId, senderId, seqNum);
    }

    public async Task AckGroup(string groupId, string senderId, long seqNum) {
        var recipientId = GetCallerId();
        if (recipientId == null) throw new HubException("NOT_REGISTERED");
        if (!IsValidToken(groupId, 8, 128) || !IsValidToken(senderId, 8, 128))
            throw new HubException("INVALID_ID");
        await _store.AckGroupAsync(recipientId, groupId, senderId, seqNum);
    }

    public async Task<KeyBundle?> GetKeys(string userId) {
        if (!IsValidToken(userId, 8, 128)) return null;
        if (await IsRateLimitedAsync("keys", 240, TimeSpan.FromMinutes(1)))
            throw new HubException("RATE_LIMITED");
        return await _store.GetKeyBundleAsync(userId);
    }

    public async Task AnnouncePresence(List<string> contactIds) {
        var me = GetCallerId();
        if (me == null) return;
        if (await IsRateLimitedAsync($"presence:{me}", 60, TimeSpan.FromMinutes(1))) return;

        foreach (var contactId in contactIds.Where(id => IsValidToken(id, 8, 128)).Take(200)) {
            if (RelayState.Connections.TryGetValue(contactId, out var connId))
                await Clients.Client(connId).SendAsync("UserOnline", me);
        }
    }

    public long Ping() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public override async Task OnDisconnectedAsync(Exception? exception) {
        if (RelayState.ConnUsers.TryRemove(Context.ConnectionId, out var userId)) {
            RelayState.Connections.TryRemove(userId, out _);
        }

        await base.OnDisconnectedAsync(exception);
    }

    static bool IsValidRegistrationPayload(string userId, string signPubKey, string dhPubKey) {
        if (string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(signPubKey) ||
            string.IsNullOrWhiteSpace(dhPubKey)) {
            return false;
        }

        if (userId.Length < 8 || userId.Length > 64) {
            return false;
        }

        try {
            var signBytes = Convert.FromBase64String(signPubKey);
            var dhBytes = Convert.FromBase64String(dhPubKey);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(signBytes, out _);
            using var ecdh = ECDiffieHellman.Create();
            ecdh.ImportSubjectPublicKeyInfo(dhBytes, out _);
            return true;
        } catch {
            return false;
        }
    }

    static bool IsValidMessageEnvelope(string targetId, string payload, string sig) {
        if (!IsValidToken(targetId, 8, 128)) return false;
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > 48_000) return false;

        try {
            var sigBytes = Convert.FromBase64String(sig);
            return sigBytes.Length is >= 48 and <= 512;
        } catch {
            return false;
        }
    }

    static bool IsValidGroupRequest(string groupId, List<string> recipientIds, string payload, string sig) {
        if (!IsValidToken(groupId, 8, 128)) return false;
        if (recipientIds.Count == 0 || recipientIds.Count > 100) return false;
        if (recipientIds.Any(id => !IsValidToken(id, 8, 128))) return false;
        return IsValidMessageEnvelope(recipientIds[0], payload, sig);
    }

    static bool IsValidToken(string value, int minLength, int maxLength) {
        if (string.IsNullOrWhiteSpace(value) || value.Length < minLength || value.Length > maxLength) {
            return false;
        }

        foreach (var ch in value) {
            if (!char.IsLetterOrDigit(ch) && ch is not '-' and not '_' and not ':') {
                return false;
            }
        }

        return true;
    }

    string? GetCallerId() {
        RelayState.ConnUsers.TryGetValue(Context.ConnectionId, out var userId);
        return userId;
    }

    async Task<bool> IsRateLimitedAsync(string bucket, int limit, TimeSpan window) {
        var clientKey = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString()
            ?? Context.ConnectionId;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return !await _store.AllowRequestAsync($"{bucket}:{clientKey}", limit, window, now);
    }

    async Task<bool> VerifyMessageAsync(string senderId, string payload, string sig, long seqNum) {
        try {
            var keys = await _store.GetKeyBundleAsync(senderId);
            if (keys == null) return false;
            var pubKeyBytes = Convert.FromBase64String(keys.SignPubKey);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(pubKeyBytes, out _);
            var data = Encoding.UTF8.GetBytes($"{payload}:{seqNum}");
            return ecdsa.VerifyData(data, Convert.FromBase64String(sig), HashAlgorithmName.SHA256);
        } catch {
            return false;
        }
    }

    static bool VerifyRegistration(string userId, string signPubKey, string dhPubKey, string selfSig) {
        try {
            var pubKeyBytes = Convert.FromBase64String(signPubKey);
            var expectedId = Convert.ToBase64String(SHA256.HashData(pubKeyBytes))
                .Replace("+", "-")
                .Replace("/", "_")[..22];
            if (userId != expectedId) return false;

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(pubKeyBytes, out _);
            return ecdsa.VerifyData(
                Encoding.UTF8.GetBytes($"{userId}:{dhPubKey}"),
                Convert.FromBase64String(selfSig),
                HashAlgorithmName.SHA256);
        } catch {
            return false;
        }
    }

    static string? NormalizePublicDisplayName(string? rawDisplayName) {
        var trimmed = (rawDisplayName ?? "").Trim();
        if (trimmed.Length is < 1 or > 48) {
            return null;
        }

        foreach (var ch in trimmed) {
            if (char.IsControl(ch)) {
                return null;
            }
        }

        return trimmed;
    }
}

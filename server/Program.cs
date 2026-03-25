using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var port)) {
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.Services.AddHttpClient();
builder.Services.Configure<ForwardedHeadersOptions>(options => {
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    var trustedProxies = (Environment.GetEnvironmentVariable("RELAY_TRUSTED_PROXIES") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    foreach (var proxy in trustedProxies) {
        if (IPAddress.TryParse(proxy, out var address)) {
            options.KnownProxies.Add(address);
        }
    }
    options.ForwardLimit = trustedProxies.Length > 0 ? null : 1;
});
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
app.UseForwardedHeaders();
app.Use(async (context, next) => {
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
    headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
    if (context.Request.IsHttps) {
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    }
    await next();
});

using (var scope = app.Services.CreateScope()) {
    var store = scope.ServiceProvider.GetRequiredService<IRelayStore>();
    await store.InitializeAsync();
}

var cleanupCts = new CancellationTokenSource();
app.Lifetime.ApplicationStopping.Register(() => cleanupCts.Cancel());
_ = Task.Run(async () => {
    while (!cleanupCts.IsCancellationRequested) {
        try {
            using var scope = app.Services.CreateScope();
            var store = scope.ServiceProvider.GetRequiredService<IRelayStore>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await store.CleanupAsync(now, cleanupCts.Token);
        } catch (OperationCanceledException) {
            break;
        } catch {
        }

        try {
            await Task.Delay(TimeSpan.FromMinutes(5), cleanupCts.Token);
        } catch (OperationCanceledException) {
            break;
        }
    }
}, cleanupCts.Token);

app.MapGet("/", () => "CIPHER RELAY ONLINE");
app.MapMethods("/", ["HEAD"], () => Results.Ok());
app.MapGet("/health", (IRelayStore store) => Results.Ok(new { status = "ok", storage = store.Name }));
app.MapHub<CipherHub>("/hub");
app.Run();

static class RelayState {
    public static readonly ConcurrentDictionary<string, string> Connections = new();
    public static readonly ConcurrentDictionary<string, string> ConnUsers = new();
    public static readonly ConcurrentDictionary<string, KeyLookupChallenge> KeyLookupChallenges = new();
}

public readonly record struct KeyLookupChallenge(string CallerUserId, long ExpiresAt);

public record KeyBundle(string UserId, string SignPubKey, string DhPubKey, long RegisteredAt, string DisplayName = "");

public class CipherHub : Hub {
    readonly IRelayStore _store;
    readonly RelayStoreOptions _options;
    static readonly TimeSpan KeyLookupChallengeTtl = TimeSpan.FromMinutes(2);
    const int MaxOutstandingKeyLookupChallenges = 20_000;

    public CipherHub(IRelayStore store, RelayStoreOptions options) {
        _store = store;
        _options = options;
    }

    public Task Register(string userId, string signPubKey, string dhPubKey, string selfSig) =>
        throw new HubException("UPGRADE_REQUIRED");

    public async Task RegisterV2(string userId, string signPubKey, string dhPubKey, string selfSig, int protocolVersion) {
        if (protocolVersion < 2) throw new HubException("UPGRADE_REQUIRED");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (await IsRateLimitedAsync("register", 20, TimeSpan.FromMinutes(1), userId)) {
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

        var existing = await _store.GetKeyBundleAsync(userId);
        if (existing != null &&
            !string.Equals(existing.DhPubKey, dhPubKey, StringComparison.Ordinal)) {
            Console.WriteLine($"[security] relay dh key changed user={userId} at={now}");
        }

        await _store.UpsertKeyBundleAsync(new KeyBundle(
            userId,
            signPubKey,
            dhPubKey,
            now));

        var pending = await _store.GetPendingMessagesAsync(userId, 200, now);
        var delivered = 0;
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

            delivered++;
            if (delivered % 20 == 0) {
                await Task.Delay(20);
            }
        }
    }

    public async Task UpdatePublicDisplayName(string displayName) {
        var userId = GetCallerId();
        if (userId == null) throw new HubException("NOT_REGISTERED");
        if (await IsRateLimitedAsync($"profile:{userId}", 30, TimeSpan.FromMinutes(1), userId))
            throw new HubException("RATE_LIMITED");

        var normalized = NormalizePublicDisplayName(displayName);
        if (normalized == null)
            throw new HubException("INVALID_DISPLAY_NAME");

        await _store.SetPublicDisplayNameAsync(userId, normalized);
    }

    public Task Send(string recipientId, string payload, string sig, long seqNum) =>
        throw new HubException("UPGRADE_REQUIRED");

    public async Task SendV2(string recipientId, string payload, string sig, long seqNum, string sessionId, string messageType, long sentAt) {
        var senderId = GetCallerId();
        if (senderId == null) throw new HubException("NOT_REGISTERED");
        if (await IsRateLimitedAsync($"dm:{senderId}", 240, TimeSpan.FromMinutes(1), senderId))
            throw new HubException("RATE_LIMITED");
        if (messageType != "dm") throw new HubException("INVALID_PAYLOAD");
        if (!IsValidToken(sessionId, 8, 128)) throw new HubException("INVALID_PAYLOAD");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (sentAt < now - (long)TimeSpan.FromDays(45).TotalMilliseconds ||
            sentAt > now + (long)TimeSpan.FromMinutes(2).TotalMilliseconds) {
            throw new HubException("INVALID_TIMESTAMP");
        }
        if (!IsValidMessageEnvelope(recipientId, payload, sig))
            throw new HubException("INVALID_PAYLOAD");
        if (!await VerifyMessageAsync(senderId, payload, sig, seqNum))
            throw new HubException("INVALID_SIGNATURE");

        var expiresAt = now + (long)TimeSpan.FromDays(_options.PendingTtlDays).TotalMilliseconds;
        var stored = await _store.TryStoreDirectAsync(recipientId, senderId, payload, sig, seqNum, now, expiresAt);
        if (!stored)
            throw new HubException("REPLAY_REJECTED");
        await _store.RecordDirectContactAsync(senderId, recipientId, now);

        if (RelayState.Connections.TryGetValue(recipientId, out var connId)) {
            await Clients.Client(connId).SendAsync("Receive", senderId, payload, sig, seqNum, now);
        }
    }

    public async Task SendGroup(string groupId, List<string> recipientIds, string payload, string sig, long seqNum, string authToken) {
        var senderId = GetCallerId();
        if (senderId == null) throw new HubException("NOT_REGISTERED");
        if (await IsRateLimitedAsync($"grp:{senderId}", 120, TimeSpan.FromMinutes(1), senderId))
            throw new HubException("RATE_LIMITED");
        if (!IsValidGroupRequest(groupId, recipientIds, payload, sig, authToken))
            throw new HubException("INVALID_PAYLOAD");
        if (!await VerifyMessageAsync(senderId, payload, sig, seqNum))
            throw new HubException("INVALID_SIGNATURE");
        if (recipientIds.Count > 100) throw new HubException("TOO_MANY_RECIPIENTS");

        var uniqueRecipients = recipientIds
            .Where(id => id != senderId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var authorized = await _store.AuthorizeGroupSendAsync(groupId, authToken, senderId, uniqueRecipients, now);
        if (!authorized) {
            throw new HubException("GROUP_AUTH_FAILED");
        }

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

    public Task AckDirect(string senderId, long seqNum) =>
        throw new HubException("UPGRADE_REQUIRED");

    public async Task AckDirectV2(string senderId, long seqNum, string sessionId) {
        var recipientId = GetCallerId();
        if (recipientId == null) throw new HubException("NOT_REGISTERED");
        if (!IsValidToken(senderId, 8, 128)) throw new HubException("INVALID_ID");
        if (!IsValidToken(sessionId, 8, 128)) throw new HubException("INVALID_ID");
        await _store.AckDirectAsync(recipientId, senderId, seqNum);
    }

    public async Task AckGroup(string groupId, string senderId, long seqNum) {
        var recipientId = GetCallerId();
        if (recipientId == null) throw new HubException("NOT_REGISTERED");
        if (!IsValidToken(groupId, 8, 128) || !IsValidToken(senderId, 8, 128))
            throw new HubException("INVALID_ID");
        await _store.AckGroupAsync(recipientId, groupId, senderId, seqNum);
    }

    public Task<string> RequestKeyLookupChallenge() =>
        throw new HubException("UPGRADE_REQUIRED");

    public async Task<string> RequestKeyLookupChallengeV2() {
        var callerId = GetCallerId();
        if (callerId == null) throw new HubException("NOT_REGISTERED");
        if (await IsRateLimitedAsync($"keys:challenge:{callerId}", 60, TimeSpan.FromMinutes(1))) {
            throw new HubException("RATE_LIMITED");
        }
        CleanupExpiredKeyLookupChallenges(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (RelayState.KeyLookupChallenges.Count >= MaxOutstandingKeyLookupChallenges) {
            throw new HubException("BUSY");
        }
        var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
        var expiresAt = DateTimeOffset.UtcNow.Add(KeyLookupChallengeTtl).ToUnixTimeMilliseconds();
        RelayState.KeyLookupChallenges[challenge] = new KeyLookupChallenge(callerId, expiresAt);
        return challenge;
    }

    public Task<KeyBundle?> GetKeys(string userId, string challenge, string challengeSig) =>
        throw new HubException("UPGRADE_REQUIRED");

    public async Task<KeyBundle?> GetPrekeyBundle(string userId, string challenge, string challengeSig) {
        var callerId = GetCallerId();
        if (callerId == null) throw new HubException("NOT_REGISTERED");
        if (!IsValidToken(userId, 8, 128)) return null;
        if (string.IsNullOrWhiteSpace(challenge) || string.IsNullOrWhiteSpace(challengeSig)) {
            throw new HubException("INVALID_CHALLENGE");
        }
        if (await IsRateLimitedAsync("keys", 240, TimeSpan.FromMinutes(1), callerId))
            throw new HubException("RATE_LIMITED");
        CleanupExpiredKeyLookupChallenges(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (!RelayState.KeyLookupChallenges.TryRemove(challenge, out var state) ||
            !string.Equals(state.CallerUserId, callerId, StringComparison.Ordinal) ||
            state.ExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) {
            throw new HubException("INVALID_CHALLENGE");
        }
        if (!await VerifyKeyLookupChallengeAsync(callerId, userId, challenge, challengeSig)) {
            throw new HubException("INVALID_SIGNATURE");
        }
        return await _store.GetKeyBundleAsync(userId);
    }

    public async Task AnnouncePresence(List<string> contactIds) {
        var me = GetCallerId();
        if (me == null) return;
        if (await IsRateLimitedAsync($"presence:{me}", 60, TimeSpan.FromMinutes(1))) return;

        foreach (var contactId in contactIds.Where(id => IsValidToken(id, 8, 128)).Take(200)) {
            var mutual = await _store.AreMutualContactsAsync(me, contactId);
            if (!mutual) continue;
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

    static bool IsValidGroupRequest(string groupId, List<string> recipientIds, string payload, string sig, string authToken) {
        if (!IsValidToken(groupId, 8, 128)) return false;
        if (recipientIds.Count == 0 || recipientIds.Count > 100) return false;
        if (recipientIds.Any(id => !IsValidToken(id, 8, 128))) return false;
        if (string.IsNullOrWhiteSpace(authToken) || authToken.Length is < 16 or > 512) return false;
        return IsValidMessageEnvelope(groupId, payload, sig);
    }

    static void CleanupExpiredKeyLookupChallenges(long nowMs) {
        foreach (var entry in RelayState.KeyLookupChallenges) {
            if (entry.Value.ExpiresAt < nowMs) {
                RelayState.KeyLookupChallenges.TryRemove(entry.Key, out _);
            }
        }
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

    async Task<bool> IsRateLimitedAsync(string bucket, int limit, TimeSpan window, string? userId = null) {
        var clientKey = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString()
            ?? Context.ConnectionId;
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ipAllowed = await _store.AllowRequestAsync($"{bucket}:ip:{clientKey}", limit, window, now);
        if (!ipAllowed) return true;
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var userAllowed = await _store.AllowRequestAsync($"{bucket}:uid:{userId}", Math.Max(10, limit / 2), window, now);
        return !userAllowed;
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

    async Task<bool> VerifyKeyLookupChallengeAsync(string callerId, string targetUserId, string challenge, string challengeSig) {
        try {
            var keys = await _store.GetKeyBundleAsync(callerId);
            if (keys == null) return false;
            var pubKeyBytes = Convert.FromBase64String(keys.SignPubKey);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(pubKeyBytes, out _);
            var signedData = Encoding.UTF8.GetBytes($"{callerId}:{targetUserId}:{challenge}");
            return ecdsa.VerifyData(
                signedData,
                Convert.FromBase64String(challengeSig),
                HashAlgorithmName.SHA256);
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
        var normalized = (rawDisplayName ?? "").Trim().Normalize(NormalizationForm.FormC);
        if (normalized.Length is < 1 or > 48) {
            return null;
        }

        foreach (var ch in normalized) {
            if (IsBidiControl(ch)) {
                return null;
            }
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is not (UnicodeCategory.LowercaseLetter or
                                 UnicodeCategory.UppercaseLetter or
                                 UnicodeCategory.DecimalDigitNumber or
                                 UnicodeCategory.SpaceSeparator)) {
                return null;
            }
        }

        return normalized;
    }

    static bool IsBidiControl(char ch) =>
        ch is '\u200E' or '\u200F' ||
        (ch >= '\u202A' && ch <= '\u202E') ||
        (ch >= '\u2066' && ch <= '\u2069');
}

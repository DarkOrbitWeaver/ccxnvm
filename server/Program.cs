using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var port)) {
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = 64 * 1024;
    options.EnableDetailedErrors = false;
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
});
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();
app.MapGet("/", () => "CIPHER RELAY ONLINE");
app.MapMethods("/", ["HEAD"], () => Results.Ok());
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapHub<CipherHub>("/hub");
app.Run();

static class RelayState {
    public static readonly ConcurrentDictionary<string, string> Connections = new();
    public static readonly ConcurrentDictionary<string, string> ConnUsers = new();
    public static readonly ConcurrentDictionary<string, KeyBundle> Keys = new();
    public static readonly ConcurrentDictionary<string, ConcurrentQueue<OfflineMsg>> Offline = new();
    public static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> DirectSeqTracker = new();
    public static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> GroupSeqTracker = new();
}

enum OfflineMessageKind {
    Direct,
    Group
}

public record KeyBundle(string UserId, string SignPubKey, string DhPubKey, long RegisteredAt);
record OfflineMsg(OfflineMessageKind Kind, string SenderId, string Payload, string Sig, long SeqNum, long Ts, string? GroupId = null);

public class CipherHub : Hub {
    public async Task Register(string userId, string signPubKey, string dhPubKey, string selfSig) {
        if (!VerifyRegistration(userId, signPubKey, selfSig)) {
            throw new HubException("INVALID_SIGNATURE");
        }

        if (!IsValidRegistrationPayload(userId, signPubKey, dhPubKey)) {
            throw new HubException("INVALID_ID");
        }

        RelayState.Connections[userId] = Context.ConnectionId;
        RelayState.ConnUsers[Context.ConnectionId] = userId;
        RelayState.Keys[userId] = new KeyBundle(
            userId,
            signPubKey,
            dhPubKey,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        if (RelayState.Offline.TryGetValue(userId, out var queue)) {
            while (queue.TryDequeue(out var msg)) {
                if (msg.Kind == OfflineMessageKind.Group && !string.IsNullOrWhiteSpace(msg.GroupId)) {
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
    }

    public async Task Send(string recipientId, string payload, string sig, long seqNum) {
        var senderId = GetCallerId();
        if (senderId == null) throw new HubException("NOT_REGISTERED");
        if (!IsValidMessageEnvelope(recipientId, payload, sig))
            throw new HubException("INVALID_PAYLOAD");

        if (!VerifyMessage(senderId, payload, sig, seqNum))
            throw new HubException("INVALID_SIGNATURE");

        var senderSeqs = RelayState.DirectSeqTracker.GetOrAdd(recipientId, _ => new());
        if (senderSeqs.TryGetValue(senderId, out var lastSeq) && seqNum <= lastSeq)
            throw new HubException("REPLAY_REJECTED");
        senderSeqs[senderId] = seqNum;

        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (RelayState.Connections.TryGetValue(recipientId, out var connId)) {
            await Clients.Client(connId).SendAsync("Receive", senderId, payload, sig, seqNum, ts);
        } else {
            var queue = RelayState.Offline.GetOrAdd(recipientId, _ => new ConcurrentQueue<OfflineMsg>());
            if (queue.Count >= 200) queue.TryDequeue(out _);
            queue.Enqueue(new OfflineMsg(OfflineMessageKind.Direct, senderId, payload, sig, seqNum, ts));
        }
    }

    public async Task SendGroup(string groupId, List<string> recipientIds, string payload, string sig, long seqNum) {
        var senderId = GetCallerId();
        if (senderId == null) throw new HubException("NOT_REGISTERED");
        if (!IsValidGroupRequest(groupId, recipientIds, payload, sig))
            throw new HubException("INVALID_PAYLOAD");
        if (!VerifyMessage(senderId, payload, sig, seqNum)) throw new HubException("INVALID_SIGNATURE");
        if (recipientIds.Count > 100) throw new HubException("TOO_MANY_RECIPIENTS");

        var senderSeqs = RelayState.GroupSeqTracker.GetOrAdd(groupId, _ => new());
        if (senderSeqs.TryGetValue(senderId, out var lastSeq) && seqNum <= lastSeq)
            throw new HubException("REPLAY_REJECTED");
        senderSeqs[senderId] = seqNum;

        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        foreach (var recipientId in recipientIds.Distinct()) {
            if (recipientId == senderId) continue;

            if (RelayState.Connections.TryGetValue(recipientId, out var connId)) {
                await Clients.Client(connId).SendAsync("ReceiveGroup", groupId, senderId, payload, sig, seqNum, ts);
            } else {
                var queue = RelayState.Offline.GetOrAdd(recipientId, _ => new ConcurrentQueue<OfflineMsg>());
                if (queue.Count >= 200) queue.TryDequeue(out _);
                queue.Enqueue(new OfflineMsg(OfflineMessageKind.Group, senderId, payload, sig, seqNum, ts, groupId));
            }
        }
    }

    public Task<KeyBundle?> GetKeys(string userId) {
        RelayState.Keys.TryGetValue(userId, out var keys);
        return Task.FromResult(keys);
    }

    public async Task AnnouncePresence(List<string> contactIds) {
        var me = GetCallerId();
        if (me == null) return;

        foreach (var contactId in contactIds.Take(200)) {
            if (RelayState.Connections.TryGetValue(contactId, out var connId))
                await Clients.Client(connId).SendAsync("UserOnline", me);
        }
    }

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
            return signBytes.Length >= 64 && dhBytes.Length >= 64;
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

    static bool VerifyMessage(string senderId, string payload, string sig, long seqNum) {
        try {
            if (!RelayState.Keys.TryGetValue(senderId, out var keys)) return false;
            var pubKeyBytes = Convert.FromBase64String(keys.SignPubKey);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(pubKeyBytes, out _);
            var data = Encoding.UTF8.GetBytes($"{payload}:{seqNum}");
            return ecdsa.VerifyData(data, Convert.FromBase64String(sig), HashAlgorithmName.SHA256);
        } catch {
            return false;
        }
    }

    static bool VerifyRegistration(string userId, string signPubKey, string selfSig) {
        try {
            var pubKeyBytes = Convert.FromBase64String(signPubKey);
            var expectedId = Convert.ToBase64String(SHA256.HashData(pubKeyBytes))
                .Replace("+", "-")
                .Replace("/", "_")[..22];
            if (userId != expectedId) return false;

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(pubKeyBytes, out _);
            return ecdsa.VerifyData(
                Encoding.UTF8.GetBytes(userId),
                Convert.FromBase64String(selfSig),
                HashAlgorithmName.SHA256);
        } catch {
            return false;
        }
    }
}

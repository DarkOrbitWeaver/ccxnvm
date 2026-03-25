using System.Net;
using System.Security.Cryptography;
using Cipher;

if (args.Length != 1) {
    Console.Error.WriteLine("usage: dotnet run --project tools\\Cipher.SmokeRunner -- <server-url>");
    return 2;
}

var serverUrl = RelayUrl.Normalize(args[0]);
if (!RelayUrl.IsValid(serverUrl)) {
    Console.Error.WriteLine(RelayUrl.ValidationHint);
    return 2;
}

var scenarios = new List<(string Name, Func<Task> Run)> {
    ("health endpoints", () => HealthEndpointsAsync(serverUrl)),
    ("direct message round trip", () => DirectMessageRoundTripAsync(serverUrl)),
    ("offline delivery", () => OfflineDeliveryAsync(serverUrl)),
    ("offline group delivery", () => OfflineGroupDeliveryAsync(serverUrl)),
    ("acked offline messages do not replay", () => AckedOfflineMessagesDoNotReplayAsync(serverUrl)),
    ("direct replay rejection", () => ReplayRejectionAsync(serverUrl)),
    ("group replay rejection", () => GroupReplayRejectionAsync(serverUrl)),
    ("key lookup", () => KeyLookupAsync(serverUrl))
};

var failures = new List<string>();
foreach (var scenario in scenarios) {
    try {
        await scenario.Run();
        Console.WriteLine($"PASS {scenario.Name}");
    } catch (Exception ex) {
        failures.Add($"{scenario.Name}: {ex.Message}");
        Console.WriteLine($"FAIL {scenario.Name}: {ex.Message}");
    }
}

if (failures.Count > 0) {
    Console.Error.WriteLine("Smoke runner failures:");
    foreach (var failure in failures) {
        Console.Error.WriteLine(" - " + failure);
    }
    return 1;
}

Console.WriteLine("All local smoke scenarios passed.");
return 0;

static async Task HealthEndpointsAsync(string serverUrl) {
    using var client = new HttpClient();
    using var health = await client.GetAsync($"{serverUrl}/health");
    using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"{serverUrl}/"));
    var root = await client.GetStringAsync($"{serverUrl}/");

    Ensure(health.StatusCode == HttpStatusCode.OK, "health endpoint did not return 200");
    Ensure(head.StatusCode == HttpStatusCode.OK, "HEAD / did not return 200");
    Ensure(root == "CIPHER RELAY ONLINE", "root endpoint returned unexpected content");
}

static async Task DirectMessageRoundTripAsync(string serverUrl) {
    var alice = CreateUser(serverUrl, "alice");
    var bob = CreateUser(serverUrl, "bob");
    await using var aliceClient = new NetworkClient();
    await using var bobClient = new NetworkClient();

    var received = new TaskCompletionSource<(string senderId, string payload, string sig, long seq, long ts)>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    bobClient.OnMessage += (senderId, payload, sig, seq, ts) => received.TrySetResult((senderId, payload, sig, seq, ts));

    await aliceClient.ConnectAsync(alice);
    await bobClient.ConnectAsync(bob);

    var convId = ConversationId(alice, bob);
    var direct = CreateDirectMessage(alice, bob, convId, "hello bob", 1);
    Ensure(await aliceClient.SendDmAsync(bob.UserId, direct.payload, direct.sig, direct.message.SeqNum), "send failed");

    var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var convKey = Crypto.DeriveSharedSecret(bob.DhPrivKey, alice.DhPubKey, convId);
    var decrypted = Crypto.DecryptDm(convKey, alice.UserId, envelope.payload);

    Ensure(decrypted != null, "recipient could not decrypt message");
    Ensure(decrypted!.Content == "hello bob", "recipient got wrong direct message content");
}

static async Task OfflineDeliveryAsync(string serverUrl) {
    var alice = CreateUser(serverUrl, "alice");
    var bob = CreateUser(serverUrl, "bob");
    await using var aliceClient = new NetworkClient();
    await using var bobClient = new NetworkClient();

    await aliceClient.ConnectAsync(alice);

    var convId = ConversationId(alice, bob);
    var queued = CreateDirectMessage(alice, bob, convId, "offline hello", 1);
    Ensure(await aliceClient.SendDmAsync(bob.UserId, queued.payload, queued.sig, queued.message.SeqNum), "offline send failed");

    var received = new TaskCompletionSource<(string senderId, string payload, string sig, long seq, long ts)>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    bobClient.OnMessage += (senderId, payload, sig, seq, ts) => received.TrySetResult((senderId, payload, sig, seq, ts));

    await bobClient.ConnectAsync(bob);

    var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var convKey = Crypto.DeriveSharedSecret(bob.DhPrivKey, alice.DhPubKey, convId);
    var decrypted = Crypto.DecryptDm(convKey, alice.UserId, envelope.payload);

    Ensure(decrypted != null && decrypted.Content == "offline hello", "offline queued message did not arrive intact");
    Ensure(await bobClient.AckDmAsync(alice.UserId, envelope.seq, convId), "recipient ack failed");
}

static async Task OfflineGroupDeliveryAsync(string serverUrl) {
    var alice = CreateUser(serverUrl, "alice");
    var bob = CreateUser(serverUrl, "bob");
    await using var aliceClient = new NetworkClient();
    await using var bobClient = new NetworkClient();

    await aliceClient.ConnectAsync(alice);

    var groupKey = RandomNumberGenerator.GetBytes(32);
    var groupId = Guid.NewGuid().ToString("N");
    var recipients = new List<string> { alice.UserId, bob.UserId };
    var message = new Message {
        Id = Guid.NewGuid().ToString("N"),
        ConversationId = groupId,
        SenderId = alice.UserId,
        Content = "offline group hello",
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        SeqNum = 1,
        ConvType = ConversationType.Group
    };
    var payload = Crypto.EncryptGroup(groupKey, message);
    var sig = Crypto.SignPayload(alice.SignPrivKey, payload, message.SeqNum);
    var authToken = Crypto.ComputeGroupAuthToken(groupKey, groupId);

    Ensure(await aliceClient.SendGroupAsync(groupId, recipients, payload, sig, message.SeqNum, authToken), "offline group send failed");

    var received = new TaskCompletionSource<(string groupId, string senderId, string payload, string sig, long seq, long ts)>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    bobClient.OnGroupMessage += (gid, senderId, body, bodySig, seq, ts) => received.TrySetResult((gid, senderId, body, bodySig, seq, ts));

    await bobClient.ConnectAsync(bob);

    var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var decrypted = Crypto.DecryptGroup(groupKey, envelope.groupId, envelope.senderId, envelope.payload);

    Ensure(decrypted != null && decrypted.Content == "offline group hello", "offline group message did not arrive intact");
    Ensure(await bobClient.AckGroupAsync(groupId, alice.UserId, envelope.seq), "offline group ack failed");
}

static async Task AckedOfflineMessagesDoNotReplayAsync(string serverUrl) {
    var alice = CreateUser(serverUrl, "alice");
    var bob = CreateUser(serverUrl, "bob");
    await using var aliceClient = new NetworkClient();
    await using var firstBobClient = new NetworkClient();

    await aliceClient.ConnectAsync(alice);

    var convId = ConversationId(alice, bob);
    var queued = CreateDirectMessage(alice, bob, convId, "ack me once", 1);
    Ensure(await aliceClient.SendDmAsync(bob.UserId, queued.payload, queued.sig, queued.message.SeqNum), "offline send failed");

    var firstReceive = new TaskCompletionSource<(string senderId, string payload, string sig, long seq, long ts)>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    firstBobClient.OnMessage += (senderId, payload, sig, seq, ts) => firstReceive.TrySetResult((senderId, payload, sig, seq, ts));
    await firstBobClient.ConnectAsync(bob);

    var delivered = await firstReceive.Task.WaitAsync(TimeSpan.FromSeconds(10));
    Ensure(await firstBobClient.AckDmAsync(alice.UserId, delivered.seq, convId), "ack after offline delivery failed");
    await firstBobClient.DisposeAsync();

    await using var secondBobClient = new NetworkClient();
    var replayed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    secondBobClient.OnMessage += (_, _, _, _, _) => replayed.TrySetResult(true);
    await secondBobClient.ConnectAsync(bob);

    var replayTask = await Task.WhenAny(replayed.Task, Task.Delay(TimeSpan.FromSeconds(3)));
    Ensure(replayTask != replayed.Task, "acked offline message replayed on reconnect");
}

static async Task ReplayRejectionAsync(string serverUrl) {
    var alice = CreateUser(serverUrl, "alice");
    var bob = CreateUser(serverUrl, "bob");
    await using var aliceClient = new NetworkClient();
    await using var bobClient = new NetworkClient();

    await aliceClient.ConnectAsync(alice);
    await bobClient.ConnectAsync(bob);

    var convId = ConversationId(alice, bob);
    var direct = CreateDirectMessage(alice, bob, convId, "once only", 1);

    Ensure(await aliceClient.SendDmAsync(bob.UserId, direct.payload, direct.sig, direct.message.SeqNum), "initial send failed");
    Ensure(!await aliceClient.SendDmAsync(bob.UserId, direct.payload, direct.sig, direct.message.SeqNum), "replay send should have been rejected");
}

static async Task GroupReplayRejectionAsync(string serverUrl) {
    var alice = CreateUser(serverUrl, "alice");
    var bob = CreateUser(serverUrl, "bob");
    await using var aliceClient = new NetworkClient();
    await using var bobClient = new NetworkClient();

    await aliceClient.ConnectAsync(alice);
    await bobClient.ConnectAsync(bob);

    var groupKey = RandomNumberGenerator.GetBytes(32);
    var groupId = Guid.NewGuid().ToString("N");
    var message = new Message {
        Id = Guid.NewGuid().ToString("N"),
        ConversationId = groupId,
        SenderId = alice.UserId,
        Content = "group hello",
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        SeqNum = 1,
        ConvType = ConversationType.Group
    };
    var payload = Crypto.EncryptGroup(groupKey, message);
    var sig = Crypto.SignPayload(alice.SignPrivKey, payload, message.SeqNum);
    var authToken = Crypto.ComputeGroupAuthToken(groupKey, groupId);
    var recipients = new List<string> { alice.UserId, bob.UserId };

    var received = new TaskCompletionSource<(string groupId, string senderId, string payload, string sig, long seq, long ts)>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    bobClient.OnGroupMessage += (gid, senderId, body, bodySig, seq, ts) => received.TrySetResult((gid, senderId, body, bodySig, seq, ts));

    Ensure(await aliceClient.SendGroupAsync(groupId, recipients, payload, sig, message.SeqNum, authToken), "initial group send failed");
    var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
    var decrypted = Crypto.DecryptGroup(groupKey, groupId, alice.UserId, envelope.payload);

    Ensure(decrypted != null && decrypted.Content == "group hello", "group payload did not decrypt");
    Ensure(await bobClient.AckGroupAsync(groupId, alice.UserId, envelope.seq), "group ack failed");
    Ensure(!await aliceClient.SendGroupAsync(groupId, recipients, payload, sig, message.SeqNum, authToken), "group replay should have been rejected");
}

static async Task KeyLookupAsync(string serverUrl) {
    var alice = CreateUser(serverUrl, "alice");
    var bob = CreateUser(serverUrl, "bob");
    await using var aliceClient = new NetworkClient();
    await using var bobClient = new NetworkClient();

    await aliceClient.ConnectAsync(alice);
    await bobClient.ConnectAsync(bob);

    var keys = await aliceClient.GetUserKeysAsync(bob.UserId);
    Ensure(keys != null, "key lookup returned null");
    Ensure(Convert.ToBase64String(keys!.Value.signPub) == Convert.ToBase64String(bob.SignPubKey), "signing key lookup mismatch");
    Ensure(Convert.ToBase64String(keys.Value.dhPub) == Convert.ToBase64String(bob.DhPubKey), "DH key lookup mismatch");
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

static string ConversationId(LocalUser a, LocalUser b) {
    var ids = new[] { a.UserId, b.UserId }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
    return $"dm:{ids[0]}:{ids[1]}";
}

static (Message message, string payload, string sig) CreateDirectMessage(LocalUser sender, LocalUser recipient, string conversationId, string content, long seqNum) {
    var convKey = Crypto.DeriveSharedSecret(sender.DhPrivKey, recipient.DhPubKey, conversationId);
    var message = new Message {
        Id = Guid.NewGuid().ToString("N"),
        ConversationId = conversationId,
        SenderId = sender.UserId,
        Content = content,
        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        SeqNum = seqNum,
        ConvType = ConversationType.Direct
    };
    var payload = Crypto.EncryptDm(convKey, message);
    var sig = Crypto.SignPayload(sender.SignPrivKey, payload, message.SeqNum);
    return (message, payload, sig);
}

static void Ensure(bool condition, string message) {
    if (!condition) {
        throw new InvalidOperationException(message);
    }
}

using System.Net;
using System.Security.Cryptography;
using Cipher;

namespace Cipher.Tests;

[Collection("relay")]
public class RelayIntegrationTests {
    readonly LocalRelayFixture _relay;

    public RelayIntegrationTests(LocalRelayFixture relay) {
        _relay = relay;
    }

    [Fact]
    public async Task HealthAndRootEndpointsWork() {
        using var client = new HttpClient();

        var health = await client.GetAsync($"{_relay.BaseUrl}/health");
        var rootHead = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"{_relay.BaseUrl}/"));
        var rootText = await client.GetStringAsync($"{_relay.BaseUrl}/");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rootHead.StatusCode);
        Assert.Equal("CIPHER RELAY ONLINE", rootText);
    }

    [Fact]
    public async Task DirectMessageRoundTripWorksWithActualNetworkClients() {
        var alice = CreateUser("alice");
        var bob = CreateUser("bob");
        await using var aliceClient = new NetworkClient();
        await using var bobClient = new NetworkClient();

        var received = new TaskCompletionSource<(string senderId, string payload, string sig, long seq, long ts)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        bobClient.OnMessage += (senderId, payload, sig, seq, ts) =>
            received.TrySetResult((senderId, payload, sig, seq, ts));

        await aliceClient.ConnectAsync(alice);
        await bobClient.ConnectAsync(bob);

        var convId = ConversationId(alice, bob);
        var message = CreateDirectMessage(alice, bob, convId, "hello bob", 1);

        Assert.True(await aliceClient.SendDmAsync(bob.UserId, message.payload, message.sig, message.message.SeqNum));

        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var convKey = Crypto.DeriveSharedSecret(bob.DhPrivKey, alice.DhPubKey, convId);
        var decrypted = Crypto.DecryptDm(convKey, alice.UserId, envelope.payload);

        Assert.NotNull(decrypted);
        Assert.Equal("hello bob", decrypted!.Content);
        Assert.Equal(alice.UserId, envelope.senderId);
        Assert.True(Crypto.Verify(alice.SignPubKey, $"{envelope.payload}:{envelope.seq}", envelope.sig));
    }

    [Fact]
    public async Task OfflineDirectMessageIsDeliveredAfterRecipientConnects() {
        var alice = CreateUser("alice");
        var bob = CreateUser("bob");
        await using var aliceClient = new NetworkClient();
        await using var bobClient = new NetworkClient();

        await aliceClient.ConnectAsync(alice);

        var convId = ConversationId(alice, bob);
        var queued = CreateDirectMessage(alice, bob, convId, "offline hello", 1);
        Assert.True(await aliceClient.SendDmAsync(bob.UserId, queued.payload, queued.sig, queued.message.SeqNum));

        var received = new TaskCompletionSource<(string senderId, string payload, string sig, long seq, long ts)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bobClient.OnMessage += (senderId, payload, sig, seq, ts) =>
            received.TrySetResult((senderId, payload, sig, seq, ts));

        await bobClient.ConnectAsync(bob);

        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var convKey = Crypto.DeriveSharedSecret(bob.DhPrivKey, alice.DhPubKey, convId);
        var decrypted = Crypto.DecryptDm(convKey, alice.UserId, envelope.payload);

        Assert.NotNull(decrypted);
        Assert.Equal("offline hello", decrypted!.Content);
    }

    [Fact]
    public async Task ReplayDirectMessageIsRejected() {
        var alice = CreateUser("alice");
        var bob = CreateUser("bob");
        await using var aliceClient = new NetworkClient();
        await using var bobClient = new NetworkClient();

        await aliceClient.ConnectAsync(alice);
        await bobClient.ConnectAsync(bob);

        var convId = ConversationId(alice, bob);
        var direct = CreateDirectMessage(alice, bob, convId, "once only", 1);

        Assert.True(await aliceClient.SendDmAsync(bob.UserId, direct.payload, direct.sig, direct.message.SeqNum));
        Assert.False(await aliceClient.SendDmAsync(bob.UserId, direct.payload, direct.sig, direct.message.SeqNum));
    }

    [Fact]
    public async Task GroupReplayIsRejected() {
        var alice = CreateUser("alice");
        var bob = CreateUser("bob");
        await using var aliceClient = new NetworkClient();
        await using var bobClient = new NetworkClient();

        await aliceClient.ConnectAsync(alice);
        await bobClient.ConnectAsync(bob);

        var groupKey = RandomNumberGenerator.GetBytes(32);
        var groupId = Guid.NewGuid().ToString("N");
        var groupMessage = new Message {
            Id = Guid.NewGuid().ToString("N"),
            ConversationId = groupId,
            SenderId = alice.UserId,
            Content = "group hello",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SeqNum = 1,
            ConvType = ConversationType.Group
        };
        var payload = Crypto.EncryptGroup(groupKey, groupMessage);
        var sig = Crypto.SignPayload(alice.SignPrivKey, payload, groupMessage.SeqNum);

        var received = new TaskCompletionSource<(string groupId, string senderId, string payload, string sig, long seq, long ts)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        bobClient.OnGroupMessage += (gid, senderId, body, bodySig, seq, ts) =>
            received.TrySetResult((gid, senderId, body, bodySig, seq, ts));

        var recipients = new List<string> { alice.UserId, bob.UserId };

        Assert.True(await aliceClient.SendGroupAsync(groupId, recipients, payload, sig, groupMessage.SeqNum));
        var envelope = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(groupId, envelope.groupId);

        var decrypted = Crypto.DecryptGroup(groupKey, groupId, alice.UserId, envelope.payload);
        Assert.NotNull(decrypted);
        Assert.Equal("group hello", decrypted!.Content);

        Assert.False(await aliceClient.SendGroupAsync(groupId, recipients, payload, sig, groupMessage.SeqNum));
    }

    [Fact]
    public async Task RegisteredUserKeysCanBeLookedUp() {
        var alice = CreateUser("alice");
        var bob = CreateUser("bob");
        await using var aliceClient = new NetworkClient();
        await using var bobClient = new NetworkClient();

        await aliceClient.ConnectAsync(alice);
        await bobClient.ConnectAsync(bob);

        var keys = await aliceClient.GetUserKeysAsync(bob.UserId);

        Assert.NotNull(keys);
        Assert.Equal(Convert.ToBase64String(bob.SignPubKey), Convert.ToBase64String(keys!.Value.signPub));
        Assert.Equal(Convert.ToBase64String(bob.DhPubKey), Convert.ToBase64String(keys.Value.dhPub));
    }

    [Fact]
    public async Task RegisteredUserProfileIncludesPublicDisplayName() {
        var alice = CreateUser("alice");
        var bob = CreateUser("Bob Builder");
        await using var aliceClient = new NetworkClient();
        await using var bobClient = new NetworkClient();

        await aliceClient.ConnectAsync(alice);
        await bobClient.ConnectAsync(bob);

        var profile = await aliceClient.GetUserProfileAsync(bob.UserId);

        Assert.NotNull(profile);
        Assert.Equal("Bob Builder", profile!.Value.DisplayName);
        Assert.Equal(Convert.ToBase64String(bob.SignPubKey), Convert.ToBase64String(profile.Value.SignPubKey));
        Assert.Equal(Convert.ToBase64String(bob.DhPubKey), Convert.ToBase64String(profile.Value.DhPubKey));
    }

    LocalUser CreateUser(string displayName) {
        var (signPriv, signPub) = Crypto.GenerateSigningKeys();
        var (dhPriv, dhPub) = Crypto.GenerateDhKeys();

        return new LocalUser {
            UserId = Crypto.DeriveUserId(signPub),
            DisplayName = displayName,
            SignPrivKey = signPriv,
            SignPubKey = signPub,
            DhPrivKey = dhPriv,
            DhPubKey = dhPub,
            ServerUrl = _relay.BaseUrl,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
    }

    static string ConversationId(LocalUser a, LocalUser b) {
        var ids = new[] { a.UserId, b.UserId }.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        return $"dm:{ids[0]}:{ids[1]}";
    }

    static (Message message, string payload, string sig) CreateDirectMessage(
        LocalUser sender,
        LocalUser recipient,
        string conversationId,
        string content,
        long seqNum) {
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
}

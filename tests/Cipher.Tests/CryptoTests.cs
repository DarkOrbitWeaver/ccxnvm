using Cipher;
using System.Security.Cryptography;
using System.Text.Json;

namespace Cipher.Tests;

public class CryptoTests {
    [Fact]
    public void RegistrationSignatureBindsDhPublicKey() {
        var (signPriv, signPub) = Crypto.GenerateSigningKeys();
        var (_, dhPub) = Crypto.GenerateDhKeys();
        var (_, otherDhPub) = Crypto.GenerateDhKeys();

        var userId = Crypto.DeriveUserId(signPub);
        var dhPubKey = Convert.ToBase64String(dhPub);
        var otherDhPubKey = Convert.ToBase64String(otherDhPub);
        var sig = Crypto.SignRegistration(signPriv, userId, dhPubKey);

        Assert.True(Crypto.Verify(signPub, $"{userId}:{dhPubKey}", sig));
        Assert.False(Crypto.Verify(signPub, $"{userId}:{otherDhPubKey}", sig));
    }

    [Fact]
    public void VaultFieldTamperingIsRejected() {
        var key = RandomNumberGenerator.GetBytes(32);
        var packed = Crypto.EncryptField(key, RandomNumberGenerator.GetBytes(64));
        packed[^1] ^= 0xFF;

        Assert.Null(Crypto.DecryptField(key, packed));
    }

    [Fact]
    public void MessagePaddingHidesExactLengthInsideBucket() {
        var key = RandomNumberGenerator.GetBytes(32);
        var shortMsg = new Message {
            Id = "same-id",
            Content = "a",
            SeqNum = 1,
            Timestamp = 1
        };
        var longerMsg = new Message {
            Id = "same-id",
            Content = new string('b', 120),
            SeqNum = 1,
            Timestamp = 1
        };

        var shortPayload = Crypto.EncryptDm(key, shortMsg);
        var longerPayload = Crypto.EncryptDm(key, longerMsg);
        var shortCtLength = JsonDocument.Parse(shortPayload).RootElement.GetProperty("ct").GetString()!.Length;
        var longerCtLength = JsonDocument.Parse(longerPayload).RootElement.GetProperty("ct").GetString()!.Length;

        Assert.Equal(shortCtLength, longerCtLength);
        Assert.Equal("a", Crypto.DecryptDm(key, "bob", shortPayload)!.Content);
        Assert.Equal(new string('b', 120), Crypto.DecryptDm(key, "bob", longerPayload)!.Content);
    }

    [Fact]
    public void EmojiContentRoundTripsWithoutCorruption() {
        var key = RandomNumberGenerator.GetBytes(32);
        var source = "hello 😊✨🌸🚀";
        var message = new Message {
            Id = "emoji-case",
            Content = source,
            SeqNum = 42,
            Timestamp = 100
        };

        var payload = Crypto.EncryptDm(key, message);
        var decrypted = Crypto.DecryptDm(key, "alice", payload);

        Assert.NotNull(decrypted);
        Assert.Equal(source, decrypted!.Content);
    }
    [Fact]
    public void DecryptDmRejectsMalformedOrWrongTypeWireMessages() {
        var key = RandomNumberGenerator.GetBytes(32);

        Assert.Null(Crypto.DecryptDm(key, "alice", """{"seq":1,"ts":0,"type":"dm"}"""));
        Assert.Null(Crypto.DecryptDm(key, "alice", """{"id":"x","ct":"AA==","nonce":"AA==","tag":"AA==","seq":1,"ts":0,"type":"grp"}"""));
    }

    [Fact]
    public void DecryptGroupRejectsDirectWireMessages() {
        var key = RandomNumberGenerator.GetBytes(32);
        var message = new Message {
            Id = "wire-type",
            Content = "hello",
            SeqNum = 5,
            Timestamp = 10
        };

        var payload = Crypto.EncryptDm(key, message);

        Assert.Null(Crypto.DecryptGroup(key, "grp-1", "alice", payload));
    }

    [Fact]
    public void DmWireUsesV2AndRejectsLegacyV1Payload() {
        var key = RandomNumberGenerator.GetBytes(32);
        var message = new Message {
            Id = "v2-case",
            ConversationId = "dm:a:b",
            Content = "hello",
            SeqNum = 5,
            Timestamp = 10
        };

        var payload = Crypto.EncryptDm(key, message);
        using var doc = JsonDocument.Parse(payload);
        Assert.Equal(2, doc.RootElement.GetProperty("v").GetInt32());
        Assert.Equal("dm", doc.RootElement.GetProperty("mt").GetString());
        Assert.Equal("dm:a:b", doc.RootElement.GetProperty("sid").GetString());

        var legacyPayload = """{"id":"x","ct":"AA==","nonce":"AA==","tag":"AA==","seq":1,"ts":0,"type":"dm"}""";
        Assert.Null(Crypto.DecryptDm(key, "alice", legacyPayload));
    }
}

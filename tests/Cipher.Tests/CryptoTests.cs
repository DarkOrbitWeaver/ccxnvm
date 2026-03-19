using Cipher;
using System.Security.Cryptography;

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
}

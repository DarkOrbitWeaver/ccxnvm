using Cipher;

namespace Cipher.Tests;

public class RelayUrlTests {
    [Theory]
    [InlineData("https://cipher-relay.onrender.com", true)]
    [InlineData("http://localhost:5000", true)]
    [InlineData("http://127.0.0.1:5000", true)]
    [InlineData("http://[::1]:5000", true)]
    [InlineData("http://example.com", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("not-a-url", false)]
    public void ValidatesRelayUrls(string url, bool expected) {
        Assert.Equal(expected, RelayUrl.IsValid(url));
    }

    [Fact]
    public void NormalizeFallsBackToDefaultRelay() {
        Assert.Equal(AppBranding.DefaultRelayUrl, RelayUrl.Normalize("   "));
    }
}

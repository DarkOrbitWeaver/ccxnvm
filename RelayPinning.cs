namespace Cipher;

/// <summary>
/// TLS certificate pinning for the relay server.
///
/// HOW TO GET YOUR RELAY'S THUMBPRINT:
///
///   PowerShell (Windows):
///     $req = [System.Net.WebRequest]::Create("https://yourrelay.com")
///     $req.GetResponse() | Out-Null
///     $cert = [System.Security.Cryptography.X509Certificates.X509Certificate2] $req.ServicePoint.Certificate
///     $cert.GetCertHashString("SHA256")
///
///   OpenSSL (Linux / Mac / WSL):
///     openssl s_client -connect yourrelay.com:443 </dev/null 2>/dev/null \
///       | openssl x509 -fingerprint -sha256 -noout
///     Result looks like: SHA256 Fingerprint=AA:BB:CC:DD:...
///     Remove all colons to get the value below.
///
/// ROTATION WARNING:
///   TLS certs typically renew annually. You MUST update PinnedThumbprint and
///   ship an app update BEFORE the old cert expires — otherwise every client loses
///   connectivity the moment the cert rotates.
///   Recommended workflow:
///     1. Obtain the new cert's thumbprint a few weeks before expiry.
///     2. Ship an app update with the new thumbprint.
///     3. Wait for users to update (or force-update).
///     4. Let the cert rotate.
///
/// TO DISABLE PINNING:
///   Set PinnedThumbprint = null.
///   Safe to do for self-hosted relays, development, or custom relay URLs
///   (pinning only makes sense when the relay host is fixed and controlled by you).
/// </summary>
public static class RelayPinning {
    /// <summary>
    /// SHA-256 thumbprint of the relay server's TLS leaf certificate.
    /// Hex-encoded, no colons, case-insensitive. Must be exactly 64 hex characters.
    /// Set to null to disable pinning entirely.
    ///
    /// Example value (not real — replace with yours):
    ///   "3A7F2B9C1D4E6F8A0B2C4D6E8F0A1B2C3D4E5F6A7B8C9D0E1F2A3B4C5D6E7F8A"
    /// </summary>
    public const string? PinnedThumbprint = null;

    public const string RelayThumbprintEnvVar = "CIPHER_RELAY_PINNED_CERT_SHA256";

    public static string? ResolvePinnedThumbprint(string relayUrl) {
        // Only pin the official fixed relay host. Custom relays should use PKI trust.
        if (!string.Equals(
                AppBranding.ResolveRelayUrl(null),
                AppBranding.ResolveRelayUrl(relayUrl),
                StringComparison.OrdinalIgnoreCase)) {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(PinnedThumbprint)) {
            return PinnedThumbprint;
        }

        var fromEnv = Environment.GetEnvironmentVariable(RelayThumbprintEnvVar)?.Trim();
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }
}

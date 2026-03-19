using System.Net;

namespace Cipher;

public static class RelayUrl {
    public const string ValidationHint = "enter a valid https relay url (http is allowed only for localhost)";

    public static string Normalize(string? serverUrl) {
        var trimmed = serverUrl?.Trim() ?? "";
        return string.IsNullOrEmpty(trimmed) ? AppBranding.DefaultRelayUrl : trimmed.TrimEnd('/');
    }

    public static bool IsValid(string serverUrl) {
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri)) {
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps) {
            return true;
        }

        return uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri.Host);
    }

    static bool IsLoopbackHost(string host) {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }

        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }
}

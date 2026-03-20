using System.Diagnostics;

namespace Cipher;

public static class AppTelemetry {
    public static string DescribeRelay(string? relayUrl) {
        if (string.IsNullOrWhiteSpace(relayUrl)) return "default-relay";
        if (!Uri.TryCreate(relayUrl, UriKind.Absolute, out var uri)) return relayUrl!;

        var host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
        var path = string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/"
            ? ""
            : uri.AbsolutePath;
        return $"{uri.Scheme}://{host}{path}";
    }

    public static string DescribeException(Exception ex) {
        var summary = $"{ex.GetType().Name}: {ex.Message}";
        return ex.InnerException == null
            ? summary
            : $"{summary} | inner={ex.InnerException.GetType().Name}: {ex.InnerException.Message}";
    }

    public static string MaskUserId(string? userId) {
        if (string.IsNullOrWhiteSpace(userId)) return "unknown";
        var trimmed = userId.Trim();
        return trimmed.Length <= 10 ? trimmed : $"{trimmed[..6]}...{trimmed[^4..]}";
    }

    public static string DescribeConversation(string conversationId, bool isGroup) =>
        isGroup ? $"group:{MaskUserId(conversationId)}" : $"dm:{MaskUserId(conversationId)}";

    public static long StartTimer() => Stopwatch.GetTimestamp();

    public static long ElapsedMilliseconds(long startTimestamp) =>
        (long)(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
}

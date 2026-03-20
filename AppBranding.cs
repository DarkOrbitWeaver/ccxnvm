using System.Reflection;

namespace Cipher;

public static class AppBranding {
    static readonly Assembly Assembly = typeof(AppBranding).Assembly;

    public const string DefaultRelayUrl = "https://cipher-relay.onrender.com";
    public const string GitHubRepoUrl = "https://github.com/DarkOrbitWeaver/ccxnvm";

    public static string ResolveRelayUrl(string? url) {
        var trimmed = url?.Trim().TrimEnd('/') ?? "";
        return string.IsNullOrWhiteSpace(trimmed) ? DefaultRelayUrl : trimmed;
    }

    public static string ProductName =>
        Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Cipher";

    public static string CompanyName =>
        Assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company ?? "DarkOrbitWeaver";

    public static string WindowTitle => ProductName.ToUpperInvariant();

    public static string AppDataFolder => ProductName;

    public static string VaultStorageHint =>
        $"vault stored in %APPDATA%\\{AppDataFolder} - no cloud, no servers, just you";
}

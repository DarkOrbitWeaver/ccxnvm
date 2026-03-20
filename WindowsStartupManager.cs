using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Cipher;

public sealed record WindowsStartupRegistration(
    bool IsEnabled,
    bool StartHidden,
    int StartupDelaySeconds,
    string Command
);

public static class WindowsStartupManager {
    const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static readonly Regex DelayRegex = new(@"--startup-delay-ms(?:=|\s+)(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string ValueName => $"{AppBranding.CompanyName}.{AppBranding.ProductName}";

    public static WindowsStartupRegistration ReadStatus() {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        var command = key?.GetValue(ValueName) as string ?? "";
        if (string.IsNullOrWhiteSpace(command)) {
            return new WindowsStartupRegistration(false, false, 0, "");
        }

        var startHidden = command.Contains("--start-hidden", StringComparison.OrdinalIgnoreCase);
        var delayMs = 0;
        var match = DelayRegex.Match(command);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var parsed)) {
            delayMs = Math.Max(0, parsed);
        }

        return new WindowsStartupRegistration(
            true,
            startHidden,
            delayMs / 1000,
            command);
    }

    public static void Apply(AppShellPreferences preferences) {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("could not open the Windows startup registry key");

        if (!preferences.StartWithWindows) {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(ValueName, BuildCommand(preferences), RegistryValueKind.String);
    }

    static string BuildCommand(AppShellPreferences preferences) {
        var exePath = AppInfo.CurrentExecutablePath;
        if (string.IsNullOrWhiteSpace(exePath)) {
            throw new InvalidOperationException("could not determine the current executable path for Windows startup");
        }

        var parts = new List<string> { Quote(exePath) };
        if (preferences.StartHiddenOnStartup) {
            parts.Add("--start-hidden");
        }

        if (preferences.StartupDelaySeconds > 0) {
            parts.Add($"--startup-delay-ms={preferences.StartupDelaySeconds * 1000}");
        }

        return string.Join(" ", parts);
    }

    static string Quote(string value) => $"\"{value}\"";
}

using System.IO;
using System.Text;
using System.Text.Json;

namespace Cipher;

public sealed record AppShellPreferences(
    bool CloseToTrayOnClose = true,
    bool StartWithWindows = true,
    bool StartHiddenOnStartup = true,
    int StartupDelaySeconds = 60,
    string ThemeFile = "Theme.Teal.xaml",
    double ChatFontSize = 17d
);

public static class AppShellPreferencesStore {
    static readonly object Gate = new();
    static readonly JsonSerializerOptions JsonOptions = new() {
        WriteIndented = true
    };

    public static string FilePath =>
        Path.Combine(AppPaths.AppDataRoot, "shell-preferences.json");

    public static bool Exists() {
        lock (Gate) {
            return File.Exists(FilePath);
        }
    }

    public static AppShellPreferences Load() {
        lock (Gate) {
            try {
                AppPaths.EnsureCreated();
                if (!File.Exists(FilePath)) return new AppShellPreferences();

                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<AppShellPreferences>(json);
                return Sanitize(loaded ?? new AppShellPreferences());
            } catch (Exception ex) {
                AppLog.Warn("prefs", $"failed to load shell preferences: {ex.Message}");
                return new AppShellPreferences();
            }
        }
    }

    public static void Save(AppShellPreferences preferences) {
        lock (Gate) {
            AppPaths.EnsureCreated();
            var sanitized = Sanitize(preferences);
            var tempPath = FilePath + ".tmp";
            var json = JsonSerializer.Serialize(sanitized, JsonOptions);
            File.WriteAllText(tempPath, json, Encoding.UTF8);
            File.Move(tempPath, FilePath, overwrite: true);
        }
    }

    public static AppShellPreferences Sanitize(AppShellPreferences preferences) {
        var delay = preferences.StartupDelaySeconds;
        if (delay < 0) delay = 0;
        if (delay is > 0 and <= 15) delay = 15;
        else if (delay is > 15 and <= 30) delay = 30;
        else if (delay is > 30 and <= 60) delay = 60;
        else if (delay > 60) delay = 60;

        var themeFile = string.IsNullOrWhiteSpace(preferences.ThemeFile)
            ? "Theme.Teal.xaml"
            : preferences.ThemeFile;

        var chatFontSize = preferences.ChatFontSize;
        if (chatFontSize <= 15.5) chatFontSize = 15d;
        else if (chatFontSize <= 18d) chatFontSize = 17d;
        else chatFontSize = 19d;

        return preferences with {
            StartupDelaySeconds = delay,
            ThemeFile = themeFile,
            ChatFontSize = chatFontSize
        };
    }
}

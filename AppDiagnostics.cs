using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Cipher;

public static class AppPaths {
    public static string AppDataRoot =>
        AppRuntime.AppDataRootOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppBranding.AppDataFolder);

    public static string LogsDir => Path.Combine(AppDataRoot, "logs");
    public static string BackupsDir => Path.Combine(AppDataRoot, "backups");
    public static string ExportsDir => Path.Combine(AppDataRoot, "exports");

    public static void EnsureCreated() {
        Directory.CreateDirectory(AppDataRoot);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(ExportsDir);
    }
}

public static class AppInfo {
    static readonly Assembly Assembly = typeof(AppInfo).Assembly;

    public static string DisplayVersion {
        get {
            var informational = Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion?
                .Split('+')[0];
            if (!string.IsNullOrWhiteSpace(informational)) return informational!;

            var version = Assembly.GetName().Version;
            if (version == null) return "0.0.0";
            return $"{version.Major}.{Math.Max(0, version.Minor)}.{Math.Max(0, version.Build)}";
        }
    }

    public static string CurrentExecutablePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
}

public static class AppLog {
    const int MaxLogFiles = 14;
    const long MaxLogBytes = 512 * 1024;
    static readonly object Gate = new();
    static bool _initialized;

    static string ActiveLogPath =>
        Path.Combine(AppPaths.LogsDir, $"cipher-{DateTime.UtcNow:yyyyMMdd}.log");

    public static void Initialize() {
        lock (Gate) {
            if (_initialized) return;
            AppPaths.EnsureCreated();
            _initialized = true;
            PruneLogs();
            WriteCore("INFO", "app", $"starting {AppBranding.ProductName} v{AppInfo.DisplayVersion}");
        }
    }

    public static void Info(string area, string message) => Write("INFO", area, message);
    public static void Warn(string area, string message) => Write("WARN", area, message);
    public static void Error(string area, string message, Exception? ex = null) =>
        Write("ERROR", area, message, ex);

    public static IReadOnlyList<string> ListLogFiles() {
        AppPaths.EnsureCreated();
        return Directory.GetFiles(AppPaths.LogsDir, "*.log")
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string CreateDiagnosticsBundle(string summary) {
        AppPaths.EnsureCreated();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(AppPaths.ExportsDir, $"cipher-diagnostics-{stamp}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var summaryEntry = archive.CreateEntry("summary.txt");
        using (var writer = new StreamWriter(summaryEntry.Open(), Encoding.UTF8)) {
            writer.WriteLine($"{AppBranding.ProductName} diagnostics");
            writer.WriteLine($"version: {AppInfo.DisplayVersion}");
            writer.WriteLine($"created_utc: {DateTime.UtcNow:O}");
            writer.WriteLine();
            writer.WriteLine(summary);
        }

        foreach (var logPath in ListLogFiles().Take(10)) {
            archive.CreateEntryFromFile(logPath, Path.Combine("logs", Path.GetFileName(logPath)));
        }

        return zipPath;
    }

    static void Write(string level, string area, string message, Exception? ex = null) {
        Initialize();
        lock (Gate) {
            RotateIfNeeded(ActiveLogPath);
            WriteCore(level, area, message, ex);
        }
    }

    static void WriteCore(string level, string area, string message, Exception? ex = null) {
        var line = new StringBuilder()
            .Append('[').Append(DateTime.UtcNow.ToString("O")).Append("] ")
            .Append(level).Append(' ')
            .Append('[').Append(area).Append("] ")
            .Append(message);

        if (ex != null) {
            line.AppendLine();
            line.Append(ex);
        }

        File.AppendAllText(ActiveLogPath, line.AppendLine().ToString(), Encoding.UTF8);
    }

    static void RotateIfNeeded(string path) {
        if (!File.Exists(path)) return;
        var info = new FileInfo(path);
        if (info.Length < MaxLogBytes) return;

        var rotatedPath = Path.Combine(
            AppPaths.LogsDir,
            $"{Path.GetFileNameWithoutExtension(path)}-{DateTime.UtcNow:HHmmss}.log");
        File.Move(path, rotatedPath, overwrite: true);
        PruneLogs();
    }

    static void PruneLogs() {
        var logs = Directory.GetFiles(AppPaths.LogsDir, "*.log")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Skip(MaxLogFiles)
            .ToList();

        foreach (var file in logs) {
            try {
                file.Delete();
            } catch {
            }
        }
    }
}

public sealed record StartupHealthReport(
    bool IsHealthy,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors
);

public static class StartupHealth {
    public static StartupHealthReport Run() {
        AppPaths.EnsureCreated();

        var warnings = new List<string>();
        var errors = new List<string>();

        try {
            var probePath = Path.Combine(AppPaths.AppDataRoot, ".write-test");
            File.WriteAllText(probePath, "ok", Encoding.UTF8);
            File.Delete(probePath);
        } catch (Exception ex) {
            errors.Add($"app-data folder is not writable: {ex.Message}");
        }

        if (!RelayUrl.IsValid(AppRuntime.EffectiveDefaultRelayUrl)) {
            errors.Add("default relay URL is invalid.");
        }

        if (Session.HasSession()) {
            var sessionKey = Session.TryLoad();
            if (sessionKey == null) warnings.Add("saved session exists but could not be decrypted.");
            else Crypto.Wipe(sessionKey);
        }

        if (File.Exists(Vault.DefaultVaultPath)) {
            try {
                using var db = new SqliteConnection($"Data Source={Vault.DefaultVaultPath};Mode=ReadOnly;");
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = "PRAGMA quick_check;";
                var result = Convert.ToString(cmd.ExecuteScalar()) ?? "";
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)) {
                    errors.Add($"vault quick_check reported: {result}");
                }
            } catch (Exception ex) {
                errors.Add($"vault file could not be opened safely: {ex.Message}");
            }
        }

        return new StartupHealthReport(errors.Count == 0, warnings, errors);
    }
}

public static class FriendlyErrors {
    public static string ToUserMessage(Exception ex) => ex switch {
        TimeoutException => "the relay took too long to respond",
        OperationCanceledException => "the operation was cancelled",
        SqliteException => "local vault data could not be accessed safely",
        CryptographicException => "a local security check failed",
        _ when ex.Message.Contains("update", StringComparison.OrdinalIgnoreCase) =>
            "the update service hit a problem",
        _ => ex.Message
    };
}

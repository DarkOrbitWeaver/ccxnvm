using System.Globalization;
using System.IO;
using System.Text;

namespace Cipher;

public sealed record AppLaunchOptions(
    bool TestMode = false,
    string? AppDataRootOverride = null,
    string? RelayUrlOverride = null,
    bool DisableUpdater = false,
    bool StartHidden = false,
    string? SignalFile = null,
    string? SeedProfile = null,
    string? FaultProfile = null,
    int StartupDelayMs = 0,
    string? TestRegisterName = null,
    string? TestRegisterPassword = null,
    bool AutoRegister = false,
    bool AutoLogin = false
);

public static class AppRuntime {
    public const string TestRegisterPasswordEnvVar = "CIPHER_TEST_REGISTER_PASSWORD";
    static readonly object SignalGate = new();

    public static AppLaunchOptions Current { get; private set; } = new();

    public static bool IsTestMode => Current.TestMode;
    public static bool DisableUpdater => Current.DisableUpdater;
    public static bool StartHidden => Current.StartHidden;
    public static string? AppDataRootOverride => Current.AppDataRootOverride;
    public static string EffectiveDefaultRelayUrl =>
        AppBranding.ResolveRelayUrl(Current.RelayUrlOverride);

    public static void Configure(string[] args) {
        var testMode = false;
        string? appDataRootOverride = null;
        string? relayUrlOverride = null;
        var disableUpdater = false;
        var startHidden = false;
        string? signalFile = null;
        string? seedProfile = null;
        string? faultProfile = null;
        var startupDelayMs = 0;
        string? testRegisterName = null;
        var testRegisterPassword = Environment.GetEnvironmentVariable(TestRegisterPasswordEnvVar);
        var autoRegister = false;
        var autoLogin = false;

        for (var i = 0; i < args.Length; i++) {
            var arg = args[i];
            var (key, inlineValue) = SplitArgument(arg);

            switch (key) {
                case "--test-mode":
                    testMode = true;
                    break;
                case "--disable-updater":
                    disableUpdater = true;
                    break;
                case "--start-hidden":
                    startHidden = true;
                    break;
                case "--appdata-dir":
                    appDataRootOverride = NormalizePath(ReadValue(args, ref i, inlineValue));
                    break;
                case "--relay-url":
                    relayUrlOverride = ReadValue(args, ref i, inlineValue)?.Trim();
                    break;
                case "--signal-file":
                    signalFile = NormalizePath(ReadValue(args, ref i, inlineValue));
                    break;
                case "--seed-profile":
                    seedProfile = ReadValue(args, ref i, inlineValue)?.Trim();
                    break;
                case "--fault-profile":
                    faultProfile = ReadValue(args, ref i, inlineValue)?.Trim();
                    break;
                case "--startup-delay-ms":
                    var value = ReadValue(args, ref i, inlineValue);
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                        parsed > 0) {
                        startupDelayMs = parsed;
                    }
                    break;
                case "--test-register-name":
                    testRegisterName = ReadValue(args, ref i, inlineValue)?.Trim();
                    break;
                // "--test-register-password" CLI arg intentionally removed.
                // Passwords in process args are visible to all users via WMI / Task Manager.
                // Use the CIPHER_TEST_REGISTER_PASSWORD environment variable instead.
                case "--test-auto-register":
                    autoRegister = true;
                    break;
                case "--test-auto-login":
                    autoLogin = true;
                    break;
            }
        }

        Current = new AppLaunchOptions(
            testMode,
            appDataRootOverride,
            relayUrlOverride,
            disableUpdater,
            startHidden,
            signalFile,
            seedProfile,
            faultProfile,
            startupDelayMs,
            testRegisterName,
            testRegisterPassword,
            autoRegister,
            autoLogin);

        if (!string.IsNullOrWhiteSpace(Current.SignalFile)) {
            Directory.CreateDirectory(Path.GetDirectoryName(Current.SignalFile!)!);
        }

#if !DEBUG
        if (!string.IsNullOrWhiteSpace(Current.RelayUrlOverride)) {
            AppLog.Warn("runtime", "RelayUrlOverride was set in a release build and has been ignored");
            Current = Current with { RelayUrlOverride = null };
        }
        // In release builds, test credentials must never be active.
        // Clear them defensively even if somehow set via environment variable.
        if (!string.IsNullOrWhiteSpace(Current.TestRegisterPassword)) {
            AppLog.Warn("runtime", "TestRegisterPassword was set in a release build — cleared for safety");
            Current = Current with { TestRegisterPassword = null };
        }
        if (Current.AutoRegister || Current.AutoLogin) {
            AppLog.Warn("runtime", "AutoRegister/AutoLogin flags are not permitted in release builds — cleared");
            Current = Current with { AutoRegister = false, AutoLogin = false };
        }
#endif
    }

    public static void ApplyStartupDelayIfConfigured() {
        if (Current.StartupDelayMs <= 0) return;
        Thread.Sleep(Current.StartupDelayMs);
    }

    public static void WriteSignal(string stage, string? detail = null) {
        if (string.IsNullOrWhiteSpace(Current.SignalFile)) return;

        var line = new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            .Append('|')
            .Append(stage);

        if (!string.IsNullOrWhiteSpace(detail)) {
            line.Append('|').Append(detail.Replace(Environment.NewLine, " ", StringComparison.Ordinal));
        }

        lock (SignalGate) {
            File.AppendAllText(Current.SignalFile!, line.AppendLine().ToString(), Encoding.UTF8);
        }
    }

    static (string key, string? inlineValue) SplitArgument(string arg) {
        var equalsIndex = arg.IndexOf('=');
        if (equalsIndex <= 0) return (arg, null);
        return (arg[..equalsIndex], arg[(equalsIndex + 1)..]);
    }

    static string? ReadValue(string[] args, ref int index, string? inlineValue) {
        if (!string.IsNullOrWhiteSpace(inlineValue)) return inlineValue;
        if (index + 1 >= args.Length) return null;
        index++;
        return args[index];
    }

    static string? NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
}

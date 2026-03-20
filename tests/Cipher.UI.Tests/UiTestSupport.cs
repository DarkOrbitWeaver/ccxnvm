using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Cipher.UI.Tests;

static class UiTestPaths {
    static string? _repoRoot;

    public static string RepoRoot => _repoRoot ??= FindRepoRoot();
    public static string ClientExePath =>
        GetPathOverride("CIPHER_UI_CLIENT_EXE") ??
        Path.Combine(RepoRoot, "bin", "Debug", "net9.0-windows", "Cipher.exe");

    public static string ServerExePath =>
        GetPathOverride("CIPHER_UI_SERVER_EXE") ??
        Path.Combine(RepoRoot, "server", "bin", "Debug", "net9.0", "CipherServer.exe");

    public static string ServerDllPath =>
        GetPathOverride("CIPHER_UI_SERVER_DLL") ??
        Path.Combine(RepoRoot, "server", "bin", "Debug", "net9.0", "CipherServer.dll");

    public static string CreateTempDirectory(string prefix) {
        var path = Path.Combine(Path.GetTempPath(), "Cipher.UiTests", $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    static string FindRepoRoot() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null) {
            if (File.Exists(Path.Combine(dir.FullName, "ccxnvm.sln"))) {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("could not locate repo root");
    }

    static string? GetPathOverride(string variableName) {
        var value = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

sealed class PortReservation : IDisposable {
    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();
    readonly Task _acceptLoop;
    bool _disposed;

    PortReservation(TcpListener listener) {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public static PortReservation Reserve() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new PortReservation(listener);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();

        try {
            _listener.Stop();
        } catch {
        }

        try {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        } catch {
        }

        _cts.Dispose();
    }

    async Task AcceptLoopAsync() {
        while (!_cts.IsCancellationRequested) {
            TcpClient? client = null;
            try {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
                client.Close();
            } catch (OperationCanceledException) {
                break;
            } catch (ObjectDisposedException) {
                break;
            } catch (SocketException) {
                if (_cts.IsCancellationRequested) {
                    break;
                }
            } finally {
                client?.Dispose();
            }
        }
    }
}

sealed class LocalRelayProcess : IAsyncDisposable {
    readonly StringBuilder _logs = new();
    Process? _process;

    public LocalRelayProcess(int port) {
        Port = port;
    }

    public int Port { get; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public string Logs {
        get {
            lock (_logs) {
                return _logs.ToString();
            }
        }
    }

    public static int GetFreePort() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task StartAsync() {
        if (_process is { HasExited: false }) {
            return;
        }

        var serverExe = UiTestPaths.ServerExePath;
        var serverDll = UiTestPaths.ServerDllPath;

        var startInfo = File.Exists(serverExe)
            ? new ProcessStartInfo(serverExe)
            : new ProcessStartInfo("dotnet", $"\"{serverDll}\"");

        startInfo.WorkingDirectory = UiTestPaths.RepoRoot;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.Environment["PORT"] = Port.ToString();

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start relay process");
        _ = PumpLogsAsync(_process.StandardOutput);
        _ = PumpLogsAsync(_process.StandardError);

        await WaitForHealthyAsync();
    }

    public async Task RestartAsync() {
        Stop();
        await StartAsync();
    }

    public void Stop() {
        try {
            if (_process is { HasExited: false }) {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        } catch {
        }

        _process?.Dispose();
        _process = null;
    }

    public ValueTask DisposeAsync() {
        Stop();
        return ValueTask.CompletedTask;
    }

    async Task WaitForHealthyAsync() {
        using var client = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(45);

        while (DateTime.UtcNow < deadline) {
            if (_process is { HasExited: true }) {
                throw new InvalidOperationException("relay exited early" + Environment.NewLine + Logs);
            }

            try {
                using var response = await client.GetAsync($"{BaseUrl}/health");
                if (response.StatusCode == HttpStatusCode.OK) {
                    return;
                }
            } catch {
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("relay did not become healthy" + Environment.NewLine + Logs);
    }

    async Task PumpLogsAsync(StreamReader reader) {
        while (!reader.EndOfStream) {
            var line = await reader.ReadLineAsync();
            if (line == null) {
                continue;
            }

            lock (_logs) {
                _logs.AppendLine(line);
            }
        }
    }
}

sealed class UiAppSession : IAsyncDisposable {
    readonly Process _process;

    UiAppSession(Process process, string appDataDir, string signalFile, AutomationElement window) {
        _process = process;
        AppDataDir = appDataDir;
        SignalFile = signalFile;
        Window = window;
    }

    public string AppDataDir { get; }
    public string SignalFile { get; }
    public AutomationElement Window { get; }

    public static async Task<UiAppSession> LaunchAsync(string appDataDir, string relayUrl, params string[] extraArgs) {
        var signalFile = Path.Combine(appDataDir, "ui-signals.log");
        Directory.CreateDirectory(appDataDir);

        var args = new List<string> {
            "--test-mode",
            $"--appdata-dir=\"{appDataDir}\"",
            $"--relay-url=\"{relayUrl}\"",
            $"--signal-file=\"{signalFile}\"",
            "--disable-updater"
        };
        args.AddRange(extraArgs);

        var startInfo = new ProcessStartInfo(UiTestPaths.ClientExePath, string.Join(" ", args)) {
            WorkingDirectory = UiTestPaths.RepoRoot,
            UseShellExecute = false
        };

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start Cipher");
        var window = await WaitForWindowAsync(process);
        return new UiAppSession(process, appDataDir, signalFile, window);
    }

    public async Task WaitForSignalAsync(string stage, TimeSpan? timeout = null, Func<string, bool>? detailPredicate = null) {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (DateTime.UtcNow < deadline) {
            ThrowIfExited();

            if (File.Exists(SignalFile)) {
                var lines = await File.ReadAllLinesAsync(SignalFile);
                foreach (var line in lines.Reverse()) {
                    var parts = line.Split('|');
                    if (parts.Length < 2 || !string.Equals(parts[1], stage, StringComparison.Ordinal)) {
                        continue;
                    }

                    var detail = parts.Length >= 3 ? parts[2] : "";
                    if (detailPredicate == null || detailPredicate(detail)) {
                        return;
                    }
                }
            }

            await Task.Delay(150);
        }

        throw new TimeoutException($"timed out waiting for signal '{stage}'");
    }

    public async Task WaitForTextAsync(string automationId, Func<string, bool> predicate, TimeSpan? timeout = null) {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));

        while (DateTime.UtcNow < deadline) {
            ThrowIfExited();

            try {
                var element = FindById(automationId, TimeSpan.FromMilliseconds(250));
                var text = ReadText(element);
                if (predicate(text)) {
                    return;
                }
            } catch {
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"timed out waiting for text on '{automationId}'");
    }

    public AutomationElement FindById(string automationId, TimeSpan? timeout = null) {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        var condition = new PropertyCondition(AutomationElement.AutomationIdProperty, automationId);

        while (DateTime.UtcNow < deadline) {
            ThrowIfExited();

            var element = Window.FindFirst(TreeScope.Descendants, condition);
            if (element != null) {
                return element;
            }

            Thread.Sleep(100);
        }

        throw new InvalidOperationException($"could not find automation id '{automationId}'");
    }

    public void Click(string automationId) {
        var element = FindById(automationId);
        BringToFront();
        element.SetFocus();

        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) {
            ((InvokePattern)invoke).Invoke();
            return;
        }

        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var select)) {
            ((SelectionItemPattern)select).Select();
            return;
        }

        throw new InvalidOperationException($"element '{automationId}' is not invokable");
    }

    public void SetText(string automationId, string value) {
        var element = FindById(automationId);
        BringToFront();
        element.SetFocus();

        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) {
            ((ValuePattern)pattern).SetValue(value);
            return;
        }

        throw new InvalidOperationException($"element '{automationId}' does not support text input");
    }

    public void TypePassword(string automationId, string password) {
        var element = FindById(automationId);
        BringToFront();
        element.SetFocus();
        Thread.Sleep(80);
        NativeInput.SendUnicodeText(password);
        Thread.Sleep(120);
    }

    public string ReadValue(string automationId) {
        var element = FindById(automationId);
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) {
            return ((ValuePattern)pattern).Current.Value;
        }

        return ReadText(element);
    }

    public string ReadHelpText(string automationId) =>
        FindById(automationId).Current.HelpText;

    public bool HasText(string text) {
        var condition = new PropertyCondition(AutomationElement.NameProperty, text);
        return Window.FindFirst(TreeScope.Descendants, condition) != null;
    }

    public int CountTextOccurrences(string text) {
        var condition = new PropertyCondition(AutomationElement.NameProperty, text);
        return Window.FindAll(TreeScope.Descendants, condition).Count;
    }

    public void BringToFront() {
        NativeInput.SetForeground(_process.MainWindowHandle);
        Window.SetFocus();
    }

    public ValueTask DisposeAsync() {
        try {
            if (!_process.HasExited) {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        } catch {
        }

        _process.Dispose();
        return ValueTask.CompletedTask;
    }

    static async Task<AutomationElement> WaitForWindowAsync(Process process) {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var condition = new PropertyCondition(AutomationElement.ProcessIdProperty, process.Id);

        while (DateTime.UtcNow < deadline) {
            if (process.HasExited) {
                throw new InvalidOperationException("Cipher exited before its main window appeared");
            }

            var window = AutomationElement.RootElement.FindFirst(TreeScope.Children, condition);
            if (window != null) {
                return window;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("timed out waiting for Cipher main window");
    }

    void ThrowIfExited() {
        if (_process.HasExited) {
            throw new InvalidOperationException($"Cipher exited unexpectedly with code {_process.ExitCode}");
        }
    }

    static string ReadText(AutomationElement element) {
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) {
            return ((ValuePattern)value).Current.Value;
        }

        return element.Current.Name ?? "";
    }
}

static class NativeInput {
    const uint InputKeyboard = 1;
    const uint KeyeventfKeyUp = 0x0002;
    const uint KeyeventfUnicode = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    public static void SetForeground(IntPtr windowHandle) {
        if (windowHandle != IntPtr.Zero) {
            SetForegroundWindow(windowHandle);
        }
    }

    public static void SendUnicodeText(string text) {
        foreach (var ch in text) {
            SendKey(ch, keyUp: false);
            SendKey(ch, keyUp: true);
        }
    }

    static void SendKey(char ch, bool keyUp) {
        var input = new INPUT {
            type = InputKeyboard,
            U = new InputUnion {
                ki = new KEYBDINPUT {
                    wVk = 0,
                    wScan = ch,
                    dwFlags = KeyeventfUnicode | (keyUp ? KeyeventfKeyUp : 0)
                }
            }
        };

        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }
}

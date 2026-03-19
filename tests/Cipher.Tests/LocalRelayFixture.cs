using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cipher.Tests;

public sealed class LocalRelayFixture : IAsyncLifetime {
    readonly StringBuilder _logs = new();
    Process? _process;

    public string BaseUrl { get; private set; } = "";
    public string Logs => _logs.ToString();

    public async Task InitializeAsync() {
        var repoRoot = FindRepoRoot();
        var port = GetFreePort();
        BaseUrl = $"http://127.0.0.1:{port}";

        var serverExe = Path.Combine(repoRoot, "server", "bin", "Debug", "net9.0", "CipherServer.exe");
        var serverDll = Path.Combine(repoRoot, "server", "bin", "Debug", "net9.0", "CipherServer.dll");

        var startInfo = File.Exists(serverExe)
            ? new ProcessStartInfo(serverExe)
            : new ProcessStartInfo("dotnet", $"\"{serverDll}\"");

        startInfo.WorkingDirectory = repoRoot;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.Environment["PORT"] = port.ToString();

        _process = Process.Start(startInfo) ?? throw new InvalidOperationException("failed to start local relay");
        _ = PumpLogsAsync(_process.StandardOutput);
        _ = PumpLogsAsync(_process.StandardError);

        await WaitForHealthyAsync();
    }

    public Task DisposeAsync() {
        try {
            if (_process is { HasExited: false }) {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        } catch {
        }

        _process?.Dispose();
        return Task.CompletedTask;
    }

    async Task WaitForHealthyAsync() {
        using var client = new HttpClient();
        var deadline = DateTime.UtcNow.AddSeconds(45);

        while (DateTime.UtcNow < deadline) {
            if (_process is { HasExited: true }) {
                throw new InvalidOperationException("local relay exited early\n" + Logs);
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

        throw new TimeoutException("local relay did not become healthy\n" + Logs);
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

    static int GetFreePort() {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}


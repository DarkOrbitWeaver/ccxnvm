using Cipher;

namespace Cipher.Tests;

public class ReleaseUpdaterTests {
    [Fact]
    public async Task DisabledUpdaterReportsDisabledState() {
        var updater = new ReleaseUpdater(new FakeBackend {
            IsAvailableValue = false,
            CurrentVersionValue = "1.0.0"
        });

        var snapshot = await updater.CheckForUpdatesAsync(interactive: true, CancellationToken.None);

        Assert.Equal(AppUpdateState.Disabled, snapshot.State);
    }

    [Fact]
    public async Task UpdaterDownloadsAndTransitionsToReadyState() {
        var backend = new FakeBackend {
            IsAvailableValue = true,
            CurrentVersionValue = "1.0.0",
            NextUpdate = new PendingAppUpdate("1.1.0", "native")
        };
        var updater = new ReleaseUpdater(backend);

        var snapshot = await updater.CheckForUpdatesAsync(interactive: true, CancellationToken.None);

        Assert.Equal(AppUpdateState.ReadyToRestart, snapshot.State);
        Assert.True(updater.CanRestartToApply);
        Assert.Equal("1.1.0", snapshot.AvailableVersion);
        Assert.Equal(100, snapshot.DownloadProgress);
    }

    sealed class FakeBackend : IAppUpdateBackend {
        public bool IsAvailableValue { get; set; }
        public string CurrentVersionValue { get; set; } = "1.0.0";
        public PendingAppUpdate? NextUpdate { get; set; }
        public PendingAppUpdate? PendingRestart { get; private set; }

        public bool IsAvailable => IsAvailableValue;
        public string CurrentVersion => CurrentVersionValue;

        public void ApplyAndRestart(PendingAppUpdate update) {
            PendingRestart = update;
        }

        public Task<PendingAppUpdate?> CheckForUpdatesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NextUpdate);

        public async Task DownloadUpdatesAsync(PendingAppUpdate update, Action<int>? progress, CancellationToken cancellationToken) {
            progress?.Invoke(50);
            await Task.Delay(1, cancellationToken);
            progress?.Invoke(100);
            PendingRestart = update;
        }
    }
}

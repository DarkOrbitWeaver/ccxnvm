using System.IO;
using Velopack;
using Velopack.Sources;

namespace Cipher;

public enum AppUpdateState {
    Disabled,
    Idle,
    Checking,
    Downloading,
    ReadyToRestart,
    UpToDate,
    Failed
}

public sealed record PendingAppUpdate(string Version, object NativeHandle);

public sealed record AppUpdateSnapshot(
    AppUpdateState State,
    string StatusText,
    string CurrentVersion,
    string? AvailableVersion = null,
    int? DownloadProgress = null
);

public interface IAppUpdateBackend {
    bool IsAvailable { get; }
    string CurrentVersion { get; }
    PendingAppUpdate? PendingRestart { get; }
    Task<PendingAppUpdate?> CheckForUpdatesAsync(CancellationToken cancellationToken);
    Task DownloadUpdatesAsync(PendingAppUpdate update, Action<int>? progress, CancellationToken cancellationToken);
    void ApplyAndRestart(PendingAppUpdate update);
}

public sealed class VelopackUpdateBackend : IAppUpdateBackend {
    readonly UpdateManager _manager;

    public VelopackUpdateBackend(string repoUrl) {
        _manager = new UpdateManager(
            new GithubSource(repoUrl, "", prerelease: false),
            new UpdateOptions {
                ExplicitChannel = "stable",
                MaximumDeltasBeforeFallback = 10
            });
    }

    public bool IsAvailable =>
        _manager.IsInstalled && File.Exists(Path.Combine(AppContext.BaseDirectory, "Update.exe"));

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString() ?? AppInfo.DisplayVersion;

    public PendingAppUpdate? PendingRestart {
        get {
            var pending = _manager.UpdatePendingRestart;
            return pending == null ? null : new PendingAppUpdate(pending.Version.ToString(), pending);
        }
    }

    public async Task<PendingAppUpdate?> CheckForUpdatesAsync(CancellationToken cancellationToken) {
        var updateInfo = await _manager.CheckForUpdatesAsync();
        if (updateInfo == null) return null;
        return new PendingAppUpdate(updateInfo.TargetFullRelease.Version.ToString(), updateInfo);
    }

    public Task DownloadUpdatesAsync(PendingAppUpdate update, Action<int>? progress, CancellationToken cancellationToken) {
        if (update.NativeHandle is not UpdateInfo info) {
            throw new InvalidOperationException("Update payload is not a Velopack UpdateInfo instance.");
        }

        return _manager.DownloadUpdatesAsync(info, progress, cancellationToken);
    }

    public void ApplyAndRestart(PendingAppUpdate update) {
        if (update.NativeHandle is VelopackAsset readyAsset) {
            _manager.ApplyUpdatesAndRestart(readyAsset);
            return;
        }

        if (update.NativeHandle is UpdateInfo info) {
            _manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
            return;
        }

        throw new InvalidOperationException("Update payload is not a Velopack asset.");
    }
}

public sealed class ReleaseUpdater {
    readonly IAppUpdateBackend _backend;
    readonly SemaphoreSlim _gate = new(1, 1);
    PendingAppUpdate? _pending;
    AppUpdateSnapshot _snapshot;

    public event Action<AppUpdateSnapshot>? SnapshotChanged;

    public ReleaseUpdater(IAppUpdateBackend backend) {
        _backend = backend;
        _pending = backend.PendingRestart;
        _snapshot = _pending != null
            ? new AppUpdateSnapshot(
                AppUpdateState.ReadyToRestart,
                $"update ready: {_pending.Version}",
                backend.CurrentVersion,
                _pending.Version,
                100)
            : backend.IsAvailable
                ? new AppUpdateSnapshot(AppUpdateState.Idle, "auto-update ready", backend.CurrentVersion)
                : new AppUpdateSnapshot(AppUpdateState.Disabled, "installer updates only work in packaged builds", backend.CurrentVersion);
    }

    public AppUpdateSnapshot Snapshot => _snapshot;
    public bool CanCheck => _backend.IsAvailable && _snapshot.State is not AppUpdateState.Checking and not AppUpdateState.Downloading;
    public bool CanRestartToApply => _pending != null;

    public static ReleaseUpdater CreateDefault() {
        try {
            return new ReleaseUpdater(new VelopackUpdateBackend(AppBranding.GitHubRepoUrl));
        } catch (Exception ex) {
            AppLog.Error("update", "failed to initialize updater", ex);
            return new ReleaseUpdater(new DisabledUpdateBackend(AppInfo.DisplayVersion));
        }
    }

    public async Task<AppUpdateSnapshot> CheckForUpdatesAsync(bool interactive, CancellationToken cancellationToken) {
        if (!_backend.IsAvailable) {
            return Publish(_snapshot with {
                State = AppUpdateState.Disabled,
                StatusText = "updates activate after installing a packaged release"
            });
        }

        await _gate.WaitAsync(cancellationToken);
        try {
            if (_pending != null) {
                return Publish(new AppUpdateSnapshot(
                    AppUpdateState.ReadyToRestart,
                    $"update ready: {_pending.Version}",
                    _backend.CurrentVersion,
                    _pending.Version,
                    100));
            }

            Publish(new AppUpdateSnapshot(
                AppUpdateState.Checking,
                interactive ? "checking GitHub Releases..." : "checking for updates in background...",
                _backend.CurrentVersion));

            var available = await _backend.CheckForUpdatesAsync(cancellationToken);
            if (available == null) {
                return Publish(new AppUpdateSnapshot(
                    AppUpdateState.UpToDate,
                    "you're on the latest stable release",
                    _backend.CurrentVersion));
            }

            Publish(new AppUpdateSnapshot(
                AppUpdateState.Downloading,
                $"downloading {available.Version}...",
                _backend.CurrentVersion,
                available.Version,
                0));

            await _backend.DownloadUpdatesAsync(available, progress => {
                Publish(new AppUpdateSnapshot(
                    AppUpdateState.Downloading,
                    $"downloading {available.Version}... {progress}%",
                    _backend.CurrentVersion,
                    available.Version,
                    progress));
            }, cancellationToken);

            _pending = _backend.PendingRestart ?? available;
            return Publish(new AppUpdateSnapshot(
                AppUpdateState.ReadyToRestart,
                $"update ready: {_pending.Version}",
                _backend.CurrentVersion,
                _pending.Version,
                100));
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            AppLog.Error("update", "update check failed", ex);
            return Publish(new AppUpdateSnapshot(
                AppUpdateState.Failed,
                $"update failed: {FriendlyErrors.ToUserMessage(ex)}",
                _backend.CurrentVersion));
        } finally {
            _gate.Release();
        }
    }

    public void ApplyPendingUpdateAndRestart() {
        if (_pending == null) {
            throw new InvalidOperationException("No update is ready to apply.");
        }

        AppLog.Info("update", $"applying downloaded update {_pending.Version}");
        _backend.ApplyAndRestart(_pending);
    }

    AppUpdateSnapshot Publish(AppUpdateSnapshot snapshot) {
        _snapshot = snapshot;
        SnapshotChanged?.Invoke(snapshot);
        return snapshot;
    }

    sealed class DisabledUpdateBackend : IAppUpdateBackend {
        public DisabledUpdateBackend(string currentVersion) {
            CurrentVersion = currentVersion;
        }

        public bool IsAvailable => false;
        public string CurrentVersion { get; }
        public PendingAppUpdate? PendingRestart => null;

        public void ApplyAndRestart(PendingAppUpdate update) =>
            throw new InvalidOperationException("Updater is disabled.");

        public Task<PendingAppUpdate?> CheckForUpdatesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<PendingAppUpdate?>(null);

        public Task DownloadUpdatesAsync(PendingAppUpdate update, Action<int>? progress, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}

using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Cipher;

public partial class MainWindow {
    readonly ReleaseUpdater _updater = ReleaseUpdater.CreateDefault();
    readonly CancellationTokenSource _uiLifetimeCts = new();

    void InitializeReleaseFeatures() {
        SettingsVersionText.Text = $"{AppBranding.ProductName} v{AppInfo.DisplayVersion}";
        _updater.SnapshotChanged += snapshot =>
            Dispatcher.InvokeAsync(() => ApplyUpdaterSnapshot(snapshot));
        ApplyUpdaterSnapshot(_updater.Snapshot);
        RefreshDiagnosticsSummary();
        ScheduleBackgroundUpdateCheck();
    }

    void ScheduleBackgroundUpdateCheck() {
        RunUiTask(async () => {
            await Task.Delay(TimeSpan.FromSeconds(6), _uiLifetimeCts.Token);
            await CheckForUpdatesCoreAsync(interactive: false);
        }, "background update check", showSidebarErrors: false);
    }

    void ApplyUpdaterSnapshot(AppUpdateSnapshot snapshot) {
        SettingsUpdateStatusText.Text = snapshot.StatusText;
        BtnCheckUpdates.IsEnabled = _updater.CanCheck;

        if (snapshot.DownloadProgress is int progress) {
            UpdateProgressBar.Visibility = Visibility.Visible;
            UpdateProgressBar.Value = progress;
        } else {
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            UpdateProgressBar.Value = 0;
        }

        BtnRestartToUpdate.Visibility = _updater.CanRestartToApply
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (snapshot.State == AppUpdateState.ReadyToRestart) {
            SetSidebarStatus("update ready: restart when you're done chatting");
        }

        _settingsWindow?.ApplyUpdateSnapshot(snapshot, _updater.CanCheck, _updater.CanRestartToApply);
        RefreshDiagnosticsSummary();
    }

    string BuildDiagnosticsSummaryText() {
        var startup = App.LastStartupHealth;
        var notes = new List<string> {
            $"data: {AppPaths.AppDataRoot}",
            $"logs: {AppPaths.LogsDir}",
            $"theme: {_activeThemeFile}",
            $"chat text: {GetCurrentChatFontSize():0}",
            $"close: {DescribeCloseBehavior()}",
            $"startup: {DescribeStartupBehavior()}"
        };

        if (startup.Errors.Count > 0) {
            notes.Add($"startup issues: {string.Join(" | ", startup.Errors)}");
        } else if (startup.Warnings.Count > 0) {
            notes.Add($"startup warnings: {string.Join(" | ", startup.Warnings)}");
        } else {
            notes.Add("startup checks passed");
        }

        if (!string.IsNullOrWhiteSpace(_vault.LastMaintenanceBackupPath)) {
            notes.Add($"latest backup: {_vault.LastMaintenanceBackupPath}");
        }

        return string.Join(Environment.NewLine, notes);
    }

    void RefreshDiagnosticsSummary() {
        var summary = BuildDiagnosticsSummaryText();
        SettingsDiagnosticsText.Text = summary;
        _settingsWindow?.SetDiagnosticsText(summary);
        _settingsWindow?.SetVersionText($"{AppBranding.ProductName} v{AppInfo.DisplayVersion}");
    }

    void ReportVaultMaintenance() {
        if (_vault.LastMaintenanceActions.Count == 0) return;
        SetSidebarStatus(string.Join(" | ", _vault.LastMaintenanceActions));
        RefreshDiagnosticsSummary();
    }

    async Task CheckForUpdatesCoreAsync(bool interactive) {
        var snapshot = await _updater.CheckForUpdatesAsync(interactive, _uiLifetimeCts.Token);
        if (interactive && snapshot.State == AppUpdateState.UpToDate) {
            SetSidebarStatus("you're on the latest stable release");
        } else if (interactive && snapshot.State == AppUpdateState.Failed) {
            SetSidebarStatus($"! {snapshot.StatusText}");
        }
    }

    void BtnSettings_Click(object s, RoutedEventArgs e) {
        InitializeShellPreferences();
        RefreshDiagnosticsSummary();
        UpdateThemeButtonStates();
        OpenSettingsWindow();
    }

    void BtnCloseSettings_Click(object s, RoutedEventArgs e) =>
        SettingsOverlay.Visibility = Visibility.Collapsed;

    void ThemeButton_Click(object s, RoutedEventArgs e) {
        if (s is not System.Windows.Controls.Button { Tag: string tag }) return;
        var preset = tag.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
            ? ThemePresetCatalog.GetByLegacyThemeFile(tag)
            : ThemePresetCatalog.GetById(tag);
        ApplyThemePreset(preset.Id);
    }

    void BtnCheckUpdates_Click(object s, RoutedEventArgs e) =>
        RunUiTask(() => CheckForUpdatesCoreAsync(interactive: true), "manual update check");

    void BtnRestartToUpdate_Click(object s, RoutedEventArgs e) =>
        RunUiTask(ApplyDownloadedUpdateAsync, "apply update", showSidebarErrors: false);

    async Task ApplyDownloadedUpdateAsync() {
        if (_updater.CanRestartToApply && _vault.IsOpen) {
            _vault.CreateMaintenanceBackup("pre-update");
        }

        _exitRequested = true;
        _outboxTimer?.Stop();
        await _net.DisposeAsync();
        AppLog.Info("update", "handing off to Velopack for restart");
        _updater.ApplyPendingUpdateAndRestart();
    }

    void BtnOpenLogs_Click(object s, RoutedEventArgs e) => OpenFolder(AppPaths.LogsDir);
    void BtnOpenData_Click(object s, RoutedEventArgs e) => OpenFolder(AppPaths.AppDataRoot);

    void BtnExportDiagnostics_Click(object s, RoutedEventArgs e) {
        var summary = new List<string> {
            $"version: {AppInfo.DisplayVersion}",
            $"update: {_updater.Snapshot.StatusText}",
            $"data_path: {AppPaths.AppDataRoot}",
            $"logs_path: {AppPaths.LogsDir}"
        };

        if (App.LastStartupHealth.Errors.Count > 0) {
            summary.Add($"startup_errors: {string.Join(" | ", App.LastStartupHealth.Errors)}");
        }
        if (App.LastStartupHealth.Warnings.Count > 0) {
            summary.Add($"startup_warnings: {string.Join(" | ", App.LastStartupHealth.Warnings)}");
        }
        if (_vault.LastMaintenanceActions.Count > 0) {
            summary.Add($"vault_maintenance: {string.Join(" | ", _vault.LastMaintenanceActions)}");
        }

        var bundle = AppLog.CreateDiagnosticsBundle(string.Join(Environment.NewLine, summary));
        SetSidebarStatus($"diagnostics exported: {bundle}");
        OpenFolder(Path.GetDirectoryName(bundle)!);
    }

    static void OpenFolder(string path) {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
    }
}

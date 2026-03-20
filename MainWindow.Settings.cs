using System.Windows;

namespace Cipher;

public partial class MainWindow {
    SettingsWindow? _settingsWindow;

    void OpenSettingsWindow() {
        if (_settingsWindow == null) {
            _settingsWindow = new SettingsWindow {
                Owner = this
            };
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.ThemeRequested += themeFile => ApplyTheme(themeFile);
            _settingsWindow.ChatFontSizeRequested += fontSize => ApplyChatFontSize(fontSize);
            _settingsWindow.CloseToTrayChanged += enabled => UpdateCloseToTrayPreference(enabled);
            _settingsWindow.StartWithWindowsChanged += enabled => UpdateStartWithWindowsPreference(enabled);
            _settingsWindow.StartHiddenOnStartupChanged += enabled => UpdateStartHiddenPreference(enabled);
            _settingsWindow.StartupDelayChanged += seconds => UpdateStartupDelayPreference(seconds);
            _settingsWindow.CheckUpdatesRequested += () =>
                RunUiTask(() => CheckForUpdatesCoreAsync(interactive: true), "manual update check");
            _settingsWindow.RestartToUpdateRequested += () =>
                RunUiTask(ApplyDownloadedUpdateAsync, "apply update", showSidebarErrors: false);
            _settingsWindow.OpenLogsRequested += () => OpenFolder(AppPaths.LogsDir);
            _settingsWindow.OpenDataRequested += () => OpenFolder(AppPaths.AppDataRoot);
            _settingsWindow.ExportDiagnosticsRequested += () => BtnExportDiagnostics_Click(this, new RoutedEventArgs());
            _settingsWindow.OpenNukeRequested += () => {
                _settingsWindow?.Close();
                BringWindowToFront();
                BtnNuke_Click(this, new RoutedEventArgs());
            };
        }

        RefreshSettingsWindowState();

        if (!_settingsWindow.IsVisible) {
            _settingsWindow.Show();
        }

        _settingsWindow.Activate();
        _settingsWindow.Focus();
    }

    void RefreshSettingsWindowState() {
        if (_settingsWindow == null) return;

        _settingsWindow.SetVersionText($"{AppBranding.ProductName} v{AppInfo.DisplayVersion}");
        _settingsWindow.SetDiagnosticsText(BuildDiagnosticsSummaryText());
        _settingsWindow.ApplyThemeSelection(_activeThemeFile);
        _settingsWindow.ApplyChatFontSizeSelection(GetCurrentChatFontSize());
        _settingsWindow.ApplyShellPreferences(_shellPreferences);
        _settingsWindow.ApplyUpdateSnapshot(_updater.Snapshot, _updater.CanCheck, _updater.CanRestartToApply);
    }

    void UpdateCloseToTrayPreference(bool enabled) {
        TryApplyShellPreferences(
            _shellPreferences with { CloseToTrayOnClose = enabled },
            enabled
                ? "closing the window now keeps ZLABO in the tray"
                : "closing the window now exits the app",
            syncWindowsStartup: false);
    }

    void UpdateStartWithWindowsPreference(bool enabled) {
        TryApplyShellPreferences(
            _shellPreferences with { StartWithWindows = enabled },
            enabled
                ? "ZLABO will now start with Windows"
                : "Windows startup disabled",
            syncWindowsStartup: true);
    }

    void UpdateStartHiddenPreference(bool enabled) {
        TryApplyShellPreferences(
            _shellPreferences with { StartHiddenOnStartup = enabled },
            enabled
                ? "Windows startup will now open ZLABO in the tray"
                : "Windows startup will now open the main window",
            syncWindowsStartup: true);
    }

    void UpdateStartupDelayPreference(int seconds) {
        TryApplyShellPreferences(
            _shellPreferences with { StartupDelaySeconds = seconds },
            seconds <= 0
                ? "Windows startup delay removed"
                : $"Windows startup delay set to {seconds} seconds",
            syncWindowsStartup: true);
    }
}

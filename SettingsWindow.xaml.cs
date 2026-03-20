using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;

namespace Cipher;

public partial class SettingsWindow : Window {
    bool _applyingState;

    public SettingsWindow() {
        InitializeComponent();
        ApplyChatFontSizeSelection(17d);
    }

    public event Action<string>? ThemeRequested;
    public event Action<double>? ChatFontSizeRequested;
    public event Action<bool>? CloseToTrayChanged;
    public event Action<bool>? StartWithWindowsChanged;
    public event Action<bool>? StartHiddenOnStartupChanged;
    public event Action<int>? StartupDelayChanged;
    public event Action? CheckUpdatesRequested;
    public event Action? RestartToUpdateRequested;
    public event Action? OpenLogsRequested;
    public event Action? OpenDataRequested;
    public event Action? ExportDiagnosticsRequested;
    public event Action? OpenNukeRequested;

    public void SetVersionText(string text) =>
        SettingsVersionText.Text = text;

    public void SetDiagnosticsText(string text) =>
        SettingsDiagnosticsText.Text = text;

    public void ApplyThemeSelection(string themeFile) {
        foreach (var button in ThemeButtons()) {
            var isActive = string.Equals(button.Tag as string, themeFile, StringComparison.Ordinal);
            button.Content = isActive ? "\u2713" : "";
            button.BorderThickness = isActive ? new Thickness(2) : new Thickness(1);
            button.Foreground = isActive
                ? (Brush)FindResource("White")
                : Brushes.Transparent;
        }

        ThemeHintText.Text = $"Current theme: {ThemeLabel(themeFile)}. Applies and saves instantly to this vault.";
    }

    public void ApplyChatFontSizeSelection(double fontSize) {
        var activeSize = NormalizeChatFontSize(fontSize);
        foreach (var (size, button) in ChatFontButtons()) {
            var active = Math.Abs(size - activeSize) < 0.01;
            button.Style = (Style)FindResource(active ? "AccentBtn" : "TermBtn");
            button.Content = active ? $"\u2713 {ChatFontSizeLabel(size)}" : ChatFontSizeLabel(size);
        }

        ChatFontHintText.Text = $"Current size: {ChatFontSizeLabel(activeSize)}. Changes apply instantly.";
    }

    public void ApplyShellPreferences(AppShellPreferences preferences) {
        _applyingState = true;
        try {
            ChkCloseToTrayOnClose.IsChecked = preferences.CloseToTrayOnClose;
            ChkStartWithWindows.IsChecked = preferences.StartWithWindows;
            ChkStartHiddenOnStartup.IsChecked = preferences.StartHiddenOnStartup;

            var enabled = preferences.StartWithWindows;
            ChkStartHiddenOnStartup.IsEnabled = enabled;
            StartupDelayPanel.IsEnabled = enabled;
            StartupHintText.Opacity = enabled ? 1.0 : 0.7;

            foreach (var (seconds, button) in DelayButtons()) {
                var active = preferences.StartupDelaySeconds == seconds;
                button.Style = (Style)FindResource(active ? "AccentBtn" : "TermBtn");
                button.Content = active ? $"\u2713 {DelayLabel(seconds)}" : DelayLabel(seconds);
            }
        } finally {
            _applyingState = false;
        }
    }

    public void ApplyUpdateSnapshot(AppUpdateSnapshot snapshot, bool canCheck, bool canRestart) {
        SettingsUpdateStatusText.Text = snapshot.StatusText;
        BtnCheckUpdates.IsEnabled = canCheck;
        BtnRestartToUpdate.Visibility = canRestart ? Visibility.Visible : Visibility.Collapsed;

        if (snapshot.DownloadProgress is int progress) {
            UpdateProgressBar.Visibility = Visibility.Visible;
            UpdateProgressBar.Value = progress;
        } else {
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            UpdateProgressBar.Value = 0;
        }
    }

    void ThemeButton_Click(object sender, RoutedEventArgs e) {
        if (sender is Button { Tag: string themeFile }) {
            ThemeRequested?.Invoke(themeFile);
        }
    }

    void ChatFontSizeButton_Click(object sender, RoutedEventArgs e) {
        if (sender is not Button { Tag: string tag }) return;
        if (!double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var fontSize)) return;
        ChatFontSizeRequested?.Invoke(fontSize);
    }

    void ChkCloseToTrayOnClose_Click(object sender, RoutedEventArgs e) {
        if (_applyingState) return;
        CloseToTrayChanged?.Invoke(ChkCloseToTrayOnClose.IsChecked == true);
    }

    void ChkStartWithWindows_Click(object sender, RoutedEventArgs e) {
        if (_applyingState) return;
        StartWithWindowsChanged?.Invoke(ChkStartWithWindows.IsChecked == true);
    }

    void ChkStartHiddenOnStartup_Click(object sender, RoutedEventArgs e) {
        if (_applyingState) return;
        StartHiddenOnStartupChanged?.Invoke(ChkStartHiddenOnStartup.IsChecked == true);
    }

    void StartupDelayButton_Click(object sender, RoutedEventArgs e) {
        if (_applyingState || sender is not Button { Tag: string tag }) return;
        if (int.TryParse(tag, out var seconds)) {
            StartupDelayChanged?.Invoke(seconds);
        }
    }

    void BtnCheckUpdates_Click(object sender, RoutedEventArgs e) =>
        CheckUpdatesRequested?.Invoke();

    void BtnRestartToUpdate_Click(object sender, RoutedEventArgs e) =>
        RestartToUpdateRequested?.Invoke();

    void BtnOpenLogs_Click(object sender, RoutedEventArgs e) =>
        OpenLogsRequested?.Invoke();

    void BtnOpenData_Click(object sender, RoutedEventArgs e) =>
        OpenDataRequested?.Invoke();

    void BtnExportDiagnostics_Click(object sender, RoutedEventArgs e) =>
        ExportDiagnosticsRequested?.Invoke();

    void BtnOpenNukeOverlay_Click(object sender, RoutedEventArgs e) =>
        OpenNukeRequested?.Invoke();

    void BtnClose_Click(object sender, RoutedEventArgs e) =>
        Close();

    IEnumerable<Button> ThemeButtons() =>
        [BtnThemeTeal, BtnThemeViolet, BtnThemeBlue, BtnThemeAmber, BtnThemeRose];

    IEnumerable<(double size, Button button)> ChatFontButtons() =>
        [(15d, BtnChatFontSize15), (17d, BtnChatFontSize17), (19d, BtnChatFontSize19)];

    IEnumerable<(int seconds, Button button)> DelayButtons() =>
        [(0, BtnStartupDelay0), (15, BtnStartupDelay15), (30, BtnStartupDelay30), (60, BtnStartupDelay60)];

    static string DelayLabel(int seconds) => seconds <= 0 ? "Off" : $"{seconds} sec";

    static string ThemeLabel(string themeFile) => themeFile switch {
        "Theme.Violet.xaml" => "Violet",
        "Theme.Blue.xaml" => "Blue",
        "Theme.Amber.xaml" => "Amber",
        "Theme.Rose.xaml" => "Rose",
        _ => "Teal"
    };

    static double NormalizeChatFontSize(double fontSize) {
        if (fontSize <= 15.5) return 15d;
        if (fontSize <= 18d) return 17d;
        return 19d;
    }

    static string ChatFontSizeLabel(double fontSize) {
        var normalized = NormalizeChatFontSize(fontSize);
        if (Math.Abs(normalized - 15d) < 0.01) return "Compact";
        if (Math.Abs(normalized - 19d) < 0.01) return "Large";
        return "Comfortable";
    }
}

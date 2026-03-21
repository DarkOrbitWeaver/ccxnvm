using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using MediaColor = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;

namespace Cipher;

public partial class SettingsWindow : Window {
    bool _applyingState;
    readonly Dictionary<string, Button> _themePresetButtons = [];

    public SettingsWindow() {
        InitializeComponent();
        BuildThemePresetButtons();
        UiThemeManager.PresetApplied += HandlePresetApplied;
        Closed += (_, _) => UiThemeManager.PresetApplied -= HandlePresetApplied;
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

    public void ApplyThemeSelection(string presetId) {
        var preset = ThemePresetCatalog.GetById(presetId);

        foreach (var (id, button) in _themePresetButtons) {
            var isActive = string.Equals(id, preset.Id, StringComparison.Ordinal);
            button.BorderThickness = isActive ? new Thickness(2) : new Thickness(0);
            button.BorderBrush = isActive
                ? (Brush)FindResource("White")
                : Brushes.Transparent;
            button.Opacity = isActive ? 1d : 0.96d;
        }

        ThemeHintText.Text = $"Current preset: {preset.DisplayName} ({preset.KindLabel}). Applies and saves instantly on this device.";
        ApplyThemeShellVisuals(preset);
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

    void BuildThemePresetButtons() {
        ThemePresetPanel.Children.Clear();
        _themePresetButtons.Clear();

        foreach (var preset in ThemePresetCatalog.All) {
            var button = new Button {
                Tag = preset.Id,
                Width = 156,
                Height = 136,
                Margin = new Thickness(0, 0, 14, 14),
                Padding = new Thickness(0),
                Style = (Style)FindResource("TermBtn"),
                Background = new SolidColorBrush(MediaColor.FromArgb(0x28, preset.BackdropColor.R, preset.BackdropColor.G, preset.BackdropColor.B)),
                BorderBrush = (Brush)FindResource("GlassBorder"),
                Content = BuildPresetCardContent(preset),
                ToolTip = $"{preset.DisplayName} ({preset.KindLabel})",
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Stretch,
                VerticalContentAlignment = System.Windows.VerticalAlignment.Stretch
            };
            button.Click += ThemeButton_Click;
            ThemePresetPanel.Children.Add(button);
            _themePresetButtons[preset.Id] = button;
        }
    }

    UIElement BuildPresetCardContent(ThemePresetDefinition preset) {
        var root = new Border {
            Width = 152,
            Height = 132,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0x44, preset.AccentColor.R, preset.AccentColor.G, preset.AccentColor.B)),
            Background = new SolidColorBrush(MediaColor.FromArgb(0x35, preset.BackdropColor.R, preset.BackdropColor.G, preset.BackdropColor.B))
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(78) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var previewHost = new Border {
            Margin = new Thickness(8, 8, 8, 0),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(MediaColor.FromArgb(0x52, preset.BackdropColor.R, preset.BackdropColor.G, preset.BackdropColor.B)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0x40, preset.AccentColor.R, preset.AccentColor.G, preset.AccentColor.B))
        };

        if (UiThemeManager.LoadImageSource(preset.PreviewAssetPath) is ImageSource previewSource) {
            previewHost.Child = new Grid {
                Children = {
                    new Image {
                        Source = previewSource,
                        Stretch = Stretch.UniformToFill
                    },
                    new Border {
                        Background = new SolidColorBrush(MediaColor.FromArgb(0x30, preset.BackdropColor.R, preset.BackdropColor.G, preset.BackdropColor.B))
                    }
                }
            };
        } else {
            previewHost.Child = new Border {
                Background = new LinearGradientBrush(
                    MediaColor.FromArgb(0x70, preset.AccentColor.R, preset.AccentColor.G, preset.AccentColor.B),
                    MediaColor.FromArgb(0x90, preset.BackdropColor.R, preset.BackdropColor.G, preset.BackdropColor.B),
                    45)
            };
        }

        Grid.SetRow(previewHost, 0);
        grid.Children.Add(previewHost);

        var labels = new StackPanel {
            Margin = new Thickness(10, 10, 10, 10)
        };
        labels.Children.Add(new TextBlock {
            Text = preset.DisplayName,
            Foreground = (Brush)FindResource("White"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        });
        labels.Children.Add(new TextBlock {
            Text = preset.KindLabel,
            Foreground = (Brush)FindResource("Muted"),
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0)
        });
        Grid.SetRow(labels, 1);
        grid.Children.Add(labels);

        root.Child = grid;
        return root;
    }

    void ApplyThemeShellVisuals(ThemePresetDefinition preset) {
        ShellWallpaperImage.Source = UiThemeManager.LoadImageSource(preset.ShellBackgroundAssetPath);
        ShellWallpaperImage.Visibility = ShellWallpaperImage.Source == null ? Visibility.Collapsed : Visibility.Visible;
        ShellWallpaperImage.Opacity = preset.ShellWallpaperOpacity;

        ShellWallpaperTint.Background = new SolidColorBrush(MediaColor.FromArgb(
            (byte)Math.Clamp((int)Math.Round(preset.ShellOverlayOpacity * 255), 0, 255),
            preset.BackdropColor.R,
            preset.BackdropColor.G,
            preset.BackdropColor.B));
        ShellWallpaperTint.Opacity = preset.ShellOverlayOpacity > 0 ? 1d : 0d;
    }

    void HandlePresetApplied(ThemePresetDefinition preset) =>
        Dispatcher.Invoke(() => ApplyThemeShellVisuals(preset));

    void ThemeButton_Click(object sender, RoutedEventArgs e) {
        if (sender is Button { Tag: string presetId }) {
            ThemeRequested?.Invoke(presetId);
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

    IEnumerable<(double size, Button button)> ChatFontButtons() =>
        [(15d, BtnChatFontSize15), (17d, BtnChatFontSize17), (19d, BtnChatFontSize19)];

    IEnumerable<(int seconds, Button button)> DelayButtons() =>
        [(0, BtnStartupDelay0), (15, BtnStartupDelay15), (30, BtnStartupDelay30), (60, BtnStartupDelay60)];

    static string DelayLabel(int seconds) => seconds <= 0 ? "Off" : $"{seconds} sec";

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

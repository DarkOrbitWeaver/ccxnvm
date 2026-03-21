using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace Cipher;

public static class UiThemeManager {
    static readonly (string ColorKey, string BrushKey)[] ThemeBrushMappings = [
        ("GreenColor", "Green"),
        ("DimGreenColor", "DimGreen"),
        ("TealAccentColor", "TealAccent"),
        ("GlassBorderColor", "GlassBorder"),
        ("BubbleBorderMineColor", "BubbleBorderMine"),
        ("BubbleBorderTheirsColor", "BubbleBorderTheirs"),
        ("AccentButtonFillColor", "AccentButtonFill"),
        ("AccentButtonHoverFillColor", "AccentButtonHoverFill"),
        ("AccentButtonBorderColor", "AccentButtonBorder"),
        ("AccentButtonHoverBorderColor", "AccentButtonHoverBorder"),
        ("ConversationSelectedColor", "ConversationSelected"),
        ("GlassPanelColor", "GlassPanel"),
        ("GlassOverlayColor", "GlassOverlay"),
        ("GlassInputBgColor", "GlassInputBg"),
        ("GlassInputFocusBgColor", "GlassInputFocusBg"),
        ("BubbleMineColor", "BubbleMine"),
        ("BubbleTheirsColor", "BubbleTheirs"),
        ("TermBtnBgColor", "TermBtnBg"),
        ("TermBtnHoverBgColor", "TermBtnHoverBg"),
        ("TermBtnHoverBorderColor", "TermBtnHoverBorder"),
        ("GroupBtnBgColor", "GroupBtnBg"),
        ("GroupBtnHoverBgColor", "GroupBtnHoverBg"),
        ("GroupBtnBorderColor", "GroupBtnBorder"),
        ("GroupBtnHoverBorderColor", "GroupBtnHoverBorder"),
        ("InputFocusRingColor", "InputFocusRing"),
        ("ScrollBarTrackBgColor", "ScrollBarTrackBg"),
        ("BadgeBgColor", "BadgeBg"),
        ("UnreadSeparatorBgColor", "UnreadSeparatorBg"),
        ("UnreadCountBgColor", "UnreadCountBg"),
        ("OverlayBgColor", "OverlayBg"),
        ("PanelBgColor", "PanelBg"),
        ("ContainerBgColor", "ContainerBg"),
        ("ListBoxHoverBgColor", "ListBoxHoverBg")
    ];

    public static ThemePresetDefinition CurrentPreset { get; private set; } = ThemePresetCatalog.Default;
    public static event Action<ThemePresetDefinition>? PresetApplied;

    public static bool TryApplyThemePreset(string? presetId) {
        if (Application.Current?.Resources is not ResourceDictionary resources)
            return false;

        var preset = ThemePresetCatalog.GetById(presetId);

        try {
            var next = new ResourceDictionary {
                Source = new Uri($"Themes/{preset.PaletteThemeFile}", UriKind.Relative)
            };

            var merged = resources.MergedDictionaries;
            var existingIndex = merged
                .Select((dict, index) => new { dict, index })
                .FirstOrDefault(item =>
                    item.dict.Source?.OriginalString.Contains("Themes/Theme.", StringComparison.OrdinalIgnoreCase) == true)
                ?.index;

            if (existingIndex is int index)
                merged[index] = next;
            else
                merged.Add(next);

            ApplyLivePalette(resources, next);
            CurrentPreset = preset;
            PresetApplied?.Invoke(preset);
            RefreshOpenWindows();
            return true;
        } catch (Exception ex) {
            AppLog.Warn("theme", $"failed to apply preset {preset.Id}: {ex.Message}");
            return false;
        }
    }

    public static ImageSource? LoadImageSource(string? packUriOrPath) {
        if (string.IsNullOrWhiteSpace(packUriOrPath)) return null;

        try {
            var uri = new Uri(packUriOrPath, UriKind.Absolute);
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = uri;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        } catch (Exception ex) {
            AppLog.Warn("theme", $"failed to load themed image {Path.GetFileName(packUriOrPath)}: {ex.Message}");
            return null;
        }
    }

    static void ApplyLivePalette(ResourceDictionary targetResources, ResourceDictionary themeResources) {
        foreach (var (colorKey, brushKey) in ThemeBrushMappings) {
            if (!TryGetColor(themeResources, colorKey, out var color)) continue;

            targetResources[colorKey] = color;

            // Recreate the brush to ensure it picks up the new color
            // This is more reliable than trying to modify an existing brush
            if (targetResources.Contains(brushKey)) {
                targetResources[brushKey] = new SolidColorBrush(color);
            }
        }

        if (targetResources.Contains("AppBackgroundBrush") &&
            targetResources["AppBackgroundBrush"] is LinearGradientBrush background &&
            !background.IsFrozen &&
            background.GradientStops.Count >= 3) {
            if (TryGetColor(themeResources, "AppBackgroundStartColor", out var startColor)) {
                targetResources["AppBackgroundStartColor"] = startColor;
                background.GradientStops[0].Color = startColor;
            }

            if (TryGetColor(themeResources, "AppBackgroundMidColor", out var midColor)) {
                targetResources["AppBackgroundMidColor"] = midColor;
                background.GradientStops[1].Color = midColor;
            }

            if (TryGetColor(themeResources, "AppBackgroundEndColor", out var endColor)) {
                targetResources["AppBackgroundEndColor"] = endColor;
                background.GradientStops[2].Color = endColor;
            }
        }
    }

    static bool TryGetColor(ResourceDictionary resources, string key, out Color color) {
        if (resources.Contains(key) && resources[key] is Color resourceColor) {
            color = resourceColor;
            return true;
        }

        color = default;
        return false;
    }

    static void RefreshOpenWindows() {
        if (Application.Current == null) return;

        foreach (Window window in Application.Current.Windows) {
            window.InvalidateVisual();
        }
    }
}

using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;

namespace Cipher;

public enum ThemeShellBackgroundMode {
    GradientOnly,
    SceneWallpaper,
    MotifOnly
}

public enum ThemeGlassIntensityProfile {
    Default,
    Elevated,
    HighContrast
}

public sealed record ThemePresetDefinition(
    string Id,
    string DisplayName,
    string PaletteThemeFile,
    string KindLabel,
    string AccentHex,
    string BackdropHex,
    string? PreviewAssetPath = null,
    string? ShellBackgroundAssetPath = null,
    string? ChatMotifAssetPath = null,
    ThemeShellBackgroundMode ShellBackgroundMode = ThemeShellBackgroundMode.GradientOnly,
    double ShellWallpaperOpacity = 0,
    double ShellOverlayOpacity = 0,
    double ChatMotifOpacity = 0,
    double ChatMotifMaxWidth = 0,
    double ChatMotifMaxHeight = 0,
    ThemeGlassIntensityProfile GlassIntensityProfile = ThemeGlassIntensityProfile.Default
) {
    public MediaColor AccentColor => (MediaColor)MediaColorConverter.ConvertFromString(AccentHex)!;
    public MediaColor BackdropColor => (MediaColor)MediaColorConverter.ConvertFromString(BackdropHex)!;
}

public static class ThemePresetCatalog {
    public const string DefaultPresetId = "Palette.Teal";

    static readonly IReadOnlyList<ThemePresetDefinition> Presets = [
        new(
            "Palette.Teal",
            "Teal",
            "Theme.Teal.xaml",
            "Palette",
            "#58D7B6",
            "#11252F"),
        new(
            "Palette.Violet",
            "Violet",
            "Theme.Violet.xaml",
            "Palette",
            "#9E8CFF",
            "#19143A"),
        new(
            "Palette.Blue",
            "Blue",
            "Theme.Blue.xaml",
            "Palette",
            "#7AA2FF",
            "#102B4E"),
        new(
            "Palette.Amber",
            "Amber",
            "Theme.Amber.xaml",
            "Palette",
            "#F6B15F",
            "#352012"),
        new(
            "Palette.Rose",
            "Rose",
            "Theme.Rose.xaml",
            "Palette",
            "#FF6B96",
            "#381225"),
        new(
            "Wallpaper.Castorice",
            "Castorice",
            "Theme.Castorice.xaml",
            "Wallpaper",
            "#C58DFF",
            "#302050",
            "pack://application:,,,/Assets/backgrounds/castorice-butterfly-3840x2160-25920.jpg",
            "pack://application:,,,/Assets/backgrounds/castorice-butterfly-3840x2160-25920.jpg",
            null,
            ThemeShellBackgroundMode.SceneWallpaper,
            0.28,
            0.36,
            0,
            0,
            0,
            ThemeGlassIntensityProfile.Elevated),
        new(
            "Wallpaper.Columbina",
            "Columbina",
            "Theme.Columbina.xaml",
            "Wallpaper",
            "#D8B7FF",
            "#201020",
            "pack://application:,,,/Assets/backgrounds/columbina-5k-3840x2160-25922.jpg",
            "pack://application:,,,/Assets/backgrounds/columbina-5k-3840x2160-25922.jpg",
            null,
            ThemeShellBackgroundMode.SceneWallpaper,
            0.24,
            0.42,
            0,
            0,
            0,
            ThemeGlassIntensityProfile.HighContrast),
        new(
            "Wallpaper.FoxInferno",
            "Fox Inferno",
            "Theme.FoxInferno.xaml",
            "Wallpaper",
            "#D60604",
            "#020201",
            "pack://application:,,,/Assets/backgrounds/fox.jpg",
            null,
            "pack://application:,,,/Assets/backgrounds/fox-motif-1024.png",
            ThemeShellBackgroundMode.MotifOnly,
            0,
            0.16,
            0.18,
            440,
            440,
            ThemeGlassIntensityProfile.HighContrast)
    ];

    public static IReadOnlyList<ThemePresetDefinition> All => Presets;

    public static ThemePresetDefinition Default => GetById(DefaultPresetId);

    public static ThemePresetDefinition GetById(string? id) =>
        Presets.FirstOrDefault(preset => string.Equals(preset.Id, id, StringComparison.Ordinal))
        ?? Default;

    public static ThemePresetDefinition Resolve(string? presetId, string? legacyThemeFile = null) {
        if (!string.IsNullOrWhiteSpace(presetId)) {
            return GetById(presetId);
        }

        return GetByLegacyThemeFile(legacyThemeFile);
    }

    public static ThemePresetDefinition GetByLegacyThemeFile(string? themeFile) {
        var normalized = string.IsNullOrWhiteSpace(themeFile)
            ? "Theme.Teal.xaml"
            : themeFile.Trim();

        return normalized switch {
            "Theme.Violet.xaml" => GetById("Palette.Violet"),
            "Theme.Blue.xaml" => GetById("Palette.Blue"),
            "Theme.Amber.xaml" => GetById("Palette.Amber"),
            "Theme.Rose.xaml" => GetById("Palette.Rose"),
            _ => Default
        };
    }
}

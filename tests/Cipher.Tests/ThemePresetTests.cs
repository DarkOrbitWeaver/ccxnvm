using Cipher;

namespace Cipher.Tests;

public class ThemePresetTests {
    [Fact]
    public void LegacyThemeFileMapsToExpectedPreset() {
        var preset = ThemePresetCatalog.GetByLegacyThemeFile("Theme.Violet.xaml");
        Assert.Equal("Palette.Violet", preset.Id);
    }

    [Fact]
    public void SanitizePromotesLegacyThemeFileToPresetId() {
        var sanitized = AppShellPreferencesStore.Sanitize(new AppShellPreferences(
            ThemePresetId: "",
            ThemeFile: "Theme.Amber.xaml"));

        Assert.Equal("Palette.Amber", sanitized.ThemePresetId);
        Assert.Equal("Theme.Amber.xaml", sanitized.ThemeFile);
    }

    [Fact]
    public void ResolveFallsBackToDefaultPresetForUnknownId() {
        var preset = ThemePresetCatalog.Resolve("Wallpaper.DoesNotExist", null);
        Assert.Equal(ThemePresetCatalog.DefaultPresetId, preset.Id);
    }
}

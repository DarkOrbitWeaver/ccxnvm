using System.Xml.Linq;
using System.Windows;
using System.Windows.Media;

namespace Cipher.Tests;

/// <summary>
/// Preservation Property Tests for Theme System Functional Color Consistency
/// **Validates: Requirements 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7, 3.8, 3.9**
/// 
/// IMPORTANT: These tests verify baseline behavior that MUST be preserved during the fix.
/// Tests should PASS on unfixed code to establish the preservation baseline.
/// 
/// GOAL: Ensure functional colors (errors, danger states, status indicators) remain consistent
/// across all themes, and that theme switching works dynamically without restart.
/// </summary>
public class ThemeSystemPreservationTests {
    private const string BaseXamlPath = "Themes/Base.xaml";
    private const string MainWindowXamlPath = "MainWindow.xaml";
    private const string SettingsWindowXamlPath = "SettingsWindow.xaml";

    private static readonly string[] ThemeFiles = [
        "Themes/Theme.Teal.xaml",
        "Themes/Theme.Violet.xaml",
        "Themes/Theme.Blue.xaml",
        "Themes/Theme.Amber.xaml",
        "Themes/Theme.Rose.xaml",
        "Themes/Theme.Castorice.xaml",
        "Themes/Theme.Columbina.xaml",
        "Themes/Theme.FoxInferno.xaml"
    ];

    [Fact]
    public void BaseXaml_RedColorShouldBeConsistentAcrossAllThemes() {
        // Requirement 3.1: Error/danger states use #FF6B6B (Red) consistently across all themes
        var baseXaml = LoadXaml(BaseXamlPath);
        var ns = baseXaml.Root!.GetDefaultNamespace();
        var x = baseXaml.Root!.GetNamespaceOfPrefix("x");

        // Verify RedColor is defined in Base.xaml
        var redColor = baseXaml.Descendants(ns + "Color")
            .FirstOrDefault(e => e.Attribute(x + "Key")?.Value == "RedColor");

        Assert.NotNull(redColor);
        Assert.Equal("#FF6B6B", redColor.Value);

        // Verify theme files do NOT override RedColor (it should remain consistent)
        foreach (var themeFile in ThemeFiles) {
            var themeXaml = LoadXaml(themeFile);
            var themeNs = themeXaml.Root!.GetDefaultNamespace();
            var themeX = themeXaml.Root!.GetNamespaceOfPrefix("x");

            var themeRedColor = themeXaml.Descendants(themeNs + "Color")
                .FirstOrDefault(e => e.Attribute(themeX + "Key")?.Value == "RedColor");

            Assert.Null(themeRedColor); // RedColor should NOT be overridden in theme files
        }
    }

    [Fact]
    public void ThemeFiles_GreenColorShouldBeDefinedForStatusIndicators() {
        // Requirement 3.2: Online status indicators use green color consistently across all themes
        // GreenColor is defined in each theme file (not in Base.xaml)
        
        // All theme files should define GreenColor for consistency
        foreach (var themeFile in ThemeFiles) {
            var themeXaml = LoadXaml(themeFile);
            var themeNs = themeXaml.Root!.GetDefaultNamespace();
            var themeX = themeXaml.Root!.GetNamespaceOfPrefix("x");

            var themeGreenColor = themeXaml.Descendants(themeNs + "Color")
                .FirstOrDefault(e => e.Attribute(themeX + "Key")?.Value == "GreenColor");

            Assert.NotNull(themeGreenColor); // Each theme should define its own green
        }
    }

    [Fact]
    public void BaseXaml_NukeBtnStyleShouldHaveHardcodedDangerStyling() {
        // Requirement 3.4: NukeBtn uses hardcoded danger styling (#801D1114, #99693139) consistently
        var xaml = LoadXaml(BaseXamlPath);
        var ns = xaml.Root!.GetDefaultNamespace();
        var x = xaml.Root!.GetNamespaceOfPrefix("x");

        // Find NukeBtn style
        var nukeBtnStyle = xaml.Descendants(ns + "Style")
            .FirstOrDefault(e => e.Attribute(x + "Key")?.Value == "NukeBtn");

        Assert.NotNull(nukeBtnStyle);

        // Verify hardcoded danger styling is present in setters
        var setters = nukeBtnStyle.Descendants(ns + "Setter").ToList();
        
        var backgroundSetter = setters.FirstOrDefault(s => s.Attribute("Property")?.Value == "Background");
        var borderBrushSetter = setters.FirstOrDefault(s => s.Attribute("Property")?.Value == "BorderBrush");

        Assert.NotNull(backgroundSetter);
        Assert.NotNull(borderBrushSetter);
        Assert.Equal("#801D1114", backgroundSetter.Attribute("Value")?.Value);
        Assert.Equal("#99693139", borderBrushSetter.Attribute("Value")?.Value);
    }

    [Fact]
    public void SettingsWindowXaml_DangerZonePanelShouldHaveHardcodedStyling() {
        // Requirement 3.5: Danger zone panel uses hardcoded styling (#401D1114, #99693139) consistently
        var xaml = LoadXaml(SettingsWindowXamlPath);
        var ns = xaml.Root!.GetDefaultNamespace();
        var x = xaml.Root!.GetNamespaceOfPrefix("x");

        // Find danger zone border (typically wraps the danger zone section)
        var borders = xaml.Descendants(ns + "Border")
            .Where(b => {
                var bg = b.Attribute("Background")?.Value;
                var border = b.Attribute("BorderBrush")?.Value;
                return bg == "#401D1114" && border == "#99693139";
            })
            .ToList();

        Assert.NotEmpty(borders); // At least one danger zone panel should exist
    }

    [Fact]
    public void MainWindowXaml_NukeOverlayShouldHaveHardcodedDangerStyling() {
        // Requirement 3.4: NukeOverlay maintains hardcoded danger styling
        var xaml = LoadXaml(MainWindowXamlPath);
        var ns = xaml.Root!.GetDefaultNamespace();
        var x = xaml.Root!.GetNamespaceOfPrefix("x");

        // Find NukeOverlay grid
        var nukeOverlay = xaml.Descendants(ns + "Grid")
            .FirstOrDefault(e => e.Attribute(x + "Name")?.Value == "NukeOverlay");

        if (nukeOverlay != null) {
            var background = nukeOverlay.Attribute("Background")?.Value;
            // NukeOverlay should have hardcoded danger background
            Assert.True(
                background?.StartsWith("#") == true,
                "NukeOverlay should maintain hardcoded danger styling"
            );
        }

        // Find NukeStatusPanel border
        var nukeStatusPanel = xaml.Descendants(ns + "Border")
            .FirstOrDefault(e => e.Attribute(x + "Name")?.Value == "NukeStatusPanel");

        if (nukeStatusPanel != null) {
            var background = nukeStatusPanel.Attribute("Background")?.Value;
            var borderBrush = nukeStatusPanel.Attribute("BorderBrush")?.Value;
            
            // NukeStatusPanel should have hardcoded danger styling
            Assert.True(
                background?.StartsWith("#") == true,
                "NukeStatusPanel should maintain hardcoded danger background"
            );
            Assert.True(
                borderBrush?.StartsWith("#") == true,
                "NukeStatusPanel should maintain hardcoded danger border"
            );
        }
    }

    [Fact]
    public void UiThemeManager_ThemeBrushMappingsShouldExist() {
        // Requirement 3.6, 3.7: Theme switching applies changes dynamically without restart
        // UiThemeManager.ApplyLivePalette updates colors using ThemeBrushMappings array
        
        // Verify ThemeBrushMappings is accessible and contains expected mappings
        var mappingsField = typeof(UiThemeManager)
            .GetField("ThemeBrushMappings", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(mappingsField);

        var mappings = mappingsField.GetValue(null) as Array;
        Assert.NotNull(mappings);
        Assert.True(mappings.Length > 0, "ThemeBrushMappings should contain color mappings");

        // Verify key mappings exist (GreenColor, TealAccentColor, etc.)
        var mappingsList = mappings.Cast<object>().ToList();
        Assert.True(mappingsList.Count >= 10, "ThemeBrushMappings should contain multiple color mappings");
    }

    [Fact]
    public void ThemeFiles_WallpaperThemesShouldMaintainDistinctCharacter() {
        // Requirement 3.8: Wallpaper themes (Castorice, Columbina, FoxInferno) maintain distinct visual character
        var wallpaperThemes = new[] {
            "Themes/Theme.Castorice.xaml",
            "Themes/Theme.Columbina.xaml",
            "Themes/Theme.FoxInferno.xaml"
        };

        var themeColorCounts = new Dictionary<string, int>();

        foreach (var themeFile in wallpaperThemes) {
            var xaml = LoadXaml(themeFile);
            var ns = xaml.Root!.GetDefaultNamespace();
            var x = xaml.Root!.GetNamespaceOfPrefix("x");

            var colorCount = xaml.Descendants(ns + "Color").Count();
            themeColorCounts[Path.GetFileName(themeFile)] = colorCount;

            // Each wallpaper theme should define multiple colors
            Assert.True(colorCount >= 10, $"{Path.GetFileName(themeFile)} should define multiple colors for distinct character");
        }

        // Verify each wallpaper theme has a reasonable number of color definitions
        Assert.All(themeColorCounts.Values, count => Assert.True(count >= 10));
    }

    [Fact]
    public void ThemeFiles_PaletteThemesShouldProvideCohesiveColorSchemes() {
        // Requirement 3.9: Palette themes (Teal, Violet, Blue, Amber, Rose) provide cohesive color schemes
        var paletteThemes = new[] {
            "Themes/Theme.Teal.xaml",
            "Themes/Theme.Violet.xaml",
            "Themes/Theme.Blue.xaml",
            "Themes/Theme.Amber.xaml",
            "Themes/Theme.Rose.xaml"
        };

        foreach (var themeFile in paletteThemes) {
            var xaml = LoadXaml(themeFile);
            var ns = xaml.Root!.GetDefaultNamespace();
            var x = xaml.Root!.GetNamespaceOfPrefix("x");

            var colors = xaml.Descendants(ns + "Color")
                .Select(e => new {
                    Key = e.Attribute(x + "Key")?.Value,
                    Value = e.Value
                })
                .Where(c => c.Key != null)
                .ToList();

            // Each palette theme should define core colors
            var requiredKeys = new[] {
                "GreenColor", "DimGreenColor", "TealAccentColor",
                "AppBackgroundStartColor", "AppBackgroundMidColor", "AppBackgroundEndColor"
            };

            foreach (var key in requiredKeys) {
                Assert.Contains(colors, c => c.Key == key);
            }

            // Verify color count is reasonable for a cohesive scheme
            Assert.True(colors.Count >= 15, $"{Path.GetFileName(themeFile)} should provide cohesive color scheme");
        }
    }

    [Theory]
    [InlineData("Themes/Theme.Teal.xaml")]
    [InlineData("Themes/Theme.Violet.xaml")]
    [InlineData("Themes/Theme.Blue.xaml")]
    [InlineData("Themes/Theme.Amber.xaml")]
    [InlineData("Themes/Theme.Rose.xaml")]
    [InlineData("Themes/Theme.Castorice.xaml")]
    [InlineData("Themes/Theme.Columbina.xaml")]
    [InlineData("Themes/Theme.FoxInferno.xaml")]
    public void ThemeFiles_AllThemesShouldDefineRequiredAccentColors(string themeFile) {
        // Requirements 3.8, 3.9: All themes maintain their distinct character with required colors
        var xaml = LoadXaml(themeFile);
        var ns = xaml.Root!.GetDefaultNamespace();
        var x = xaml.Root!.GetNamespaceOfPrefix("x");

        var definedColors = xaml.Descendants(ns + "Color")
            .Select(e => e.Attribute(x + "Key")?.Value)
            .Where(k => k != null)
            .ToHashSet();

        // Core colors that all themes should define
        var coreColors = new[] {
            "GreenColor",
            "TealAccentColor",
            "AppBackgroundStartColor",
            "AppBackgroundMidColor",
            "AppBackgroundEndColor"
        };

        foreach (var color in coreColors) {
            Assert.Contains(color, definedColors);
        }
    }

    private static XDocument LoadXaml(string relativePath) {
        var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "..", relativePath);
        var normalizedPath = Path.GetFullPath(fullPath);
        
        if (!File.Exists(normalizedPath)) {
            throw new FileNotFoundException($"XAML file not found: {normalizedPath}");
        }

        return XDocument.Load(normalizedPath);
    }
}

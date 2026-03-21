using System.Xml.Linq;

namespace Cipher.Tests;

/// <summary>
/// Bug Condition Exploration Test for Theme System Hardcoded Colors
/// **Validates: Requirements 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.7, 1.8, 1.9, 1.10, 1.11, 1.12, 1.13, 1.14**
/// 
/// CRITICAL: This test MUST FAIL on unfixed code - failure confirms the bug exists.
/// This test encodes the expected behavior - it will validate the fix when it passes after implementation.
/// 
/// GOAL: Surface counterexamples that demonstrate hardcoded colors exist in the theme system.
/// </summary>
public class ThemeSystemBugConditionTests {
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

    private static readonly string[] RequiredColorResources = [
        "TermBtnBgColor",
        "TermBtnHoverBgColor",
        "TermBtnHoverBorderColor",
        "GroupBtnBgColor",
        "GroupBtnHoverBgColor",
        "InputFocusRingColor",
        "ScrollBarTrackBgColor",
        "BadgeBgColor",
        "OverlayBgColor",
        "PanelBgColor",
        "ContainerBgColor",
        "ListBoxHoverBgColor"
    ];

    [Fact]
    public void BaseXaml_ShouldNotHaveHardcodedTermBtnColors() {
        // Requirement 1.3: TermBtn style uses hardcoded colors
        var xaml = LoadXaml(BaseXamlPath);
        var ns = xaml.Root!.GetDefaultNamespace();
        var x = xaml.Root!.GetNamespaceOfPrefix("x");

        // Find TermBtn style
        var termBtnStyle = xaml.Descendants(ns + "Style")
            .FirstOrDefault(e => e.Attribute(x + "Key")?.Value == "TermBtn");

        Assert.NotNull(termBtnStyle);

        // Check for hardcoded background color
        var backgroundSetter = termBtnStyle.Descendants(ns + "Setter")
            .FirstOrDefault(s => s.Attribute("Property")?.Value == "Background");
        
        var hardcodedBg = backgroundSetter?.Attribute("Value")?.Value;
        Assert.False(
            hardcodedBg == "#50162840",
            $"COUNTEREXAMPLE: TermBtn has hardcoded Background='{hardcodedBg}' instead of dynamic resource"
        );

        // Check for hardcoded hover colors in triggers
        var triggers = termBtnStyle.Descendants(ns + "Trigger");
        foreach (var trigger in triggers) {
            var setters = trigger.Descendants(ns + "Setter");
            foreach (var setter in setters) {
                var property = setter.Attribute("Property")?.Value;
                var value = setter.Attribute("Value")?.Value;

                if (property == "Background" && value == "#641D324C") {
                    Assert.Fail($"COUNTEREXAMPLE: TermBtn hover has hardcoded Background='{value}' instead of dynamic resource");
                }
                if (property == "BorderBrush" && value == "#55708CAD") {
                    Assert.Fail($"COUNTEREXAMPLE: TermBtn hover has hardcoded BorderBrush='{value}' instead of dynamic resource");
                }
            }
        }
    }

    [Fact]
    public void BaseXaml_ShouldNotHaveHardcodedGroupBtnColors() {
        // Requirement 1.4: GroupBtn style uses hardcoded amber colors
        var xaml = LoadXaml(BaseXamlPath);
        var ns = xaml.Root!.GetDefaultNamespace();
        var x = xaml.Root!.GetNamespaceOfPrefix("x");

        var groupBtnStyle = xaml.Descendants(ns + "Style")
            .FirstOrDefault(e => e.Attribute(x + "Key")?.Value == "GroupBtn");

        Assert.NotNull(groupBtnStyle);

        // Check for hardcoded colors
        var setters = groupBtnStyle.Descendants(ns + "Setter");
        foreach (var setter in setters) {
            var property = setter.Attribute("Property")?.Value;
            var value = setter.Attribute("Value")?.Value;

            if (property == "Background" && value == "#1AF2B55B") {
                Assert.Fail($"COUNTEREXAMPLE: GroupBtn has hardcoded Background='{value}' instead of dynamic resource");
            }
            if (property == "BorderBrush" && value == "#4DF2B55B") {
                Assert.Fail($"COUNTEREXAMPLE: GroupBtn has hardcoded BorderBrush='{value}' instead of dynamic resource");
            }
        }
    }

    [Fact]
    public void BaseXaml_ShouldNotHaveHardcodedInputFocusRingColor() {
        // Requirement 1.5: Input focus rings use hardcoded #3358D7B6
        var xaml = LoadXaml(BaseXamlPath);
        var ns = xaml.Root!.GetDefaultNamespace();
        var x = xaml.Root!.GetNamespaceOfPrefix("x");

        var inputStyles = new[] { "TermInput", "TermPasswordInput", "TermRichInput" };
        var counterexamples = new List<string>();

        foreach (var styleName in inputStyles) {
            var style = xaml.Descendants(ns + "Style")
                .FirstOrDefault(e => e.Attribute(x + "Key")?.Value == styleName);

            if (style == null) continue;

            // Find focus ring border brush setters
            var triggers = style.Descendants(ns + "Trigger");
            foreach (var trigger in triggers) {
                var setters = trigger.Descendants(ns + "Setter");
                foreach (var setter in setters) {
                    var targetName = setter.Attribute("TargetName")?.Value;
                    var property = setter.Attribute("Property")?.Value;
                    var value = setter.Attribute("Value")?.Value;

                    if (targetName == "focusRing" && property == "BorderBrush" && value == "#3358D7B6") {
                        counterexamples.Add($"{styleName} focus ring has hardcoded BorderBrush='{value}'");
                    }
                }
            }
        }

        Assert.False(
            counterexamples.Any(),
            $"COUNTEREXAMPLES: {string.Join("; ", counterexamples)}"
        );
    }

    [Fact]
    public void BaseXaml_ShouldNotHaveHardcodedScrollBarTrackBackground() {
        // Requirement 1.6: ScrollBar uses hardcoded #22000000 track background
        var xaml = LoadXaml(BaseXamlPath);
        var ns = xaml.Root!.GetDefaultNamespace();
        var x = xaml.Root!.GetNamespaceOfPrefix("x");

        var scrollBarTemplates = new[] { "VerticalScrollBarTemplate", "HorizontalScrollBarTemplate" };
        var counterexamples = new List<string>();

        foreach (var templateName in scrollBarTemplates) {
            var template = xaml.Descendants(ns + "ControlTemplate")
                .FirstOrDefault(e => e.Attribute(x + "Key")?.Value == templateName);

            if (template == null) continue;

            // Find borders with hardcoded background
            var borders = template.Descendants(ns + "Border");
            foreach (var border in borders) {
                var background = border.Attribute("Background")?.Value;
                if (background == "#22000000") {
                    counterexamples.Add($"{templateName} has hardcoded track Background='{background}'");
                }
            }
        }

        Assert.False(
            counterexamples.Any(),
            $"COUNTEREXAMPLES: {string.Join("; ", counterexamples)}"
        );
    }

    [Fact]
    public void MainWindowXaml_ShouldNotHaveHardcodedUIElementBackgrounds() {
        // Requirements 1.7-1.13: Various UI elements use hardcoded background colors
        var xaml = LoadXaml(MainWindowXamlPath);
        var ns = xaml.Root!.GetDefaultNamespace();
        var x = xaml.Root!.GetNamespaceOfPrefix("x");

        var counterexamples = new List<string>();

        // Find all borders with hardcoded backgrounds
        var borders = xaml.Descendants(ns + "Border");
        foreach (var border in borders) {
            var background = border.Attribute("Background")?.Value;
            var name = border.Attribute(x + "Name")?.Value ?? "unnamed";

            // Check for specific hardcoded colors (excluding danger zone colors)
            if (background == "#14000000") {
                counterexamples.Add($"Border '{name}' has hardcoded Background='{background}' (unread separator/container)");
            } else if (background == "#20303F") {
                counterexamples.Add($"Border '{name}' has hardcoded Background='{background}' (conversation badge)");
            } else if (background == "#3A2A17") {
                counterexamples.Add($"Border '{name}' has hardcoded Background='{background}' (unread count badge)");
            } else if (background == "#12000000") {
                counterexamples.Add($"Border '{name}' has hardcoded Background='{background}' (container)");
            } else if (background == "#1A2432") {
                counterexamples.Add($"Border '{name}' has hardcoded Background='{background}' (status panel)");
            }
        }

        // Check for hardcoded overlay backgrounds in Grid elements
        var grids = xaml.Descendants(ns + "Grid");
        foreach (var grid in grids) {
            var background = grid.Attribute("Background")?.Value;
            var name = grid.Attribute(x + "Name")?.Value ?? "unnamed";

            if (background == "#B3000000" && !name.Contains("Nuke")) {
                counterexamples.Add($"Grid '{name}' has hardcoded Background='{background}' (overlay)");
            }
        }

        Assert.False(
            counterexamples.Any(),
            $"COUNTEREXAMPLES: {string.Join("; ", counterexamples)}"
        );
    }

    [Fact]
    public void SettingsWindowXaml_PaletteSwatchesShouldNotUseIndividualStackPanels() {
        // Requirements 1.1, 1.2: Palette swatches use individual StackPanels with fixed 58x58px buttons
        var xaml = LoadXaml(SettingsWindowXamlPath);
        var ns = xaml.Root!.GetDefaultNamespace();
        var x = xaml.Root!.GetNamespaceOfPrefix("x");

        var paletteButtons = new[] { "BtnThemeTeal", "BtnThemeViolet", "BtnThemeBlue", "BtnThemeAmber", "BtnThemeRose" };
        var counterexamples = new List<string>();

        foreach (var buttonName in paletteButtons) {
            var button = xaml.Descendants(ns + "Button")
                .FirstOrDefault(e => e.Attribute(x + "Name")?.Value == buttonName);

            if (button == null) continue;

            // Check if button is wrapped in a StackPanel
            var parent = button.Parent;
            if (parent?.Name.LocalName == "StackPanel") {
                counterexamples.Add($"{buttonName} is wrapped in individual StackPanel");
            }

            // Check for fixed width/height
            var width = button.Attribute("Width")?.Value;
            var height = button.Attribute("Height")?.Value;
            if (width == "58" && height == "58") {
                counterexamples.Add($"{buttonName} has fixed size Width='{width}' Height='{height}'");
            }
        }

        Assert.False(
            counterexamples.Any(),
            $"COUNTEREXAMPLES: {string.Join("; ", counterexamples)}"
        );
    }

    [Fact]
    public void ThemeFiles_ShouldHaveRequiredColorResources() {
        // Requirement 1.14: Theme files lack required color resources
        var counterexamples = new List<string>();

        foreach (var themeFile in ThemeFiles) {
            var xaml = LoadXaml(themeFile);
            var ns = xaml.Root!.GetDefaultNamespace();
            var x = xaml.Root!.GetNamespaceOfPrefix("x");

            var definedColors = xaml.Descendants(ns + "Color")
                .Select(e => e.Attribute(x + "Key")?.Value)
                .Where(k => k != null)
                .ToHashSet();

            var missingColors = RequiredColorResources
                .Where(required => !definedColors.Contains(required))
                .ToList();

            if (missingColors.Any()) {
                counterexamples.Add($"{Path.GetFileName(themeFile)} lacks: {string.Join(", ", missingColors)}");
            }
        }

        Assert.False(
            counterexamples.Any(),
            $"COUNTEREXAMPLES: {string.Join("; ", counterexamples)}"
        );
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

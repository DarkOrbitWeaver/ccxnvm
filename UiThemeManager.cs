using System.Windows;
using Application = System.Windows.Application;

namespace Cipher;

public static class UiThemeManager {
    public static bool TryApplyTheme(string themeFile) {
        if (Application.Current?.Resources is not ResourceDictionary resources)
            return false;

        try {
            var next = new ResourceDictionary {
                Source = new Uri($"Themes/{themeFile}", UriKind.Relative)
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

            return true;
        } catch (Exception ex) {
            AppLog.Warn("theme", $"failed to apply {themeFile}: {ex.Message}");
            return false;
        }
    }
}

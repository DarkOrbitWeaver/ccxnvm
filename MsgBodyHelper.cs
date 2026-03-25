using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Collections.Concurrent;
using Application = System.Windows.Application;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;
using RichTextBox = System.Windows.Controls.RichTextBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace Cipher;

public static class MsgBodyHelper {
    public sealed record MessageRenderProfile(
        bool IsEmojiOnly,
        int EmojiCount,
        bool UseBubblelessLayout,
        double EffectiveFontSize
    );

    static readonly FontFamily EmojiFont = new("Segoe UI Emoji");
    static readonly IReadOnlyDictionary<string, OpenMojiEntry> EmojiEntries =
        OpenMojiCatalog.Entries.ToDictionary(entry => entry.Emoji, StringComparer.Ordinal);
    static readonly ConcurrentDictionary<string, ImageSource> EmojiImageCache = new(StringComparer.Ordinal);

    public static readonly DependencyProperty ContentProperty =
        DependencyProperty.RegisterAttached(
            "Content",
            typeof(MessageViewModel),
            typeof(MsgBodyHelper),
            new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty RenderFontSizeProperty =
        DependencyProperty.RegisterAttached(
            "RenderFontSize",
            typeof(double),
            typeof(MsgBodyHelper),
            new PropertyMetadata(15d, OnRenderFontSizeChanged));

    public static readonly DependencyProperty PlainTextProperty =
        DependencyProperty.RegisterAttached(
            "PlainText",
            typeof(string),
            typeof(MsgBodyHelper),
            new PropertyMetadata("", OnPlainTextChanged));

    public static void SetContent(DependencyObject element, MessageViewModel value) =>
        element.SetValue(ContentProperty, value);

    public static MessageViewModel? GetContent(DependencyObject element) =>
        element.GetValue(ContentProperty) as MessageViewModel;

    public static void SetRenderFontSize(DependencyObject element, double value) =>
        element.SetValue(RenderFontSizeProperty, value);

    public static double GetRenderFontSize(DependencyObject element) =>
        element.GetValue(RenderFontSizeProperty) is double fontSize
            ? fontSize
            : 15d;

    public static void SetPlainText(DependencyObject element, string value) =>
        element.SetValue(PlainTextProperty, value);

    public static string GetPlainText(DependencyObject element) =>
        element.GetValue(PlainTextProperty) as string ?? "";

    static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
        if (d is RichTextBox rtb) {
            rtb.Document = BuildDocument(e.NewValue as MessageViewModel, GetRenderFontSize(rtb));
            return;
        }

        if (d is TextBlock textBlock) {
            RenderTextBlock(
                textBlock,
                (e.NewValue as MessageViewModel)?.Content ?? "",
                (e.NewValue as MessageViewModel)?.TextBrush ?? textBlock.Foreground,
                GetRenderFontSize(textBlock));
        }
    }

    static void OnRenderFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
        if (d is RichTextBox rtb) {
            rtb.Document = BuildDocument(GetContent(rtb), GetRenderFontSize(rtb));
            return;
        }

        if (d is TextBlock textBlock) {
            var vm = GetContent(textBlock);
            if (vm != null) {
                RenderTextBlock(textBlock, vm.Content, vm.TextBrush, GetRenderFontSize(textBlock));
            } else {
                RenderTextBlock(textBlock, GetPlainText(textBlock), textBlock.Foreground, GetRenderFontSize(textBlock));
            }
        }
    }

    static void OnPlainTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
        if (d is not TextBlock textBlock) return;
        RenderTextBlock(textBlock, e.NewValue as string ?? "", textBlock.Foreground, GetRenderFontSize(textBlock));
    }

    public static MessageRenderProfile BuildRenderProfile(string? text, double baseFontSize) {
        var normalized = text ?? "";
        var emojiCount = CountEmojiElements(normalized);
        var emojiOnly = IsEmojiOnlyMessage(normalized);
        return new MessageRenderProfile(
            IsEmojiOnly: emojiOnly,
            EmojiCount: emojiCount,
            UseBubblelessLayout: emojiOnly && emojiCount is > 0 and <= 3,
            EffectiveFontSize: emojiOnly ? baseFontSize * 2d : baseFontSize
        );
    }

    public static bool ContainsEmoji(string? text) =>
        CountEmojiElements(text ?? "") > 0;

    public static ImageSource? GetEmojiImageSource(string emoji) {
        if (!EmojiEntries.TryGetValue(emoji, out var entry)) {
            return null;
        }

        return GetEmojiImageSource(entry);
    }

    static FlowDocument BuildDocument(MessageViewModel? vm, double fontSize) {
        var text = vm?.Content ?? "";
        var renderProfile = BuildRenderProfile(text, fontSize);

        var doc = new FlowDocument {
            PagePadding = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            TextAlignment = TextAlignment.Left
        };

        var paragraph = new Paragraph {
            Margin = new Thickness(0),
            LineHeight = renderProfile.IsEmojiOnly
                ? renderProfile.EffectiveFontSize * 1.05
                : renderProfile.EffectiveFontSize * 1.28
        };

        var textBrush = vm?.TextBrush ?? System.Windows.Media.Brushes.White;
        var textFont = Application.Current?.TryFindResource("ChatFont") as FontFamily
            ?? new FontFamily("Segoe UI");

        var plainText = new StringBuilder();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) {
            var element = enumerator.GetTextElement();
            if (LooksLikeEmoji(element)) {
                FlushPlainText();
                paragraph.Inlines.Add(BuildEmojiInline(
                    element,
                    renderProfile.EffectiveFontSize,
                    renderProfile.IsEmojiOnly));
            } else {
                plainText.Append(element);
            }
        }

        FlushPlainText();
        doc.Blocks.Add(paragraph);
        return doc;

        void FlushPlainText() {
            if (plainText.Length == 0) return;
            paragraph.Inlines.Add(new Run(plainText.ToString()) {
                Foreground = textBrush,
                FontFamily = textFont,
                FontSize = renderProfile.EffectiveFontSize
            });
            plainText.Clear();
        }
    }

    static void RenderTextBlock(TextBlock textBlock, string text, System.Windows.Media.Brush textBrush, double fontSize) {
        var renderProfile = BuildRenderProfile(text, fontSize);
        var textFont = Application.Current?.TryFindResource("ChatFont") as FontFamily
            ?? new FontFamily("Segoe UI");

        textBlock.Inlines.Clear();
        textBlock.TextWrapping = TextWrapping.Wrap;
        textBlock.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        textBlock.LineHeight = renderProfile.IsEmojiOnly
            ? renderProfile.EffectiveFontSize * 1.05
            : renderProfile.EffectiveFontSize * 1.28;

        var plainText = new StringBuilder();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) {
            var element = enumerator.GetTextElement();
            if (LooksLikeEmoji(element)) {
                FlushPlainText();
                textBlock.Inlines.Add(BuildEmojiInline(
                    element,
                    renderProfile.EffectiveFontSize,
                    renderProfile.IsEmojiOnly));
            } else {
                plainText.Append(element);
            }
        }

        FlushPlainText();

        void FlushPlainText() {
            if (plainText.Length == 0) return;
            textBlock.Inlines.Add(new Run(plainText.ToString()) {
                Foreground = textBrush,
                FontFamily = textFont,
                FontSize = renderProfile.EffectiveFontSize
            });
            plainText.Clear();
        }
    }

    static Inline BuildEmojiInline(string element, double fontSize, bool emojiOnly) {
        if (TryCreateEmojiImage(element, fontSize, emojiOnly, out var image)) {
            return new InlineUIContainer(image) {
                BaselineAlignment = BaselineAlignment.Center
            };
        }

        return new Run(element) {
            FontFamily = EmojiFont,
            FontSize = fontSize
        };
    }

    static bool TryCreateEmojiImage(string element, double fontSize, bool emojiOnly, out Image image) {
        image = null!;
        if (!EmojiEntries.TryGetValue(element, out var entry)) {
            return false;
        }

        var source = GetEmojiImageSource(entry);
        if (source == null) {
            return false;
        }

        var size = emojiOnly ? fontSize : Math.Max(fontSize, fontSize * 1.12);
        image = new Image {
            Source = source,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Margin = emojiOnly
                ? new Thickness(0, -2, 0, -4)
                : new Thickness(0, -1, 0, -3)
        };
        return true;
    }

    static bool IsEmojiOnlyMessage(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return false;
        }

        var sawEmoji = false;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) {
            var element = enumerator.GetTextElement();
            if (string.IsNullOrWhiteSpace(element)) {
                continue;
            }

            if (!LooksLikeEmoji(element)) {
                return false;
            }

            sawEmoji = true;
        }

        return sawEmoji;
    }

    static int CountEmojiElements(string text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return 0;
        }

        var count = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) {
            var element = enumerator.GetTextElement();
            if (!string.IsNullOrWhiteSpace(element) && LooksLikeEmoji(element)) {
                count++;
            }
        }

        return count;
    }

    static ImageSource? GetEmojiImageSource(OpenMojiEntry entry) {
        try {
            return EmojiImageCache.GetOrAdd(entry.Code, _ => {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(entry.PackUri, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            });
        } catch {
            return null;
        }
    }

    static bool LooksLikeEmoji(string element) {
        foreach (var rune in element.EnumerateRunes()) {
            var value = rune.Value;
            if (value == 0x200D || value == 0xFE0F || value == 0x20E3)
                return true;
            if (value is >= 0x1F1E6 and <= 0x1F1FF)
                return true;
            if (value is >= 0x2300 and <= 0x23FF)
                return true;
            if (value is >= 0x1F300 and <= 0x1FAFF)
                return true;
            if (value is >= 0x2600 and <= 0x27BF)
                return true;
            if (value is >= 0x2B00 and <= 0x2BFF)
                return true;
        }

        return false;
    }
}

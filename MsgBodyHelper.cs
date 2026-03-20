using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using FontFamily = System.Windows.Media.FontFamily;
using Image = System.Windows.Controls.Image;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace Cipher;

public static class MsgBodyHelper {
    static readonly FontFamily EmojiFont = new("Segoe UI Emoji");
    static readonly IReadOnlyDictionary<string, OpenMojiEntry> EmojiEntries =
        OpenMojiCatalog.Entries.ToDictionary(entry => entry.Emoji, StringComparer.Ordinal);
    static readonly Dictionary<string, ImageSource> EmojiImageCache = [];

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

    static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
        if (d is not RichTextBox rtb) return;
        rtb.Document = BuildDocument(e.NewValue as MessageViewModel, GetRenderFontSize(rtb));
    }

    static void OnRenderFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
        if (d is not RichTextBox rtb) return;
        rtb.Document = BuildDocument(GetContent(rtb), GetRenderFontSize(rtb));
    }

    static FlowDocument BuildDocument(MessageViewModel? vm, double fontSize) {
        var text = vm?.Content ?? "";
        var emojiOnly = IsEmojiOnlyMessage(text);
        var effectiveFontSize = emojiOnly ? fontSize * 2d : fontSize;

        var doc = new FlowDocument {
            PagePadding = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            TextAlignment = TextAlignment.Left
        };

        var paragraph = new Paragraph {
            Margin = new Thickness(0),
            LineHeight = emojiOnly ? effectiveFontSize * 1.08 : effectiveFontSize * 1.35
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
                paragraph.Inlines.Add(BuildEmojiInline(element, effectiveFontSize, emojiOnly));
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
                FontSize = effectiveFontSize
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

    static ImageSource? GetEmojiImageSource(OpenMojiEntry entry) {
        if (EmojiImageCache.TryGetValue(entry.Code, out var cached)) {
            return cached;
        }

        try {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(entry.PackUri, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            EmojiImageCache[entry.Code] = bitmap;
            return bitmap;
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
            if (value is >= 0x1F300 and <= 0x1FAFF)
                return true;
            if (value is >= 0x2600 and <= 0x27BF)
                return true;
        }

        return false;
    }
}

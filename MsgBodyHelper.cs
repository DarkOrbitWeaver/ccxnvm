using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Application = System.Windows.Application;
using FontFamily = System.Windows.Media.FontFamily;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace Cipher;

public static class MsgBodyHelper {
    static readonly FontFamily EmojiFont = new("Segoe UI Emoji");

    public static readonly DependencyProperty ContentProperty =
        DependencyProperty.RegisterAttached(
            "Content",
            typeof(MessageViewModel),
            typeof(MsgBodyHelper),
            new PropertyMetadata(null, OnContentChanged));

    public static void SetContent(DependencyObject element, MessageViewModel value) =>
        element.SetValue(ContentProperty, value);

    public static MessageViewModel? GetContent(DependencyObject element) =>
        element.GetValue(ContentProperty) as MessageViewModel;

    static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
        if (d is not RichTextBox rtb) return;
        rtb.Document = BuildDocument(e.NewValue as MessageViewModel);
    }

    static FlowDocument BuildDocument(MessageViewModel? vm) {
        var fontSize = 15d;
        var doc = new FlowDocument {
            PagePadding = new Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            TextAlignment = TextAlignment.Left
        };

        var paragraph = new Paragraph {
            Margin = new Thickness(0),
            LineHeight = fontSize * 1.35
        };

        var text = vm?.Content ?? "";
        var textBrush = vm?.TextBrush ?? System.Windows.Media.Brushes.White;
        var textFont = Application.Current?.TryFindResource("ChatFont") as FontFamily
            ?? new FontFamily("Segoe UI");

        var plainText = new StringBuilder();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) {
            var element = enumerator.GetTextElement();
            if (LooksLikeEmoji(element)) {
                FlushPlainText();
                paragraph.Inlines.Add(new Run(element) {
                    FontFamily = EmojiFont,
                    FontSize = fontSize
                });
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
                FontSize = fontSize
            });
            plainText.Clear();
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

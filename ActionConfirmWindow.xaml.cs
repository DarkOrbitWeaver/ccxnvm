using System.Windows;

namespace Cipher;

public partial class ActionConfirmWindow : Window {
    public ActionConfirmWindow() {
        InitializeComponent();
    }

    public bool IsConfirmed { get; private set; }

    public static bool Show(
        Window owner,
        string title,
        string message,
        string confirmLabel,
        string hintText,
        bool destructive = false) {
        var dialog = new ActionConfirmWindow {
            Owner = owner
        };
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmLabel;
        dialog.HintText.Text = hintText;
        dialog.ConfirmButton.Style = (Style)dialog.FindResource(destructive ? "NukeBtn" : "AccentBtn");
        dialog.ShowDialog();
        return dialog.IsConfirmed;
    }

    void ConfirmButton_Click(object sender, RoutedEventArgs e) {
        IsConfirmed = true;
        DialogResult = true;
        Close();
    }

    void CancelButton_Click(object sender, RoutedEventArgs e) {
        IsConfirmed = false;
        DialogResult = false;
        Close();
    }
}

using System.Windows;

namespace Cipher;

public partial class App : Application {
    protected override void OnStartup(StartupEventArgs e) {
        // Global unhandled exception handler — never crash silently
        DispatcherUnhandledException += (_, args) => {
            MessageBox.Show($"[CIPHER ERROR]\n{args.Exception.Message}",
                "Cipher", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => {
            var msg = (args.ExceptionObject as Exception)?.Message ?? args.ExceptionObject.ToString();
            MessageBox.Show($"[CIPHER FATAL]\n{msg}", "Cipher", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        base.OnStartup(e);
    }
}

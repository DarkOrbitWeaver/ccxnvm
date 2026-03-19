using System.Windows;

namespace Cipher;

public partial class App : Application {
    protected override void OnStartup(StartupEventArgs e) {
        // Global unhandled exception handler — never crash silently
        DispatcherUnhandledException += (_, args) => {
            MessageBox.Show($"[{AppBranding.WindowTitle} ERROR]\n{args.Exception.Message}",
                AppBranding.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => {
            var msg = (args.ExceptionObject as Exception)?.Message ?? args.ExceptionObject.ToString();
            MessageBox.Show($"[{AppBranding.WindowTitle} FATAL]\n{msg}",
                AppBranding.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        };
        base.OnStartup(e);
    }
}

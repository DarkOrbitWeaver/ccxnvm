using System.Windows;
using System.Windows.Threading;
using Velopack;

namespace Cipher;

public partial class App : Application {
    public static StartupHealthReport LastStartupHealth { get; private set; } =
        new(true, Array.Empty<string>(), Array.Empty<string>());

    [STAThread]
    static void Main(string[] args) {
        VelopackApp.Build().Run();
        AppLog.Initialize();

        LastStartupHealth = StartupHealth.Run();
        foreach (var warning in LastStartupHealth.Warnings)
            AppLog.Warn("startup", warning);
        foreach (var error in LastStartupHealth.Errors)
            AppLog.Error("startup", error);

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e) {
        DispatcherUnhandledException += (_, args) => {
            HandleException("dispatcher", args.Exception, fatal: false);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) => {
            HandleException("task", args.Exception, fatal: false);
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) => {
            var ex = args.ExceptionObject as Exception ??
                     new Exception(args.ExceptionObject.ToString());
            HandleException("appdomain", ex, fatal: true);
        };

        Exit += (_, _) => AppLog.Info("app", "shutdown complete");
        base.OnStartup(e);
    }

    static void HandleException(string area, Exception ex, bool fatal) {
        AppLog.Error(area, fatal ? "fatal unhandled exception" : "unhandled exception", ex);

        try {
            MessageBox.Show(
                fatal
                    ? $"Cipher hit a fatal error and needs to close.\n\n{FriendlyErrors.ToUserMessage(ex)}"
                    : $"Cipher recovered from an unexpected error.\n\n{FriendlyErrors.ToUserMessage(ex)}",
                AppBranding.ProductName,
                MessageBoxButton.OK,
                fatal ? MessageBoxImage.Error : MessageBoxImage.Warning);
        } catch {
        }
    }
}

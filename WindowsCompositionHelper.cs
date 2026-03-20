using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Cipher;

static class WindowsCompositionHelper {
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    enum DwmSystemBackdropType {
        Auto = 0,
        None = 1,
        MainWindow = 2,
        TransientWindow = 3,
        TabbedWindow = 4
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int pvAttribute, int cbAttribute);

    public static void TryApplyMica(Window window) {
        try {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
                return;

            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            var darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            var backdrop = (int)DwmSystemBackdropType.MainWindow;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        } catch (DllNotFoundException) {
        } catch (EntryPointNotFoundException) {
        } catch (Exception ex) {
            AppLog.Warn("mica", $"failed to apply system backdrop: {ex.Message}");
        }
    }
}

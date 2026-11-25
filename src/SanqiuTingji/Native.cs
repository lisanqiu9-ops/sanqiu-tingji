using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace SanqiuTingji.App;

/// Windows APIs WPF does not wrap directly: Win11 DWM Liquid Glass (Acrylic + rounded corners),
/// non-activating floating panels, screen-capture exclusion, push-to-talk hook, and login launch.
internal static class Native
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WDA_NONE = 0x0;
    private const uint WDA_MONITOR = 0x1;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Real hardware Acrylic blur

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    /// Whether the OS supports Windows 11 22H2+ hardware system backdrop (Acrylic / Mica).
    public static bool SupportsWin11Backdrop => Environment.OSVersion.Version.Build >= 22621;

    /// Whether Windows transparency effects are currently enabled and High Contrast is off.
    public static bool SystemTransparencyEnabled
    {
        get
        {
            if (SystemParameters.HighContrast) return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key?.GetValue("EnableTransparency") is int v && v == 0) return false;
            }
            catch { }
            return true;
        }
    }

    /// Applies Windows 11 hardware Acrylic backdrop + rounded corners to a WPF window, with a clean
    /// frosted fallback on older Windows versions, High Contrast, disabled transparency, or snapshots.
    public static void ApplyLiquidGlass(Window window, bool dark = false, bool allowBackdrop = true)
    {
        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            try
            {
                var darkVal = dark ? 1 : 0;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, sizeof(int));

                var corner = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

                if (allowBackdrop && SupportsWin11Backdrop && SystemTransparencyEnabled && !window.AllowsTransparency)
                {
                    var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    DwmExtendFrameIntoClientArea(hwnd, ref margins);

                    var backdrop = DWMSBT_TRANSIENTWINDOW;
                    if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0)
                    {
                        window.Background = Brushes.Transparent;
                        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
                            target.BackgroundColor = Colors.Transparent;
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Debug($"glass: backdrop fallback ({e.Message})");
            }
            // Graceful fallback when hardware backdrop isn't active.
            if (!window.AllowsTransparency)
                window.Background = dark ? Theme.NightFallback : Theme.GlassWindowFallback;
        }

        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply();
        else window.SourceInitialized += (_, _) => Apply();
    }

    /// A panel that never takes focus from the meeting app and stays off the taskbar and Alt-Tab.
    public static void MakeNonActivating(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    /// Kept out of screen shares and recordings (Windows 10 2004+; older systems show it black).
    public static void SetCapturable(Window w, bool capturable)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        if (capturable) { SetWindowDisplayAffinity(hwnd, WDA_NONE); return; }
        if (!SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)) SetWindowDisplayAffinity(hwnd, WDA_MONITOR);
    }

    // MARK: Launch at login

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "SanqiuTingji";

    public static void SetLaunchAtLogin(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;
            if (on) key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue(RunName, false);
        }
        catch (Exception e)
        {
            Log.Debug($"login item: {e.Message}");
        }
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception e) { Log.Debug($"open: {e.Message}"); }
    }
}

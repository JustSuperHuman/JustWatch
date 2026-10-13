using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace JustWatch.TrayApp;

public partial class MainWindow
{
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarTheme();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        ApplyTitleBarTheme();
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        ApplyTitleBarTheme();
    }

    private void ApplyTitleBarTheme()
    {
        // Keep the native caption, including snap layouts, resizing and the system menu.
        // These color attributes are supported starting with Windows 11.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        var highContrast = SystemParameters.HighContrast;
        SetCaptionAttribute(handle, 20, highContrast ? 0u : 1u); // Immersive dark mode
        SetCaptionAttribute(handle, 33, highContrast ? 0u : 2u); // Rounded corners
        SetCaptionAttribute(handle, 34, highContrast ? uint.MaxValue
            : ColorRef(IsActive ? 0x365853u : 0x2A3543u)); // Border
        SetCaptionAttribute(handle, 35, highContrast ? uint.MaxValue : ColorRef(0x161E28)); // Caption
        SetCaptionAttribute(handle, 36, highContrast ? uint.MaxValue
            : ColorRef(IsActive ? 0xEDF5FCu : 0x899BB1u)); // Title text
    }

    private static uint ColorRef(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | (rgb >> 16);

    private static void SetCaptionAttribute(IntPtr handle, int attribute, uint value)
    {
        // Unsupported compositor settings may fail without affecting window behavior.
        _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(uint));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using QuickSweep.Native;

namespace QuickSweep.Ui;


internal static class WindowEffects
{
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(nint mon, int type, out uint x, out uint y);

    public static double ScaleForMonitor(nint mon)
    {
        try { if (GetDpiForMonitor(mon, 0, out uint x, out _) == 0 && x > 0) return x / 96.0; } catch { }
        return 1.0;
    }

    /// <summary>Dark title bar, rounded corners, thin border colour and a soft shadow on Windows 11.</summary>
    public static void Apply(Window w, bool borderless)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        int on = 1, round = 2;
        Win.DwmSetWindowAttribute(hwnd, 20, ref on, 4);     // immersive dark mode
        Win.DwmSetWindowAttribute(hwnd, 33, ref round, 4);  // rounded corners
        int border = 0x00453C3B;                             // BGR of #3B3C45
        Win.DwmSetWindowAttribute(hwnd, 34, ref border, 4);  // border colour
        if (!borderless)
        {
            int caption = 0x00241F1E;                        // BGR of #1E1F24
            Win.DwmSetWindowAttribute(hwnd, 35, ref caption, 4);
            int text = 0x00F5F2F2;
            Win.DwmSetWindowAttribute(hwnd, 36, ref text, 4);
        }
        else
        {
            var m = new Win.MARGINS { l = 1, r = 1, t = 1, b = 1 };
            Win.DwmExtendFrameIntoClientArea(hwnd, ref m);   // gives borderless windows a shadow
        }
    }
}

using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Win32;
using QuickSweep.Native;

namespace QuickSweep.Ui;

/// <summary>Notification-area icon through Shell_NotifyIcon (v4 callbacks), with silent balloons.</summary>

internal sealed unsafe class TrayIcon : IDisposable
{
    const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    const uint NIIF_INFO = 1, NIIF_WARNING = 2, NIIF_NOSOUND = 0x10;
    const uint CB = 0x8001;           // WM_APP + 1
    const int NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, WM_CONTEXTMENU = 0x7B, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize; public nint hWnd; public uint uID; public uint uFlags; public uint uCallbackMessage; public nint hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NOTIFYICONIDENTIFIER { public int cbSize; public nint hWnd; public uint uID; public Guid guidItem; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATA d);
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT r);

    readonly HwndSource _src;
    readonly uint _taskbarCreated = Win.RegisterWindowMessageW("TaskbarCreated");
    nint _hIcon;
    string _tip = "Quick Sweep";
    bool _added, _v4;

    public nint Hwnd => _src.Handle;
    public event Action? LeftClick;
    public event Action? RightClick;
    public event Action? Recreated;

    public TrayIcon()
    {
        var p = new HwndSourceParameters("QuickSweepTray") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 }; // HWND_MESSAGE
        _src = new HwndSource(p);
        _src.AddHook(WndProc);
    }

    nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if ((uint)msg == CB)
        {
            int ev = (int)((long)lParam & 0xFFFF);
            if (QuickSweep.Core.Log.Trace) QuickSweep.Core.Log.Write($"trace: tray callback 0x{ev:X}");
            // Version 4 sends WM_LBUTTONDOWN, WM_LBUTTONUP *and* NIN_SELECT for one left click (and WM_RBUTTONUP *and*
            // WM_CONTEXTMENU for a right click). Reacting to the raw mouse messages as well opened the flyout and closed it
            // again about 90 ms later. Only the semantic messages count; the raw ones are a fallback for version 3 behaviour.
            switch (ev)
            {
                case NIN_SELECT: case NIN_KEYSELECT: LeftClick?.Invoke(); break;
                case WM_CONTEXTMENU: RightClick?.Invoke(); break;
                case WM_LBUTTONUP when !_v4: LeftClick?.Invoke(); break;
                case WM_RBUTTONUP when !_v4: RightClick?.Invoke(); break;
            }
            handled = true;
        }
        else if ((uint)msg == _taskbarCreated)
        {
            _added = false; Add(); Recreated?.Invoke();
        }
        return 0;
    }

    NOTIFYICONDATA Base()
    {
        var d = new NOTIFYICONDATA { hWnd = _src.Handle, uID = 1 };
        d.cbSize = sizeof(NOTIFYICONDATA);
        return d;
    }

    static void CopyTo(char* dst, int max, string s)
    {
        int n = Math.Min(s.Length, max - 1);
        for (int i = 0; i < n; i++) dst[i] = s[i];
        dst[n] = '\0';
    }

    public void Add()
    {
        var d = Base();
        d.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        d.uCallbackMessage = CB; d.hIcon = _hIcon;
        CopyTo(d.szTip, 128, _tip);
        _added = Shell_NotifyIconW(NIM_ADD, ref d);
        d.uVersion = 4;
        _v4 = Shell_NotifyIconW(NIM_SETVERSION, ref d);
        if (QuickSweep.Core.Log.Trace) QuickSweep.Core.Log.Write($"trace: tray icon added, v4={_v4}");
    }

    public void Update(nint hIcon, string tip)
    {
        var old = _hIcon;
        _hIcon = hIcon; _tip = tip;
        if (!_added) { Add(); if (old != 0) Win.DestroyIcon(old); return; }
        var d = Base();
        d.uFlags = NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        d.hIcon = hIcon;
        CopyTo(d.szTip, 128, tip);
        Shell_NotifyIconW(NIM_MODIFY, ref d);
        if (old != 0) Win.DestroyIcon(old);
    }

    /// <summary>Quiet notification: never plays a sound.</summary>
    public void Balloon(string title, string text, bool warning)
    {
        var d = Base();
        d.uFlags = NIF_INFO;
        CopyTo(d.szInfo, 256, text);
        CopyTo(d.szInfoTitle, 64, title);
        d.dwInfoFlags = (warning ? NIIF_WARNING : NIIF_INFO) | NIIF_NOSOUND;
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    public RECT? GetRect()
    {
        var id = new NOTIFYICONIDENTIFIER { hWnd = _src.Handle, uID = 1 };
        id.cbSize = sizeof(NOTIFYICONIDENTIFIER);
        return Shell_NotifyIconGetRect(ref id, out var r) == 0 ? r : null;
    }

    public void Dispose()
    {
        if (_added) { var d = Base(); Shell_NotifyIconW(NIM_DELETE, ref d); _added = false; }
        if (_hIcon != 0) Win.DestroyIcon(_hIcon);
        _src.Dispose();
    }

    /// <summary>Native dark popup menu at the cursor. Returns the chosen command id or 0.</summary>
    public int ShowMenu(IReadOnlyList<(int id, string text, bool check, bool sep)> items)
    {
        Win.SetPreferredAppMode(2); // ForceDark
        Win.FlushMenuThemes();
        var menu = Win.CreatePopupMenu();
        try
        {
            foreach (var (id, text, check, sep) in items)
            {
                if (sep) Win.AppendMenuW(menu, Win.MF_SEPARATOR, 0, null);
                else Win.AppendMenuW(menu, Win.MF_STRING | (check ? Win.MF_CHECKED : 0), (nuint)id, text);
            }
            Win.GetCursorPos(out var pt);
            Win.SetForegroundWindow(_src.Handle);
            // TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RIGHTALIGN
            int cmd = Win.TrackPopupMenuEx(menu, 0x100 | 0x2 | 0x20 | 0x8, pt.X, pt.Y, _src.Handle, 0);
            Win.PostMessageW(_src.Handle, 0, 0, 0);
            return cmd;
        }
        finally { Win.DestroyMenu(menu); }
    }
}

/// <summary>Draws "CPU over GPU" temperatures with a crisp 5x7 pixel font straight into an ARGB icon.</summary>

internal static unsafe class TrayIconRenderer
{
    static readonly string[][] Glyphs =
    {
        new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." }, // 0
        new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." }, // 1
        new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" }, // 2
        new[] { "#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###." }, // 3
        new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." }, // 4
        new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." }, // 5
        new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###." }, // 6
        new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." }, // 7
        new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." }, // 8
        new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." }, // 9
    };
    static readonly string[] Dash = { ".....", ".....", ".....", ".###.", ".....", ".....", "....." };

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public nint hbmMask, hbmColor; }

    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER bmi, uint usage, out void* bits, nint section, uint offset);
    [DllImport("gdi32.dll")] static extern nint CreateBitmap(int w, int h, uint planes, uint bpp, void* bits);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint o);
    [DllImport("user32.dll")] static extern nint CreateIconIndirect(ref ICONINFO info);

    static bool _light; static DateTime _lightChecked;
    static bool LightTaskbar()
    {
        if (DateTime.Now - _lightChecked < TimeSpan.FromSeconds(15)) return _light;
        _lightChecked = DateTime.Now;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            _light = k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch { _light = false; }
        return _light;
    }

    static uint HeatColor(double? t, double warm, double hot, double crit, bool light, bool gpu)
    {
        // returns 0xRRGGBB
        if (t == null) return light ? 0x6B7280u : 0x9CA3AFu;
        if (t >= crit) return light ? 0xB91C1Cu : 0xFF6B6Bu;
        if (t >= hot) return light ? 0xC2410Cu : 0xFFA24Du;
        if (t >= warm) return light ? 0xA16207u : 0xFFD24Au;
        return light ? (gpu ? 0x166534u : 0x1E293Bu) : (gpu ? 0xA7F3C0u : 0xFFFFFFu);
    }

    /// <param name="cpu">CPU temp (null = unavailable)</param><param name="gpu">GPU temp (null = unavailable)</param>
    public static nint Make(double? cpu, double? gpu, int size = 0)
    {
        if (size <= 0) size = Win.GetSystemMetricsForDpi(49, Win.GetDpiForSystem()); // SM_CXSMICON
        if (size < 16) size = 16;
        bool light = LightTaskbar();
        var px = (uint*)NativeMemory.AllocZeroed((nuint)(size * size * 4));
        try
        {
            DrawRow(px, size, cpu, 0, HeatColor(cpu, 70, 80, 90, light, false));
            DrawRow(px, size, gpu, 8, HeatColor(gpu, 65, 75, 83, light, true));

            var bmi = new BITMAPINFOHEADER { biSize = sizeof(BITMAPINFOHEADER), biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
            nint color = CreateDIBSection(0, ref bmi, 0, out void* bits, 0, 0);
            Buffer.MemoryCopy(px, bits, size * size * 4, size * size * 4);
            int maskBytes = ((size + 31) / 32) * 4 * size;
            byte* mask = (byte*)NativeMemory.AllocZeroed((nuint)maskBytes);
            nint hMask = CreateBitmap(size, size, 1, 1, mask);
            NativeMemory.Free(mask);
            var ii = new ICONINFO { fIcon = true, hbmMask = hMask, hbmColor = color };
            nint icon = CreateIconIndirect(ref ii);
            DeleteObject(color); DeleteObject(hMask);
            return icon;
        }
        finally { NativeMemory.Free(px); }
    }

    static void DrawRow(uint* px, int size, double? temp, int row0, uint rgb)
    {
        string[][] g;
        int w;
        if (temp == null) { g = new[] { Dash, Dash }; w = 11; }
        else
        {
            int v = (int)Math.Round(Math.Clamp(temp.Value, 0, 99));
            var digits = v >= 10 ? new[] { v / 10, v % 10 } : new[] { v };
            g = digits.Select(d => Glyphs[d]).ToArray();
            w = g.Length * 5 + (g.Length - 1);
        }
        int x0 = (16 - w) / 2 + (w % 2 == 1 ? 0 : 0);
        // supersampled mapping of the 16x16 logical grid onto size x size
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int lit = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++)
                    {
                        double u = (x + (sx + 0.5) / 4) * 16.0 / size;
                        double v = (y + (sy + 0.5) / 4) * 16.0 / size;
                        int gy = (int)Math.Floor(v) - row0;
                        if (gy < 0 || gy >= 7) continue;
                        int gx = (int)Math.Floor(u) - x0;
                        if (gx < 0 || gx >= w) continue;
                        int gi = gx / 6; int cx = gx % 6;
                        if (gi >= g.Length || cx >= 5) continue;
                        if (g[gi][gy][cx] == '#') lit++;
                    }
                if (lit == 0) continue;
                uint a = (uint)(lit * 255 / 16);
                uint r = ((rgb >> 16) & 0xFF) * a / 255, gg = ((rgb >> 8) & 0xFF) * a / 255, b = (rgb & 0xFF) * a / 255; // premultiplied
                uint cur = px[y * size + x];
                uint ca = cur >> 24;
                if (a > ca) px[y * size + x] = (a << 24) | (r << 16) | (gg << 8) | b;
            }
    }
}

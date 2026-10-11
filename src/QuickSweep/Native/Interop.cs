using System.Runtime.InteropServices;
using System.Text;

namespace QuickSweep.Native;

[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X, Y; }

/// <summary>Plain Win32 / NT calls used across the app. Everything resolves from System32 only.</summary>

internal static unsafe class Win
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_SET_INFORMATION = 0x0200;
    public const uint PROCESS_TERMINATE = 0x0001;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint IDLE_PRIORITY_CLASS = 0x40;

    [DllImport("kernel32.dll")] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
    [DllImport("kernel32.dll")] public static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll")] public static extern uint GetPriorityClass(nint h);
    [DllImport("kernel32.dll")] public static extern bool SetPriorityClass(nint h, uint cls);
    [DllImport("kernel32.dll")] public static extern bool TerminateProcess(nint h, uint code);
    [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(nint h, out long create, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern bool QueryFullProcessImageNameW(nint h, int flags, char* buf, ref int size);
    [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(nint h, nint min, nint max);
    [DllImport("kernel32.dll")] public static extern nint GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [StructLayout(LayoutKind.Sequential)]
    public struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache, KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }
    [DllImport("psapi.dll")] public static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION p, uint size);

    [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int cls, void* buf, int len, out int ret);
    [DllImport("ntdll.dll")] public static extern int NtQueryInformationProcess(nint h, int cls, void* buf, int len, out int ret);

    // windows
    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, nint lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out RECT r);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(nint hwnd);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);   // -1 = ASFW_ANY
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();

    /// <summary>
    /// Take the foreground even when Windows' foreground lock would refuse (an elevated process answering a click that
    /// went to the lower-integrity shell): share input state with the current foreground thread for the call, and as a
    /// last resort tap Alt, which also lifts the lock.
    /// </summary>
    public static bool ForceForeground(nint hwnd)
    {
        nint fg = GetForegroundWindow();
        uint fgThread = fg == 0 ? 0 : GetWindowThreadProcessId(fg, out _);
        uint me = GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != me && AttachThreadInput(me, fgThread, true);
        try { BringWindowToTop(hwnd); SetForegroundWindow(hwnd); }
        finally { if (attached) AttachThreadInput(me, fgThread, false); }
        if (GetForegroundWindow() == hwnd) return true;
        keybd_event(0x12, 0, 0, 0); keybd_event(0x12, 0, 2, 0);   // Alt down, Alt up
        SetForegroundWindow(hwnd);
        return GetForegroundWindow() == hwnd;
    }
    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(POINT pt, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] public static extern bool GetMonitorInfoW(nint mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] public static extern bool DestroyIcon(nint hIcon);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern uint RegisterWindowMessageW([MarshalAs(UnmanagedType.LPWStr)] string name);

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int l, r, t, b; }
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS m);

    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public nint hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern nint SHGetFileInfoW(string path, uint attrs, ref SHFILEINFO fi, uint size, uint flags);

    // uxtheme ordinal 135: SetPreferredAppMode (makes native popup menus follow dark mode)
    [DllImport("uxtheme.dll", EntryPoint = "#135")] public static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] public static extern void FlushMenuThemes();

    // native popup menu
    public const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 8, MF_GRAYED = 1;
    [DllImport("user32.dll")] public static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] public static extern bool PostMessageW(nint hwnd, uint msg, nint w, nint l);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetLongPathNameW(string path, char* buf, uint len);

    /// <summary>Expands 8.3 short names (PROGRA~1) so denylist comparisons cannot be dodged.</summary>
    public static string LongPath(string path)
    {
        try
        {
            if (!path.Contains('~')) return path;
            // expand the longest prefix that exists, keep the rest as written
            string head = path, tail = "";
            char* buf = stackalloc char[1024];
            while (head.Length > 3)
            {
                uint n = GetLongPathNameW(head, buf, 1024);
                if (n > 0 && n < 1024) return new string(buf, 0, (int)n) + tail;
                int cut = head.LastIndexOf('\\');
                if (cut < 2) break;
                tail = head.Substring(cut) + tail;
                head = head.Substring(0, cut);
            }
            return path;
        }
        catch { return path; }
    }

    public static long FileTimeNow() => DateTime.UtcNow.ToFileTimeUtc();

    public static string? ImagePath(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try
        {
            char* buf = stackalloc char[1024];
            int len = 1024;
            return QueryFullProcessImageNameW(h, 0, buf, ref len) ? new string(buf, 0, len) : null;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Command line via NtQueryInformationProcess(ProcessCommandLineInformation).</summary>
    public static string? CommandLine(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == 0) return null;
        try
        {
            int size = 4096;
            byte* buf = (byte*)NativeMemory.Alloc((nuint)size);
            try
            {
                int st = NtQueryInformationProcess(h, 60, buf, size, out int need);
                if (st == unchecked((int)0xC0000004) || st == unchecked((int)0xC0000023))
                {
                    NativeMemory.Free(buf);
                    size = need + 16;
                    buf = (byte*)NativeMemory.Alloc((nuint)size);
                    st = NtQueryInformationProcess(h, 60, buf, size, out need);
                }
                if (st < 0) return null;
                ushort len = *(ushort*)buf;
                char* str = *(char**)(buf + 8);
                return len == 0 ? "" : new string(str, 0, len / 2);
            }
            finally { NativeMemory.Free(buf); }
        }
        finally { CloseHandle(h); }
    }

    // ---- priorities other than the CPU class (all reversible) ------------------------------------------------
    [DllImport("kernel32.dll")] static extern bool SetProcessInformation(nint h, int cls, void* info, uint size);
    [DllImport("kernel32.dll")] static extern bool GetProcessInformation(nint h, int cls, void* info, uint size);
    [DllImport("gdi32.dll")] static extern int D3DKMTSetProcessSchedulingPriorityClass(nint h, int cls);
    [DllImport("gdi32.dll")] static extern int D3DKMTGetProcessSchedulingPriorityClass(nint h, out int cls);

    /// <summary>Memory priority 1 (very low) to 5 (normal): low-priority pages are trimmed first when RAM runs short.</summary>
    public static uint? GetMemoryPriority(nint h)
    {
        uint v = 0;
        return GetProcessInformation(h, 0, &v, 4) ? v : null;
    }
    public static bool SetMemoryPriority(nint h, uint v) => SetProcessInformation(h, 0, &v, 4);

    /// <summary>GPU scheduling class (0 idle, 1 below normal, 2 normal ...) so a background render yields the GPU to the game.</summary>
    public static int? GetGpuClass(nint h)
    {
        try { return D3DKMTGetProcessSchedulingPriorityClass(h, out int c) == 0 ? c : null; }
        catch { return null; }
    }
    public static bool SetGpuClass(nint h, int cls)
    {
        try { return D3DKMTSetProcessSchedulingPriorityClass(h, cls) == 0; }
        catch { return false; }
    }

    /// <summary>Trim our own working set (called after UI closes) so idle RAM stays low.</summary>
    public static void TrimSelf()
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        SetProcessWorkingSetSize(GetCurrentProcess(), -1, -1);
    }
}

public readonly record struct ProcInfo(
    int Pid, int ParentPid, string Name, long PrivateWs, long WorkingSet, long Commit,
    long CpuTime, long CreateTime, int Threads, int Session);

public readonly record struct MemoryLists(long Zero, long Free, long Modified, long Standby);

/// <summary>Reads the full process table with one NtQuerySystemInformation call.</summary>
internal static unsafe class ProcessTable
{
    static byte* _buf;
    static int _size;
    static readonly object _lock = new();

    /// <summary>When set, the next Read() also tallies the scheduler state of this process's threads.</summary>
    public static volatile int WatchPid;
    public static List<ProcInfo> Read() => Read(out _);

    /// <param name="states">threads of the watched process: waiting for a core (ready), on a core (running), blocked</param>
    public static List<ProcInfo> Read(out (int ready, int running, int waiting) states)
    {
        lock (_lock)
        {
            states = default;
            int watch = WatchPid, tReady = 0, tRun = 0, tWait = 0;
            if (_buf == null) { _size = 1 << 20; _buf = (byte*)NativeMemory.Alloc((nuint)_size); }
            int need;
            int st;
            while ((st = Win.NtQuerySystemInformation(5, _buf, _size, out need)) == unchecked((int)0xC0000004))
            {
                _size = Math.Max(need + (64 << 10), _size * 2);
                _buf = (byte*)NativeMemory.Realloc(_buf, (nuint)_size);
            }
            var list = new List<ProcInfo>(512);
            if (st < 0) return list;
            byte* p = _buf;
            while (true)
            {
                uint next = *(uint*)p;
                int threads = *(int*)(p + 4);
                long wsPrivate = *(long*)(p + 8);
                long create = *(long*)(p + 32);
                long user = *(long*)(p + 40);
                long kernel = *(long*)(p + 48);
                ushort nameLen = *(ushort*)(p + 56);
                char* nameBuf = *(char**)(p + 64);
                int pid = (int)*(nint*)(p + 80);
                int ppid = (int)*(nint*)(p + 88);
                int session = *(int*)(p + 100);
                long ws = (long)*(nuint*)(p + 144);
                long commit = (long)*(nuint*)(p + 184);
                string name = nameLen == 0 ? (pid == 0 ? "System Idle Process" : "") : new string(nameBuf, 0, nameLen / 2);
                list.Add(new ProcInfo(pid, ppid, name, wsPrivate, ws, commit, user + kernel, create, threads, session));
                if (pid == watch && watch != 0)
                {
                    // SYSTEM_THREAD_INFORMATION entries follow the 256-byte process record, 80 bytes each (x64)
                    for (int i = 0; i < threads; i++)
                    {
                        uint state = *(uint*)(p + 256 + i * 80 + 68);
                        if (state is 1 or 7) tReady++;            // Ready, DeferredReady: wants a core, has none
                        else if (state is 2 or 3) tRun++;          // Running, Standby
                        else tWait++;
                    }
                }
                if (next == 0) break;
                p += next;
            }
            states = (tReady, tRun, tWait);
            return list;
        }
    }

    public static MemoryLists? ReadMemoryLists(long pageSize)
    {
        long* b = stackalloc long[22];
        int st = Win.NtQuerySystemInformation(80, b, 22 * 8, out _);
        if (st < 0) return null;
        long standby = 0;
        for (int i = 0; i < 8; i++) standby += b[5 + i];
        return new MemoryLists(b[0] * pageSize, b[1] * pageSize, b[2] * pageSize, standby * pageSize);
    }
}

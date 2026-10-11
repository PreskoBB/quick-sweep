using System.Runtime.InteropServices;

namespace QuickSweep.Native;

/// <summary>An impersonation token of the user who runs the desktop shell (explorer.exe), for asking Windows about that user's folders.</summary>
internal static class ShellToken
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_DUPLICATE = 2, TOKEN_QUERY = 8, TOKEN_IMPERSONATE = 4;

    [DllImport("user32.dll")] static extern nint GetShellWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(nint proc, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(nint existing, uint access, nint attrs, int level, int type, out nint token);

    /// <summary>The token, or 0 when there is no desktop shell or the token cannot be duplicated. Close it with <see cref="Close"/>.</summary>
    public static nint OpenImpersonation()
    {
        var hwnd = GetShellWindow();
        if (hwnd == 0) return 0;
        GetWindowThreadProcessId(hwnd, out int pid);
        nint hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp == 0) return 0;
        nint source = 0, dup = 0;
        try
        {
            if (!OpenProcessToken(hp, TOKEN_DUPLICATE | TOKEN_QUERY, out source)) return 0;
            return DuplicateTokenEx(source, TOKEN_QUERY | TOKEN_IMPERSONATE | TOKEN_DUPLICATE, 0, 2 /* SecurityImpersonation */, 2 /* TokenImpersonation */, out dup) ? dup : 0;
        }
        finally { if (source != 0) CloseHandle(source); CloseHandle(hp); }
    }

    public static void Close(nint token) { if (token != 0) CloseHandle(token); }
}

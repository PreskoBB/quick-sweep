using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace QuickSweep.Native;

public enum ReadResult { Ok, Missing, Rejected }

/// <summary>
/// File operations for an elevated process that must not be steered by a user-writable location. Every operation opens
/// the object with FILE_FLAG_OPEN_REPARSE_POINT (a junction or symlink is opened as the link itself, never followed)
/// and then verifies through the handle: the object is not a reparse point, and GetFinalPathNameByHandle equals the path
/// we meant (so no folder on the way is a link either). Anything else is refused with a reason.
/// </summary>
public static unsafe class SafeIo
{
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, DELETE = 0x00010000, FILE_READ_ATTRIBUTES = 0x80;
    const uint SHARE_ALL = 7, OPEN_EXISTING = 3, OPEN_ALWAYS = 4;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000, FILE_ATTRIBUTE_NORMAL = 0x80;
    const uint ATTR_DIRECTORY = 0x10, ATTR_REPARSE = 0x400;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct BY_HANDLE_FILE_INFORMATION
    {
        public uint dwFileAttributes;
        public long ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint dwVolumeSerialNumber, nFileSizeHigh, nFileSizeLow, nNumberOfLinks, nFileIndexHigh, nFileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateFileW(string name, uint access, uint share, nint sec, uint disp, uint flags, nint tmpl);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(nint h, out BY_HANDLE_FILE_INFORMATION info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(nint h, char* buf, uint len, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetFileInformationByHandle(nint h, int cls, void* info, uint size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);

    static bool Bad(nint h) => h == 0 || h == -1;

    /// <summary>Full path, 8.3 names expanded, no trailing separator (except for a drive root).</summary>
    public static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        var full = Win.LongPath(Path.GetFullPath(path));
        return full.Length > 3 ? full.TrimEnd('\\') : full;
    }

    /// <summary>The real path of an open handle in the same form as <see cref="Normalize"/>, or null.</summary>
    static string? FinalPath(nint h)
    {
        char* buf = stackalloc char[1024];
        uint n = GetFinalPathNameByHandleW(h, buf, 1024, 0);   // FILE_NAME_NORMALIZED | VOLUME_NAME_DOS
        if (n == 0 || n >= 1024) return null;
        var s = new string(buf, 0, (int)n);
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return null;
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) s = s[4..];
        return s.Length > 3 ? s.TrimEnd('\\') : s;
    }

    /// <summary>Handle check: the object is what the path says (final path equal) and, unless allowed, not a reparse point.</summary>
    static bool CheckHandle(nint h, string expected, bool allowReparse, bool wantDirectory, out string why)
    {
        why = "";
        if (!GetFileInformationByHandle(h, out var info)) { why = "cannot query the object"; return false; }
        bool isDir = (info.dwFileAttributes & ATTR_DIRECTORY) != 0;
        if (wantDirectory != isDir) { why = wantDirectory ? "is not a folder" : "is a folder"; return false; }
        if (!allowReparse && (info.dwFileAttributes & ATTR_REPARSE) != 0) { why = "is a junction or symbolic link"; return false; }
        var final = FinalPath(h);
        if (final == null) { why = "its real path cannot be determined"; return false; }
        if (!string.Equals(final, expected, StringComparison.OrdinalIgnoreCase)) { why = $"resolves to a different place ({final})"; return false; }
        return true;
    }

    /// <summary>The folder exists, is a plain folder and so is every folder on the way to it.</summary>
    public static bool IsPlainDirectory(string dir, out string why)
    {
        why = "";
        string norm;
        try { norm = Normalize(dir); } catch (Exception e) { why = e.Message; return false; }
        nint h = CreateFileW(norm, FILE_READ_ATTRIBUTES, SHARE_ALL, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, 0);
        if (Bad(h)) { why = "cannot be opened (error " + Marshal.GetLastWin32Error() + ")"; return false; }
        try { return CheckHandle(h, norm, allowReparse: false, wantDirectory: true, out why); }
        finally { CloseHandle(h); }
    }

    /// <summary>
    /// Opens a folder for listing and relative opens, shared for read and write but NOT for delete or rename, so nobody can swap
    /// it while the handle is held, and verifies through the handle that it is a plain folder at exactly this path.
    /// The caller closes the handle.
    /// </summary>
    internal static bool OpenVerifiedDirectory(string dir, out nint handle, out string why)
    {
        handle = 0; why = "";
        string norm;
        try { norm = Normalize(dir); } catch (Exception e) { why = e.Message; return false; }
        const uint FILE_LIST_DIRECTORY = 1, SYNCHRONIZE = 0x100000, SHARE_READ_WRITE = 3;
        nint h = CreateFileW(norm, FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE, SHARE_READ_WRITE, 0, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, 0);
        if (Bad(h)) { why = "cannot be opened (error " + Marshal.GetLastWin32Error() + ")"; return false; }
        if (!CheckHandle(h, norm, allowReparse: false, wantDirectory: true, out why)) { CloseHandle(h); return false; }
        handle = h;
        return true;
    }

    /// <summary>Reads a small file. Missing = nothing there; Rejected = a link, a redirected path or too large.</summary>
    public static ReadResult TryReadSmall(string path, int maxBytes, out string text, out string why)
    {
        text = ""; why = "";
        string norm;
        try { norm = Normalize(path); } catch (Exception e) { why = e.Message; return ReadResult.Rejected; }
        nint h = CreateFileW(norm, GENERIC_READ, SHARE_ALL, 0, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, 0);
        if (Bad(h))
        {
            int err = Marshal.GetLastWin32Error();
            if (err is 2 or 3) return ReadResult.Missing;      // file / path not found
            why = "cannot be opened (error " + err + ")"; return ReadResult.Rejected;
        }
        if (!CheckHandle(h, norm, allowReparse: false, wantDirectory: false, out why)) { CloseHandle(h); return ReadResult.Rejected; }
        using var fs = new FileStream(new SafeFileHandle(h, true), FileAccess.Read, 1, false);
        if (fs.Length > maxBytes) { why = $"larger than {maxBytes} bytes"; return ReadResult.Rejected; }
        var buf = new byte[fs.Length];
        int got = 0;
        while (got < buf.Length) { int r = fs.Read(buf, got, buf.Length - got); if (r <= 0) break; got += r; }
        text = Decode(buf, got);
        return ReadResult.Ok;
    }

    /// <summary>Text with the byte-order mark removed, like File.ReadAllText (PowerShell and Notepad write one).</summary>
    public static string Decode(byte[] buf, int count)
    {
        if (count >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) return Encoding.UTF8.GetString(buf, 3, count - 3);
        if (count >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) return Encoding.Unicode.GetString(buf, 2, count - 2);
        if (count >= 2 && buf[0] == 0xFE && buf[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(buf, 2, count - 2);
        return Encoding.UTF8.GetString(buf, 0, count);
    }

    /// <summary>Overwrites or appends. Creates the file when it is missing, but only inside a verified plain folder.</summary>
    public static bool TryWriteText(string path, string text, bool append, out string why)
    {
        why = "";
        string norm;
        try { norm = Normalize(path); } catch (Exception e) { why = e.Message; return false; }
        var dir = Path.GetDirectoryName(norm);
        if (dir == null) { why = "no folder"; return false; }
        if (!IsPlainDirectory(dir, out var dwhy)) { why = "folder " + dwhy; return false; }
        nint h = CreateFileW(norm, GENERIC_WRITE, 1 /* share read */, 0, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, 0);
        if (Bad(h)) { why = "cannot be opened (error " + Marshal.GetLastWin32Error() + ")"; return false; }
        if (!CheckHandle(h, norm, allowReparse: false, wantDirectory: false, out why)) { CloseHandle(h); return false; }
        try
        {
            using var fs = new FileStream(new SafeFileHandle(h, true), FileAccess.Write, 1, false);
            if (append) fs.Seek(0, SeekOrigin.End); else fs.SetLength(0);
            var bytes = new UTF8Encoding(false).GetBytes(text);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush();
            return true;
        }
        catch (Exception e) { why = e.Message; return false; }
    }

    /// <summary>
    /// Deletes a file without following links: a junction or symlink at the path is removed as the link itself, and a
    /// path that resolves elsewhere (a link folder on the way) is refused.
    /// </summary>
    public static bool TryDelete(string path, out string why)
    {
        why = "";
        string norm;
        try { norm = Normalize(path); } catch (Exception e) { why = e.Message; return false; }
        nint h = CreateFileW(norm, DELETE | FILE_READ_ATTRIBUTES, SHARE_ALL, 0, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, 0);
        if (Bad(h))
        {
            int err = Marshal.GetLastWin32Error();
            if (err is 2 or 3) return true;                     // already gone
            why = "cannot be opened (error " + err + ")"; return false;
        }
        try
        {
            if (!CheckHandle(h, norm, allowReparse: true, wantDirectory: false, out why)) return false;
            byte del = 1;                                       // FILE_DISPOSITION_INFO { BOOLEAN DeleteFile }
            if (!SetFileInformationByHandle(h, 4, &del, 1)) { why = "delete failed (error " + Marshal.GetLastWin32Error() + ")"; return false; }
            return true;
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Renames a file over another inside a verified plain folder (log rotation, atomic save).</summary>
    public static bool TryReplace(string from, string to, out string why)
    {
        why = "";
        try
        {
            string a = Normalize(from), b = Normalize(to);
            var dir = Path.GetDirectoryName(a);
            if (dir == null || !string.Equals(dir, Path.GetDirectoryName(b), StringComparison.OrdinalIgnoreCase)) { why = "different folders"; return false; }
            if (!IsPlainDirectory(dir, out var dwhy)) { why = "folder " + dwhy; return false; }
            // the source must be a plain file
            nint h = CreateFileW(a, FILE_READ_ATTRIBUTES, SHARE_ALL, 0, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT, 0);
            if (Bad(h)) { why = "source cannot be opened"; return false; }
            try { if (!CheckHandle(h, a, false, false, out why)) return false; } finally { CloseHandle(h); }
            File.Move(a, b, true);    // replaces a link at the destination, never follows it
            return true;
        }
        catch (Exception e) { why = e.Message; return false; }
    }

    public static long LengthOrZero(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }
}

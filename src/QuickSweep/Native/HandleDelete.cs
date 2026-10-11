using System.Runtime.InteropServices;

namespace QuickSweep.Native;

public sealed class DeleteOptions
{
    public CancellationToken Ct;
    /// <summary>Test hook: called after a folder has been listed and before any of its entries is opened, with the folder's path and entry names.</summary>
    public Action<string, IReadOnlyList<string>>? AfterEnumerate;
}

/// <param name="Refused">Set when nothing was touched because the parent folder could not be verified.</param>
public readonly record struct DeleteOutcome(int Failed, string? FirstError, int LinksRemoved, string? Refused)
{
    public bool Ok => Refused == null && Failed == 0;
}

/// <summary>
/// Recursive delete that a program running as a normal user cannot redirect, written for the elevated process.
/// Nothing is deleted by path. The parent of the item is opened once, with FILE_FLAG_OPEN_REPARSE_POINT, and verified through
/// the handle (a plain folder whose final path is exactly the expected one, so no link sits anywhere on the way); that handle
/// is shared for read and write but not for delete or rename, so the folder cannot be swapped while it is held. Every entry is
/// then opened relative to its parent's handle (NtCreateFile with RootDirectory), again as the object itself and not what it
/// might point to. A reparse point (junction, symlink, cloud placeholder) is removed as a link and never entered, a folder is
/// listed through its own handle and emptied the same way, and each object is deleted through the very handle that was opened
/// and inspected (FileDispositionInformationEx), so replacing a folder with a junction between listing and deleting changes
/// nothing: the entry is opened as the junction and only the junction is removed.
/// </summary>
public static unsafe class HandleDelete
{
    const uint DELETE = 0x10000, SYNCHRONIZE = 0x100000, FILE_LIST_DIRECTORY = 1, FILE_READ_ATTRIBUTES = 0x80, FILE_WRITE_ATTRIBUTES = 0x100;
    const uint SHARE_READ_WRITE = 3, FILE_OPEN = 1;
    const uint FILE_OPEN_REPARSE_POINT = 0x00200000, FILE_OPEN_FOR_BACKUP_INTENT = 0x00004000, FILE_SYNCHRONOUS_IO_NONALERT = 0x20;
    const uint OBJ_CASE_INSENSITIVE = 0x40;
    const int FileBasicInformation = 4, FileDispositionInformation = 13, FileNamesInformation = 12, FileAttributeTagInformation = 35, FileDispositionInformationEx = 64;
    const int MaxDepth = 1500;

    const int STATUS_NO_MORE_FILES = unchecked((int)0x80000006), STATUS_NO_SUCH_FILE = unchecked((int)0xC000000F);
    const int STATUS_OBJECT_NAME_NOT_FOUND = unchecked((int)0xC0000034), STATUS_OBJECT_PATH_NOT_FOUND = unchecked((int)0xC000003A);
    const int STATUS_ACCESS_DENIED = unchecked((int)0xC0000022), STATUS_SHARING_VIOLATION = unchecked((int)0xC0000043);
    const int STATUS_DIRECTORY_NOT_EMPTY = unchecked((int)0xC0000101), STATUS_CANNOT_DELETE = unchecked((int)0xC0000121);

    [StructLayout(LayoutKind.Sequential)] struct UNICODE_STRING { public ushort Length, MaximumLength; public nint Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    struct OBJECT_ATTRIBUTES { public uint Length; public nint RootDirectory; public UNICODE_STRING* ObjectName; public uint Attributes; public nint SecurityDescriptor, SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)] struct IO_STATUS_BLOCK { public nint Status, Information; }
    [StructLayout(LayoutKind.Sequential)] struct FILE_ATTRIBUTE_TAG_INFORMATION { public uint FileAttributes, ReparseTag; }
    [StructLayout(LayoutKind.Sequential)] struct FILE_BASIC_INFORMATION { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint FileAttributes; }

    [DllImport("ntdll.dll")]
    static extern int NtCreateFile(out nint handle, uint access, OBJECT_ATTRIBUTES* oa, IO_STATUS_BLOCK* iosb, long* allocSize, uint attrs, uint share, uint disposition, uint options, void* ea, uint eaLen);
    [DllImport("ntdll.dll")]
    static extern int NtQueryDirectoryFile(nint h, nint evt, nint apc, nint apcCtx, IO_STATUS_BLOCK* iosb, void* info, uint len, int cls, byte single, UNICODE_STRING* name, byte restart);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationFile(nint h, IO_STATUS_BLOCK* iosb, void* info, uint len, int cls);
    [DllImport("ntdll.dll")] static extern int NtSetInformationFile(nint h, IO_STATUS_BLOCK* iosb, void* info, uint len, int cls);
    [DllImport("ntdll.dll")] static extern int NtClose(nint h);

    sealed class State
    {
        public readonly DeleteOptions Opt;
        public int Failed, Links; public string? First;
        public State(DeleteOptions o) { Opt = o; }
        public void Fail(string why) { Failed++; First ??= why; }
    }

    /// <summary>Deletes a file, a folder tree or a link (the link only). The item's parent folder must verify as a plain folder.</summary>
    public static DeleteOutcome DeleteItem(string path, DeleteOptions? options = null)
    {
        options ??= new DeleteOptions();
        string norm;
        try { norm = SafeIo.Normalize(path); } catch (Exception e) { return new(0, null, 0, e.Message); }
        var name = Path.GetFileName(norm);
        var parent = Path.GetDirectoryName(norm);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(parent)) return new(0, null, 0, "a drive root is never deleted");
        if (!SafeIo.OpenVerifiedDirectory(parent, out var ph, out var why)) return new(0, null, 0, "the parent folder " + why);
        try
        {
            var st = new State(options);
            DeleteChild(ph, name, parent.TrimEnd('\\'), st, 0);
            return new(st.Failed, st.First, st.Links, null);
        }
        finally { NtClose(ph); }
    }

    static void DeleteChild(nint parent, string name, string parentPath, State s, int depth)
    {
        s.Opt.Ct.ThrowIfCancellationRequested();
        string display = parentPath + "\\" + name;
        if (depth > MaxDepth) { s.Fail("nested too deeply: " + display); return; }
        int st = OpenChild(parent, name, out var h);
        if (st == STATUS_OBJECT_NAME_NOT_FOUND || st == STATUS_OBJECT_PATH_NOT_FOUND) return;   // already gone
        if (st != 0) { s.Fail($"{Describe(st)}: {display}"); return; }
        try
        {
            FILE_ATTRIBUTE_TAG_INFORMATION tag;
            IO_STATUS_BLOCK iosb;
            if (NtQueryInformationFile(h, &iosb, &tag, (uint)sizeof(FILE_ATTRIBUTE_TAG_INFORMATION), FileAttributeTagInformation) != 0)
            { s.Fail("cannot inspect " + display); return; }
            bool reparse = (tag.FileAttributes & 0x400) != 0, isDir = (tag.FileAttributes & 0x10) != 0;
            if (reparse)
            {
                // the handle is the link itself (FILE_OPEN_REPARSE_POINT): remove it, never look inside
                int r = SetDelete(h);
                if (r == 0) s.Links++; else s.Fail($"{Describe(r)}: {display} (link)");
                return;
            }
            if (isDir)
            {
                var names = ListNames(h);
                if (names == null) s.Fail("cannot list " + display);
                else
                {
                    s.Opt.AfterEnumerate?.Invoke(display, names);
                    foreach (var n in names) DeleteChild(h, n, display, s, depth + 1);
                }
            }
            int rc = SetDelete(h);
            if (rc != 0) s.Fail($"{Describe(rc)}: {display}");
        }
        finally { NtClose(h); }
    }

    static int OpenChild(nint parent, string name, out nint h)
    {
        // as much access as the object allows: listing and attribute writes are only needed for folders and read-only files
        uint[] tries =
        {
            DELETE | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | SYNCHRONIZE,
            DELETE | FILE_LIST_DIRECTORY | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
            DELETE | FILE_READ_ATTRIBUTES | SYNCHRONIZE,
        };
        int st = 0; h = 0;
        fixed (char* p = name)
        {
            foreach (var access in tries)
            {
                var us = new UNICODE_STRING { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2), Buffer = (nint)p };
                var oa = new OBJECT_ATTRIBUTES { Length = (uint)sizeof(OBJECT_ATTRIBUTES), RootDirectory = parent, ObjectName = &us, Attributes = OBJ_CASE_INSENSITIVE };
                IO_STATUS_BLOCK iosb;
                st = NtCreateFile(out h, access, &oa, &iosb, null, 0, SHARE_READ_WRITE, FILE_OPEN,
                    FILE_OPEN_REPARSE_POINT | FILE_OPEN_FOR_BACKUP_INTENT | FILE_SYNCHRONOUS_IO_NONALERT, null, 0);
                if (st == 0 || st != STATUS_ACCESS_DENIED) return st;
            }
        }
        return st;
    }

    static int SetDelete(nint h)
    {
        IO_STATUS_BLOCK iosb;
        uint flags = 1 /* DELETE */ | 2 /* POSIX_SEMANTICS */ | 0x10 /* IGNORE_READONLY_ATTRIBUTE */;
        int st = NtSetInformationFile(h, &iosb, &flags, 4, FileDispositionInformationEx);
        if (st == 0 || st == STATUS_DIRECTORY_NOT_EMPTY || st == STATUS_SHARING_VIOLATION) return st;
        // older Windows 10 or a file system without POSIX semantics: clear read-only, then the classic request
        var basic = new FILE_BASIC_INFORMATION { FileAttributes = 0x80 /* NORMAL: clears READONLY, times stay as they are */ };
        NtSetInformationFile(h, &iosb, &basic, (uint)sizeof(FILE_BASIC_INFORMATION), FileBasicInformation);
        byte del = 1;
        return NtSetInformationFile(h, &iosb, &del, 1, FileDispositionInformation);
    }

    static List<string>? ListNames(nint dir)
    {
        const int BufLen = 64 * 1024;
        var list = new List<string>();
        byte* buf = (byte*)NativeMemory.Alloc(BufLen);
        try
        {
            IO_STATUS_BLOCK iosb;
            byte restart = 1;
            while (true)
            {
                int st = NtQueryDirectoryFile(dir, 0, 0, 0, &iosb, buf, BufLen, FileNamesInformation, 0, null, restart);
                restart = 0;
                if (st == STATUS_NO_MORE_FILES || st == STATUS_NO_SUCH_FILE) break;
                if (st != 0) return null;
                byte* p = buf;
                while (true)
                {
                    uint next = *(uint*)p, len = *(uint*)(p + 8);
                    var n = new string((char*)(p + 12), 0, (int)(len / 2));
                    if (n != "." && n != "..") list.Add(n);
                    if (next == 0) break;
                    p += next;
                }
            }
            return list;
        }
        finally { NativeMemory.Free(buf); }
    }

    static string Describe(int st) => st switch
    {
        STATUS_ACCESS_DENIED => "access denied",
        STATUS_SHARING_VIOLATION => "in use by another program",
        STATUS_CANNOT_DELETE => "cannot be deleted (protected or read-only)",
        STATUS_DIRECTORY_NOT_EMPTY => "folder not empty (items inside were kept)",
        _ => $"error 0x{st:X8}",
    };
}

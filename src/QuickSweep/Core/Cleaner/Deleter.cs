using System.Runtime.InteropServices;
using QuickSweep.Native;

namespace QuickSweep.Core.Cleaner;

public sealed record DeleteResult(string Path, DeleteMode Mode, long Bytes, bool Success, string Message);

/// <summary>
/// Deletes what the user ticked and confirmed. Every item is re-checked against the denylist right before it is touched,
/// junctions and symlinks are never followed (only the link is removed), every action is logged, and a dry run does
/// everything except the final delete.
/// <para>Who deletes what. An unelevated process deletes everything itself. The elevated instance never deletes in a place a
/// normal user can write: those items are handed to <see cref="IUserDeleteRunner"/>, which runs them with the user's own
/// token. It deletes itself only below <see cref="AdminRoots"/> (system Temp, NVIDIA folders in ProgramData, Windows error
/// reports), always permanently, always through <see cref="HandleDelete"/>.</para>
/// </summary>
public sealed class Deleter
{
    readonly PathGuard _guard;
    readonly bool _dryRun;
    readonly IUserDeleteRunner? _userRunner;
    static readonly object LogLock = new();

    /// <param name="userRunner">set in the elevated instance: runs the deletions of user-writable places with the user's own rights</param>
    public Deleter(PathGuard guard, bool dryRun, IUserDeleteRunner? userRunner = null) { _guard = guard; _dryRun = dryRun; _userRunner = userRunner; }

    /// <summary>False in the helper: the elevated side writes the log.</summary>
    public bool LogActions { get; init; } = true;
    /// <summary>Test hook for <see cref="HandleDelete"/> (runs between listing a folder and opening its entries).</summary>
    internal Action<string, IReadOnlyList<string>>? AfterEnumerate { get; init; }
    static long _logBytes = -1;

    /// <summary>Appends to deletions.log in the state folder (no-follow, verified writes; see <see cref="StateIo"/>).</summary>
    void LogLine(string mode, string result, long bytes, string path, string note)
    {
        if (!LogActions) return;
        try
        {
            lock (LogLock)
            {
                if (_logBytes < 0) _logBytes = SafeIo.LengthOrZero(Paths.File("deletions.log"));
                if (_logBytes > 2_000_000) { StateIo.Rotate("deletions.log"); _logBytes = 0; }
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {mode,-9} | {result,-7} | {bytes,14} | {path} | {note}{Environment.NewLine}";
                if (StateIo.AppendText("deletions.log", line)) _logBytes += line.Length;
            }
        }
        catch { }
    }

    public List<DeleteResult> Run(IReadOnlyList<CleanItem> items, bool forceRecycle, IProgress<(int done, int total, string path)>? progress, CancellationToken ct)
    {
        if (_userRunner == null || _dryRun) return RunHere(items, forceRecycle, progress, ct);

        // elevated: split by who may delete. Results keep the order of the items.
        var results = new DeleteResult?[items.Count];
        var userIdx = new List<int>();
        var adminIdx = new List<int>();
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (AdminRoots.IsAdminClass(item.Path)) { adminIdx.Add(i); continue; }
            // the same safety check as always, before anything is handed over (the helper checks again)
            var g = item.IsDirectory ? _guard.CheckTree(item.Path, ct) : _guard.Check(item.Path);
            var mode = forceRecycle ? DeleteMode.Recycle : item.Mode;
            if (!g.Allowed)
            {
                LogLine(mode.ToString().ToUpperInvariant(), "REFUSED", item.Bytes, item.Path, g.Reason);
                results[i] = new DeleteResult(item.Path, mode, 0, false, "refused: " + g.Reason);
            }
            else userIdx.Add(i);
        }

        int done = 0, total = items.Count;
        done += items.Count - userIdx.Count - adminIdx.Count;
        progress?.Report((done, total, ""));
        if (userIdx.Count > 0)
        {
            var sub = userIdx.Select(i => items[i]).ToList();
            var baseDone = done;
            var subProgress = progress == null ? null : new SyncProgress(v => progress.Report((baseDone + v.done, total, v.path)));
            var helperResults = _userRunner.Run(sub, forceRecycle, _guard.UserPatterns, subProgress, ct);
            for (int k = 0; k < userIdx.Count; k++)
            {
                var r = helperResults[k]; var item = items[userIdx[k]];
                string tag = r.Mode.ToString().ToUpperInvariant();
                LogLine(tag, r.Success ? "OK" : "FAILED", r.Bytes, item.Path, "by the helper with the user's own rights: " + r.Message);
                results[userIdx[k]] = r;
            }
            done += userIdx.Count;
        }
        foreach (var i in adminIdx)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((done++, total, items[i].Path));
            // always permanent: the Recycle Bin is a path-based shell operation, which the elevated side does not use
            results[i] = One(items[i], false, ct);
        }
        progress?.Report((total, total, ""));
        return results.Select(r => r!).ToList();
    }

    sealed class SyncProgress : IProgress<(int done, int total, string path)>
    {
        readonly Action<(int done, int total, string path)> _a;
        public SyncProgress(Action<(int done, int total, string path)> a) { _a = a; }
        public void Report((int done, int total, string path) v) => _a(v);
    }

    List<DeleteResult> RunHere(IReadOnlyList<CleanItem> items, bool forceRecycle, IProgress<(int done, int total, string path)>? progress, CancellationToken ct)
    {
        var results = new List<DeleteResult>();
        int i = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((i++, items.Count, item.Path));
            results.Add(One(item, forceRecycle, ct));
        }
        progress?.Report((items.Count, items.Count, ""));
        return results;
    }

    DeleteResult One(CleanItem item, bool forceRecycle, CancellationToken ct)
    {
        var mode = forceRecycle ? DeleteMode.Recycle : item.Mode;
        string tag = _dryRun ? "DRYRUN" : mode.ToString().ToUpperInvariant();
        var g = item.IsDirectory ? _guard.CheckTree(item.Path, ct) : _guard.Check(item.Path);
        if (!g.Allowed)
        {
            LogLine(tag, "REFUSED", item.Bytes, item.Path, g.Reason);
            return new DeleteResult(item.Path, mode, 0, false, "refused: " + g.Reason);
        }
        bool isDir = Directory.Exists(item.Path), isFile = File.Exists(item.Path);
        if (!isDir && !isFile)
        {
            LogLine(tag, "GONE", 0, item.Path, "already removed");
            return new DeleteResult(item.Path, mode, 0, true, "already gone");
        }
        bool link;
        try { link = File.GetAttributes(item.Path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception e) { return new DeleteResult(item.Path, mode, 0, false, e.Message); }

        // fresh size for the log (the scan may be old)
        long bytes = link ? 0 : Measurer.Measure(item.Path, ct).Bytes;

        if (_dryRun)
        {
            string note = link ? "link: only the link would be removed, the target is kept" : $"would {(mode == DeleteMode.Recycle ? "move to Recycle Bin" : "delete permanently")}";
            LogLine(tag, "DRYRUN", bytes, item.Path, note);
            return new DeleteResult(item.Path, mode, bytes, true, "dry run: " + note);
        }

        try
        {
            if (mode == DeleteMode.Recycle && !link)
            {
                int rc = RecycleBin.Send(item.Path, out bool aborted);
                if (rc == 0 && !aborted && !(Directory.Exists(item.Path) || File.Exists(item.Path)))
                {
                    LogLine(tag, "OK", bytes, item.Path, "moved to Recycle Bin");
                    return new DeleteResult(item.Path, mode, bytes, true, "moved to Recycle Bin");
                }
                string why = aborted ? "cancelled" : $"Windows could not recycle it (code 0x{rc:X}; very long paths are a common cause)";
                LogLine(tag, "FAILED", bytes, item.Path, why);
                return new DeleteResult(item.Path, mode, 0, false, why);
            }

            // permanent delete, and the removal of a link, by handles (see HandleDelete): nothing is followed, nothing is swapped under us
            var outcome = HandleDelete.DeleteItem(item.Path, new DeleteOptions { Ct = ct, AfterEnumerate = AfterEnumerate });
            if (outcome.Refused != null)
            {
                LogLine(tag, "REFUSED", bytes, item.Path, outcome.Refused);
                return new DeleteResult(item.Path, mode, 0, false, "refused: " + outcome.Refused);
            }
            bool gone = !(Directory.Exists(item.Path) || File.Exists(item.Path));
            if (link)
            {
                if (gone) { LogLine("LINK", "OK", 0, item.Path, "link removed, target untouched"); return new DeleteResult(item.Path, mode, 0, true, "link removed (target kept)"); }
                LogLine("LINK", "FAILED", 0, item.Path, outcome.FirstError ?? "could not remove the link");
                return new DeleteResult(item.Path, mode, 0, false, outcome.FirstError ?? "could not remove the link");
            }
            if (gone) { LogLine(tag, "OK", bytes, item.Path, "deleted"); return new DeleteResult(item.Path, mode, bytes, true, "deleted"); }
            long left = Directory.Exists(item.Path) ? Measurer.Measure(item.Path, ct).Bytes : 0;
            long freed = Math.Max(0, bytes - left);
            string msg = $"partly deleted: {outcome.Failed} item(s) in use or protected by Windows were kept" + (outcome.FirstError != null ? $" ({outcome.FirstError})" : "");
            LogLine(tag, "PARTIAL", freed, item.Path, msg);
            return new DeleteResult(item.Path, mode, freed, false, msg);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            LogLine(tag, "FAILED", bytes, item.Path, e.Message);
            return new DeleteResult(item.Path, mode, 0, false, e.Message);
        }
    }
}

/// <summary>Moves a file or folder to the Recycle Bin through the shell (undo possible). Only ever called by a process running with the user's own rights.</summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public nint hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public nint hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);

    const uint FO_DELETE = 3;
    // SILENT | NOCONFIRMATION | ALLOWUNDO | NOERRORUI | WANTNUKEWARNING (warn instead of silently destroying when the bin cannot take it)
    const ushort Flags = 0x4 | 0x10 | 0x40 | 0x400 | 0x4000;

    public static int Send(string path, out bool aborted)
    {
        var op = new SHFILEOPSTRUCT { wFunc = FO_DELETE, pFrom = path + "\0", fFlags = Flags };
        int rc = SHFileOperationW(ref op);
        aborted = op.fAnyOperationsAborted;
        return rc;
    }
}

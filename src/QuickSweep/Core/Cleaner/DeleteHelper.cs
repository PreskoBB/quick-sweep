using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using QuickSweep.Native;

namespace QuickSweep.Core.Cleaner;

/// <summary>Deletes the items of a job and reports one result per item, in order. Runs the cleaner's deletion for folders a normal user can write.</summary>
public interface IUserDeleteRunner
{
    List<DeleteResult> Run(IReadOnlyList<CleanItem> items, bool forceRecycle, IReadOnlyList<string> protectedFolders,
        IProgress<(int done, int total, string path)>? progress, CancellationToken ct);
}

public sealed class JobItem
{
    public string Path { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsLink { get; set; }
    public DeleteMode Mode { get; set; }
    public long Bytes { get; set; }
    public string Category { get; set; } = "";
}

public sealed class HelperJob
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public bool ForceRecycle { get; set; }
    public List<string> ProtectedFolders { get; set; } = new();
    public List<JobItem> Items { get; set; } = new();
}

public sealed class HelperProgress
{
    public int Done { get; set; }
    public int Total { get; set; }
    public string Path { get; set; } = "";
}

public interface IUserProcess : IDisposable
{
    bool WaitForExit(int milliseconds);
    int ExitCode { get; }
    void Kill();
}

public interface IUserProcessLauncher
{
    IUserProcess? Start(string exe, string arguments, out string why);
}

/// <summary>
/// The part of the cleaner that runs as the interactive user, not elevated. The elevated instance never deletes in places a
/// normal user can write (user Temp, browser and package caches, your own extra folders): a program running as that user
/// could swap a folder for a junction between the elevated check and the elevated delete and have the elevated process delete
/// somewhere else. So the elevated side writes a job (admin-only folder), starts <c>QuickSweep.exe --delete-job</c> with the
/// user's own token, and reads back one result per item. A helper can only do what the user can already do.
/// </summary>
public sealed class HelperRunner : IUserDeleteRunner
{
    public const int MaxResultBytes = 16 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    readonly IUserProcessLauncher _launcher;
    readonly string _jobDir, _outDir, _exe;
    readonly TimeSpan _idleLimit;

    /// <param name="jobDir">where the job is written: inside the elevated state folder, which users can read but not write</param>
    /// <param name="outDir">where the helper writes progress and results: a folder of the user's own, read back with link checks</param>
    public HelperRunner(IUserProcessLauncher launcher, string jobDir, string outDir, string? exe = null, TimeSpan? idleLimit = null)
    {
        _launcher = launcher; _jobDir = jobDir; _outDir = outDir;
        _exe = exe ?? Environment.ProcessPath ?? throw new InvalidOperationException("no executable path");
        _idleLimit = idleLimit ?? TimeSpan.FromMinutes(15);
    }

    /// <summary>For the installed, elevated instance: the job goes to the state folder, the helper runs with the shell user's token.</summary>
    public static HelperRunner ForInstalledApp()
        => new(new ShellUserLauncher(), Path.Combine(Paths.Data, "jobs"), Path.Combine(TrustedPaths.Local, "QuickSweep", "jobs"));

    static string Clean(string? s, int max = 300)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var ch in s) { if (sb.Length >= max) break; sb.Append(char.IsControl(ch) ? ' ' : ch); }
        return sb.ToString();
    }

    public List<DeleteResult> Run(IReadOnlyList<CleanItem> items, bool forceRecycle, IReadOnlyList<string> protectedFolders,
        IProgress<(int done, int total, string path)>? progress, CancellationToken ct)
    {
        DeleteMode ModeOf(CleanItem i) => forceRecycle ? DeleteMode.Recycle : i.Mode;
        List<DeleteResult> All(string why) => items.Select(i => new DeleteResult(i.Path, ModeOf(i), 0, false, "nothing was deleted: " + why)).ToList();

        string id = Guid.NewGuid().ToString("N");
        string jobFile = Path.Combine(_jobDir, id + ".job.json");
        string progressFile = Path.Combine(_outDir, id + ".progress.json");
        string resultFile = Path.Combine(_outDir, id + ".result.json");
        try
        {
            var job = new HelperJob
            {
                Id = id, ForceRecycle = forceRecycle, ProtectedFolders = protectedFolders.ToList(),
                Items = items.Select(i => new JobItem { Path = i.Path, IsDirectory = i.IsDirectory, IsLink = i.IsLink, Mode = i.Mode, Bytes = i.Bytes, Category = i.Category }).ToList()
            };
            Directory.CreateDirectory(_jobDir);
            if (!SafeIo.TryWriteText(jobFile, JsonSerializer.Serialize(job), append: false, out var why)) return All("the job file could not be written (" + why + ")");

            using var proc = _launcher.Start(_exe, $"--delete-job=\"{jobFile}\" --job-out=\"{_outDir}\"", out why);
            if (proc == null) return All("the helper that runs with your normal rights could not be started (" + why + ")");

            int lastDone = -1; var lastChange = DateTime.UtcNow;
            while (!proc.WaitForExit(150))
            {
                if (ct.IsCancellationRequested) { proc.Kill(); break; }
                if (SafeIo.TryReadSmall(progressFile, 8192, out var pt, out _) == ReadResult.Ok)
                {
                    try
                    {
                        var p = JsonSerializer.Deserialize<HelperProgress>(pt, Json);
                        if (p != null)
                        {
                            if (p.Done != lastDone) { lastDone = p.Done; lastChange = DateTime.UtcNow; }
                            progress?.Report((Math.Clamp(p.Done, 0, items.Count), items.Count, Clean(p.Path)));
                        }
                    }
                    catch { }
                }
                if (DateTime.UtcNow - lastChange > _idleLimit) { proc.Kill(); break; }
            }

            var results = new List<DeleteResult>();
            List<DeleteResult>? reported = null;
            if (SafeIo.TryReadSmall(resultFile, MaxResultBytes, out var rt, out _) == ReadResult.Ok)
                try { reported = JsonSerializer.Deserialize<List<DeleteResult>>(rt, Json); } catch { }
            for (int i = 0; i < items.Count; i++)
            {
                if (reported != null && i < reported.Count)
                    results.Add(new DeleteResult(items[i].Path, ModeOf(items[i]), Math.Max(0, reported[i].Bytes), reported[i].Success, Clean(reported[i].Message)));
                else
                    results.Add(new DeleteResult(items[i].Path, ModeOf(items[i]), 0, false, ct.IsCancellationRequested ? "cancelled" : "the helper stopped before it reached this item"));
            }
            return results;
        }
        catch (Exception e) { return All(e.Message); }
        finally
        {
            foreach (var f in new[] { jobFile, progressFile, resultFile }) { try { SafeIo.TryDelete(f, out _); } catch { } }
        }
    }
}

/// <summary>What <c>QuickSweep.exe --delete-job</c> does: read the job, delete each item with the same guarded code as an unelevated run, write the results.</summary>
public static class DeleteHelper
{
    public const int Done = 0, BadArguments = 2, BadJob = 3, WrongToken = 6;
    public const int MaxJobBytes = 32 * 1024 * 1024, MaxItems = 200_000;
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    sealed class FileProgress : IProgress<(int done, int total, string path)>
    {
        readonly string _file; DateTime _last = DateTime.MinValue;
        public FileProgress(string file) { _file = file; }
        public void Report((int done, int total, string path) v)
        {
            if (v.done != v.total && DateTime.UtcNow - _last < TimeSpan.FromMilliseconds(120)) return;
            _last = DateTime.UtcNow;
            SafeIo.TryWriteText(_file, JsonSerializer.Serialize(new HelperProgress { Done = v.done, Total = v.total, Path = v.path }), append: false, out _);
        }
    }

    public static int Run(string? jobFile, string? outDir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jobFile) || string.IsNullOrWhiteSpace(outDir)) return BadArguments;
        if (SafeIo.TryReadSmall(jobFile, MaxJobBytes, out var text, out _) != ReadResult.Ok) return BadJob;
        HelperJob? job;
        try { job = JsonSerializer.Deserialize<HelperJob>(text, Json); } catch { return BadJob; }
        if (job == null || job.Version != 1 || job.Items.Count > MaxItems || job.Id.Length is < 8 or > 64 || job.Id.Any(c => !char.IsAsciiLetterOrDigit(c))) return BadJob;
        try { Directory.CreateDirectory(outDir); } catch { return BadArguments; }

        var items = job.Items.Select(j => new CleanItem
        {
            Path = j.Path, Display = j.Path, IsDirectory = j.IsDirectory, IsLink = j.IsLink, Mode = j.Mode, Bytes = j.Bytes, Category = j.Category
        }).ToList();
        var deleter = new Deleter(new PathGuard(job.ProtectedFolders), dryRun: false) { LogActions = false };
        var results = deleter.Run(items, job.ForceRecycle, new FileProgress(Path.Combine(outDir, job.Id + ".progress.json")), ct);
        return SafeIo.TryWriteText(Path.Combine(outDir, job.Id + ".result.json"), JsonSerializer.Serialize(results), append: false, out _) ? Done : BadArguments;
    }
}

/// <summary>Starts the helper as a plain child process (no elevation involved): for tests, and for runs that are not elevated anyway.</summary>
public sealed class DirectLauncher : IUserProcessLauncher
{
    sealed class P : IUserProcess
    {
        readonly Process _p;
        public P(Process p) { _p = p; }
        public bool WaitForExit(int ms) => _p.WaitForExit(ms);
        public int ExitCode => _p.ExitCode;
        public void Kill() { try { _p.Kill(true); } catch { } }
        public void Dispose() => _p.Dispose();
    }

    public IUserProcess? Start(string exe, string arguments, out string why)
    {
        why = "";
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            return p == null ? null : new P(p);
        }
        catch (Exception e) { why = e.Message; return null; }
    }
}

/// <summary>
/// Starts a process with the interactive user's own, non-elevated token. The elevated process takes the token of the
/// desktop shell (explorer.exe, which runs as the user at medium integrity), duplicates it and calls CreateProcessWithTokenW
/// (an administrator holds SeImpersonatePrivilege, which that call needs). Refuses unless the token is the same user's and is not
/// elevated, so it can never hand a deletion job to something that has more rights than the user.
/// </summary>
public sealed class ShellUserLauncher : IUserProcessLauncher
{
    const uint TOKEN_ASSIGN_PRIMARY = 1, TOKEN_DUPLICATE = 2, TOKEN_QUERY = 8, TOKEN_ADJUST_DEFAULT = 0x80, TOKEN_ADJUST_SESSIONID = 0x100;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, SYNCHRONIZE = 0x100000, PROCESS_TERMINATE = 1;
    const uint CREATE_NO_WINDOW = 0x08000000, CREATE_UNICODE_ENVIRONMENT = 0x400, LOGON_WITH_PROFILE = 1;
    const int TokenUser = 1, TokenElevation = 20;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFOW
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public nint lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public nint hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll")] static extern nint GetShellWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(nint h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(nint h, out int code);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(nint h, uint code);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(nint proc, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool DuplicateTokenEx(nint existing, uint access, nint attrs, int level, int type, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(nint token, int cls, nint buf, int len, out int ret);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessWithTokenW(nint token, uint logonFlags, string? app, StringBuilder cmd, uint flags, nint env, string? cwd, ref STARTUPINFOW si, out PROCESS_INFORMATION pi);

    sealed class Proc : IUserProcess
    {
        nint _h;
        public Proc(nint h) { _h = h; }
        public bool WaitForExit(int ms) => WaitForSingleObject(_h, (uint)ms) == 0;
        public int ExitCode { get { GetExitCodeProcess(_h, out var c); return c; } }
        public void Kill() { if (_h != 0) TerminateProcess(_h, 1); }
        public void Dispose() { if (_h != 0) { CloseHandle(_h); _h = 0; } }
    }

    /// <summary>True when the token belongs to this user and is not elevated.</summary>
    static bool IsSameUserAndLimited(nint token, out string why)
    {
        why = "";
        int len;
        GetTokenInformation(token, TokenUser, 0, 0, out len);
        nint buf = Marshal.AllocHGlobal(Math.Max(len, 64));
        try
        {
            if (!GetTokenInformation(token, TokenUser, buf, len, out _)) { why = "cannot read the shell token"; return false; }
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buf));
            if (sid != WindowsIdentity.GetCurrent().User) { why = "the desktop shell runs as a different user"; return false; }
            if (!GetTokenInformation(token, TokenElevation, buf, 4, out _)) { why = "cannot read the shell token's elevation"; return false; }
            if (Marshal.ReadInt32(buf) != 0) { why = "the desktop shell itself is elevated (is User Account Control off?), so there is no normal-rights context to use"; return false; }
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public IUserProcess? Start(string exe, string arguments, out string why)
    {
        why = "";
        nint shellToken = 0, primary = 0;
        try
        {
            var hwnd = GetShellWindow();
            if (hwnd == 0) { why = "no desktop shell is running"; return null; }
            GetWindowThreadProcessId(hwnd, out int pid);
            nint hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hp == 0) { why = $"cannot open the shell process (error {Marshal.GetLastWin32Error()})"; return null; }
            bool ok = OpenProcessToken(hp, TOKEN_DUPLICATE | TOKEN_QUERY, out shellToken);
            CloseHandle(hp);
            if (!ok) { why = $"cannot open the shell token (error {Marshal.GetLastWin32Error()})"; return null; }
            const uint access = TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID;
            if (!DuplicateTokenEx(shellToken, access, 0, 2 /* SecurityImpersonation */, 1 /* TokenPrimary */, out primary))
            { why = $"cannot duplicate the shell token (error {Marshal.GetLastWin32Error()})"; return null; }
            if (!IsSameUserAndLimited(primary, out why)) return null;

            var si = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>(), lpDesktop = @"winsta0\default", dwFlags = 1 /* USESHOWWINDOW */, wShowWindow = 0 /* SW_HIDE */ };
            var cmd = new StringBuilder($"\"{exe}\" {arguments}");
            if (!CreateProcessWithTokenW(primary, LOGON_WITH_PROFILE, null, cmd, CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT, 0,
                    Path.GetDirectoryName(exe), ref si, out var pi))
            { why = $"CreateProcessWithTokenW failed (error {Marshal.GetLastWin32Error()})"; return null; }
            CloseHandle(pi.hThread);
            return new Proc(pi.hProcess);
        }
        catch (Exception e) { why = e.Message; return null; }
        finally
        {
            if (primary != 0) CloseHandle(primary);
            if (shellToken != 0) CloseHandle(shellToken);
        }
    }
}

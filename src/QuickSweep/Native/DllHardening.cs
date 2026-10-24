using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using QuickSweep.Core;

namespace QuickSweep.Native;

/// <summary>
/// The elevated process must never load a DLL from a folder a normal user can write to. LibreHardwareMonitor (and the
/// libraries under it) load vendor DLLs by bare name (AMD's atiadlxx.dll, Intel's ControlLib.dll, ...); on a PC that does
/// not have one in System32 the default search falls through to the current directory and PATH, and a user-level process
/// could plant a file there. Two layers:
/// <list type="number">
/// <item><see cref="Apply"/>: SetDefaultDllDirectories(system32 | application dir) + SetDllDirectory(""), process-wide, run from a
/// module initializer so it is the first managed code of the process (before WPF, before any P/Invoke).</item>
/// <item><see cref="ResolveImport"/>: a NativeLibrary resolver on every assembly in the app folder that resolves a bare name
/// from System32, then the app folder, and nothing else (a miss is a DllNotFoundException, never a PATH lookup). A path is only
/// accepted below a protected root.</item>
/// </list>
/// </summary>
public static class DllHardening
{
    const uint LoadLibrarySearchApplicationDir = 0x00000200, LoadLibrarySearchSystem32 = 0x00000800;

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetDefaultDllDirectories(uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetDllDirectoryW(string path);

    static readonly object Gate = new();
    static bool _applied;
    static readonly ConcurrentQueue<string> Pending = new();

    /// <summary>What <see cref="Apply"/> did ("ok" or the Win32 error), for the log and --gpu-probe.</summary>
    public static string Status { get; private set; } = "not applied";
    public static bool Applied => _applied;

    /// <summary>Runs before Main (before WPF starts): the first managed code of the process.</summary>
    [ModuleInitializer]
    internal static void Init() => Apply();

    /// <summary>Idempotent. Safe to call again from startup code.</summary>
    public static void Apply()
    {
        lock (Gate)
        {
            if (_applied) return;
            _applied = true;
            try
            {
                bool a = SetDefaultDllDirectories(LoadLibrarySearchSystem32 | LoadLibrarySearchApplicationDir);
                int e1 = a ? 0 : Marshal.GetLastWin32Error();
                bool b = SetDllDirectoryW("");
                int e2 = b ? 0 : Marshal.GetLastWin32Error();
                Status = a && b ? "ok (system32 + application folder only; current directory and PATH removed)"
                                : $"FAILED SetDefaultDllDirectories={(a ? "ok" : "error " + e1)} SetDllDirectory={(b ? "ok" : "error " + e2)}";
                if (!(a && b)) Pending.Enqueue("dll hardening: " + Status);
                RegisterResolvers();
            }
            catch (Exception e)
            {
                Status = "FAILED: " + e.Message;
                Pending.Enqueue("dll hardening: " + Status);
            }
        }
    }

    /// <summary>Writes what happened before the log was usable (a refused or failed load) into the log. Called once the state folder is known, and by --gpu-probe.</summary>
    public static IReadOnlyList<string> DrainNotes()
    {
        var l = new List<string>();
        while (Pending.TryDequeue(out var s)) l.Add(s);
        return l;
    }

    // ---------- resolver ----------

    static string AppDir => AppContext.BaseDirectory.TrimEnd('\\', '/');

    static void RegisterResolvers()
    {
        AppDomain.CurrentDomain.AssemblyLoad += (_, a) => Register(a.LoadedAssembly);
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) Register(a);
    }

    /// <summary>Every assembly that sits in the app folder (ours, LibreHardwareMonitor and its libraries). Framework assemblies keep the runtime's own probing.</summary>
    static void Register(Assembly a)
    {
        try
        {
            if (a.IsDynamic || string.IsNullOrEmpty(a.Location)) return;
            if (!IsUnder(a.Location, AppDir)) return;
            NativeLibrary.SetDllImportResolver(a, ResolveImport);
        }
        catch (InvalidOperationException) { /* a resolver is already set for it */ }
        catch (Exception e) { Pending.Enqueue("dll hardening: could not guard " + a.GetName().Name + ": " + e.Message); }
    }

    static nint ResolveImport(string name, Assembly caller, DllImportSearchPath? searchPath)
    {
        var d = Decide(name, AppDir, Environment.SystemDirectory, TrustedRoots(), File.Exists, TrustedNvml);
        if (d.Path == null)
        {
            Pending.Enqueue($"dll hardening: refused '{name}' for {caller.GetName().Name}: {d.Reason}");
            throw new DllNotFoundException($"'{name}' is not in System32 or the Quick Sweep folder ({d.Reason}); the search path and current directory are not used");
        }
        if (NativeLibrary.TryLoad(d.Path, out var h)) return h;
        Pending.Enqueue($"dll hardening: '{d.Path}' for {caller.GetName().Name} would not load");
        throw new DllNotFoundException($"'{d.Path}' could not be loaded");
    }

    internal readonly record struct Decision(string? Path, string Reason);

    static string[] TrustedRoots() => new[]
    {
        Environment.SystemDirectory, AppDir,
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
    }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s.TrimEnd('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    static bool IsUnder(string path, string root)
        => path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The whole policy, pure so it can be tested. A bare name resolves from System32, then the app folder. A full path is accepted
    /// below a protected root only. Anything else (a relative path, another folder) is refused. <c>nvml.dll</c> may also come from
    /// the driver's NVSMI folder when <paramref name="trustedFile"/> vouches for it.
    /// </summary>
    internal static Decision Decide(string name, string appDir, string system32, string[] trustedRoots, Func<string, bool> exists, Func<string, bool> trustedFile)
    {
        if (string.IsNullOrWhiteSpace(name)) return new(null, "empty name");
        bool hasDir = name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0;
        if (hasDir)
        {
            if (!Path.IsPathFullyQualified(name)) return new(null, "relative path");
            string full = Path.GetFullPath(name);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return new(null, "network or device path");
            return trustedRoots.Any(r => IsUnder(full, r)) && exists(full) ? new(full, "") : new(null, "not below a protected folder");
        }
        string file = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
        string inSys = Path.Combine(system32, file);
        if (exists(inSys)) return new(inSys, "");
        string inApp = Path.Combine(appDir, file);
        if (exists(inApp)) return new(inApp, "");
        if (string.Equals(file, "nvml.dll", StringComparison.OrdinalIgnoreCase))
        {
            string nvsmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll");
            if (exists(nvsmi) && trustedFile(nvsmi)) return new(nvsmi, "");
        }
        return new(null, "not found there");
    }

    static bool TrustedNvml(string path) => ProtectedFile.IsProtected(path, out _);

    // ---------- what is loaded (for --gpu-probe and the tests) ----------

    /// <summary>Loaded modules outside the Windows folder, the app folder and the .NET runtime folder: a planted DLL would show up here.</summary>
    public static IReadOnlyList<string> ForeignModules()
    {
        var okRoots = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.Windows), AppDir, RuntimeEnvDir() };
        var list = new List<string>();
        try
        {
            foreach (ProcessModule m in Process.GetCurrentProcess().Modules)
            {
                string f = m.FileName;
                if (okRoots.Any(r => r.Length > 0 && IsUnder(f, r.TrimEnd('\\', '/')))) continue;
                list.Add(f);
            }
        }
        catch (Exception e) { list.Add("(module list failed: " + e.Message + ")"); }
        return list;
    }

    static string RuntimeEnvDir()
    {
        try { return Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd('\\')))) ?? ""; }   // ...\dotnet\shared\Microsoft.NETCore.App\x.y.z -> ...\dotnet
        catch { return ""; }
    }
}

/// <summary>A file the elevated process may load code from by full path: below Program Files or Windows, no link on the way, owned by Administrators/SYSTEM/TrustedInstaller, nobody else may write it or its folders.</summary>
public static class ProtectedFile
{
    public static bool IsProtected(string path, out string why)
    {
        why = "";
        try
        {
            string full = Path.GetFullPath(path);
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            }.Where(s => !string.IsNullOrEmpty(s)).Select(s => s.TrimEnd('\\')).ToArray();
            string? root = roots.FirstOrDefault(r => full.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase));
            if (root == null) { why = "not below Program Files or Windows"; return false; }
            if (!File.Exists(full)) { why = "missing"; return false; }

            var fi = new FileInfo(full);
            if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) { why = "is a link"; return false; }
            var fsec = fi.GetAccessControl();
            if (!StateFolder.IsTrusted(fsec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)) { why = "owner is not Administrators/SYSTEM/TrustedInstaller"; return false; }
            var bad = StateFolder.PermissiveEntries(fsec);
            if (bad.Count > 0) { why = string.Join("; ", bad); return false; }

            // every folder from the file up to (and including) the root must be a plain folder, protected the same way
            for (var dir = fi.Directory; dir != null; dir = dir.Parent)
            {
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) { why = dir.FullName + " is a link"; return false; }
                var dsec = dir.GetAccessControl();
                if (!StateFolder.IsTrusted(dsec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)) { why = dir.FullName + " owner not trusted"; return false; }
                var dbad = StateFolder.PermissiveEntries(dsec);
                if (dbad.Count > 0) { why = dir.FullName + ": " + string.Join("; ", dbad); return false; }
                if (string.Equals(dir.FullName.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase)) break;
            }
            return true;
        }
        catch (Exception e) { why = "check failed: " + e.Message; return false; }
    }
}

using System.Text.RegularExpressions;
using Microsoft.Win32;
using QuickSweep.Native;

namespace QuickSweep.Core;

public sealed class StartupEntry
{
    public string Name = "";
    public string Command = "";
    public string? ExePath;
    public string Source = "";          // human label
    public RegistryHive Hive;            // hive of the StartupApproved key
    public string ApprovedSubKey = "";   // Run, Run32 or StartupFolder
    public string ValueName = "";        // value in the StartupApproved key
    public bool Enabled;
    public bool CanChange = true;        // false when it needs admin and we are not elevated
    public long RamBytes;
    public int ProcessCount;
    public string Publisher = "";
}

/// <summary>
/// Lists autostart entries (Run keys and Startup folders) and flips them through the StartupApproved keys,
/// exactly like Task Manager's Startup tab. Nothing is ever deleted; toggling twice returns to the start.
/// </summary>
public static class StartupManager
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static List<StartupEntry> Load(List<ProcInfo> procs)
    {
        if (Demo.On) return Demo.Startup();
        var list = new List<StartupEntry>();
        AddRun(list, Registry.CurrentUser, RunKey, RegistryView.Default, "Run", "Your account (Run)", RegistryHive.CurrentUser, true);
        AddRun(list, Registry.LocalMachine, RunKey, RegistryView.Registry64, "Run", "All users (Run)", RegistryHive.LocalMachine, Elevation.IsElevated);
        AddRun(list, Registry.LocalMachine, RunKey, RegistryView.Registry32, "Run32", "All users, 32-bit (Run)", RegistryHive.LocalMachine, Elevation.IsElevated);
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Your Startup folder", RegistryHive.CurrentUser, true);
        AddFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "All users Startup folder", RegistryHive.LocalMachine, Elevation.IsElevated);
        foreach (var e in list)
        {
            e.Enabled = ReadApproved(e);
            e.Publisher = Publisher(e.ExePath);
        }
        MeasureRam(list, procs);
        return list.OrderByDescending(e => e.RamBytes).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void AddRun(List<StartupEntry> list, RegistryKey root, string sub, RegistryView view, string approved, string label, RegistryHive hive, bool canChange)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var k = baseKey.OpenSubKey(sub);
            if (k == null) return;
            foreach (var name in k.GetValueNames())
            {
                var cmd = k.GetValue(name)?.ToString() ?? "";
                list.Add(new StartupEntry
                {
                    Name = name, Command = cmd, ExePath = ExeFromCommand(cmd), Source = label,
                    Hive = hive, ApprovedSubKey = approved, ValueName = name, CanChange = canChange
                });
            }
        }
        catch (Exception e) { Log.Write("startup scan: " + e.Message); }
    }

    static void AddFolder(List<StartupEntry> list, string dir, string label, RegistryHive hive, bool canChange)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var file = Path.GetFileName(f);
                if (file.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                string? exe = f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(f) : f;
                list.Add(new StartupEntry
                {
                    Name = Path.GetFileNameWithoutExtension(file), Command = f, ExePath = exe, Source = label,
                    Hive = hive, ApprovedSubKey = "StartupFolder", ValueName = file, CanChange = canChange
                });
            }
        }
        catch (Exception e) { Log.Write("startup folder scan: " + e.Message); }
    }

    static string? ShortcutTarget(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;
            dynamic sh = Activator.CreateInstance(t)!;
            string target = sh.CreateShortcut(lnk).TargetPath;
            return string.IsNullOrEmpty(target) ? null : target;
        }
        catch { return null; }
    }

    static readonly Regex Quoted = new("^\\s*\"([^\"]+)\"");
    public static string? ExeFromCommand(string cmd)
    {
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        var m = Quoted.Match(cmd);
        string path = m.Success ? m.Groups[1].Value : cmd;
        if (!m.Success)
        {
            // unquoted: take up to the first .exe/.bat/.cmd/.lnk
            var m2 = Regex.Match(path, @"^(.*?\.(exe|bat|cmd|lnk|com))(\s|$)", RegexOptions.IgnoreCase);
            if (m2.Success) path = m2.Groups[1].Value;
            else path = path.Split(' ')[0];
        }
        return path.Length > 2 ? path : null;
    }

    static string Publisher(string? exe)
    {
        try
        {
            if (exe != null && File.Exists(exe))
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
                var c = vi.CompanyName?.Trim() ?? "";
                // some installers store UTF-8 bytes in the ANSI resource field: repair the obvious cases
                if (c.Contains('Ã')) { try { c = System.Text.Encoding.UTF8.GetString(System.Text.Encoding.Latin1.GetBytes(c)); } catch { } }
                return c;
            }
        }
        catch { }
        return "";
    }

    // ---- StartupApproved -------------------------------------------------
    static string ApprovedPath(StartupEntry e) => $@"{ApprovedRoot}\{e.ApprovedSubKey}";

    public static bool ReadApproved(StartupEntry e)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(e.Hive, RegistryView.Registry64);
            using var k = baseKey.OpenSubKey(ApprovedPath(e));
            if (k?.GetValue(e.ValueName) is byte[] b && b.Length > 0) return (b[0] & 1) == 0;
        }
        catch { }
        return true; // no record = enabled
    }

    /// <summary>Same bytes Task Manager writes: 02 = enabled, 03 + FILETIME = disabled.</summary>
    public static bool SetEnabled(StartupEntry e, bool enabled)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(e.Hive, RegistryView.Registry64);
            using var k = baseKey.CreateSubKey(ApprovedPath(e), true);
            var data = new byte[12];
            data[0] = enabled ? (byte)2 : (byte)3;
            if (!enabled) BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(data, 4);
            k.SetValue(e.ValueName, data, RegistryValueKind.Binary);
            e.Enabled = enabled;
            Log.Write($"startup '{e.Name}' ({e.Source}) -> {(enabled ? "enabled" : "disabled")}");
            return true;
        }
        catch (Exception ex) { Log.Write("startup toggle failed: " + ex.Message); return false; }
    }

    // ---- measured RAM ------------------------------------------------------
    static readonly Regex VersionDir = new(@"^(app-.*|\d+(\.\d+)+.*)$", RegexOptions.IgnoreCase);

    static string? AppRoot(string? exe)
    {
        if (exe == null) return null;
        var dir = Path.GetDirectoryName(exe);
        if (dir == null) return null;
        // climb out of versioned folders (Discord\app-1.0.9\...)
        while (dir != null && VersionDir.IsMatch(Path.GetFileName(dir))) dir = Path.GetDirectoryName(dir);
        if (dir == null) return null;
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string[] shared =
        {
            win, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.GetPathRoot(dir) ?? ""
        };
        if (shared.Any(s => s.Length > 0 && dir.TrimEnd('\\').Equals(s.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) return null;
        if (dir.StartsWith(win, StringComparison.OrdinalIgnoreCase)) return null;
        return dir;
    }

    static void MeasureRam(List<StartupEntry> entries, List<ProcInfo> procs)
    {
        var withPath = procs.Select(p => (p, path: ProcessInsight.PathOf(p))).Where(x => x.path != null).ToList();
        var claimed = new HashSet<int>();
        // exact exe matches first, then app-folder matches (longest folder wins)
        foreach (var e in entries)
        {
            if (e.ExePath == null) continue;
            foreach (var (p, path) in withPath)
                if (!claimed.Contains(p.Pid) && path!.Equals(e.ExePath, StringComparison.OrdinalIgnoreCase))
                { claimed.Add(p.Pid); e.RamBytes += p.PrivateWs; e.ProcessCount++; }
        }
        foreach (var e in entries.OrderByDescending(x => AppRoot(x.ExePath)?.Length ?? 0))
        {
            var root = AppRoot(e.ExePath);
            if (root == null) continue;
            string prefix = root.TrimEnd('\\') + "\\";
            foreach (var (p, path) in withPath)
                if (!claimed.Contains(p.Pid) && path!.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                { claimed.Add(p.Pid); e.RamBytes += p.PrivateWs; e.ProcessCount++; }
        }
    }
}

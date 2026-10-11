using System.Diagnostics;
using QuickSweep.Native;

namespace QuickSweep.Core;

public sealed class AppGroup
{
    public string Key = "";
    public string Name = "";
    public string? ExePath;
    public long PrivateBytes, Commit;
    public int Count;
    public bool Protected;
    public List<ProcInfo> Procs = new();
}

/// <summary>Maps processes to per-app groups (all Firefox processes = one row) using the exe's product name.</summary>
public static class ProcessInsight
{
    static readonly Dictionary<(int, long), string?> PathCache = new();
    static readonly Dictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);
    static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static readonly int OwnPid = Environment.ProcessId;
    static readonly object L = new();

    public static string? PathOf(ProcInfo p)
    {
        lock (L)
        {
            if (PathCache.TryGetValue((p.Pid, p.CreateTime), out var cached)) return cached;
            string? path = p.Pid <= 4 ? null : Win.ImagePath(p.Pid);
            PathCache[(p.Pid, p.CreateTime)] = path;
            return path;
        }
    }

    /// <summary>Forget cache entries for processes that no longer exist.</summary>
    public static void Prune(List<ProcInfo> live)
    {
        lock (L)
        {
            if (PathCache.Count < live.Count * 2 + 200) return;
            var keep = new HashSet<(int, long)>(live.Select(p => (p.Pid, p.CreateTime)));
            foreach (var k in PathCache.Keys.Where(k => !keep.Contains(k)).ToList()) PathCache.Remove(k);
        }
    }

    public static string Describe(string path)
    {
        lock (L)
        {
            if (NameCache.TryGetValue(path, out var n)) return n;
            string name;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                var product = vi.ProductName?.Trim();
                var desc = vi.FileDescription?.Trim();
                bool genericProduct = string.IsNullOrEmpty(product) || product.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) && product.Contains("Windows");
                name = !genericProduct ? product! : !string.IsNullOrEmpty(desc) ? desc! : Path.GetFileNameWithoutExtension(path);
            }
            catch { name = Path.GetFileNameWithoutExtension(path); }
            NameCache[path] = name;
            return name;
        }
    }

    static bool IsProtected(ProcInfo p, string? path)
    {
        if (p.Pid == OwnPid) return true;
        if (path == null) return true;
        if (path.StartsWith(WinDir, StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Contains(@"\Windows Defender\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static List<AppGroup> Group(List<ProcInfo> procs)
    {
        var map = new Dictionary<string, AppGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in procs)
        {
            if (p.Pid == 0) continue;
            string? path = PathOf(p);
            string name, key;
            if (path == null)
            {
                name = p.Name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
                if (name is "System" or "Registry" or "Memory Compression" or "Secure System") name += " (Windows)";
                key = "sys:" + name;
            }
            else
            {
                name = Describe(path);
                key = "app:" + name;
            }
            if (!map.TryGetValue(key, out var g)) map[key] = g = new AppGroup { Key = key, Name = name, ExePath = path };
            g.PrivateBytes += p.PrivateWs;
            g.Commit += p.Commit;
            g.Count++;
            g.Procs.Add(p);
            if (g.ExePath == null && path != null) g.ExePath = path;
            if (IsProtected(p, path)) g.Protected = true;
        }
        return map.Values.OrderByDescending(g => g.PrivateBytes).ToList();
    }

    /// <summary>End every process in the group. Only kills a pid whose start time still matches the snapshot.</summary>
    public static (int killed, int failed) EndGroup(AppGroup g)
    {
        int ok = 0, bad = 0;
        if (g.Protected) return (0, g.Count);
        foreach (var p in g.Procs)
        {
            var h = Win.OpenProcess(Win.PROCESS_TERMINATE | Win.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
            if (h == 0) { bad++; continue; }
            try
            {
                if (Win.GetProcessTimes(h, out long create, out _, out _, out _) && create != p.CreateTime) continue; // pid reused
                if (Win.TerminateProcess(h, 1)) ok++; else bad++;
            }
            finally { Win.CloseHandle(h); }
        }
        Log.Write($"end task '{g.Name}': {ok} ended, {bad} failed");
        return (ok, bad);
    }
}

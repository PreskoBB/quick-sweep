using QuickSweep.Native;

namespace QuickSweep.Core;

/// <summary>Per-process CPU use from the deltas between two process-table reads (percent of ONE core).</summary>
public sealed class CpuTracker
{
    Dictionary<(int, long), long> _prev = new();
    long _prevTick;
    public IReadOnlyDictionary<(int, long), double> Pct { get; private set; } = new Dictionary<(int, long), double>();
    public static int Cores { get; } = Math.Max(1, Environment.ProcessorCount);

    public void Update(List<ProcInfo> procs)
    {
        long now = Environment.TickCount64;
        double elapsed100ns = (now - _prevTick) * 10_000.0;
        var next = new Dictionary<(int, long), long>(procs.Count);
        var pct = new Dictionary<(int, long), double>(procs.Count);
        foreach (var p in procs)
        {
            var k = (p.Pid, p.CreateTime);
            next[k] = p.CpuTime;
            if (_prevTick != 0 && elapsed100ns > 0 && _prev.TryGetValue(k, out long before))
                pct[k] = Math.Max(0, 100.0 * (p.CpuTime - before) / elapsed100ns);
        }
        _prev = next; _prevTick = now; Pct = pct;
    }

    public double Of(ProcInfo p) => Pct.TryGetValue((p.Pid, p.CreateTime), out var v) ? v : 0;
    /// <summary>Share of the whole CPU (all cores), percent.</summary>
    public double ShareOf(ProcInfo p) => Of(p) / Cores;
}

public enum JobKind { Render, Script, Agent }

public sealed class BgJob
{
    public ProcInfo P;
    public JobKind Kind;
    public string Name = "";
    public double CpuShare;   // percent of the whole CPU
    public double GpuShare;   // percent of the GPU (highest engine)
    public double MinShare;   // CPU share (percent of the whole CPU) from which this kind of job counts as busy
}

/// <summary>Finds background work that could compete with a game: encoders, 3D and build tools, headless browsers,
/// busy scripts and whatever an AI coding agent started. What counts is set in Settings (the defaults are in
/// <see cref="AppSettings"/>). <see cref="Find"/> lists the jobs by identity; <see cref="Contributors"/> keeps the
/// ones that really add to the kind of pressure at hand.</summary>
public static class BackgroundWork
{
    // headless Chromium-based browsers (started with --headless) always count as render work
    static readonly HashSet<string> Chromium = new(StringComparer.OrdinalIgnoreCase) { "chrome", "chromium", "msedge" };

    static readonly Dictionary<(int, long), bool> HeadlessCache = new();

    static string Stem(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    public static List<BgJob> Find(List<ProcInfo> procs, CpuTracker cpu, IReadOnlyDictionary<int, double>? gpuByPid, int gamePid, AppSettings s)
    {
        var jobs = new List<BgJob>();
        var byPid = new Dictionary<int, ProcInfo>(procs.Count);
        foreach (var p in procs) byPid[p.Pid] = p;
        int me = Environment.ProcessId, fg = ForegroundPid();
        var live = new HashSet<(int, long)>();
        var tools = new HashSet<string>(s.BackgroundProcesses, StringComparer.OrdinalIgnoreCase);
        var runtimes = new HashSet<string>(s.ScriptRuntimes, StringComparer.OrdinalIgnoreCase);
        var agents = s.AgentApps.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToArray();

        foreach (var p in procs)
        {
            live.Add((p.Pid, p.CreateTime));
            if (p.Pid <= 4 || p.Pid == gamePid || p.Pid == me || p.Pid == fg) continue;
            string stem = Stem(p.Name);
            double share = cpu.ShareOf(p);
            double gpu = gpuByPid != null && gpuByPid.TryGetValue(p.Pid, out var g) ? g : 0;
            JobKind? kind = null;
            double minShare = 2;

            if (tools.Contains(stem)) { kind = JobKind.Render; minShare = 1; }
            else if (Chromium.Contains(stem) && IsHeadless(p)) { kind = JobKind.Render; minShare = 1; }
            else if (runtimes.Contains(stem)) kind = JobKind.Script;
            if (kind == null && agents.Length > 0 && IsAgentChild(p, byPid, gamePid, agents)) kind = JobKind.Agent;
            if (kind == null) continue;
            if (IsDescendantOf(p, gamePid, byPid)) continue;
            jobs.Add(new BgJob { P = p, Kind = kind.Value, Name = stem, CpuShare = share, GpuShare = gpu, MinShare = minShare });
        }
        lock (HeadlessCache) foreach (var k in HeadlessCache.Keys.Where(k => !live.Contains(k)).ToList()) HeadlessCache.Remove(k);
        return jobs;
    }

    static int ForegroundPid()
    {
        var h = Win.GetForegroundWindow();
        if (h == 0) return 0;
        Win.GetWindowThreadProcessId(h, out int pid);
        return pid;
    }

    static bool IsHeadless(ProcInfo p)
    {
        lock (HeadlessCache)
        {
            if (HeadlessCache.TryGetValue((p.Pid, p.CreateTime), out var v)) return v;
            var cl = Win.CommandLine(p.Pid);
            v = cl != null && cl.Contains("--headless", StringComparison.OrdinalIgnoreCase);
            HeadlessCache[(p.Pid, p.CreateTime)] = v;
            return v;
        }
    }

    static bool IsDescendantOf(ProcInfo p, int ancestorPid, Dictionary<int, ProcInfo> byPid)
    {
        if (ancestorPid <= 0) return false;
        int cur = p.ParentPid;
        for (int i = 0; i < 12 && cur > 4 && byPid.TryGetValue(cur, out var parent); i++)
        {
            if (cur == ancestorPid) return true;
            cur = parent.ParentPid;
        }
        return false;
    }

    /// <summary>A process (other than the agent apps themselves) that sits below an AI coding agent in the process tree.</summary>
    static bool IsAgentChild(ProcInfo p, Dictionary<int, ProcInfo> byPid, int gamePid, string[] agents)
    {
        if (IsAgent(p, agents)) return false;
        int cur = p.ParentPid;
        for (int i = 0; i < 12 && cur > 4 && byPid.TryGetValue(cur, out var parent); i++)
        {
            if (cur == gamePid) return false;
            if (IsAgent(parent, agents)) return true;
            cur = parent.ParentPid;
        }
        return false;
    }

    /// <summary>True when the program's file name equals an entry, or its product name starts with one.</summary>
    internal static bool IsAgent(ProcInfo p, string[] agents)
    {
        string stem = Stem(p.Name);
        foreach (var a in agents) if (stem.Equals(a, StringComparison.OrdinalIgnoreCase)) return true;
        var path = ProcessInsight.PathOf(p);
        if (path == null) return false;
        var product = ProcessInsight.Describe(path);
        foreach (var a in agents) if (product.StartsWith(a, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Which of the jobs actually contribute to the kind of pressure, and are worth slowing down.</summary>
    public static List<BgJob> Contributors(List<BgJob> jobs, PressureKind kinds, AppSettings s)
    {
        var chosen = new HashSet<BgJob>();
        double min = s.GameMinContributionPct;
        if (kinds.HasFlag(PressureKind.Cpu) || kinds.HasFlag(PressureKind.Ready))
        {
            var list = jobs.Where(j => j.CpuShare >= j.MinShare).ToList();
            if (list.Sum(j => j.CpuShare) >= min) foreach (var j in list) chosen.Add(j);
        }
        if (kinds.HasFlag(PressureKind.Gpu))
        {
            var list = jobs.Where(j => j.GpuShare >= 2).ToList();
            if (list.Sum(j => j.GpuShare) >= min) foreach (var j in list) chosen.Add(j);
        }
        if (kinds.HasFlag(PressureKind.Ram))
        {
            var list = jobs.Where(j => j.P.PrivateWs >= 300L * 1024 * 1024).ToList();
            if (list.Sum(j => j.P.PrivateWs) >= 500L * 1024 * 1024) foreach (var j in list) chosen.Add(j);
        }
        return chosen.OrderByDescending(j => j.CpuShare + j.GpuShare).ToList();
    }
}

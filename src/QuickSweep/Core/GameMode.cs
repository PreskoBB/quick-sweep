using QuickSweep.Native;

namespace QuickSweep.Core;

public sealed class LoweredProc
{
    public int Pid { get; set; }
    public long Create { get; set; }
    public string Name { get; set; } = "";
    public uint OrigClass { get; set; }        // CPU priority class before
    public uint Target { get; set; }           // CPU class we set (0 = we left the CPU class alone)
    public uint OrigMemPriority { get; set; }  // memory priority before (0 = unchanged)
    public int OrigGpuClass { get; set; } = -1; // GPU scheduling class before (-1 = unchanged)
}

public sealed class LoweredState { public List<LoweredProc> Items { get; set; } = new(); }

/// <summary>
/// Game Mode: while a game runs, watch the pressure on the machine (CPU, GPU, free RAM, the game's own threads waiting
/// for a core). Only when that pressure is sustained AND heavy background work contributes to it, lower that work
/// (priority only, never a kill). It is restored when the load has been calm for a long while or when the game closes.
/// Every action goes into the session summary; at most one quiet notification per game.
/// </summary>
public sealed class GameMode
{
    const uint Idle = 0x40, BelowNormal = 0x4000;

    readonly AppSettings _s;
    readonly PressureWatcher _w;
    readonly Dictionary<(int, long), LoweredProc> _lowered = new();
    readonly List<string> _log = new();
    readonly HashSet<(int, long)> _everLowered = new();
    DateTime _lastTopUp = DateTime.MinValue;
    bool _notified;
    string _lastNames = "", _lastReason = "";

    public bool Watching { get; private set; }
    public bool Active => _lowered.Count > 0;
    public int LoweredCount => _lowered.Count;
    /// <summary>Distinct processes slowed during this game (restored ones included).</summary>
    public int TotalLowered => _everLowered.Count;
    public IReadOnlyList<string> Log => _log;
    public string LastNames => _lastNames;
    public string LastReason => _lastReason;

    public event Action? StateChanged;
    /// <summary>First time per game: title and text for one quiet notification.</summary>
    public event Action<string, string>? Kicked;

    public GameMode(AppSettings s, Func<DateTime>? clock = null)
    {
        _s = s;
        _w = new PressureWatcher(s, clock);
        RecoverStale();
    }

    public void GameStarted()
    {
        Watching = true; _log.Clear(); _everLowered.Clear(); _notified = false; _lastNames = _lastReason = "";
        _w.Reset();
        StateChanged?.Invoke();
    }

    public void GameEnded()
    {
        if (Active) RestoreAll("the game closed");
        Watching = false;
        StateChanged?.Invoke();
    }

    /// <summary>Called every couple of seconds while a game runs.</summary>
    public void Tick(Telemetry t, List<ProcInfo> procs, CpuTracker cpu, double? readyPct, int gamePid)
    {
        if (!Watching) return;
        if (!_s.GameModeAuto) { if (Active) RestoreAll("Game Mode was switched off"); return; }

        double loweredShare = _lowered.Keys.Sum(k => cpu.Pct.TryGetValue(k, out var v) ? v : 0) / CpuTracker.Cores;
        var sample = new PressureSample(t.CpuLoad, Math.Max(0, t.CpuLoad - loweredShare), t.Gpu?.Load ?? 0,
            t.Mem.Available / 1073741824.0, t.Mem.CommitPct, readyPct);
        _w.Add(sample);

        if (Active)
        {
            if (_w.ReleaseReady)
            {
                RestoreAll($"the load dropped (CPU {sample.CpuWithoutLowered:0}% without them)");
                _w.StartCooldown();
                return;
            }
            var now = PressureWatcher.Classify(_s, sample);
            if (now != PressureKind.None && (DateTime.Now - _lastTopUp).TotalSeconds >= 6)
            {
                _lastTopUp = DateTime.Now;
                SlowContributors(procs, cpu, now, sample, gamePid, topUp: true);
            }
            return;
        }

        if (!_w.Triggered) return;
        var (kinds, worst) = _w.Recent();
        if (!SlowContributors(procs, cpu, kinds, worst, gamePid, topUp: false))
        {
            Core.Log.Write($"game mode: {PressureWatcher.Describe(kinds, worst)}, but no background work is contributing: nothing to slow");
            _w.Quiet(TimeSpan.FromSeconds(30));
        }
    }

    bool SlowContributors(List<ProcInfo> procs, CpuTracker cpu, PressureKind kinds, PressureSample sample, int gamePid, bool topUp)
    {
        Dictionary<int, double>? gpu = kinds.HasFlag(PressureKind.Gpu) ? GpuEngines.SamplePerPid(1000) : null;
        var jobs = BackgroundWork.Find(procs, cpu, gpu, gamePid, _s);
        var chosen = BackgroundWork.Contributors(jobs, kinds, _s).Where(j => !_lowered.ContainsKey((j.P.Pid, j.P.CreateTime))).ToList();
        var done = new List<BgJob>();
        foreach (var j in chosen) if (Lower(j, kinds)) done.Add(j);
        if (done.Count == 0) return false;

        string names = NameList(done.Select(j => j.Name));
        string reason = PressureWatcher.Describe(kinds, sample);
        _lastNames = NameList(_lowered.Values.Select(l => Path.GetFileNameWithoutExtension(l.Name)));
        _lastReason = reason;
        _log.Add($"{DateTime.Now:HH:mm} slowed {names}: {reason}");
        Core.Log.Write($"game mode: slowed {names} ({reason}){(topUp ? " [more work started]" : "")}");
        StateChanged?.Invoke();
        if (!_notified && _s.NotifyGameMode)
        {
            _notified = true;
            Kicked?.Invoke("Game Mode", $"Slowed {names} ({reason}). Priority only: they speed up again when the load drops or the game closes.");
        }
        return true;
    }

    static string NameList(IEnumerable<string> names)
    {
        var distinct = names.Select(n => n.Replace(".exe", "", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return distinct.Count <= 4 ? string.Join(", ", distinct) : string.Join(", ", distinct.Take(4)) + $" +{distinct.Count - 4} more";
    }

    bool Lower(BgJob job, PressureKind kinds)
    {
        var p = job.P;
        var h = Win.OpenProcess(Win.PROCESS_SET_INFORMATION | Win.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Pid);
        if (h == 0) return false;
        try
        {
            if (Win.GetProcessTimes(h, out long create, out _, out _, out _) && create != p.CreateTime) return false;
            uint orig = Win.GetPriorityClass(h);
            if (orig == 0) return false;
            uint target = _s.GameUseBelowNormal ? BelowNormal : Idle;
            var lp = new LoweredProc { Pid = p.Pid, Create = p.CreateTime, Name = p.Name, OrigClass = orig };
            bool alreadyLow = orig == Idle || (target == BelowNormal && orig == BelowNormal);
            if (!alreadyLow && Win.SetPriorityClass(h, target)) lp.Target = target;
            if (kinds.HasFlag(PressureKind.Ram) && Win.GetMemoryPriority(h) is uint mp && mp > 1 && Win.SetMemoryPriority(h, 1)) lp.OrigMemPriority = mp;
            int wantGpu = target == Idle ? 0 : 1;
            if (kinds.HasFlag(PressureKind.Gpu) && Win.GetGpuClass(h) is int gc && gc > wantGpu && Win.SetGpuClass(h, wantGpu)) lp.OrigGpuClass = gc;
            if (lp.Target == 0 && lp.OrigMemPriority == 0 && lp.OrigGpuClass < 0) return false;
            _lowered[(p.Pid, p.CreateTime)] = lp;
            _everLowered.Add((p.Pid, p.CreateTime));
            Persist();
            Core.Log.Write($"game mode: {p.Name} (pid {p.Pid}) cpu class 0x{orig:X} -> 0x{Win.GetPriorityClass(h):X}" +
                           (lp.OrigMemPriority > 0 ? $", memory priority {lp.OrigMemPriority} -> {Win.GetMemoryPriority(h)}" : "") +
                           (lp.OrigGpuClass >= 0 ? $", GPU class {lp.OrigGpuClass} -> {Win.GetGpuClass(h)}" : ""));
            return true;
        }
        finally { Win.CloseHandle(h); }
    }

    public void RestoreAll(string reason)
    {
        int n = _lowered.Count;
        foreach (var lp in _lowered.Values.ToList()) RestoreOne(lp);
        _lowered.Clear();
        Persist();
        if (n > 0)
        {
            _log.Add($"{DateTime.Now:HH:mm} restored {n} job{(n == 1 ? "" : "s")}: {reason}");
            Core.Log.Write($"game mode: restored {n} job(s): {reason}");
        }
        StateChanged?.Invoke();
    }

    static void RestoreOne(LoweredProc lp)
    {
        var h = Win.OpenProcess(Win.PROCESS_SET_INFORMATION | Win.PROCESS_QUERY_LIMITED_INFORMATION, false, lp.Pid);
        if (h == 0) return; // already gone
        try
        {
            if (Win.GetProcessTimes(h, out long create, out _, out _, out _) && create != lp.Create) return; // pid reused
            // undo only our own change
            if (lp.Target != 0 && Win.GetPriorityClass(h) == lp.Target) Win.SetPriorityClass(h, lp.OrigClass);
            if (lp.OrigMemPriority > 0) Win.SetMemoryPriority(h, lp.OrigMemPriority);
            if (lp.OrigGpuClass >= 0) Win.SetGpuClass(h, lp.OrigGpuClass);
        }
        finally { Win.CloseHandle(h); }
    }

    void Persist() => JsonStore.Save("gamemode-state.json", new LoweredState { Items = _lowered.Values.ToList() });

    /// <summary>After a crash or kill: put back whatever a previous run still had lowered.</summary>
    void RecoverStale()
    {
        var st = JsonStore.Load<LoweredState>("gamemode-state.json");
        foreach (var lp in st.Items) RestoreOne(lp);
        if (st.Items.Count > 0) JsonStore.Save("gamemode-state.json", new LoweredState());
    }
}

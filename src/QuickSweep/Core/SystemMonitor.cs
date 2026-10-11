using QuickSweep.Native;

namespace QuickSweep.Core;

/// <summary>Owns the sampling loop and wires sensors, game detection, Game Mode, sessions and alerts together.</summary>
public sealed class SystemMonitor : IDisposable
{
    public AppSettings Settings { get; }
    public CpuSensor Cpu { get; } = new();
    public SystemStats Sys { get; } = new();
    public PingMonitor Ping { get; } = new();
    public GameWatcher Watcher { get; }
    public GameMode Mode { get; }
    public SessionRecorder Recorder { get; } = new();
    public AlertEngine Alerts { get; }

    public Telemetry Latest { get; private set; } = new();
    public List<ProcInfo> LastProcs { get; private set; } = new();
    public List<AppGroup> LastGroups { get; private set; } = new();
    public List<DriveReport> LastHealth { get; private set; } = new();
    DateTime _lastHealth = DateTime.Now.AddMinutes(-58);   // first background check about 2 minutes after start
    int _fast;
    /// <summary>True while any window wants 1 s refreshes (flyout, memory page...).</summary>
    public bool FastMode => Volatile.Read(ref _fast) > 0;
    public void PushFast() => Interlocked.Increment(ref _fast);
    public void PopFast() { if (Interlocked.Decrement(ref _fast) < 0) Interlocked.Exchange(ref _fast, 0); }

    readonly List<Telemetry> _history = new();
    readonly object _hl = new();
    readonly List<(string title, string text)> _pendingToasts = new();
    DateTime _lastScan = DateTime.MinValue, _lastSlow = DateTime.MinValue, _lastDrives = DateTime.MinValue;
    DriveStat[] _drives = Array.Empty<DriveStat>();
    CancellationTokenSource? _cts;
    double _lastGpuLoad;

    public event Action<Telemetry>? Sampled;
    public event Action<string, string, bool>? Toast;          // title, text, isWarning
    public event Action<SessionEntry>? SessionSaved;
    public event Action? GameChanged;

    public SystemMonitor(AppSettings s)
    {
        Settings = s;
        Watcher = new GameWatcher(s);
        Mode = new GameMode(s);
        Alerts = new AlertEngine(s);
        Watcher.Started += g =>
        {
            Recorder.Begin(g);
            GamingFlag.Write(g);
            Mode.GameStarted();
            GameChanged?.Invoke();
        };
        Mode.Kicked += (title, text) => Toast?.Invoke(title, text, false);
        Watcher.Ended += g =>
        {
            GamingFlag.Clear();
            Mode.GameEnded();                                  // restores whatever is still slowed and logs it
            var entry = Recorder.End(Ping, Mode.TotalLowered, Mode.Log.ToList());
            GameChanged?.Invoke();
            if (entry != null)
            {
                SessionRecorder.Append(entry);
                SessionSaved?.Invoke(entry);
                if (Settings.NotifyAfterGame) Toast?.Invoke($"{entry.Game}: session saved", SessionBrief(entry), false);
            }
            FlushPending();
        };
        Alerts.Raised += a =>
        {
            if (Recorder.Recording) { Recorder.AddAlert(a.Title); lock (_pendingToasts) _pendingToasts.Add((a.Title, a.Detail)); }
            else Toast?.Invoke(a.Title, a.Detail, true);
        };
        Ping.Enabled = s.PingEnabled;
    }

    public static string SessionBrief(SessionEntry e)
    {
        var parts = new List<string>
        {
            $"{TimeSpan.FromSeconds(e.DurationSec):h\\:mm\\:ss} played",
            $"GPU peak {e.GpuTempPeak:0} °C" + (e.CpuTempPeak is double c ? $", CPU peak {c:0} °C" : "")
        };
        if (e.CpuThrottled || e.GpuThrottled) parts.Add("thermal throttling seen");
        if (e.Internet.Spikes > 0) parts.Add($"{e.Internet.Spikes} ping spikes");
        return string.Join(" · ", parts);
    }

    void FlushPending()
    {
        List<(string, string)> list;
        lock (_pendingToasts) { list = _pendingToasts.ToList(); _pendingToasts.Clear(); }
        foreach (var (title, text) in list) Toast?.Invoke(title + " (during your game)", text, true);
    }

    public IReadOnlyList<Telemetry> History(int max = 600)
    {
        lock (_hl) return _history.Skip(Math.Max(0, _history.Count - max)).ToList();
    }

    public void Start()
    {
        GamingFlag.Clear();   // a flag left by a crashed run would claim a game is running
        Cpu.Open();
        Log.Write($"cpu sensor: {Cpu.Status}; nvml: {Nvml.Available}; elevated: {Elevation.IsElevated}");
        Ping.Start();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var t = new Thread(() => Loop(ct)) { IsBackground = true, Name = "QuickSweep.SystemMonitor" };
        t.Start();
    }

    void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Sample(); }
            catch (Exception e) { Log.Write("sample failed: " + e); }
            int ms = FastMode ? 1000 : Mode.Watching || Recorder.Recording ? 2000 : 4000;
            if (ct.WaitHandle.WaitOne(ms)) break;
        }
    }

    /// <summary>Demo mode (<c>--shots --demo</c>): sample data instead of sensors, pings and the process scan.</summary>
    public void StartDemo()
    {
        lock (_hl) { _history.Clear(); _history.AddRange(Demo.History()); }
        Latest = Demo.Reading();
        LastGroups = Demo.Groups();
        LastHealth = Demo.Health();
        Ping.Seed(Demo.Pings());
    }

    public void SampleNow() { try { Sample(); } catch (Exception e) { Log.Write("sample failed: " + e); } }

    void Sample()
    {
        var t = new Telemetry { Time = DateTime.Now };
        t.CpuLoad = Sys.ReadCpuLoad();
        t.Mem = Sys.ReadMemory();
        Cpu.Update();
        t.CpuTemp = Cpu.TempC; t.CpuPower = Cpu.PowerW; t.CpuClock = Cpu.ClockMhz; t.CpuStatus = Cpu.Status;
        t.Gpu = Nvml.Read();
        _lastGpuLoad = t.Gpu?.Load ?? 0;
        t.Uptime = SystemStats.Uptime();
        if (DateTime.Now - _lastDrives > TimeSpan.FromSeconds(30) || _drives.Length == 0)
        {
            _drives = SystemStats.ReadDrives(Settings.LowSpaceGb); _lastDrives = DateTime.Now;
        }
        t.Drives = _drives;
        if (Ping.Last is { } p) { t.RouterMs = p.RouterMs; t.NetMs = p.NetMs; t.RouterLost = p.RouterMs == null; t.NetLost = p.NetMs == null; }

        lock (_hl)
        {
            _history.Add(t);
            if (_history.Count > 900) _history.RemoveRange(0, 150);
        }
        Latest = t;

        if ((DateTime.Now - _lastScan).TotalSeconds >= (Mode.Watching || FastMode ? 3 : 6)) { Scan(); _lastScan = DateTime.Now; }
        if ((DateTime.Now - _lastSlow).TotalSeconds >= 30) { Slow(t); _lastSlow = DateTime.Now; }

        Alerts.EvaluateFast(t);
        Recorder.Add(t);
        if (Mode.Watching)
        {
            try { Mode.Tick(t, LastProcs, ProcCpu, _ready, Watcher.Current?.Pid ?? 0); }
            catch (Exception e) { Log.Write("game mode tick failed: " + e); }
        }
        Sampled?.Invoke(t);
    }

    /// <summary>Per-process CPU use, refreshed with every scan.</summary>
    public CpuTracker ProcCpu { get; } = new();
    double? _ready;
    readonly Queue<double> _readyHist = new();

    void Scan()
    {
        int gamePid = Watcher.Current?.Pid ?? 0;
        ProcessTable.WatchPid = gamePid;
        var procs = ProcessTable.Read(out var th);
        LastProcs = procs;
        ProcCpu.Update(procs);
        if (gamePid != 0 && th.ready + th.running > 0)
        {
            // CPU-ready proxy: of the game's threads that want a core, how many are waiting for one (average of the last scans)
            _readyHist.Enqueue(100.0 * th.ready / (th.ready + th.running));
            while (_readyHist.Count > 4) _readyHist.Dequeue();
            _ready = _readyHist.Average();
        }
        else { _readyHist.Clear(); _ready = null; }
        var mc = procs.FirstOrDefault(p => p.Name == "Memory Compression");
        Sys.SetCompressed(mc.PrivateWs);
        Watcher.Tick(procs, _lastGpuLoad);
        ProcessInsight.Prune(procs);
    }

    /// <summary>Store a drive-health result and raise or clear alerts (only for failing or worsening drives).</summary>
    public void SetHealth(List<DriveReport> reports)
    {
        // a background check skips spinning disks: keep their last full result
        var merged = reports.Select(r => r.Error != null && r.Error.StartsWith("skipped") && LastHealth.FirstOrDefault(o => o.Number == r.Number && o.Attrs.Count > 0) is { } old ? old : r).ToList();
        LastHealth = merged;
        foreach (var d in merged.Where(d => d.Attrs.Count > 0))
        {
            bool bad = d.State == HealthState.Critical || d.Degrading;
            string name = string.IsNullOrWhiteSpace(d.Model) ? $"Drive {d.Number}" : d.Model;
            Alerts.Raise("drive:" + d.Key, bad, $"{name} is {(d.State == HealthState.Critical ? "failing" : "getting worse")}",
                (d.Findings.Count > 0 ? string.Join(" ", d.Findings) : "SMART reports problems.") + " Back up important data and see the Drive health page.");
        }
    }

    void Slow(Telemetry t)
    {
        if (Elevation.IsElevated && (DateTime.Now - _lastHealth).TotalMinutes >= 60)
        {
            _lastHealth = DateTime.Now;
            Task.Run(() =>
            {
                try { var r = DriveHealth.ReadAll(includeHdd: false); DriveHealth.ApplyBaseline(r); SetHealth(r); }
                catch (Exception e) { Log.Write("health check: " + e.Message); }
            });
        }
        var groups = ProcessInsight.Group(LastProcs);
        LastGroups = groups;
        Alerts.EvaluateSlow(t, groups);
    }

    /// <summary>Refresh the per-app grouping right now (used when a window opens).</summary>
    public List<AppGroup> RefreshGroups()
    {
        if (Demo.On) return LastGroups;
        var procs = ProcessTable.Read();
        LastProcs = procs;
        var mc = procs.FirstOrDefault(p => p.Name == "Memory Compression");
        Sys.SetCompressed(mc.PrivateWs);
        var g = ProcessInsight.Group(procs);
        LastGroups = g;
        return g;
    }

    /// <summary>Game Mode master switch (flyout, tray menu, settings).</summary>
    public void SetGameMode(bool on)
    {
        Settings.GameModeAuto = on;
        Settings.Save();
        GameChanged?.Invoke();
    }

    public void ToggleGameMode() => SetGameMode(!Settings.GameModeAuto);

    public void Dispose()
    {
        _cts?.Cancel();
        Ping.Stop();
        Mode.RestoreAll("Quick Sweep closed");
        GamingFlag.Clear();
        Cpu.Dispose();
    }
}

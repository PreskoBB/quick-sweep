namespace QuickSweep.Core;

public sealed class Alert
{
    public string Key = "";
    public string Title = "";
    public string Detail = "";
    public DateTime Since = DateTime.Now;
}

/// <summary>
/// Raises an alert only when something is really wrong: CPU/GPU too hot for a sustained stretch, C: nearly full,
/// an app whose memory climbs steadily (leak) or Windows kernel memory ballooning. Never sounds, never nags.
/// </summary>
public sealed class AlertEngine
{
    readonly AppSettings _s;
    readonly Dictionary<string, Alert> _active = new();
    readonly Dictionary<string, DateTime> _lastNotified = new();
    readonly Dictionary<string, DateTime> _overSince = new();
    readonly Dictionary<string, List<(DateTime t, double v)>> _series = new();
    public static readonly TimeSpan Cooldown = TimeSpan.FromHours(3);

    public AlertEngine(AppSettings s) { _s = s; }

    /// <summary>Clock used for sustained-condition timing and the rolling series (replaceable in tests).</summary>
    public Func<DateTime> Clock { get; set; } = () => DateTime.Now;

    public IReadOnlyList<Alert> Active { get { lock (_active) return _active.Values.OrderBy(a => a.Since).ToList(); } }
    public event Action<Alert>? Raised;     // newly raised (cooldown already applied)
    public event Action? Changed;

    void Set(string key, bool on, string title, string detail)
    {
        bool changed = false;
        lock (_active)
        {
            if (on)
            {
                if (!_active.TryGetValue(key, out var a))
                {
                    _active[key] = a = new Alert { Key = key, Title = title, Detail = detail, Since = Clock() };
                    changed = true;
                    if (!_lastNotified.TryGetValue(key, out var last) || Clock() - last > Cooldown)
                    {
                        _lastNotified[key] = Clock();
                        Raised?.Invoke(a);
                    }
                }
                else { a.Detail = detail; }
            }
            else if (_active.Remove(key)) changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Raise or clear an alert owned by another component (drive health).</summary>
    public void Raise(string key, bool on, string title, string detail)
    {
        if (!_s.AlertsEnabled) on = false;
        Set(key, on, title, detail);
    }

    bool Sustained(string key, bool cond, double seconds)
    {
        if (!cond) { _overSince.Remove(key); return false; }
        if (!_overSince.TryGetValue(key, out var t)) { _overSince[key] = Clock(); return seconds <= 0; }
        return (Clock() - t).TotalSeconds >= seconds;
    }

    /// <summary>Fast checks, every sample.</summary>
    public void EvaluateFast(Telemetry t)
    {
        if (!_s.AlertsEnabled) { ClearAll(); return; }
        if (t.CpuTemp is double ct)
            Set("cpu-hot", Sustained("cpu-hot", ct > _s.CpuAlertC, 10) || (_active.ContainsKey("cpu-hot") && ct > _s.CpuAlertC - 3),
                "CPU is running hot", $"CPU is at {ct:0} °C (alert limit {_s.CpuAlertC} °C). Check cooler, dust and case airflow.");
        if (t.Gpu is { } g)
            Set("gpu-hot", Sustained("gpu-hot", g.TempC > _s.GpuAlertC, 10) || (_active.ContainsKey("gpu-hot") && g.TempC > _s.GpuAlertC - 3),
                "GPU is running hot", $"GPU is at {g.TempC:0} °C (alert limit {_s.GpuAlertC} °C). Check fans and airflow.");
    }

    /// <summary>Slower checks (every ~30 s): disk space, memory growth, kernel pool.</summary>
    public void EvaluateSlow(Telemetry t, List<AppGroup> groups)
    {
        if (!_s.AlertsEnabled) { ClearAll(); return; }
        var c = t.Drives.FirstOrDefault(d => d.Letter == "C");
        if (c != null)
            Set("c-low", c.Free < _s.LowSpaceGb * 1024L * 1024 * 1024,
                "C: is almost full", $"Only {c.Free / 1073741824.0:0.0} GB free on C:. Windows needs room to update and swap; open the Disk Cleaner.");

        // kernel pool growth
        double pool = t.Mem.KernelPool;
        var ps = Push("kernel-pool", pool, TimeSpan.FromMinutes(30));
        bool poolBad = pool > 6.0 * 1073741824 || (ps.Count >= 20 && pool > 3.0 * 1073741824 && Growth(ps) > 1.5 * 1073741824 && Monotone(ps, 0.8));
        Set("kernel-pool", poolBad, "Windows kernel memory is ballooning",
            $"Kernel pool is {pool / 1073741824.0:0.0} GB (normally 1 to 3 GB). Large file scans cause this. It is cache; a restart resets it.");

        // leaks: steady growth of one app
        var seenKeys = new HashSet<string>();
        foreach (var g in groups.Take(40))
        {
            if (g.PrivateBytes < 300L * 1024 * 1024) continue;
            string key = "leak:" + g.Name;
            seenKeys.Add(key);
            var s = Push(key, g.PrivateBytes, TimeSpan.FromMinutes(10));
            bool leaking = false;
            if (s.Count >= 16 && !g.Name.Contains("(Windows)"))
            {
                var (slopePerMin, r2) = Regress(s);
                leaking = slopePerMin >= 60e6 && r2 >= 0.9 && Growth(s) >= 600e6 && g.PrivateBytes >= 1e9;
            }
            Set(key, leaking, $"{g.Name} keeps growing in memory",
                $"{g.Name} uses {g.PrivateBytes / 1073741824.0:0.0} GB and has climbed steadily for 10 minutes. It may be leaking; restarting it frees the memory.");
        }
        // forget series and alerts of groups that vanished
        foreach (var k in _series.Keys.Where(k => k.StartsWith("leak:") && !seenKeys.Contains(k)).ToList()) { _series.Remove(k); Set(k, false, "", ""); }
    }

    List<(DateTime t, double v)> Push(string key, double v, TimeSpan window)
    {
        if (!_series.TryGetValue(key, out var l)) _series[key] = l = new();
        var now = Clock();
        l.Add((now, v));
        l.RemoveAll(x => now - x.t > window);
        return l;
    }

    static double Growth(List<(DateTime t, double v)> s) => s[^1].v - s[0].v;

    static bool Monotone(List<(DateTime t, double v)> s, double fraction)
    {
        int up = 0;
        for (int i = 1; i < s.Count; i++) if (s[i].v >= s[i - 1].v) up++;
        return up >= fraction * (s.Count - 1);
    }

    /// <summary>Least squares slope (bytes per minute) and R squared.</summary>
    static (double slopePerMin, double r2) Regress(List<(DateTime t, double v)> s)
    {
        int n = s.Count;
        double t0 = s[0].t.Ticks;
        double[] x = s.Select(p => (p.t.Ticks - t0) / TimeSpan.TicksPerMinute).ToArray();
        double[] y = s.Select(p => p.v).ToArray();
        double mx = x.Average(), my = y.Average();
        double sxx = 0, sxy = 0, syy = 0;
        for (int i = 0; i < n; i++) { sxx += (x[i] - mx) * (x[i] - mx); sxy += (x[i] - mx) * (y[i] - my); syy += (y[i] - my) * (y[i] - my); }
        if (sxx == 0 || syy == 0) return (0, 0);
        return (sxy / sxx, sxy * sxy / (sxx * syy));
    }

    void ClearAll()
    {
        bool any;
        lock (_active) { any = _active.Count > 0; _active.Clear(); }
        if (any) Changed?.Invoke();
    }
}

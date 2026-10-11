namespace QuickSweep.Core;

public sealed class PingBrief
{
    public double Avg { get; set; }
    public double Max { get; set; }
    public double LossPct { get; set; }
    public int Spikes { get; set; }
    public int Count { get; set; }
}

public sealed class SessionEntry
{
    public string Game { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public int DurationSec { get; set; }
    public double? CpuTempPeak { get; set; }
    public double? CpuTempAvg { get; set; }
    public double GpuTempPeak { get; set; }
    public double GpuTempAvg { get; set; }
    public double CpuLoadPeak { get; set; }
    public double CpuLoadAvg { get; set; }
    public double GpuLoadPeak { get; set; }
    public double GpuLoadAvg { get; set; }
    public double RamPeakGb { get; set; }
    public double RamPeakPct { get; set; }
    public double VramPeakGb { get; set; }
    public bool CpuThrottled { get; set; }
    public bool GpuThrottled { get; set; }
    public int GpuThrottleSec { get; set; }
    public PingBrief Router { get; set; } = new();
    public PingBrief Internet { get; set; } = new();
    public List<string> Alerts { get; set; } = new();
    public int BackgroundLowered { get; set; }
    /// <summary>What Game Mode did during the session, for example "21:14 slowed ffmpeg, blender: CPU 92%".</summary>
    public List<string> GameModeLog { get; set; } = new();
}

public sealed class SessionStore { public List<SessionEntry> Items { get; set; } = new(); }

/// <summary>Accumulates stats while one game runs and produces a single summary entry when it closes.</summary>
public sealed class SessionRecorder
{
    public const double CpuThrottleC = 95; // package temperature at which a CPU is assumed to be throttling (most desktop CPUs limit themselves around 95 to 100 degrees)
    GameInfo? _game;
    DateTime _start;
    int _n;
    double _cpuTSum; int _cpuTN; double? _cpuTMax;
    double _gpuTSum, _gpuTMax, _cpuLSum, _cpuLMax, _gpuLSum, _gpuLMax, _ramMax, _ramPctMax, _vramMax;
    bool _cpuThr, _gpuThr; double _gpuThrSec;
    readonly List<string> _alerts = new();
    DateTime _last;

    public bool Recording => _game != null;
    public string? GameName => _game?.Name;
    public DateTime Started => _start;

    public void Begin(GameInfo g)
    {
        _game = g; _start = DateTime.Now; _last = _start; _n = 0;
        _cpuTSum = 0; _cpuTN = 0; _cpuTMax = null;
        _gpuTSum = _gpuTMax = _cpuLSum = _cpuLMax = _gpuLSum = _gpuLMax = _ramMax = _ramPctMax = _vramMax = 0;
        _cpuThr = _gpuThr = false; _gpuThrSec = 0;
        _alerts.Clear();
    }

    public void AddAlert(string text) { if (_game != null && !_alerts.Contains(text)) _alerts.Add(text); }

    public void Add(Telemetry t)
    {
        if (_game == null) return;
        double dt = Math.Clamp((t.Time - _last).TotalSeconds, 0, 10);
        _last = t.Time;
        _n++;
        if (t.CpuTemp is double ct)
        {
            _cpuTSum += ct; _cpuTN++; _cpuTMax = Math.Max(_cpuTMax ?? 0, ct);
            if (ct >= CpuThrottleC) _cpuThr = true;
        }
        _cpuLSum += t.CpuLoad; _cpuLMax = Math.Max(_cpuLMax, t.CpuLoad);
        if (t.Gpu is { } g)
        {
            _gpuTSum += g.TempC; _gpuTMax = Math.Max(_gpuTMax, g.TempC);
            _gpuLSum += g.Load; _gpuLMax = Math.Max(_gpuLMax, g.Load);
            _vramMax = Math.Max(_vramMax, g.VramUsed / 1073741824.0);
            if (g.ThermalThrottle) { _gpuThr = true; _gpuThrSec += dt; }
        }
        _ramMax = Math.Max(_ramMax, t.Mem.InUse / 1073741824.0);
        _ramPctMax = Math.Max(_ramPctMax, t.Mem.UsedPct);
    }

    /// <summary>Finish the session. Returns null for blips shorter than a minute.</summary>
    public SessionEntry? End(PingMonitor ping, int lowered, IEnumerable<string>? gameModeLog = null)
    {
        if (_game == null) return null;
        var end = DateTime.Now;
        var name = _game.Name;
        _game = null;
        if ((end - _start).TotalSeconds < 60 || _n == 0) return null;
        var (r, n) = PingMonitor.Summarize(ping.History(_start));
        PingBrief B(PingStats s) => new() { Avg = Math.Round(s.Avg, 1), Max = s.Max, LossPct = Math.Round(s.LossPct, 1), Spikes = s.Spikes, Count = s.Count };
        return new SessionEntry
        {
            Game = name, Start = _start, End = end, DurationSec = (int)(end - _start).TotalSeconds,
            CpuTempPeak = _cpuTMax, CpuTempAvg = _cpuTN > 0 ? Math.Round(_cpuTSum / _cpuTN, 1) : null,
            GpuTempPeak = _gpuTMax, GpuTempAvg = Math.Round(_gpuTSum / _n, 1),
            CpuLoadPeak = Math.Round(_cpuLMax), CpuLoadAvg = Math.Round(_cpuLSum / _n, 1),
            GpuLoadPeak = Math.Round(_gpuLMax), GpuLoadAvg = Math.Round(_gpuLSum / _n, 1),
            RamPeakGb = Math.Round(_ramMax, 1), RamPeakPct = Math.Round(_ramPctMax),
            VramPeakGb = Math.Round(_vramMax, 1),
            CpuThrottled = _cpuThr, GpuThrottled = _gpuThr, GpuThrottleSec = (int)_gpuThrSec,
            Router = B(r), Internet = B(n), Alerts = _alerts.ToList(), BackgroundLowered = lowered,
            GameModeLog = gameModeLog?.ToList() ?? new()
        };
    }

    public static List<SessionEntry> Load() => JsonStore.Load<SessionStore>("sessions.json").Items;

    public static void Append(SessionEntry e)
    {
        var st = JsonStore.Load<SessionStore>("sessions.json");
        st.Items.Insert(0, e);
        if (st.Items.Count > 100) st.Items.RemoveRange(100, st.Items.Count - 100);
        JsonStore.Save("sessions.json", st);
    }
}

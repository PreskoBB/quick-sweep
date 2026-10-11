namespace QuickSweep.Core;

[Flags]
public enum PressureKind { None = 0, Cpu = 1, Gpu = 2, Ram = 4, Ready = 8 }

/// <summary>One look at the machine while a game runs.</summary>
/// <param name="Cpu">total CPU load, percent</param>
/// <param name="CpuWithoutLowered">the same, minus what the already-slowed background jobs use (decides the release)</param>
/// <param name="AvailGb">available memory (standby + free), GB</param>
/// <param name="ReadyPct">share of the game's runnable threads that wait for a core, percent (null = unknown)</param>
public readonly record struct PressureSample(double Cpu, double CpuWithoutLowered, double Gpu, double AvailGb, double CommitPct, double? ReadyPct);

/// <summary>
/// Decides when the machine is overloaded enough that background work should yield, and when it is calm enough to give
/// the room back. Trigger: a limit is crossed for most of the last N seconds. Release: everything is well below the
/// limits for a much longer stretch. Between the two sits a cooldown so a borderline load cannot flap.
/// </summary>
public sealed class PressureWatcher
{
    readonly AppSettings _s;
    readonly Func<DateTime> _now;
    readonly List<(DateTime t, PressureKind kinds, bool calm, PressureSample sample)> _w = new();
    DateTime _quietUntil = DateTime.MinValue;

    public PressureWatcher(AppSettings s, Func<DateTime>? clock = null) { _s = s; _now = clock ?? (() => DateTime.Now); }

    public void Reset() { _w.Clear(); _quietUntil = DateTime.MinValue; }

    /// <summary>After a restore (or a trigger that found nothing to slow): stay quiet for this long.</summary>
    public void Quiet(TimeSpan span) { _quietUntil = _now() + span; }
    public void StartCooldown() => Quiet(TimeSpan.FromSeconds(_s.GameCooldownSeconds));

    /// <summary>Which limits this sample crosses.</summary>
    public static PressureKind Classify(AppSettings s, in PressureSample x)
    {
        var k = PressureKind.None;
        if (x.Cpu >= s.GameCpuPct) k |= PressureKind.Cpu;
        if (x.Gpu >= s.GameGpuPct) k |= PressureKind.Gpu;
        if (x.AvailGb < s.GameMinFreeRamGb || (x.CommitPct >= s.GameCommitPct && x.AvailGb < 4)) k |= PressureKind.Ram;
        if (s.GameReadyPct > 0 && x.ReadyPct is double r && r >= s.GameReadyPct && x.Cpu >= 55) k |= PressureKind.Ready;
        return k;
    }

    /// <summary>Well below every limit (the margin is the hysteresis).</summary>
    public static bool IsCalm(AppSettings s, in PressureSample x)
        => x.CpuWithoutLowered < Math.Max(30, s.GameCpuPct - 35)
           && x.Gpu < s.GameGpuPct - 15
           && x.AvailGb > s.GameMinFreeRamGb + 1.5
           && x.CommitPct < s.GameCommitPct - 5
           && (x.ReadyPct is not double r || s.GameReadyPct <= 0 || r < s.GameReadyPct / 2.0);

    public void Add(PressureSample x)
    {
        var now = _now();
        _w.Add((now, Classify(_s, x), IsCalm(_s, x), x));
        double keep = Math.Max(_s.GameTriggerSeconds, _s.GameReleaseSeconds) + 5;
        _w.RemoveAll(e => (now - e.t).TotalSeconds > keep);
    }

    /// <summary>Pressure for most of the trigger window, and not in a cooldown.</summary>
    public bool Triggered
    {
        get
        {
            var now = _now();
            if (now < _quietUntil) return false;
            var win = _w.Where(e => (now - e.t).TotalSeconds <= _s.GameTriggerSeconds).ToList();
            if (win.Count < 3) return false;
            if ((now - win[0].t).TotalSeconds < _s.GameTriggerSeconds * 0.7) return false;      // window not covered yet
            return win.Count(e => e.kinds != PressureKind.None) >= win.Count * 0.8;
        }
    }

    /// <summary>The limits crossed in the recent window (what to relieve) and the sample that describes them best.</summary>
    public (PressureKind kinds, PressureSample worst) Recent()
    {
        var now = _now();
        var win = _w.Where(e => (now - e.t).TotalSeconds <= _s.GameTriggerSeconds && e.kinds != PressureKind.None).ToList();
        var kinds = PressureKind.None;
        foreach (var e in win) kinds |= e.kinds;
        var worst = win.Count > 0 ? win.OrderByDescending(e => e.sample.Cpu + e.sample.Gpu).First().sample : default;
        return (kinds, worst);
    }

    /// <summary>Calm for the whole release window.</summary>
    public bool ReleaseReady
    {
        get
        {
            var now = _now();
            var win = _w.Where(e => (now - e.t).TotalSeconds <= _s.GameReleaseSeconds).ToList();
            if (win.Count < 3) return false;
            if ((now - win[0].t).TotalSeconds < _s.GameReleaseSeconds * 0.9) return false;
            return win.Count(e => e.calm) >= win.Count * 0.95;
        }
    }

    public static string Describe(PressureKind kinds, in PressureSample s)
    {
        var parts = new List<string>();
        if (kinds.HasFlag(PressureKind.Cpu)) parts.Add($"CPU {s.Cpu:0}%");
        if (kinds.HasFlag(PressureKind.Gpu)) parts.Add($"GPU {s.Gpu:0}%");
        if (kinds.HasFlag(PressureKind.Ram)) parts.Add($"only {s.AvailGb:0.0} GB RAM free");
        if (kinds.HasFlag(PressureKind.Ready)) parts.Add($"game threads waiting for a core ({s.ReadyPct:0}%)");
        return string.Join(", ", parts);
    }
}

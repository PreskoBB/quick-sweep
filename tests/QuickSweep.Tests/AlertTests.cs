using QuickSweep.Core;
using QuickSweep.Native;
using Xunit;

namespace QuickSweep.Tests;

public class AlertTests
{
    const long MB = 1024L * 1024, GB = 1024L * MB;

    sealed class Rig
    {
        public DateTime Now = new(2026, 10, 11, 20, 0, 0);
        public readonly AlertEngine Engine;
        public readonly List<Alert> Raised = new();
        public Rig(AppSettings? s = null)
        {
            Engine = new AlertEngine(s ?? new AppSettings()) { Clock = () => Now };
            Engine.Raised += Raised.Add;
        }
        public static Telemetry Tele(double? cpu = null, double? gpu = null, long cFree = 100 * GB, long pool = 2 * GB, long avail = 16 * GB, long commit = 20 * GB)
        {
            var t = new Telemetry { CpuTemp = cpu };
            if (gpu != null) t.Gpu = new GpuReading { TempC = gpu.Value };
            t.Drives = new[] { new DriveStat { Letter = "C", Total = 230 * GB, Free = cFree } };
            t.Mem = new MemoryStats { Total = 32 * GB, Available = avail, Commit = commit, CommitLimit = 64 * GB, KernelPaged = pool / 2, KernelNonpaged = pool / 2 };
            return t;
        }
        public static AppGroup Group(string name, long bytes) => new() { Key = "app:" + name, Name = name, PrivateBytes = bytes, Count = 1 };
        public bool Has(string keyPrefix) => Engine.Active.Any(a => a.Key.StartsWith(keyPrefix));
    }

    [Fact]
    public void CpuHeatNeedsToBeSustained()
    {
        var r = new Rig();
        r.Engine.EvaluateFast(Rig.Tele(cpu: 95));
        Assert.False(r.Has("cpu-hot"));                       // one hot sample is not an alert
        r.Now = r.Now.AddSeconds(11);
        r.Engine.EvaluateFast(Rig.Tele(cpu: 95));
        Assert.True(r.Has("cpu-hot"));
        Assert.Single(r.Raised);
        r.Now = r.Now.AddSeconds(5);
        r.Engine.EvaluateFast(Rig.Tele(cpu: 70));              // cooled right down: cleared
        Assert.False(r.Has("cpu-hot"));
    }

    [Fact]
    public void NormalTemperaturesNeverAlert()
    {
        var r = new Rig();
        for (int i = 0; i < 100; i++) { r.Now = r.Now.AddSeconds(4); r.Engine.EvaluateFast(Rig.Tele(cpu: 68, gpu: 66)); }
        Assert.Empty(r.Engine.Active);
        Assert.Empty(r.Raised);
    }

    [Fact]
    public void GpuAlertUsesItsOwnLimit()
    {
        var r = new Rig();
        r.Engine.EvaluateFast(Rig.Tele(gpu: 85));
        r.Now = r.Now.AddSeconds(12);
        r.Engine.EvaluateFast(Rig.Tele(gpu: 85));
        Assert.True(r.Has("gpu-hot"));
    }

    [Fact]
    public void LowSystemDriveSpaceAlerts()
    {
        var r = new Rig();
        r.Engine.EvaluateSlow(Rig.Tele(cFree: 9 * GB), new());
        Assert.True(r.Has("c-low"));
        r.Engine.EvaluateSlow(Rig.Tele(cFree: 40 * GB), new());
        Assert.False(r.Has("c-low"));
    }

    [Fact]
    public void SteadilyGrowingAppIsFlaggedAsLeak()
    {
        var r = new Rig();
        long bytes = 1500 * MB;
        for (int i = 0; i < 21; i++)                           // 10 minutes, one sample per 30 s, +100 MB/min
        {
            r.Engine.EvaluateSlow(Rig.Tele(), new() { Rig.Group("LeakyApp", bytes) });
            r.Now = r.Now.AddSeconds(30); bytes += 50 * MB;
        }
        Assert.True(r.Has("leak:LeakyApp"));
    }

    [Fact]
    public void OneBigLoadStepIsNotALeak()
    {
        var r = new Rig();
        long bytes = 1200 * MB;
        for (int i = 0; i < 21; i++)                           // a game loading a level: jump, then flat
        {
            if (i == 6) bytes += 3000 * MB;
            r.Engine.EvaluateSlow(Rig.Tele(), new() { Rig.Group("Game", bytes) });
            r.Now = r.Now.AddSeconds(30);
        }
        Assert.False(r.Has("leak:Game"));
    }

    [Fact]
    public void StableAndSmallAppsNeverAlert()
    {
        var r = new Rig();
        for (int i = 0; i < 40; i++)
        {
            r.Engine.EvaluateSlow(Rig.Tele(), new() { Rig.Group("Browser", 2500 * MB), Rig.Group("Tiny", 200 * MB + i * 5 * MB) });
            r.Now = r.Now.AddSeconds(30);
        }
        Assert.Empty(r.Engine.Active);
    }

    [Fact]
    public void BallooningKernelPoolAlerts()
    {
        var r = new Rig();
        r.Engine.EvaluateSlow(Rig.Tele(pool: (long)(7.5 * GB), avail: 1 * GB), new());     // the 7.5 GB seen in the audit, with RAM nearly used up
        Assert.True(r.Has("kernel-pool"));
        r.Engine.EvaluateSlow(Rig.Tele(pool: 2 * GB, avail: 1 * GB), new());
        Assert.False(r.Has("kernel-pool"));
    }

    [Fact]
    public void KernelPoolGrowthOverHalfAnHourAlerts()
    {
        var r = new Rig();
        long pool = 3 * GB + 100 * MB;
        for (int i = 0; i < 61; i++) { r.Engine.EvaluateSlow(Rig.Tele(pool: pool, avail: 1 * GB), new()); r.Now = r.Now.AddSeconds(30); pool += 40 * MB; }
        Assert.True(r.Has("kernel-pool"));
    }

    [Fact]
    public void BigKernelPoolWithPlentyOfMemoryDoesNotAlert()
    {
        var r = new Rig();
        r.Engine.EvaluateSlow(Rig.Tele(pool: (long)(7.5 * GB)), new());                       // 16 GB free, 31% committed: only cache
        Assert.False(r.Has("kernel-pool"));
        Assert.Empty(r.Raised);
    }

    [Fact]
    public void BigKernelPoolAlertsWhenFreeRamIsLow()
    {
        var r = new Rig();
        r.Engine.EvaluateSlow(Rig.Tele(pool: 7 * GB, avail: (long)(1.9 * GB)), new());
        Assert.True(r.Has("kernel-pool"));
        Assert.Equal("memory", r.Engine.Active.Single(a => a.Key == "kernel-pool").Page);
    }

    [Fact]
    public void BigKernelPoolAlertsWhenCommitIsHigh()
    {
        var r = new Rig();
        r.Engine.EvaluateSlow(Rig.Tele(pool: 7 * GB, commit: (long)(0.91 * 64 * GB)), new());     // plenty of free RAM, but commit at 91%
        Assert.True(r.Has("kernel-pool"));
        r.Engine.EvaluateSlow(Rig.Tele(pool: 7 * GB, commit: (long)(0.80 * 64 * GB)), new());
        Assert.False(r.Has("kernel-pool"));
    }

    [Fact]
    public void SmallKernelPoolNeverAlertsEvenUnderPressure()
    {
        var r = new Rig();
        r.Engine.EvaluateSlow(Rig.Tele(pool: 2 * GB, avail: 500 * MB, commit: 62 * GB), new());
        Assert.False(r.Has("kernel-pool"));
    }

    [Fact]
    public void PoolAlertThresholdsComeFromSettings()
    {
        var r = new Rig(new AppSettings { PoolAlertGb = 3, PoolPressureFreeRamGb = 6, PoolPressureCommitPct = 99 });
        r.Engine.EvaluateSlow(Rig.Tele(pool: 4 * GB, avail: 5 * GB), new());                  // 4 GB pool and 5 GB free: over the user's limits
        Assert.True(r.Has("kernel-pool"));
        var strict = new Rig(new AppSettings { PoolPressureFreeRamGb = 0.5, PoolPressureCommitPct = 99 });
        strict.Engine.EvaluateSlow(Rig.Tele(pool: 7 * GB, avail: 1 * GB), new());              // 1 GB free is fine for a 0.5 GB limit
        Assert.False(strict.Has("kernel-pool"));
    }

    [Fact]
    public void PoolAlertDoesNotFlapAtTheEdgeOfThePressureLimit()
    {
        var r = new Rig();
        r.Engine.EvaluateSlow(Rig.Tele(pool: 7 * GB, avail: (long)(1.8 * GB)), new());
        Assert.True(r.Has("kernel-pool"));
        r.Engine.EvaluateSlow(Rig.Tele(pool: 7 * GB, avail: (long)(2.2 * GB)), new());        // just above 2 GB: still on (hysteresis)
        Assert.True(r.Has("kernel-pool"));
        r.Engine.EvaluateSlow(Rig.Tele(pool: 7 * GB, avail: 4 * GB), new());
        Assert.False(r.Has("kernel-pool"));
    }

    [Fact]
    public void UnreadableMemoryFiguresAreNotPressure()
    {
        var r = new Rig();
        var t = Rig.Tele(pool: 7 * GB);
        t.Mem = new MemoryStats { KernelPaged = 3 * GB, KernelNonpaged = 4 * GB };             // everything zero: not read
        r.Engine.EvaluateSlow(t, new());
        Assert.False(r.Has("kernel-pool"));
    }

    [Fact]
    public void EveryAlertHasATitleADetailAndAKnownPage()
    {
        var r = new Rig();
        r.Engine.EvaluateFast(Rig.Tele(cpu: 99, gpu: 99)); r.Now = r.Now.AddSeconds(11);
        r.Engine.EvaluateFast(Rig.Tele(cpu: 99, gpu: 99));
        r.Engine.EvaluateSlow(Rig.Tele(cFree: 2 * GB, pool: 7 * GB, avail: 1 * GB), new());
        r.Engine.Raise("drive:X", true, "Some drive is failing", "Detail", "health", AlertLevel.Critical);
        Assert.True(r.Engine.Active.Count >= 5);
        foreach (var a in r.Engine.Active)
        {
            Assert.False(string.IsNullOrWhiteSpace(a.Title), a.Key);
            Assert.False(string.IsNullOrWhiteSpace(a.Detail), a.Key);
            Assert.True(a.Page == null || QuickSweep.Ui.OpenRequest.PageKeys.Contains(a.Page), a.Key + " -> " + a.Page);
        }
    }

    [Fact]
    public void QuietAlertShowsInTheBannerButNeverNotifiesUntilItTurnsCritical()
    {
        var r = new Rig();
        r.Engine.Raise("drive:X", true, "Disk has old damage", "296 reallocated sectors", "health", AlertLevel.Warning, quiet: true);
        Assert.Single(r.Engine.Active);
        Assert.Empty(r.Raised);
        r.Engine.Raise("drive:X", true, "Disk is getting worse", "297 reallocated sectors", "health", AlertLevel.Critical);
        Assert.Single(r.Engine.Active);
        Assert.Equal("Disk is getting worse", r.Engine.Active[0].Title);       // the title follows the new result
        Assert.True(r.Engine.Active[0].IsCritical);
        Assert.Single(r.Raised);                                               // and it notifies once
        r.Engine.Raise("drive:X", true, "Disk is getting worse", "297 reallocated sectors", "health", AlertLevel.Critical);
        Assert.Single(r.Raised);
    }

    [Fact]
    public void AlertsCanBeSwitchedOff()
    {
        var r = new Rig(new AppSettings { AlertsEnabled = false });
        r.Engine.EvaluateSlow(Rig.Tele(cFree: 1 * GB), new());
        r.Engine.EvaluateFast(Rig.Tele(cpu: 99));
        Assert.Empty(r.Engine.Active);
    }

    [Fact]
    public void RepeatedConditionDoesNotNagWithinTheCooldown()
    {
        var r = new Rig();
        r.Engine.EvaluateSlow(Rig.Tele(cFree: 5 * GB), new());
        r.Engine.EvaluateSlow(Rig.Tele(cFree: 40 * GB), new());          // fixed
        r.Now = r.Now.AddMinutes(30);
        r.Engine.EvaluateSlow(Rig.Tele(cFree: 5 * GB), new());           // back again 30 min later
        Assert.Single(r.Raised);                                          // active again, but no second notification
        r.Now = r.Now.AddHours(4);
        r.Engine.EvaluateSlow(Rig.Tele(cFree: 40 * GB), new());
        r.Engine.EvaluateSlow(Rig.Tele(cFree: 5 * GB), new());
        Assert.Equal(2, r.Raised.Count);
    }
}

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
        public static Telemetry Tele(double? cpu = null, double? gpu = null, long cFree = 100 * GB, long pool = 2 * GB)
        {
            var t = new Telemetry { CpuTemp = cpu };
            if (gpu != null) t.Gpu = new GpuReading { TempC = gpu.Value };
            t.Drives = new[] { new DriveStat { Letter = "C", Total = 230 * GB, Free = cFree } };
            t.Mem = new MemoryStats { Total = 32 * GB, KernelPaged = pool / 2, KernelNonpaged = pool / 2 };
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
        r.Engine.EvaluateSlow(Rig.Tele(pool: (long)(7.5 * GB)), new());     // the 7.5 GB seen in the audit
        Assert.True(r.Has("kernel-pool"));
        r.Engine.EvaluateSlow(Rig.Tele(pool: 2 * GB), new());
        Assert.False(r.Has("kernel-pool"));
    }

    [Fact]
    public void KernelPoolGrowthOverHalfAnHourAlerts()
    {
        var r = new Rig();
        long pool = 3 * GB + 100 * MB;
        for (int i = 0; i < 61; i++) { r.Engine.EvaluateSlow(Rig.Tele(pool: pool), new()); r.Now = r.Now.AddSeconds(30); pool += 40 * MB; }
        Assert.True(r.Has("kernel-pool"));
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

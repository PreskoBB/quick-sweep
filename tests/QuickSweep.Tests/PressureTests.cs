using QuickSweep.Core;
using QuickSweep.Native;
using Xunit;

namespace QuickSweep.Tests;

public class PressureTests
{
    sealed class Rig
    {
        public DateTime Now = new(2026, 10, 11, 21, 0, 0);
        public readonly AppSettings S = new();
        public readonly PressureWatcher W;
        public Rig() { W = new PressureWatcher(S, () => Now); }
        public static PressureSample Calm => new(40, 40, 50, 12, 40, 5);
        public void Feed(PressureSample x, int seconds, int step = 2)
        {
            for (int t = 0; t < seconds; t += step) { W.Add(x); Now = Now.AddSeconds(step); }
        }
    }

    [Fact]
    public void DefaultsMatchTheDocumentedThresholds()
    {
        var s = new AppSettings();
        Assert.True(s.GameModeAuto);
        Assert.Equal(85, s.GameCpuPct); Assert.Equal(95, s.GameGpuPct); Assert.Equal(2.0, s.GameMinFreeRamGb);
        Assert.Equal(12, s.GameTriggerSeconds); Assert.Equal(90, s.GameReleaseSeconds);
    }

    [Fact]
    public void SustainedCpuPressureTriggers()
    {
        var r = new Rig();
        var hot = Rig.Calm with { Cpu = 93, CpuWithoutLowered = 93 };
        r.Feed(hot, 6);
        Assert.False(r.W.Triggered);                // too short
        r.Feed(hot, 10);
        Assert.True(r.W.Triggered);
        var (kinds, worst) = r.W.Recent();
        Assert.Equal(PressureKind.Cpu, kinds);
        Assert.Equal("CPU 93%", PressureWatcher.Describe(kinds, worst));
    }

    [Fact]
    public void ABriefSpikeDoesNotTrigger()
    {
        var r = new Rig();
        r.Feed(Rig.Calm, 20);
        r.Feed(Rig.Calm with { Cpu = 99 }, 4);      // a level load or alt-tab
        r.Feed(Rig.Calm, 10);
        Assert.False(r.W.Triggered);
    }

    [Fact]
    public void NormalGamingLoadNeverTriggers()
    {
        var r = new Rig();
        r.Feed(new PressureSample(70, 70, 92, 6, 60, 12), 300);   // heavy but within limits
        Assert.False(r.W.Triggered);
    }

    [Theory]
    [InlineData(40, 97, 12, 40, 5, PressureKind.Gpu)]
    [InlineData(40, 50, 1.4, 40, 5, PressureKind.Ram)]
    [InlineData(40, 50, 3.5, 96, 5, PressureKind.Ram)]        // commit nearly full with little RAM left
    [InlineData(70, 50, 12, 40, 31, PressureKind.Ready)]      // game threads wait for a core
    [InlineData(90, 99, 1.0, 40, 5, PressureKind.Cpu | PressureKind.Gpu | PressureKind.Ram)]
    public void EachSignalIsClassified(double cpu, double gpu, double availGb, double commit, double ready, PressureKind expected)
        => Assert.Equal(expected, PressureWatcher.Classify(new AppSettings(), new PressureSample(cpu, cpu, gpu, availGb, commit, ready)));

    [Fact]
    public void ReadyProxyIsIgnoredAtLowCpuOrWhenSwitchedOff()
    {
        var s = new AppSettings();
        Assert.Equal(PressureKind.None, PressureWatcher.Classify(s, new PressureSample(30, 30, 40, 12, 40, 80)));
        s.GameReadyPct = 0;
        Assert.Equal(PressureKind.None, PressureWatcher.Classify(s, new PressureSample(70, 70, 40, 12, 40, 80)));
    }

    [Fact]
    public void ReleaseNeedsALongCalmAndHasHysteresis()
    {
        var r = new Rig();
        r.Feed(Rig.Calm with { Cpu = 95, CpuWithoutLowered = 95 }, 20);
        Assert.True(r.W.Triggered);
        // below the trigger limit but not calm (80% without the slowed jobs): no release, however long
        r.Feed(Rig.Calm with { Cpu = 80, CpuWithoutLowered = 80 }, 200);
        Assert.False(r.W.ReleaseReady);
        r.Feed(Rig.Calm with { Cpu = 98, CpuWithoutLowered = 30 }, 60);    // the slowed jobs burn the idle cycles; the rest is calm
        Assert.False(r.W.ReleaseReady);                                      // 60 s is not enough
        r.Feed(Rig.Calm with { Cpu = 98, CpuWithoutLowered = 30 }, 40);
        Assert.True(r.W.ReleaseReady);
    }

    [Fact]
    public void CooldownStopsFlapping()
    {
        var r = new Rig();
        var hot = Rig.Calm with { Cpu = 95, CpuWithoutLowered = 95 };
        r.Feed(hot, 20);
        Assert.True(r.W.Triggered);
        r.W.StartCooldown();
        r.Feed(hot, 60);
        Assert.False(r.W.Triggered);                // still cooling down (120 s)
        r.Feed(hot, 70);
        Assert.True(r.W.Triggered);
    }

    [Fact]
    public void ThresholdsComeFromSettings()
    {
        var r = new Rig();
        r.S.GameCpuPct = 70; r.S.GameTriggerSeconds = 6;
        var load = Rig.Calm with { Cpu = 75, CpuWithoutLowered = 75 };
        r.Feed(load, 10);
        Assert.True(r.W.Triggered);
    }

    // ---- which background jobs count as contributing ----------------------------------------------------------
    static BgJob Job(string name, double cpu, double gpu = 0, long ws = 100L * 1024 * 1024, double min = 1)
        => new() { P = new ProcInfo(100 + name.Length, 1, name + ".exe", ws, ws, ws, 0, 0, 4, 1), Name = name, CpuShare = cpu, GpuShare = gpu, MinShare = min };

    [Fact]
    public void CpuPressureTakesTheJobsThatAddUpToEnough()
    {
        var s = new AppSettings();
        var jobs = new List<BgJob> { Job("ffmpeg", 22), Job("blender", 14), Job("idle-node", 0.1, min: 2) };
        var chosen = BackgroundWork.Contributors(jobs, PressureKind.Cpu, s);
        Assert.Equal(new[] { "ffmpeg", "blender" }, chosen.Select(j => j.Name).ToArray());
    }

    [Fact]
    public void BackgroundWorkThatIsTooSmallIsLeftAlone()
    {
        var s = new AppSettings();                        // needs 5% in total
        var jobs = new List<BgJob> { Job("ffmpeg", 2.5), Job("node", 1.0, min: 2) };
        Assert.Empty(BackgroundWork.Contributors(jobs, PressureKind.Cpu, s));
    }

    [Fact]
    public void GpuPressureNeedsGpuUse()
    {
        var s = new AppSettings();
        var jobs = new List<BgJob> { Job("blender", 3, gpu: 40), Job("ffmpeg", 30, gpu: 0) };
        var chosen = BackgroundWork.Contributors(jobs, PressureKind.Gpu, s);
        Assert.Equal(new[] { "blender" }, chosen.Select(j => j.Name).ToArray());
    }

    [Fact]
    public void RamPressureLooksAtMemoryNotCpu()
    {
        var s = new AppSettings();
        var jobs = new List<BgJob> { Job("blender", 0, ws: 3L * 1024 * 1024 * 1024), Job("small", 0, ws: 50L * 1024 * 1024) };
        var chosen = BackgroundWork.Contributors(jobs, PressureKind.Ram, s);
        Assert.Equal(new[] { "blender" }, chosen.Select(j => j.Name).ToArray());
    }

    [Fact]
    public void NothingContributesWhenThereIsNoBackgroundWork() =>
        Assert.Empty(BackgroundWork.Contributors(new List<BgJob>(), PressureKind.Cpu | PressureKind.Gpu | PressureKind.Ram, new AppSettings()));

    [Fact]
    public void GpuEngineInstanceNamesYieldThePid()
    {
        Assert.Equal(1234, GpuEngines.ParsePid("pid_1234_luid_0x00000000_0x0000F3A8_phys_0_eng_0_engtype_3D"));
        Assert.Equal(-1, GpuEngines.ParsePid("garbage"));
    }

    [Fact]
    public void GamingFlagIsWrittenAndRemoved()
    {
        // The flag lives in the data folder. Never touch the real one (a real game may be running): run this test only
        // with QUICKSWEEP_DATA pointing at a scratch folder.
        if (Environment.GetEnvironmentVariable("QUICKSWEEP_DATA") is not { Length: > 0 }) return;
        GamingFlag.Clear();
        GamingFlag.Write(new GameInfo { Name = "Test Game", ExeName = "TestGame.exe", Pid = 42, Started = new DateTime(2026, 10, 11, 21, 14, 0) });
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(GamingFlag.FilePath));
            var r = doc.RootElement;
            Assert.Equal("Test Game", r.GetProperty("game").GetString());
            Assert.Equal("TestGame.exe", r.GetProperty("exe").GetString());
            Assert.True(DateTimeOffset.TryParse(r.GetProperty("since").GetString(), out _));
        }
        finally { GamingFlag.Clear(); }
        Assert.False(File.Exists(GamingFlag.FilePath));
    }
}

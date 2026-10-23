using QuickSweep.Core;
using QuickSweep.Native;
using Xunit;

namespace QuickSweep.Tests;

/// <summary>
/// The GPU chain (NVIDIA, LibreHardwareMonitor, Windows counters) with fakes for every source. Nothing here touches hardware, so
/// the AMD and "no sensor" cases are exercised on a PC that only has an NVIDIA card.
/// </summary>
public class GpuSourceTests
{
    const long MB = 1024L * 1024;

    static GpuAdapter A(string name, GpuVendor v, long vramMb, long luid = 0xA1, bool software = false)
        => new(name, v, luid, vramMb * MB, 0, software, GpuChooser.LooksIntegrated(name, v));

    sealed class FakeLister : IGpuAdapterLister
    {
        public List<GpuAdapter> Items = new();
        public bool Throw;
        public int Calls;
        public IReadOnlyList<GpuAdapter> List() { Calls++; if (Throw) throw new InvalidOperationException("dxgi failed"); return Items; }
    }

    sealed class FakeSource : IGpuSource
    {
        public string Label { get; init; } = "fake";
        public bool Authoritative { get; init; }
        public Func<GpuAdapter?, bool> AppliesFn = _ => true;
        public Func<GpuAdapter?, GpuPartial?> ReadFn = _ => null;
        public int Reads;
        public string Status { get; set; } = "ok";
        public bool Applies(GpuAdapter? a) => AppliesFn(a);
        public GpuPartial? Read(GpuAdapter? a) { Reads++; return ReadFn(a); }
    }

    static GpuMonitor Mon(FakeLister l, params IGpuSource[] s) => new(l, s, () => new DateTime(2026, 10, 23, 12, 0, 0));

    static FakeSource Nvidia(GpuPartial? p) => new() { Label = "NVML", Authoritative = true, AppliesFn = a => (a == null || a.Vendor == GpuVendor.Nvidia) && p != null, ReadFn = _ => p };
    static FakeSource Lhm(GpuPartial? p) => new() { Label = "LibreHardwareMonitor", AppliesFn = a => a is { Vendor: GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel }, ReadFn = _ => p };
    static FakeSource Counters(GpuPartial? p) => new() { Label = "Windows counters", ReadFn = _ => p };

    // ---- the four situations in the task --------------------------------------------------------------------------------------

    [Fact]
    public void NvidiaPresent_UsesNvmlAndNeverOpensTheOtherSources()
    {
        var nv = new GpuPartial { Source = "NVML", Name = "NVIDIA GeForce RTX 3060", TempC = 44, Load = 26, VramUsed = 3221225472, VramTotal = 12884901888, FanPct = 0, PowerW = 29.3, ClockMhz = 1170, ThermalThrottle = false, ThrottleReasons = 1 };
        var lhm = Lhm(new GpuPartial { Source = "LibreHardwareMonitor", TempC = 99 });
        var cnt = Counters(new GpuPartial { Source = "Windows counters", Load = 99 });
        var m = Mon(new FakeLister { Items = { A("NVIDIA GeForce RTX 3060", GpuVendor.Nvidia, 12288), A("Microsoft Basic Render Driver", GpuVendor.Microsoft, 0, 2, true) } }, Nvidia(nv), lhm, cnt);
        var r = m.Read()!;
        Assert.Equal("NVIDIA GeForce RTX 3060", r.Name);
        Assert.Equal(44, r.TempC); Assert.Equal(26, r.Load);
        Assert.Equal(3221225472, r.VramUsed); Assert.Equal(12884901888, r.VramTotal);
        Assert.Equal(0, r.FanPct); Assert.Equal(29.3, r.PowerW); Assert.Equal(1170, r.ClockMhz); Assert.Equal(1UL, r.ThrottleReasons);
        Assert.Equal("NVML", r.TempSource); Assert.Equal("NVML", r.LoadSource); Assert.Equal("NVML", r.VramSource);
        Assert.Equal(0, lhm.Reads);           // LibreHardwareMonitor is not even opened
        Assert.Equal(0, cnt.Reads);           // and no performance counter is sampled
    }

    [Fact]
    public void NvidiaIdleAtZeroLoadStillStopsAtNvml()
    {
        var nv = new GpuPartial { Source = "NVML", Name = "RTX", TempC = 38, Load = 0, VramUsed = 900 * MB, VramTotal = 12000 * MB };
        var cnt = Counters(new GpuPartial { Source = "Windows counters", Load = 40 });
        var r = Mon(new FakeLister { Items = { A("RTX", GpuVendor.Nvidia, 12000) } }, Nvidia(nv), Lhm(null), cnt).Read()!;
        Assert.Equal(0, r.Load);              // the driver's 0 is believed: it is the authoritative source
        Assert.Equal(0, cnt.Reads);
    }

    [Fact]
    public void AmdThroughLibreHardwareMonitor_GivesTemperatureLoadVramAndFan()
    {
        var rx = A("Radeon RX 580 Series", GpuVendor.Amd, 8192);
        var lhm = new GpuPartial { Source = "LibreHardwareMonitor", TempC = 62, TempSensor = "GPU Core", HotSpotC = 71, Load = 37, VramUsed = 2300 * MB, VramTotal = 8192 * MB, FanPct = 41, FanRpm = 1450 };
        var cnt = new GpuPartial { Source = "Windows counters", Load = 36, VramUsed = 2290 * MB };
        var nv = Nvidia(null);
        var m = Mon(new FakeLister { Items = { rx } }, nv, Lhm(lhm), Counters(cnt));
        var r = m.Read()!;
        Assert.Equal("Radeon RX 580 Series", r.Name); Assert.Equal("AMD", r.Vendor);
        Assert.Equal(62, r.TempC); Assert.Equal("LibreHardwareMonitor", r.TempSource); Assert.Equal("GPU Core", r.TempSensor);
        Assert.Equal(71, r.HotSpotC);
        Assert.Equal(37, r.Load); Assert.Equal("LibreHardwareMonitor", r.LoadSource);
        Assert.Equal(2300 * MB, r.VramUsed); Assert.Equal(8192 * MB, r.VramTotal);
        Assert.Equal(41, r.FanPct); Assert.Equal(1450, r.FanRpm);
        Assert.Equal(0, nv.Reads);           // NVML never asked about an AMD card
        Assert.Equal("RX 580", GpuChooser.ShortName(r.Name).Replace("Radeon ", ""));
    }

    [Fact]
    public void AmdWhoseDriverReportsZeroLoad_FallsBackToTheCounters()
    {
        var rx = A("Radeon RX 580 Series", GpuVendor.Amd, 8192);
        var lhm = new GpuPartial { Source = "LibreHardwareMonitor", TempC = 55, Load = 0, VramUsed = 0, VramTotal = 8192 * MB };
        var cnt = new GpuPartial { Source = "Windows counters", Load = 64, VramUsed = 3100 * MB };
        var r = Mon(new FakeLister { Items = { rx } }, Nvidia(null), Lhm(lhm), Counters(cnt)).Read()!;
        Assert.Equal(55, r.TempC);
        Assert.Equal(64, r.Load); Assert.Equal("Windows counters", r.LoadSource);          // LibreHardwareMonitor said 0 while Windows sees work
        Assert.Equal(3100 * MB, r.VramUsed);
    }

    [Fact]
    public void AmdWhoseDriverReportsZeroLoadAndCountersAgree_StaysZero()
    {
        var rx = A("Radeon RX 580 Series", GpuVendor.Amd, 8192);
        var lhm = new GpuPartial { Source = "LibreHardwareMonitor", TempC = 40, Load = 0, VramUsed = 300 * MB, VramTotal = 8192 * MB };
        var cnt = new GpuPartial { Source = "Windows counters", Load = 1.5, VramUsed = 310 * MB };
        var r = Mon(new FakeLister { Items = { rx } }, Lhm(lhm), Counters(cnt)).Read()!;
        Assert.Equal(0, r.Load); Assert.Equal("LibreHardwareMonitor", r.LoadSource);
    }

    [Fact]
    public void CountersOnly_NoTemperatureSensor_StillShowsUsageAndVram()
    {
        var rx = A("Radeon RX 580 Series", GpuVendor.Amd, 8192);
        var lhm = new GpuPartial { Source = "LibreHardwareMonitor", Note = "LibreHardwareMonitor found this GPU but the driver offers no temperature sensor." };
        var cnt = new GpuPartial { Source = "Windows counters", Load = 52, VramUsed = 4096 * MB };
        var r = Mon(new FakeLister { Items = { rx } }, Nvidia(null), Lhm(lhm), Counters(cnt)).Read()!;
        Assert.Null(r.TempC);                                  // "--", not 0
        Assert.Contains("no temperature sensor", r.Note);
        Assert.Equal(52, r.Load); Assert.Equal("Windows counters", r.LoadSource);
        Assert.Equal(4096 * MB, r.VramUsed);
        Assert.Equal(8192 * MB, r.VramTotal); Assert.Equal("Windows counters", r.VramSource);   // total from DXGI
        Assert.Equal("Radeon RX 580 Series", r.Name);
    }

    [Fact]
    public void CountersOnly_VramTotalComesFromTheAdapterList()
    {
        var r = Mon(new FakeLister { Items = { A("Some Vendor GPU", GpuVendor.Other, 6144) } }, Counters(new GpuPartial { Source = "Windows counters", Load = 10, VramUsed = 1000 * MB })).Read()!;
        Assert.Equal(6144 * MB, r.VramTotal);
        Assert.Null(r.TempC);
    }

    [Fact]
    public void GpuFoundButEveryReadingSourceFails_StillNamesTheCardInsteadOfSayingNoGpu()
    {
        var r = Mon(new FakeLister { Items = { A("Radeon RX 580 Series", GpuVendor.Amd, 8192) } }, Nvidia(null), Lhm(null), Counters(null)).Read();
        Assert.NotNull(r);
        Assert.Equal("Radeon RX 580 Series", r!.Name);
        Assert.Null(r.TempC); Assert.Null(r.Load);
        Assert.Equal(8192 * MB, r.VramTotal);
    }

    [Fact]
    public void Nothing_ReturnsNullSoTheCardSaysNoGpuFound()
    {
        var l = new FakeLister();
        var m = Mon(l, Nvidia(null), Lhm(null), Counters(null));
        Assert.Null(m.Read());
        Assert.Null(m.Last);
        Assert.Equal("GPU: none found", m.StatusText());
    }

    [Fact]
    public void OnlyASoftwareAdapter_CountsAsNoGpu()
    {
        var m = Mon(new FakeLister { Items = { A("Microsoft Basic Render Driver", GpuVendor.Microsoft, 0, 2, true) } }, Nvidia(null), Counters(null));
        Assert.Null(m.Read());
    }

    [Fact]
    public void WindowsListsNoAdapterButNvmlWorks_NvmlStillAnswers()
    {
        var nv = new GpuPartial { Source = "NVML", Name = "NVIDIA GeForce GTX 1060", TempC = 51, Load = 12, VramUsed = 500 * MB, VramTotal = 6144 * MB };
        var l = new FakeLister { Throw = true };
        var r = Mon(l, Nvidia(nv), Lhm(null), Counters(null)).Read()!;
        Assert.Equal("NVIDIA GeForce GTX 1060", r.Name); Assert.Equal(51, r.TempC);
    }

    [Fact]
    public void NvidiaCardWithoutNvml_FallsBackToLibreHardwareMonitorThenCounters()
    {
        var gtx = A("NVIDIA GeForce GTX 1060", GpuVendor.Nvidia, 6144);
        var r = Mon(new FakeLister { Items = { gtx } }, Nvidia(null),
            Lhm(new GpuPartial { Source = "LibreHardwareMonitor", TempC = 47 }),
            Counters(new GpuPartial { Source = "Windows counters", Load = 22, VramUsed = 800 * MB })).Read()!;
        Assert.Equal(47, r.TempC); Assert.Equal("LibreHardwareMonitor", r.TempSource);
        Assert.Equal(22, r.Load); Assert.Equal("Windows counters", r.LoadSource);
    }

    [Fact]
    public void ASourceThatThrowsNeverBreaksTheReading()
    {
        var boom = new FakeSource { Label = "LibreHardwareMonitor", ReadFn = _ => throw new InvalidOperationException("ADL crashed") };
        var r = Mon(new FakeLister { Items = { A("Radeon RX 580 Series", GpuVendor.Amd, 8192) } }, boom, Counters(new GpuPartial { Source = "Windows counters", Load = 30 })).Read()!;
        Assert.Equal(30, r.Load);
    }

    [Fact]
    public void VramUsedLargerThanTheCardIsDroppedNotShown()
    {
        var lhm = new GpuPartial { Source = "LibreHardwareMonitor", VramUsed = 8192 * MB * 1000 };   // a unit mix-up
        var r = Mon(new FakeLister { Items = { A("Radeon RX 580 Series", GpuVendor.Amd, 8192) } }, Lhm(lhm)).Read()!;
        Assert.Equal(0, r.VramUsed);
        Assert.Equal(8192 * MB, r.VramTotal);
    }

    // ---- choosing the adapter -----------------------------------------------------------------------------------------------

    [Fact]
    public void DiscreteBeatsIntegrated_WhateverTheOrder()
    {
        var igpu = A("Intel(R) UHD Graphics 630", GpuVendor.Intel, 128, 1);
        var dgpu = A("Radeon RX 580 Series", GpuVendor.Amd, 8192, 2);
        Assert.Same(dgpu, GpuChooser.Pick(new[] { igpu, dgpu }));
        Assert.Same(dgpu, GpuChooser.Pick(new[] { dgpu, igpu }));
    }

    [Fact]
    public void ABigIntegratedCarveOutDoesNotBeatAnActualCard()
    {
        var apu = A("AMD Radeon(TM) Graphics", GpuVendor.Amd, 8192, 1);       // UMA frame buffer set to 8 GB in the BIOS
        var card = A("NVIDIA GeForce GTX 1650", GpuVendor.Nvidia, 4096, 2);
        Assert.True(apu.Integrated); Assert.False(card.Integrated);
        Assert.Same(card, GpuChooser.Pick(new[] { apu, card }));
    }

    [Fact]
    public void SeveralDiscreteCards_MostDedicatedVramWins()
    {
        var small = A("NVIDIA GeForce GT 1030", GpuVendor.Nvidia, 2048, 1);
        var big = A("Radeon RX 580 Series", GpuVendor.Amd, 8192, 2);
        Assert.Same(big, GpuChooser.Pick(new[] { small, big }));
    }

    [Fact]
    public void OnlyAnIntegratedGpu_IsUsed()
    {
        var igpu = A("Intel(R) UHD Graphics 630", GpuVendor.Intel, 128);
        Assert.Same(igpu, GpuChooser.Pick(new[] { igpu, A("Microsoft Basic Render Driver", GpuVendor.Microsoft, 0, 9, true) }));
    }

    [Fact]
    public void SoftwareAndRemoteStandInsComeLast()
    {
        var remote = A("Microsoft Remote Display Adapter", GpuVendor.Microsoft, 0, 3);
        var igpu = A("Intel(R) UHD Graphics 630", GpuVendor.Intel, 128, 1);
        Assert.Same(igpu, GpuChooser.Pick(new[] { remote, igpu }));
        Assert.Same(remote, GpuChooser.Pick(new[] { remote, A("WARP", GpuVendor.Microsoft, 0, 4, true) }));
        Assert.Null(GpuChooser.Pick(Array.Empty<GpuAdapter>()));
    }

    [Theory]
    [InlineData("Intel(R) UHD Graphics 630", GpuVendor.Intel, true)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", GpuVendor.Intel, true)]
    [InlineData("Intel(R) Arc(TM) Graphics", GpuVendor.Intel, true)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", GpuVendor.Intel, false)]
    [InlineData("Intel(R) Arc(TM) B580 Graphics", GpuVendor.Intel, false)]
    [InlineData("AMD Radeon(TM) Graphics", GpuVendor.Amd, true)]
    [InlineData("AMD Radeon(TM) Vega 8 Graphics", GpuVendor.Amd, true)]
    [InlineData("AMD Radeon 780M Graphics", GpuVendor.Amd, true)]
    [InlineData("Radeon RX 580 Series", GpuVendor.Amd, false)]
    [InlineData("AMD Radeon RX 6800 XT", GpuVendor.Amd, false)]
    [InlineData("Radeon RX Vega 56", GpuVendor.Amd, false)]
    [InlineData("AMD Radeon Pro W6800", GpuVendor.Amd, false)]
    [InlineData("NVIDIA GeForce RTX 3060", GpuVendor.Nvidia, false)]
    public void IntegratedGraphicsAreRecognisedByName(string name, GpuVendor vendor, bool integrated)
        => Assert.Equal(integrated, GpuChooser.LooksIntegrated(name, vendor));

    [Theory]
    [InlineData(0x10DE, GpuVendor.Nvidia)] [InlineData(0x1002, GpuVendor.Amd)] [InlineData(0x8086, GpuVendor.Intel)]
    [InlineData(0x1414, GpuVendor.Microsoft)] [InlineData(0x1234, GpuVendor.Other)]
    public void PciVendorIds(int id, GpuVendor v) => Assert.Equal(v, GpuChooser.VendorFromPci((uint)id));

    [Theory]
    [InlineData("AMD Radeon RX 580 Series", "Radeon RX 580")]
    [InlineData("Radeon RX 580 Series", "Radeon RX 580")]
    [InlineData("NVIDIA GeForce RTX 3060", "RTX 3060")]
    [InlineData("Intel(R) UHD Graphics 630", "UHD Graphics 630")]
    [InlineData("", "GPU")]
    public void CardTitleShowsTheProductName(string raw, string shown) => Assert.Equal(shown, GpuChooser.ShortName(raw));

    // ---- performance-counter names and sums -------------------------------------------------------------------------------------

    const string Luid1 = "luid_0x00000000_0x0000F3B1";
    const string Luid2 = "luid_0x00000000_0x00010CE2";
    static string Eng(int pid, string luid, int eng, string type, int phys = 0) => $"pid_{pid}_{luid}_phys_{phys}_eng_{eng}_engtype_{type}";

    [Fact]
    public void EngineInstanceNameIsParsed()
    {
        Assert.True(GpuCounterMath.TryParseEngine("pid_1234_luid_0x00000000_0x0000F3B1_phys_0_eng_2_engtype_3D", out var e));
        Assert.Equal(1234, e.Pid); Assert.Equal(0xF3B1, e.Luid); Assert.Equal(0, e.Phys); Assert.Equal(2, e.Eng); Assert.Equal("3D", e.EngType);

        Assert.True(GpuCounterMath.TryParseEngine("pid_4_luid_0x00000001_0x0000AA58_phys_1_eng_10_engtype_High Priority Compute", out e));
        Assert.Equal(0x1_0000AA58L, e.Luid); Assert.Equal(1, e.Phys); Assert.Equal(10, e.Eng); Assert.Equal("High Priority Compute", e.EngType);
        Assert.True(GpuCounterMath.IsGraphicsOrCompute(e.EngType));
    }

    [Theory]
    [InlineData("")] [InlineData("garbage")] [InlineData("pid_x_luid_0x0_0x1_phys_0_eng_0_engtype_3D")]
    [InlineData("pid_12_luid_0x00000000_phys_0_eng_0_engtype_3D")] [InlineData("pid_12_luid_0x00000000_0x0000F3B1_phys_0_eng_0")]
    [InlineData("luid_0x00000000_0x0000F3B1_phys_0")]
    public void MalformedEngineNamesAreRejected(string name) => Assert.False(GpuCounterMath.TryParseEngine(name, out _));

    [Fact]
    public void AdapterMemoryInstanceNameIsParsed()
    {
        Assert.True(GpuCounterMath.TryParseAdapter("luid_0x00000000_0x0000F3B1_phys_0", out long luid, out int phys));
        Assert.Equal(0xF3B1, luid); Assert.Equal(0, phys);
        Assert.False(GpuCounterMath.TryParseAdapter("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D", out _, out _));
        Assert.False(GpuCounterMath.TryParseAdapter("luid_0x00000000_0x0000F3B1", out _, out _));
    }

    [Fact]
    public void OnlyGraphicsAndComputeEnginesCount()
    {
        foreach (var t in new[] { "3D", "High Priority 3D", "Compute_0", "Compute_1", "High Priority Compute", "Cuda", "Graphics_1" })
            Assert.True(GpuCounterMath.IsGraphicsOrCompute(t), t);
        foreach (var t in new[] { "Copy", "VideoDecode", "VideoEncode", "VideoProcessing", "Security", "Overlay", "Optical Flow Accelerator" })
            Assert.False(GpuCounterMath.IsGraphicsOrCompute(t), t);
    }

    [Fact]
    public void ProcessesAreSummedPerEngine_AndTheBusiestEngineIsTheAdapterLoad()
    {
        var samples = new (string, double)[]
        {
            (Eng(100, Luid1, 0, "3D"), 30), (Eng(200, Luid1, 0, "3D"), 25),          // two processes on the same 3D engine: 55
            (Eng(100, Luid1, 1, "Compute_0"), 20),                                    // a separate compute engine: not added to the 3D one
            (Eng(300, Luid1, 2, "Copy"), 90),                                         // copy engine: ignored
            (Eng(300, Luid1, 3, "VideoEncode"), 80),                                  // video: ignored
            (Eng(100, Luid2, 0, "3D"), 7),                                            // the other adapter
        };
        var load = GpuCounterMath.LoadByLuid(samples);
        Assert.Equal(55, load[0xF3B1], 6);
        Assert.Equal(7, load[0x10CE2], 6);
    }

    [Fact]
    public void ComputeBusierThanGraphicsTakesOver_AndTheLoadIsCappedAt100()
    {
        var load = GpuCounterMath.LoadByLuid(new (string, double)[]
        {
            (Eng(1, Luid1, 0, "3D"), 10), (Eng(2, Luid1, 1, "Compute_0"), 70),
            (Eng(1, Luid2, 0, "3D"), 80), (Eng(2, Luid2, 0, "3D"), 60),
        });
        Assert.Equal(70, load[0xF3B1], 6);
        Assert.Equal(100, load[0x10CE2], 6);
    }

    [Fact]
    public void AnAdapterThatOnlyShowsIdleVideoEnginesIsZeroPercentNotUnknown()
    {
        var load = GpuCounterMath.LoadByLuid(new (string, double)[] { (Eng(1, Luid1, 0, "VideoDecode"), 40), ("junk", 5), (Eng(2, Luid1, 0, "3D"), double.NaN) });
        Assert.Equal(0, load[0xF3B1]);
    }

    [Fact]
    public void DedicatedMemoryIsSummedPerAdapter()
    {
        var mem = GpuCounterMath.DedicatedByLuid(new (string, double)[]
        {
            ($"{Luid1}_phys_0", 2_000_000_000), ($"{Luid1}_phys_1", 500_000_000), ($"{Luid2}_phys_0", 100), ("bogus", 1), ($"{Luid2}_phys_0", -4),
        });
        Assert.Equal(2_500_000_000, mem[0xF3B1]);
        Assert.Equal(100, mem[0x10CE2]);
    }

    [Fact]
    public void CountersAreAttributedByLuid_OrBySoleAdapterWhenTheLuidIsUnknown()
    {
        var engines = new[] { (Eng(1, Luid1, 0, "3D"), 41.0), (Eng(1, Luid2, 0, "3D"), 5.0) };
        var memory = new[] { ($"{Luid1}_phys_0", 3.0e9), ($"{Luid2}_phys_0", 1.0e8) };

        var p = GpuCounterMath.ToPartial(engines, memory, A("Radeon RX 580 Series", GpuVendor.Amd, 8192, 0xF3B1), "Windows counters", out string why)!;
        Assert.Equal(41, p.Load); Assert.Equal(3_000_000_000, p.VramUsed); Assert.Equal("ok", why);

        Assert.Null(GpuCounterMath.ToPartial(engines, memory, A("Other", GpuVendor.Other, 1024, 0x999), "x", out why));          // LUID not in the counters
        Assert.Contains("do not list", why);

        Assert.Null(GpuCounterMath.ToPartial(engines, memory, A("Registry-listed", GpuVendor.Amd, 8192, 0), "x", out why));     // no LUID and two adapters: cannot tell
        Assert.Contains("several adapters", why);

        var single = GpuCounterMath.ToPartial(new[] { (Eng(1, Luid1, 0, "3D"), 12.0) }, new[] { ($"{Luid1}_phys_0", 1.0e9) }, A("Registry-listed", GpuVendor.Amd, 8192, 0), "c", out _)!;
        Assert.Equal(12, single.Load); Assert.Equal("c", single.Source);

        Assert.Null(GpuCounterMath.ToPartial(Array.Empty<(string, double)>(), Array.Empty<(string, double)>(), null, "c", out why));
    }

    // ---- LibreHardwareMonitor sensors --------------------------------------------------------------------------------------------

    static GpuSensorSample S(string type, string name, double? v) => new(type, name, v);

    /// <summary>The sensors AmdGpu creates in LibreHardwareMonitorLib 0.9.6 (names verified against the library), with plausible RX 580 values.</summary>
    static List<GpuSensorSample> RadeonSensors() => new()
    {
        S("Temperature", "GPU Core", 62), S("Temperature", "GPU Memory", 58), S("Temperature", "GPU Hot Spot", 71), S("Temperature", "GPU VR VDDC", 66),
        S("Clock", "GPU Core", 1257), S("Clock", "GPU Memory", 2000), S("Voltage", "GPU Core", 1.05),
        S("Load", "GPU Core", 37), S("Load", "GPU Memory", 12),
        S("Fan", "GPU Fan", 1450), S("Control", "GPU Fan", 41),
        S("Power", "GPU Package", 95), S("Power", "GPU PPT", 120),
        S("SmallData", "GPU Memory Used", 2300), S("SmallData", "GPU Memory Free", 5892), S("SmallData", "GPU Memory Total", 8192),
        S("SmallData", "D3D Dedicated Memory Used", 2250), S("SmallData", "D3D Dedicated Memory Total", 8176),
        S("Load", "D3D 3D", 35),
    };

    [Fact]
    public void RadeonSensorsMapToCoreTemperatureLoadMemoryFanPower()
    {
        var p = LhmGpuPicker.ToPartial("LibreHardwareMonitor", RadeonSensors(), A("Radeon RX 580 Series", GpuVendor.Amd, 8192));
        Assert.Equal(62, p.TempC); Assert.Equal("GPU Core", p.TempSensor); Assert.Equal(71, p.HotSpotC);
        Assert.Equal(37, p.Load);
        Assert.Equal(2300 * MB, p.VramUsed); Assert.Equal(8192 * MB, p.VramTotal);     // SmallData is megabytes
        Assert.Equal(41, p.FanPct); Assert.Equal(1450, p.FanRpm);
        Assert.Equal(95, p.PowerW); Assert.Equal(1257, p.ClockMhz);
        Assert.Null(p.Note);
    }

    [Fact]
    public void HotSpotIsTheTemperatureWhenThereIsNoCoreSensor()
    {
        var p = LhmGpuPicker.ToPartial("L", new[] { S("Temperature", "GPU Hot Spot", 74), S("Temperature", "GPU Memory", 60) }, null);
        Assert.Equal(74, p.TempC); Assert.Equal("GPU Hot Spot", p.TempSensor);
    }

    [Fact]
    public void MemoryOrVrmTemperaturesAreNeverTheGpuTemperature()
    {
        var p = LhmGpuPicker.ToPartial("L", new[] { S("Temperature", "GPU Memory", 60), S("Temperature", "GPU VR VDDC", 66) }, null);
        Assert.Null(p.TempC);
        Assert.Contains("read nothing usable", p.Note);
    }

    [Fact]
    public void AZeroOrMissingTemperatureMeansNoSensor()
    {
        var zero = LhmGpuPicker.ToPartial("L", new[] { S("Temperature", "GPU Core", 0), S("Load", "GPU Core", 10) }, null);
        Assert.Null(zero.TempC); Assert.Equal(10, zero.Load);
        var none = LhmGpuPicker.ToPartial("L", new[] { S("Load", "GPU Core", 10) }, null);
        Assert.Null(none.TempC); Assert.Contains("offers no temperature sensor", none.Note);
        var nan = LhmGpuPicker.ToPartial("L", new[] { S("Temperature", "GPU Core", double.NaN), S("Temperature", "GPU Hot Spot", null) }, null);
        Assert.Null(nan.TempC);
    }

    [Fact]
    public void WithoutAGpuCoreLoadTheD3dNodeIsUsed()
    {
        var p = LhmGpuPicker.ToPartial("L", new[] { S("Load", "GPU Core", null), S("Load", "D3D Copy", 80), S("Load", "D3D 3D", 44) }, null);
        Assert.Equal(44, p.Load);
    }

    [Fact]
    public void DedicatedD3dMemoryIsTheFallbackForVram()
    {
        var p = LhmGpuPicker.ToPartial("L", new[] { S("SmallData", "D3D Dedicated Memory Used", 500), S("SmallData", "D3D Dedicated Memory Total", 4096), S("SmallData", "D3D Shared Memory Used", 9000) }, null);
        Assert.Equal(500 * MB, p.VramUsed); Assert.Equal(4096 * MB, p.VramTotal);
    }

    [Fact]
    public void ATotalThatDisagreesWithWindowsByMoreThanTwoIsNotTrusted()
    {
        // pretend the library reported bytes-as-megabytes: 1000x too large
        var bad = LhmGpuPicker.ToPartial("L", new[] { S("SmallData", "GPU Memory Total", 8192.0 * 1000), S("SmallData", "GPU Memory Used", 2300) }, A("Radeon RX 580 Series", GpuVendor.Amd, 8192));
        Assert.Null(bad.VramTotal);
        Assert.Equal(2300 * MB, bad.VramUsed);          // a used figure that fits the card is still fine
        var tooBigUsed = LhmGpuPicker.ToPartial("L", new[] { S("SmallData", "GPU Memory Used", 2300.0 * 1000) }, A("Radeon RX 580 Series", GpuVendor.Amd, 8192));
        Assert.Null(tooBigUsed.VramUsed);
        var fine = LhmGpuPicker.ToPartial("L", new[] { S("SmallData", "GPU Memory Total", 8176) }, A("Radeon RX 580 Series", GpuVendor.Amd, 8192));
        Assert.Equal(8176 * MB, fine.VramTotal);
    }

    [Fact]
    public void TwoFanNvidiaCardUsesTheFirstFan()
    {
        var p = LhmGpuPicker.ToPartial("L", new[] { S("Fan", "GPU Fan 1", 0), S("Fan", "GPU Fan 2", 1800), S("Control", "GPU Fan 1", 35), S("Control", "GPU Fan 2", 35) }, null);
        Assert.Equal(35, p.FanPct); Assert.Equal(1800, p.FanRpm);
    }

    [Fact]
    public void LibreHardwareMonitorGpuIsMatchedByVendorThenName()
    {
        var hw = new[]
        {
            new LhmGpuInfo(GpuVendor.Amd, "AMD Radeon(TM) Graphics"),
            new LhmGpuInfo(GpuVendor.Nvidia, "NVIDIA GeForce RTX 3060"),
            new LhmGpuInfo(GpuVendor.Amd, "AMD Radeon RX 580 Series"),
        };
        Assert.Equal(2, LhmGpuPicker.Match(hw, A("Radeon RX 580 Series", GpuVendor.Amd, 8192)));
        Assert.Equal(0, LhmGpuPicker.Match(hw, A("AMD Radeon(TM) Graphics", GpuVendor.Amd, 512)));
        Assert.Equal(1, LhmGpuPicker.Match(hw, A("NVIDIA GeForce RTX 3060", GpuVendor.Nvidia, 12000)));
        Assert.Equal(-1, LhmGpuPicker.Match(hw, A("Intel(R) Arc(TM) A770 Graphics", GpuVendor.Intel, 16000)));
    }

    // ---- the places that consume the reading ---------------------------------------------------------------------------------------

    static Telemetry Tele(GpuReading? g, DateTime t) => new() { Time = t, Gpu = g, CpuLoad = 30, Mem = new MemoryStats { Total = 16 * 1024 * MB, Available = 8 * 1024 * MB, InUse = 8 * 1024 * MB } };

    [Fact]
    public void AfterGameSummaryWorksForAGpuWithoutATemperatureSensor()
    {
        var clock = new DateTime(2026, 10, 23, 20, 0, 0);
        var rec = new SessionRecorder { Clock = () => clock };
        rec.Begin(new GameInfo { Name = "Some Game", ExeName = "game.exe" });
        for (int i = 0; i < 40; i++)
        {
            clock = clock.AddSeconds(5);
            rec.Add(Tele(new GpuReading { Name = "Radeon RX 580 Series", TempC = null, Load = 60 + i % 30, VramUsed = (2000 + i * 10) * MB, VramTotal = 8192 * MB }, clock));
        }
        var e = rec.End(new PingMonitor(), 0)!;
        Assert.NotNull(e);
        Assert.Null(e.GpuTempPeak); Assert.Null(e.GpuTempAvg);
        Assert.Equal(89, e.GpuLoadPeak);
        Assert.True(e.GpuLoadAvg > 60);
        Assert.True(e.VramPeakGb > 2);
        Assert.False(e.GpuThrottled);
        Assert.Contains("GPU peak load 89%", SystemMonitor.SessionBrief(e));
    }

    [Fact]
    public void AfterGameSummaryStillShowsTemperatureOnNvidia()
    {
        var clock = new DateTime(2026, 10, 23, 20, 0, 0);
        var rec = new SessionRecorder { Clock = () => clock };
        rec.Begin(new GameInfo { Name = "Some Game", ExeName = "game.exe" });
        for (int i = 0; i < 40; i++)
        {
            clock = clock.AddSeconds(5);
            rec.Add(Tele(new GpuReading { Name = "RTX", TempC = 60 + i % 10, Load = 90, VramUsed = 4000 * MB, VramTotal = 12000 * MB }, clock));
        }
        var e = rec.End(new PingMonitor(), 0)!;
        Assert.Equal(69, e.GpuTempPeak); Assert.Equal(64.5, e.GpuTempAvg);
        Assert.Contains("GPU peak 69 °C", SystemMonitor.SessionBrief(e));
    }

    [Fact]
    public void SessionsSavedBeforeThisVersionStillLoad()
    {
        var json = "{\"Game\":\"Old\",\"GpuTempPeak\":71.0,\"GpuTempAvg\":66.5,\"GpuLoadPeak\":99}";
        var e = System.Text.Json.JsonSerializer.Deserialize<SessionEntry>(json)!;
        Assert.Equal(71, e.GpuTempPeak);
        var none = System.Text.Json.JsonSerializer.Deserialize<SessionEntry>("{\"Game\":\"X\",\"GpuTempPeak\":null}")!;
        Assert.Null(none.GpuTempPeak);
    }

    [Fact]
    public void AGpuWithoutATemperatureNeverRaisesTheHotAlert()
    {
        var s = new AppSettings();
        var now = new DateTime(2026, 10, 23, 20, 0, 0);
        var engine = new AlertEngine(s) { Clock = () => now };
        var raised = new List<Alert>();
        engine.Raised += raised.Add;
        for (int i = 0; i < 10; i++)
        {
            engine.EvaluateFast(new Telemetry { Gpu = new GpuReading { TempC = null, Load = 100 }, Time = now });
            now = now.AddSeconds(5);
        }
        Assert.Empty(raised);
        Assert.DoesNotContain(engine.Active, a => a.Key == "gpu-hot");
    }

    [Fact]
    public void AGpuLoadFromCountersFeedsTheGameModePressureCheck()
    {
        // GameMode builds its PressureSample from Telemetry.Gpu?.Load: a counters-only AMD reading at 98 % is GPU pressure
        var t = Tele(new GpuReading { Name = "Radeon RX 580 Series", TempC = null, Load = 98 }, DateTime.Now);
        var sample = new PressureSample(40, 40, t.Gpu?.Load ?? 0, 12, 40, 5);
        Assert.True(PressureWatcher.Classify(new AppSettings(), sample).HasFlag(PressureKind.Gpu));
        var unknown = Tele(new GpuReading { Name = "x", Load = null }, DateTime.Now);
        Assert.Equal(0, unknown.Gpu?.Load ?? 0);
    }
}

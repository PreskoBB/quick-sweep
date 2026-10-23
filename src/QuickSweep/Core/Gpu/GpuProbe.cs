using System.Text;
using QuickSweep.Native;

namespace QuickSweep.Core;

/// <summary>
/// <c>QuickSweep.exe --gpu-probe=file.txt</c>: asks every GPU source on its own, whether or not the normal chain would, and writes
/// what each one saw. For a PC whose GPU shows "no sensor" or wrong numbers: run it from a normal (non-elevated) prompt and send
/// the file. It changes nothing and does not talk to a running Quick Sweep.
/// </summary>
public static class GpuProbe
{
    static string P(GpuPartial? p)
        => p == null ? "no reading"
            : $"temp={Fmt(p.TempC)} hotspot={Fmt(p.HotSpotC)} load={Fmt(p.Load)} vram={p.VramUsed / 1048576}/{p.VramTotal / 1048576} MB fan%={p.FanPct} fan_rpm={p.FanRpm} power={Fmt(p.PowerW)} W clock={p.ClockMhz} MHz tempSensor={p.TempSensor} note={p.Note}";

    static string Fmt(double? v) => v is double d ? d.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "-";

    public static string Run()
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);
        L($"Quick Sweep {typeof(GpuProbe).Assembly.GetName().Version?.ToString(3)} GPU probe, {DateTime.Now:yyyy-MM-dd HH:mm:ss}, elevated: {Elevation.IsTokenElevated}");
        L();

        var adapters = new WindowsGpuLister().List();
        var chosen = GpuChooser.Pick(adapters);
        L("Adapters (DXGI, registry when DXGI fails):");
        foreach (var a in adapters) L("  " + a + (ReferenceEquals(a, chosen) ? "   <== shown" : ""));
        if (adapters.Count == 0) L("  none");
        L();

        var nv = new NvidiaGpuSource();
        L($"NVML: {nv.Status}");
        if (nv.Applies(chosen) || Nvml.Available) L("  " + P(nv.Read(chosen)));
        L();

        using var lhm = new LhmGpuSource();
        L("LibreHardwareMonitor (opened here even if the normal chain would not need it):");
        try { foreach (var line in lhm.ListAll()) L("  " + line); }
        catch (Exception e) { L("  failed: " + e.Message); }
        if (chosen != null)
        {
            var p = lhm.Read(chosen);
            L($"  picked for '{chosen.Name}': {P(p)}");
            L($"  status: {lhm.Status}");
        }
        L();

        using var counters = new CounterGpuSource();
        L("Windows GPU counters (two samples, one second apart):");
        counters.Read(chosen);
        Thread.Sleep(1000);
        var c = counters.Read(chosen);
        L($"  {P(c)}");
        L($"  status: {counters.Status}");
        var raw = new GpuCounterReader();
        try
        {
            var eng = new List<(string, double)>(); var mem = new List<(string, double)>();
            raw.TrySample(eng, mem);
            Thread.Sleep(1000);
            eng.Clear(); mem.Clear();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool got = raw.TrySample(eng, mem);
            clock.Stop();
            if (got)
            {
                L($"  one sample took {clock.Elapsed.TotalMilliseconds:0} ms");
                foreach (var kv in GpuCounterMath.LoadByLuid(eng)) L($"  luid 0x{kv.Key:X}: engines busiest {kv.Value:0.#} %");
                foreach (var kv in GpuCounterMath.DedicatedByLuid(mem)) L($"  luid 0x{kv.Key:X}: dedicated memory in use {kv.Value / 1048576} MB");
                L($"  {eng.Count} engine instances, {mem.Count} adapter-memory instances");
            }
            else L("  raw counters: " + raw.Status);
        }
        finally { raw.Dispose(); }
        L();

        using var mon = GpuMonitor.CreateDefault();
        mon.Read();
        Thread.Sleep(1000);
        var r = mon.Read();
        L("What Quick Sweep itself would show:");
        L("  " + mon.Describe());
        if (r != null) L($"  name='{r.Name}' vendor={r.Vendor} temp={Fmt(r.TempC)} load={Fmt(r.Load)} vram={r.VramUsed / 1048576}/{r.VramTotal / 1048576} MB fan%={r.FanPct} fan_rpm={r.FanRpm}");
        else L("  no GPU found");
        return sb.ToString();
    }
}

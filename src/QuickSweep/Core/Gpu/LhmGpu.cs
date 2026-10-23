using LibreHardwareMonitor.Hardware;
using QuickSweep.Native;

namespace QuickSweep.Core;

/// <summary>One LibreHardwareMonitor sensor, reduced to what the picker needs (type is the <c>SensorType</c> name: "Temperature", "Load", "SmallData", "Fan", "Control"...).</summary>
public readonly record struct GpuSensorSample(string Type, string Name, double? Value);

public readonly record struct LhmGpuInfo(GpuVendor Vendor, string Name);

/// <summary>
/// Which LibreHardwareMonitor GPU belongs to the adapter Windows listed, and which of its sensors to use.
/// Sensor names are the ones LibreHardwareMonitorLib 0.9.6 creates (AmdGpu, NvidiaGpu, IntelGpu, IntelIntegratedGpu):
/// Temperature "GPU Core" / "GPU Hot Spot", Load "GPU Core" or "D3D 3D", SmallData (megabytes) "GPU Memory Used" / "GPU Memory Total"
/// or "D3D Dedicated Memory Used" / "...Total", Control "GPU Fan" (percent), Fan "GPU Fan" (rpm).
/// </summary>
public static class LhmGpuPicker
{
    const double MiB = 1048576.0;

    static readonly string[] Noise = { "amd", "nvidia", "intel", "geforce", "graphics", "series", "(tm)", "(r)", "corporation", "corp" };

    static HashSet<string> Tokens(string name)
    {
        string s = name.ToLowerInvariant();
        foreach (var n in Noise) s = s.Replace(n, " ");
        return s.Split(new[] { ' ', '-', '_', ',', '(', ')' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    }

    /// <summary>Index into <paramref name="hardware"/> of the GPU for <paramref name="adapter"/>, or -1. Same vendor first, then the best name overlap.</summary>
    public static int Match(IReadOnlyList<LhmGpuInfo> hardware, GpuAdapter adapter)
    {
        var candidates = Enumerable.Range(0, hardware.Count).Where(i => hardware[i].Vendor == adapter.Vendor).ToList();
        if (candidates.Count == 0) return -1;
        if (candidates.Count == 1) return candidates[0];
        var want = Tokens(adapter.Name);
        int best = candidates[0]; double bestScore = -1;
        foreach (var i in candidates)
        {
            var have = Tokens(hardware[i].Name);
            double score = (double)have.Intersect(want).Count() / Math.Max(1, Math.Max(have.Count, want.Count));
            if (score > bestScore) { bestScore = score; best = i; }
        }
        return best;
    }

    static double? Num(GpuSensorSample s) => s.Value is double v && double.IsFinite(v) ? v : null;

    readonly record struct Hit(string Name, double Value);

    static Hit? Find(IReadOnlyList<GpuSensorSample> all, string type, string name, Func<double, bool> ok)
    {
        foreach (var s in all)
            if (s.Type == type && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) && Num(s) is double v && ok(v)) return new Hit(s.Name, v);
        return null;
    }

    /// <summary>"GPU Fan" on AMD, "GPU Fan 1" / "GPU Fan 2" on NVIDIA cards with two fans: the first one that reads something.</summary>
    static Hit? FindPrefix(IReadOnlyList<GpuSensorSample> all, string type, string prefix, Func<double, bool> ok)
    {
        foreach (var s in all)
            if (s.Type == type && s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Num(s) is double v && ok(v)) return new Hit(s.Name, v);
        return null;
    }

    static Hit? FindAny(IReadOnlyList<GpuSensorSample> all, string type, string[] names, Func<double, bool> ok)
    {
        foreach (var n in names) if (Find(all, type, n, ok) is { } h) return h;
        return null;
    }

    public static GpuPartial ToPartial(string label, IEnumerable<GpuSensorSample> sensors, GpuAdapter? adapter)
    {
        var all = sensors.ToList();
        var p = new GpuPartial { Source = label };

        // A driver call it does not support often leaves a sensor at 0 instead of removing it: 0 degrees is "no reading".
        static bool Temp(double v) => v > 0 && v < 150;
        if (FindAny(all, "Temperature", new[] { "GPU Core", "GPU Hot Spot" }, Temp) is { } t) { p.TempC = t.Value; p.TempSensor = t.Name; }
        if (Find(all, "Temperature", "GPU Hot Spot", Temp) is { } hs) p.HotSpotC = hs.Value;
        if (p.TempC == null)
            p.Note = all.Any(s => s.Type == "Temperature")
                ? "LibreHardwareMonitor found this GPU but its temperature sensors read nothing usable."
                : "LibreHardwareMonitor found this GPU but the driver offers no temperature sensor.";

        if (FindAny(all, "Load", new[] { "GPU Core", "D3D 3D" }, v => v is >= 0 and <= 100) is { } l) p.Load = l.Value;

        // SmallData = megabytes. A total that disagrees with what Windows says the card has (by more than 2x) means the unit
        // or the sensor is not what we think: use none of the LibreHardwareMonitor memory figures then.
        if (FindAny(all, "SmallData", new[] { "GPU Memory Total", "D3D Dedicated Memory Total" }, v => v > 0) is { } tot)
        {
            long total = (long)(tot.Value * MiB);
            bool plausible = adapter is not { DedicatedBytes: > 0 } || (total >= adapter.DedicatedBytes / 2 && total <= adapter.DedicatedBytes * 2);
            if (plausible) p.VramTotal = total;
        }
        long? known = p.VramTotal ?? (adapter is { DedicatedBytes: > 0 } ? adapter.DedicatedBytes : null);
        if (FindAny(all, "SmallData", new[] { "GPU Memory Used", "D3D Dedicated Memory Used" }, v => v >= 0) is { } used)
        {
            long u = (long)(used.Value * MiB);
            if (known == null || u <= known * 1.1) p.VramUsed = u;
        }

        if (FindPrefix(all, "Control", "GPU Fan", v => v is >= 0 and <= 100) is { } fp) p.FanPct = (int)Math.Round(fp.Value);
        if (FindPrefix(all, "Fan", "GPU Fan", v => v is > 0 and < 30000) is { } fr) p.FanRpm = (int)Math.Round(fr.Value);
        if (FindAny(all, "Power", new[] { "GPU Package", "GPU Total", "GPU PPT", "GPU Core" }, v => v > 0 && v < 2000) is { } pw) p.PowerW = pw.Value;
        if (Find(all, "Clock", "GPU Core", v => v > 0) is { } ck) p.ClockMhz = (int)Math.Round(ck.Value);
        return p;
    }
}

/// <summary>
/// Source (b): LibreHardwareMonitorLib's GPU support, the same library that reads the CPU. AMD goes through the display driver's
/// ADL library (atiadlxx.dll, user mode: no admin, no PawnIO), Intel Arc through Intel's control library, and the D3D sensors
/// work for any vendor. It is opened only when the chosen GPU is not an NVIDIA one that NVML already covers.
/// </summary>
public sealed class LhmGpuSource : IGpuSource, IDisposable
{
    Computer? _pc;
    List<IHardware> _gpus = new();
    IHardware? _match;
    string _matchKey = "";
    bool _opened;
    public string Label => "LibreHardwareMonitor";
    public bool Authoritative => false;
    public string Status { get; private set; } = "not used on this PC";
    /// <summary>Every GPU sensor LibreHardwareMonitor listed when it first read the matched GPU (for the log and --gpu-probe).</summary>
    public string SensorDump { get; private set; } = "";

    public bool Applies(GpuAdapter? adapter) => adapter != null && adapter.Vendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel;

    void Open()
    {
        if (_opened) return;
        _opened = true;
        try
        {
            _pc = new Computer { IsGpuEnabled = true };
            _pc.Open();
            _gpus = _pc.Hardware.Where(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia or HardwareType.GpuIntel).ToList();
            Status = _gpus.Count == 0 ? "opened, but lists no GPU" : "ok";
        }
        catch (Exception e) { Status = "failed to open: " + e.Message; Log.Write("gpu: LibreHardwareMonitor open failed: " + e); _gpus = new(); }
    }

    static GpuVendor VendorOf(IHardware h) => h.HardwareType switch
    {
        HardwareType.GpuAmd => GpuVendor.Amd,
        HardwareType.GpuNvidia => GpuVendor.Nvidia,
        HardwareType.GpuIntel => GpuVendor.Intel,
        _ => GpuVendor.Other
    };

    static List<GpuSensorSample> Samples(IHardware h)
        => h.Sensors.Select(s => new GpuSensorSample(s.SensorType.ToString(), s.Name, s.Value is float f ? f : null)).ToList();

    public GpuPartial? Read(GpuAdapter? adapter)
    {
        if (adapter == null) return null;
        try
        {
            Open();
            if (_gpus.Count == 0) return new GpuPartial { Source = Label, Note = "LibreHardwareMonitor lists no GPU on this PC." };
            string key = adapter.Name + "|" + adapter.Luid;
            if (_match == null || _matchKey != key)
            {
                int i = LhmGpuPicker.Match(_gpus.Select(h => new LhmGpuInfo(VendorOf(h), h.Name)).ToList(), adapter);
                _matchKey = key;
                _match = i < 0 ? null : _gpus[i];
                if (_match != null) { _match.Update(); SensorDump = Describe(_match); Log.Write("gpu: LibreHardwareMonitor matched '" + _match.Name + "' to '" + adapter.Name + "': " + SensorDump); }
                else Log.Write("gpu: LibreHardwareMonitor has no " + adapter.Vendor + " GPU for '" + adapter.Name + "' (it lists: " + string.Join("; ", _gpus.Select(g => g.HardwareType + " " + g.Name)) + ")");
            }
            if (_match == null)
            {
                Status = "no matching hardware";
                return new GpuPartial { Source = Label, Note = "LibreHardwareMonitor does not list this GPU." };
            }
            _match.Update();
            Status = "ok";
            return LhmGpuPicker.ToPartial(Label, Samples(_match), adapter);
        }
        catch (Exception e)
        {
            Status = "failed: " + e.Message;
            return null;
        }
    }

    public static string Describe(IHardware h)
        => h.HardwareType + " '" + h.Name + "': " + string.Join(", ", h.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Name)
            .Select(s => $"{s.SensorType} {s.Name}={(s.Value is float f ? f.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "null")}"));

    /// <summary>All GPUs LibreHardwareMonitor sees, for --gpu-probe (opens it even if the chain would not).</summary>
    public IReadOnlyList<string> ListAll()
    {
        Open();
        foreach (var g in _gpus) g.Update();
        return _gpus.Select(Describe).ToList();
    }

    public void Dispose() { try { _pc?.Close(); } catch { } }
}

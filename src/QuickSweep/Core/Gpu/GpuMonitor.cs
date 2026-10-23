using QuickSweep.Native;

namespace QuickSweep.Core;

/// <summary>
/// The one place that answers "what is the GPU doing". It lists the display adapters, picks the one to show, then asks the
/// sources in order: (a) NVIDIA through NVML, (b) LibreHardwareMonitor for AMD / Intel (and NVIDIA when NVML is missing),
/// (c) the Windows GPU counters for any vendor. The partial readings are merged field by field, so a GPU without a temperature
/// sensor still shows usage and VRAM. The flyout, tray icon, alerts, Game Mode and the after-game summary all read the result.
/// </summary>
public sealed class GpuMonitor : IDisposable
{
    static readonly TimeSpan RetryWhenEmpty = TimeSpan.FromSeconds(60), Relist = TimeSpan.FromMinutes(10);

    readonly IGpuAdapterLister _lister;
    readonly IReadOnlyList<IGpuSource> _sources;
    readonly Func<DateTime> _now;
    IReadOnlyList<GpuAdapter> _adapters = Array.Empty<GpuAdapter>();
    GpuAdapter? _chosen;
    DateTime _listed;
    bool _listedOnce;
    string _signature = "";

    public GpuMonitor(IGpuAdapterLister lister, IReadOnlyList<IGpuSource> sources, Func<DateTime>? clock = null)
    {
        _lister = lister; _sources = sources; _now = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>The sources of a real PC, in the order of the chain.</summary>
    public static GpuMonitor CreateDefault()
        => new(new WindowsGpuLister(), new IGpuSource[] { new NvidiaGpuSource(), new LhmGpuSource(), new CounterGpuSource() });

    public IReadOnlyList<GpuAdapter> Adapters => _adapters;
    public GpuAdapter? Chosen => _chosen;
    public GpuReading? Last { get; private set; }
    public IReadOnlyList<IGpuSource> Sources => _sources;

    void EnsureAdapters()
    {
        var now = _now();
        bool due = !_listedOnce || (_adapters.Count == 0 && now - _listed > RetryWhenEmpty) || now - _listed > Relist;
        if (!due) return;
        _listedOnce = true; _listed = now;
        try { _adapters = _lister.List(); }
        catch (Exception e) { Log.Write("gpu: adapter listing failed: " + e.Message); _adapters = Array.Empty<GpuAdapter>(); }
        _chosen = GpuChooser.Pick(_adapters);
    }

    /// <summary>One reading, or null when there is no GPU at all (no adapter listed and no source that answers).</summary>
    public GpuReading? Read()
    {
        EnsureAdapters();
        var parts = new List<GpuPartial>();
        foreach (var src in _sources)
        {
            if (!src.Applies(_chosen)) continue;
            GpuPartial? p;
            try { p = src.Read(_chosen); }
            catch (Exception e) { Log.Write($"gpu: {src.Label} threw: {e.Message}"); p = null; }
            if (p == null) continue;
            parts.Add(p);
            // the NVIDIA path is complete: nothing else is opened or sampled, so this PC reads exactly what it always did
            if (src.Authoritative && p.TempC != null && p.Load != null && p.VramTotal is > 0) break;
        }
        if (_chosen == null && !parts.Any(p => p.HasAnything)) { Last = null; return null; }
        var r = GpuMerge.Merge(parts, _chosen);
        Last = r;
        string sig = $"{r.Name}|{r.TempSource}|{r.LoadSource}|{r.VramSource}";
        if (sig != _signature) { _signature = sig; Log.Write("gpu: " + Describe()); }
        return r;
    }

    /// <summary>Several lines for the log and --gpu-probe: every adapter, the one chosen, the state of each source and what the last reading used.</summary>
    public string Describe()
    {
        var lines = new List<string>
        {
            "adapters: " + (_adapters.Count == 0 ? "none listed" : string.Join(" | ", _adapters.Select(a => a.ToString()))),
            "showing: " + (_chosen?.Name ?? "(none)")
        };
        lines.Add("sources: " + string.Join("; ", _sources.Select(s => $"{s.Label} {s.Status}")));
        if (Last is { } r)
            lines.Add($"temperature: {(r.TempC is double t ? $"{t:0} C via {r.TempSource} ({r.TempSensor})" : "none (" + r.Note + ")")}; load: {(r.Load is double l ? $"{l:0.#}% via {r.LoadSource}" : "none")}; vram: {r.VramUsed / 1048576} of {r.VramTotal / 1048576} MB via {r.VramSource}");
        return string.Join("\n  ", lines);
    }

    /// <summary>The GPU lines of Settings > status, in plain words.</summary>
    public string StatusText()
    {
        if (Last is not { } r) return _chosen == null ? "GPU: none found" : "GPU: " + _chosen.Name + " (no reading yet)";
        string mem = r.VramTotal > 0 ? $"{r.VramTotal / 1073741824.0:0.#} GB" : "memory size unknown";
        var lines = new List<string>
        {
            $"GPU: {r.Name}" + (r.Vendor.Length > 0 ? $" ({r.Vendor}, {mem})" : $" ({mem})"),
            "GPU temperature: " + (r.TempC != null ? $"{r.TempSource}" + (r.TempSensor.Length > 0 ? $" ({r.TempSensor})" : "") : "no sensor available"),
            "GPU usage: " + (r.Load != null ? r.LoadSource : "not available"),
            "GPU memory: " + (r.VramSource.Length > 0 ? r.VramSource : "not available"),
        };
        return string.Join("\n", lines);
    }

    public void Dispose()
    {
        foreach (var s in _sources) if (s is IDisposable d) { try { d.Dispose(); } catch { } }
    }
}

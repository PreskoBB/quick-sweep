using System.Globalization;

namespace QuickSweep.Core;

/// <summary>One "\GPU Engine(*)" instance: <c>pid_1234_luid_0x00000000_0x0000F3B1_phys_0_eng_0_engtype_3D</c>.</summary>
public readonly record struct GpuEngineInstance(int Pid, long Luid, int Phys, int Eng, string EngType);

/// <summary>
/// The arithmetic behind the vendor-neutral GPU readings, kept free of PDH so it can be tested with plain strings.
/// Windows reports the same numbers Task Manager shows: utilisation per (process, engine) and dedicated memory per adapter,
/// both keyed by the adapter's LUID (the same LUID DXGI gives each adapter).
/// </summary>
public static class GpuCounterMath
{
    /// <summary>"0x00000000" + "0x0000F3B1" -> 0xF3B1 (high part in the upper 32 bits, like DXGI's LUID).</summary>
    public static long CombineLuid(uint high, uint low) => ((long)high << 32) | low;

    static bool TryHex(ReadOnlySpan<char> s, out uint v)
        => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(s[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out v)
            : uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out v);

    /// <summary>Reads "luid_0xHIGH_0xLOW" starting at <paramref name="at"/> (the index of "luid_"). Returns the index after the LOW part.</summary>
    static bool TryLuid(string s, int at, out long luid, out int end)
    {
        luid = 0; end = at;
        var parts = s.AsSpan(at + 5);
        int u1 = parts.IndexOf('_');
        if (u1 <= 0 || !TryHex(parts[..u1], out uint high)) return false;
        var rest = parts[(u1 + 1)..];
        int u2 = rest.IndexOf('_');
        var lowSpan = u2 < 0 ? rest : rest[..u2];
        if (!TryHex(lowSpan, out uint low)) return false;
        luid = CombineLuid(high, low);
        end = at + 5 + u1 + 1 + lowSpan.Length;
        return true;
    }

    public static bool TryParseEngine(string? instance, out GpuEngineInstance e)
    {
        e = default;
        if (string.IsNullOrEmpty(instance) || !instance.StartsWith("pid_", StringComparison.Ordinal)) return false;
        int pidEnd = instance.IndexOf('_', 4);
        if (pidEnd <= 4 || !int.TryParse(instance.AsSpan(4, pidEnd - 4), NumberStyles.None, CultureInfo.InvariantCulture, out int pid)) return false;
        int li = instance.IndexOf("_luid_", pidEnd - 1, StringComparison.Ordinal);
        if (li < 0 || !TryLuid(instance, li + 1, out long luid, out int afterLuid)) return false;
        int phys = IntAfter(instance, "_phys_", afterLuid, out _);
        int eng = IntAfter(instance, "_eng_", afterLuid, out _);
        int et = instance.IndexOf("_engtype_", afterLuid, StringComparison.Ordinal);
        if (phys < 0 || eng < 0 || et < 0) return false;
        string type = instance[(et + 9)..].Trim();
        if (type.Length == 0) return false;
        e = new GpuEngineInstance(pid, luid, phys, eng, type);
        return true;
    }

    /// <summary>"luid_0x00000000_0x0000F3B1_phys_0" (the \GPU Adapter Memory instance names).</summary>
    public static bool TryParseAdapter(string? instance, out long luid, out int phys)
    {
        luid = 0; phys = 0;
        if (string.IsNullOrEmpty(instance) || !instance.StartsWith("luid_", StringComparison.Ordinal)) return false;
        if (!TryLuid(instance, 0, out luid, out int after)) return false;
        phys = IntAfter(instance, "_phys_", after, out _);
        return phys >= 0;
    }

    static int IntAfter(string s, string key, int from, out int end)
    {
        end = from;
        int i = s.IndexOf(key, from, StringComparison.Ordinal);
        if (i < 0) return -1;
        int start = i + key.Length, j = start;
        while (j < s.Length && char.IsAsciiDigit(s[j])) j++;
        end = j;
        return j > start && int.TryParse(s.AsSpan(start, j - start), NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : -1;
    }

    /// <summary>
    /// Engines that do the graphics and compute work a game or a render loads: 3D (also "High Priority 3D"), Compute_n, Cuda,
    /// Graphics_n. Copy, video decode / encode / processing, overlay and security engines are left out, as Task Manager's main
    /// GPU figure does.
    /// </summary>
    public static bool IsGraphicsOrCompute(string engType)
        => engType.Contains("3D", StringComparison.OrdinalIgnoreCase)
           || engType.Contains("Compute", StringComparison.OrdinalIgnoreCase)
           || engType.Contains("Cuda", StringComparison.OrdinalIgnoreCase)
           || engType.StartsWith("Graphics", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Percent busy per adapter. Per engine (adapter, physical adapter, engine number, type) the processes are added up;
    /// the adapter's figure is the busiest 3D / compute engine, capped at 100. Adding the separate engines together
    /// would double-count a GPU that runs a 3D engine and a compute engine at the same time.
    /// </summary>
    public static Dictionary<long, double> LoadByLuid(IEnumerable<(string instance, double value)> samples)
    {
        var perEngine = new Dictionary<(long luid, int phys, int eng, string type), double>();
        var seen = new HashSet<long>();
        foreach (var (name, value) in samples)
        {
            if (!TryParseEngine(name, out var e) || double.IsNaN(value) || value < 0) continue;
            seen.Add(e.Luid);
            if (!IsGraphicsOrCompute(e.EngType)) continue;
            var key = (e.Luid, e.Phys, e.Eng, e.EngType);
            perEngine[key] = perEngine.GetValueOrDefault(key) + value;
        }
        var result = new Dictionary<long, double>();
        foreach (var l in seen) result[l] = 0;                  // an adapter with engines but nothing running is 0 %, not unknown
        foreach (var (k, v) in perEngine) result[k.luid] = Math.Min(100, Math.Max(result[k.luid], v));
        return result;
    }

    /// <summary>Dedicated video memory in use per adapter (bytes), summed over its physical adapters.</summary>
    public static Dictionary<long, long> DedicatedByLuid(IEnumerable<(string instance, double value)> samples)
    {
        var result = new Dictionary<long, long>();
        foreach (var (name, value) in samples)
        {
            if (!TryParseAdapter(name, out long luid, out _) || double.IsNaN(value) || value < 0) continue;
            result[luid] = result.GetValueOrDefault(luid) + (long)value;
        }
        return result;
    }

    /// <summary>
    /// Which LUID in the counters belongs to the chosen adapter: its own LUID when it has one and the counters show it; with
    /// no LUID (registry fallback) only when the counters show exactly one adapter, otherwise the figures cannot be attributed.
    /// </summary>
    public static long? ResolveLuid(long adapterLuid, IReadOnlyCollection<long> luidsInCounters)
    {
        if (adapterLuid != 0) return luidsInCounters.Contains(adapterLuid) ? adapterLuid : null;
        return luidsInCounters.Count == 1 ? luidsInCounters.First() : null;
    }

    /// <summary>The counters of one sample turned into a reading for <paramref name="adapter"/> (null when they cannot be attributed to it).</summary>
    public static GpuPartial? ToPartial(IReadOnlyList<(string instance, double value)> engines, IReadOnlyList<(string instance, double value)> memory,
        GpuAdapter? adapter, string label, out string status)
    {
        var load = LoadByLuid(engines);
        var dedicated = DedicatedByLuid(memory);
        var luids = load.Keys.Union(dedicated.Keys).ToList();
        if (luids.Count == 0) { status = "no GPU counters reported"; return null; }
        if (ResolveLuid(adapter?.Luid ?? 0, luids) is not long luid)
        {
            status = adapter is { HasLuid: true } ? "counters do not list this adapter" : "several adapters in the counters and no way to tell which is this one";
            return null;
        }
        var p = new GpuPartial { Source = label };
        if (load.TryGetValue(luid, out double l)) p.Load = Math.Round(l, 1);
        if (dedicated.TryGetValue(luid, out long used)) p.VramUsed = used;
        status = "ok";
        return p.Load != null || p.VramUsed != null ? p : null;
    }
}

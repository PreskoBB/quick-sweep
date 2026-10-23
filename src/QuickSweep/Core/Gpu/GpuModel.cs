using QuickSweep.Native;

namespace QuickSweep.Core;

public enum GpuVendor { Other, Nvidia, Amd, Intel, Microsoft }

/// <summary>One display adapter as Windows lists it (DXGI, or the registry class key when DXGI fails).</summary>
public sealed record GpuAdapter(
    string Name, GpuVendor Vendor, long Luid, long DedicatedBytes, long SharedBytes, bool Software, bool Integrated)
{
    /// <summary>Luid 0 = not known (registry fallback): performance counters can then only be attributed when there is exactly one adapter in them.</summary>
    public bool HasLuid => Luid != 0;
    public override string ToString()
        => $"{Name} [{Vendor}{(Integrated ? ", integrated" : "")}{(Software ? ", software" : "")}] {DedicatedBytes / 1048576.0:0} MB dedicated, luid 0x{Luid:X}";
}

/// <summary>
/// What one source could read. Every field is null when the source does not have it; the monitor merges the partials of
/// the sources in order (first value wins) so a missing temperature never hides a usable load or VRAM reading.
/// </summary>
public sealed class GpuPartial
{
    public string Source = "";
    public string? Name;
    public double? TempC, HotSpotC, Load, PowerW;
    public long? VramUsed, VramTotal;
    public int? FanPct, FanRpm, ClockMhz;
    public bool? ThermalThrottle;
    public ulong? ThrottleReasons;
    public string? TempSensor;
    /// <summary>Plain-words reason for something this source could not give.</summary>
    public string? Note;

    public bool HasAnything => TempC != null || Load != null || VramUsed != null || VramTotal != null || FanPct != null || FanRpm != null || Name != null;
}

public interface IGpuSource
{
    /// <summary>Short label used in the status text ("NVML", "LibreHardwareMonitor", "Windows counters").</summary>
    string Label { get; }
    /// <summary>
    /// True for the NVIDIA driver path: when it returns temperature, load and memory the chain stops there and nothing else is
    /// opened or sampled, so an NVIDIA PC reads exactly what it always did.
    /// </summary>
    bool Authoritative { get; }
    /// <summary>Cheap check, no I/O: can this source say anything about <paramref name="adapter"/> (null = Windows listed no adapter)?</summary>
    bool Applies(GpuAdapter? adapter);
    /// <summary>One reading, or null. May open its resources on first use. Never throws.</summary>
    GpuPartial? Read(GpuAdapter? adapter);
    /// <summary>Plain-words state for Settings and the log ("ok", "not available", "no matching hardware"...).</summary>
    string Status { get; }
}

public interface IGpuAdapterLister { IReadOnlyList<GpuAdapter> List(); }

public static class GpuChooser
{
    public static GpuVendor VendorFromPci(uint id) => id switch
    {
        0x10DE => GpuVendor.Nvidia,
        0x1002 or 0x1022 => GpuVendor.Amd,
        0x8086 => GpuVendor.Intel,
        0x1414 => GpuVendor.Microsoft,
        _ => GpuVendor.Other
    };

    /// <summary>
    /// Integrated graphics by name, because DXGI has no "integrated" flag. Intel Arc cards (A380, B580...) and AMD Radeon
    /// RX / Pro cards are discrete; "Intel(R) Arc(TM) Graphics" (Meteor Lake and later), UHD / HD / Iris and plain
    /// "Radeon(TM) Graphics" or Vega-without-RX parts are the ones that share system memory.
    /// </summary>
    public static bool LooksIntegrated(string name, GpuVendor vendor)
    {
        string n = name.ToLowerInvariant();
        switch (vendor)
        {
            case GpuVendor.Intel:
                if (System.Text.RegularExpressions.Regex.IsMatch(n, @"\barc\b.*\b[ab]\d{3}\b")) return false;   // Arc A770, Arc Pro A60, Arc B580
                return true;                                                                                       // UHD, HD, Iris, Xe, Arc Graphics
            case GpuVendor.Amd:
                if (System.Text.RegularExpressions.Regex.IsMatch(n, @"\b(rx|pro|r[579]|hd|fury|firepro|instinct|w\d{4})\b")) return false;
                if (n.Contains("radeon graphics") || n.Contains("radeon(tm) graphics") || n.Contains("vega") || n.Contains("radeon 8") || n.Contains("radeon 7") || n.Contains("radeon 6")) return true;
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// The adapter to show: real hardware before the Microsoft stand-ins (remote display, basic render), discrete before
    /// integrated, then the most dedicated video memory; ties keep Windows' own order. Software adapters (WARP) never count.
    /// </summary>
    public static GpuAdapter? Pick(IReadOnlyList<GpuAdapter> adapters)
        => adapters.Select((a, i) => (a, i)).Where(x => !x.a.Software)
            .OrderBy(x => x.a.Vendor == GpuVendor.Microsoft ? 1 : 0)
            .ThenBy(x => x.a.Integrated ? 1 : 0)
            .ThenByDescending(x => x.a.DedicatedBytes)
            .ThenBy(x => x.i)
            .Select(x => x.a).FirstOrDefault();

    /// <summary>Name for the card title: the product, without the maker's boilerplate ("Radeon RX 580", "RTX 3060", "UHD Graphics 630").</summary>
    public static string ShortName(string name)
    {
        string s = name;
        foreach (var junk in new[] { "NVIDIA GeForce ", "NVIDIA ", "AMD ", "Intel(R) ", "Intel ", "(TM)", "(R)", "(tm)", "(r)" })
            s = s.Replace(junk, "", StringComparison.OrdinalIgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        if (s.EndsWith(" Series", StringComparison.OrdinalIgnoreCase)) s = s[..^7].TrimEnd();
        if (s.Length > 28) s = s[..27].TrimEnd() + "…";
        return s.Length == 0 ? "GPU" : s;
    }
}

/// <summary>Combines the partial readings of the sources into one value per field, remembering which source gave it.</summary>
public static class GpuMerge
{
    /// <summary>Under this a load of "exactly 0" from a less trusted source is believed only if the next source agrees.</summary>
    public const double SuspiciousZeroLoad = 5;

    /// <summary>
    /// First non-null wins, with one exception: a source that says exactly 0 for load or VRAM used loses to a later source that
    /// shows real activity. A driver call that is unsupported often returns 0 instead of failing, and the Windows counters
    /// (what Task Manager shows) are the cross-check.
    /// </summary>
    public static GpuReading Merge(IReadOnlyList<GpuPartial> parts, GpuAdapter? adapter)
    {
        var r = new GpuReading();
        var notes = new List<string>();
        T? First<T>(Func<GpuPartial, T?> pick, out string src) where T : struct
        {
            foreach (var p in parts)
                if (pick(p) is T val) { src = p.Source; return val; }
            src = "";
            return null;
        }

        r.Name = parts.Select(p => p.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? adapter?.Name ?? "";
        r.Vendor = adapter?.Vendor switch { GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", GpuVendor.Intel => "Intel", _ => "" };

        if (First(p => p.TempC, out var tsrc) is double t) { r.TempC = t; r.TempSource = tsrc; r.TempSensor = parts.FirstOrDefault(p => p.TempC != null)?.TempSensor ?? ""; }
        r.HotSpotC = First(p => p.HotSpotC, out _);

        // suspicious zero: the first source says 0, a later one sees real activity
        double? load = null; string lsrc = "";
        foreach (var p in parts)
        {
            if (p.Load is not double l) continue;
            if (load == null) { load = l; lsrc = p.Source; if (l > 0) break; continue; }
            if (l >= SuspiciousZeroLoad) { load = l; lsrc = p.Source; }
            break;
        }
        r.Load = load; r.LoadSource = lsrc;

        long? total = null; string totalSrc = "";
        foreach (var p in parts) if (p.VramTotal is long vt && vt > 0) { total = vt; totalSrc = p.Source; break; }
        long? used = null; string usedSrc = "";
        foreach (var p in parts)
        {
            if (p.VramUsed is not long vu) continue;
            if (used == null) { used = vu; usedSrc = p.Source; if (vu > 0) break; continue; }
            if (vu > 0) { used = vu; usedSrc = p.Source; }
            break;
        }
        if (total == null && adapter is { DedicatedBytes: > 0 }) { total = adapter.DedicatedBytes; totalSrc = "DXGI"; }
        r.VramTotal = total ?? 0;
        r.VramUsed = used ?? 0;
        r.VramSource = used != null ? usedSrc : totalSrc;
        // a used figure above the total (plus a little slack) is a unit or source mix-up: show no bar rather than a wrong one
        if (r.VramTotal > 0 && r.VramUsed > r.VramTotal * 1.1) { r.VramUsed = 0; r.VramSource = totalSrc; notes.Add("VRAM use ignored: larger than the card"); }

        r.PowerW = First(p => p.PowerW, out _) ?? 0;
        r.FanPct = First(p => p.FanPct, out _) is int fp ? fp : -1;
        r.FanRpm = First(p => p.FanRpm, out _) is int fr ? fr : -1;
        r.ClockMhz = First(p => p.ClockMhz, out _) ?? 0;
        r.ThermalThrottle = parts.Any(p => p.ThermalThrottle == true);
        r.ThrottleReasons = First(p => p.ThrottleReasons, out _) ?? 0;

        if (r.TempC == null)
        {
            var why = parts.Select(p => p.Note).FirstOrDefault(n => !string.IsNullOrEmpty(n));
            notes.Insert(0, why ?? "No source reported a temperature for this GPU.");
        }
        r.Note = string.Join(" ", notes);
        return r;
    }
}

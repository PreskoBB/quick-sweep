namespace QuickSweep.Native;

/// <summary>
/// What the flyout, the tray icon, the alerts, Game Mode and the after-game summary know about the GPU. It is filled by
/// <see cref="QuickSweep.Core.GpuMonitor"/>, which merges the NVIDIA, LibreHardwareMonitor and Windows-counter sources.
/// A null <see cref="TempC"/> / <see cref="Load"/> means "no source reported it" (shown as "--" / "n/a"), never 0.
/// </summary>
public sealed class GpuReading
{
    public string Name = "";
    public string Vendor = "";
    /// <summary>Core (edge) temperature; null = this GPU has no temperature sensor Quick Sweep can read.</summary>
    public double? TempC;
    /// <summary>Hot spot / junction temperature where the driver reports one (AMD), for the diagnostics.</summary>
    public double? HotSpotC;
    /// <summary>Busy percent of the 3D / compute engines; null = no source.</summary>
    public double? Load;
    public double PowerW;
    /// <summary>Dedicated video memory in bytes; <see cref="VramTotal"/> 0 = unknown.</summary>
    public long VramUsed, VramTotal;
    public int FanPct = -1;
    public int FanRpm = -1;
    public int ClockMhz;
    public bool ThermalThrottle;
    public ulong ThrottleReasons;
    /// <summary>Which source gave each value ("NVML", "LibreHardwareMonitor", "Windows counters", "DXGI"), for the status text and the log.</summary>
    public string TempSource = "", LoadSource = "", VramSource = "";
    /// <summary>The sensor the temperature came from (for example "GPU Core").</summary>
    public string TempSensor = "";
    /// <summary>Why a value is missing, in plain words (tooltip of the "no sensor" hint).</summary>
    public string Note = "";
}

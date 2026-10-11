using System.Runtime.InteropServices;
using System.Text;

namespace QuickSweep.Native;

public sealed class GpuReading
{
    public string Name = "";
    public double TempC, Load, PowerW;
    public long VramUsed, VramTotal;
    public int FanPct = -1;
    public int ClockMhz;
    public bool ThermalThrottle;
    public ulong ThrottleReasons;
}

/// <summary>NVIDIA GPU via NVML (nvml.dll ships with the display driver). No admin needed.</summary>

internal static class Nvml
{
    [StructLayout(LayoutKind.Sequential)] struct Util { public uint Gpu, Mem; }
    [StructLayout(LayoutKind.Sequential)] struct Mem { public ulong Total, Free, Used; }

    [DllImport("nvml.dll")] static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint idx, out nint dev);
    [DllImport("nvml.dll", CharSet = CharSet.Ansi)] static extern int nvmlDeviceGetName(nint dev, StringBuilder name, uint len);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(nint dev, int sensor, out uint temp);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(nint dev, out Util u);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetMemoryInfo(nint dev, out Mem m);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetFanSpeed(nint dev, out uint pct);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(nint dev, out uint mw);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetClockInfo(nint dev, int type, out uint mhz);
    [DllImport("nvml.dll")] static extern int nvmlDeviceGetCurrentClocksThrottleReasons(nint dev, out ulong reasons);

    static nint _dev;
    static bool _ok, _tried;
    static string _name = "";

    public static bool Available { get { Init(); return _ok; } }

    static void Init()
    {
        if (_tried) return;
        _tried = true;
        try
        {
            if (nvmlInit_v2() != 0) return;
            if (nvmlDeviceGetHandleByIndex_v2(0, out _dev) != 0) return;
            var sb = new StringBuilder(96);
            nvmlDeviceGetName(_dev, sb, 96);
            _name = sb.ToString();
            _ok = true;
        }
        catch { _ok = false; }
    }

    public static GpuReading? Read()
    {
        Init();
        if (!_ok) return null;
        try
        {
            var r = new GpuReading { Name = _name };
            if (nvmlDeviceGetTemperature(_dev, 0, out uint t) == 0) r.TempC = t;
            if (nvmlDeviceGetUtilizationRates(_dev, out var u) == 0) r.Load = u.Gpu;
            if (nvmlDeviceGetMemoryInfo(_dev, out var m) == 0) { r.VramTotal = (long)m.Total; r.VramUsed = (long)m.Used; }
            if (nvmlDeviceGetFanSpeed(_dev, out uint f) == 0) r.FanPct = (int)f;
            if (nvmlDeviceGetPowerUsage(_dev, out uint mw) == 0) r.PowerW = mw / 1000.0;
            if (nvmlDeviceGetClockInfo(_dev, 0, out uint clk) == 0) r.ClockMhz = (int)clk;
            try
            {
                if (nvmlDeviceGetCurrentClocksThrottleReasons(_dev, out ulong reasons) == 0)
                {
                    r.ThrottleReasons = reasons;
                    // 0x20 SW thermal slowdown, 0x40 HW thermal slowdown
                    r.ThermalThrottle = (reasons & 0x60) != 0;
                }
            }
            catch (EntryPointNotFoundException) { }
            return r;
        }
        catch { return null; }
    }
}

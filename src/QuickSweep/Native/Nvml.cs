using System.Runtime.InteropServices;
using System.Text;
using QuickSweep.Core;

namespace QuickSweep.Native;

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

    /// <summary>One NVML reading; a call the driver refuses leaves its field null (shown as "--"), it is no longer reported as 0.</summary>
    public static GpuPartial? Read()
    {
        Init();
        if (!_ok) return null;
        try
        {
            var r = new GpuPartial { Source = "NVML", Name = _name, TempSensor = "NVML temperature" };
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

/// <summary>Source (a): NVIDIA through NVML, exactly the path earlier versions used. Authoritative: when it answers completely nothing else runs.</summary>
internal sealed class NvidiaGpuSource : IGpuSource
{
    public string Label => "NVML";
    public bool Authoritative => true;
    public string Status => Nvml.Available ? "ok" : "not available (no NVIDIA driver)";
    public bool Applies(GpuAdapter? adapter) => (adapter == null || adapter.Vendor == GpuVendor.Nvidia) && Nvml.Available;
    public GpuPartial? Read(GpuAdapter? adapter) => Nvml.Read();
}

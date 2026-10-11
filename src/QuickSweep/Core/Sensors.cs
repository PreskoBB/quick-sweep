using System.Security.Principal;
using LibreHardwareMonitor.Hardware;
using Microsoft.Win32;
using QuickSweep.Native;

namespace QuickSweep.Core;

public sealed class DriveStat
{
    public string Letter = "";
    public string Label = "";
    public long Total, Free;
    public bool Low;   // under the low-space threshold
}

public sealed class MemoryStats
{
    public long Total, Available, InUse, Commit, CommitLimit, KernelPaged, KernelNonpaged;
    public long Modified, Standby, Free, Compressed;
    public bool ListsKnown;
    public double UsedPct => Total == 0 ? 0 : 100.0 * InUse / Total;
    public double CommitPct => CommitLimit == 0 ? 0 : 100.0 * Commit / CommitLimit;
    public long KernelPool => KernelPaged + KernelNonpaged;
}

public sealed class Telemetry
{
    public DateTime Time = DateTime.Now;
    public double? CpuTemp, CpuPower, CpuClock;
    public double CpuLoad;
    public GpuReading? Gpu;
    public MemoryStats Mem = new();
    public DriveStat[] Drives = Array.Empty<DriveStat>();
    public TimeSpan Uptime;
    public double? RouterMs, NetMs;
    public bool RouterLost, NetLost;
    public string CpuStatus = "";
}

public static class Elevation
{
    static readonly bool Real = Check();
    /// <summary>Demo mode (screenshots only) pretends to be elevated so the pages show their normal, installed look.</summary>
    /// <summary>What the process token really is. Every security decision (which folder is trusted, who may override it) uses this.</summary>
    public static bool IsTokenElevated => Real;
    public static bool IsElevated => Demo.On || Real;
    static bool Check()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch { return false; }
    }
}

/// <summary>CPU temperature/power/clock through LibreHardwareMonitorLib (PawnIO driver, admin).</summary>
public sealed class CpuSensor : IDisposable
{
    Computer? _pc;
    IHardware? _cpu;
    public string Status { get; private set; } = "not started";
    public double? TempC { get; private set; }
    public double? PowerW { get; private set; }
    public double? ClockMhz { get; private set; }
    public string SensorName { get; private set; } = "";
    public string CpuName { get; private set; } = "";
    int _fails;

    public static bool PawnIoInstalled()
    {
        try { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"); return k != null; }
        catch { return false; }
    }

    public void Open()
    {
        if (!Elevation.IsElevated) { Status = "needs admin: start Quick Sweep from its logon task (install.ps1)"; return; }
        if (!PawnIoInstalled()) { Status = "PawnIO driver not installed (install.ps1 installs it)"; return; }
        try
        {
            _pc = new Computer { IsCpuEnabled = true };
            _pc.Open();
            _cpu = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
            CpuName = _cpu?.Name ?? "";
            Status = _cpu == null ? "no CPU hardware found" : "ok";
        }
        catch (Exception e) { Status = "sensor init failed: " + e.Message; Log.Write("LHM open: " + e); }
    }

    public void Update()
    {
        if (_cpu == null) return;
        try
        {
            _cpu.Update();
            double? tctl = null, pkg = null, maxT = null, power = null, clock = null;
            string tname = "";
            foreach (var s in _cpu.Sensors)
            {
                if (s.Value is not float v) continue;
                switch (s.SensorType)
                {
                    case SensorType.Temperature:
                        if (s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) || s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase))
                        { if (tctl == null || s.Name.Contains("Tdie")) { tctl = v; tname = s.Name; } }
                        else if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)) { pkg = v; if (tname == "") tname = s.Name; }
                        if (!s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)) maxT = Math.Max(maxT ?? 0, v);
                        break;
                    case SensorType.Power:
                        if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)) power = v;
                        break;
                    case SensorType.Clock:
                        if (s.Name.Contains("Average", StringComparison.OrdinalIgnoreCase)) clock = v;
                        break;
                }
            }
            TempC = tctl ?? pkg ?? maxT;
            if (TempC == 0) TempC = null;
            SensorName = tname;
            PowerW = power; ClockMhz = clock;
            if (TempC == null) Status = "driver loaded but no temperature sensor reported";
            else Status = "ok";
            _fails = 0;
        }
        catch (Exception e)
        {
            if (++_fails == 1) Log.Write("cpu update failed: " + e.Message);
        }
    }

    public void Dispose() { try { _pc?.Close(); } catch { } }
}

/// <summary>Cheap whole-system counters: CPU load, memory, uptime, drives.</summary>
public sealed class SystemStats
{
    long _prevIdle, _prevKernel, _prevUser;
    bool _have;
    long _compressed;
    public long PageSize { get; } = Environment.SystemPageSize;

    public double ReadCpuLoad()
    {
        if (!Win.GetSystemTimes(out long idle, out long kernel, out long user)) return 0;
        double load = 0;
        if (_have)
        {
            long di = idle - _prevIdle, dk = kernel - _prevKernel, du = user - _prevUser;
            long total = dk + du; // kernel time includes idle
            if (total > 0) load = Math.Clamp(100.0 * (total - di) / total, 0, 100);
        }
        _prevIdle = idle; _prevKernel = kernel; _prevUser = user; _have = true;
        return load;
    }

    public void SetCompressed(long bytes) => _compressed = bytes;

    public MemoryStats ReadMemory()
    {
        var m = new MemoryStats();
        var ms = new Win.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win.MEMORYSTATUSEX>() };
        if (Win.GlobalMemoryStatusEx(ref ms))
        {
            m.Total = (long)ms.ullTotalPhys;
            m.Available = (long)ms.ullAvailPhys;
            m.InUse = m.Total - m.Available; // refined below once the Modified list is known
        }
        var pi = new Win.PERFORMANCE_INFORMATION { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win.PERFORMANCE_INFORMATION>() };
        if (Win.GetPerformanceInfo(out pi, pi.cb))
        {
            long ps = (long)pi.PageSize;
            m.Commit = (long)pi.CommitTotal * ps;
            m.CommitLimit = (long)pi.CommitLimit * ps;
            m.KernelPaged = (long)pi.KernelPaged * ps;
            m.KernelNonpaged = (long)pi.KernelNonpaged * ps;
        }
        var lists = ProcessTable.ReadMemoryLists(PageSize);
        if (lists is { } l)
        {
            m.ListsKnown = true;
            m.Modified = l.Modified; m.Standby = l.Standby; m.Free = l.Zero + l.Free;
            m.InUse = Math.Max(0, m.Total - m.Available - m.Modified); // same split as Task Manager / Resource Monitor
        }
        m.Compressed = _compressed;
        return m;
    }

    public static DriveStat[] ReadDrives(int lowGb)
    {
        var list = new List<DriveStat>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new DriveStat
                {
                    Letter = d.Name.Substring(0, 1), Label = d.VolumeLabel, Total = d.TotalSize, Free = d.AvailableFreeSpace,
                    Low = d.AvailableFreeSpace < lowGb * 1024L * 1024 * 1024
                });
            }
            catch { }
        }
        return list.OrderBy(d => d.Letter).ToArray();
    }

    public static TimeSpan Uptime() => TimeSpan.FromMilliseconds(Win.GetTickCount64());
}

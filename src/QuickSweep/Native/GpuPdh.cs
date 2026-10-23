using System.Runtime.InteropServices;
using QuickSweep.Core;

namespace QuickSweep.Native;

/// <summary>
/// The Windows GPU performance counters (what Task Manager's GPU column reads): engine utilisation per process and engine, and
/// dedicated memory per adapter. Works for every vendor and needs no admin. The query stays open between samples, so each
/// sample is a rate over the time since the previous one; it is reopened every 20 minutes so a long-lived wildcard
/// query cannot pile up stale instances.
/// </summary>
internal sealed unsafe class GpuCounterReader : IDisposable
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? src, nint user, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(nint query, string path, nint user, out nint counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint size, ref uint count, byte* buffer);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(nint query);

    const uint PDH_FMT_DOUBLE = 0x200, PDH_MORE_DATA = 0x800007D2;
    static readonly TimeSpan Reopen = TimeSpan.FromMinutes(20);

    nint _q, _engine, _mem;
    DateTime _opened;
    public string Status { get; private set; } = "not started";

    bool Open()
    {
        Close();
        try
        {
            if (PdhOpenQueryW(null, 0, out _q) != 0) { Status = "performance counters could not be opened"; _q = 0; return false; }
            if (PdhAddEnglishCounterW(_q, @"\GPU Engine(*)\Utilization Percentage", 0, out _engine) != 0)
            { Status = "this Windows has no GPU Engine counters"; Close(); return false; }
            if (PdhAddEnglishCounterW(_q, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out _mem) != 0) _mem = 0;   // load alone is still useful
            PdhCollectQueryData(_q);
            Thread.Sleep(250);                       // one short first interval, so the first reading is already a rate
            _opened = DateTime.UtcNow;
            return true;
        }
        catch (Exception e) { Status = "counters failed: " + e.Message; Close(); return false; }
    }

    void Close()
    {
        if (_q != 0) { try { PdhCloseQuery(_q); } catch { } }
        _q = _engine = _mem = 0;
    }

    public bool TrySample(List<(string instance, double value)> engines, List<(string instance, double value)> memory)
    {
        try
        {
            if (_q == 0 || DateTime.UtcNow - _opened > Reopen) { if (!Open()) return false; }
            if (PdhCollectQueryData(_q) != 0) { Status = "counter sample failed"; Close(); return false; }
            Read(_engine, engines);
            if (_mem != 0) Read(_mem, memory);
            Status = "ok";
            return engines.Count > 0 || memory.Count > 0;
        }
        catch (Exception e) { Status = "counters failed: " + e.Message; Close(); return false; }
    }

    static void Read(nint counter, List<(string, double)> into)
    {
        uint size = 0, count = 0;
        if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, ref count, null) != PDH_MORE_DATA || size == 0) return;
        byte* buf = (byte*)NativeMemory.Alloc(size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, ref count, buf) != 0) return;
            for (uint i = 0; i < count; i++)
            {
                // PDH_FMT_COUNTERVALUE_ITEM_W (x64): szName (8), CStatus (4) + pad (4), value (8)
                byte* item = buf + i * 24;
                string? name = Marshal.PtrToStringUni(*(nint*)item);
                if (name == null || *(uint*)(item + 8) != 0) continue;       // new instances have no rate yet
                into.Add((name, *(double*)(item + 16)));
            }
        }
        finally { NativeMemory.Free(buf); }
    }

    public void Dispose() => Close();
}

/// <summary>Source (c): usage and dedicated VRAM on any GPU, no temperature.</summary>
internal sealed class CounterGpuSource : IGpuSource, IDisposable
{
    readonly GpuCounterReader _reader = new();
    public string Label => "Windows counters";
    public bool Authoritative => false;
    public string Status { get; private set; } = "not started";
    public bool Applies(GpuAdapter? adapter) => true;

    public GpuPartial? Read(GpuAdapter? adapter)
    {
        var engines = new List<(string, double)>(); var memory = new List<(string, double)>();
        if (!_reader.TrySample(engines, memory)) { Status = _reader.Status; return null; }
        var p = GpuCounterMath.ToPartial(engines, memory, adapter, Label, out string why);
        Status = why;
        return p;
    }

    public void Dispose() => _reader.Dispose();
}

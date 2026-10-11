using System.Runtime.InteropServices;

namespace QuickSweep.Native;

/// <summary>
/// Per-process GPU utilisation from the Windows "GPU Engine" performance counters (the numbers Task Manager shows),
/// read through PDH with English counter names so it works on any display language. Two samples one second apart.
/// </summary>
internal static unsafe class GpuEngines
{
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhOpenQueryW(string? src, nint user, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] static extern uint PdhAddEnglishCounterW(nint query, string path, nint user, out nint counter);
    [DllImport("pdh.dll")] static extern uint PdhCollectQueryData(nint query);
    [DllImport("pdh.dll")] static extern uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint size, ref uint count, byte* buffer);
    [DllImport("pdh.dll")] static extern uint PdhCloseQuery(nint query);

    const uint PDH_FMT_DOUBLE = 0x200, PDH_MORE_DATA = 0x800007D2;

    /// <summary>pid -> highest engine utilisation in percent (0..100), or null when the counters are not available.</summary>
    public static Dictionary<int, double>? SamplePerPid(int intervalMs = 1000)
    {
        nint q = 0;
        try
        {
            if (PdhOpenQueryW(null, 0, out q) != 0) return null;
            if (PdhAddEnglishCounterW(q, @"\GPU Engine(*)\Utilization Percentage", 0, out nint counter) != 0) return null;
            PdhCollectQueryData(q);
            Thread.Sleep(intervalMs);
            if (PdhCollectQueryData(q) != 0) return null;

            uint size = 0, count = 0;
            uint rc = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, ref count, null);
            if (rc != PDH_MORE_DATA || size == 0) return null;
            byte* buf = (byte*)NativeMemory.Alloc(size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref size, ref count, buf) != 0) return null;
                var byEngine = new Dictionary<(int pid, string type), double>();
                for (uint i = 0; i < count; i++)
                {
                    // PDH_FMT_COUNTERVALUE_ITEM_W (x64): szName (8), CStatus (4) + pad (4), value (8)
                    byte* item = buf + i * 24;
                    string? name = Marshal.PtrToStringUni(*(nint*)item);
                    if (name == null || *(uint*)(item + 8) != 0) continue;
                    double v = *(double*)(item + 16);
                    int pid = ParsePid(name);
                    if (pid <= 0) continue;
                    int et = name.LastIndexOf("engtype_", StringComparison.Ordinal);
                    string type = et >= 0 ? name[(et + 8)..] : "";
                    byEngine[(pid, type)] = byEngine.GetValueOrDefault((pid, type)) + v;
                }
                var result = new Dictionary<int, double>();
                foreach (var ((pid, _), v) in byEngine) result[pid] = Math.Min(100, Math.Max(result.GetValueOrDefault(pid), v));
                return result;
            }
            finally { NativeMemory.Free(buf); }
        }
        catch { return null; }
        finally { if (q != 0) PdhCloseQuery(q); }
    }

    /// <summary>Instance names look like pid_1234_luid_0x..._phys_0_eng_0_engtype_3D.</summary>
    public static int ParsePid(string instance)
    {
        if (!instance.StartsWith("pid_", StringComparison.Ordinal)) return -1;
        int end = instance.IndexOf('_', 4);
        return end > 4 && int.TryParse(instance.AsSpan(4, end - 4), out int pid) ? pid : -1;
    }
}

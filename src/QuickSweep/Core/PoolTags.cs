using System.Runtime.InteropServices;
using QuickSweep.Native;

namespace QuickSweep.Core;

public sealed record PoolTag(string Tag, long PagedBytes, long NonPagedBytes, string Meaning)
{
    public long Total => PagedBytes + NonPagedBytes;
}

/// <summary>
/// Which kernel components hold the Windows kernel pool (the memory Task Manager does not show).
/// Same data PoolMon shows; reading it needs admin on most systems.
/// </summary>
public static unsafe class PoolTags
{
    // Plain-words meanings for the tags that actually show up at the top on desktop PCs.
    static readonly Dictionary<string, string> Known = new()
    {
        ["FMfn"] = "File-system filter name cache (Windows remembers file names it has seen; grows with big file scans)",
        ["FMsl"] = "File-system filter stream-list contexts (grows with big file scans)",
        ["FMfc"] = "File-system filter file contexts (grows with big file scans)",
        ["File"] = "Open file objects",
        ["Ntff"] = "NTFS file control blocks (cached file records)",
        ["NtFs"] = "NTFS bookkeeping structures",
        ["NtfF"] = "NTFS file records",
        ["Ntfn"] = "NTFS name cache",
        ["Ntf0"] = "NTFS general allocations",
        ["Ntfr"] = "NTFS resource structures",
        ["Proc"] = "Process objects",
        ["Thre"] = "Thread objects",
        ["Toke"] = "Security tokens",
        ["Key "] = "Registry key objects",
        ["CM25"] = "Registry (configuration manager) cache",
        ["CMNb"] = "Registry (configuration manager) cache",
        ["Mm  "] = "Memory manager structures",
        ["MmSt"] = "Memory manager section mappings",
        ["MmCa"] = "Memory manager mapped-file cache",
        ["Cont"] = "Contiguous memory (drivers, GPU)",
        ["Mdl "] = "Memory descriptor lists (drivers)",
        ["Ntfx"] = "NTFS transaction structures",
        ["ALPC"] = "Inter-process message ports",
        ["Sema"] = "Semaphore objects",
        ["Even"] = "Event objects",
        ["Muta"] = "Mutex objects",
        ["Sect"] = "Section objects (memory-mapped files)",
        ["Dxgk"] = "Graphics driver (DirectX kernel)",
        ["Nvrm"] = "NVIDIA display driver",
        ["WdFl"] = "Windows Defender file-system filter",
        ["WDEf"] = "Windows Defender engine",
        ["MpFl"] = "Windows Defender file filter",
    };

    public static string Describe(string tag)
        => Known.TryGetValue(tag, out var m) ? m : "Driver or component (no plain-words name known)";

    /// <summary>Top pool consumers, or null when Windows does not allow reading them.</summary>
    public static List<PoolTag>? ReadTop(int count)
    {
        if (Demo.On) return Demo.PoolTags();
        int size = 1 << 20;
        byte* buf = (byte*)NativeMemory.Alloc((nuint)size);
        try
        {
            int need;
            int st;
            while ((st = Win.NtQuerySystemInformation(22, buf, size, out need)) == unchecked((int)0xC0000004))
            {
                size = Math.Max(need + (64 << 10), size * 2);
                buf = (byte*)NativeMemory.Realloc(buf, (nuint)size);
            }
            if (st < 0) return null;
            uint n = *(uint*)buf;
            var list = new List<PoolTag>((int)n);
            // SYSTEM_POOLTAG_INFORMATION: Count (4) + pad (4), then entries of 40 bytes
            byte* p = buf + 8;
            for (uint i = 0; i < n; i++, p += 40)
            {
                string tag = new string(new[] { (char)p[0], (char)p[1], (char)p[2], (char)p[3] });
                long paged = (long)*(nuint*)(p + 16);
                long nonPaged = (long)*(nuint*)(p + 32);
                if (paged + nonPaged < 8L * 1024 * 1024) continue;
                list.Add(new PoolTag(tag, paged, nonPaged, Describe(tag)));
            }
            return list.OrderByDescending(t => t.Total).Take(count).ToList();
        }
        catch { return null; }
        finally { NativeMemory.Free(buf); }
    }
}

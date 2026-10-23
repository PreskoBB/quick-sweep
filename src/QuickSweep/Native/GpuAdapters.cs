using System.Runtime.InteropServices;
using Microsoft.Win32;
using QuickSweep.Core;

namespace QuickSweep.Native;

/// <summary>
/// Lists the display adapters with name, vendor, dedicated video memory and LUID: DXGI first (works for every vendor, no admin,
/// reports 64-bit memory sizes), the display-class registry keys when DXGI cannot be used.
/// </summary>
internal sealed unsafe class WindowsGpuLister : IGpuAdapterLister
{
    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out nint factory);

    [StructLayout(LayoutKind.Sequential)]
    struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow; public int LuidHigh;
        public uint Flags;
    }

    const int NotFound = unchecked((int)0x887A0002);   // DXGI_ERROR_NOT_FOUND: end of the list
    const uint SoftwareFlag = 2;                       // DXGI_ADAPTER_FLAG_SOFTWARE (WARP, "Microsoft Basic Render Driver")

    public IReadOnlyList<GpuAdapter> List()
    {
        var viaDxgi = TryDxgi();
        if (viaDxgi.Count > 0) return viaDxgi;
        return FromRegistry();
    }

    static List<GpuAdapter> TryDxgi()
    {
        var list = new List<GpuAdapter>();
        nint factory = 0;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == 0) return list;
            // vtable: IUnknown 0..2, IDXGIObject 3..6, IDXGIFactory 7..11, IDXGIFactory1: EnumAdapters1 = 12
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12];
            for (uint i = 0; i < 32; i++)
            {
                nint adapter = 0;
                int hr = enumAdapters1(factory, i, &adapter);
                if (hr == NotFound || hr < 0 || adapter == 0) break;
                try
                {
                    // IDXGIAdapter1: IUnknown 0..2, IDXGIObject 3..6, IDXGIAdapter 7..9, GetDesc1 = 10
                    var getDesc1 = (delegate* unmanaged[Stdcall]<nint, DXGI_ADAPTER_DESC1*, int>)(*(nint**)adapter)[10];
                    DXGI_ADAPTER_DESC1 d = default;
                    if (getDesc1(adapter, &d) < 0) continue;
                    string name = new string(d.Description).Trim();
                    var vendor = GpuChooser.VendorFromPci(d.VendorId);
                    long luid = GpuCounterMath.CombineLuid((uint)d.LuidHigh, d.LuidLow);
                    bool soft = (d.Flags & SoftwareFlag) != 0;
                    list.Add(new GpuAdapter(name, vendor, luid, (long)d.DedicatedVideoMemory, (long)d.SharedSystemMemory, soft,
                        GpuChooser.LooksIntegrated(name, vendor)));
                }
                finally { Release(adapter); }
            }
        }
        catch (Exception e) { Log.Write("gpu: DXGI listing failed: " + e.Message); list.Clear(); }
        finally { if (factory != 0) Release(factory); }
        return list;
    }

    static void Release(nint com)
    {
        try { ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)com)[2])(com); } catch { }
    }

    /// <summary>The display class (GUID {4d36e968-...}) lists every installed graphics driver with its description and, for most, the memory size.</summary>
    static IReadOnlyList<GpuAdapter> FromRegistry()
    {
        var list = new List<GpuAdapter>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-911a-00a0c9223a1b}");
            if (cls == null) return list;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsAsciiDigit)) continue;
                using var k = cls.OpenSubKey(sub);
                if (k == null) continue;
                string id = (k.GetValue("MatchingDeviceId") as string ?? "").ToLowerInvariant();
                if (!id.StartsWith(@"pci\")) continue;                      // virtual and software display devices are not on the PCI bus
                string name = (k.GetValue("DriverDesc") as string ?? "").Trim();
                if (name.Length == 0) continue;
                uint ven = 0;
                int vi = id.IndexOf("ven_", StringComparison.Ordinal);
                if (vi >= 0 && id.Length >= vi + 8) uint.TryParse(id.AsSpan(vi + 4, 4), System.Globalization.NumberStyles.AllowHexSpecifier, null, out ven);
                long mem = k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    int i => (uint)i,
                    _ => 0
                };
                var vendor = GpuChooser.VendorFromPci(ven);
                list.Add(new GpuAdapter(name, vendor, 0, mem, 0, false, GpuChooser.LooksIntegrated(name, vendor)));
            }
        }
        catch (Exception e) { Log.Write("gpu: registry adapter listing failed: " + e.Message); }
        return list;
    }
}

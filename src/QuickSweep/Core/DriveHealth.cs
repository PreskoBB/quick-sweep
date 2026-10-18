using System.Runtime.InteropServices;
using System.Globalization;
using System.Text;

namespace QuickSweep.Core;

public sealed class SmartAttr
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int Value { get; set; }
    public int Worst { get; set; }
    public int Threshold { get; set; }
    public long Raw { get; set; }
}

public enum HealthState { Unknown, Good, Warning, Critical }

public sealed class DriveReport
{
    public int Number { get; set; }
    public string Model { get; set; } = "";
    public string Serial { get; set; } = "";
    public string Firmware { get; set; } = "";
    public string Bus { get; set; } = "";
    public long SizeBytes { get; set; }
    public bool IsSsd { get; set; }
    public List<string> Letters { get; set; } = new();
    public List<SmartAttr> Attrs { get; set; } = new();
    public double? TempC { get; set; }
    public double? LifeLeftPct { get; set; }
    public string LifeSource { get; set; } = "";
    public long? Reallocated { get; set; }
    public long? Pending { get; set; }
    public long? Uncorrectable { get; set; }
    public long? CrcErrors { get; set; }
    public long? PowerOnHours { get; set; }
    public double? WrittenTb { get; set; }
    public HealthState State { get; set; } = HealthState.Unknown;
    public List<string> Findings { get; set; } = new();
    public string? Error { get; set; }
    public string Method { get; set; } = "";
    /// <summary>The numbers that matter went up since the remembered baseline (sectors, CRC errors) or the wear dropped.</summary>
    public bool Degrading { get; set; }
    /// <summary>What is wrong, as the end of a sentence about the drive: "has old damage", "is failing"... Empty when healthy.</summary>
    public string Headline { get; set; } = "";
    public string Key => string.IsNullOrWhiteSpace(Serial) ? Model : Serial;
}

/// <summary>Values remembered between checks, to notice a drive that is getting worse.</summary>
public sealed class DriveBaseline
{
    public long Reallocated { get; set; }
    public long Pending { get; set; }
    public long Uncorrectable { get; set; }
    /// <summary>Null in baselines written before 1.0.3: unknown, so a high count is not mistaken for a rising one.</summary>
    public long? CrcErrors { get; set; }
    public DateTime? CrcSince { get; set; }
    public double? LifeLeftPct { get; set; }
    public DateTime Since { get; set; } = DateTime.Now;
}
public sealed class BaselineStore { public Dictionary<string, DriveBaseline> Drives { get; set; } = new(); }

/// <summary>
/// SMART through the classic IOCTLs on \\.\PhysicalDriveN (needs admin). Reads the attribute table and thresholds of
/// SATA drives and judges them: pre-fail thresholds, reallocated/pending/uncorrectable sectors, SSD wear, temperature.
/// NVMe and USB-bridged drives are listed without SMART.
/// </summary>
public static unsafe class DriveHealth
{
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_RW = 3, OPEN_EXISTING = 3;
    const uint IOCTL_ATA_PASS_THROUGH = 0x4D02C, IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400, IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080, SMART_RCV_DRIVE_DATA = 0x7C088, IOCTL_DISK_GET_LENGTH_INFO = 0x7405C;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern nint CreateFileW(string name, uint access, uint share, nint sec, uint disp, uint flags, nint tmpl);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(nint h, uint code, void* inBuf, uint inSize, void* outBuf, uint outSize, out uint ret, nint ov);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);

    static readonly Dictionary<int, string> Names = new()
    {
        [1] = "Read error rate", [3] = "Spin-up time", [4] = "Start/stop count", [5] = "Reallocated sectors", [7] = "Seek error rate",
        [9] = "Power-on hours", [10] = "Spin retry count", [12] = "Power cycle count", [169] = "Remaining lifetime", [170] = "Available reserved space",
        [171] = "Program fail count", [172] = "Erase fail count", [173] = "Average block erase count", [174] = "Unexpected power loss",
        [175] = "Power-loss protection failure", [177] = "Wear leveling count", [179] = "Used reserved blocks", [180] = "Unused reserved blocks",
        [181] = "Program fail count", [182] = "Erase fail count", [183] = "SATA downshift errors", [184] = "End-to-end error", [187] = "Reported uncorrectable errors",
        [188] = "Command timeout", [189] = "High fly writes", [190] = "Airflow temperature", [191] = "G-sense errors", [192] = "Power-off retract count",
        [193] = "Load cycle count", [194] = "Temperature", [195] = "Hardware ECC recovered", [196] = "Reallocation events", [197] = "Pending sectors",
        [198] = "Offline uncorrectable", [199] = "UDMA CRC errors", [202] = "Percent lifetime remaining", [206] = "Write error rate", [231] = "SSD life left",
        [232] = "Available reserved space", [233] = "Media wearout indicator", [234] = "Total NAND writes", [241] = "Total LBAs written", [242] = "Total LBAs read", [246] = "Total host sector writes",
    };

    public static string AttrName(int id) => Names.TryGetValue(id, out var n) ? n : $"Attribute {id}";

    public static List<DriveReport> ReadAll(bool includeHdd, Func<DriveReport, bool>? skip = null)
    {
        if (Demo.On) return Demo.Health();
        var list = new List<DriveReport>();
        var letters = LettersByDisk();
        for (int n = 0; n < 16; n++)
        {
            var h = CreateFileW($@"\\.\PhysicalDrive{n}", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, 0, OPEN_EXISTING, 0, 0);
            int err = Marshal.GetLastWin32Error();
            if (h == -1 || h == 0)
            {
                if (err == 5) { if (list.Count == 0) list.Add(new DriveReport { Model = "Drives", Error = "needs admin rights" }); break; }
                continue;   // no disk with this number
            }
            try
            {
                var d = new DriveReport { Number = n };
                if (letters.TryGetValue(n, out var ls)) d.Letters = ls;
                Describe(h, d);
                if (!d.IsSsd && !includeHdd && d.Error == null) { d.Error = "skipped (hard disk: checked when you open this page)"; list.Add(d); continue; }
                if (d.Bus.Contains("NVMe", StringComparison.OrdinalIgnoreCase)) d.Error = "NVMe health is not read by this version";
                else if (d.Bus.Contains("USB", StringComparison.OrdinalIgnoreCase)) d.Error = "USB drive: SMART passthrough not supported";
                else ReadSmart(h, d);
                Evaluate(d);
                list.Add(d);
            }
            finally { CloseHandle(h); }
        }
        return list;
    }

    // ---- device description --------------------------------------------------------
    static void Describe(nint h, DriveReport d)
    {
        byte* q = stackalloc byte[12];
        for (int i = 0; i < 12; i++) q[i] = 0;
        byte* buf = stackalloc byte[1024];
        // StorageDeviceProperty (0), PropertyStandardQuery (0)
        if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, q, 12, buf, 1024, out uint ret, 0) && ret > 40)
        {
            uint vOff = *(uint*)(buf + 12), pOff = *(uint*)(buf + 16), rOff = *(uint*)(buf + 20), sOff = *(uint*)(buf + 24);
            int bus = *(int*)(buf + 28);
            string Str(uint off) => off == 0 || off >= 1024 ? "" : Marshal.PtrToStringAnsi((nint)(buf + off))?.Trim() ?? "";
            string vendor = Str(vOff), prod = Str(pOff);
            d.Model = (vendor + " " + prod).Trim();
            d.Firmware = Str(rOff); d.Serial = Str(sOff);
            d.Bus = bus switch { 1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 12 => "SD", 17 => "NVMe", _ => $"bus {bus}" };
        }
        // seek penalty: no penalty = SSD (StorageDeviceSeekPenaltyProperty = 7)
        for (int i = 0; i < 12; i++) q[i] = 0;
        *(int*)q = 7;
        byte* sp = stackalloc byte[16];
        if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, q, 12, sp, 16, out ret, 0) && ret >= 9) d.IsSsd = sp[8] == 0;
        long len;
        if (DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, null, 0, &len, 8, out ret, 0)) d.SizeBytes = len;
    }

    static Dictionary<int, List<string>> LettersByDisk()
    {
        var map = new Dictionary<int, List<string>>();
        foreach (var dr in DriveInfo.GetDrives())
        {
            if (dr.DriveType != DriveType.Fixed) continue;
            var h = CreateFileW($@"\\.\{dr.Name[0]}:", 0, FILE_SHARE_RW, 0, OPEN_EXISTING, 0, 0);
            if (h == -1 || h == 0) continue;
            try
            {
                int* num = stackalloc int[3];
                if (DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0, num, 12, out _, 0))
                {
                    if (!map.TryGetValue(num[1], out var l)) map[num[1]] = l = new();
                    l.Add(dr.Name[0] + ":");
                }
            }
            finally { CloseHandle(h); }
        }
        return map;
    }

    // ---- SMART ---------------------------------------------------------------------------
    static byte[]? SmartCommand(nint h, int driveNo, byte feature)
    {
        const int inSize = 35, outSize = 16 + 512 + 8;
        byte* inb = stackalloc byte[40];
        for (int i = 0; i < 40; i++) inb[i] = 0;
        *(uint*)inb = 512;                    // cBufferSize
        inb[4] = feature;                      // bFeaturesReg: D0 = read data, D1 = read thresholds
        inb[5] = 1; inb[6] = 1;                // sector count, sector number
        inb[7] = 0x4F; inb[8] = 0xC2;          // SMART signature
        inb[9] = (byte)(0xA0 | ((driveNo & 1) << 4));
        inb[10] = 0xB0;                        // SMART command
        inb[12] = (byte)driveNo;
        byte* outb = stackalloc byte[outSize];
        for (int i = 0; i < outSize; i++) outb[i] = 0;
        if (!DeviceIoControl(h, SMART_RCV_DRIVE_DATA, inb, inSize, outb, outSize, out uint ret, 0) || ret < 16 + 512)
            return SmartViaPassThrough(h, feature);
        var data = new byte[512];
        Marshal.Copy((nint)(outb + 16), data, 0, 512);
        return data;
    }

    /// <summary>Same SMART read through IOCTL_ATA_PASS_THROUGH, for controllers that reject the legacy SMART ioctl.</summary>
    static byte[]? SmartViaPassThrough(nint h, byte feature)
    {
        const int hdr = 48, total = hdr + 512;
        byte* b = stackalloc byte[total];
        for (int i = 0; i < total; i++) b[i] = 0;
        *(ushort*)b = hdr;                  // Length
        *(ushort*)(b + 2) = 0x02;           // ATA_FLAGS_DATA_IN
        *(uint*)(b + 8) = 512;              // DataTransferLength
        *(uint*)(b + 12) = 10;              // TimeOutValue (s)
        *(ulong*)(b + 24) = hdr;            // DataBufferOffset
        byte* tf = b + 40;                  // CurrentTaskFile
        tf[0] = feature; tf[1] = 1; tf[2] = 1; tf[3] = 0x4F; tf[4] = 0xC2; tf[5] = 0xA0; tf[6] = 0xB0;
        if (!DeviceIoControl(h, IOCTL_ATA_PASS_THROUGH, b, total, b, total, out uint ret, 0) || ret < total) return null;
        var data = new byte[512];
        Marshal.Copy((nint)(b + hdr), data, 0, 512);
        return data;
    }

    static void ReadSmart(nint h, DriveReport d)
    {
        var data = SmartCommand(h, d.Number, 0xD0);
        if (data == null) { d.Error = "SMART data is not available through this controller"; return; }
        var thr = SmartCommand(h, d.Number, 0xD1);
        d.Attrs = ParseAttributes(data, thr);
        Summarize(d);
    }

    public static List<SmartAttr> ParseAttributes(byte[] data, byte[]? thr)
    {
        var list = new List<SmartAttr>();
        for (int i = 0; i < 30; i++)
        {
            int o = 2 + i * 12;
            int id = data[o];
            if (id == 0) continue;
            long raw = 0;
            for (int b = 5; b >= 0; b--) raw = (raw << 8) | data[o + 5 + b];
            int threshold = 0;
            if (thr != null) for (int k = 0; k < 30; k++) if (thr[2 + k * 12] == id) { threshold = thr[2 + k * 12 + 1]; break; }
            list.Add(new SmartAttr { Id = id, Name = AttrName(id), Value = data[o + 3], Worst = data[o + 4], Threshold = threshold, Raw = raw });
        }
        return list;
    }

    public static void Summarize(DriveReport d)
    {
        SmartAttr? A(int id) => d.Attrs.FirstOrDefault(a => a.Id == id);
        long Raw32(SmartAttr? a) => a == null ? 0 : a.Raw & 0xFFFFFFFF;
        if ((A(194) ?? A(190)) is { } t) d.TempC = t.Raw & 0xFF;
        if (A(5) is { } r5) d.Reallocated = Raw32(r5);
        if (A(197) is { } r197) d.Pending = Raw32(r197);
        if ((A(198) ?? A(187)) is { } r198) d.Uncorrectable = Raw32(r198);
        if (A(199) is { } crc) d.CrcErrors = Raw32(crc);
        if (A(9) is { } poh) d.PowerOnHours = poh.Raw & 0xFFFFFFFF;
        foreach (var id in new[] { 231, 202, 233, 177, 169 })
            if (A(id) is { } life && life.Value is > 0 and <= 100) { d.LifeLeftPct = life.Value; d.LifeSource = $"{life.Name} (attribute {id})"; break; }
        if (d.IsSsd && A(241) is { } w) d.WrittenTb = Math.Round((w.Raw & 0xFFFFFFFFFFFF) * 512.0 / 1e12, 1);
    }

    static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
    static string Day(DateTime t) => t.ToString("d MMM", CultureInfo.InvariantCulture);

    /// <summary>
    /// Judges a drive. <paramref name="b"/> is what was remembered about it earlier (null on the first sight).
    /// Rules: pending or uncorrectable sectors, a growing reallocated count and a failed vendor threshold are Critical;
    /// a reallocated count that has stayed the same since the baseline is Warning ("old damage"); interface CRC errors
    /// point at the cable, not the drive: Warning, Critical only while the count is still rising.
    /// </summary>
    public static void Evaluate(DriveReport d, DriveBaseline? b = null)
    {
        var f = d.Findings; f.Clear();
        d.Degrading = false; d.Headline = "";
        var state = d.Attrs.Count == 0 ? HealthState.Unknown : HealthState.Good;
        int rank = 0;
        // rank decides the headline: 6 getting worse, 5 failing, 4 wearing faster, 3 old damage, 2 cable, 1 other
        void Add(HealthState s, int r, string headline, string text)
        {
            f.Add(text);
            if (s > state) state = s;
            if (r > rank) { rank = r; d.Headline = headline; }
        }
        static string S(long n, string word) => n == 1 ? word : word + "s";

        foreach (var a in d.Attrs)
            if (a.Threshold > 0 && a.Value > 0 && a.Value <= a.Threshold && a.Id != 1 && a.Id != 7)
                Add(HealthState.Critical, 5, "is failing", $"{a.Name}: value {a.Value} reached its failure threshold {a.Threshold}.");

        if (d.Reallocated is long ra && ra > 0)
        {
            if (b != null && ra > b.Reallocated)
            {
                d.Degrading = true;
                Add(HealthState.Critical, 6, "is getting worse", $"Reallocated sectors rose from {N(b.Reallocated)} to {N(ra)} since {Day(b.Since)}: the drive is failing. Back up now and plan a replacement.");
            }
            else if (b != null)
                Add(HealthState.Warning, 3, "has old damage", $"{N(ra)} reallocated {S(ra, "sector")}, unchanged since {Day(b.Since)}. Keep a backup of anything important on it.");
            else
                Add(HealthState.Warning, 3, "has reallocated sectors", $"{N(ra)} reallocated {S(ra, "sector")}, first seen now; Quick Sweep tells you if the number grows. Keep a backup of anything important on it.");
        }
        if (d.Pending is long pe && pe > 0)
        {
            bool grew = b != null && pe > b.Pending;
            if (grew) d.Degrading = true;
            Add(HealthState.Critical, grew ? 6 : 5, grew ? "is getting worse" : "is failing",
                $"{N(pe)} {S(pe, "sector")} the drive cannot read until rewritten (pending reallocation)" + (grew ? $", up from {N(b!.Pending)} on {Day(b.Since)}." : ".") + " Back up now.");
        }
        if (d.Uncorrectable is long un && un > 0)
        {
            bool grew = b != null && un > b.Uncorrectable;
            if (grew) d.Degrading = true;
            Add(HealthState.Critical, grew ? 6 : 5, grew ? "is getting worse" : "is failing",
                $"{N(un)} uncorrectable read {S(un, "error")}" + (grew ? $", up from {N(b!.Uncorrectable)} on {Day(b.Since)}." : ".") + " Back up now.");
        }
        if (d.LifeLeftPct is double life)
        {
            if (life <= 10) Add(HealthState.Critical, 5, "is nearly worn out", $"SSD life left is only {life:0}%: replace it soon.");
            else if (life <= 25) Add(HealthState.Warning, 1, "is wearing out", $"SSD life left {life:0}%.");
            if (b?.LifeLeftPct is double bl && life <= bl - 2)
            {
                d.Degrading = true;
                Add(HealthState.Warning, 4, "is wearing fast", $"SSD life left went from {bl:0}% to {life:0}% since {Day(b.Since)}.");
            }
        }
        if (d.TempC is double temp)
        {
            if (d.IsSsd ? temp >= 75 : temp >= 60) Add(HealthState.Critical, 5, "is too hot", $"Temperature {temp:0} °C is too high.");
            else if (d.IsSsd ? temp >= 65 : temp >= 52) Add(HealthState.Warning, 1, "is running warm", $"Temperature {temp:0} °C is warm.");
        }
        if (d.CrcErrors is long crcs && crcs > 100)
        {
            if (b?.CrcErrors is long bc && crcs > bc)
            {
                d.Degrading = true;
                Add(HealthState.Critical, 6, "has a cable problem that is getting worse",
                    $"{N(crcs)} interface (CRC) errors, up from {N(bc)} on {Day(b.CrcSince ?? b.Since)}. Reseat or replace the SATA cable now; the drive itself is probably fine.");
            }
            else
                Add(HealthState.Warning, 2, "has a cable problem",
                    $"{N(crcs)} interface (CRC) errors" + (b?.CrcErrors != null ? $", not rising since {Day(b.CrcSince ?? b.Since)}" : "") + ". Cable problem: reseat or replace the SATA cable.");
        }
        d.State = state;
    }

    // ---- degradation vs. the remembered baseline ---------------------------------------------
    static DriveBaseline Capture(DriveReport d, DateTime now) => new()
    {
        Reallocated = d.Reallocated ?? 0, Pending = d.Pending ?? 0, Uncorrectable = d.Uncorrectable ?? 0,
        CrcErrors = d.CrcErrors, CrcSince = d.CrcErrors != null ? now : null, LifeLeftPct = d.LifeLeftPct, Since = now
    };

    /// <summary>Judges every drive against the stored baseline (creating one on first sight) and saves changes.</summary>
    public static void ApplyBaseline(List<DriveReport> reports, bool save = true)
    {
        var store = JsonStore.Load<BaselineStore>("drivehealth.json");
        if (ApplyBaseline(reports, store, DateTime.Now) && save) JsonStore.Save("drivehealth.json", store);
    }

    /// <summary>Same, on an in-memory store. Returns true when the store changed.</summary>
    public static bool ApplyBaseline(List<DriveReport> reports, BaselineStore store, DateTime now)
    {
        bool changed = false;
        foreach (var d in reports.Where(r => r.Attrs.Count > 0))
        {
            if (!store.Drives.TryGetValue(d.Key, out var b))
            {
                store.Drives[d.Key] = Capture(d, now);
                changed = true;
                Evaluate(d, null);
                continue;
            }
            // a baseline from before CRC was stored: start remembering it now, do not call a high count "rising"
            if (b.CrcErrors == null && d.CrcErrors != null) { b.CrcErrors = d.CrcErrors; b.CrcSince = now; changed = true; }
            // counters that went down (a repaired sector, a replaced cable): lower the baseline so a later rise is noticed
            if (d.Reallocated is long r && r < b.Reallocated) { b.Reallocated = r; changed = true; }
            if (d.Pending is long p && p < b.Pending) { b.Pending = p; changed = true; }
            if (d.Uncorrectable is long u && u < b.Uncorrectable) { b.Uncorrectable = u; changed = true; }
            if (d.CrcErrors is long c && b.CrcErrors is long bc && c < bc) { b.CrcErrors = c; b.CrcSince = now; changed = true; }
            Evaluate(d, b);
        }
        return changed;
    }

    /// <summary>Accept today's numbers as the new normal.</summary>
    public static void ResetBaseline(IEnumerable<DriveReport> reports)
    {
        var store = JsonStore.Load<BaselineStore>("drivehealth.json");
        var now = DateTime.Now;
        foreach (var d in reports.Where(r => r.Attrs.Count > 0))
            store.Drives[d.Key] = Capture(d, now);
        JsonStore.Save("drivehealth.json", store);
    }
}

using QuickSweep.Core.Cleaner;
using QuickSweep.Native;

namespace QuickSweep.Core;

/// <summary>
/// Demo mode (<c>--shots --demo</c>): every page is filled with neutral sample data instead of this PC's real readings,
/// so screenshots of the README show no real process names, folders, drives or user names. Nothing here is read from
/// the machine, and the sensors, the ping loop and the real process scan are not started.
/// </summary>
public static class Demo
{
    public static bool On { get; set; }
    /// <summary><c>--demo-problem</c>: the sample hard disk has old damage and the sample SSD a cable problem (flyout banner, Drive health page).</summary>
    public static bool Problem { get; set; }
    /// <summary><c>--demo-problem=worse</c>: the remembered numbers are lower than the current ones, so the damage is growing (red banner).</summary>
    public static bool Worse { get; set; }

    /// <summary><c>--demo-gpu=amd</c>: an AMD card with no temperature sensor (usage and VRAM from the Windows counters), to check the "no sensor" look.</summary>
    public static bool AmdNoSensor { get; set; }

    const long GB = 1024L * 1024 * 1024;
    const long MB = 1024L * 1024;

    // ---- live readings --------------------------------------------------------------------------------------------
    public static Telemetry Reading(double cpuTemp = 56, double cpuLoad = 24, double gpuTemp = 49, double gpuLoad = 11)
    {
        var mem = new MemoryStats
        {
            Total = 32 * GB, InUse = (long)(15.2 * GB), Compressed = (long)(0.9 * GB), Modified = (long)(0.3 * GB),
            Standby = (long)(11.4 * GB), Free = (long)(5.1 * GB), KernelPaged = (long)(0.35 * GB), KernelNonpaged = (long)(0.9 * GB),
            Commit = (long)(19.6 * GB), CommitLimit = 44 * GB, ListsKnown = true
        };
        mem.Available = mem.Total - mem.InUse;
        return new Telemetry
        {
            Time = DateTime.Now,
            CpuTemp = cpuTemp, CpuLoad = cpuLoad, CpuClock = 4350, CpuPower = 38, CpuStatus = "ok",
            Gpu = AmdNoSensor
                ? new GpuReading
                {
                    Name = "Radeon RX 580 Series", Vendor = "AMD", TempC = null, Load = gpuLoad, VramUsed = (long)(2.3 * GB), VramTotal = 8 * GB,
                    LoadSource = "Windows counters", VramSource = "Windows counters", Note = "LibreHardwareMonitor found this GPU but the driver offers no temperature sensor."
                }
                : new GpuReading
                {
                    Name = "NVIDIA GeForce RTX 3060", TempC = gpuTemp, Load = gpuLoad, PowerW = 34,
                    VramUsed = (long)(1.7 * GB), VramTotal = 12 * GB, FanPct = 30, ClockMhz = 1320
                },
            Mem = mem,
            Drives = new[]
            {
                new DriveStat { Letter = "C", Label = "System", Total = 476 * GB, Free = 268 * GB },
                new DriveStat { Letter = "D", Label = "Data", Total = 931 * GB, Free = 695 * GB },
            },
            Uptime = TimeSpan.FromHours(54.5),
        };
    }

    /// <summary>A believable temperature history for the sparklines in the flyout.</summary>
    public static List<Telemetry> History(int n = 150)
    {
        var rnd = new Random(7);
        double c = 52, g = 46;
        var list = new List<Telemetry>();
        for (int i = 0; i < n; i++)
        {
            c = Math.Clamp(c + (rnd.NextDouble() - 0.46) * 4, 44, 71);
            g = Math.Clamp(g + (rnd.NextDouble() - 0.48) * 2.4, 41, 58);
            list.Add(Reading(c, 18 + rnd.NextDouble() * 22, g, 6 + rnd.NextDouble() * 12));
        }
        return list;
    }

    // ---- apps (flyout, memory, startup) ------------------------------------------------------------------------------
    static AppGroup App(string name, double gb, int count, bool prot = false)
        => new() { Key = name.ToLowerInvariant(), Name = name, ExePath = "demo:" + name, PrivateBytes = (long)(gb * GB), Commit = (long)(gb * 1.2 * GB), Count = count, Protected = prot };

    public static List<AppGroup> Groups() => new()
    {
        App("Web browser", 3.9, 38), App("Video editor", 2.4, 6), App("Chat app", 1.1, 7), App("Code editor", 0.95, 9),
        App("Music player", 0.62, 5), App("Photo editor", 0.52, 2), App("Notes", 0.41, 4), App("Cloud sync", 0.33, 3),
        App("Windows shell", 0.28, 4, prot: true), App("Mail client", 0.24, 3), App("PDF reader", 0.18, 1), App("Calculator", 0.05, 1),
    };

    public static List<PoolTag> PoolTags() => new()
    {
        new("FMfn", 210 * MB, 0, "File-system filter name cache (Windows remembers file names it has seen; grows with big file scans)"),
        new("Ntff", 160 * MB, 0, "NTFS file control blocks (cached file records)"),
        new("Dxgk", 0, 120 * MB, "Graphics driver (DirectX kernel)"),
        new("File", 84 * MB, 0, "Open file objects"),
        new("Proc", 61 * MB, 0, "Process objects"),
        new("Mdl ", 0, 44 * MB, "Memory descriptor lists (drivers)"),
    };

    public static List<StartupEntry> Startup() => new()
    {
        Entry("Chat app", "Your account (Run)", 410, 7, true), Entry("Cloud sync", "Your account (Run)", 190, 3, true),
        Entry("Music player", "Your account (Run)", 160, 5, true), Entry("Game launcher", "Your account (Run)", 120, 2, false),
        Entry("Driver control panel", "All users (Run)", 85, 1, true), Entry("Printer status", "All users (Run)", 0, 0, false),
        Entry("Updater", "Your Startup folder", 0, 0, true),
    };

    static StartupEntry Entry(string name, string source, long mb, int procs, bool on)
        => new()
        {
            Name = name, Command = "\"" + name + ".exe\" --minimized", ExePath = "demo:" + name, Source = source, Enabled = on, CanChange = true,
            RamBytes = mb * MB, ProcessCount = procs, Publisher = "Sample Software"
        };

    // ---- audio -----------------------------------------------------------------------------------------------------
    public static List<AudioDevice> AudioDevices() => new()
    {
        new() { Id = "d1", Name = "Speakers", IsDefault = true },
        new() { Id = "d2", Name = "Headphones" },
        new() { Id = "d3", Name = "Monitor audio" },
    };

    public static List<AudioSession> AudioSessions() => new()
    {
        new() { Pid = 1, Name = "Music player", ExePath = "demo:Music player", Volume = 1f, Active = true },
        new() { Pid = 2, Name = "Web browser", ExePath = "demo:Web browser", Volume = 1f },
        new() { Pid = 3, Name = "Chat app", ExePath = "demo:Chat app", Volume = 0.62f },
        new() { Pid = 4, Name = "System sounds", Volume = 1f, IsSystemSounds = true },
    };

    // ---- drive health ----------------------------------------------------------------------------------------------
    public static List<DriveReport> Health()
    {
        var list = HealthyDrives();
        if (!Problem) return list;
        var ssd = list[0]; var hdd = list[1];
        ssd.CrcErrors = 786451; ssd.Attrs.First(a => a.Id == 199).Raw = 786451;
        hdd.Reallocated = 296; hdd.Attrs.First(a => a.Id == 5).Raw = 296;
        return list;
    }

    /// <summary>Remembered numbers eleven days old that match <see cref="Health"/> in problem mode: the damage has not grown since.</summary>
    public static void SeedHealthBaseline()
    {
        var then = DateTime.Now.AddDays(-11);
        var store = new BaselineStore();
        store.Drives["Sample SATA SSD 1TB"] = new DriveBaseline { CrcErrors = 786451, CrcSince = then, LifeLeftPct = 97, Since = then };
        store.Drives["Sample hard disk 2TB"] = new DriveBaseline { Reallocated = Worse ? 290 : 296, CrcErrors = 0, CrcSince = then, Since = then };
        JsonStore.Save("drivehealth.json", store);
    }

    static List<DriveReport> HealthyDrives()
    {
        SmartAttr A(int id, string name, int v, int w, int t, long raw) => new() { Id = id, Name = name, Value = v, Worst = w, Threshold = t, Raw = raw };
        return new()
        {
            new DriveReport
            {
                Number = 0, Model = "Sample SATA SSD 1TB", Firmware = "1.02", Bus = "SATA", IsSsd = true, SizeBytes = 1000L * 1000 * 1000 * 1000,
                Letters = new() { "C:" }, TempC = 34, LifeLeftPct = 97, LifeSource = "Wear-levelling count (attribute 177)",
                Reallocated = 0, Pending = 0, Uncorrectable = 0, CrcErrors = 0, PowerOnHours = 4180, WrittenTb = 18.4,
                State = HealthState.Good, Method = "ATA SMART",
                Attrs = new()
                {
                    A(5, "Reallocated sectors", 100, 100, 10, 0), A(9, "Power-on hours", 99, 99, 0, 4180), A(12, "Power cycles", 99, 99, 0, 612),
                    A(177, "Wear-levelling count", 97, 97, 0, 61), A(194, "Temperature", 66, 52, 0, 34), A(197, "Pending sectors", 100, 100, 0, 0),
                    A(199, "Interface CRC errors", 100, 100, 0, 0), A(241, "Total host writes (GB)", 99, 99, 0, 18841)
                }
            },
            new DriveReport
            {
                Number = 1, Model = "Sample hard disk 2TB", Firmware = "CC45", Bus = "SATA", IsSsd = false, SizeBytes = 2000L * 1000 * 1000 * 1000,
                Letters = new() { "D:" }, TempC = 31, Reallocated = 0, Pending = 0, Uncorrectable = 0, CrcErrors = 0, PowerOnHours = 15900,
                State = HealthState.Good, Method = "ATA SMART",
                Attrs = new()
                {
                    A(1, "Raw read error rate", 100, 100, 16, 0), A(5, "Reallocated sectors", 100, 100, 5, 0), A(9, "Power-on hours", 82, 82, 0, 15900),
                    A(194, "Temperature", 69, 42, 0, 31), A(197, "Pending sectors", 100, 100, 0, 0), A(198, "Uncorrectable sectors", 100, 100, 0, 0)
                }
            },
        };
    }

    // ---- sessions (written into the demo state folder, then read by the normal code) -----------------------------------
    public static void SeedSessions()
    {
        var now = DateTime.Now;
        PingBrief P(double avg, double max, double loss, int spikes, int lost = 0, DateTime? maxAt = null) => new() { Avg = avg, Max = max, LossPct = loss, Spikes = spikes, Count = 1200, Lost = lost, MaxAt = maxAt };
        var items = new List<SessionEntry>
        {
            new()
            {
                Game = "Starfall Arena", Start = now.Date.AddHours(Math.Min(19, Math.Max(0, now.Hour - 2))), DurationSec = 5 * 3600 / 4 + 420,
                CpuTempPeak = 71, CpuTempAvg = 62, GpuTempPeak = 68, GpuTempAvg = 63, CpuLoadPeak = 78, CpuLoadAvg = 41, GpuLoadPeak = 99, GpuLoadAvg = 91,
                RamPeakGb = 14.2, RamPeakPct = 44, VramPeakGb = 6.8, Router = P(2, 11, 0, 0), Internet = P(17, 64, 0, 0),
                GameModeLog = new() { "20:14 slowed video encoder, 3D tool: CPU 92%", "20:31 restored 2 jobs: the load dropped" }, BackgroundLowered = 2
            },
            new()
            {
                Game = "Pixel Quest", Start = now.Date.AddDays(-1).AddHours(21), DurationSec = 2 * 3600 + 900,
                CpuTempPeak = 83, CpuTempAvg = 74, GpuTempPeak = 79, GpuTempAvg = 72, CpuLoadPeak = 96, CpuLoadAvg = 63, GpuLoadPeak = 99, GpuLoadAvg = 97,
                RamPeakGb = 21.6, RamPeakPct = 68, VramPeakGb = 9.9, Router = P(3, 18, 0, 0), Internet = P(29, 310, 1.8, 14, 22, now.Date.AddDays(-1).AddHours(21).AddMinutes(47)),
            },
            new()
            {
                Game = "Orbit Racing", Start = now.Date.AddDays(-3).AddHours(18), DurationSec = 3600 + 1500,
                CpuTempPeak = 96, CpuTempAvg = 88, GpuTempPeak = 84, GpuTempAvg = 80, CpuLoadPeak = 100, CpuLoadAvg = 88, GpuLoadPeak = 100, GpuLoadAvg = 99,
                CpuThrottled = true, GpuThrottled = true, GpuThrottleSec = 95, RamPeakGb = 18.1, RamPeakPct = 56, VramPeakGb = 7.4,
                Router = P(2, 9, 0, 0), Internet = P(21, 88, 0, 0), Alerts = new() { "CPU is very hot" }
            },
        };
        foreach (var e in items) e.End = e.Start.AddSeconds(e.DurationSec);
        JsonStore.Save("sessions.json", new SessionStore { Items = items });
    }

    // ---- network ----------------------------------------------------------------------------------------------------
    public static List<PingSample> Pings()
    {
        var rnd = new Random(11);
        var list = new List<PingSample>();
        var t0 = DateTime.Now.AddMinutes(-10);
        for (int i = 0; i < 200; i++)
        {
            double? router = 1.2 + rnd.NextDouble() * 1.8;
            double? net = 15 + rnd.NextDouble() * 6 + (i % 47 == 0 ? 8 : 0);
            if (i == 61) router = null;                  // one lost packet
            if (i == 94) net = 212;
            if (i == 133) net = 128;
            if (i == 151) net = 1840;                    // far above the 500 ms top of the chart
            list.Add(new PingSample(t0.AddSeconds(i * 3), router, net));
        }
        return list;
    }

    // ---- disk cleaner -----------------------------------------------------------------------------------------------
    public static List<CategoryResult> CleanResults(CleanContext ctx)
    {
        string root = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + @"\Demo";
        var cats = Categories.All().ToDictionary(c => c.Id);
        CleanItem I(string cat, string display, double gb, int daysOld = 40, int files = 800)
            => new()
            {
                Category = cat, Path = System.IO.Path.Combine(root, cat, display.Replace(':', '_').Replace(' ', '_')), Display = display, IsDirectory = true,
                Bytes = (long)(gb * GB), Files = files, Newest = DateTime.UtcNow.AddDays(-daysOld), Mode = cats[cat].Mode
            };
        CategoryResult R(string cat, params CleanItem[] items)
        {
            var r = new CategoryResult { Category = cats[cat] };
            r.Items.AddRange(items.OrderByDescending(i => i.Bytes));
            return r;
        }
        var wu = I("windows-managed", "Windows Update downloads", 4.2);
        wu.Denied = "managed by Windows: use Windows Disk Cleanup";
        return new()
        {
            R("user-temp", I("user-temp", @"Temp\setup-cache", 2.3), I("user-temp", @"Temp\render-scratch", 1.6, 2), I("user-temp", @"Temp\unpacked-installer", 0.9), I("user-temp", @"Temp\logs-0412", 0.2), I("user-temp", @"Temp\thumbnails", 0.1)),
            R("browser-cache", I("browser-cache", "Chrome Default: Cache", 1.8, 1), I("browser-cache", "Edge Default: Cache", 0.7, 3), I("browser-cache", "Firefox (profile): cache2", 0.4, 4)),
            R("dev-caches", I("dev-caches", "NuGet packages", 3.1), I("dev-caches", "npm cache", 1.4), I("dev-caches", "pip cache", 0.6)),
            R("shader-caches", I("shader-caches", "NVIDIA DXCache", 2.2), I("shader-caches", "DirectX shader cache", 0.5)),
            R("crash-dumps", I("crash-dumps", "CrashDumps", 0.8)),
            R("windows-managed", wu, I("windows-managed", "Windows setup logs (Panther)", 0.1)),
        };
    }

    public static DirNode DiskTree()
    {
        DirNode D(string name, DirNode? parent, params (string name, double gb)[] files)
        {
            var n = new DirNode { Name = name, FullPath = parent == null ? name : System.IO.Path.Combine(parent.FullPath, name), Parent = parent, Newest = DateTime.UtcNow.AddDays(-9) };
            foreach (var (fn, g) in files) { long b = (long)(g * GB); n.OwnBytes += b; n.Files++; n.BigFiles.Add((fn, b)); }
            n.Files += 40 + (long)(files.Sum(f => f.gb) * 30);
            n.OwnBytes += 120 * MB;
            n.Size = n.OwnBytes;
            return n;
        }
        DirNode Add(DirNode parent, DirNode child)
        {
            parent.Children.Add(child); parent.Size += child.Size; parent.Files += child.Files; parent.Dirs += child.Dirs + 1;
            return child;
        }
        var root = D(@"D:\", null);
        var games = D("Games", root);
        foreach (var (n, a, b) in new[] { ("Starfall Arena", 41.0, 6.0), ("Pixel Quest", 28.0, 4.0), ("Orbit Racing", 22.0, 3.0), ("Dungeon Tales", 17.0, 2.0), ("Puzzle Pack", 6.0, 1.0) })
        {
            var g = D(n, games, ("content.pak", a), ("textures.pak", b));
            Add(games, g);
        }
        var videos = D("Videos", root);
        foreach (var (n, g) in new[] { ("Holiday clips", 22.0), ("Screen recordings", 17.0), ("Exports", 11.0), ("Raw footage", 8.0) })
            Add(videos, D(n, videos, ("clip-01.mp4", g * 0.6), ("clip-02.mp4", g * 0.35)));
        var projects = D("Projects", root);
        foreach (var (n, g) in new[] { ("website", 4.2), ("mobile-app", 3.1), ("data-tools", 2.4), ("scripts", 0.4) })
            Add(projects, D(n, projects, ("build-cache.bin", g)));
        var backups = D("Backups", root, ("laptop-image.img", 17.5));
        var downloads = D("Downloads", root, ("installer.iso", 5.6), ("dataset.zip", 4.1), ("archive.7z", 2.2));
        var music = D("Music", root, ("library.flac", 8.9));
        foreach (var c in new[] { games, videos, projects, backups, downloads, music }) Add(root, c);
        root.Children.Sort((a, b) => b.Size.CompareTo(a.Size));
        return root;
    }

    public static string[] DriveChoices() => new[] { @"C:\   System  (208 GB of 476 GB used)", @"D:\   Data  (236 GB used of 931 GB)" };
}

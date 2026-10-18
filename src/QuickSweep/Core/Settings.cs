using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace QuickSweep.Core;

/// <summary>A folder pattern the disk cleaner should also look at, with a label for the list.</summary>
public sealed class ScanFolder
{
    /// <summary>Folder pattern. <c>*</c> and <c>?</c> work inside one folder name; <c>%ENV%</c> variables are expanded.
    /// Example: <c>D:\projects\*\build</c>. Every folder that matches becomes one item in the cleaner.</summary>
    public string Pattern { get; set; } = "";
    /// <summary>Short name shown in the list, for example "Build output".</summary>
    public string Label { get; set; } = "";
    /// <summary>Only offer a match when nothing inside it has changed for this many days (0 = no such condition).</summary>
    public int IdleDays { get; set; }

    public override string ToString() => string.IsNullOrWhiteSpace(Label) ? Pattern : $"{Pattern} | {Label}" + (IdleDays > 0 ? $" | {IdleDays}" : "");

    /// <summary>Reads "pattern | label | idle days" (the last two parts are optional).</summary>
    public static ScanFolder? Parse(string line)
    {
        var parts = line.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0])) return null;
        var f = new ScanFolder { Pattern = parts[0], Label = parts.Length > 1 ? parts[1] : "" };
        if (parts.Length > 2 && int.TryParse(parts[2], out int d) && d is > 0 and <= 3650) f.IdleDays = d;
        return f;
    }
}

public sealed class AppSettings
{
    // general
    public bool GameModeAuto { get; set; } = true;
    public bool NotifyAfterGame { get; set; } = true;
    public bool AlertsEnabled { get; set; } = true;
    public bool PingEnabled { get; set; } = true;
    public string? TrayPromotedFor { get; set; }   // exe path whose tray icon was already set to "show in the taskbar" (done once per install)

    // Game Mode: while a game runs, slow background work (priority only) when the game actually needs the room.
    // GameModeAuto is the master switch (on by default). Everything below has a safe default; see the README.
    public bool NotifyGameMode { get; set; } = true;        // one quiet notification per game when it kicks in
    public int GameCpuPct { get; set; } = 85;               // total CPU at or above this, sustained
    public int GameGpuPct { get; set; } = 95;               // GPU load at or above this, sustained
    public double GameMinFreeRamGb { get; set; } = 2.0;     // available RAM below this, sustained
    public int GameCommitPct { get; set; } = 93;            // commit charge at or above this (with little RAM left)
    public int GameReadyPct { get; set; } = 25;             // share of the game's runnable threads waiting for a core (0 = ignore)
    public int GameTriggerSeconds { get; set; } = 12;       // pressure must last about this long
    public int GameReleaseSeconds { get; set; } = 90;       // calm for this long before the jobs are restored
    public int GameCooldownSeconds { get; set; } = 120;     // after a restore, wait before slowing them again
    public int GameMinContributionPct { get; set; } = 5;    // background work must account for this much CPU or GPU
    public bool GameUseBelowNormal { get; set; }            // false: Idle priority, true: Below Normal

    // alert thresholds
    public int CpuAlertC { get; set; } = 90;
    public int GpuAlertC { get; set; } = 83;
    public int LowSpaceGb { get; set; } = 10;
    // kernel memory: alert only when the pool is large AND the PC is short of memory
    public double PoolAlertGb { get; set; } = 6.0;              // a pool at or above this counts as large
    public double PoolPressureFreeRamGb { get; set; } = 2.0;    // ... and available RAM is under this
    public int PoolPressureCommitPct { get; set; } = 90;        // ... or commit charge is above this share of the limit

    // game detection: extra folders whose executables count as games (Steam, Epic, GOG, Riot, Ubisoft, EA and Xbox are built in)
    public List<string> ExtraGameFolders { get; set; } = new();

    // Game Mode: what counts as background work (process names without .exe). Everything is editable in Settings.
    /// <summary>Encoders, 3D and slicer tools, build tools: slowed when the game needs the room.</summary>
    public List<string> BackgroundProcesses { get; set; } = new(DefaultBackgroundProcesses);
    /// <summary>Script runtimes (busy scripts and servers). They count only when they really use the CPU.</summary>
    public List<string> ScriptRuntimes { get; set; } = new(DefaultScriptRuntimes);
    /// <summary>AI coding agents: everything an agent started (its builds, renders, browsers) counts as background work.
    /// Matched against the program's file name and its product name.</summary>
    public List<string> AgentApps { get; set; } = new(DefaultAgentApps);

    public static readonly string[] DefaultBackgroundProcesses =
    {
        "ffmpeg", "handbrake", "handbrakecli", "x264", "x265", "magick",
        "blender", "prusa-slicer", "prusa-slicer-console", "prusaslicer", "bambu-studio", "orca-slicer", "cura",
        "msbuild", "vbcscompiler", "cmake", "ninja", "cargo", "rustc",
        "chrome-headless-shell", "headless_shell"
    };
    public static readonly string[] DefaultScriptRuntimes = { "node", "bun", "deno", "python", "pythonw", "python3", "py" };
    public static readonly string[] DefaultAgentApps = { "claude", "codex" };

    // disk cleaner
    public bool CleanerDryRun { get; set; } = true;
    /// <summary>Extra folders the cleaner should look at (pattern + label). Empty by default.</summary>
    public List<ScanFolder> ExtraScanFolders { get; set; } = new();
    /// <summary>Folders you added to the built-in protection list (the cleaner never touches them or anything inside).
    /// A pattern may use <c>*</c> inside a folder name and <c>**</c> for "any depth".</summary>
    public List<string> ProtectedFolders { get; set; } = new();

    // Settings files of earlier versions: read once, merged into the lists above, never written back.
    [JsonPropertyName("ExtraDenyPatterns"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyDenyPatterns { get; set; }
    [JsonPropertyName("ExtraHeavyProcesses"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyHeavyProcesses { get; set; }

    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static event Action? Changed;

    public static AppSettings Load()
    {
        try
        {
            var text = StateIo.ReadText("settings.json");
            if (text != null) return Parse(text) ?? new();
        }
        catch (Exception e) { Log.Write("settings load failed: " + e.Message); }
        var s = new AppSettings();
        s.Save();
        return s;
    }

    /// <summary>Parses a settings file and folds in the keys of earlier versions.</summary>
    public static AppSettings? Parse(string json)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(json, Opt);
        s?.MigrateLegacy();
        return s;
    }

    void MigrateLegacy()
    {
        if (LegacyDenyPatterns is { Count: > 0 })
            foreach (var p in LegacyDenyPatterns) if (!ProtectedFolders.Contains(p, StringComparer.OrdinalIgnoreCase)) ProtectedFolders.Add(p);
        if (LegacyHeavyProcesses is { Count: > 0 })
            foreach (var p in LegacyHeavyProcesses) if (!BackgroundProcesses.Contains(p, StringComparer.OrdinalIgnoreCase)) BackgroundProcesses.Add(p);
        LegacyDenyPatterns = null; LegacyHeavyProcesses = null;
    }

    /// <summary>Writes the settings to a file chosen by the user ("Export" in Settings).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Opt);

    /// <summary>Overlays the keys found in <paramref name="json"/> on these settings ("Import" in Settings). Keys that are
    /// missing in the file keep their value. Returns the number of keys applied, or -1 when the file is not usable.</summary>
    public static int Import(AppSettings current, string json, out AppSettings result)
    {
        result = current;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject incoming) return -1;
            var merged = JsonNode.Parse(current.ToJson())!.AsObject();
            int n = 0;
            foreach (var kv in incoming)
            {
                if (kv.Key.Equals("TrayPromotedFor", StringComparison.OrdinalIgnoreCase)) continue;   // belongs to the installation
                merged.Remove(merged.Select(m => m.Key).FirstOrDefault(k => k.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)) ?? kv.Key);
                merged[kv.Key] = kv.Value?.DeepClone();
                n++;
            }
            var parsed = Parse(merged.ToJsonString());
            if (parsed == null) return -1;
            parsed.TrayPromotedFor = current.TrayPromotedFor;
            result = parsed;
            return n;
        }
        catch { return -1; }
    }

    /// <summary>Copies every setting from <paramref name="other"/> into this live instance (the monitor keeps a reference to it).</summary>
    public void CopyFrom(AppSettings other)
    {
        foreach (var prop in typeof(AppSettings).GetProperties())
            if (prop.CanRead && prop.CanWrite && prop.GetCustomAttributes(typeof(JsonIgnoreAttribute), false).Length == 0)
                prop.SetValue(this, prop.GetValue(other));
    }

    /// <summary>Writes settings.json through the state-folder rules (no-follow, verified). False when it could not be written.</summary>
    public bool Save()
    {
        bool ok = false;
        try
        {
            ok = StateIo.WriteText("settings.json", JsonSerializer.Serialize(this, Opt));
            if (!ok) Log.Write("settings not saved: " + StateIo.LastWhy);
        }
        catch (Exception e) { Log.Write("settings save failed: " + e.Message); }
        Changed?.Invoke();
        return ok;
    }

    /// <summary>For the installer-side import: the current settings, or defaults when there is no file yet, but never defaults
    /// in place of a file that exists and cannot be read or parsed (null, with the reason): an import must not wipe it.</summary>
    public static AppSettings? TryLoadForImport(out string why)
    {
        why = "";
        var text = StateIo.ReadText("settings.json");
        if (text == null)
        {
            if (StateIo.LastWhy.Length > 0) { why = "settings.json cannot be read: " + StateIo.LastWhy; return null; }
            return new AppSettings();                      // no file yet
        }
        try { return Parse(text) ?? new AppSettings(); }
        catch (Exception e) { why = "settings.json is not valid JSON: " + e.Message; return null; }
    }
}

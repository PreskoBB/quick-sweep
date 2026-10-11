using System.Text.Json;
using System.Text.RegularExpressions;
using QuickSweep.Native;

namespace QuickSweep.Core;

public sealed class GameInfo
{
    public string Name = "";
    public string ExeName = "";
    public string? Path;
    public int Pid;
    public long CreateTime;
    public DateTime Started = DateTime.Now;
    public bool ByFullscreen;
}

/// <summary>
/// Detects a running game: (1) a visible-window process whose exe lives under a known game folder
/// (Steam/Epic/Riot/EA/Ubisoft/GOG/Xbox + user folders), or (2) an unknown app holding the foreground in true
/// fullscreen with the GPU busy for ~15 s. A game session ends when its process exits.
/// </summary>
public sealed class GameWatcher
{
    static readonly string[] RootMarkers =
    {
        @"\steamapps\common\", @"\epic games\", @"\riot games\", @"\ea games\", @"\electronic arts\",
        @"\ubisoft game launcher\games\", @"\ubisoft\ubisoft game launcher\games\", @"\gog galaxy\games\", @"\gog games\",
        @"\xboxgames\", @"\rockstar games\", @"\battle.net\games\", @"\origin games\"
    };

    // helper / launcher / anti-cheat executables that live in game folders but are not the game
    static readonly string[] IgnoreNames =
    {
        "unitycrashhandler", "crashreportclient", "easyanticheat", "eacefsubprocess", "beservice", "battleye", "vgc", "vgtray",
        "riotclient", "leagueclient", "epicgameslauncher", "epicwebhelper", "steam", "steamwebhelper", "steamservice",
        "gameoverlayui", "eosoverlayrenderer", "unrealcefsubprocess", "qtwebengineprocess", "vc_redist", "vcredist",
        "dxsetup", "dotnetfx", "setup", "uninstall", "unins000", "installer", "overwolf", "cefsubprocess", "crashhandler",
        "launcher", "bootstrapper", "ubisoftconnect", "upc", "eadesktop", "origin", "galaxyclient", "faceit", "faceitclient"
    };

    static readonly string[] FullscreenIgnore =
    {
        "explorer", "firefox", "chrome", "msedge", "opera", "brave", "vivaldi", "vlc", "mpv", "potplayer", "potplayermini64",
        "wmplayer", "powerpnt", "mstsc", "applicationframehost", "discord", "slack", "teams", "zoom", "obs64", "claude",
        "code", "devenv", "photoshop", "netflix", "spotify", "searchhost", "shellexperiencehost", "lockapp"
    };

    readonly AppSettings _s;
    GameInfo? _current;
    int _fsPid; int _fsTicks; double _fsGpuSum;
    readonly Dictionary<string, string> _steamNames = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> _epic = new(StringComparer.OrdinalIgnoreCase);
    DateTime _epicLoaded = DateTime.MinValue;

    public GameInfo? Current => _current;
    public event Action<GameInfo>? Started;
    public event Action<GameInfo>? Ended;

    public GameWatcher(AppSettings s) { _s = s; }

    public void Tick(List<ProcInfo> procs, double gpuLoad)
    {
        // currently tracking: stay until the process is gone
        if (_current != null)
        {
            bool alive = procs.Any(p => p.Pid == _current.Pid && p.CreateTime == _current.CreateTime);
            if (!alive)
            {
                var g = _current; _current = null; _fsPid = 0; _fsTicks = 0;
                Ended?.Invoke(g);
            }
            return;
        }

        var candidates = KnownGameCandidates(procs);
        if (candidates is { } known)
        {
            Begin(known, false);
            return;
        }
        var fs = FullscreenCandidate(procs, gpuLoad);
        if (fs is { } full) Begin(full, true);
    }

    void Begin(ProcInfo p, bool fullscreen)
    {
        var path = ProcessInsight.PathOf(p);
        var gi = new GameInfo
        {
            Pid = p.Pid, CreateTime = p.CreateTime, Path = path, ExeName = p.Name,
            Name = ResolveName(path, p.Name), ByFullscreen = fullscreen, Started = DateTime.Now
        };
        _current = gi;
        Log.Write($"game started: {gi.Name} ({gi.ExeName}, pid {gi.Pid}, fullscreen-detected={fullscreen})");
        Started?.Invoke(gi);
    }

    ProcInfo? KnownGameCandidates(List<ProcInfo> procs)
    {
        HashSet<int>? windowed = null;
        ProcInfo? best = null;
        var extra = _s.ExtraGameFolders;
        foreach (var p in procs)
        {
            if (p.Pid <= 4 || p.PrivateWs < 150L * 1024 * 1024) continue; // real games are big; launchers/helpers are small
            var path = ProcessInsight.PathOf(p);
            if (path == null) continue;
            if (!IsGamePath(path, extra)) continue;
            var stem = System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (IgnoreNames.Any(i => stem == i || stem.StartsWith(i + "-") || stem.StartsWith(i + "_") || stem.Contains("crashhandler") || stem.Contains("anticheat")))
                continue;
            windowed ??= WindowPids();
            if (!windowed.Contains(p.Pid)) continue;
            if (best == null || p.PrivateWs > best.Value.PrivateWs) best = p;
        }
        return best;
    }

    bool IsGamePath(string path, List<string> extra)
    {
        var low = path.ToLowerInvariant();
        foreach (var m in RootMarkers) if (low.Contains(m)) return true;
        foreach (var f in extra)
            if (!string.IsNullOrWhiteSpace(f) && low.StartsWith(f.TrimEnd('\\').ToLowerInvariant() + "\\")) return true;
        RefreshEpic();
        foreach (var loc in _epic.Keys) if (low.StartsWith(loc)) return true;
        return false;
    }

    void RefreshEpic()
    {
        if (DateTime.Now - _epicLoaded < TimeSpan.FromMinutes(2)) return;
        _epicLoaded = DateTime.Now;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (Directory.Exists(dir))
                foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
                {
                    try
                    {
                        // read without following links, small files only (these folders are not ours)
                        if (SafeIo.TryReadSmall(f, 262144, out var itemText, out _) != ReadResult.Ok) continue;
                        using var doc = JsonDocument.Parse(itemText);
                        var r = doc.RootElement;
                        string? loc = r.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;
                        string? name = r.TryGetProperty("DisplayName", out var d) ? d.GetString() : null;
                        if (!string.IsNullOrEmpty(loc))
                            dict[loc.TrimEnd('\\').ToLowerInvariant() + "\\"] = name ?? "";
                    }
                    catch { }
                }
        }
        catch { }
        _epic = dict;
    }

    string ResolveName(string? path, string exe)
    {
        if (path != null)
        {
            var low = path.ToLowerInvariant();
            // Epic manifest
            RefreshEpic();
            foreach (var kv in _epic) if (low.StartsWith(kv.Key) && kv.Value != "") return kv.Value;
            // Steam appmanifest
            int i = low.IndexOf(@"\steamapps\common\", StringComparison.Ordinal);
            if (i >= 0)
            {
                string steamapps = path.Substring(0, i + @"\steamapps".Length);
                string rest = path.Substring(i + @"\steamapps\common\".Length);
                string installDir = rest.Split('\\')[0];
                var n = SteamName(steamapps, installDir);
                if (n != null) return n;
                return installDir;
            }
            try
            {
                var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                var pn = vi.ProductName?.Trim();
                if (!string.IsNullOrEmpty(pn) && !pn.StartsWith("Unreal", StringComparison.OrdinalIgnoreCase) && pn.Length < 50) return pn;
                if (!string.IsNullOrWhiteSpace(vi.FileDescription)) return vi.FileDescription.Trim();
            }
            catch { }
        }
        return exe.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
    }

    string? SteamName(string steamapps, string installDir)
    {
        var key = steamapps + "|" + installDir;
        if (_steamNames.TryGetValue(key, out var cached)) return cached;
        try
        {
            foreach (var f in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                if (SafeIo.TryReadSmall(f, 262144, out var txt, out _) != ReadResult.Ok) continue;
                var idm = Regex.Match(txt, "\"installdir\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
                if (!idm.Success || !idm.Groups[1].Value.Equals(installDir, StringComparison.OrdinalIgnoreCase)) continue;
                var nm = Regex.Match(txt, "\"name\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
                if (nm.Success) { _steamNames[key] = nm.Groups[1].Value; return nm.Groups[1].Value; }
            }
        }
        catch { }
        return null;
    }

    ProcInfo? FullscreenCandidate(List<ProcInfo> procs, double gpuLoad)
    {
        var hwnd = Win.GetForegroundWindow();
        if (hwnd == 0) { _fsPid = 0; _fsTicks = 0; return null; }
        Win.GetWindowThreadProcessId(hwnd, out int pid);
        var p = procs.FirstOrDefault(x => x.Pid == pid);
        if (p.Pid == 0 || !IsTrueFullscreen(hwnd)) { _fsPid = 0; _fsTicks = 0; return null; }
        var stem = p.Name.Replace(".exe", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        if (FullscreenIgnore.Contains(stem)) { _fsPid = 0; _fsTicks = 0; return null; }
        if (pid != _fsPid) { _fsPid = pid; _fsTicks = 0; _fsGpuSum = 0; }
        _fsTicks++; _fsGpuSum += gpuLoad;
        // ~3 ticks of 5 s with the GPU actually working
        if (_fsTicks >= 3 && _fsGpuSum / _fsTicks >= 20) return p;
        return null;
    }

    static bool IsTrueFullscreen(nint hwnd)
    {
        if (Win.IsIconic(hwnd) || !Win.GetWindowRect(hwnd, out var r)) return false;
        var mon = Win.MonitorFromWindow(hwnd, 2);
        var mi = new Win.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win.MONITORINFO>() };
        if (!Win.GetMonitorInfoW(mon, ref mi)) return false;
        bool covers = r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        if (!covers) return false;
        long style = (long)Win.GetWindowLongPtr(hwnd, -16);
        return (style & 0x00C00000) != 0x00C00000; // no caption
    }

    /// <summary>pids that own a visible, uncloaked top-level window of game-like size.</summary>
    static HashSet<int> WindowPids()
    {
        var set = new HashSet<int>();
        Win.EnumWindows((h, _) =>
        {
            if (!Win.IsWindowVisible(h)) return true;
            if (Win.DwmGetWindowAttribute(h, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;
            if (!Win.GetWindowRect(h, out var r)) return true;
            if (r.Width < 640 || r.Height < 360) return true;
            Win.GetWindowThreadProcessId(h, out int pid);
            set.Add(pid);
            return true;
        }, 0);
        return set;
    }
}

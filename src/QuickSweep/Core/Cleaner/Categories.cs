using System.Text.RegularExpressions;

namespace QuickSweep.Core.Cleaner;

/// <summary>The catalogue of things the cleaner knows how to find, and why each one is safe.</summary>
public static class Categories
{
    static string J(params string[] p) => Path.Combine(p);

    internal static IEnumerable<string> SubDirs(string path, string pattern) => Dirs(path, pattern);

    static IEnumerable<string> Dirs(string path, string pattern = "*")
    {
        try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path, pattern).ToList() : new List<string>(); }
        catch { return new List<string>(); }
    }

    public static List<CleanCategory> All() => new()
    {
        new()
        {
            Id = "user-temp", Group = "Windows", Title = "User temporary files",
            What = "Scratch files that programs leave in your Temp folder (installer unpacking, pip downloads, crash leftovers).",
            WhySafe = "Programs treat Temp as disposable. Files that are in use are locked by Windows and simply skipped.",
            Regen = "Nothing to regenerate; programs recreate what they need.",
            Roots = c => new[] { new RootSpec(c.Temp, "Temp", true) }
        },
        new()
        {
            Id = "win-temp", Group = "Windows", Title = "Windows temporary files", NeedsAdmin = true,
            What = "The system-wide Temp folder (C:\\Windows\\Temp), used by services and installers.",
            WhySafe = "Same as user Temp: disposable scratch space. In-use files are skipped.",
            Regen = "Nothing.",
            Roots = c => new[] { new RootSpec(J(c.Windows, "Temp"), "Windows\\Temp", true) }
        },
        new()
        {
            Id = "browser-cache", Group = "Browsers", Title = "Browser caches",
            What = "Downloaded page pieces (images, scripts) that browsers keep so sites load faster. Only cache folders: no passwords, history, bookmarks or logins.",
            WhySafe = "Browsers rebuild their cache automatically. Close the browser first, otherwise locked files are skipped.",
            Regen = "Pages load a little slower the first time you revisit them.",
            Roots = BrowserCaches
        },
        new()
        {
            Id = "dev-caches", Group = "Developer", Title = "Package-manager caches (npm, pip, uv, NuGet, Gradle)",
            What = "Copies of libraries that package managers downloaded earlier so installs are fast and work offline.",
            WhySafe = "They are pure caches: the next install downloads what it needs again.",
            Regen = "The next install or build re-downloads (pip, uv, npm, NuGet need internet; large ones can take minutes).",
            Roots = c => DevCaches(c)
        },
        new()
        {
            Id = "shader-caches", Group = "Graphics", Title = "GPU shader caches",
            What = "Compiled shaders that NVIDIA/DirectX store so games do not stutter while compiling them.",
            WhySafe = "The driver rebuilds the cache on demand. Nothing of yours is inside.",
            Regen = "Each game stutters slightly the first time while shaders recompile, then it is fast again.",
            Roots = c => new[]
            {
                new RootSpec(J(c.Local, "NVIDIA", "DXCache"), "NVIDIA DXCache", true),
                new RootSpec(J(c.Local, "NVIDIA", "GLCache"), "NVIDIA GLCache", true),
                new RootSpec(J(c.Local, "D3DSCache"), "DirectX shader cache", true),
                new RootSpec(J(c.Local, "AMD", "DxCache"), "AMD DxCache", true),
                new RootSpec(J(c.ProgramData, "NVIDIA Corporation", "NV_Cache"), "NVIDIA NV_Cache", true),
            }
        },
        new()
        {
            Id = "nvidia-installers", Group = "Graphics", Title = "NVIDIA installer leftovers", NeedsAdmin = true,
            What = "Driver and app installers that the NVIDIA app and GeForce Experience downloaded and never removed (update downloads and the extracted driver package).",
            WhySafe = "They are only used while installing. The NVIDIA app downloads a fresh copy when it needs one.",
            Regen = "The next driver update downloads again (a few hundred MB).",
            Roots = c => new[]
            {
                new RootSpec(J(c.ProgramData, "NVIDIA Corporation", "NVIDIA app", "UpdateFramework", "ota-artifacts"), "NVIDIA app update downloads", true),
                new RootSpec(J(c.ProgramData, "NVIDIA Corporation", "Downloader"), "GeForce Experience downloads", true),
                new RootSpec(J(c.SystemDrive, "NVIDIA"), "Extracted NVIDIA driver (C:\\NVIDIA)", true),
            }
        },
        new()
        {
            Id = "crash-dumps", Group = "Windows", Title = "Crash dumps and error reports",
            What = "Memory dumps and reports that Windows and apps write after a crash.",
            WhySafe = "Only useful to a developer debugging that exact crash.",
            Regen = "Nothing; a future crash writes a new one.",
            Roots = c => new[]
            {
                new RootSpec(J(c.Local, "CrashDumps"), "CrashDumps", true),
                new RootSpec(J(c.ProgramData, "Microsoft", "Windows", "WER", "ReportArchive"), "Windows error reports (archive)", true),
                new RootSpec(J(c.ProgramData, "Microsoft", "Windows", "WER", "ReportQueue"), "Windows error reports (queue)", true),
            }
        },
        new()
        {
            Id = "extra-folders", Group = "Your folders", Title = "Extra folders you listed in Settings", Mode = DeleteMode.Recycle,
            What = "Folders that match the patterns under \"Extra folders to scan\" in Settings (for example build output or caches of your own projects). Empty until you add a pattern.",
            WhySafe = "Only folders you listed are offered. Protected folders are still refused, anything changed in the last 7 days is not ticked automatically, and everything goes to the Recycle Bin so you can take it back.",
            Regen = "Depends on what you listed: rebuilding or re-downloading recreates most of it.",
            Roots = c => ExtraFolders(c)
        },
        new()
        {
            Id = "windows-managed", Group = "Managed by Windows", Title = "Windows Update leftovers, old Windows, setup logs", ManagedByWindows = true, NeedsAdmin = true,
            What = "Downloaded updates, the previous Windows installation (Windows.old) and setup logs. They sit in protected system locations.",
            WhySafe = "Quick Sweep only reports their size. Clean them with Windows' own Disk Cleanup, which handles permissions and services correctly.",
            Regen = "Deleting Windows.old removes the ability to roll back to the previous Windows version.",
            Roots = c => new[]
            {
                new RootSpec(J(c.Windows, "SoftwareDistribution", "Download"), "Windows Update downloads", false),
                new RootSpec(J(c.SystemDrive, "Windows.old"), "Previous Windows installation (Windows.old)", false),
                new RootSpec(J(c.SystemDrive, "$WINDOWS.~BT"), "Windows upgrade staging ($WINDOWS.~BT)", false),
                new RootSpec(J(c.Windows, "Panther"), "Windows setup logs (Panther)", false),
            }
        },
    };

    static IEnumerable<RootSpec> BrowserCaches(CleanContext c)
    {
        // Firefox
        foreach (var prof in Dirs(J(c.Local, "Mozilla", "Firefox", "Profiles")))
        {
            var cache = J(prof, "cache2");
            if (Directory.Exists(cache)) yield return new RootSpec(cache, $"Firefox ({Path.GetFileName(prof)})", false);
        }
        // Chromium family
        var bases = new (string name, string path)[]
        {
            ("Chrome", J(c.Local, "Google", "Chrome", "User Data")), ("Edge", J(c.Local, "Microsoft", "Edge", "User Data")),
            ("Brave", J(c.Local, "BraveSoftware", "Brave-Browser", "User Data")), ("Vivaldi", J(c.Local, "Vivaldi", "User Data")),
        };
        foreach (var (name, b) in bases)
            foreach (var prof in Dirs(b))
            {
                var pn = Path.GetFileName(prof);
                if (pn != "Default" && !pn.StartsWith("Profile ")) continue;
                foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
                {
                    var d = J(prof, sub);
                    if (Directory.Exists(d)) yield return new RootSpec(d, $"{name} {pn}: {sub}", false);
                }
            }
        foreach (var (name, path) in new[] { ("Opera", J(c.Local, "Opera Software", "Opera Stable")), ("Opera GX", J(c.Local, "Opera Software", "Opera GX Stable")) })
            foreach (var sub in new[] { "Cache", "Code Cache", "GPUCache" })
            {
                var d = J(path, sub);
                if (Directory.Exists(d)) yield return new RootSpec(d, $"{name}: {sub}", false);
            }
    }

    internal static IEnumerable<RootSpec> DevCaches(CleanContext c, Func<string, string?>? env = null)
    {
        string? Env(string n) => (env ?? Environment.GetEnvironmentVariable)(n);
        var list = new List<(string path, string name)>
        {
            (J(c.Local, "npm-cache"), "npm cache"),
            (J(c.Local, "pnpm", "store"), "pnpm store"),
            (J(c.Local, "Yarn", "Cache"), "Yarn cache"),
            (J(c.Local, "pip", "Cache"), "pip cache"),
            (J(c.Local, "uv", "cache"), "uv cache"),
            (J(c.Local, "NuGet", "v3-cache"), "NuGet HTTP cache"),
            (J(c.Local, "NuGet", "plugins-cache"), "NuGet plugins cache"),
            (J(c.Profile, ".nuget", "packages"), "NuGet packages"),
            (J(c.Profile, ".gradle", "caches"), "Gradle caches"),
        };
        // Cache folders named by environment variables: those variables are set by the user's own programs (or by anything running as
        // the user), so a folder is taken only when it is a plain, user-owned, non-system folder; otherwise it is dropped and logged.
        foreach (var (v, n) in new[] { ("PIP_CACHE_DIR", "pip cache (PIP_CACHE_DIR)"), ("UV_CACHE_DIR", "uv cache (UV_CACHE_DIR)"), ("NUGET_PACKAGES", "NuGet packages (NUGET_PACKAGES)"), ("npm_config_cache", "npm cache (npm_config_cache)") })
        {
            if (Env(v) is not { Length: > 0 } e) continue;
            var accepted = TrustedPaths.AcceptEnvCacheRoot(e, out var why);
            if (accepted != null) list.Add((accepted, n));
            else Log.Write($"cleaner: ignored {v}={e}: {why}");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, n) in list)
            if (Directory.Exists(p) && seen.Add(PathGuard.Normalize(p))) yield return new RootSpec(p, n, false);
    }

    static IEnumerable<RootSpec> ExtraFolders(CleanContext c)
    {
        foreach (var f in c.Settings.ExtraScanFolders)
        {
            if (string.IsNullOrWhiteSpace(f.Pattern)) continue;
            string label = string.IsNullOrWhiteSpace(f.Label) ? "Extra folder" : f.Label.Trim();
            foreach (var path in PatternScan.Expand(f.Pattern))
            {
                if (f.IdleDays > 0 && (DateTime.UtcNow - Measurer.Measure(path).Newest).TotalDays < f.IdleDays) continue;
                yield return new RootSpec(path, $"{label}: {PatternScan.Tail(f.Pattern, path)}", false);
            }
        }
    }
}

/// <summary>
/// Finds the folders that match a pattern such as <c>D:\projects\*\build</c>. <c>*</c> and <c>?</c> work inside one folder name,
/// <c>**</c> stands for "any number of folders" (down to <see cref="MaxDepth"/> levels). Never follows links, and never
/// descends into .git, node_modules, virtual environments or build caches while looking for a match.
/// </summary>
public static class PatternScan
{
    public const int MaxMatches = 300;
    public const int MaxDepth = 8;
    const int MaxVisited = 60000;

    static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    { ".git", "node_modules", ".venv", "venv", "__pycache__", ".next", ".cache", "$RECYCLE.BIN", "System Volume Information" };

    public static List<string> Expand(string pattern)
    {
        var result = new List<string>();
        try
        {
            pattern = TrustedPaths.Expand(pattern.Trim()).Replace('/', '\\');
            var segs = pattern.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length < 2 || segs[0].Length != 2 || segs[0][1] != ':' || !char.IsLetter(segs[0][0])) return result;   // must start with a drive
            if (segs.Any(x => x == "..") || segs[^1] == "**") return result;
            var level = new List<string> { segs[0] + "\\" };
            int visited = 0;
            for (int i = 1; i < segs.Length && level.Count > 0; i++)
            {
                var next = new List<string>();
                if (segs[i] == "**")
                {
                    var stack = new Stack<(string dir, int depth)>();
                    foreach (var d in level) { next.Add(d); stack.Push((d, 0)); }
                    while (stack.Count > 0 && visited < MaxVisited)
                    {
                        var (d, depth) = stack.Pop();
                        if (depth >= MaxDepth) continue;
                        foreach (var sub in Categories.SubDirs(d, "*"))
                        {
                            if (SkipNames.Contains(Path.GetFileName(sub)) || IsLink(sub)) continue;
                            visited++; next.Add(sub); stack.Push((sub, depth + 1));
                        }
                    }
                }
                else if (segs[i].IndexOfAny(new[] { '*', '?' }) >= 0)
                {
                    foreach (var dir in level)
                        foreach (var sub in Categories.SubDirs(dir, segs[i]))
                            if (!IsLink(sub)) next.Add(sub);
                }
                else
                {
                    foreach (var dir in level)
                    {
                        var sub = Path.Combine(dir, segs[i]);
                        if (Directory.Exists(sub) && !IsLink(sub)) next.Add(sub);
                    }
                }
                level = next;
            }
            result.AddRange(level.Take(MaxMatches));
        }
        catch { }
        return result;
    }

    static bool IsLink(string dir)
    {
        try { return File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint); } catch { return true; }
    }

    /// <summary>The part of a match from the first wildcard on, for the list (for example "app\build").</summary>
    public static string Tail(string pattern, string path)
    {
        var segs = TrustedPaths.Expand(pattern).Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        int firstWild = Array.FindIndex(segs, x => x.IndexOfAny(new[] { '*', '?' }) >= 0);
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (firstWild < 0 || firstWild >= parts.Length) return parts.Length > 0 ? parts[^1] : path;
        return string.Join("\\", parts.Skip(firstWild));
    }
}

/// <summary>Measures every category (a dry run: nothing is changed).</summary>
public static class CleanerEngine
{
    public static List<CategoryResult> ScanAll(CleanContext ctx, IProgress<string>? progress, CancellationToken ct)
    {
        if (Demo.On) return Demo.CleanResults(ctx);
        var results = new List<CategoryResult>();
        foreach (var cat in Categories.All())
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(cat.Title);
            var res = new CategoryResult { Category = cat };
            try
            {
                foreach (var root in cat.Roots(ctx))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Directory.Exists(root.Path) && !File.Exists(root.Path)) continue;
                    var targets = root.ChildrenAreItems
                        ? Measurer.Children(root.Path).Select(t => (t.full, t.isDir, t.isLink)).ToList()
                        : new List<(string full, bool isDir, bool isLink)> { (root.Path, Directory.Exists(root.Path), false) };
                    foreach (var (full, isDir, isLink) in targets)
                    {
                        ct.ThrowIfCancellationRequested();
                        var m = Measurer.Measure(full, ct);
                        var item = new CleanItem
                        {
                            Category = cat.Id, Path = full, IsDirectory = isDir,
                            Display = root.ChildrenAreItems ? (Path.GetFileName(full) is { Length: > 0 } n ? $"{root.Display}\\{n}" : full) : root.Display,
                            Bytes = m.Bytes, Files = m.Files, Newest = m.Newest, LinksInside = m.Links, IsLink = m.IsLink, Mode = cat.Mode
                        };
                        if (cat.ManagedByWindows) item.Denied = "managed by Windows: use Windows Disk Cleanup";
                        else
                        {
                            var g = isDir ? ctx.Guard.CheckTree(full, ct) : ctx.Guard.Check(full);
                            if (!g.Allowed) item.Denied = g.Reason;
                        }
                        if (cat.Keep != null && !cat.Keep(ctx, item)) continue;
                        if (item.Bytes == 0 && item.Files == 0 && !item.IsLink && cat.ManagedByWindows == false && isDir) continue; // empty folders are noise
                        res.Items.Add(item);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { res.Error = e.Message; Log.Write($"clean scan {cat.Id}: {e.Message}"); }
            res.Items.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            results.Add(res);
        }
        return results;
    }
}

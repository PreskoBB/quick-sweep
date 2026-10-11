using System.Text.RegularExpressions;

namespace QuickSweep.Core.Cleaner;

public readonly record struct GuardResult(bool Allowed, string Reason)
{
    public static GuardResult Ok => new(true, "");
    public static GuardResult Deny(string why) => new(false, why);
}

/// <summary>
/// The hard denylist of the cleaner. Every path is checked before it is measured offered, and again right before it
/// is deleted. A path is refused when it is, or sits inside, or contains one of the protected places.
/// Protected: C:\Windows (except Temp), Program Files, ProgramData (except named cache folders), the Documents /
/// Desktop / Pictures / Videos / Music / Downloads / OneDrive folders, any .git folder, drive roots and the broad
/// profile folders, plus every protected folder the user added in settings.
/// </summary>
public sealed class PathGuard
{
    readonly List<string> _zones = new();            // "this folder and everything below is protected"
    readonly List<string> _exceptions = new();       // named folders inside a zone that may be cleaned (children only)
    readonly List<string> _exact = new();            // too broad to delete themselves, but their named children are fine
    readonly List<Regex> _patterns = new();
    readonly List<Regex> _exactPatterns = new();   // too broad to delete themselves (any user); their contents are fine
    readonly List<string> _patternText = new();
    readonly string _systemDrive;
    readonly List<string> _user;

    /// <summary>The protected folders the user added, as written (passed on to the helper that runs with the user's rights).</summary>
    public IReadOnlyList<string> UserPatterns => _user;

    public PathGuard(AppSettings? s = null) : this(s?.ProtectedFolders ?? new()) { }

    /// <summary>What is protected without any setting, in plain words (shown in Settings).</summary>
    public static readonly string[] BuiltInProtection =
    {
        @"Windows (except its Temp folder) and the system folders under it",
        @"Program Files and Program Files (x86)",
        @"ProgramData (except the NVIDIA download folders and Windows error reports)",
        @"Your Documents, Desktop, Pictures, Videos, Music and Downloads folders",
        @"OneDrive folders",
        @"Every .git folder, on any drive",
        @"Drive roots, whole user profiles, AppData, the Recycle Bin and System Volume Information",
        @"Network paths, folders reached through a junction or symlink, and paths containing '..'",
    };

    /// <param name="extraPatterns">Folders the user added (see <see cref="AppSettings.ProtectedFolders"/>).</param>
    public PathGuard(IEnumerable<string> extraPatterns)
    {
        // from Windows' own records, not from environment variables (see TrustedPaths)
        string win = TrustedPaths.Windows, pf = TrustedPaths.ProgramFiles, pf86 = TrustedPaths.ProgramFilesX86, pd = TrustedPaths.ProgramData;
        string profile = TrustedPaths.Profile, local = TrustedPaths.Local, roaming = TrustedPaths.Roaming;
        _user = extraPatterns.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();
        _systemDrive = Path.GetPathRoot(win) ?? @"C:\";

        foreach (var z in new[] { win, pf, pf86, pd }) if (!string.IsNullOrEmpty(z)) _zones.Add(Normalize(z));
        foreach (var k in new[]
        {
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.MyPictures,
            Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic
        }) { var p = Environment.GetFolderPath(k); if (!string.IsNullOrEmpty(p)) _zones.Add(Normalize(p)); }
        _zones.Add(Normalize(Path.Combine(profile, "Downloads")));
        _zones.Add(Normalize(Path.Combine(profile, "OneDrive")));
        _zones.Add(Normalize(Path.Combine(local, "QuickSweep")));
        // other known-folder style patterns
        AddPattern(@"*:\Users\*\Documents");
        AddPattern(@"*:\Users\*\Desktop");
        AddPattern(@"*:\Users\*\Pictures");
        AddPattern(@"*:\Users\*\Videos");
        AddPattern(@"*:\Users\*\Music");
        AddPattern(@"*:\Users\*\Downloads");
        AddPattern(@"*:\Users\*\OneDrive*");
        AddPattern(@"*:\$Recycle.Bin");
        AddPattern(@"*:\System Volume Information");
        AddPattern(@"*:\Recovery");
        foreach (var broad in new[]
        {
            @"*:\Users", @"*:\Users\*", @"*:\Users\*\AppData", @"*:\Users\*\AppData\Local", @"*:\Users\*\AppData\Roaming",
            @"*:\Users\*\AppData\LocalLow", @"*:\Users\*\AppData\Local\Temp", @"*:\ProgramData"
        }) AddPattern(broad, exactOnly: true);
        AddPattern(@"*:\Windows");
        AddPattern(@"*:\Program Files");
        AddPattern(@"*:\Program Files (x86)");

        // the only places inside a zone that may be cleaned
        foreach (var e in new[]
        {
            Path.Combine(win, "Temp"),
            Path.Combine(pd, "NVIDIA Corporation", "Downloader"),
            Path.Combine(pd, "NVIDIA Corporation", "NV_Cache"),
            Path.Combine(pd, "NVIDIA Corporation", "NVIDIA app", "UpdateFramework", "ota-artifacts"),
            Path.Combine(pd, "Microsoft", "Windows", "WER", "ReportArchive"),
            Path.Combine(pd, "Microsoft", "Windows", "WER", "ReportQueue"),
            Path.Combine(pd, "Microsoft", "Windows", "WER", "Temp"),
        }) _exceptions.Add(Normalize(e));

        // too broad to delete as a whole
        foreach (var e in new[] { profile, Path.GetDirectoryName(profile) ?? profile, local, roaming, Path.Combine(profile, "AppData"), Path.Combine(profile, "AppData", "LocalLow"), Path.Combine(local, "Temp") })
            _exact.Add(Normalize(e));

        // .git anywhere, any other repo metadata
        AddPattern(@"**\.git");

        // folders the user added: environment variables work (%USERPROFILE%\Work), * works inside a folder name, ** means any depth
        foreach (var p in _user) AddPattern(TrustedPaths.Expand(p));
    }

    public IReadOnlyList<string> PatternText => _patternText;

    void AddPattern(string pattern, bool exactOnly = false)
    {
        // each pattern protects the matching folder and everything below it
        var segs = pattern.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder("^");
        for (int i = 0; i < segs.Length; i++)
        {
            if (i > 0) sb.Append(@"\\");
            if (segs[i] == "**") sb.Append(@"(?:[^\\]+\\)*[^\\]+"); // one or more segments
            else sb.Append(Regex.Escape(segs[i]).Replace(@"\*", @"[^\\]*"));
        }
        if (exactOnly)
        {
            _exactPatterns.Add(new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            return;
        }
        sb.Append(@"(?:\\.*)?$");
        _patterns.Add(new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        _patternText.Add(pattern);
    }

    public static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        var full = QuickSweep.Native.Win.LongPath(Path.GetFullPath(path));
        return full.Length > 3 ? full.TrimEnd('\\') : full;
    }

    static bool IsUnder(string path, string root)
        => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);

    public GuardResult Check(string rawPath)
    {
        string p;
        try
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return GuardResult.Deny("empty path");
            if (rawPath.StartsWith(@"\\", StringComparison.Ordinal) && !rawPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return GuardResult.Deny("network paths are never cleaned");
            if (rawPath.Contains("..")) return GuardResult.Deny("path contains '..'");
            p = Normalize(rawPath);
        }
        catch (Exception e) { return GuardResult.Deny("invalid path: " + e.Message); }

        if (p.Length < 4 || (p.Length == 3 && p[1] == ':')) return GuardResult.Deny("a whole drive is never cleaned");
        if (!(p.Length > 2 && char.IsLetter(p[0]) && p[1] == ':' && p[2] == '\\')) return GuardResult.Deny("not a normal drive path");

        foreach (var e in _exact) if (p.Equals(e, StringComparison.OrdinalIgnoreCase)) return GuardResult.Deny("too broad: " + e);
        foreach (var rx in _exactPatterns) if (rx.IsMatch(p)) return GuardResult.Deny("too broad: a whole profile or system folder");

        // inside a protected zone? only the named exceptions (their contents) get through
        foreach (var z in _zones)
        {
            if (!IsUnder(p, z)) continue;
            if (!InsideException(p)) return GuardResult.Deny($"protected location ({z})");
        }

        foreach (var (rx, text) in _patterns.Zip(_patternText))
        {
            if (!rx.IsMatch(p)) continue;
            // the named exceptions sit inside Windows/ProgramData, which the generic patterns also cover
            if (InsideException(p)) continue;
            return GuardResult.Deny($"protected by rule '{text}'");
        }
        return GuardResult.Ok;
    }

    /// <summary>Named exceptions allow their contents only, never the exception folder itself.</summary>
    bool InsideException(string p) => _exceptions.Any(ex => p.StartsWith(ex + "\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>Refuses paths below a junction or symlink: the real location could be somewhere protected.</summary>
    static GuardResult CheckAncestorLinks(string p)
    {
        try
        {
            var dir = Path.GetDirectoryName(p);
            while (!string.IsNullOrEmpty(dir) && dir.Length > 3)
            {
                if (Directory.Exists(dir) && File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint))
                    return GuardResult.Deny($"sits inside a linked folder ({dir})");
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch { /* unreadable ancestors: the path-level checks above already passed */ }
        return GuardResult.Ok;
    }

    /// <summary>Like <see cref="Check"/> but also walks the folder: refuses when anything protected sits inside it.
    /// Never follows junctions or symlinks.</summary>
    public GuardResult CheckTree(string path, CancellationToken ct = default)
    {
        var r = Check(path);
        if (!r.Allowed) return r;
        var links = CheckAncestorLinks(Normalize(path));
        if (!links.Allowed) return links;
        try
        {
            if (!Directory.Exists(path)) return r;
            var attr = File.GetAttributes(path);
            if (attr.HasFlag(FileAttributes.ReparsePoint)) return r; // a link: only the link is removed
            var stack = new Stack<string>();
            stack.Push(path);
            var opt = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0, ReturnSpecialDirectories = false };
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var d = stack.Pop();
                foreach (var sub in new System.IO.Enumeration.FileSystemEnumerable<(string full, bool link)>(d,
                    (ref System.IO.Enumeration.FileSystemEntry e) => (e.ToFullPath(), e.Attributes.HasFlag(FileAttributes.ReparsePoint)), opt)
                    { ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry e) => e.IsDirectory })
                {
                    var c = Check(sub.full);
                    if (!c.Allowed) return GuardResult.Deny($"contains a protected folder: {sub.full} ({c.Reason})");
                    if (!sub.link) stack.Push(sub.full);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return GuardResult.Deny("could not inspect the folder safely: " + e.Message); }
        return GuardResult.Ok;
    }

    public bool IsOnSystemDrive(string path)
        => Normalize(path).StartsWith(_systemDrive, StringComparison.OrdinalIgnoreCase);
}

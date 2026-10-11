using System.Text.Json;
using QuickSweep.Native;

namespace QuickSweep.Core;

/// <summary>
/// Where things live, and why.
/// <list type="bullet">
/// <item>The installed (elevated) instance keeps its state in %ProgramData%\QuickSweep, a folder only SYSTEM and
/// Administrators can write. Anything a user-level process could redirect (a junction, a swapped file) is never a place
/// the elevated process writes to. The folder is checked at every start (<see cref="StateFolder.EnsureSecure"/>).</item>
/// <item>The only user-writable place the elevated process touches is the request inbox (open.txt in
/// %LOCALAPPDATA%\QuickSweep). It reads at most a few bytes from it, with link checks, and acts only on an allowlist.</item>
/// <item>A plain (non-elevated) run keeps everything in %LOCALAPPDATA%\QuickSweep. The QUICKSWEEP_DATA variable and the --data switch
/// move that for a non-elevated run (tests, --dev); an elevated process ignores both, because anything running as the user
/// can set an environment variable or start the program with an argument, and the elevated process must not be steered.</item>
/// </list>
/// </summary>
public static class Paths
{
    static readonly List<string> Notes = new();

    /// <summary>State folder of this process (settings, sessions, logs, dump, public\gaming.json).</summary>
    public static string Data { get; private set; } = "";
    /// <summary>Folder of the request inbox, the one place a lower-privileged launcher may write.</summary>
    public static string InboxDir { get; private set; } = "";
    /// <summary>Set when the state folder cannot be trusted: state is then neither read nor written.</summary>
    public static string? Refused { get; private set; }
    public static bool Writable => Refused == null;

    /// <summary>%LOCALAPPDATA%\QuickSweep, with the profile taken from the machine's profile list rather than from the environment.</summary>
    public static string UserDir => Path.Combine(TrustedPaths.Local, "QuickSweep");
    public static string ProgramDataDir => Path.Combine(TrustedPaths.ProgramData, "QuickSweep");
    /// <summary>User-readable output of the elevated side (gaming.json), in its own subfolder.</summary>
    public static string Public => Path.Combine(Data, "public");
    public static string InboxFile => Path.Combine(InboxDir, "open.txt");
    public static bool IsCustom { get; private set; }

    static Paths() { Resolve(); }

    /// <summary>The decision, separated from its effects so it can be tested.</summary>
    internal readonly record struct Resolution(string Data, string Inbox, bool Custom, bool SecureCheck, string? IgnoredOverride);

    /// <param name="elevated">the real token of this process</param>
    /// <param name="overridePath">QUICKSWEEP_DATA (or --data)</param>
    internal static Resolution ResolveFor(bool elevated, string? overridePath, string programDataDir, string userDir)
    {
        bool wanted = !string.IsNullOrWhiteSpace(overridePath);
        if (elevated) return new Resolution(programDataDir, userDir, false, true, wanted ? overridePath : null);   // always the secured folder
        if (wanted) return new Resolution(overridePath!, overridePath!, true, false, null);
        return new Resolution(userDir, userDir, false, false, null);
    }

    static void Resolve()
    {
        try
        {
            var r = ResolveFor(Elevation.IsTokenElevated, Environment.GetEnvironmentVariable("QUICKSWEEP_DATA"), ProgramDataDir, UserDir);
            Data = r.Data; InboxDir = r.Inbox; IsCustom = r.Custom;
            if (r.IgnoredOverride != null) Notes.Add($"ignored the data-folder override '{r.IgnoredOverride}': an elevated process only uses {ProgramDataDir}");
            if (r.SecureCheck) Refused = StateFolder.EnsureSecure(Data, Notes);     // every elevated start: owner, ACL and links are verified
            else Directory.CreateDirectory(Data);
        }
        catch (Exception e)
        {
            if (Data.Length == 0) Data = ProgramDataDir;
            if (InboxDir.Length == 0) InboxDir = UserDir;
            Refused ??= "could not prepare the state folder: " + e.Message;
        }
    }

    /// <summary>A line for the log once it exists (the log needs the paths first).</summary>
    public static void AddNote(string note) { lock (Notes) Notes.Add(note); }

    public static string File(string name) => Path.Combine(Data, name);

    /// <summary>Messages produced while the paths were resolved (the log did not exist yet).</summary>
    public static List<string> TakeNotes()
    {
        lock (Notes) { var l = Notes.ToList(); Notes.Clear(); return l; }
    }
}

public static class Log
{
    static readonly object L = new();
    static long _bytes = -1;
    static bool _notesFlushed;
    /// <summary>Extra diagnostics (tray messages, flyout open/close reasons) when QUICKSWEEP_TRACE is set.</summary>
    public static readonly bool Trace = Environment.GetEnvironmentVariable("QUICKSWEEP_TRACE") is { Length: > 0 };

    public static void Write(string msg)
    {
        try
        {
            lock (L)
            {
                if (!_notesFlushed)
                {
                    _notesFlushed = true;
                    foreach (var n in Paths.TakeNotes()) Append(n);
                    if (Paths.Refused != null) Append("STATE FOLDER REFUSED: " + Paths.Refused);
                }
                Append(msg);
            }
        }
        catch { }
    }

    static void Append(string msg)
    {
        if (!Paths.Writable)
        {
            // there is no trusted state folder; leave the reason where the user can read it
            StateFolder.NoteRefusal(msg);
            return;
        }
        if (_bytes < 0) _bytes = SafeIo.LengthOrZero(Paths.File("quicksweep.log"));
        if (_bytes > 400_000) { StateIo.Rotate("quicksweep.log"); _bytes = 0; }
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}";
        if (StateIo.AppendText("quicksweep.log", line)) _bytes += line.Length;
    }
}

/// <summary>All reads and writes of the state folder: no-follow, verified, and disabled when the folder is not trusted.</summary>
public static class StateIo
{
    [ThreadStatic] static string? _why;
    public static string LastWhy => _why ?? "";

    static string P(string name) => Path.Combine(Paths.Data, name);

    public static string? ReadText(string name, int maxBytes = 8_000_000)
    {
        _why = null;
        if (!Paths.Writable) { _why = Paths.Refused; return null; }
        var r = SafeIo.TryReadSmall(P(name), maxBytes, out var text, out var why);
        if (r == ReadResult.Ok) return text;
        if (r == ReadResult.Rejected) _why = why;
        return null;
    }

    static bool EnsureFolder(string name)
    {
        var dir = Path.GetDirectoryName(P(name));
        try { if (dir != null) Directory.CreateDirectory(dir); return true; }
        catch (Exception e) { _why = e.Message; return false; }
    }

    public static bool WriteText(string name, string text)
    {
        _why = null;
        if (!Paths.Writable) { _why = Paths.Refused; return false; }
        if (!EnsureFolder(name)) return false;
        bool ok = SafeIo.TryWriteText(P(name), text, append: false, out var why);
        if (!ok) _why = why;
        return ok;
    }

    public static bool AppendText(string name, string text)
    {
        _why = null;
        if (!Paths.Writable) { _why = Paths.Refused; return false; }
        if (!EnsureFolder(name)) return false;
        bool ok = SafeIo.TryWriteText(P(name), text, append: true, out var why);
        if (!ok) _why = why;
        return ok;
    }

    public static bool Delete(string name)
    {
        _why = null;
        if (!Paths.Writable) { _why = Paths.Refused; return false; }
        bool ok = SafeIo.TryDelete(P(name), out var why);
        if (!ok) _why = why;
        return ok;
    }

    /// <summary>Rename <paramref name="from"/> over <paramref name="to"/> (both names in the state folder).</summary>
    public static bool Replace(string from, string to)
    {
        _why = null;
        if (!Paths.Writable) { _why = Paths.Refused; return false; }
        bool ok = SafeIo.TryReplace(P(from), P(to), out var why);
        if (!ok) _why = why;
        return ok;
    }

    public static bool Rotate(string name) => Replace(name, name + ".old");
}

public static class JsonStore
{
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static T Load<T>(string file) where T : new()
    {
        try
        {
            var text = StateIo.ReadText(file);
            if (text != null) return JsonSerializer.Deserialize<T>(text, Opt) ?? new T();
        }
        catch (Exception e) { Log.Write($"load {file} failed: {e.Message}"); }
        return new T();
    }

    public static void Save<T>(string file, T value)
    {
        try
        {
            var tmp = file + ".tmp";
            if (!StateIo.WriteText(tmp, JsonSerializer.Serialize(value, Opt)) || !StateIo.Replace(tmp, file))
                Log.Write($"save {file} refused: {StateIo.LastWhy}");
        }
        catch (Exception e) { Log.Write($"save {file} failed: {e.Message}"); }
    }
}

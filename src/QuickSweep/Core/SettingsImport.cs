using QuickSweep.Native;

namespace QuickSweep.Core;

/// <summary>
/// <c>QuickSweep.exe --import-settings=&lt;file&gt;</c>: merges a settings file into the state folder's settings.json with the same
/// code as Settings > Import (only the keys that are in the file change). <c>install.ps1 -ImportSettings &lt;file&gt;</c> runs it
/// elevated during the install, before the task starts. The file is opened without following links and capped in size (it
/// sits in a user-writable place), the current settings are never replaced by defaults when they cannot be read, and
/// nothing is written unless the file was usable. Nothing in the file is ever run; only values are taken.
/// </summary>
public static class SettingsImport
{
    public const int MaxBytes = 256 * 1024;
    /// <summary>Exit codes of the command.</summary>
    public const int Done = 0, Unreadable = 2, NotSettings = 3, NotSaved = 4;

    /// <summary>The file named on the command line, and whether the switch was present at all (a bare switch is an error, never a normal start).</summary>
    public static string? FileFromArgs(string[] args, out bool requested)
    {
        requested = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--import-settings=", StringComparison.Ordinal)) { requested = true; return args[i]["--import-settings=".Length..].Trim('"'); }
            if (args[i] == "--import-settings") { requested = true; return i + 1 < args.Length ? args[i + 1].Trim('"') : null; }
        }
        return null;
    }

    /// <param name="load">current settings (null + reason when they exist but cannot be used)</param>
    /// <param name="save">writes the merged settings (false when it did not work)</param>
    public static int Run(string? file, Func<(AppSettings? settings, string why)> load, Func<AppSettings, bool> save, out string message)
    {
        if (string.IsNullOrWhiteSpace(file)) { message = "settings import: no file given"; return Unreadable; }
        switch (SafeIo.TryReadSmall(file, MaxBytes, out var text, out var why))
        {
            case ReadResult.Missing: message = $"settings import: file not found: {file}"; return Unreadable;
            case ReadResult.Rejected: message = $"settings import: file refused ({why}): {file}"; return Unreadable;
        }
        var (current, loadWhy) = load();
        if (current == null) { message = $"settings import: nothing changed, {loadWhy}"; return NotSaved; }
        int n = AppSettings.Import(current, text, out var merged);
        if (n < 0) { message = $"settings import: not a Quick Sweep settings file: {file}"; return NotSettings; }
        if (n == 0) { message = "settings import: the file has no settings in it, nothing changed"; return Done; }
        if (!save(merged)) { message = "settings import: the merged settings could not be saved (see the log line before this one)"; return NotSaved; }
        message = $"settings import: merged {n} key(s) from {Path.GetFileName(file)}";
        return Done;
    }
}

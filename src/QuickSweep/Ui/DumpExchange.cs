using QuickSweep.Core;

namespace QuickSweep.Ui;

/// <summary>
/// Client side of "--dump=<file>" against a running instance. The instance has no code path that accepts a path from
/// anybody: setting its dump event makes it write dump.json into its own state folder. This client (running with the
/// caller's own, lower privileges) waits for a fresh copy and copies it to the requested file itself.
/// </summary>
public static class DumpExchange
{
    public const string FileName = "dump.json";

    /// <summary>State folders a running instance may be using (the installed one is elevated and uses ProgramData).</summary>
    public static IReadOnlyList<string> Candidates()
        => Paths.IsCustom ? new[] { Paths.Data } : new[] { Paths.ProgramDataDir, Paths.UserDir };

    public static bool Request(EventWaitHandle dumpEvent, string destination, int waitSeconds = 15, IReadOnlyList<string>? stateFolders = null)
    {
        var dirs = stateFolders ?? Candidates();
        var before = dirs.ToDictionary(d => d, d => LastWrite(Path.Combine(d, FileName)));
        dumpEvent.Set();
        var until = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (DateTime.UtcNow < until)
        {
            foreach (var d in dirs)
            {
                var src = Path.Combine(d, FileName);
                if (LastWrite(src) > before[d])
                {
                    try { Thread.Sleep(150); File.Copy(src, destination, true); return true; }
                    catch (IOException) { /* still being written: try again */ }
                }
            }
            Thread.Sleep(200);
        }
        return false;
    }

    static DateTime LastWrite(string file)
    {
        try { return File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue; } catch { return DateTime.MinValue; }
    }
}

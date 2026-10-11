namespace QuickSweep.Core;

/// <summary>Command-line switches whose meaning depends on who is running the program.</summary>
internal static class StartupArgs
{
    /// <summary>
    /// The folder <c>--data=</c> asks for, but only for a non-elevated <c>--dev</c> run. An elevated process (and a run without
    /// --dev) gets null and the rejected value in <paramref name="ignored"/>: a program running as the user can start the
    /// elevated instance's executable or set QUICKSWEEP_DATA, so neither may choose where the elevated side reads and writes.
    /// </summary>
    public static string? DataFolder(string[] args, bool tokenElevated, out string? ignored)
    {
        ignored = null;
        var d = args.FirstOrDefault(a => a.StartsWith("--data=", StringComparison.Ordinal))?[7..];
        if (string.IsNullOrWhiteSpace(d)) return null;
        if (tokenElevated || !args.Contains("--dev")) { ignored = d; return null; }
        return d;
    }
}

using QuickSweep.Core;
using QuickSweep.Native;

namespace QuickSweep.Ui;

public enum RequestKind { OpenMain, ToggleFlyout, Page, Ignored }

/// <summary>
/// The channel a lower-privileged launcher can reach the elevated instance through (a medium-labelled event plus a
/// small file in a user-writable folder). It carries no commands and no paths: the content is either empty (open the
/// main window), "flyout" (toggle the flyout) or the key of one of the known pages. Everything else is ignored.
/// </summary>
public static class OpenRequest
{
    public const string FlyoutKey = "flyout";
    public const int MaxBytes = 64;

    /// <summary>The page keys of the main window. Kept in one place so the allowlist cannot drift from the navigation.</summary>
    public static readonly string[] PageKeys = { "sessions", "memory", "startup", "audio", "disk", "health", "network", "settings" };

    public static (RequestKind kind, string? page) Parse(string? content)
    {
        var t = (content ?? "").Trim();
        if (t.Length == 0) return (RequestKind.OpenMain, null);
        if (t.Length > 24) return (RequestKind.Ignored, null);
        if (string.Equals(t, FlyoutKey, StringComparison.Ordinal)) return (RequestKind.ToggleFlyout, null);
        foreach (var k in PageKeys) if (string.Equals(t, k, StringComparison.Ordinal)) return (RequestKind.Page, k);
        return (RequestKind.Ignored, null);
    }

    /// <summary>Only characters that cannot mislead a reader of the log.</summary>
    public static string ForLog(string? content)
    {
        var t = new string((content ?? "").Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '.' or ' ').Take(24).ToArray());
        return t;
    }

    /// <summary>Launcher side: leave a request. Unknown content is replaced by "open the main window".</summary>
    public static void Write(string? content)
    {
        var (kind, page) = Parse(content);
        string text = kind switch { RequestKind.Page => page!, RequestKind.ToggleFlyout => FlyoutKey, _ => "" };
        try { Directory.CreateDirectory(Paths.InboxDir); File.WriteAllText(Paths.InboxFile, text); } catch { }
    }

    /// <summary>
    /// Instance side: take the request and delete the file. The file lives in a folder any process of the user can
    /// change, so it is read with link checks and a size cap, and deleted without following links.
    /// </summary>
    /// <param name="startup">true: at start-up, only a request younger than 90 s counts and a missing file means no request;
    /// false: the event was signalled, so a missing file simply means "open the main window"</param>
    public static (RequestKind kind, string? page)? Take(bool startup) => Take(Paths.InboxFile, startup);

    internal static (RequestKind kind, string? page)? Take(string path, bool startup)
    {
        if (startup)
        {
            try { if (!File.Exists(path) || (DateTime.Now - File.GetLastWriteTime(path)).TotalSeconds > 90) return null; } catch { return null; }
        }
        var r = SafeIo.TryReadSmall(path, MaxBytes, out var text, out var why);
        if (r == ReadResult.Missing) return startup ? null : (RequestKind.OpenMain, null);
        if (!SafeIo.TryDelete(path, out var dwhy)) Log.Write($"request inbox: could not delete the request file ({dwhy})");
        if (r == ReadResult.Rejected) { Log.Write($"request inbox: ignored ({why})"); return null; }
        var parsed = Parse(text);
        if (parsed.kind == RequestKind.Ignored) { Log.Write($"request inbox: ignored unknown request '{ForLog(text)}'"); return null; }
        return parsed;
    }
}

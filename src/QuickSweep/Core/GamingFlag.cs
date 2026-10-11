using System.Text.Json;

namespace QuickSweep.Core;

/// <summary>
/// While a game runs, <c>public\gaming.json</c> in the state folder exists: {"game", "exe", "since", "pid"}. For the
/// installed (elevated) instance that is %ProgramData%\QuickSweep\public\gaming.json: a folder only SYSTEM and
/// Administrators can write and everybody can read. It is deleted when the game closes (and on start-up and exit, so it
/// is never stale). Other programs, for example Claude Code sessions, can check it and defer heavy work.
/// All writes go through the no-follow checks of <see cref="StateIo"/>.
/// </summary>
public static class GamingFlag
{
    public const string Name = @"public\gaming.json";
    public static string FilePath => Path.Combine(Paths.Public, "gaming.json");

    public static void Write(GameInfo g)
    {
        try
        {
            var json = JsonSerializer.Serialize(new
            {
                game = g.Name,
                exe = g.ExeName,
                since = new DateTimeOffset(g.Started).ToString("o"),
                pid = g.Pid
            }, new JsonSerializerOptions { WriteIndented = true });
            if (!StateIo.WriteText(Name, json)) Log.Write("gaming flag not written: " + StateIo.LastWhy);
        }
        catch (Exception e) { Log.Write("gaming flag write failed: " + e.Message); }
    }

    public static void Clear()
    {
        try { if (!StateIo.Delete(Name)) Log.Write("gaming flag not deleted: " + StateIo.LastWhy); }
        catch (Exception e) { Log.Write("gaming flag delete failed: " + e.Message); }
    }
}

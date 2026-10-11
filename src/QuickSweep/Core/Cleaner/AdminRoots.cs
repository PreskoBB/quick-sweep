using QuickSweep.Native;

namespace QuickSweep.Core.Cleaner;

/// <summary>
/// The few places the elevated process itself may delete in, because a normal user cannot do it for them: the system Temp
/// folder, NVIDIA's download and shader-cache folders under ProgramData, Windows error reports, and C:\NVIDIA. Everything else
/// is cleaned by the helper that runs with the user's own rights (see <see cref="HelperRunner"/>). The list comes from Windows'
/// known folders, never from environment variables, and only the contents of these folders are ever eligible (the folder itself is not).
/// Even here nothing is deleted by path: <see cref="HandleDelete"/> verifies each parent through a handle first, because users can
/// often create folders inside these locations.
/// </summary>
public static class AdminRoots
{
    public static IReadOnlyList<string> All()
    {
        string win = TrustedPaths.Windows, pd = TrustedPaths.ProgramData;
        return new[]
        {
            Path.Combine(win, "Temp"),
            Path.Combine(pd, "NVIDIA Corporation", "Downloader"),
            Path.Combine(pd, "NVIDIA Corporation", "NV_Cache"),
            Path.Combine(pd, "NVIDIA Corporation", "NVIDIA app", "UpdateFramework", "ota-artifacts"),
            Path.Combine(pd, "Microsoft", "Windows", "WER", "ReportArchive"),
            Path.Combine(pd, "Microsoft", "Windows", "WER", "ReportQueue"),
            Path.Combine(pd, "Microsoft", "Windows", "WER", "Temp"),
            Path.Combine(TrustedPaths.SystemDrive, "NVIDIA"),
        }.Select(p => PathGuard.Normalize(p)).ToList();
    }

    /// <summary>True when the path lies strictly inside one of the administrator-level roots.</summary>
    public static bool IsAdminClass(string path)
    {
        string p;
        try { p = PathGuard.Normalize(path); } catch { return false; }
        foreach (var root in All())
            if (p.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

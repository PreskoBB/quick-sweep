using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using QuickSweep.Native;

namespace QuickSweep.Core;

/// <summary>
/// Locations the elevated process may rely on. The user's own environment variables (TEMP, LOCALAPPDATA, PIP_CACHE_DIR...)
/// live in HKCU\Environment, which any program running as that user can change, so they are never used to decide where
/// the elevated process looks. The profile folder comes from the machine's profile list (HKLM, what the logon used);
/// Windows, ProgramData and Program Files come from Windows' own known folders.
/// </summary>
public static class TrustedPaths
{
    [DllImport("userenv.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetUserProfileDirectoryW(nint token, StringBuilder? path, ref uint size);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint h);
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();

    /// <summary>The user's profile folder as the profile list records it (the user's folder under the Users folder).</summary>
    public static string Profile { get; } = ReadProfile();

    static string ReadProfile()
    {
        try
        {
            if (OpenProcessToken(GetCurrentProcess(), 0x8 /* TOKEN_QUERY */, out var token))
            {
                try
                {
                    uint size = 0;
                    GetUserProfileDirectoryW(token, null, ref size);
                    if (size > 0)
                    {
                        var sb = new StringBuilder((int)size);
                        if (GetUserProfileDirectoryW(token, sb, ref size) && sb.Length > 0) return sb.ToString().TrimEnd('\\');
                    }
                }
                finally { CloseHandle(token); }
            }
        }
        catch { }
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    public static string Local => Path.Combine(Profile, "AppData", "Local");
    public static string Roaming => Path.Combine(Profile, "AppData", "Roaming");
    /// <summary>The user's Temp folder, derived from the profile, not from the TEMP variable.</summary>
    public static string Temp => Path.Combine(Local, "Temp");
    public static string Windows { get; } = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static string ProgramData { get; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public static string ProgramFiles { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public static string ProgramFilesX86 { get; } = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    public static string SystemDrive => Path.GetPathRoot(Windows) ?? @"C:\";

    /// <summary>The few well-known variables a folder pattern may use, with their trusted values.</summary>
    static Dictionary<string, string> Known() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["USERPROFILE"] = Profile, ["LOCALAPPDATA"] = Local, ["APPDATA"] = Roaming, ["TEMP"] = Temp, ["TMP"] = Temp,
        ["PROGRAMDATA"] = ProgramData, ["ALLUSERSPROFILE"] = ProgramData, ["SYSTEMROOT"] = Windows, ["WINDIR"] = Windows,
        ["SYSTEMDRIVE"] = SystemDrive.TrimEnd('\\'), ["PROGRAMFILES"] = ProgramFiles, ["PROGRAMFILES(X86)"] = ProgramFilesX86,
    };

    static readonly Regex Var = new(@"%([^%\\/]+)%", RegexOptions.Compiled);

    /// <summary>
    /// Like Environment.ExpandEnvironmentVariables, but only for the well-known variables above and with trusted values.
    /// Any other %NAME% stays as written (and so matches nothing).
    /// </summary>
    public static string Expand(string text)
    {
        var known = Known();
        return Var.Replace(text, m => known.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    // ---- cache folders named by environment variables ------------------------------------------------------------------
    static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>
    /// A cache folder that an environment variable points at (PIP_CACHE_DIR, NUGET_PACKAGES...), or null with the reason.
    /// Accepted only when it is an absolute local path, a plain folder (no link in it or on the way, checked through a handle),
    /// not a system location or drive root, and owned by this user (or by Administrators, which no ordinary program can create).
    /// </summary>
    public static string? AcceptEnvCacheRoot(string? value, out string why, Func<string, SecurityIdentifier?>? ownerOf = null)
    {
        why = "";
        if (string.IsNullOrWhiteSpace(value)) { why = "empty"; return null; }
        value = value.Trim().Trim('"');
        if (value.StartsWith(@"\\", StringComparison.Ordinal)) { why = "a network or device path"; return null; }
        if (value.Length < 4 || !char.IsLetter(value[0]) || value[1] != ':' || value[2] != '\\') { why = "not an absolute drive path"; return null; }
        if (value.Contains("..")) { why = "contains '..'"; return null; }
        string norm;
        try { norm = SafeIo.Normalize(value); } catch (Exception e) { why = e.Message; return null; }
        if (norm.Length <= 3) { why = "a drive root"; return null; }
        foreach (var sys in new[] { Windows, ProgramFiles, ProgramFilesX86, ProgramData })
            if (!string.IsNullOrEmpty(sys) && (norm.Equals(sys, StringComparison.OrdinalIgnoreCase) || norm.StartsWith(sys.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
            { why = "a system location"; return null; }
        if (norm.Equals(Profile, StringComparison.OrdinalIgnoreCase) || Profile.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase))
        { why = "the whole profile or a folder above it"; return null; }
        if (!SafeIo.IsPlainDirectory(norm, out var dwhy)) { why = dwhy; return null; }
        SecurityIdentifier? owner;
        try { owner = ownerOf != null ? ownerOf(norm) : new DirectoryInfo(norm).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier; }
        catch (Exception e) { why = "owner cannot be read: " + e.Message; return null; }
        var me = WindowsIdentity.GetCurrent().User;
        if (owner == null || (owner != me && owner != AdminsSid)) { why = $"owned by {owner?.Value ?? "unknown"}, not by this user"; return null; }
        return norm;
    }
}

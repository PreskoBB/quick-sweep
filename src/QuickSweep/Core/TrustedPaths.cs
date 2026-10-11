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

    // ---- the user's personal folders, wherever they live --------------------------------------------------------------------
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(nint p);

    /// <summary>The known folders that hold a person's own files (KNOWNFOLDERID values from the Windows SDK).</summary>
    internal static readonly (string Name, Guid Id)[] PersonalFolderIds =
    {
        ("Documents", new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7")),
        ("Desktop", new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641")),
        ("Pictures", new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB")),
        ("Videos", new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC")),
        ("Music", new Guid("4BD8D571-6D19-48D3-BE97-422220080E43")),
        ("Downloads", new Guid("374DE290-123F-4565-9164-39C4925E467B")),
        ("OneDrive", new Guid("A52BBA46-E9E1-435F-B3D9-28DAA648C0F6")),
    };

    /// <summary>One known folder of the user behind <paramref name="token"/> (0: the user of this process), without creating or verifying it.</summary>
    internal static string? KnownFolder(Guid id, nint token)
    {
        try
        {
            if (SHGetKnownFolderPath(ref id, 0x4000 /* KF_FLAG_DONT_VERIFY */, token, out var p) != 0 || p == 0) return null;
            try { return Marshal.PtrToStringUni(p); } finally { CoTaskMemFree(p); }
        }
        catch { return null; }
    }

    static IReadOnlyList<string>? _userFolders;

    /// <summary>
    /// Documents, Desktop, Pictures, Videos, Music, Downloads and OneDrive of the interactive user, where Windows says they are (a folder
    /// that was moved to another drive is listed there) plus their default places below the profile. For the user of this process and for
    /// the user of the desktop shell, which differ when the process was elevated with another account's credentials.
    /// </summary>
    public static IReadOnlyList<string> UserFolders => _userFolders ??= ComputeUserFolders(KnownFolder, ShellToken.OpenImpersonation, ShellToken.Close);

    internal static List<string> ComputeUserFolders(Func<Guid, nint, string?> resolve, Func<nint> openShellToken, Action<nint> closeToken)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string profile = Profile, local = Local;
        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string norm;
            try { norm = SafeIo.Normalize(path.Trim()); } catch { return; }
            if (norm.Length < 4 || norm.StartsWith(@"\\", StringComparison.Ordinal)) return;           // a drive root or a network path is not a personal folder
            // a "personal folder" that is the profile or contains AppData would protect far too much (and block the temp cleaner): ignore it
            if (norm.Equals(profile, StringComparison.OrdinalIgnoreCase) || profile.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase) ||
                local.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase)) return;
            if (seen.Add(norm)) found.Add(norm);
        }
        nint shell = 0;
        try { shell = openShellToken(); } catch { }
        try
        {
            foreach (var (name, id) in PersonalFolderIds)
            {
                Add(resolve(id, 0));                                                                   // this process's user
                if (shell != 0) Add(resolve(id, shell));                                                // the user of the desktop shell
                Add(Path.Combine(profile, name));                                                      // and the default place, always
            }
        }
        finally { if (shell != 0) closeToken(shell); }
        return found;
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

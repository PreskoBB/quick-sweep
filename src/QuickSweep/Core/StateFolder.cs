using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using QuickSweep.Native;

namespace QuickSweep.Core;

/// <summary>
/// The elevated instance's own state folder (%ProgramData%\QuickSweep) and what makes it trustworthy: owned by
/// Administrators or SYSTEM, a DACL that gives SYSTEM and Administrators full control and the user read-only, no inherited
/// write access for Users, no links inside. Checked (and, when it is merely too open, repaired) at every start; if it
/// cannot be trusted the elevated process neither reads nor writes it.
/// </summary>
public static class StateFolder
{
    static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier AdminsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier UsersSid = new(WellKnownSidType.BuiltinUsersSid, null);
    static readonly SecurityIdentifier TrustedInstallerSid = new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    /// <summary>Rights that let an account change what the elevated process will later read or write.</summary>
    const FileSystemRights WriteLike = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
        | (FileSystemRights)0x50000000;   // GENERIC_WRITE and GENERIC_ALL: entries that children inherit are stored with generic rights

    public static bool IsTrusted(SecurityIdentifier? sid, IEnumerable<SecurityIdentifier>? extra = null)
        => sid != null && (sid == SystemSid || sid == AdminsSid || sid == TrustedInstallerSid || (extra?.Contains(sid) ?? false));

    /// <summary>Allow-entries that give anybody but SYSTEM and Administrators a way to write, delete or re-permission.</summary>
    public static List<string> PermissiveEntries(FileSystemSecurity sec, IEnumerable<SecurityIdentifier>? extraTrusted = null)
    {
        var bad = new List<string>();
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType != AccessControlType.Allow) continue;
            if (IsTrusted(r.IdentityReference as SecurityIdentifier, extraTrusted)) continue;
            if ((r.FileSystemRights & WriteLike) != 0) bad.Add($"{r.IdentityReference} may {r.FileSystemRights & WriteLike}");
        }
        return bad;
    }

    /// <summary>SYSTEM and Administrators full control, Users read and execute, nothing inherited.</summary>
    public static DirectorySecurity Canonical()
    {
        var s = new DirectorySecurity();
        s.SetAccessRuleProtection(true, false);
        var flags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        s.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(AdminsSid, FileSystemRights.FullControl, flags, PropagationFlags.None, AccessControlType.Allow));
        s.AddAccessRule(new FileSystemAccessRule(UsersSid, FileSystemRights.ReadAndExecute, flags, PropagationFlags.None, AccessControlType.Allow));
        return s;
    }

    /// <summary>Null when the folder is trustworthy (after repairing a too-open DACL); otherwise the reason it is refused.</summary>
    public static string? EnsureSecure(string dir, IList<string> notes, IEnumerable<SecurityIdentifier>? extraTrustedOwners = null)
    {
        try
        {
            if (!Directory.Exists(dir)) { Directory.CreateDirectory(dir); notes.Add($"created state folder {dir}"); }
            if (!SafeIo.IsPlainDirectory(dir, out var why)) return $"{dir} {why}";

            var di = new DirectoryInfo(dir);
            var sec = di.GetAccessControl();
            var owner = sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (!IsTrusted(owner, extraTrustedOwners)) return $"{dir} is owned by {owner?.Value ?? "unknown"}, not by Administrators or SYSTEM";

            var bad = PermissiveEntries(sec, extraTrustedOwners);
            if (bad.Count > 0 || !sec.AreAccessRulesProtected)
            {
                notes.Add($"state folder permissions were too open ({(bad.Count > 0 ? string.Join("; ", bad) : "inheriting from ProgramData")}): reset to SYSTEM + Administrators full, Users read-only");
                di.SetAccessControl(Canonical());
                var again = new DirectoryInfo(dir).GetAccessControl();
                var still = PermissiveEntries(again, extraTrustedOwners);
                if (still.Count > 0) return $"{dir} permissions could not be tightened ({string.Join("; ", still)})";
            }

            // nothing inside may be a link or belong to somebody else (checked one level down: public\ is the only subfolder)
            foreach (var sub in new[] { dir, Path.Combine(dir, "public") })
            {
                if (!Directory.Exists(sub)) continue;
                foreach (var e in new DirectoryInfo(sub).EnumerateFileSystemInfos())
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) return $"{e.FullName} is a junction or symbolic link";
                    var o = (e is DirectoryInfo d ? d.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) : ((FileInfo)e).GetAccessControl().GetOwner(typeof(SecurityIdentifier))) as SecurityIdentifier;
                    if (!IsTrusted(o, extraTrustedOwners)) return $"{e.FullName} is owned by {o?.Value ?? "unknown"}";
                }
            }
            return null;
        }
        catch (Exception e) { return $"state folder check failed: {e.Message}"; }
    }

    /// <summary>The state folder cannot be used, so say why where the user can read it (checked write, no links followed).</summary>
    public static void NoteRefusal(string text)
    {
        try
        {
            var f = Path.Combine(Paths.InboxDir, "state-refused.txt");
            Directory.CreateDirectory(Paths.InboxDir);
            SafeIo.TryWriteText(f, $"{DateTime.Now:s}  {text}{Environment.NewLine}", append: true, out _);
        }
        catch { }
    }

    static readonly string[] Legacy = { "settings.json", "sessions.json", "drivehealth.json", "deletions.log", "deletions.log.old", "quicksweep.log" };
    public const string MigrationMarker = "migrated-from-user-folder.txt";

    /// <summary>
    /// Once, when the elevated instance first runs with the new state folder: copy what the earlier version kept in
    /// %LOCALAPPDATA%\QuickSweep. The originals are left where they are.
    /// </summary>
    public static void MigrateLegacy()
    {
        if (Paths.IsCustom || !Elevation.IsTokenElevated || !Paths.Writable) return;
        int n = MigrateFrom(Paths.UserDir, Paths.Data, msg => Log.Write("migration: " + msg));
        if (n >= 0) Log.Write($"migration: copied {n} file(s) from the old user folder into {Paths.Data}");
    }

    /// <summary>
    /// Copies the known files from <paramref name="sourceDir"/> (a folder a user-level process can change, so every read
    /// is size-capped and link-checked and JSON is validated) into <paramref name="targetDir"/>, never overwriting
    /// anything there, and leaves a marker so it runs once. Returns the number of files copied, or -1 when already done.
    /// </summary>
    public static int MigrateFrom(string sourceDir, string targetDir, Action<string>? note = null)
    {
        var markerPath = Path.Combine(targetDir, MigrationMarker);
        if (File.Exists(markerPath)) return -1;
        int copied = 0;
        foreach (var name in Legacy)
        {
            var src = Path.Combine(sourceDir, name);
            var r = SafeIo.TryReadSmall(src, 8_000_000, out var text, out var why);
            if (r != ReadResult.Ok)
            {
                if (r == ReadResult.Rejected) note?.Invoke($"skipped {name}: {why}");
                continue;
            }
            if (name.EndsWith(".json", StringComparison.Ordinal))
            {
                try { System.Text.Json.JsonDocument.Parse(text).Dispose(); }
                catch { note?.Invoke($"skipped {name}: not valid JSON"); continue; }
            }
            var dst = Path.Combine(targetDir, name);
            if (File.Exists(dst)) continue;                          // never overwrite what the new folder already has
            if (SafeIo.TryWriteText(dst, text, append: false, out var wwhy)) copied++;
            else note?.Invoke($"could not write {name}: {wwhy}");
        }
        SafeIo.TryWriteText(markerPath, $"{DateTime.Now:s}: copied {copied} file(s) from {sourceDir}{Environment.NewLine}", append: false, out _);
        return copied;
    }
}

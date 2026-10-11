using System.Security.AccessControl;
using System.Security.Principal;
using QuickSweep.Core;
using QuickSweep.Native;
using QuickSweep.Ui;
using Xunit;

namespace QuickSweep.Tests;

/// <summary>What the elevated instance accepts from, and writes to, places a user-level process can change.</summary>
public class RequestChannelTests
{
    [Theory]
    [InlineData("", RequestKind.OpenMain, null)]
    [InlineData("   \r\n", RequestKind.OpenMain, null)]
    [InlineData("flyout", RequestKind.ToggleFlyout, null)]
    [InlineData("sessions", RequestKind.Page, "sessions")]
    [InlineData("settings\r\n", RequestKind.Page, "settings")]
    [InlineData("disk", RequestKind.Page, "disk")]
    public void AllowlistedKeysAreAccepted(string content, RequestKind kind, string? page)
        => Assert.Equal((kind, page), OpenRequest.Parse(content));

    [Theory]
    [InlineData(@"dump:C:\Windows\System32\evil.dll")]       // the old command that gave an arbitrary elevated file write
    [InlineData(@"dump:D:\temp\x.json")]
    [InlineData("dump")]
    [InlineData("cmd:toggle-flyout")]
    [InlineData("Sessions")]                                   // exact keys only
    [InlineData("sessions ; calc")]
    [InlineData(@"..\..\Windows\win.ini")]
    [InlineData("flyout\0")]
    [InlineData("exit")]
    [InlineData("0123456789012345678901234567890123456789")]  // too long
    public void EverythingElseIsIgnored(string content) => Assert.Equal(RequestKind.Ignored, OpenRequest.Parse(content).kind);

    [Fact]
    public void NullIsJustAnOpenRequest() => Assert.Equal(RequestKind.OpenMain, OpenRequest.Parse(null).kind);

    [Fact]
    public void TheAllowlistMatchesTheRealPages()
    {
        var real = PageCatalog.All(null!).Select(p => p.key).OrderBy(k => k).ToArray();
        Assert.Equal(OpenRequest.PageKeys.OrderBy(k => k).ToArray(), real);   // a new page needs a key here, and only here
    }

    [Fact]
    public void NoRequestKindCarriesAPath()
        => Assert.Equal(new[] { "OpenMain", "ToggleFlyout", "Page", "Ignored" }, Enum.GetNames<RequestKind>());

    [Fact]
    public void ALogLineCannotBeForgedFromARequest()
        => Assert.DoesNotContain("\n", OpenRequest.ForLog("a\r\nb 2026-01-01 FAKE LINE\t\0"));

    [Fact]
    public void AnInboxFileWithADumpCommandIsDeletedIgnoredAndWritesNothing()
    {
        using var sb = new Sandbox();
        var evil = Path.Combine(sb.Root, "evil-dump.txt");
        var inbox = sb.File("inbox\\open.txt", "dump:" + evil);
        Assert.Null(OpenRequest.Take(inbox, startup: false));
        Assert.False(File.Exists(inbox));          // consumed
        Assert.False(File.Exists(evil));           // and nothing was written anywhere
    }

    [Fact]
    public void AValidInboxFileIsTakenOnce()
    {
        using var sb = new Sandbox();
        var inbox = sb.File("inbox\\open.txt", "memory");
        Assert.Equal((RequestKind.Page, "memory"), OpenRequest.Take(inbox, startup: false));
        Assert.False(File.Exists(inbox));
        Assert.Equal((RequestKind.OpenMain, null), OpenRequest.Take(inbox, startup: false));   // signalled with no file: open
        Assert.Null(OpenRequest.Take(inbox, startup: true));                                   // start-up with no file: nothing
    }

    [Fact]
    public void AnOversizedInboxFileIsRefusedNotRead()
    {
        using var sb = new Sandbox();
        var inbox = sb.File("inbox\\open.txt", new string('x', 5000));
        Assert.Null(OpenRequest.Take(inbox, startup: false));
        Assert.False(File.Exists(inbox));
    }

    [Fact]
    public void AnInboxBehindAJunctionIsNeverRead()
    {
        using var sb = new Sandbox();
        var real = sb.Dir("real");
        sb.File("real\\open.txt", "sessions");
        var link = sb.Junction("linked", real);
        Assert.Null(OpenRequest.Take(Path.Combine(link, "open.txt"), startup: false));      // refused: the folder is a link
        Assert.True(File.Exists(Path.Combine(real, "open.txt")));                             // and the target file was left alone
    }

    [Fact]
    public void TheDumpAnswerGoesToAFixedFileAndTheClientCopiesIt()
    {
        using var sb = new Sandbox();
        var state = sb.Dir("state");
        var dest = Path.Combine(sb.Dir("out"), "copy.json");
        var name = @"Local\QuickSweep.Test." + Guid.NewGuid().ToString("N");
        using var server = SingleInstance.Create(out _, name: name);
        var t = new Thread(() => { if (server!.WaitOne(5000)) File.WriteAllText(Path.Combine(state, DumpExchange.FileName), "{\"ok\":true}"); });
        t.Start();
        using var client = SingleInstance.TryOpen(out _, name);
        Assert.True(DumpExchange.Request(client!, dest, 5, new[] { state }));
        t.Join();
        Assert.Equal("{\"ok\":true}", File.ReadAllText(dest));
        Assert.Equal("dump.json", DumpExchange.FileName);   // the only name the instance ever writes for a dump
    }
}

public class SafeIoTests
{
    [Fact]
    public void PlainFilesWorkEndToEnd()
    {
        using var sb = new Sandbox();
        var f = Path.Combine(sb.Dir("d"), "a.txt");
        Assert.True(SafeIo.TryWriteText(f, "one", false, out var why), why);
        Assert.True(SafeIo.TryWriteText(f, "two", true, out why), why);
        Assert.Equal(ReadResult.Ok, SafeIo.TryReadSmall(f, 100, out var text, out _));
        Assert.Equal("onetwo", text);
        Assert.True(SafeIo.TryWriteText(f, "x", false, out why), why);          // overwrite truncates
        Assert.Equal("x", File.ReadAllText(f));
        Assert.True(SafeIo.TryDelete(f, out why), why);
        Assert.False(File.Exists(f));
        Assert.Equal(ReadResult.Missing, SafeIo.TryReadSmall(f, 100, out _, out _));
        Assert.True(SafeIo.TryDelete(f, out _));                                  // already gone is fine
    }

    [Fact]
    public void AByteOrderMarkFromPowerShellOrNotepadDoesNotBreakJson()
    {
        using var sb = new Sandbox();
        var f = Path.Combine(sb.Dir("d"), "settings.json");
        File.WriteAllText(f, "{\"CpuAlertC\":91}", new System.Text.UTF8Encoding(true));      // with BOM
        Assert.Equal(ReadResult.Ok, SafeIo.TryReadSmall(f, 1000, out var text, out _));
        Assert.Equal("{\"CpuAlertC\":91}", text);
        Assert.Equal(91, System.Text.Json.JsonSerializer.Deserialize<AppSettings>(text)!.CpuAlertC);
        File.WriteAllText(f, "{\"CpuAlertC\":92}", System.Text.Encoding.Unicode);             // UTF-16 with BOM
        Assert.Equal(ReadResult.Ok, SafeIo.TryReadSmall(f, 1000, out text, out _));
        Assert.Equal("{\"CpuAlertC\":92}", text);
    }

    [Fact]
    public void WritingBelowAJunctionIsRefusedAndCreatesNothingAtTheTarget()
    {
        using var sb = new Sandbox();
        var target = sb.Dir("protected");
        var link = sb.Junction("via", target);
        Assert.False(SafeIo.TryWriteText(Path.Combine(link, "gaming.json"), "pwned", false, out var why));
        Assert.Contains("link", why);
        Assert.Empty(Directory.GetFileSystemEntries(target));
        Assert.False(SafeIo.TryWriteText(Path.Combine(link, "sub", "x.txt"), "pwned", false, out _));
    }

    [Fact]
    public void ReadingBelowAJunctionIsRefusedEvenForAnExistingFile()
    {
        using var sb = new Sandbox();
        var target = sb.Dir("secret");
        sb.File("secret\\data.txt", "do not read me");
        var link = sb.Junction("via", target);
        Assert.Equal(ReadResult.Rejected, SafeIo.TryReadSmall(Path.Combine(link, "data.txt"), 100, out var text, out var why));
        Assert.Equal("", text);
        Assert.NotEqual("", why);
    }

    [Fact]
    public void DeletingBelowAJunctionIsRefusedAndTheTargetFileSurvives()
    {
        using var sb = new Sandbox();
        var target = sb.Dir("keep");
        var precious = sb.File("keep\\precious.txt", "x");
        var link = sb.Junction("via", target);
        Assert.False(SafeIo.TryDelete(Path.Combine(link, "precious.txt"), out _));
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void ARotateThroughAJunctionIsRefused()
    {
        using var sb = new Sandbox();
        var target = sb.Dir("t");
        sb.File("t\\log", "x");
        var link = sb.Junction("via", target);
        Assert.False(SafeIo.TryReplace(Path.Combine(link, "log"), Path.Combine(link, "log.old"), out _));
        Assert.True(File.Exists(Path.Combine(target, "log")));
        Assert.False(File.Exists(Path.Combine(target, "log.old")));
    }

    [Fact]
    public void OversizedFilesAreRejected()
    {
        using var sb = new Sandbox();
        var f = sb.File("big.txt", new string('a', 2000));
        Assert.Equal(ReadResult.Rejected, SafeIo.TryReadSmall(f, 1000, out _, out var why));
        Assert.Contains("larger", why);
    }

    [Fact]
    public void PlainDirectoryCheck()
    {
        using var sb = new Sandbox();
        var plain = sb.Dir("plain");
        var link = sb.Junction("link", plain);
        Assert.True(SafeIo.IsPlainDirectory(plain, out _));
        Assert.False(SafeIo.IsPlainDirectory(link, out var why));
        Assert.Contains("link", why);
        Assert.False(SafeIo.IsPlainDirectory(Path.Combine(sb.Root, "missing"), out _));
        Assert.False(SafeIo.IsPlainDirectory(Path.Combine(link, "child"), out _));
    }

    [Fact]
    public void AFileSymlinkIsNotFollowedWhenTheSystemAllowsCreatingOne()
    {
        using var sb = new Sandbox();
        var target = sb.File("target.txt", "original");
        var link = Path.Combine(sb.Root, "link.txt");
        try { File.CreateSymbolicLink(link, target); }
        catch { return; }                                          // creating symlinks needs a privilege this account may lack
        Assert.Equal(ReadResult.Rejected, SafeIo.TryReadSmall(link, 100, out _, out _));
        Assert.False(SafeIo.TryWriteText(link, "overwritten", false, out _));
        Assert.Equal("original", File.ReadAllText(target));
        Assert.True(SafeIo.TryDelete(link, out _));                // removes the link itself
        Assert.True(File.Exists(target));
        Assert.Equal("original", File.ReadAllText(target));
    }
}

[Trait("Needs", "NonAdmin")]
public class StateFolderTests
{
    static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;

    static DirectorySecurity With(params (SecurityIdentifier sid, FileSystemRights rights)[] rules)
    {
        var s = new DirectorySecurity();
        foreach (var (sid, rights) in rules)
            s.AddAccessRule(new FileSystemAccessRule(sid, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return s;
    }

    [Fact]
    public void TheCanonicalAclIsReadOnlyForUsersAndOpenToNobodyElse()
    {
        var c = StateFolder.Canonical();
        Assert.True(c.AreAccessRulesProtected);
        Assert.Empty(StateFolder.PermissiveEntries(c));
        var rules = c.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.Equal(3, rules.Count);
        Assert.Equal(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize, rules.Single(r => r.IdentityReference == Users).FileSystemRights & (FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize));
        Assert.Equal(FileSystemRights.FullControl, rules.Single(r => r.IdentityReference == Admins).FileSystemRights);
    }

    [Fact]
    public void ProgramDataStyleInheritedWriteAccessForUsersIsFlagged()
    {
        // what a fresh folder under C:\ProgramData inherits: Users may create files and folders
        var open = With((Users, FileSystemRights.CreateFiles | FileSystemRights.AppendData), (Admins, FileSystemRights.FullControl));
        Assert.Single(StateFolder.PermissiveEntries(open));
    }

    [Theory]
    [InlineData(FileSystemRights.Modify)]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData(FileSystemRights.FullControl)]
    public void AnyWriteLikeRightForAnotherAccountIsFlagged(FileSystemRights rights)
        => Assert.NotEmpty(StateFolder.PermissiveEntries(With((Me, rights))));

    [Fact]
    public void ReadOnlyForUsersIsFine() => Assert.Empty(StateFolder.PermissiveEntries(With((Users, FileSystemRights.ReadAndExecute))));

    [Fact]
    public void OnlySystemAndAdministratorsAreTrustedOwners()
    {
        Assert.True(StateFolder.IsTrusted(Admins));
        Assert.True(StateFolder.IsTrusted(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.False(StateFolder.IsTrusted(Me));
        Assert.False(StateFolder.IsTrusted(Users));
        Assert.False(StateFolder.IsTrusted(null));
    }

    [Fact]
    public void AFolderOwnedByTheUserIsRefused()
    {
        if (Elevation.IsElevated) return;             // an elevated test run would own the sandbox as Administrators
        using var sb = new Sandbox();                 // owned by this (non-administrator) account
        var notes = new List<string>();
        var reason = StateFolder.EnsureSecure(sb.Root, notes);
        Assert.NotNull(reason);
        Assert.Contains("owned by", reason);
    }

    [Fact]
    public void AJunctionIsRefusedAsStateFolder()
    {
        using var sb = new Sandbox();
        var real = sb.Dir("real");
        var link = sb.Junction("state", real);
        var reason = StateFolder.EnsureSecure(link, new List<string>(), new[] { Me });
        Assert.NotNull(reason);
        Assert.Contains("link", reason);
    }

    [Fact]
    public void ATooOpenFolderIsTightenedAndALinkInsideIsRefused()
    {
        using var sb = new Sandbox();
        var dir = sb.Dir("state");
        try
        {
            // this account owns the sandbox, so for the test it counts as the trusted owner
            var notes = new List<string>();
            var reason = StateFolder.EnsureSecure(dir, notes, new[] { Me });
            Assert.Null(reason);
            var sec = new DirectoryInfo(dir).GetAccessControl();
            Assert.True(sec.AreAccessRulesProtected);
            Assert.Empty(StateFolder.PermissiveEntries(sec, new[] { Me }));
            var rules = sec.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            Assert.DoesNotContain(rules, r => r.IdentityReference == Me && (r.FileSystemRights & FileSystemRights.WriteData) != 0);

            // the owner can always change the DACL: open it up again just to plant a link, then expect the refusal
            new DirectoryInfo(dir).SetAccessControl(With((Me, FileSystemRights.FullControl)));
            sb.Junction("state\\inside", sb.Dir("elsewhere"));
            reason = StateFolder.EnsureSecure(dir, new List<string>(), new[] { Me });
            Assert.NotNull(reason);
            Assert.Contains("junction", reason);
        }
        finally { try { new DirectoryInfo(dir).SetAccessControl(With((Me, FileSystemRights.FullControl))); } catch { } }
    }

    [Fact]
    public void MigrationCopiesValidFilesOnceAndNeverOverwrites()
    {
        using var sb = new Sandbox();
        var src = sb.Dir("old"); var dst = sb.Dir("new");
        File.WriteAllText(Path.Combine(src, "settings.json"), "{\"CpuAlertC\":88}");
        File.WriteAllText(Path.Combine(src, "sessions.json"), "this is not json");
        File.WriteAllText(Path.Combine(src, "quicksweep.log"), "old log\n");
        File.WriteAllText(Path.Combine(dst, "quicksweep.log"), "new log\n");               // exists already: kept
        var notes = new List<string>();
        Assert.Equal(1, StateFolder.MigrateFrom(src, dst, notes.Add));
        Assert.Equal("{\"CpuAlertC\":88}", File.ReadAllText(Path.Combine(dst, "settings.json")));
        Assert.False(File.Exists(Path.Combine(dst, "sessions.json")));                  // invalid JSON is not trusted
        Assert.Equal("new log\n", File.ReadAllText(Path.Combine(dst, "quicksweep.log")));
        Assert.Contains(notes, n => n.Contains("not valid JSON"));
        File.WriteAllText(Path.Combine(src, "drivehealth.json"), "{}");
        Assert.Equal(-1, StateFolder.MigrateFrom(src, dst));                            // once only
        Assert.False(File.Exists(Path.Combine(dst, "drivehealth.json")));
    }

    [Fact]
    public void MigrationRefusesASourceFolderThatIsAJunction()
    {
        using var sb = new Sandbox();
        var real = sb.Dir("real");
        File.WriteAllText(Path.Combine(real, "settings.json"), "{}");
        var link = sb.Junction("old", real);
        var dst = sb.Dir("new");
        Assert.Equal(0, StateFolder.MigrateFrom(link, dst));
        Assert.False(File.Exists(Path.Combine(dst, "settings.json")));
    }
}

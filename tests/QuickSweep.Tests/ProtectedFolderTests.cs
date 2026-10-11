using QuickSweep.Core;
using QuickSweep.Core.Cleaner;
using Xunit;

namespace QuickSweep.Tests;

/// <summary>Protected-folder patterns that mean what they say, and personal folders protected where they really are.</summary>
public class ProtectedPatternTests
{
    static bool Allowed(string path, params string[] protectedFolders) => new PathGuard(protectedFolders).Check(path).Allowed;

    [Fact]
    public void QuestionMarkStandsForOneCharacter()
    {
        Assert.False(Allowed(@"D:\Projects\keep\x", @"D:\Pro?ects\keep"));
        Assert.False(Allowed(@"D:\Prosects\keep", @"D:\Pro?ects\keep"));
        Assert.True(Allowed(@"D:\Proects\keep\x", @"D:\Pro?ects\keep"));            // ? is exactly one character, not none
        Assert.True(Allowed(@"D:\Proooects\keep\x", @"D:\Pro?ects\keep"));          // and not two
        Assert.False(Allowed(@"E:\Work\a", @"*:\Work\?"));                          // any drive, one-letter folder
    }

    [Fact]
    public void QuestionMarkWorksOnARealFolder()
    {
        using var sb = new Sandbox();
        sb.File(@"pro1ects\keep\a.txt"); sb.File(@"pro22ects\other\b.txt");
        var guard = new PathGuard(new[] { Path.Combine(sb.Root, "pro?ects") });
        Assert.False(guard.CheckTree(Path.Combine(sb.Root, "pro1ects", "keep")).Allowed);
        Assert.False(guard.Check(Path.Combine(sb.Root, "pro1ects")).Allowed);
        Assert.True(guard.CheckTree(Path.Combine(sb.Root, "pro22ects", "other")).Allowed);
    }

    [Theory]
    [InlineData("Important")]
    [InlineData(@".\Important")]
    [InlineData(@"..\Important")]
    [InlineData(@"\Important")]
    [InlineData(@"D:Important")]
    [InlineData(@"Documents\Work")]
    [InlineData(@"%NO_SUCH_VARIABLE%\Work")]
    [InlineData(@"\\server\share\Work")]
    [InlineData(@"D:\a\..\b")]
    [InlineData(@"D:\a\.\b")]
    [InlineData(@"D:\bad|name")]
    [InlineData(@"D:\a:stream")]
    [InlineData("")]
    [InlineData("   ")]
    public void ARelativeOrMalformedPatternIsAnErrorNotASilentNothing(string pattern)
    {
        var c = PathGuard.CheckUserPattern(pattern);
        Assert.NotNull(c.Error);
        Assert.False(string.IsNullOrWhiteSpace(c.Error));
        // and the guard does not pretend to honour it: the same folder name somewhere real stays unprotected, nothing throws
        Assert.True(Allowed(@"C:\Temp\Important", pattern));
    }

    [Theory]
    [InlineData(@"D:\Important")]
    [InlineData(@"d:/Important/")]
    [InlineData("\"D:\\Important\"")]
    [InlineData(@"D:\Projects\**\src")]
    [InlineData(@"D:\Pro?ects\*\src")]
    [InlineData(@"*:\Keep")]
    [InlineData(@"%LOCALAPPDATA%\Keep")]
    [InlineData(@"%USERPROFILE%\Work")]
    public void FullPathsAreAccepted(string pattern) => Assert.Null(PathGuard.CheckUserPattern(pattern).Error);

    [Fact]
    public void TrustedVariablesAreFilledInInThePatternThatIsMatched()
    {
        var c = PathGuard.CheckUserPattern(@"%USERPROFILE%\Work\Keep");
        Assert.Equal(Path.Combine(TrustedPaths.Profile, @"Work\Keep"), c.Pattern);
        Assert.False(Allowed(Path.Combine(TrustedPaths.Profile, @"Work\Keep\x"), @"%USERPROFILE%\Work\Keep"));
    }

    [Fact]
    public void AValidPatternThatMatchesNothingYetIsOnlyAWarning()
    {
        using var sb = new Sandbox();
        sb.File(@"pro1ects\src\a.cs"); sb.File(@"deep\er\src\b.cs"); sb.Dir("exists");
        Assert.Null(PathGuard.CheckUserPattern(Path.Combine(sb.Root, "exists")).Warning);
        Assert.Null(PathGuard.CheckUserPattern(Path.Combine(sb.Root, "pro?ects")).Warning);
        Assert.Null(PathGuard.CheckUserPattern(Path.Combine(sb.Root, "**", "src")).Warning);
        Assert.Null(PathGuard.CheckUserPattern(Path.Combine(sb.Root, "exists", "**")).Warning);        // a trailing ** only means "and everything below"
        var typo = PathGuard.CheckUserPattern(Path.Combine(sb.Root, "exits"));
        Assert.Null(typo.Error);                                                                         // legal: the folder may appear later
        Assert.Contains("no folder matches", typo.Warning);
        Assert.NotNull(PathGuard.CheckUserPattern(Path.Combine(sb.Root, "pr?jects-x")).Warning);
    }

    [Fact]
    public void BadLinesAreSkippedAndGoodLinesStillProtect()
    {
        using var sb = new Sandbox();
        sb.File(@"keep\a.txt");
        var guard = new PathGuard(new[] { "Important", Path.Combine(sb.Root, "keep"), @"..\x" });
        Assert.False(guard.Check(Path.Combine(sb.Root, "keep", "a.txt")).Allowed);
        Assert.Equal(3, guard.UserPatterns.Count);                       // the helper receives them as written and applies the same rules
    }

    [Fact]
    public void TheBuiltInProtectionIsUntouchedByUserLines()
    {
        var guard = new PathGuard(new[] { "garbage", @"D:\Mine" });
        Assert.False(guard.Check(@"C:\Windows\System32").Allowed);
        Assert.False(guard.Check(@"C:\Users\someone\Documents\a.docx").Allowed);
        Assert.False(guard.Check(@"E:\proj\.git").Allowed);
    }
}

public class PersonalFolderTests
{
    static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    [Fact]
    public void AFolderMovedToAnotherDriveIsProtectedThere()
    {
        var guard = new PathGuard(Array.Empty<string>(), new[] { @"E:\Moved\Downloads", @"E:\Moved\Documents" });
        Assert.False(guard.Check(@"E:\Moved\Downloads").Allowed);
        Assert.False(guard.Check(@"E:\Moved\Downloads\installers\old.exe").Allowed);
        Assert.False(guard.CheckTree(@"E:\Moved\Documents").Allowed);
        Assert.True(guard.Check(@"E:\Moved\Scratch").Allowed);
        Assert.True(guard.Check(@"E:\Moved\DownloadsCopy").Allowed);       // a sibling with the same prefix is not inside it
    }

    [Fact]
    public void ResolvedFoldersFromBothUsersAndTheDefaultsAreAllListed()
    {
        string profile = TrustedPaths.Profile;
        var ownFolders = new Dictionary<Guid, string>
        {
            [TrustedPaths.PersonalFolderIds.Single(f => f.Name == "Downloads").Id] = @"E:\Moved\Downloads",
            [TrustedPaths.PersonalFolderIds.Single(f => f.Name == "Documents").Id] = @"E:\Moved\Docs",
            [TrustedPaths.PersonalFolderIds.Single(f => f.Name == "Desktop").Id] = profile,                      // broken answer: the profile itself
            [TrustedPaths.PersonalFolderIds.Single(f => f.Name == "Videos").Id] = @"\\server\share\Videos",     // a network path is not a local personal folder
        };
        var shellFolders = new Dictionary<Guid, string> { [TrustedPaths.PersonalFolderIds.Single(f => f.Name == "Downloads").Id] = @"G:\Other\Downloads" };
        int closed = 0;
        var list = TrustedPaths.ComputeUserFolders(
            (id, token) => token == 0 ? ownFolders.GetValueOrDefault(id) : shellFolders.GetValueOrDefault(id),
            () => 42, t => { Assert.Equal(42, (int)t); closed++; });
        Assert.Contains(@"E:\Moved\Downloads", list);
        Assert.Contains(@"E:\Moved\Docs", list);
        Assert.Contains(@"G:\Other\Downloads", list);                                             // the desktop shell's user, when it is another account
        Assert.Contains(Path.Combine(profile, "Downloads"), list, StringComparer.OrdinalIgnoreCase);   // defaults are always there
        Assert.Contains(Path.Combine(profile, "Documents"), list, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(list, f => f.Equals(profile, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(list, f => f.StartsWith(@"\\"));
        Assert.Equal(1, closed);
    }

    [Fact]
    public void WithoutADesktopShellTheProcessUsersFoldersAreStillResolved()
    {
        var id = TrustedPaths.PersonalFolderIds.Single(f => f.Name == "Pictures").Id;
        var list = TrustedPaths.ComputeUserFolders((i, t) => i == id && t == 0 ? @"E:\Photos" : null, () => 0, _ => Assert.Fail("no token was opened"));
        Assert.Contains(@"E:\Photos", list);
    }

    [Fact]
    public void WindowsOwnAnswerForThisUserIsProtectedByTheDefaultGuard()
    {
        var guard = new PathGuard(Array.Empty<string>());
        foreach (var (name, id) in TrustedPaths.PersonalFolderIds)
        {
            var real = TrustedPaths.KnownFolder(id, 0);
            if (string.IsNullOrEmpty(real) || real.Length < 4 || real.StartsWith(@"\\")) continue;
            if (real.Equals(TrustedPaths.Profile, StringComparison.OrdinalIgnoreCase)) continue;
            Assert.False(guard.Check(Path.Combine(real, "some", "file.bin")).Allowed, $"{name} at {real}");
        }
        Assert.False(guard.Check(Path.Combine(TrustedPaths.Profile, "Downloads", "x.zip")).Allowed);
        Assert.Contains(TrustedPaths.UserFolders, f => f.Equals(Path.Combine(TrustedPaths.Profile, "Documents"), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheTempFolderAndTheProfileItselfAreNotSwallowedByThePersonalFolders()
    {
        var guard = new PathGuard(Array.Empty<string>());
        Assert.True(guard.Check(Path.Combine(TrustedPaths.Temp, "quicksweep-leftover")).Allowed);          // the temp cleaner keeps working
        Assert.DoesNotContain(TrustedPaths.UserFolders, f => TrustedPaths.Local.StartsWith(f + "\\", StringComparison.OrdinalIgnoreCase));
    }
}

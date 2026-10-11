using QuickSweep.Core;
using QuickSweep.Core.Cleaner;
using QuickSweep.Native;
using Xunit;

namespace QuickSweep.Tests;

/// <summary>The defaults are neutral, old settings files still load, and the user-defined lists drive the cleaner and Game Mode.</summary>
public class SettingsDefaultsTests
{
    [Fact]
    public void ExtraFoldersAndProtectedFoldersAreEmptyByDefault()
    {
        var s = new AppSettings();
        Assert.Empty(s.ExtraScanFolders);
        Assert.Empty(s.ProtectedFolders);
        Assert.Empty(s.ExtraGameFolders);
    }

    [Fact]
    public void BackgroundWorkDefaultsAreGenericTools()
    {
        var s = new AppSettings();
        Assert.Contains("ffmpeg", s.BackgroundProcesses);
        Assert.Contains("blender", s.BackgroundProcesses);
        Assert.Contains("node", s.ScriptRuntimes);
        Assert.Contains("claude", s.AgentApps);
        Assert.Contains("codex", s.AgentApps);
    }

    [Fact]
    public void NoDefaultNamesAPersonOrADriveOrAProject()
    {
        var s = new AppSettings();
        var all = s.BackgroundProcesses.Concat(s.ScriptRuntimes).Concat(s.AgentApps).Concat(AppSettings.DefaultBackgroundProcesses)
            .Concat(PathGuard.BuiltInProtection).ToList();
        foreach (var text in all)
        {
            Assert.DoesNotMatch(@"^[A-Za-z]:\\", text);
            Assert.DoesNotContain("Showcase", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("frames", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AFileFromAnEarlierVersionIsFoldedIntoTheNewLists()
    {
        const string old = """
            { "ExtraDenyPatterns": ["D:\\Keep"], "ExtraHeavyProcesses": ["mytool"], "ProjectRoots": ["D:\\p"], "ToolRoots": ["D:\\t"], "CpuAlertC": 88 }
            """;
        var s = AppSettings.Parse(old)!;
        Assert.Equal(88, s.CpuAlertC);
        Assert.Contains(@"D:\Keep", s.ProtectedFolders);
        Assert.Contains("mytool", s.BackgroundProcesses);
        Assert.Contains("ffmpeg", s.BackgroundProcesses);          // the defaults stay
        Assert.Null(s.LegacyDenyPatterns);                           // never written back
        Assert.DoesNotContain("ExtraDenyPatterns", s.ToJson());
        Assert.DoesNotContain("ProjectRoots", s.ToJson());
    }

    [Fact]
    public void ImportOverlaysOnlyTheKeysInTheFile()
    {
        var cur = new AppSettings { CpuAlertC = 80, TrayPromotedFor = "x.exe" };
        cur.ExtraGameFolders.Add(@"E:\Games");
        int n = AppSettings.Import(cur, """{ "GpuAlertC": 77, "TrayPromotedFor": "evil.exe", "ExtraScanFolders": [ { "Pattern": "D:\\p\\*\\build", "Label": "Build", "IdleDays": 30 } ] }""", out var r);
        Assert.Equal(2, n);                                           // the installation key is not imported
        Assert.Equal(77, r.GpuAlertC);
        Assert.Equal(80, r.CpuAlertC);
        Assert.Equal("x.exe", r.TrayPromotedFor);
        Assert.Contains(@"E:\Games", r.ExtraGameFolders);
        Assert.Equal("Build", Assert.Single(r.ExtraScanFolders).Label);
    }

    [Theory]
    [InlineData("[1,2,3]")]
    [InlineData("not json")]
    [InlineData("")]
    public void ImportRefusesFilesThatAreNotSettings(string text)
    {
        Assert.Equal(-1, AppSettings.Import(new AppSettings(), text, out _));
    }

    [Fact]
    public void CopyFromReplacesEverythingInTheLiveObject()
    {
        var live = new AppSettings();
        var other = new AppSettings { LowSpaceGb = 33 };
        other.ProtectedFolders.Add(@"D:\Keep");
        live.CopyFrom(other);
        Assert.Equal(33, live.LowSpaceGb);
        Assert.Contains(@"D:\Keep", live.ProtectedFolders);
    }

    [Theory]
    [InlineData(@"D:\p\*\build | Build output | 30", @"D:\p\*\build", "Build output", 30)]
    [InlineData(@"D:\p\cache|Cache", @"D:\p\cache", "Cache", 0)]
    [InlineData(@"D:\p\cache", @"D:\p\cache", "", 0)]
    public void ScanFolderLinesAreParsed(string line, string pattern, string label, int idle)
    {
        var f = ScanFolder.Parse(line)!;
        Assert.Equal(pattern, f.Pattern); Assert.Equal(label, f.Label); Assert.Equal(idle, f.IdleDays);
    }

    [Fact]
    public void AnEmptyScanFolderLineIsIgnored() => Assert.Null(ScanFolder.Parse("  | label"));
}

public class ExtraFolderScanTests
{
    [Fact]
    public void PatternMatchesFoldersAndSkipsLinksAndFiles()
    {
        using var sb = new Sandbox();
        sb.File(@"proj-a\build\x.o"); sb.File(@"proj-b\build\y.o"); sb.File(@"proj-c\src\z.c"); sb.File(@"proj-d\build");   // proj-d\build is a file
        var outside = sb.Dir("elsewhere", "build");
        sb.Junction(@"proj-e\build-link", outside);
        var hits = PatternScan.Expand(Path.Combine(sb.Root, "proj-*", "build")).Select(p => Path.GetFileName(Path.GetDirectoryName(p)!)).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "proj-a", "proj-b" }, hits);
        Assert.Empty(PatternScan.Expand(Path.Combine(sb.Root, "proj-e", "build-*")));          // a junction is never followed
    }

    [Fact]
    public void DoubleStarFindsFoldersAtAnyDepthButNotInsideNodeModulesOrGit()
    {
        using var sb = new Sandbox();
        sb.File(@"a\out\frames-1\0001.png"); sb.File(@"b\deep\er\out\frames-2\0001.png"); sb.File(@"c\node_modules\x\out\frames-3\0001.png");
        sb.File(@"d\out\other\0001.png");
        var hits = PatternScan.Expand(Path.Combine(sb.Root, "**", "out", "frames*")).Select(p => Path.GetFileName(p)).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "frames-1", "frames-2" }, hits);
    }

    [Fact]
    public void DoubleStarAlsoMatchesTheStartFolderItself()
    {
        using var sb = new Sandbox();
        sb.File(@"node_modules\pkg\index.js"); sb.File(@"p1\node_modules\pkg\index.js"); sb.File(@"p1\sub\node_modules\pkg\index.js");
        Assert.Equal(3, PatternScan.Expand(Path.Combine(sb.Root, "**", "node_modules")).Count);
    }

    [Fact]
    public void OnlyWellKnownVariablesWorkInPatternsAndTheyComeFromWindowsRecords()
    {
        Assert.StartsWith(TrustedPaths.Temp, TrustedPaths.Expand(@"%TEMP%\x"), StringComparison.OrdinalIgnoreCase);
        using var sb = new Sandbox();
        sb.File(@"tool\cache\a.bin");
        Environment.SetEnvironmentVariable("QUICKSWEEP_TEST_ROOT", sb.Root);
        try { Assert.Empty(PatternScan.Expand(@"%QUICKSWEEP_TEST_ROOT%\tool\cache")); }       // arbitrary variables are not expanded: anything running as the user can set them
        finally { Environment.SetEnvironmentVariable("QUICKSWEEP_TEST_ROOT", null); }
    }

    [Theory]
    [InlineData(@"relative\path\*")]
    [InlineData(@"D:\a\..\b")]
    [InlineData(@"D:\a\**")]
    [InlineData(@"\\server\share\*")]
    [InlineData("")]
    public void UnsafePatternsFindNothing(string pattern) => Assert.Empty(PatternScan.Expand(pattern));

    [Fact]
    public void ProtectedFoldersStillWinOverAnExtraPattern()
    {
        using var sb = new Sandbox();
        sb.File(@"p1\build\a.o"); sb.File(@"p2\build\b.o");
        var keep = Path.Combine(sb.Root, "p2");
        var guard = new PathGuard(new[] { keep });
        var matches = PatternScan.Expand(Path.Combine(sb.Root, "p*", "build"));
        Assert.True(guard.Check(matches.Single(m => m.Contains("p1"))).Allowed);
        Assert.False(guard.Check(matches.Single(m => m.Contains("p2"))).Allowed);
    }

    [Fact]
    public void TheCleanerOffersMatchesOfTheUsersPatternsAndNothingElseByDefault()
    {
        using var sb = new Sandbox();
        sb.File(@"p1\build\a.o");
        var s = new AppSettings();
        var cat = Categories.All().Single(c => c.Id == "extra-folders");               // only this category: no scan of the real machine
        Assert.Empty(cat.Roots(new CleanContext(s)));                                    // empty by default
        s.ExtraScanFolders.Add(new ScanFolder { Pattern = Path.Combine(sb.Root, "p*", "build"), Label = "Build output" });
        var root = Assert.Single(cat.Roots(new CleanContext(s)));
        Assert.StartsWith("Build output:", root.Display);
        Assert.Equal(DeleteMode.Recycle, cat.Mode);
    }
}

public class AgentMatchingTests
{
    static ProcInfo P(string name) => new(500, 1, name, 0, 0, 0, 0, 0, 1, 1);

    [Fact]
    public void AnAgentIsRecognisedByItsFileName()
    {
        Assert.True(BackgroundWork.IsAgent(P("claude.exe"), new[] { "claude", "codex" }));
        Assert.True(BackgroundWork.IsAgent(P("Codex.EXE"), new[] { "claude", "codex" }));
        Assert.False(BackgroundWork.IsAgent(P("notepad.exe"), new[] { "claude", "codex" }));
        Assert.False(BackgroundWork.IsAgent(P("claude.exe"), Array.Empty<string>()));
    }
}

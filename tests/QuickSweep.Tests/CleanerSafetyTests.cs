using System.Diagnostics;
using QuickSweep.Core.Cleaner;
using Xunit;

namespace QuickSweep.Tests;

/// <summary>Sandbox folder for tests that touch the disk. Set QUICKSWEEP_TEST_TMP to keep it off the system drive.</summary>
public sealed class Sandbox : IDisposable
{
    public string Root { get; }
    public Sandbox()
    {
        var baseDir = Environment.GetEnvironmentVariable("QUICKSWEEP_TEST_TMP") ?? Path.GetTempPath();
        Root = Path.Combine(baseDir, "quicksweep-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
    }
    public string Dir(params string[] parts) { var p = Path.Combine(new[] { Root }.Concat(parts).ToArray()); Directory.CreateDirectory(p); return p; }
    public string File(string rel, string content = "x")
    {
        var p = Path.Combine(Root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); System.IO.File.WriteAllText(p, content); return p;
    }
    /// <summary>NTFS junction (no admin needed).</summary>
    public string Junction(string linkRel, string target)
    {
        var link = Path.Combine(Root, linkRel);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!; p.WaitForExit();
        Assert.True(p.ExitCode == 0, "mklink failed");
        return link;
    }
    public void Dispose()
    {
        try
        {
            // remove junctions first so cleanup can never reach a target
            foreach (var d in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).ToList())
                try { if (System.IO.File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d, false); } catch { }
            Directory.Delete(Root, true);
        }
        catch { }
    }
}

public class PathGuardTests
{
    static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string Win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static readonly string Pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    // two folders a user might add in Settings: one exact folder, one pattern
    static PathGuard Guard() => new(new[] { @"D:\Important", @"D:\Projects\**\src" });

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32\drivers\etc")]
    [InlineData(@"C:\Windows\SoftwareDistribution\Download")]
    [InlineData(@"C:\Windows\Temp")]                       // the folder itself: only its contents are cleanable
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files\NVIDIA Corporation\Installer2")]
    [InlineData(@"C:\Program Files (x86)\Steam")]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\Start Menu")]
    [InlineData(@"C:\ProgramData\NVIDIA Corporation\Downloader")]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\Users")]
    [InlineData(@"D:\Important")]
    [InlineData(@"D:\Important\clips\intro.mp4")]
    [InlineData(@"D:\Projects\app\src")]
    [InlineData(@"D:\Projects\app\sub\src\main.ts")]
    [InlineData(@"D:\Projects\app\.git")]
    [InlineData(@"D:\Projects\app\.git\objects\ab\cd")]
    [InlineData(@"E:\anywhere\at\all\.git")]
    [InlineData(@"C:\Users\someone\Documents\thesis.docx")]
    [InlineData(@"C:\Users\someone\Desktop")]
    [InlineData(@"C:\Users\someone\Pictures\2020")]
    [InlineData(@"C:\Users\someone\Videos")]
    [InlineData(@"C:\Users\someone\OneDrive - Company\Files")]
    [InlineData(@"C:\Users\someone\AppData\Local")]
    [InlineData(@"C:\Users\someone\AppData\Roaming")]
    [InlineData(@"\\server\share\folder")]
    [InlineData(@"C:\Users\x\AppData\Local\Temp\..\..\..\..\Windows")]
    [InlineData(@"C:\PROGRA~1\Anything")]                  // 8.3 short name of Program Files
    [InlineData(@"\\?\C:\Windows\System32")]
    [InlineData(@"")]
    public void Denies(string path) => Assert.False(Guard().Check(path).Allowed, path);

    [Theory]
    [InlineData(@"C:\Windows\Temp\old-installer")]
    [InlineData(@"C:\ProgramData\NVIDIA Corporation\Downloader\abc123")]
    [InlineData(@"C:\ProgramData\NVIDIA Corporation\NVIDIA app\UpdateFramework\ota-artifacts\drv")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows\WER\ReportArchive\Report1")]
    [InlineData(@"D:\Projects\app\node_modules")]
    [InlineData(@"D:\Projects\app\build")]
    [InlineData(@"E:\Tools\some-tool\cache")]
    [InlineData(@"C:\Users\someone\AppData\Local\NVIDIA\DXCache")]
    [InlineData(@"C:\Users\someone\AppData\Local\Temp\pip-unpack-abc")]
    [InlineData(@"C:\Users\someone\AppData\Local\Mozilla\Firefox\Profiles\x.default\cache2")]
    public void Allows(string path) => Assert.True(Guard().Check(path).Allowed, path + " -> " + Guard().Check(path).Reason);

    [Fact]
    public void ShortNameOfRealFolderIsExpanded()
    {
        // PROGRA~1 exists on this machine; a child that does not exist must still be refused
        Assert.False(Guard().Check(@"C:\PROGRA~1\does-not-exist-quicksweep").Allowed);
    }

    [Fact]
    public void TreeWithGitInsideIsRefused()
    {
        using var sb = new Sandbox();
        sb.File(@"proj\node_modules\dep\.git\HEAD");
        var g = new PathGuard(Array.Empty<string>());
        var r = g.CheckTree(Path.Combine(sb.Root, "proj", "node_modules"));
        Assert.False(r.Allowed);
        Assert.Contains(".git", r.Reason);
    }

    [Fact]
    public void TreeWithProtectedPatternInsideIsRefused()
    {
        using var sb = new Sandbox();
        sb.File(@"protected-copy\keep\a.txt");
        sb.File(@"junk\x.txt");
        var g = new PathGuard(new[] { Path.Combine(sb.Root, "protected-copy") });
        Assert.False(g.CheckTree(sb.Root).Allowed);                         // ancestor of a protected folder
        Assert.True(g.CheckTree(Path.Combine(sb.Root, "junk")).Allowed);
    }

    [Fact]
    public void PathBelowAJunctionIsRefused()
    {
        using var sb = new Sandbox();
        var real = sb.Dir("real", "cache");
        var link = sb.Junction("via-link", Path.Combine(sb.Root, "real"));
        var g = new PathGuard(Array.Empty<string>());
        Assert.False(g.CheckTree(Path.Combine(link, "cache")).Allowed);
        Assert.True(g.CheckTree(real).Allowed);
    }

    [Fact]
    public void JunctionInsideATreeIsNotFollowedByTheCheck()
    {
        using var sb = new Sandbox();
        var outside = sb.Dir("outside");
        sb.File(@"outside\.git\HEAD");                                      // would be protected if followed
        sb.Dir("tree");
        sb.Junction(@"tree\link", outside);
        var g = new PathGuard(Array.Empty<string>());
        Assert.True(g.CheckTree(Path.Combine(sb.Root, "tree")).Allowed);
    }
}

public class DeleterTests
{
    static PathGuard Guard() => new(Array.Empty<string>());

    static CleanItem Item(string path, DeleteMode mode = DeleteMode.Permanent, bool dir = true)
        => new() { Path = path, IsDirectory = dir, Display = path, Mode = mode, Category = "test" };

    [Fact]
    public void DeletesATreeIncludingReadOnlyFiles()
    {
        using var sb = new Sandbox();
        var f = sb.File(@"t\a\b\ro.txt");
        File.SetAttributes(f, FileAttributes.ReadOnly);
        sb.File(@"t\c.txt");
        var res = new Deleter(Guard(), false).Run(new[] { Item(Path.Combine(sb.Root, "t")) }, false, null, default);
        Assert.True(res[0].Success, res[0].Message);
        Assert.False(Directory.Exists(Path.Combine(sb.Root, "t")));
    }

    [Fact]
    public void DryRunChangesNothing()
    {
        using var sb = new Sandbox();
        sb.File(@"t\a.txt");
        var res = new Deleter(Guard(), true).Run(new[] { Item(Path.Combine(sb.Root, "t")) }, false, null, default);
        Assert.True(res[0].Success);
        Assert.StartsWith("dry run", res[0].Message);
        Assert.True(File.Exists(Path.Combine(sb.Root, "t", "a.txt")));
    }

    [Fact]
    public void JunctionInsideATreeIsRemovedButItsTargetSurvives()
    {
        using var sb = new Sandbox();
        var outside = sb.Dir("outside");
        var precious = sb.File(@"outside\precious.txt", "do not lose me");
        sb.File(@"tree\x.txt");
        sb.Junction(@"tree\sub\link", outside);
        var res = new Deleter(Guard(), false).Run(new[] { Item(Path.Combine(sb.Root, "tree")) }, false, null, default);
        Assert.True(res[0].Success, res[0].Message);
        Assert.False(Directory.Exists(Path.Combine(sb.Root, "tree")));
        Assert.True(File.Exists(precious));
        Assert.Equal("do not lose me", File.ReadAllText(precious));
    }

    [Fact]
    public void AnItemThatIsItselfAJunctionOnlyLosesTheLink()
    {
        using var sb = new Sandbox();
        var outside = sb.Dir("outside");
        var precious = sb.File(@"outside\precious.txt", "keep");
        var link = sb.Junction("thelink", outside);
        var res = new Deleter(Guard(), false).Run(new[] { Item(link) }, false, null, default);
        Assert.True(res[0].Success, res[0].Message);
        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(precious));
    }

    [Fact]
    public void ProtectedPathsAreRefusedBeforeAnythingIsTouched()
    {
        var bogus = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "quicksweep-test-does-not-exist");
        var res = new Deleter(Guard(), false).Run(new[] { Item(bogus) }, false, null, default);
        Assert.False(res[0].Success);
        Assert.StartsWith("refused", res[0].Message);
        Assert.False(Directory.Exists(bogus));
    }

    [Fact]
    public void TreeContainingGitIsRefused()
    {
        using var sb = new Sandbox();
        sb.File(@"repo\.git\HEAD");
        sb.File(@"repo\src.txt");
        var res = new Deleter(Guard(), false).Run(new[] { Item(Path.Combine(sb.Root, "repo")) }, false, null, default);
        Assert.False(res[0].Success);
        Assert.True(File.Exists(Path.Combine(sb.Root, "repo", "src.txt")));
    }

    [Fact]
    public void RecycleSendsAFileToTheBin()
    {
        using var sb = new Sandbox();
        var f = sb.File("recycle-me-quicksweep-test.txt", "tiny test file");
        var res = new Deleter(Guard(), false).Run(new[] { Item(f, DeleteMode.Recycle, false) }, false, null, default);
        Assert.True(res[0].Success, res[0].Message);
        Assert.False(File.Exists(f));
    }
}

using System.Security.Principal;
using System.Text.RegularExpressions;
using QuickSweep.Core;
using QuickSweep.Core.Cleaner;
using QuickSweep.Native;
using Xunit;

namespace QuickSweep.Tests;

// ---- 1. the elevated process does not take its state folder from the environment or the command line -------------------------

public class StateFolderOverrideTests
{
    const string Pd = @"C:\ProgramData\QuickSweep", User = @"C:\Users\someone\AppData\Local\QuickSweep";

    [Fact]
    public void AnElevatedProcessIgnoresTheOverrideAndUsesTheSecuredFolder()
    {
        var r = Paths.ResolveFor(elevated: true, overridePath: @"D:\evil", Pd, User);
        Assert.Equal(Pd, r.Data);
        Assert.False(r.Custom);
        Assert.True(r.SecureCheck);                     // EnsureSecure is applied to the folder it uses
        Assert.Equal(@"D:\evil", r.IgnoredOverride);    // and the refusal is noted in the log
        Assert.Equal(User, r.Inbox);
    }

    [Fact]
    public void AnElevatedProcessWithoutAnOverrideStillGetsTheSecurityCheck()
    {
        var r = Paths.ResolveFor(true, null, Pd, User);
        Assert.Equal(Pd, r.Data); Assert.True(r.SecureCheck); Assert.Null(r.IgnoredOverride);
        Assert.True(Paths.ResolveFor(true, "  ", Pd, User).SecureCheck);
    }

    [Fact]
    public void AnUnelevatedRunMayMoveItsFolder()
    {
        var r = Paths.ResolveFor(false, @"D:\scratch", Pd, User);
        Assert.Equal(@"D:\scratch", r.Data); Assert.True(r.Custom); Assert.False(r.SecureCheck);
        var plain = Paths.ResolveFor(false, null, Pd, User);
        Assert.Equal(User, plain.Data); Assert.False(plain.Custom);
    }

    [Theory]
    [InlineData(true, new[] { "--dev", "--data=D:\\x" }, null, "D:\\x")]            // elevated: never
    [InlineData(false, new[] { "--data=D:\\x" }, null, "D:\\x")]                     // not --dev: never
    [InlineData(false, new[] { "--dev", "--data=D:\\x" }, "D:\\x", null)]            // non-elevated --dev: yes
    [InlineData(false, new[] { "--dev" }, null, null)]
    [InlineData(false, new[] { "--dev", "--data=" }, null, null)]
    public void TheDataSwitchIsHonoredOnlyForANonElevatedDevRun(bool elevated, string[] args, string? honored, string? ignored)
    {
        Assert.Equal(honored, StartupArgs.DataFolder(args, elevated, out var ign));
        Assert.Equal(ignored, ign);
    }
}

// ---- 3. environment-derived locations ------------------------------------------------------------------------------------------

[Collection("environment")]
public class TrustedLocationTests
{
    [Fact]
    public void WellKnownVariablesComeFromWindowsNotFromTheEnvironment()
    {
        var saved = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        try
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", @"X:\evil");
            Assert.Equal(Path.Combine(TrustedPaths.Local, "x"), TrustedPaths.Expand(@"%LOCALAPPDATA%\x"));
            Assert.DoesNotContain("evil", TrustedPaths.Temp);
            Assert.DoesNotContain("evil", TrustedPaths.Expand(@"%TEMP%\y"));
        }
        finally { Environment.SetEnvironmentVariable("LOCALAPPDATA", saved); }
    }

    [Fact]
    public void OtherVariablesAreLeftAloneSoTheyMatchNothing()
    {
        Assert.Equal(@"%QUICKSWEEP_ANYTHING%\x", TrustedPaths.Expand(@"%QUICKSWEEP_ANYTHING%\x"));
        Assert.Empty(PatternScan.Expand(@"%QUICKSWEEP_ANYTHING%\x\*"));
    }

    [Fact]
    public void TheProfileComesFromTheProfileListAndTempIsBelowIt()
    {
        Assert.True(Directory.Exists(TrustedPaths.Profile));
        Assert.StartsWith(TrustedPaths.Profile, TrustedPaths.Temp, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TrustedPaths.Temp, new CleanContext(new AppSettings()).Temp);
        var userTemp = Categories.All().Single(c => c.Id == "user-temp");
        Assert.Equal(TrustedPaths.Temp, Assert.Single(userTemp.Roots(new CleanContext(new AppSettings()))).Path);
    }

    static string? Accept(string? value, Func<string, SecurityIdentifier?>? owner = null) => TrustedPaths.AcceptEnvCacheRoot(value, out _, owner);
    static string Why(string? value, Func<string, SecurityIdentifier?>? owner = null) { TrustedPaths.AcceptEnvCacheRoot(value, out var why, owner); return why; }

    [Fact]
    public void APlainUserFolderIsAccepted()
    {
        using var sb = new Sandbox();
        var dir = sb.Dir("pip-cache");
        Assert.Equal(dir, Accept(dir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"relative\cache")]
    [InlineData(@"\\server\share\cache")]
    [InlineData(@"\\?\C:\Windows\System32")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Users\x\..\..\Windows")]
    public void MalformedOrBroadRootsAreRejected(string? value) => Assert.Null(Accept(value));

    [Fact]
    public void SystemLocationsAndTheWholeProfileAreRejected()
    {
        Assert.Contains("system", Why(Path.Combine(TrustedPaths.Windows, "System32")));
        Assert.Contains("system", Why(TrustedPaths.ProgramFiles));
        Assert.Contains("system", Why(Path.Combine(TrustedPaths.ProgramData, "Microsoft")));
        Assert.Null(Accept(TrustedPaths.Profile));
        Assert.Null(Accept(Path.GetDirectoryName(TrustedPaths.Profile)));
    }

    [Fact]
    public void ALinkAndAPathThroughALinkAreRejected()
    {
        using var sb = new Sandbox();
        var real = sb.Dir("real", "cache");
        var link = sb.Junction("link", Path.Combine(sb.Root, "real"));
        Assert.Null(Accept(link));
        Assert.Null(Accept(Path.Combine(link, "cache")));
        Assert.NotNull(Accept(real));
    }

    [Fact]
    public void AFolderOwnedBySomebodyElseIsRejected()
    {
        using var sb = new Sandbox();
        var dir = sb.Dir("c");
        var me = WindowsIdentity.GetCurrent().User!;
        Assert.NotNull(Accept(dir, _ => me));
        Assert.NotNull(Accept(dir, _ => new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)));
        Assert.Contains("owned by", Why(dir, _ => new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null)));
        Assert.Null(Accept(dir, _ => new SecurityIdentifier("S-1-5-21-1-2-3-1001")));
        Assert.Null(Accept(dir, _ => null));
    }

    [Fact]
    public void CacheRootsFromTheEnvironmentAreFilteredWhenTheCleanerBuildsItsList()
    {
        using var sb = new Sandbox();
        var good = sb.Dir("nuget");
        var env = new Dictionary<string, string>
        {
            ["PIP_CACHE_DIR"] = Path.Combine(TrustedPaths.Windows, "System32"),   // an attacker's choice of a protected place
            ["UV_CACHE_DIR"] = @"\\attacker\share\x",
            ["NUGET_PACKAGES"] = good,
            ["npm_config_cache"] = TrustedPaths.Profile,
        };
        var roots = Categories.DevCaches(new CleanContext(new AppSettings()), n => env.GetValueOrDefault(n)).ToList();
        Assert.Contains(roots, r => r.Display == "NuGet packages (NUGET_PACKAGES)" && r.Path == good);
        Assert.DoesNotContain(roots, r => r.Display.Contains("PIP_CACHE_DIR") || r.Display.Contains("UV_CACHE_DIR") || r.Display.Contains("npm_config_cache"));
    }
}

[CollectionDefinition("environment", DisableParallelization = true)]
public class EnvironmentCollection { }

// ---- 2. deletion that cannot be redirected --------------------------------------------------------------------------------------

public class HandleDeleteTests
{
    static void SwapForJunction(Sandbox sb, string path, string target)
    {
        if (Directory.Exists(path)) Directory.Delete(path, false); else File.Delete(path);
        sb.Junction(Path.GetRelativePath(sb.Root, path), target);
    }

    [Fact]
    public void DeletesATreeWithReadOnlyAndNestedFiles()
    {
        using var sb = new Sandbox();
        var f = sb.File(@"t\a\b\ro.txt"); File.SetAttributes(f, FileAttributes.ReadOnly);
        sb.File(@"t\a\x.txt"); sb.File(@"t\y.txt"); sb.Dir("t", "empty");
        var o = HandleDelete.DeleteItem(Path.Combine(sb.Root, "t"));
        Assert.True(o.Ok, o.FirstError);
        Assert.False(Directory.Exists(Path.Combine(sb.Root, "t")));
    }

    [Fact]
    public void ALinkInsideIsRemovedAndItsTargetKept()
    {
        using var sb = new Sandbox();
        var outside = sb.Dir("outside"); sb.File(@"outside\keep.txt");
        sb.File(@"t\f.txt"); sb.Junction(@"t\link", outside);
        var o = HandleDelete.DeleteItem(Path.Combine(sb.Root, "t"));
        Assert.True(o.Ok, o.FirstError);
        Assert.Equal(1, o.LinksRemoved);
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(sb.Root, "t")));
    }

    [Fact]
    public void AFolderSwappedForAJunctionAfterItWasListedNeverReachesItsTarget()
    {
        using var sb = new Sandbox();
        var victim = sb.Dir("victim"); sb.File(@"victim\precious.txt"); sb.File(@"victim\deep\precious2.txt");
        sb.Dir("t", "a", "sub");                                     // an empty folder the attacker can replace
        sb.File(@"t\a\file.txt");
        bool swapped = false;
        var o = HandleDelete.DeleteItem(Path.Combine(sb.Root, "t"), new DeleteOptions
        {
            AfterEnumerate = (dir, names) =>
            {
                if (!dir.EndsWith(@"\a", StringComparison.OrdinalIgnoreCase) || swapped) return;
                swapped = true;                                      // between listing and deleting: sub becomes a junction to the victim
                SwapForJunction(sb, Path.Combine(sb.Root, "t", "a", "sub"), victim);
            }
        });
        Assert.True(swapped);
        Assert.True(File.Exists(Path.Combine(victim, "precious.txt")));
        Assert.True(File.Exists(Path.Combine(victim, "deep", "precious2.txt")));
        Assert.False(Directory.Exists(Path.Combine(sb.Root, "t")));
        Assert.Equal(1, o.LinksRemoved);                             // only the junction itself went
    }

    [Fact]
    public void AFileSwappedForAJunctionIsRemovedAsALinkOnly()
    {
        using var sb = new Sandbox();
        var victim = sb.Dir("victim"); sb.File(@"victim\precious.txt");
        sb.File(@"t\f.txt");
        HandleDelete.DeleteItem(Path.Combine(sb.Root, "t"), new DeleteOptions
        {
            AfterEnumerate = (dir, names) =>
            {
                if (names.Contains("f.txt")) SwapForJunction(sb, Path.Combine(sb.Root, "t", "f.txt"), victim);
            }
        });
        Assert.True(File.Exists(Path.Combine(victim, "precious.txt")));
        Assert.False(Directory.Exists(Path.Combine(sb.Root, "t")));
    }

    [Fact]
    public void ADeeperFolderSwappedForAJunctionWithContentsIsStillJustALink()
    {
        using var sb = new Sandbox();
        var victim = sb.Dir("victim"); sb.File(@"victim\x\y\precious.txt");
        sb.File(@"t\a\b\c.txt");
        HandleDelete.DeleteItem(Path.Combine(sb.Root, "t"), new DeleteOptions
        {
            AfterEnumerate = (dir, names) =>
            {
                // when the list of t\a is known, replace its child b (full of files) by renaming it away and linking to the victim
                if (dir.EndsWith(@"\t\a", StringComparison.OrdinalIgnoreCase))
                {
                    var b = Path.Combine(sb.Root, "t", "a", "b");
                    try { Directory.Move(b, b + "-moved"); SwapForJunction(sb, b, victim); } catch (IOException) { /* a held handle may stop the rename: also fine */ }
                }
            }
        });
        Assert.True(File.Exists(Path.Combine(victim, "x", "y", "precious.txt")));
    }

    [Fact]
    public void AFolderThatIsBeingDeletedCannotBeRenamedOrReplacedUnderneath()
    {
        using var sb = new Sandbox();
        sb.File(@"t\a\f.txt");
        bool refused = false;
        HandleDelete.DeleteItem(Path.Combine(sb.Root, "t"), new DeleteOptions
        {
            AfterEnumerate = (dir, names) =>
            {
                if (!dir.EndsWith(@"\t", StringComparison.OrdinalIgnoreCase)) return;
                // the handle on t (and on the parent folder above it) is held without delete/rename sharing
                try { Directory.Move(Path.Combine(sb.Root, "t"), Path.Combine(sb.Root, "t-moved")); }
                catch (IOException) { refused = true; }
                catch (UnauthorizedAccessException) { refused = true; }
            }
        });
        Assert.True(refused);
    }

    [Fact]
    public void AParentThatIsAJunctionOrBelowOneIsRefusedAndNothingIsTouched()
    {
        using var sb = new Sandbox();
        var real = sb.Dir("real"); sb.File(@"real\child\keep.txt");
        var link = sb.Junction("link", real);
        var o = HandleDelete.DeleteItem(Path.Combine(link, "child"));
        Assert.NotNull(o.Refused);
        Assert.True(File.Exists(Path.Combine(real, "child", "keep.txt")));
        Assert.NotNull(HandleDelete.DeleteItem(@"C:\").Refused);       // a drive root has no name to delete
    }

    [Fact]
    public void AFileInUseIsKeptAndTheRestIsDeleted()
    {
        using var sb = new Sandbox();
        sb.File(@"t\free.txt"); var busy = sb.File(@"t\busy.txt");
        using var hold = new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read);
        var o = HandleDelete.DeleteItem(Path.Combine(sb.Root, "t"));
        Assert.False(o.Ok);
        Assert.True(File.Exists(busy));
        Assert.False(File.Exists(Path.Combine(sb.Root, "t", "free.txt")));
    }

    [Fact]
    public void ACancelledDeleteStopsWithoutTouchingAnythingFurther()
    {
        using var sb = new Sandbox();
        sb.File(@"t\a.txt");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => HandleDelete.DeleteItem(Path.Combine(sb.Root, "t"), new DeleteOptions { Ct = cts.Token }));
        Assert.True(File.Exists(Path.Combine(sb.Root, "t", "a.txt")));
    }

    [Fact]
    public void ControlAPathBasedWalkIsReallyExposedToTheSameSwap()
    {
        // what the earlier design did: list the entries, keep the "not a link" flag, then act by path. The sandbox reproduces the attack,
        // so the tests above are not passing for want of a working swap.
        using var sb = new Sandbox();
        var victim = sb.Dir("victim"); sb.File(@"victim\precious.txt");
        var sub = sb.Dir("t", "a", "sub");
        var listed = Directory.EnumerateDirectories(Path.Combine(sb.Root, "t", "a")).Select(d => (d, link: File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint))).ToList();
        Assert.False(listed.Single().link);
        SwapForJunction(sb, sub, victim);                                                    // after the check, before the delete
        foreach (var f in Directory.EnumerateFiles(listed.Single().d)) File.Delete(f);       // by path: goes through the junction
        Assert.False(File.Exists(Path.Combine(victim, "precious.txt")));
    }

    [Fact]
    public void TheDeleterUsesItAndSurvivesTheSameRace()
    {
        using var sb = new Sandbox();
        var victim = sb.Dir("victim"); sb.File(@"victim\precious.txt");
        sb.Dir("t", "a", "sub"); sb.File(@"t\a\f.txt");
        bool swapped = false;
        var d = new Deleter(new PathGuard(Array.Empty<string>()), false)
        {
            LogActions = false,
            AfterEnumerate = (dir, names) =>
            {
                if (dir.EndsWith(@"\a", StringComparison.OrdinalIgnoreCase) && !swapped) { swapped = true; SwapForJunction(sb, Path.Combine(sb.Root, "t", "a", "sub"), victim); }
            }
        };
        var res = d.Run(new[] { new CleanItem { Path = Path.Combine(sb.Root, "t"), IsDirectory = true, Display = "t" } }, false, null, CancellationToken.None);
        Assert.True(res.Single().Success, res.Single().Message);
        Assert.True(swapped);
        Assert.True(File.Exists(Path.Combine(victim, "precious.txt")));
    }
}

// ---- 2. who deletes what in the elevated instance ----------------------------------------------------------------------------

public class ElevatedSplitTests
{
    sealed class RecordingRunner : IUserDeleteRunner
    {
        public readonly List<string> Sent = new();
        public IReadOnlyList<string>? Protected;
        public List<DeleteResult> Run(IReadOnlyList<CleanItem> items, bool forceRecycle, IReadOnlyList<string> protectedFolders,
            IProgress<(int done, int total, string path)>? progress, CancellationToken ct)
        {
            Protected = protectedFolders;
            Sent.AddRange(items.Select(i => i.Path));
            return items.Select(i => new DeleteResult(i.Path, i.Mode, i.Bytes, true, "deleted by the fake helper")).ToList();
        }
    }

    static CleanItem Item(string path) => new() { Path = path, IsDirectory = true, Display = path, Bytes = 10 };

    [Fact]
    public void TheElevatedSideNeverDeletesInAUserWritableFolderItHandsItOver()
    {
        using var sb = new Sandbox();
        var mine = sb.File(@"user-cache\x.bin"); var dir = Path.Combine(sb.Root, "user-cache");
        var runner = new RecordingRunner();
        var d = new Deleter(new PathGuard(new[] { @"D:\Keep" }), false, runner) { LogActions = false };
        var res = d.Run(new[] { Item(dir) }, false, null, CancellationToken.None);
        Assert.Equal(new[] { dir }, runner.Sent);                       // handed to the helper
        Assert.True(File.Exists(mine));                                 // and not deleted by the elevated side
        Assert.True(res.Single().Success);
        Assert.Contains(@"D:\Keep", runner.Protected!);                 // the user's protected folders travel with the job
    }

    [Fact]
    public void RefusedItemsAreNotHandedOverAtAll()
    {
        using var sb = new Sandbox();
        sb.File(@"keep\a.txt");
        var runner = new RecordingRunner();
        var d = new Deleter(new PathGuard(new[] { Path.Combine(sb.Root, "keep") }), false, runner) { LogActions = false };
        var res = d.Run(new[] { Item(Path.Combine(sb.Root, "keep")) }, false, null, CancellationToken.None);
        Assert.Empty(runner.Sent);
        Assert.False(res.Single().Success);
        Assert.Contains("refused", res.Single().Message);
    }

    [Fact]
    public void ItemsBelowTheAdministratorRootsStayWithTheElevatedSide()
    {
        var root = AdminRoots.All()[0];                                 // <Windows>\Temp
        var gone = Path.Combine(root, "quicksweep-test-does-not-exist-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.True(AdminRoots.IsAdminClass(gone));
        var runner = new RecordingRunner();
        var d = new Deleter(new PathGuard(Array.Empty<string>()), false, runner) { LogActions = false };
        var res = d.Run(new[] { Item(gone) }, false, null, CancellationToken.None);
        Assert.Empty(runner.Sent);
        Assert.True(res.Single().Success);                              // "already gone": handled in the elevated process itself
    }

    [Theory]
    [InlineData(@"C:\Windows\Temp", false)]                             // the root itself is never an item
    [InlineData(@"C:\Windows\Temp\x", true)]
    [InlineData(@"C:\Windows\TempX\x", false)]
    [InlineData(@"C:\Windows\System32\x", false)]
    [InlineData(@"C:\Users\someone\AppData\Local\Temp\x", false)]       // user-writable: the helper's job
    [InlineData(@"C:\Windows\Temp\..\System32\x", true)]                // normalised first: this is C:\Windows\System32\x ... see below
    public void OnlyTheNamedSystemFoldersCountAsAdministratorLevel(string path, bool expected)
    {
        if (path.Contains(".."))
        {
            // a path with '..' collapses to what it really names before it is classified
            Assert.False(AdminRoots.IsAdminClass(path));
            return;
        }
        string win = TrustedPaths.Windows;
        path = path.Replace(@"C:\Windows", win);
        Assert.Equal(expected, AdminRoots.IsAdminClass(path));
    }

    [Fact]
    public void EveryAdministratorRootIsOneTheGuardAllowsToBeCleanedBelow()
    {
        var guard = new PathGuard(Array.Empty<string>());
        foreach (var root in AdminRoots.All())
            Assert.True(guard.Check(Path.Combine(root, "child")).Allowed, root);
    }
}

// ---- the helper protocol ----------------------------------------------------------------------------------------------------------

public class HelperProtocolTests
{
    /// <summary>Runs DeleteHelper.Run on a task instead of a process: the same job and result files, no process boundary.</summary>
    sealed class InProcessLauncher : IUserProcessLauncher
    {
        sealed class Fake : IUserProcess
        {
            readonly Task<int> _t;
            public Fake(Task<int> t) { _t = t; }
            public bool WaitForExit(int ms) => _t.Wait(ms);
            public int ExitCode => _t.Result;
            public void Kill() { }
            public void Dispose() { }
        }
        public Func<string, string, int>? Behaviour;
        public IUserProcess? Start(string exe, string arguments, out string why)
        {
            why = "";
            var m = Regex.Match(arguments, "--delete-job=\"([^\"]+)\" --job-out=\"([^\"]+)\"");
            Assert.True(m.Success, arguments);
            string job = m.Groups[1].Value, outDir = m.Groups[2].Value;
            return new Fake(Task.Run(() => Behaviour != null ? Behaviour(job, outDir) : DeleteHelper.Run(job, outDir)));
        }
    }

    sealed class NoLauncher : IUserProcessLauncher
    {
        public IUserProcess? Start(string exe, string arguments, out string why) { why = "no desktop shell is running"; return null; }
    }

    static CleanItem Item(string path) => new() { Path = path, IsDirectory = true, Display = path, Bytes = 5, Mode = DeleteMode.Permanent };

    [Fact]
    public void TheHelperDeletesWhatIsAllowedRefusesWhatIsProtectedAndCleansUp()
    {
        using var sb = new Sandbox();
        sb.File(@"del\a\x.bin"); sb.File(@"keep\y.bin"); var gone = Path.Combine(sb.Root, "already-gone");
        var jobs = sb.Dir("jobs"); var outDir = Path.Combine(sb.Root, "out");
        var runner = new HelperRunner(new InProcessLauncher(), jobs, outDir, exe: "unused.exe");
        var items = new[] { Item(Path.Combine(sb.Root, "del")), Item(Path.Combine(sb.Root, "keep")), Item(gone) };
        var res = runner.Run(items, false, new[] { Path.Combine(sb.Root, "keep") }, null, CancellationToken.None);
        Assert.True(res[0].Success, res[0].Message);
        Assert.False(Directory.Exists(Path.Combine(sb.Root, "del")));
        Assert.False(res[1].Success); Assert.Contains("refused", res[1].Message);
        Assert.True(File.Exists(Path.Combine(sb.Root, "keep", "y.bin")));
        Assert.True(res[2].Success);
        Assert.Equal(items.Select(i => i.Path), res.Select(r => r.Path));               // the elevated side reports its own paths
        Assert.Empty(Directory.GetFiles(jobs));                                          // job, progress and result files are removed
        Assert.Empty(Directory.GetFiles(outDir));
    }

    [Fact]
    public void WhenTheHelperCannotBeStartedNothingIsDeleted()
    {
        using var sb = new Sandbox();
        sb.File(@"del\a.bin");
        var runner = new HelperRunner(new NoLauncher(), sb.Dir("jobs"), Path.Combine(sb.Root, "out"), exe: "unused.exe");
        var d = new Deleter(new PathGuard(Array.Empty<string>()), false, runner) { LogActions = false };
        var res = d.Run(new[] { Item(Path.Combine(sb.Root, "del")) }, false, null, CancellationToken.None);
        Assert.False(res.Single().Success);
        Assert.Contains("could not be started", res.Single().Message);
        Assert.True(File.Exists(Path.Combine(sb.Root, "del", "a.bin")));                // and the elevated side did not fall back to deleting itself
    }

    [Fact]
    public void AHelperThatStopsEarlyLeavesTheRestMarkedAsNotDone()
    {
        using var sb = new Sandbox();
        sb.File(@"a\x"); sb.File(@"b\x");
        var launcher = new InProcessLauncher { Behaviour = (job, outDir) => 7 };       // dies without writing a result
        var runner = new HelperRunner(launcher, sb.Dir("jobs"), Path.Combine(sb.Root, "out"), exe: "unused.exe");
        var res = runner.Run(new[] { Item(Path.Combine(sb.Root, "a")), Item(Path.Combine(sb.Root, "b")) }, false, Array.Empty<string>(), null, CancellationToken.None);
        Assert.All(res, r => Assert.False(r.Success));
        Assert.Contains("stopped", res[0].Message);
    }

    [Fact]
    public void ForgedResultTextIsCleanedAndOnlyTheOwnPathsAreReported()
    {
        using var sb = new Sandbox();
        sb.File(@"a\x");
        var launcher = new InProcessLauncher
        {
            Behaviour = (job, outDir) =>
            {
                var id = Path.GetFileName(job).Split('.')[0];
                Directory.CreateDirectory(outDir);
                File.WriteAllText(Path.Combine(outDir, id + ".result.json"),
                    System.Text.Json.JsonSerializer.Serialize(new[] { new DeleteResult(@"C:\Windows\evil", DeleteMode.Permanent, -5, true, "ok\r\nFAKE LOG LINE\u0007" + new string('x', 5000)) }));
                return 0;
            }
        };
        var runner = new HelperRunner(launcher, sb.Dir("jobs"), Path.Combine(sb.Root, "out"), exe: "unused.exe");
        var res = runner.Run(new[] { Item(Path.Combine(sb.Root, "a")) }, false, Array.Empty<string>(), null, CancellationToken.None).Single();
        Assert.Equal(Path.Combine(sb.Root, "a"), res.Path);
        Assert.DoesNotContain('\n', res.Message); Assert.DoesNotContain('\r', res.Message);
        Assert.True(res.Message.Length <= 300);
        Assert.Equal(0, res.Bytes);
        Assert.True(File.Exists(Path.Combine(sb.Root, "a", "x")));                       // a forged report deleted nothing
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("job.json", null)]
    public void TheHelperNeedsBothArguments(string? job, string? outDir) => Assert.Equal(DeleteHelper.BadArguments, DeleteHelper.Run(job, outDir));

    [Fact]
    public void TheHelperRefusesJobsThatAreMissingMalformedOrCarryAnOddId()
    {
        using var sb = new Sandbox(); var outDir = Path.Combine(sb.Root, "out");
        Assert.Equal(DeleteHelper.BadJob, DeleteHelper.Run(Path.Combine(sb.Root, "nope.json"), outDir));
        Assert.Equal(DeleteHelper.BadJob, DeleteHelper.Run(sb.File("bad.json", "not json"), outDir));
        Assert.Equal(DeleteHelper.BadJob, DeleteHelper.Run(sb.File("old.json", """{ "Version": 2, "Id": "abcdefgh12345678", "Items": [] }"""), outDir));
        Assert.Equal(DeleteHelper.BadJob, DeleteHelper.Run(sb.File("id.json", """{ "Version": 1, "Id": "..\\..\\evil", "Items": [] }"""), outDir));
        Assert.False(Directory.Exists(outDir));
    }

    [Fact]
    public void TheRealExecutableRunsTheSameJobWithItsOwnToken()
    {
        if (Elevation.IsTokenElevated) return;      // an elevated test run starts an elevated child, which refuses (that is the point of the check)
        var exe = Path.Combine(AppContext.BaseDirectory, typeof(AppSettings).Assembly.GetName().Name + ".exe");
        if (!File.Exists(exe)) return;
        using var sb = new Sandbox();
        sb.File(@"del\a.bin"); sb.File(@"keep\b.bin");
        var runner = new HelperRunner(new DirectLauncher(), sb.Dir("jobs"), Path.Combine(sb.Root, "out"), exe);
        var res = runner.Run(new[] { Item(Path.Combine(sb.Root, "del")), Item(Path.Combine(sb.Root, "keep")) }, false, new[] { Path.Combine(sb.Root, "keep") }, null, CancellationToken.None);
        Assert.True(res[0].Success, res[0].Message);
        Assert.False(Directory.Exists(Path.Combine(sb.Root, "del")));
        Assert.False(res[1].Success);
        Assert.True(File.Exists(Path.Combine(sb.Root, "keep", "b.bin")));
    }
}

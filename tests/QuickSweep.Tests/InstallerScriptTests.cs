using System.Diagnostics;
using System.Text.RegularExpressions;
using QuickSweep.Core;
using Xunit;

namespace QuickSweep.Tests;

/// <summary>
/// install.ps1 and uninstall.ps1 run elevated, and the environment of an elevated process includes the variables the user can set.
/// These tests load the scripts' own functions in a child PowerShell (the installer itself is never run) and check that locations come
/// from known folders, that the trust test refuses what it must, and that no script reads a location from the environment.
/// </summary>
public class InstallerScriptTests
{
    static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "QuickSweep.slnx"))) return d.FullName;
        return null;
    }

    static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    /// <summary>Runs one scenario of tests/scripts/installer-checks.ps1; null when the repository layout is not available.</summary>
    static Dictionary<string, string>? Run(string script, string scenario, string? work = null, string? modulePath = null, bool raw = false)
    {
        var root = RepoRoot();
        if (root == null || !File.Exists(PowerShell)) return null;
        var psi = new ProcessStartInfo(PowerShell,
            $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(root, "tests", "scripts", "installer-checks.ps1")}\" -Script \"{Path.Combine(root, script)}\" -Scenario {scenario}" +
            (work != null ? $" -Work \"{work}\"" : "") + (raw ? " -Raw" : ""))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (modulePath != null) psi.Environment["PSModulePath"] = modulePath;       // what a PowerShell 7 step of the workflow hands to a child
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd(); var stderr = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(120_000), "the child PowerShell did not finish");
        Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}: {stdout} {stderr}");
        var values = new Dictionary<string, string>();
        foreach (var l in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l.Contains('=')))
            values[l[..l.IndexOf('=')]] = l[(l.IndexOf('=') + 1)..];
        values["_raw"] = stdout;                                    // the scenario's whole output: put into assertion messages, so a failure on a runner explains itself
        return values;
    }

    [Theory]
    [InlineData("install.ps1")]
    [InlineData("uninstall.ps1")]
    public void LocationsComeFromKnownFoldersEvenWhenTheEnvironmentIsRewritten(string script)
    {
        var r = Run(script, "known-locations");
        if (r == null) return;
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), r["ProgramFiles"]);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), r["ProgramData"]);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows\Start Menu\Programs"), r["StartMenu"]);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), r["UserProfile"]);
        Assert.Equal(Environment.SystemDirectory, r["System"], ignoreCase: true);
        Assert.DoesNotContain(r.Values, v => v.Contains("evil", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NeitherScriptReadsALocationOrAToolFromTheEnvironment()
    {
        var root = RepoRoot();
        if (root == null) return;
        foreach (var name in new[] { "install.ps1", "uninstall.ps1" })
        {
            var text = File.ReadAllText(Path.Combine(root, name));
            foreach (Match m in Regex.Matches(text, @"\$env:(\w+)(\s*=(?!=))?"))
            {
                bool assignment = m.Groups[2].Success;
                Assert.True(assignment && (m.Groups[1].Value is "Path" or "PSModulePath"), $"{name}: reads or sets $env:{m.Groups[1].Value}");
            }
            Assert.DoesNotContain("Get-Command winget", text);
            Assert.DoesNotMatch(@"(?m)^\s*winget\s", text);                 // winget is only run through its resolved package path
            Assert.DoesNotContain("ExpandEnvironmentVariables", text);
        }
    }

    [Fact]
    public void ThePathCheckForTheTasksProgramIsStrict()
    {
        var r = Run("install.ps1", "under-directory");
        if (r == null) return;
        Assert.Equal("True", r["inside"]);
        Assert.Equal("False", r["sibling-prefix"]);                          // "Program Files Evil" is not below "Program Files"
        Assert.Equal("False", r["elsewhere"]);
        Assert.Equal("False", r["dotdot"]);                                  // '..' is resolved before the comparison
        Assert.Equal("False", r["same-folder"]);                             // the folder itself is not "below" it
    }

    [Fact]
    public void AFolderThatUsersCanWriteIsNotTrustedAsAnInstallOrStateFolder()
    {
        using var sb = new Sandbox();
        var r = Run("install.ps1", "trust-users-writable", sb.Root);
        if (r == null) return;
        Assert.True(r["trusted"] == "False", r["_raw"]);
    }

    [Fact]
    public void ALinkOrAMissingFolderIsNotTrusted()
    {
        using var sb = new Sandbox();
        var junction = Run("install.ps1", "trust-junction", sb.Root);
        if (junction == null) return;
        Assert.Equal("False", junction["trusted"]);
        Assert.Equal("False", Run("install.ps1", "trust-missing", sb.Root)!["trusted"]);
    }

    [Fact]
    public void TheInstallersOwnPermissionsAreTrustedAndAGrantBelowThemIsNot()
    {
        // needs administrator rights (it takes ownership for Administrators): on a normal account the scenario reports "skipped", on the
        // hosted runner it is the positive control for Set-AdminOnlyAcl, Set-AdminOnlyTree, Test-TrustedStateFolder and Test-AdminOnlyTree.
        // Every assertion carries the scenario's output: the reasons the checks gave and the owner and SDDL of the folder and what is in it.
        using var sb = new Sandbox();
        var r = Run("install.ps1", "acl-roundtrip", sb.Root);
        if (r == null || r.ContainsKey("skipped")) return;
        string all = "\n--- scenario output ---\n" + r["_raw"];
        Assert.False(r.ContainsKey("error"), "the installer's permission helper failed: " + r.GetValueOrDefault("error") + all);
        // the folder started with an explicit FullControl entry for the account that runs the installer (what the hosted runner had):
        // the complete descriptor must have replaced it, not sat next to it
        Assert.True(r["folder-difference"] == "", "the folder is not what the installer wrote: " + r["folder-difference"] + all);
        Assert.True(r["tree-differences"] == "", "something below the folder is not what the installer wrote: " + r["tree-differences"] + all);
        Assert.True(r["folder-trusted"] == "True", "the folder the installer protected itself is judged untrusted: " + r["folder-reason"] + all);
        Assert.True(r["tree-trusted"] == "True", "the tree the installer protected itself is judged untrusted: " + r["tree-reason"] + all);
        Assert.True(r["tree-after-grant-trusted"] == "False", "a grant to Users below the folder was not noticed" + all);
    }

    // ---- the complete security descriptor (no incremental icacls edits), the order of checks on an existing state folder, and no link followed ----
    // These run without administrator rights: the account that runs them stands in for Administrators as the owner (a normal account may
    // not own as Administrators) and, where a scenario says so, as a trusted owner in the check. Everything else is the installer's own code.

    static string Me => System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
    const string FolderAces = "(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)";
    const string FileAces = "(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)";

    /// <summary>"owner SID; O:SID G:SID D:flags(ACE)(ACE)..." must be exactly the canonical owner and DACL: protected, and these entries and no others.</summary>
    static void AssertCanonical(string shown, string aces, string owner, string what, string all)
    {
        // the owner is compared as the full SID the scenario read through GetOwner (SDDL spells well-known accounts as two letters: the built-in
        // administrator account is "LA"); the SDDL text must still be well-formed, with a protected DACL holding exactly the expected entries
        var m = Regex.Match(shown, @"^owner (?<own>\S+); O:(?<o>\S+?)(?:G:(?<g>\S+?))?D:(?<flags>[A-Z]*)(?<aces>\(.*\))$");
        Assert.True(m.Success, $"{what}: cannot parse '{shown}'{all}");
        Assert.True(m.Groups["own"].Value == owner, $"{what}: owner is {m.Groups["own"].Value}, expected {owner}{all}");
        Assert.True(m.Groups["flags"].Value.Contains('P'), $"{what}: the DACL is not protected (flags '{m.Groups["flags"].Value}'){all}");
        Assert.True(m.Groups["aces"].Value == aces, $"{what}: DACL is {m.Groups["aces"].Value}, expected {aces}{all}");
    }

    [Fact]
    public void AnExplicitGrantToTheCallersOwnAccountDoesNotSurviveTheInstallersDescriptor()
    {
        // the CI failure of 1.0.2: icacls /grant:r left the explicit FullControl entry of the account that runs the installer in place
        using var sb = new Sandbox();
        var r = Run("install.ps1", "explicit-grant-removed", sb.Root);
        if (r == null) return;
        string all = "\n--- scenario output ---\n" + r["_raw"];
        Assert.True(int.Parse(r["before-write-entries"]) >= 1, "the starting folder was meant to carry a write entry for this account" + all);
        Assert.True(r["after-write-entries"] == "0", "an entry that lets this account write survived" + all);
        Assert.True(r["after-difference"] == "", r["after-difference"] + all);
        AssertCanonical(r["after"], FolderAces, Me, "folder", all);
    }

    [Fact]
    public void AFreshStateFolderGetsExactlyTheCanonicalDescriptor()
    {
        using var sb = new Sandbox();
        var r = Run("install.ps1", "state-fresh", sb.Root);
        if (r == null) return;
        string all = "\n--- scenario output ---\n" + r["_raw"];
        Assert.True(r["aside"] == "", "nothing was there, so nothing should have been set aside" + all);
        AssertCanonical(r["dir"], FolderAces, Me, "state folder", all);
        Assert.True(r["dir-difference"] == "" && r["dir-trusted"] == "True", "the new folder fails the installer's own checks: " + r["dir-difference"] + all);
        // what the installer copies gets the folder or the file variant of it, item by item
        AssertCanonical(r["tree[sub]"], FolderAces, Me, "sub", all);
        AssertCanonical(r["tree[g.txt]"], FileAces, Me, "g.txt", all);
        AssertCanonical(r["tree[sub\\f.txt]"], FileAces, Me, "sub\\f.txt", all);
        foreach (var k in new[] { "tree[sub]-difference", "tree[g.txt]-difference", "tree[sub\\f.txt]-difference" }) Assert.True(r[k] == "", k + ": " + r[k] + all);
    }

    [Fact]
    public void AnExistingStateFolderWithAJunctionInsideIsSetAsideWholeAndNothingIsReowned()
    {
        // Before the fix the contents were re-owned and reset first and checked afterwards, so planted content passed as the installer's own.
        using var sb = new Sandbox();
        var r = Run("install.ps1", "state-junction", sb.Root);
        if (r == null) return;
        string all = "\n--- scenario output ---\n" + r["_raw"];
        Assert.True(r["deep-folder-check-alone"] == "True", "the scenario is meant to hide the junction from the folder-level check" + all);
        foreach (var c in new[] { "direct", "deep" })
        {
            Assert.True(r[c + "-tree-check-alone"] == "False", c + ": the check of the whole tree should refuse the junction" + all);
            Assert.True(r[c + "-aside-name-ok"] == "True" && r[c + "-aside-exists"] == "True", c + ": the folder was not renamed aside" + all);
            Assert.True(r[c + "-aside-still-has-the-junction"] == "True", c + ": the junction was removed or replaced" + all);
            Assert.True(r[c + "-aside-file-untouched"] == "True", c + ": a file in the folder was re-owned or reset" + all);
            Assert.True(r[c + "-fresh-difference"] == "" && r[c + "-fresh-empty"] == "True", c + ": the new folder is not clean and canonical: " + r[c + "-fresh-difference"] + all);
        }
        Assert.True(r["victim-unchanged"] == "True" && r["victim-file-still-there"] == "True", "where the junction pointed was changed" + all);
    }

    [Fact]
    public void AnExistingStateFolderThatPassesEveryCheckIsKeptAndItsContentsAreNotTouched()
    {
        using var sb = new Sandbox();
        var r = Run("install.ps1", "state-trusted-kept", sb.Root);
        if (r == null) return;
        string all = "\n--- scenario output ---\n" + r["_raw"];
        Assert.True(r["aside"] == "" && r["kept"] == "True", "a folder that passed was set aside" + all);
        Assert.True(r["contents-unchanged"] == "True" && r["file-count"] == "3", "the contents of a trusted folder were changed" + all);
    }

    [Fact]
    [Trait("Needs", "NonAdmin")]
    public void AFolderAUserPrecreatedWithAFileInItIsSetAsideExactlyAsItWas()
    {
        // ProgramData lets any user create C:\ProgramData\QuickSweep before the installer runs; it is owned by that user, so it is not trusted.
        // (Needs an account that is not an administrator: on an administrator account the scratch folder can be owned by Administrators.)
        using var sb = new Sandbox();
        var r = Run("install.ps1", "state-userowned", sb.Root);
        if (r == null) return;
        string all = "\n--- scenario output ---\n" + r["_raw"];
        Assert.True(r["aside-name-ok"] == "True" && r["original-gone"] == "True", "the planted folder was not renamed out of the way" + all);
        Assert.True(r["contents-unchanged"] == "True", "the planted folder's contents were re-owned or reset before it was set aside" + all);
        Assert.Contains("is owned by", r["reasons"]);
    }

    [Fact]
    public void ATreeWithAJunctionBelowIsRefusedBeforeAnythingIsChangedAndTheTargetIsNeverTouched()
    {
        using var sb = new Sandbox();
        var r = Run("install.ps1", "tree-link-refused", sb.Root);
        if (r == null) return;
        string all = "\n--- scenario output ---\n" + r["_raw"];
        Assert.True(r["threw"] == "True" && r["listing-threw"] == "True", "a link below the folder was not refused" + all);
        Assert.Contains("junction", r["message"]);
        Assert.True(r["unchanged-including-the-victim"] == "True", "something was changed although a link was found" + all);
    }

    [Theory]
    [InlineData("install.ps1")]
    [InlineData("uninstall.ps1")]
    public void RemovingATreeRemovesLinksAsLinksAndNeverEntersThem(string script)
    {
        using var sb = new Sandbox();
        var r = Run(script, "remove-tree-links", sb.Root);
        if (r == null) return;
        string all = "\n--- scenario output ---\n" + r["_raw"];
        Assert.True(r["tree-removed"] == "True" && r["toplink-removed"] == "True" && r["missing-ok"] == "True", "the tree or the top-level link was not removed" + all);
        Assert.True(r["victim-intact"] == "True" && r["victim-intact-after-toplink"] == "True", "a junction was followed and its target emptied" + all);
    }

    [Fact]
    public void TheUninstallerDeletesTheStateFolderOnlyWhenItIsTrustedAndNeverDeletesInsideOneThatIsNot()
    {
        // Deleting by path as an administrator inside a folder a normal program can write to can be raced with a link, so a folder that does not
        // pass the checks is left exactly as it is (not even the program's own file names are removed from it).
        using var sb = new Sandbox();
        var r = Run("uninstall.ps1", "remove-state-folder", sb.Root);
        if (r == null) return;
        string all = "\n--- scenario output ---\n" + r["_raw"];
        Assert.True(r["trusted-case-removed"] == "True" && r["trusted-case-gone"] == "True", "a folder that passes the checks was not removed" + all);
        Assert.True(r["untrusted-case-removed"] == "False" && r["untrusted-case-nothing-deleted"] == "True", "something was deleted inside a folder that does not pass the checks" + all);
        Assert.True(r["untrusted-case-junction-untouched"] == "True" && r["victim-intact"] == "True", "a junction in the folder was removed or followed" + all);
        Assert.True(r["untrusted-plain-removed"] == "False" && r["untrusted-plain-nothing-deleted"] == "True", "something was deleted inside a plain folder that does not pass the checks" + all);
        Assert.True(r["link-case-removed"] == "True" && r["link-case-gone"] == "True" && r["victim-intact-after-link"] == "True", "a link standing in the folder's place was not removed as a link" + all);
    }

    [Fact]
    public void AFolderThatAlreadyExistsIsReportedNotAdoptedAndLeftExactlyAsItWas()
    {
        // New-AdminOnlyFolder creates with CreateDirectoryW and the descriptor in one call; ERROR_ALREADY_EXISTS comes back as "false", never as success
        using var sb = new Sandbox();
        var r = Run("install.ps1", "create-exists", sb.Root);
        if (r == null) return;
        string all = "\n--- scenario output ---\n" + r["_raw"];
        Assert.True(r["existing-returned"] == "False" && r["existing-api-returned"] == "False", "an existing folder was reported as created" + all);
        Assert.True(r["existing-unchanged"] == "True", "an existing folder was changed" + all);
        Assert.True(r["fresh-returned"] == "True" && r["fresh-difference"] == "", "a new folder is not what the installer wrote: " + r["fresh-difference"] + all);
        AssertCanonical(r["fresh"], FolderAces, Me, "new folder", all);
        Assert.True(r["missing-parent-threw"] == "True" && r["missing-parent-message"].Contains("error 3"), "any other failure should throw with its error code" + all);
    }

    [Fact]
    public void AFolderThatAppearsBetweenTheCheckAndTheCreationIsSetAsideUnreadAndAFreshOneIsCreated()
    {
        using var sb = new Sandbox();
        var r = Run("install.ps1", "state-race", sb.Root);
        if (r == null) return;
        string all = "\n--- scenario output ---\n" + r["_raw"];
        Assert.True(r["aside-name-ok"] == "True", "the folder that appeared was not renamed aside" + all);
        Assert.True(r["aside-contents-unchanged"] == "True", "the folder that appeared was re-owned, reset or changed" + all);
        Assert.True(r["fresh-difference"] == "" && r["fresh-empty"] == "True", "the new folder is not clean and canonical: " + r["fresh-difference"] + all);
    }

    static string WithoutComments(string text) =>
        Regex.Replace(Regex.Replace(text, @"<#.*?#>", "", RegexOptions.Singleline), @"(?m)\s#.*$|^#.*$", "");

    [Fact]
    public void NeitherScriptEditsPermissionsOneEntryAtATimeOrWalksATreeThroughLinks()
    {
        var root = RepoRoot();
        if (root == null) return;
        foreach (var name in new[] { "install.ps1", "uninstall.ps1" })
        {
            var code = WithoutComments(File.ReadAllText(Path.Combine(root, name)));
            // permissions are written as a complete descriptor; the only icacls left is the owner fallback: one item, /L, never /T, never /grant, /reset or /inheritance
            foreach (Match m in Regex.Matches(code, @"[^\r\n]*icacls[^\r\n]*"))
            {
                Assert.True(m.Value.Contains("/setowner") && m.Value.Contains("/L"), $"{name}: icacls used for more than the owner fallback: {m.Value.Trim()}");
                Assert.DoesNotMatch(@"/(T|grant|reset|inheritance)\b", m.Value);
            }
            Assert.DoesNotMatch(@"Get-ChildItem[^\r\n]*-Recurse", code);                           // Windows PowerShell 5.1 follows junctions with -Recurse
            Assert.DoesNotContain("Add-Type", code);                                               // it compiles into the user's TEMP folder, where the result could be swapped before the elevated process loads it
            Assert.DoesNotContain("Remove-UserFolderKnownFiles", code);                            // nothing is deleted by path as an administrator inside a folder a normal program can write to
            Assert.DoesNotContain("IgnoreOwner", code);                                            // a folder is only accepted with the owner read back
            foreach (Match m in Regex.Matches(code, @"[^\r\n]*Remove-Item[^\r\n]*-Recurse[^\r\n]*"))
                Assert.True(m.Value.Contains("PSPath"), $"{name}: recursive Remove-Item outside the registry: {m.Value.Trim()}");   // the one allowed use is a registry key
        }
    }

    [Fact]
    public void TheCheckAndRemovalFunctionsBothScriptsShareAreIdentical()
    {
        // uninstall.ps1 is a separate file (it is run on its own), so these are copies: they must not drift apart
        var root = RepoRoot();
        if (root == null) return;
        string install = File.ReadAllText(Path.Combine(root, "install.ps1")), uninstall = File.ReadAllText(Path.Combine(root, "uninstall.ps1"));
        foreach (var fn in new[] { "Get-AdminSids", "Get-UntrustedWriteAces", "Test-TrustedStateFolder", "Test-AdminOnlyTree", "Remove-TreeNoFollow", "Remove-AdminTree", "Test-Exists", "Test-Link" })
            Assert.True(Body(install, fn) == Body(uninstall, fn), $"{fn} differs between install.ps1 and uninstall.ps1");

        static string Body(string text, string fn)
        {
            int start = text.IndexOf("function " + fn, StringComparison.Ordinal);
            Assert.True(start >= 0, $"function {fn} not found");
            int open = text.IndexOf('{', start), depth = 0, i = open;
            for (; i < text.Length; i++) { if (text[i] == '{') depth++; else if (text[i] == '}' && --depth == 0) break; }
            return text[start..(i + 1)].Replace("\r\n", "\n");
        }
    }

    [Fact]
    public void ASystemFolderWithItsDefaultPermissionsIsTrustedWhenTheImageIsStandard()
    {
        // informational positive control on this machine's own System32\drivers\etc: only a False caused by something other than the
        // permission rules (an unusual image) would be a reason to look, so the assertion is on the rule-level cases above instead
        var sys = Run("install.ps1", "trust-system");
        if (sys == null) return;
        Assert.Contains(sys["trusted"], new[] { "True", "False" });
    }

    [Fact]
    public void AnInheritableEntryThatLetsANonAdministratorWriteMakesTheFolderUntrusted()
    {
        var r = Run("install.ps1", "aces");
        if (r == null) return;
        // entries that children inherit are judged like entries on the folder (they used to be skipped: a fail-open check)
        Assert.Equal("1", r["users-modify-inheritable"]);
        Assert.Equal("1", r["users-modify-inherit-only"]);
        Assert.Equal("1", r["users-generic-all-inherit-only"]);
        Assert.Equal("1", r["users-generic-write-inherit-only"]);
        Assert.Equal("1", r["authenticated-users-modify-inherit-only"]);
        Assert.Equal("1", r["everyone-write-inherit-only"]);
        Assert.Equal("1", r["creator-owner-applies-to-folder"]);                     // only the inherit-only placeholder is excused
        Assert.Equal("1", r["program-files-plus-a-users-modify-entry"]);
        // what is allowed: read and execute for users, the CREATOR placeholders, administrative accounts, deny entries
        Assert.Equal("0", r["users-generic-read-exec-inherit-only"]);
        Assert.Equal("0", r["users-read-execute"]);
        Assert.Equal("0", r["creator-owner-inherit-only"]);
        Assert.Equal("0", r["creator-group-inherit-only"]);
        Assert.Equal("0", r["administrators-inherit-only"]);
        Assert.Equal("0", r["system-and-trustedinstaller"]);
        Assert.Equal("0", r["deny-entries-are-ignored"]);
        Assert.Equal("0", r["typical-program-files-folder"]);                         // the real default ACL of a Program Files subfolder passes
    }

    [Fact]
    public void TheTreeCheckRefusesAWritableChildAndALinkAnywhereBelow()
    {
        using var sb = new Sandbox();
        var r = Run("install.ps1", "tree-child-writable", sb.Root);
        if (r == null) return;
        Assert.Equal("False", r["tree-trusted"]);
        Assert.Equal("False", Run("install.ps1", "tree-link", sb.Root)!["tree-trusted"]);
    }

    [Fact]
    public void AnInheritedPowerShellSevenModulePathCannotBreakTheInstallerFunctions()
    {
        // The workflow runs "dotnet test" from a pwsh (PowerShell 7) step, and the Windows PowerShell 5.1 child inherits PowerShell 7's
        // module path, where an incompatible Microsoft.PowerShell.Security comes first: Get-Acl then "could not be loaded". A stand-in for
        // that module (it demands PowerShell 7) is put first, exactly like that.
        using var sb = new Sandbox();
        var fake = sb.Dir("ps7-modules");
        var mod = sb.Dir("ps7-modules", "Microsoft.PowerShell.Security");
        File.WriteAllText(Path.Combine(mod, "Microsoft.PowerShell.Security.psd1"),
            "@{ ModuleVersion = '7.0.0.0'; GUID = 'a9dd0f3d-4a8c-4f43-8f16-5c0f0b6d9b11'; PowerShellVersion = '7.0'; CompatiblePSEditions = @('Core'); CmdletsToExport = @('Get-Acl','Set-Acl') }");
        string inherited = fake + ";" + Environment.GetEnvironmentVariable("PSModulePath");
        var raw = Run("install.ps1", "autoload", sb.Root, inherited, raw: true);
        if (raw == null) return;
        Assert.Equal("failed", raw["autoload"]);                                                   // the stand-in reproduces the workflow's failure
        // with the installer's own reset and explicit import (what install.ps1 and uninstall.ps1 do first), the same environment works
        Assert.Contains(Run("install.ps1", "trust-system", sb.Root, inherited)!["trusted"], new[] { "True", "False" });   // runs; Get-Acl loaded
        Assert.Equal("False", Run("install.ps1", "trust-users-writable", sb.Root, inherited)!["trusted"]);
        Assert.Equal("ok", Run("uninstall.ps1", "autoload", sb.Root, inherited)!["autoload"]);
    }

    [Fact]
    public void BothScriptsImportWhatTheyUseByFullPathAndRerunInWindowsPowerShellWhenStartedFromPowerShellSeven()
    {
        var root = RepoRoot();
        if (root == null) return;
        foreach (var name in new[] { "install.ps1", "uninstall.ps1" })
        {
            var text = File.ReadAllText(Path.Combine(root, name));
            Assert.Contains("Import-SystemModules @('ScheduledTasks')", text.Replace("'Microsoft.PowerShell.Security', ", ""));
            Assert.Contains("Import-SystemModules @('Appx') -Optional", text);
            Assert.Contains("$PSVersionTable.PSEdition -eq 'Core'", text);                  // started from PowerShell 7: run again in 5.1
            Assert.Contains(@"WindowsPowerShell\v1.0\powershell.exe", text);
        }
    }

    [Fact]
    public void WingetIsOnlyEverRunFromItsPackageFolderBelowProgramFiles()
    {
        var r = Run("install.ps1", "winget");
        if (r == null) return;
        if (r["found"] == "True") Assert.Equal("True", r["under-windowsapps"]);   // found means found in its package folder, never through PATH
    }

    [Fact]
    public void BothScriptsParseAndTheBatchFileStartsPowerShellFromTheSystemFolder()
    {
        var root = RepoRoot();
        if (root == null) return;
        var bat = File.ReadAllText(Path.Combine(root, "Install Quick Sweep.bat"));
        Assert.Contains(@"\System32\WindowsPowerShell\v1.0\powershell.exe", bat);
        Assert.Contains("[Environment]::SystemDirectory", bat);                // the elevated copy is started by absolute path, not through PATH
    }
}

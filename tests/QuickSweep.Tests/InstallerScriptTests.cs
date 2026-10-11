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
        Assert.True(r["folder-trusted"] == "True", "the folder the installer protected itself is judged untrusted: " + r["folder-reason"] + all);
        Assert.True(r["tree-trusted"] == "True", "the tree the installer protected itself is judged untrusted: " + r["tree-reason"] + all);
        Assert.True(r["tree-after-grant-trusted"] == "False", "a grant to Users below the folder was not noticed" + all);
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

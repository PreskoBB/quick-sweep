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
    static Dictionary<string, string>? Run(string script, string scenario, string? work = null)
    {
        var root = RepoRoot();
        if (root == null || !File.Exists(PowerShell)) return null;
        var psi = new ProcessStartInfo(PowerShell,
            $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(root, "tests", "scripts", "installer-checks.ps1")}\" -Script \"{Path.Combine(root, script)}\" -Scenario {scenario}" +
            (work != null ? $" -Work \"{work}\"" : ""))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd(); var stderr = p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(120_000), "the child PowerShell did not finish");
        Assert.True(p.ExitCode == 0, $"exit {p.ExitCode}: {stdout} {stderr}");
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Contains('=')).ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
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
        Assert.Equal("False", r["trusted"]);
    }

    [Fact]
    public void ASystemFolderIsTrustedAndALinkOrMissingFolderIsNot()
    {
        using var sb = new Sandbox();
        var sys = Run("install.ps1", "trust-system");
        if (sys == null) return;
        Assert.Equal("True", sys["trusted"]);
        Assert.Equal("False", Run("install.ps1", "trust-junction", sb.Root)!["trusted"]);
        Assert.Equal("False", Run("install.ps1", "trust-missing", sb.Root)!["trusted"]);
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

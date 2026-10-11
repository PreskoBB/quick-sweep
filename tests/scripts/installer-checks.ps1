<#
 Test helper for tests/QuickSweep.Tests/InstallerScriptTests.cs. Loads ONLY the function definitions of install.ps1 or uninstall.ps1
 (nothing of the installer runs), then performs one scenario and prints "key=value" lines. Needs no elevation.
#>
param(
    [Parameter(Mandatory)][string]$Script,
    [Parameter(Mandatory)][string]$Scenario,
    [string]$Work
)
$ErrorActionPreference = 'Stop'
$tok = $null; $err = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$tok, [ref]$err)
if (@($err).Count -gt 0) { 'parse-error=1'; exit 3 }
foreach ($fn in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) { . ([ScriptBlock]::Create($fn.Extent.Text)) }

switch ($Scenario) {
    'known-locations' {
        # what a program running as the user can do to the environment of an elevated process started afterwards
        $env:ProgramFiles = 'X:\evil\pf'; ${env:ProgramFiles(x86)} = 'X:\evil\pf86'; $env:ProgramData = 'X:\evil\pd'; $env:ALLUSERSPROFILE = 'X:\evil\pd'
        $env:LOCALAPPDATA = 'X:\evil\local'; $env:USERPROFILE = 'X:\evil\user'; $env:SystemRoot = 'X:\evil\win'; $env:windir = 'X:\evil\win'
        $k = Get-KnownLocations
        foreach ($name in 'System', 'ProgramFiles', 'ProgramData', 'StartMenu', 'UserProfile') { "$name=$($k[$name])" }
    }
    'under-directory' {
        "inside=$(Test-UnderDirectory 'C:\Program Files\QuickSweep\QuickSweep.exe' 'C:\Program Files')"
        "sibling-prefix=$(Test-UnderDirectory 'C:\Program Files Evil\x.exe' 'C:\Program Files')"
        "elsewhere=$(Test-UnderDirectory 'C:\Users\someone\Program Files\q.exe' 'C:\Program Files')"
        "dotdot=$(Test-UnderDirectory 'C:\Program Files\..\Users\someone\q.exe' 'C:\Program Files')"
        "same-folder=$(Test-UnderDirectory 'C:\Program Files' 'C:\Program Files')"
    }
    'trust-users-writable' {
        $d = Join-Path $Work 'writable'; New-Item -ItemType Directory -Force $d | Out-Null
        icacls $d /grant '*S-1-5-32-545:(OI)(CI)M' | Out-Null          # Users may modify: the folder must not be trusted
        "trusted=$(Test-TrustedStateFolder $d 3>$null)"
    }
    'trust-system' {
        "trusted=$(Test-TrustedStateFolder (Join-Path ([Environment]::SystemDirectory) 'drivers\etc') 3>$null)"
    }
    'trust-junction' {
        $j = Join-Path $Work 'junction'
        New-Item -ItemType Junction -Path $j -Target (Join-Path ([Environment]::SystemDirectory) 'drivers\etc') | Out-Null
        "trusted=$(Test-TrustedStateFolder $j 3>$null)"
        [IO.Directory]::Delete($j)
    }
    'trust-missing' {
        "trusted=$(Test-TrustedStateFolder (Join-Path $Work 'does-not-exist') 3>$null)"
    }
    'winget' {
        $pf = [Environment]::GetFolderPath('ProgramFiles')
        $w = Get-WingetPath $pf
        "found=$([bool]$w)"
        if ($w) { "under-windowsapps=$(Test-UnderDirectory $w (Join-Path $pf 'WindowsApps'))" }
    }
    default { "unknown-scenario=$Scenario"; exit 2 }
}

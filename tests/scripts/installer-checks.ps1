<#
 Test helper for tests/QuickSweep.Tests/InstallerScriptTests.cs. Loads ONLY the function definitions of install.ps1 or uninstall.ps1
 (nothing of the installer runs), then performs one scenario and prints "key=value" lines. Needs no elevation.
#>
param(
    [Parameter(Mandatory)][string]$Script,
    [Parameter(Mandatory)][string]$Scenario,
    [string]$Work,
    [switch]$Raw          # skip the installer's own environment reset and module imports (shows what an inherited module path does without them)
)
$ErrorActionPreference = 'Stop'
$tok = $null; $err = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$tok, [ref]$err)
if (@($err).Count -gt 0) { 'parse-error=1'; exit 3 }
foreach ($fn in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) { . ([ScriptBlock]::Create($fn.Extent.Text)) }

# what the installer itself does first: its own environment reset and explicit module imports (not autoloading), so an inherited module path
# (for example PowerShell 7's, when this runs below a pwsh step of a workflow) cannot stop Get-Acl and friends from loading
if (-not $Raw) {
    Set-SafeEnvironment
    Import-SystemModules @('Microsoft.PowerShell.Security')
}

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
    'aces' {
        # access-control entries as SDDL (generic rights, inherit-only flags and all, the way inherited entries are really stored);
        # prints how many entries a non-administrator could use to write, delete or re-permission what is in the folder
        function Count($sddl) { $sec = New-Object Security.AccessControl.DirectorySecurity; $sec.SetSecurityDescriptorSddlForm($sddl); @(Get-UntrustedWriteAces $sec).Count }
        $cases = [ordered]@{
            'users-modify-inheritable'        = 'D:(A;OICI;0x1301bf;;;BU)'
            'users-modify-inherit-only'       = 'D:(A;OICIIO;0x1301bf;;;BU)'
            'users-generic-all-inherit-only'  = 'D:(A;OICIIO;GA;;;BU)'
            'users-generic-write-inherit-only'= 'D:(A;OICIIO;GW;;;BU)'
            'users-generic-read-exec-inherit-only' = 'D:(A;OICIIO;GRGX;;;BU)'
            'users-read-execute'              = 'D:(A;OICI;0x1200a9;;;BU)'
            'authenticated-users-modify-inherit-only' = 'D:(A;OICIIO;0x1301bf;;;AU)'
            'everyone-write-inherit-only'     = 'D:(A;OICIIO;GW;;;WD)'
            'creator-owner-inherit-only'      = 'D:(A;OICIIO;GA;;;CO)'
            'creator-group-inherit-only'      = 'D:(A;OICIIO;GA;;;CG)'
            'creator-owner-applies-to-folder' = 'D:(A;OICI;GA;;;CO)'
            'administrators-inherit-only'     = 'D:(A;OICIIO;GA;;;BA)'
            'system-and-trustedinstaller'     = 'D:(A;;FA;;;SY)(A;OICIIO;GA;;;SY)(A;;FA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)'
            'deny-entries-are-ignored'        = 'D:(D;OICI;GA;;;BU)'
            'typical-program-files-folder'    = 'D:PAI(A;;FA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)(A;CIIO;GA;;;S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464)(A;;FA;;;SY)(A;OICIIO;GA;;;SY)(A;;FA;;;BA)(A;OICIIO;GA;;;BA)(A;;0x1200a9;;;BU)(A;OICIIO;GXGR;;;BU)(A;;0x1200a9;;;AC)(A;OICIIO;GXGR;;;AC)(A;OICIIO;GA;;;CO)'
            'program-files-plus-a-users-modify-entry' = 'D:PAI(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)(A;OICIIO;GA;;;CO)(A;OICIIO;0x1301bf;;;BU)'
        }
        foreach ($k in $cases.Keys) { "$k=$(Count $cases[$k])" }
    }
    'tree-child-writable' {
        # a child that Users may modify, below an otherwise unremarkable folder: the tree check must refuse the whole tree
        $d = Join-Path $Work 'tree'; New-Item -ItemType Directory -Force (Join-Path $d 'sub') | Out-Null
        $f = Join-Path $d 'sub\file.txt'; 'x' | Set-Content $f
        icacls $f /grant '*S-1-5-32-545:(M)' | Out-Null
        "tree-trusted=$(Test-AdminOnlyTree $d 3>$null)"
    }
    'tree-link' {
        $d = Join-Path $Work 'treelink'; New-Item -ItemType Directory -Force $d | Out-Null
        New-Item -ItemType Junction -Path (Join-Path $d 'j') -Target (Join-Path ([Environment]::SystemDirectory) 'drivers\etc') | Out-Null
        "tree-trusted=$(Test-AdminOnlyTree $d 3>$null)"
        [IO.Directory]::Delete((Join-Path $d 'j'))
    }
    'autoload' {
        try { Get-Acl -LiteralPath ([Environment]::SystemDirectory) | Out-Null; 'autoload=ok' } catch { 'autoload=failed' }
    }
    'acl-roundtrip' {
        # Set-AdminOnlyAcl on a scratch folder, then the checks that follow it in the installer. Needs administrator rights (taking ownership);
        # without them it says "skipped". On the hosted runner, which is an administrator, this is the positive control.
        $d = Join-Path $Work 'canon'; New-Item -ItemType Directory -Force $d | Out-Null
        try { Set-AdminOnlyAcl $d } catch { 'skipped=1'; return }
        New-Item -ItemType Directory -Force (Join-Path $d 'sub') | Out-Null
        'x' | Set-Content (Join-Path $d 'sub\f.txt')
        icacls "$d\*" /reset /T /C | Out-Null
        "folder-trusted=$(Test-TrustedStateFolder $d 3>$null)"
        "tree-trusted=$(Test-AdminOnlyTree $d 3>$null)"
        icacls (Join-Path $d 'sub\f.txt') /grant '*S-1-5-32-545:(M)' | Out-Null
        "tree-after-grant=$(Test-AdminOnlyTree $d 3>$null)"
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

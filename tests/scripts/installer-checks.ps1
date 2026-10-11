<#
 Test helper for tests/QuickSweep.Tests/InstallerScriptTests.cs. Loads ONLY the function definitions of install.ps1 or uninstall.ps1
 (nothing of the installer runs), then performs one scenario and prints "key=value" lines. Needs no elevation.
#>
param(
    [Parameter(Mandatory)][string]$Script,
    [Parameter(Mandatory)][string]$Scenario,
    [string]$Work,
    [switch]$ForceAdminScenarios,   # run the administrator-only scenarios on a normal account too (they then report the error; for checking the script itself)
    [switch]$Raw          # skip the installer's own environment reset and module imports (shows what an inherited module path does without them)
)
$ErrorActionPreference = 'Stop'
$tok = $null; $err = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($Script, [ref]$tok, [ref]$err)
if (@($err).Count -gt 0) { 'parse-error=1'; exit 3 }
foreach ($fn in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) { . ([ScriptBlock]::Create($fn.Extent.Text)) }

# what the installer itself does first: its own environment reset and explicit module imports (not autoloading), so an inherited module path
# (for example PowerShell 7's, when this runs below a pwsh step of a workflow) cannot stop Get-Acl and friends from loading
# a check's answer together with the warnings it gave as its reasons (so a failure on a machine nobody can look at explains itself)
function Show-Trust($label, [scriptblock]$check) {
    $out = @(& $check 3>&1)
    $answer = $out | Where-Object { $_ -is [bool] } | Select-Object -Last 1
    $why = ($out | Where-Object { $_ -is [System.Management.Automation.WarningRecord] } | ForEach-Object { $_.Message.Trim() }) -join ' / '
    "$label-trusted=$answer"
    "$label-reason=$why"
}

function Describe-Acl($path, [switch]$Children) {
    $items = @(Get-Item -LiteralPath $path -Force)
    if ($Children) { $items += @(Get-ChildItem -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue) }
    foreach ($i in $items) {
        $sec = Get-Acl -LiteralPath $i.FullName
        $name = if ($i.FullName -eq $path) { '.' } else { $i.FullName.Substring($path.Length).TrimStart('\') }
        "acl[$name]=owner $($sec.GetOwner([Security.Principal.SecurityIdentifier]).Value); $($sec.Sddl)"
    }
}

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
        # Set-AdminOnlyAcl and Set-AdminOnlyTree on a scratch folder, then the checks that follow them in the installer. Needs an administrator
        # (taking ownership): on a normal account it says "skipped". On an administrator account everything must work, and when it does not the
        # output says why: the check's own reasons, the owner and the permission list of the folder and of everything in it.
        $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if (-not $isAdmin -and -not $ForceAdminScenarios) { 'skipped=1'; return }
        $d = Join-Path $Work 'canon'; New-Item -ItemType Directory -Force $d | Out-Null
        "me=$([Security.Principal.WindowsIdentity]::GetCurrent().User.Value)"
        try { Set-AdminOnlyAcl $d } catch { "error=Set-AdminOnlyAcl: $($_.Exception.Message)"; Describe-Acl $d; return }
        New-Item -ItemType Directory -Force (Join-Path $d 'sub') | Out-Null
        'x' | Set-Content (Join-Path $d 'sub\f.txt')
        'before-owner-fix:'; Describe-Acl $d -Children
        Set-AdminOnlyTree $d
        'after-owner-fix:'; Describe-Acl $d -Children
        Show-Trust 'folder' { Test-TrustedStateFolder $d }
        Show-Trust 'tree' { Test-AdminOnlyTree $d }
        icacls (Join-Path $d 'sub\f.txt') /grant '*S-1-5-32-545:(M)' | Out-Null
        Show-Trust 'tree-after-grant' { Test-AdminOnlyTree $d }
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

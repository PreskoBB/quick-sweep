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

$me = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value

# "owner <sid>; <SDDL>" of any file or folder, no link followed (install.ps1 has the same helper; uninstall.ps1 does not)
function Show-Item($path) {
    $s = (Get-Item -LiteralPath $path -Force).GetAccessControl()
    "owner $($s.GetOwner([Security.Principal.SecurityIdentifier]).Value); $($s.GetSecurityDescriptorSddlForm('All'))"
}
function Exists($p) { [bool](Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue) }

# Test cleanup only: a scenario can leave folders that only SYSTEM and Administrators may write. As the owner, this account may always rewrite
# the permissions of what it owns, so it hands itself full control again, top-down; a junction met on the way is removed as the link it is.
function Open-ForCleanup([string]$path) {
    $it = Get-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    if (-not $it) { return }
    if ($it.Attributes -band [IO.FileAttributes]::ReparsePoint) { if ($it -is [IO.DirectoryInfo]) { [IO.Directory]::Delete($path) } else { [IO.File]::Delete($path) }; return }
    $isDir = $it -is [IO.DirectoryInfo]
    try {
        $s = $it.GetAccessControl()
        $s.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule ([Security.Principal.SecurityIdentifier]$me), 'FullControl', $(if ($isDir) { 'ContainerInherit, ObjectInherit' } else { 'None' }), 'None', 'Allow'))
        $it.SetAccessControl($s)
    } catch { }
    if ($isDir) { foreach ($c in $it.GetFileSystemInfos()) { Open-ForCleanup $c.FullName } }
}

# A folder whose permissions are exactly this account, SYSTEM and Administrators (so a scratch folder cannot pick up whatever its parent
# grants on the machine the test runs on); what is created inside inherits that.
function New-PrivateFolder([string]$path) {
    New-Item -ItemType Directory -Force $path | Out-Null
    $s = (Get-Item -LiteralPath $path).GetAccessControl()
    $s.SetAccessRuleProtection($true, $false)
    foreach ($r in @($s.Access)) { [void]$s.RemoveAccessRule($r) }
    foreach ($sid in $me, 'S-1-5-18', 'S-1-5-32-544') {
        $s.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule ([Security.Principal.SecurityIdentifier]$sid), 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
    }
    (Get-Item -LiteralPath $path).SetAccessControl($s)
}

try {
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
        "me=$me"
        # what the hosted runner had: an explicit FullControl entry for the account that runs the installer, which is not one of the accounts
        # the installer names and so used to survive. It must be gone afterwards (and the account may only get in through Administrators).
        $seed = (Get-Item -LiteralPath $d).GetAccessControl()
        $seed.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule ([Security.Principal.SecurityIdentifier]$me), 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
        (Get-Item -LiteralPath $d).SetAccessControl($seed)
        'before-install:'; Describe-Acl $d
        try { Set-AdminOnlyAcl $d } catch { "error=Set-AdminOnlyAcl: $($_.Exception.Message)"; Describe-Acl $d; return }
        "folder-difference=$(Get-AdminOnlyDifference $d $true)"
        New-Item -ItemType Directory -Force (Join-Path $d 'sub') | Out-Null
        'x' | Set-Content (Join-Path $d 'sub\f.txt')
        'before-owner-fix:'; Describe-Acl $d -Children
        try { Set-AdminOnlyTree $d } catch { "error=Set-AdminOnlyTree: $($_.Exception.Message)"; Describe-Acl $d -Children; return }
        'after-owner-fix:'; Describe-Acl $d -Children
        "tree-differences=$((@(Get-TreeItems $d) | ForEach-Object { Get-AdminOnlyDifference $_.FullName ($_ -is [IO.DirectoryInfo]) } | Where-Object { $_ }) -join '|')"
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
    'explicit-grant-removed' {
        # Problem 1 without administrator rights: a folder that already has an explicit FullControl entry for the account (what the hosted runner
        # had) ends with exactly the three canonical entries. This account stands in as the owner (a normal account cannot own as Administrators).
        $d = Join-Path $Work 'explicit'; New-Item -ItemType Directory -Force $d | Out-Null
        $s = (Get-Item -LiteralPath $d).GetAccessControl()
        $s.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule ([Security.Principal.SecurityIdentifier]$me), 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow'))
        (Get-Item -LiteralPath $d).SetAccessControl($s)
        "me=$me"
        "before-write-entries=$(@(Get-UntrustedWriteAces (Get-Item -LiteralPath $d).GetAccessControl()).Count)"
        "before=$(Show-Item $d)"
        Set-AdminOnlyAcl $d $me
        "after-write-entries=$(@(Get-UntrustedWriteAces (Get-Item -LiteralPath $d).GetAccessControl()).Count)"
        "after-difference=$(Get-AdminOnlyDifference $d $true $me)"
        "after=$(Show-Item $d)"
    }
    'state-fresh' {
        # a state folder that does not exist yet is created with exactly the canonical descriptor; a tree the installer copied gets the folder
        # and the file variants of it. (This account stands in for Administrators as owner and as a trusted owner in the check.)
        function Get-AdminSids { @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464', $me) }
        "me=$me"
        $d = Join-Path $Work 'fresh'
        $aside = Initialize-StateFolder $d $me @()
        "aside=$aside"
        "dir=$(Show-Item $d)"
        "dir-difference=$(Get-AdminOnlyDifference $d $true $me)"
        "dir-trusted=$((Test-TrustedStateFolder $d 3>$null) -and (Test-AdminOnlyTree $d 3>$null))"
        $t = Join-Path $Work 'tree'
        New-Item -ItemType Directory -Force (Join-Path $t 'sub') | Out-Null
        'x' | Set-Content (Join-Path $t 'sub\f.txt'); 'y' | Set-Content (Join-Path $t 'g.txt')
        Set-AdminOnlyTree $t $me
        foreach ($i in @(Get-TreeItems $t)) {
            $rel = $i.FullName.Substring($t.Length).TrimStart('\')
            "tree[$rel]=$(Show-Item $i.FullName)"
            "tree[$rel]-difference=$(Get-AdminOnlyDifference $i.FullName ($i -is [IO.DirectoryInfo]) $me)"
        }
    }
    'state-junction' {
        # An existing state folder is judged BEFORE anything is changed. A junction inside it (directly, or deeper where only the tree check looks)
        # means the whole folder is renamed aside; the junction's target is left exactly as it was, and nothing is re-owned.
        function Get-AdminSids { @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464', $me) }
        $victim = Join-Path $Work 'victim'; New-Item -ItemType Directory -Force $victim | Out-Null; 'precious' | Set-Content (Join-Path $victim 'v.txt')
        $before = "$(Show-Item $victim)|$(Show-Item (Join-Path $victim 'v.txt'))"
        foreach ($case in 'direct', 'deep') {
            $data = Join-Path $Work "data-$case"
            New-Item -ItemType Directory -Force (Join-Path $data 'sub') | Out-Null
            'settings' | Set-Content (Join-Path $data 'settings.json')
            $linkParent = if ($case -eq 'direct') { $data } else { Join-Path $data 'sub' }
            New-Item -ItemType Junction -Path (Join-Path $linkParent 'planted') -Target $victim | Out-Null
            Set-AdminOnlyAcl $data $me                     # the folder itself looks fine: only the junction is wrong
            "$case-folder-check-alone=$(Test-TrustedStateFolder $data 3>$null)"
            "$case-tree-check-alone=$(Test-AdminOnlyTree $data 3>$null)"
            $sbefore = Show-Item (Join-Path $data 'settings.json')
            $aside = Initialize-StateFolder $data $me @() 3>$null
            "$case-aside-name-ok=$([bool]($aside -and $aside -like "$data.untrusted-*"))"
            "$case-aside-exists=$(Exists $aside)"
            $inAside = if ($case -eq 'direct') { $aside } else { Join-Path $aside 'sub' }
            "$case-aside-still-has-the-junction=$([bool]((Get-Item -LiteralPath (Join-Path $inAside 'planted') -Force).Attributes -band [IO.FileAttributes]::ReparsePoint))"
            "$case-aside-file-untouched=$((Show-Item (Join-Path $aside 'settings.json')) -eq $sbefore)"
            "$case-fresh-difference=$(Get-AdminOnlyDifference $data $true $me)"
            "$case-fresh-empty=$(-not (Get-ChildItem -LiteralPath $data -Force))"
        }
        "victim-unchanged=$("$(Show-Item $victim)|$(Show-Item (Join-Path $victim 'v.txt'))" -eq $before)"
        "victim-file-still-there=$(Test-Path -LiteralPath (Join-Path $victim 'v.txt'))"
    }
    'state-trusted-kept' {
        # the positive control: an existing folder that passes every check is not set aside, and its contents are not touched
        function Get-AdminSids { @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464', $me) }
        $data = Join-Path $Work 'data-ok'
        New-Item -ItemType Directory -Force (Join-Path $data 'public') | Out-Null
        'settings' | Set-Content (Join-Path $data 'settings.json')
        'flag' | Set-Content (Join-Path $data 'public\gaming.json')
        Set-AdminOnlyAcl $data $me; Set-AdminOnlyTree $data $me
        $before = (@(Get-TreeItems $data) | ForEach-Object { "$($_.FullName)=$(Show-Item $_.FullName)" }) -join '|'
        $aside = Initialize-StateFolder $data $me @()
        "aside=$aside"
        "kept=$(Exists $data)"
        "contents-unchanged=$(((@(Get-TreeItems $data) | ForEach-Object { "$($_.FullName)=$(Show-Item $_.FullName)" }) -join '|') -eq $before)"
        "file-count=$(@(Get-TreeItems $data).Count)"
    }
    'state-userowned' {
        # A folder a normal user pre-created in ProgramData, with a file in it: owned by that user, so not trusted. It must be renamed aside
        # as it is: the file keeps its owner and permissions (nothing is re-owned or reset), and the original name is free for a fresh folder.
        $data = Join-Path $Work 'data-planted'
        New-Item -ItemType Directory -Force (Join-Path $data 'sub') | Out-Null
        'planted' | Set-Content (Join-Path $data 'settings.json'); 'x' | Set-Content (Join-Path $data 'sub\more.txt')
        "me=$me"
        $before = (@(Get-TreeItems $data) | ForEach-Object { "$($_.FullName.Substring($data.Length))=$(Show-Item $_.FullName)" }) -join '|'
        $out = @(Move-UntrustedFolderAside $data 3>&1)
        $aside = $out | Where-Object { $_ -is [string] } | Select-Object -Last 1
        "reasons=$(($out | Where-Object { $_ -is [System.Management.Automation.WarningRecord] } | ForEach-Object { $_.Message.Trim() }) -join ' / ')"
        "aside-name-ok=$([bool]($aside -and $aside -like "$data.untrusted-*"))"
        "original-gone=$(-not (Exists $data))"
        "contents-unchanged=$(((@(Get-TreeItems $aside) | ForEach-Object { "$($_.FullName.Substring($aside.Length))=$(Show-Item $_.FullName)" }) -join '|') -eq $before)"
    }
    'tree-link-refused' {
        # Set-AdminOnlyTree throws on a junction anywhere below, before it changes anything, and never touches where the junction points
        $victim = Join-Path $Work 'victim'; New-Item -ItemType Directory -Force $victim | Out-Null; 'precious' | Set-Content (Join-Path $victim 'v.txt')
        $t = Join-Path $Work 'tree'; New-Item -ItemType Directory -Force (Join-Path $t 'sub') | Out-Null
        'a' | Set-Content (Join-Path $t 'a.txt'); 'b' | Set-Content (Join-Path $t 'sub\b.txt')
        New-Item -ItemType Junction -Path (Join-Path $t 'sub\planted') -Target $victim | Out-Null
        $snap = { (@($t, $victim, (Join-Path $victim 'v.txt'), (Join-Path $t 'a.txt'), (Join-Path $t 'sub'), (Join-Path $t 'sub\b.txt')) | ForEach-Object { "$_=$(Show-Item $_)" }) -join '|' }
        $before = & $snap
        try { Set-AdminOnlyTree $t $me; 'threw=False' } catch { 'threw=True'; "message=$($_.Exception.Message)" }
        "unchanged-including-the-victim=$((& $snap) -eq $before)"
        try { Get-TreeItems $t | Out-Null; 'listing-threw=False' } catch { 'listing-threw=True' }
    }
    'remove-tree-links' {
        # Remove-AdminTree (both scripts): a junction inside a tree, and a junction as the top, go as links; their target survives
        $victim = Join-Path $Work 'victim'; New-Item -ItemType Directory -Force $victim | Out-Null; 'precious' | Set-Content (Join-Path $victim 'v.txt')
        $t = Join-Path $Work 'tree'; New-Item -ItemType Directory -Force (Join-Path $t 'a\b') | Out-Null
        'x' | Set-Content (Join-Path $t 'a\b\c.txt'); 'ro' | Set-Content (Join-Path $t 'ro.txt'); Set-ItemProperty -LiteralPath (Join-Path $t 'ro.txt') -Name IsReadOnly -Value $true
        New-Item -ItemType Junction -Path (Join-Path $t 'a\planted') -Target $victim | Out-Null
        Remove-AdminTree $t
        "tree-removed=$(-not (Exists $t))"
        "victim-intact=$(Test-Path -LiteralPath (Join-Path $victim 'v.txt'))"
        $top = Join-Path $Work 'toplink'; New-Item -ItemType Junction -Path $top -Target $victim | Out-Null
        Remove-AdminTree $top
        "toplink-removed=$(-not (Exists $top))"
        "victim-intact-after-toplink=$(Test-Path -LiteralPath (Join-Path $victim 'v.txt'))"
        Remove-AdminTree (Join-Path $Work 'does-not-exist')
        'missing-ok=True'
    }
    'remove-state-folder' {
        # uninstall.ps1: a folder a user could have written to is not walked as an administrator (only the program's own file names go);
        # one that passes the checks is removed whole. Run once per case (-Work is a different scratch folder each time).
        $victim = Join-Path $Work 'victim'; New-Item -ItemType Directory -Force $victim | Out-Null; 'precious' | Set-Content (Join-Path $victim 'v.txt')
        $data = Join-Path $Work 'QuickSweep'
        New-PrivateFolder $data
        New-Item -ItemType Directory -Force (Join-Path $data 'public') | Out-Null
        'settings' | Set-Content (Join-Path $data 'settings.json'); 'flag' | Set-Content (Join-Path $data 'public\gaming.json'); 'mine' | Set-Content (Join-Path $data 'notes.txt')
        function Get-AdminSids { @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464', $me) }
        # case 1: this account counts as an administrator and the folder is plain: removed whole
        "trusted-case-removed=$(Remove-StateFolder $data 3>$null)"
        "trusted-case-gone=$(-not (Exists $data))"
        # case 2: this account is NOT trusted (the real list), the folder holds a junction, an unknown file and known files
        function Get-AdminSids { @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464') }
        New-PrivateFolder $data
        New-Item -ItemType Directory -Force (Join-Path $data 'public') | Out-Null
        'settings' | Set-Content (Join-Path $data 'settings.json'); 'flag' | Set-Content (Join-Path $data 'public\gaming.json'); 'mine' | Set-Content (Join-Path $data 'notes.txt')
        New-Item -ItemType Junction -Path (Join-Path $data 'planted') -Target $victim | Out-Null
        $snap = { (@($data, (Join-Path $data 'settings.json'), (Join-Path $data 'public'), (Join-Path $data 'public\gaming.json'), (Join-Path $data 'notes.txt'), $victim, (Join-Path $victim 'v.txt')) | ForEach-Object { "$_=$(Show-Item $_)" }) -join '|' }
        $before = & $snap
        "untrusted-case-removed=$(Remove-StateFolder $data 3>$null)"
        # nothing at all is deleted inside a folder that is not administrators-only, not even the program's own file names
        "untrusted-case-nothing-deleted=$((& $snap) -eq $before)"
        "untrusted-case-junction-untouched=$(Exists (Join-Path $data 'planted'))"
        "victim-intact=$(Test-Path -LiteralPath (Join-Path $victim 'v.txt'))"
        # case 3: the same without a junction (a plain folder the user filled): also left alone
        [IO.Directory]::Delete((Join-Path $data 'planted'))
        $before = & $snap
        "untrusted-plain-removed=$(Remove-StateFolder $data 3>$null)"
        "untrusted-plain-nothing-deleted=$((& $snap) -eq $before)"
        # case 4: a link standing in the place of the folder is removed as a link; its target is not touched
        $top = Join-Path $Work 'QuickSweepLink'; New-Item -ItemType Junction -Path $top -Target $victim | Out-Null
        "link-case-removed=$(Remove-StateFolder $top 3>$null)"
        "link-case-gone=$(-not (Exists $top))"
        "victim-intact-after-link=$(Test-Path -LiteralPath (Join-Path $victim 'v.txt'))"
    }
    'create-exists' {
        # New-AdminOnlyFolder creates with CreateDirectoryW and the descriptor; an existing folder is reported (ERROR_ALREADY_EXISTS) and left exactly as it was
        function Get-AdminSids { @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464', $me) }
        $pre = Join-Path $Work 'pre'; New-Item -ItemType Directory -Force $pre | Out-Null; 'x' | Set-Content (Join-Path $pre 'a.txt')
        $before = "$(Show-Item $pre)|$(Show-Item (Join-Path $pre 'a.txt'))"
        "existing-returned=$(New-AdminOnlyFolder $pre $me)"
        "existing-api-returned=$(New-FolderWithDescriptor $pre)"
        "existing-unchanged=$("$(Show-Item $pre)|$(Show-Item (Join-Path $pre 'a.txt'))" -eq $before)"
        $fresh = Join-Path $Work 'fresh'
        "fresh-returned=$(New-AdminOnlyFolder $fresh $me)"
        "fresh=$(Show-Item $fresh)"
        "fresh-difference=$(Get-AdminOnlyDifference $fresh $true $me)"
        try { [void](New-FolderWithDescriptor (Join-Path $Work 'no-parent\x')); 'missing-parent-threw=False' } catch { 'missing-parent-threw=True'; "missing-parent-message=$($_.Exception.Message)" }
    }
    'state-race' {
        # Somebody creates the folder between the installer's look ("nothing there") and its creation. CreateDirectoryW says ERROR_ALREADY_EXISTS;
        # the folder is not adopted or checked, it is set aside as it is, and a new one is created. (The look is staged by hiding the folder
        # from the existence test once and skipping the earlier judgement, which is what an earlier look finding nothing amounts to.)
        function Get-AdminSids { @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464', $me) }
        $data = Join-Path $Work 'data-race'
        New-Item -ItemType Directory -Force (Join-Path $data 'sub') | Out-Null
        'planted' | Set-Content (Join-Path $data 'settings.json'); 'x' | Set-Content (Join-Path $data 'sub\more.txt')
        $before = (@(Get-TreeItems $data) | ForEach-Object { "$($_.FullName.Substring($data.Length))=$(Show-Item $_.FullName)" }) -join '|'
        function Move-UntrustedFolderAside([string]$dir) { return $null }
        $script:hide = 1
        function Test-Exists($p) { if ($p -eq $data -and $script:hide -gt 0) { $script:hide--; return $false }; [bool](Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue) }
        $aside = Initialize-StateFolder $data $me @() 3>$null
        "aside-name-ok=$([bool]($aside -and $aside -like "$data.untrusted-*"))"
        "aside-contents-unchanged=$(((@(Get-TreeItems $aside) | ForEach-Object { "$($_.FullName.Substring($aside.Length))=$(Show-Item $_.FullName)" }) -join '|') -eq $before)"
        "fresh-difference=$(Get-AdminOnlyDifference $data $true $me)"
        "fresh-empty=$(-not (Get-ChildItem -LiteralPath $data -Force))"
    }
    default { "unknown-scenario=$Scenario"; exit 2 }
}
} finally {
    # scratch folders of the scenarios above can be locked to Administrators: hand them back before the caller deletes the sandbox
    if ($Work -and (Test-Path -LiteralPath $Work)) { try { Open-ForCleanup $Work } catch { } }
}

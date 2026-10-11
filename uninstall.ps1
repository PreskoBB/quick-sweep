<#
 Quick Sweep uninstaller. Run from an elevated PowerShell:   .\uninstall.ps1

 Removes the logon task, the Start-menu shortcut, the program files and (unless -KeepData) the settings, session
 history and logs under C:\ProgramData\QuickSweep, when that folder is administrators-only (a folder a normal program can write to is never
 deleted from inside as an administrator, so it is left for you to delete; that includes the per-user folder %LOCALAPPDATA%\QuickSweep,
 which only holds what a plain, non-elevated run wrote). The PawnIO driver is removed
 only if install.ps1 installed it (use -KeepSensorDriver to leave it in place, for example when another tool uses it).
 It also cleans up anything left by the earlier name of this program ("PC Care": task PcCare, C:\Program Files\PcCare,
 C:\ProgramData\PcCare, Start-menu link "PC Care").
#>
[CmdletBinding()]
param(
    [switch]$KeepData,
    [switch]$KeepSensorDriver
)
$ErrorActionPreference = 'Stop'

# Started from PowerShell 7 (pwsh)? This script is written for Windows PowerShell 5.1, the one "Install Quick Sweep.bat" uses (the
# ScheduledTasks and Appx modules it needs are Windows PowerShell modules): run it again in 5.1, from its system folder, same arguments.
if ($PSVersionTable.PSEdition -eq 'Core') {
    $ps51 = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\powershell.exe'
    $forward = @()
    foreach ($k in $PSBoundParameters.Keys) {
        $v = $PSBoundParameters[$k]
        if ($v -is [switch]) { if ($v.IsPresent) { $forward += "-$k" } } else { $forward += "-$k"; $forward += [string]$v }
    }
    & $ps51 -NoProfile -ExecutionPolicy Bypass -File $MyInvocation.MyCommand.Path @forward
    exit $LASTEXITCODE
}

# The environment of an elevated process contains the user's own variables (HKCU\Environment), which any normal program can change:
# ProgramFiles, ProgramData, PATH, PSModulePath... Nothing below trusts them. Locations come from Windows' known folders
# (SHGetKnownFolderPath, GetSystemDirectory), native tools are found in System32 only, and PowerShell modules load from the
# system folders only, so a variable set by a user-level program cannot point this elevated script at a folder or program it controls.
function Set-SafeEnvironment {
    $sys = [Environment]::SystemDirectory
    $env:Path = @($sys, (Split-Path $sys -Parent), (Join-Path $sys 'Wbem'), (Join-Path $sys 'WindowsPowerShell\v1.0')) -join ';'
    $env:PSModulePath = (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'WindowsPowerShell\Modules') + ';' + (Join-Path $sys 'WindowsPowerShell\v1.0\Modules')
}

# Imports modules by the full path of their manifest in the Windows PowerShell system folder, so nothing depends on how PSModulePath was
# inherited (a PowerShell 7 window that starts this one hands over its own module path, and autoloading would then find PowerShell 7's
# incompatible copies first: "the module could not be loaded").
function Import-SystemModules([string[]]$Names, [switch]$Optional) {
    $root = Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\Modules'
    foreach ($n in $Names) {
        $manifest = Join-Path (Join-Path $root $n) "$n.psd1"
        try { Import-Module -Name $manifest -Force -DisableNameChecking -ErrorAction Stop }
        catch { if (-not $Optional) { throw "Cannot load the PowerShell module $n from $manifest : $_" } }
    }
}

Set-SafeEnvironment

function Get-KnownLocations {
    @{
        System       = [Environment]::SystemDirectory
        ProgramFiles = [Environment]::GetFolderPath('ProgramFiles')
        ProgramData  = [Environment]::GetFolderPath('CommonApplicationData')
        # not GetFolderPath('CommonStartMenu'): that one expands %ProgramData% from the environment. ProgramData itself does not.
        StartMenu    = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'Microsoft\Windows\Start Menu\Programs'
        UserProfile  = [Environment]::GetFolderPath('UserProfile')
    }
}

# True when $path (after '..' and relative parts are resolved) lies inside $dir.
function Test-UnderDirectory($path, $dir) {
    $p = [IO.Path]::GetFullPath($path)
    $d = [IO.Path]::GetFullPath($dir).TrimEnd('\') + '\'
    return $p.StartsWith($d, [StringComparison]::OrdinalIgnoreCase)
}

# winget lives in a per-user alias folder that a normal program can write to, so it is never run through PATH: the package's own folder
# below Program Files\WindowsApps is looked up instead. $null when it cannot be found.
function Get-WingetPath($programFiles) {
    try {
        $pkg = Get-AppxPackage -AllUsers -Name Microsoft.DesktopAppInstaller -ErrorAction Stop | Sort-Object Version -Descending | Select-Object -First 1
        if ($pkg) {
            $exe = Join-Path $pkg.InstallLocation 'winget.exe'
            if ((Test-Path -LiteralPath $exe) -and (Test-UnderDirectory $exe (Join-Path $programFiles 'WindowsApps'))) { return $exe }
        }
    } catch { }
    return $null
}

Import-SystemModules @('ScheduledTasks')
Import-SystemModules @('Appx') -Optional      # only used to find winget (to remove PawnIO)

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell (right-click PowerShell > Run as administrator).'
}

$loc          = Get-KnownLocations
$programFiles = $loc.ProgramFiles
$programData  = $loc.ProgramData
$startMenu    = $loc.StartMenu
$localAppData = Join-Path $loc.UserProfile 'AppData\Local'

# both names: the current one and the earlier one
$names = @(
    @{ Task = 'QuickSweep'; Exe = 'QuickSweep'; Dir = 'QuickSweep'; Lnk = 'Quick Sweep.lnk' },
    @{ Task = 'PcCare';     Exe = 'PcCare';     Dir = 'PcCare';     Lnk = 'PC Care.lnk' }
)

function Test-Link($p) { [bool]((Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue).Attributes -band [IO.FileAttributes]::ReparsePoint) }

function Get-AdminSids { @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464') }

# Allow-entries of an ACL that give somebody other than SYSTEM, Administrators and TrustedInstaller a way to write, delete, re-permission
# or take ownership of what is in the folder, INCLUDING entries that only children inherit (a user who is granted "modify, inherited by new
# files" can change everything copied there afterwards). The only entries excused are CREATOR OWNER and CREATOR GROUP when they are
# inherit-only: they are placeholders that become the owner or group of a future child, and a child created by an administrator is owned by one.
# GENERIC_WRITE and GENERIC_ALL count as write-like too: inherit-only entries are stored with generic rights.
function Get-UntrustedWriteAces($sec) {
    $admin = Get-AdminSids
    $writeLike = [int]([Security.AccessControl.FileSystemRights]'Write, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership') -bor 0x50000000
    $inheritOnly = [Security.AccessControl.PropagationFlags]::InheritOnly
    foreach ($r in $sec.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($r.AccessControlType -ne 'Allow') { continue }
        $sid = $r.IdentityReference.Value
        if ($admin -contains $sid) { continue }
        $onlyForChildren = [bool]($r.PropagationFlags -band $inheritOnly)
        if (($sid -eq 'S-1-3-0' -or $sid -eq 'S-1-3-1') -and $onlyForChildren) { continue }
        if (([int]$r.FileSystemRights -band $writeLike) -ne 0) { "$sid ($($r.FileSystemRights)$(if ($onlyForChildren) { ', inherited by new children' }))" }
    }
}

# The trust test the app applies to its own state folder: not a link; owned by SYSTEM, Administrators or TrustedInstaller; nobody else
# may write, delete or re-permission it (or what will be created in it); nothing directly inside is a link or belongs to somebody else.
function Test-TrustedStateFolder($dir) {
    $admin = Get-AdminSids
    if (-not (Test-Path -LiteralPath $dir)) { return $false }
    if (Test-Link $dir) { Write-Warning "      $dir is a junction or symbolic link"; return $false }
    $sec = Get-Acl -LiteralPath $dir
    $owner = $sec.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($admin -notcontains $owner) { Write-Warning "      $dir is owned by $owner, not by Administrators or SYSTEM"; return $false }
    $bad = @(Get-UntrustedWriteAces $sec)
    if ($bad.Count -gt 0) { Write-Warning "      $dir lets $($bad -join '; ') change it"; return $false }
    foreach ($e in Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue) {
        if ($e.Attributes -band [IO.FileAttributes]::ReparsePoint) { Write-Warning "      $($e.FullName) is a link"; return $false }
        $o = (Get-Acl -LiteralPath $e.FullName).GetOwner([Security.Principal.SecurityIdentifier]).Value
        if ($admin -notcontains $o) { Write-Warning "      $($e.FullName) is owned by $o"; return $false }
    }
    return $true
}

# The same test for everything below a folder, however deep (links are refused, never walked into): every file and folder must belong to an
# administrative account and must not give anybody else write-like rights. Used on what was just copied.
function Test-AdminOnlyTree($dir) {
    $admin = Get-AdminSids
    try {
        $stack = New-Object System.Collections.Stack
        $stack.Push($dir)
        while ($stack.Count -gt 0) {
            $d = $stack.Pop()
            foreach ($e in Get-ChildItem -LiteralPath $d -Force -ErrorAction Stop) {
                if ($e.Attributes -band [IO.FileAttributes]::ReparsePoint) { Write-Warning "      $($e.FullName) is a link"; return $false }
                $sec = Get-Acl -LiteralPath $e.FullName
                $o = $sec.GetOwner([Security.Principal.SecurityIdentifier]).Value
                if ($admin -notcontains $o) { Write-Warning "      $($e.FullName) is owned by $o"; return $false }
                $bad = @(Get-UntrustedWriteAces $sec)
                if ($bad.Count -gt 0) { Write-Warning "      $($e.FullName) lets $($bad -join '; ') change it"; return $false }
                if ($e.PSIsContainer) { $stack.Push($e.FullName) }
            }
        }
        return $true
    } catch { Write-Warning "      could not check the permissions below ${dir}: $_"; return $false }
}

# Removes a folder tree without ever following a link: a junction or symbolic link is removed as the link it is (where it points is never
# touched), real folders are emptied entry by entry, depth first. Throws when something cannot be removed.
function Remove-TreeNoFollow([IO.FileSystemInfo]$e) {
    if ($e.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        if ($e -is [IO.DirectoryInfo]) { [IO.Directory]::Delete($e.FullName) } else { [IO.File]::Delete($e.FullName) }
        return
    }
    if ($e.Attributes -band [IO.FileAttributes]::ReadOnly) { [IO.File]::SetAttributes($e.FullName, [IO.FileAttributes]::Normal) }
    if ($e -is [IO.DirectoryInfo]) {
        foreach ($c in $e.GetFileSystemInfos()) { Remove-TreeNoFollow $c }
        [IO.Directory]::Delete($e.FullName)
    } else {
        [IO.File]::Delete($e.FullName)
    }
}

function Remove-AdminTree($p) {
    $it = Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
    if ($it) { Remove-TreeNoFollow $it }
}

# %ProgramData%\QuickSweep. A normal user can create a folder of that name in ProgramData and fill it (the installer sets such a folder aside
# and makes a new one), and anything deleted by path as an administrator inside a folder a normal program can write to can be raced (a link
# put in the place of a folder between the check and the delete). So nothing is deleted inside such a folder at all: a folder is only removed
# when it passes the checks the app applies (owned by Administrators or SYSTEM, nobody else able to write, no link anywhere, every item
# administrators-only), which also means nobody else can change it while it is being removed. Returns $true when it is gone, $false when it
# was left alone (the checks print their reasons). A link standing in its place is removed as a link, which touches nothing it points to.
function Remove-StateFolder($p) {
    $it = Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue
    if (-not $it) { return $false }
    if ($it.Attributes -band [IO.FileAttributes]::ReparsePoint) { Remove-TreeNoFollow $it; return $true }
    $ok = $false
    try { $ok = [bool]((Test-TrustedStateFolder $p) -and (Test-AdminOnlyTree $p)) } catch { Write-Warning "  could not check $p : $($_.Exception.Message)" }
    if ($ok) { Remove-AdminTree $p; return $true }
    return $false
}

function Test-Exists($p) { [bool](Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue) }      # also true for a link whose target is gone

# did install.ps1 (of either name) install PawnIO?
$weInstalledDriver = $false
foreach ($n in $names) {
    $st = Join-Path (Join-Path $programFiles $n.Dir) 'install-state.json'
    if (Test-Path -LiteralPath $st) { try { if ([bool]((Get-Content -LiteralPath $st -Raw | ConvertFrom-Json).InstalledPawnIO)) { $weInstalledDriver = $true } } catch { } }
}

Write-Host 'Stopping Quick Sweep...'
foreach ($n in $names) {
    if (Get-ScheduledTask -TaskName $n.Task -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $n.Task -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $n.Task -Confirm:$false
        Write-Host "  logon task $($n.Task) removed"
    }
}
Get-Process ($names | ForEach-Object { $_.Exe }) -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

foreach ($n in $names) {
    $lnk = Join-Path $startMenu $n.Lnk
    if (Test-Path -LiteralPath $lnk) { Remove-Item -LiteralPath $lnk -Force; Write-Host "  shortcut $($n.Lnk) removed" }
    $dir = Join-Path $programFiles $n.Dir
    if (Test-Path -LiteralPath $dir) { Remove-AdminTree $dir; Write-Host "  program files removed ($dir)" }
}

if ($weInstalledDriver -and -not $KeepSensorDriver) {
    $winget = Get-WingetPath $programFiles
    if ($winget) {
        Write-Host '  removing the PawnIO driver (installed by install.ps1)...'
        & $winget uninstall --id namazso.PawnIO -e --silent
    } else {
        Write-Warning '  winget was not found in its package folder: remove PawnIO from Settings > Apps if you no longer need it.'
    }
} elseif ($weInstalledDriver) {
    Write-Host '  PawnIO driver kept (-KeepSensorDriver)'
}

if (-not $KeepData) {
    foreach ($n in $names) {
        $elev = Join-Path $programData $n.Dir
        if (Test-Exists $elev) {
            if (Remove-StateFolder $elev) { Write-Host "  settings, sessions and logs removed ($elev)" }
            else { Write-Host "  $elev was left in place because it is not administrators-only (see above); delete it yourself when you like" }
        }
        # a folder a normal program can write to: nothing is deleted inside it as an administrator
        $user = Join-Path $localAppData $n.Dir
        if (Test-Exists $user) { Write-Host "  $user was left in place (it is writable by normal programs, so it is not deleted as an administrator); delete it yourself when you like" }
    }
} else {
    Write-Host '  data kept (-KeepData)'
}
Write-Host 'Quick Sweep is removed.'

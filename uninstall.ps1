<#
 Quick Sweep uninstaller. Run from an elevated PowerShell:   .\uninstall.ps1

 Removes the logon task, the Start-menu shortcut, the program files and (unless -KeepData) the settings, session
 history and logs under C:\ProgramData\QuickSweep (and the per-user folder %LOCALAPPDATA%\QuickSweep). The PawnIO driver is removed
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

# Removes an admin-only folder; a link is removed as a link, never walked into.
function Remove-AdminTree($p) {
    if (-not (Test-Path -LiteralPath $p)) { return }
    $it = Get-Item -LiteralPath $p -Force
    if ($it.Attributes -band [IO.FileAttributes]::ReparsePoint) { $it.Delete() } else { Remove-Item -LiteralPath $p -Recurse -Force }
}

# The per-user folder is writable by a normal process, so this elevated script never deletes recursively there: only files with the
# program's own names, one level deep, and the folder itself when it ends up empty.
function Remove-UserFolderKnownFiles($dir) {
    if (-not (Test-Path -LiteralPath $dir) -or (Test-Link $dir)) { return }
    $files = 'settings.json', 'sessions.json', 'drivehealth.json', 'deletions.log', 'deletions.log.old', 'pccare.log', 'pccare.log.old',
             'quicksweep.log', 'quicksweep.log.old', 'open.txt', 'dump.json', 'gamemode-state.json', 'state-refused.txt', 'migrated-from-user-folder.txt'
    foreach ($n in $files) { $f = Join-Path $dir $n; if ((Test-Path -LiteralPath $f -PathType Leaf) -and -not (Test-Link $f)) { [IO.File]::Delete($f) } }
    foreach ($sub in 'public', 'jobs') {
        $d = Join-Path $dir $sub
        if ((Test-Path -LiteralPath $d) -and -not (Test-Link $d)) {
            foreach ($f in Get-ChildItem -LiteralPath $d -File -Force -ErrorAction SilentlyContinue) { if (-not ($f.Attributes -band [IO.FileAttributes]::ReparsePoint) -and $f.Name -match '^[A-Za-z0-9._-]+$') { [IO.File]::Delete($f.FullName) } }
            if (-not (Get-ChildItem -LiteralPath $d -Force -ErrorAction SilentlyContinue)) { [IO.Directory]::Delete($d) }
        }
    }
    if (-not (Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue)) { [IO.Directory]::Delete($dir) }
}

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
        if (Test-Path -LiteralPath $elev) { Remove-AdminTree $elev; Write-Host "  settings, sessions and logs removed ($elev)" }
        $user = Join-Path $localAppData $n.Dir
        if (Test-Path -LiteralPath $user) {
            Remove-UserFolderKnownFiles $user
            if (Test-Path -LiteralPath $user) { Write-Host "  $user holds files that are not Quick Sweep's own; delete it yourself when you like" }
            else { Write-Host "  per-user data removed ($user)" }
        }
    }
} else {
    Write-Host '  data kept (-KeepData)'
}
Write-Host 'Quick Sweep is removed.'

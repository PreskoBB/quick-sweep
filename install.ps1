<#
 Quick Sweep installer. Run ONCE from an elevated PowerShell (right-click > Run as administrator):

     .\install.ps1

 What it does (and nothing else):
   1. Copies the app (from .\app in the release zip, or .\dist after build.ps1) to C:\Program Files\QuickSweep (admin-writable
      only, because the app runs elevated), installs the .NET 10 Desktop Runtime through winget if it is missing, and
      creates the state folder C:\ProgramData\QuickSweep: SYSTEM + Administrators full control, users read-only.
   2. Installs the PawnIO driver (signed, from the official winget package namazso.PawnIO) if it is missing.
      Quick Sweep reads the CPU temperature through it. Skip with -SkipSensorDriver (CPU temperature stays unavailable).
   3. Registers a hidden Task Scheduler task "QuickSweep": starts at logon, highest privileges, no console window, no UAC prompt.
   4. Adds a Start-menu shortcut "Quick Sweep".
   5. Starts the app.
 uninstall.ps1 reverses all of it.

 Upgrading from the earlier name of this program ("PC Care": task PcCare, C:\Program Files\PcCare, C:\ProgramData\PcCare, Start-menu
 link "PC Care") is automatic: its settings, sessions and deletion log are copied into the new secured state folder (only
 from a folder that passes the same trust checks the app applies), then the old task, program files, shortcut and state
 folder are removed. The PawnIO driver is kept. -KeepOldData leaves the old state folder in place.

 Optional: -ImportSettings <file.json> merges that settings file into C:\ProgramData\QuickSweep\settings.json during the install
 (the same code as Settings > Import: only the keys that are in the file change, everything else keeps its value). It runs
 QuickSweep.exe --import-settings elevated before the task starts, so one UAC prompt covers install and settings.
#>
[CmdletBinding()]
param(
    [string]$Source,
    [switch]$SkipSensorDriver,
    [switch]$NoStart,
    [switch]$Yes,
    [string]$ImportSettings,
    [switch]$KeepOldData
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
# below Program Files\WindowsApps is looked up instead (needs the elevation this script already has). $null when it cannot be found.
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

Import-SystemModules @('Microsoft.PowerShell.Security', 'ScheduledTasks')
Import-SystemModules @('Appx') -Optional      # only used to find winget; without it the installer says so and the steps that need winget are skipped

# Windows PowerShell 5.1 leaves $PSScriptRoot empty in param defaults, so resolve the folder here.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
# the release zip keeps the program in .\app, a source checkout publishes to .\dist (build.ps1)
if (-not $Source) {
    $Source = foreach ($c in 'app', 'dist', '.') { $p = Join-Path $here $c; if (Test-Path (Join-Path $p 'QuickSweep.exe')) { $p; break } }
    if (-not $Source) { $Source = Join-Path $here 'dist' }
}
try { Start-Transcript -Path (Join-Path $here 'install.log') -Force | Out-Null } catch { }
trap { Write-Host "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" -ForegroundColor Red; try { Stop-Transcript | Out-Null } catch { }; exit 1 }

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell (right-click PowerShell > Run as administrator).'
}

$loc          = Get-KnownLocations
$programFiles = $loc.ProgramFiles
$programData  = $loc.ProgramData
$taskName   = 'QuickSweep'
$installDir = Join-Path $programFiles 'QuickSweep'
$exe        = Join-Path $installDir 'QuickSweep.exe'
$lnk        = Join-Path $loc.StartMenu 'Quick Sweep.lnk'
$state      = Join-Path $installDir 'install-state.json'
$dataDir    = Join-Path $programData 'QuickSweep'
$user       = $id.Name
$adminSids  = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')

# the earlier name of this program
$oldTask    = 'PcCare'
$oldInstall = Join-Path $programFiles 'PcCare'
$oldData    = Join-Path $programData 'PcCare'
$oldLnk     = Join-Path $loc.StartMenu 'PC Care.lnk'
$oldUserDir = Join-Path (Join-Path $loc.UserProfile 'AppData\Local') 'PcCare'

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

# Owner Administrators, inheritance removed, SYSTEM and Administrators full control, Users read and execute (inherited by what is inside).
function Set-AdminOnlyAcl($dir) {
    icacls $dir /setowner '*S-1-5-32-544' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls could not take ownership of $dir for Administrators" }
    icacls $dir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls could not set the permissions of $dir" }
    # read it back: the owner must really be Administrators now (not assumed from the exit code)
    $now = (Get-Acl -LiteralPath $dir).GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($now -ne 'S-1-5-32-544') { throw "The owner of $dir is $now after setting it to Administrators (S-1-5-32-544)." }
}

# Everything below a folder: owner Administrators, then inherit the folder's protected permissions. The owner is set explicitly because the
# default owner of what an elevated administrator creates can be that person's own account (a policy decides), and that account is also
# the one a normal program of the same user runs as: an owner may always rewrite the permissions of what it owns, so a file owned by the
# user's own account is not administrators-only no matter what its permission list says. Files owned by Administrators are not: the
# Administrators group is deny-only in the normal program's token.
function Set-AdminOnlyTree($dir) {
    icacls "$dir\*" /setowner '*S-1-5-32-544' /T /C | Out-Null
    icacls "$dir\*" /reset /T /C | Out-Null
}

# Removes an admin-only folder; a link is removed as a link, never walked into.
function Remove-AdminTree($p) {
    if (-not (Test-Path -LiteralPath $p)) { return }
    $it = Get-Item -LiteralPath $p -Force
    if ($it.Attributes -band [IO.FileAttributes]::ReparsePoint) { $it.Delete() } else { Remove-Item -LiteralPath $p -Recurse -Force }
}

# The per-user folders (%LOCALAPPDATA%\...) are writable by a normal process, so an elevated script never deletes recursively there:
# only files with the program's own names, one level deep, and the folder itself when it ends up empty.
function Remove-UserFolderKnownFiles($dir) {
    if (-not (Test-Path -LiteralPath $dir) -or (Test-Link $dir)) { return }
    $names = 'settings.json', 'sessions.json', 'drivehealth.json', 'deletions.log', 'deletions.log.old', 'pccare.log', 'pccare.log.old',
             'quicksweep.log', 'quicksweep.log.old', 'open.txt', 'dump.json', 'gamemode-state.json', 'state-refused.txt', 'migrated-from-user-folder.txt'
    foreach ($n in $names) { $f = Join-Path $dir $n; if ((Test-Path -LiteralPath $f -PathType Leaf) -and -not (Test-Link $f)) { [IO.File]::Delete($f) } }
    foreach ($sub in 'public', 'jobs') {
        $d = Join-Path $dir $sub
        if ((Test-Path -LiteralPath $d) -and -not (Test-Link $d)) {
            foreach ($f in Get-ChildItem -LiteralPath $d -File -Force -ErrorAction SilentlyContinue) { if (-not ($f.Attributes -band [IO.FileAttributes]::ReparsePoint) -and $f.Name -match '^[A-Za-z0-9._-]+$') { [IO.File]::Delete($f.FullName) } }
            if (-not (Get-ChildItem -LiteralPath $d -Force -ErrorAction SilentlyContinue)) { [IO.Directory]::Delete($d) }
        }
    }
    if (-not (Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue)) { [IO.Directory]::Delete($dir) }
}

if (-not (Test-Path (Join-Path $Source 'QuickSweep.exe'))) {
    throw "No QuickSweep.exe in '$Source'. Run .\build.ps1 first (or pass -Source)."
}
if ($ImportSettings) {
    # fail before anything is changed when the file is not there
    if (-not (Test-Path -LiteralPath $ImportSettings -PathType Leaf)) { throw "-ImportSettings: file not found: $ImportSettings" }
    $ImportSettings = (Resolve-Path -LiteralPath $ImportSettings).Path
}

# --- stop a running copy -------------------------------------------------
Write-Host '[1/5] Stopping any running copy...'
foreach ($t in $taskName, $oldTask) { if (Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue) { Stop-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue } }
Get-Process QuickSweep, PcCare -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

# --- files -----------------------------------------------------------------
Write-Host "[2/5] Copying files to $installDir ..."
$old = $null
if (Test-Path $state) { try { $old = Get-Content $state -Raw | ConvertFrom-Json } catch { } }
# an install of the earlier name knows whether it was the one that installed PawnIO: keep that fact, so uninstall stays correct
$oldState = Join-Path $oldInstall 'install-state.json'
if (-not $old -and (Test-Path -LiteralPath $oldState) -and -not (Test-Link $oldInstall)) { try { $old = Get-Content -LiteralPath $oldState -Raw | ConvertFrom-Json } catch { } }
if (-not (Test-UnderDirectory $exe $programFiles)) { throw "$exe is not below the real Program Files folder ($programFiles)." }
New-Item -ItemType Directory -Force $installDir | Out-Null
# the folder the elevated task will run from must be writable by administrators only: refuse otherwise (nothing has been copied yet)
if (-not (Test-TrustedStateFolder $installDir)) { throw "$installDir is not an administrators-only folder (see the warning above), so Quick Sweep will not be installed into it." }
Set-AdminOnlyAcl $installDir                       # explicit and protected before anything is copied in: what arrives inherits it
robocopy $Source $installDir /MIR /NFL /NDL /NJH /NJS /NP /XF install-state.json | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with code $LASTEXITCODE" }
Set-AdminOnlyTree $installDir                       # every copied file and folder: owner Administrators, inheriting SYSTEM + Administrators full, Users read and execute
if (-not (Test-AdminOnlyTree $installDir)) { throw "What was copied to $installDir is not administrators-only (see the warnings above); the logon task was not changed." }
# files from a downloaded zip carry the "downloaded from the internet" mark; the copy in Program Files should not
Get-ChildItem $installDir -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue

# --- .NET 10 Desktop Runtime (the app is built against it, not bundled) ------------------------------
$hasRuntime = [bool](Get-ChildItem (Join-Path $programFiles 'dotnet\shared\Microsoft.WindowsDesktop.App') -Directory -Filter '10.*' -ErrorAction SilentlyContinue)
if (-not $hasRuntime) {
    Write-Host '      The .NET 10 Desktop Runtime is not installed (Quick Sweep needs it).'
    $go = $Yes
    if (-not $go) {
        $a = Read-Host '      Install it now from Microsoft through winget (Microsoft.DotNet.DesktopRuntime.10)? [Y/n]'
        $go = ($a -eq '' -or $a -match '^(y|yes)$')
    }
    $winget = Get-WingetPath $programFiles
    if ($go -and $winget) {
        & $winget install --id Microsoft.DotNet.DesktopRuntime.10 -e --silent --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -ne 0) { Write-Warning "      The runtime install did not complete (winget exit code $LASTEXITCODE). Install it from https://dotnet.microsoft.com/download/dotnet/10.0 and run this script again." }
    } else {
        Write-Warning '      Install the .NET 10 Desktop Runtime from https://dotnet.microsoft.com/download/dotnet/10.0 (x64), then start Quick Sweep from the Start menu.'
    }
}

# --- state folder ----------------------------------------------------------------
# The elevated app keeps its settings, sessions, logs and the gaming flag in %ProgramData%\QuickSweep. Only SYSTEM and
# Administrators may write there; every user may read (gaming.json is for other programs to check). The app verifies
# this at every start and refuses the folder if it is wrong. Nothing a user-level process can write is ever trusted.
Write-Host "      State folder $dataDir (SYSTEM + Administrators full, users read-only)..."
if (Test-Path $dataDir) {
    $item = Get-Item $dataDir -Force
    $ownerSid = (Get-Acl $dataDir).GetOwner([System.Security.Principal.SecurityIdentifier]).Value
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$dataDir is a junction or symbolic link. Remove it first; Quick Sweep will not use it." }
    if ($adminSids -notcontains $ownerSid) {
        $aside = "$dataDir.untrusted-$(Get-Date -Format yyyyMMddHHmmss)"
        Rename-Item $dataDir $aside
        Write-Warning "      $dataDir belonged to $ownerSid, not to Administrators. It was renamed to $aside; a fresh folder is used."
    }
}
New-Item -ItemType Directory -Force $dataDir | Out-Null
Set-AdminOnlyAcl $dataDir
if (Get-ChildItem $dataDir -Force -ErrorAction SilentlyContinue) { Set-AdminOnlyTree $dataDir }
New-Item -ItemType Directory -Force (Join-Path $dataDir 'public') | Out-Null
if (-not ((Test-TrustedStateFolder $dataDir) -and (Test-AdminOnlyTree $dataDir))) {
    # something inside belongs to a non-administrator, or is a link: the app would refuse this folder at every start. Keep it (nothing is
    # deleted), set it aside, and start with a fresh folder.
    $aside = "$dataDir.untrusted-$(Get-Date -Format yyyyMMddHHmmss)"
    Rename-Item $dataDir $aside
    Write-Warning "      $dataDir contained something that is not administrators-only (see the warnings above). It was renamed to $aside; a fresh folder is used."
    New-Item -ItemType Directory -Force $dataDir | Out-Null
    Set-AdminOnlyAcl $dataDir
    New-Item -ItemType Directory -Force (Join-Path $dataDir 'public') | Out-Null
    if (-not ((Test-TrustedStateFolder $dataDir) -and (Test-AdminOnlyTree $dataDir))) { throw "$dataDir is not administrators-only even when new; Quick Sweep will not use it." }
}

# --- settings from the earlier name: only from a folder that passes the same trust test, copies only, never over what exists ---------
$oldFound = (Get-ScheduledTask -TaskName $oldTask -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $oldInstall) -or (Test-Path -LiteralPath $oldData) -or (Test-Path -LiteralPath $oldLnk)
$oldDataTrusted = $false
if ($oldFound) {
    Write-Host '      Found the earlier install of this program (PC Care): moving its settings.'
    if (Test-TrustedStateFolder $oldData) {
        $oldDataTrusted = $true
        $copied = 0
        foreach ($name in 'settings.json', 'sessions.json', 'drivehealth.json', 'deletions.log', 'deletions.log.old') {
            $src = Join-Path $oldData $name; $dst = Join-Path $dataDir $name
            if (-not (Test-Path -LiteralPath $src -PathType Leaf) -or (Test-Path -LiteralPath $dst)) { continue }
            $fi = Get-Item -LiteralPath $src -Force
            if ($fi.Attributes -band [IO.FileAttributes]::ReparsePoint) { Write-Warning "      $name is a link: skipped"; continue }
            if ($fi.Length -gt 8MB) { Write-Warning "      $name is larger than 8 MB: skipped"; continue }
            if ($name.EndsWith('.json')) { try { Get-Content -LiteralPath $src -Raw | ConvertFrom-Json | Out-Null } catch { Write-Warning "      $name is not valid JSON: skipped"; continue } }
            Copy-Item -LiteralPath $src -Destination $dst
            $copied++
        }
        Set-AdminOnlyTree $dataDir                        # everything in the new folder: owner Administrators, inheriting SYSTEM + Administrators full, users read-only
        if (-not (Test-AdminOnlyTree $dataDir)) { throw "The files copied into $dataDir are not administrators-only (see the warnings above)." }
        Write-Host "      copied $copied file(s) into $dataDir"
    } elseif (Test-Path -LiteralPath $oldData) {
        Write-Warning "      $oldData did not pass the trust test, so nothing was taken from it and it is left where it is."
    }
}

# --- sensor driver -----------------------------------------------------------
$weInstalledDriver = [bool]($old -and $old.InstalledPawnIO)
Write-Host '[3/5] Sensor driver (PawnIO)...'
$pawn = Get-Service -Name PawnIO -ErrorAction SilentlyContinue
if ($SkipSensorDriver) {
    Write-Host '      skipped (-SkipSensorDriver): CPU temperature will show as unavailable.'
} elseif ($pawn) {
    Write-Host '      already installed.'
} else {
    $go = $Yes
    if (-not $go) {
        $a = Read-Host '      Quick Sweep reads CPU temperature through the signed PawnIO driver (winget: namazso.PawnIO). Install it now? [Y/n]'
        $go = ($a -eq '' -or $a -match '^(y|yes)$')
    }
    if ($go) {
        $winget = Get-WingetPath $programFiles
        if (-not $winget) {
            Write-Warning '      winget was not found in its package folder. Install PawnIO manually from https://pawnio.eu then re-run, or use -SkipSensorDriver.'
        } else {
            & $winget install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements
            if ($LASTEXITCODE -eq 0 -and (Get-Service -Name PawnIO -ErrorAction SilentlyContinue)) {
                $weInstalledDriver = $true
                Write-Host '      installed.'
            } else {
                Write-Warning "      PawnIO install did not complete (winget exit code $LASTEXITCODE). A reboot may be needed; CPU temperature stays unavailable until it is installed."
            }
        }
    } else {
        Write-Host '      skipped by choice: CPU temperature will show as unavailable.'
    }
}
@{ InstalledPawnIO = $weInstalledDriver; InstalledAt = (Get-Date).ToString('s'); User = $user } | ConvertTo-Json | Set-Content $state -Encoding ASCII

# --- optional: merge a settings file (same code as Settings > Import), before the task starts ------------------
if ($ImportSettings) {
    Write-Host "      Merging settings from $ImportSettings ..."
    if (-not (Get-ChildItem (Join-Path $programFiles 'dotnet\shared\Microsoft.WindowsDesktop.App') -Directory -Filter '10.*' -ErrorAction SilentlyContinue)) {
        Write-Warning '      The .NET 10 Desktop Runtime is missing, so Quick Sweep cannot run the import. Install it, then import the file from Settings > Import settings.'
        $imp = $null
    } else {
        $imp = Start-Process -FilePath $exe -ArgumentList @('--import-settings', ('"{0}"' -f $ImportSettings)) -WorkingDirectory $installDir -WindowStyle Hidden -PassThru
        $null = $imp.Handle    # keeps the exit code readable after the process ends (Windows PowerShell 5.1)
    }
    if (-not $imp) { }
    elseif (-not $imp.WaitForExit(30000)) {
        try { $imp.Kill() } catch { }
        Write-Warning '      The settings import did not finish within 30 seconds and was stopped; settings were not changed.'
    } elseif ($imp.ExitCode -eq 0) {
        $line = Get-Content (Join-Path $dataDir 'quicksweep.log') -Tail 5 -ErrorAction SilentlyContinue | Where-Object { $_ -match 'settings import' } | Select-Object -Last 1
        Write-Host "      $line"
    } else {
        Write-Warning "      The settings import failed (exit code $($imp.ExitCode): 2 file unreadable, 3 not a settings file, 4 could not read or save settings.json). Settings were NOT changed; the reason is in $dataDir\quicksweep.log."
    }
}

# --- scheduled task ------------------------------------------------------------
Write-Host '[4/5] Registering the logon task (hidden, highest privileges)...'
if (-not ((Test-TrustedStateFolder $installDir) -and (Test-AdminOnlyTree $installDir))) { throw "$installDir is not administrators-only (see the warnings above); the logon task was not registered." }
$action    = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $installDir
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -Hidden -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
             -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
    -Description 'Quick Sweep tray utility (temperatures, Game Mode, disk and memory care). Runs elevated for sensors.' -Force | Out-Null

# what the elevated task will run must be this program, below the real Program Files (read back, not assumed)
$registered = (Get-ScheduledTask -TaskName $taskName).Actions | Select-Object -First 1
if (-not $registered -or $registered.Execute -ne $exe -or -not (Test-UnderDirectory $registered.Execute ([Environment]::GetFolderPath('ProgramFiles')))) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    throw "The logon task does not point at $exe below Program Files (it says '$($registered.Execute)'); it was removed. Nothing runs elevated at logon."
}

# --- shortcut --------------------------------------------------------------------
Write-Host '[5/5] Start-menu shortcut...'
$sh = New-Object -ComObject WScript.Shell
$sc = $sh.CreateShortcut($lnk)
$sc.TargetPath = $exe
$sc.Arguments = '--open'
$sc.WorkingDirectory = $installDir
$sc.IconLocation = "$exe,0"
$sc.Description = 'Quick Sweep'
$sc.Save()

# --- remove the earlier install, now that the new one is complete ----------------------------------------------
if ($oldFound) {
    Write-Host 'Removing the earlier install (PC Care)...'
    if (Get-ScheduledTask -TaskName $oldTask -ErrorAction SilentlyContinue) { Unregister-ScheduledTask -TaskName $oldTask -Confirm:$false; Write-Host '      old logon task removed' }
    if (Test-Path -LiteralPath $oldLnk) { Remove-Item -LiteralPath $oldLnk -Force; Write-Host '      old Start-menu shortcut removed' }
    if (Test-Path -LiteralPath $oldInstall) { Remove-AdminTree $oldInstall; Write-Host '      old program files removed' }
    if ($oldDataTrusted -and -not $KeepOldData) { Remove-AdminTree $oldData; Write-Host '      old state folder removed (its contents were copied)' }
    elseif (Test-Path -LiteralPath $oldData) { Write-Host "      old state folder kept: $oldData" }
    # the taskbar's "show this icon" switch is stored per program path: drop the entry of the old program so the new icon is promoted once
    try {
        $nis = 'HKCU:\Control Panel\NotifyIconSettings'
        if (Test-Path $nis) {
            foreach ($k in Get-ChildItem $nis -ErrorAction SilentlyContinue) {
                $p = (Get-ItemProperty -LiteralPath $k.PSPath -ErrorAction SilentlyContinue).ExecutablePath
                if ($p -and ([IO.Path]::GetFileName($p) -ieq 'PcCare.exe')) { Remove-Item -LiteralPath $k.PSPath -Recurse -Force -ErrorAction SilentlyContinue }
            }
        }
    } catch { }
    if ((Test-Path -LiteralPath $oldUserDir) -and -not $KeepOldData) {
        Remove-UserFolderKnownFiles $oldUserDir
        if (Test-Path -LiteralPath $oldUserDir) { Write-Host "      $oldUserDir still holds files this script did not create; delete it yourself when you like." }
    }
}

if (-not $NoStart) { Start-ScheduledTask -TaskName $taskName; Write-Host 'Quick Sweep is starting in the tray.' }
Write-Host ''
Write-Host "Installed. It starts at every logon. Open it from the tray icon or the Start menu ('Quick Sweep')."
try { Stop-Transcript | Out-Null } catch { }

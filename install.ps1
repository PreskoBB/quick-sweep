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
 folder are removed. The PawnIO driver is kept. -KeepOldData leaves the old state folder in place. The earlier per-user folder (%LOCALAPPDATA%\PcCare) is never touched.

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

function Test-Exists($p) { [bool](Get-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue) }      # also true for a link whose target is gone

# ---- the permissions this installer writes: ONE complete security descriptor, never an edit of what was there ----------------------------
# An incremental edit cannot be trusted: "icacls /grant:r" replaces only the accounts it names and leaves every other explicit entry where it
# is (for example the creator's own FullControl entry, which a program running as that user could then use to rewrite the folder). So a
# complete descriptor is written instead: owner Administrators and a PROTECTED DACL (nothing inherited) holding exactly SYSTEM full control,
# Administrators full control and Users read and execute. For a folder the entries are passed on to what is created inside it ((OI)(CI));
# a file gets the plain equivalent. Only the owner and the DACL are written (the primary group is left alone: a normal account may not set it).
function Get-AdminOnlySddl([bool]$Container) {
    if ($Container) { 'O:BAD:PAI(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)' }
    else            { 'O:BAD:PAI(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;BU)' }
}

# A security object that carries only the DACL part of that descriptor (so only the DACL is written when it is applied).
function New-AdminOnlySecurity([bool]$Container) {
    $sec = if ($Container) { New-Object Security.AccessControl.DirectorySecurity } else { New-Object Security.AccessControl.FileSecurity }
    $sec.SetSecurityDescriptorSddlForm((Get-AdminOnlySddl $Container), [Security.AccessControl.AccessControlSections]::Access)
    return $sec
}

# "owner <sid>; <SDDL>" of an item, for error messages.
function Get-OwnerAndSddl([string]$Path) {
    try {
        $s = (Get-Item -LiteralPath $Path -Force -ErrorAction Stop).GetAccessControl()
        return "owner $($s.GetOwner([Security.Principal.SecurityIdentifier]).Value); $($s.GetSecurityDescriptorSddlForm('All'))"
    } catch { return "unreadable ($($_.Exception.Message))" }
}

# What differs between an item and that descriptor, as text ('' when nothing does): the owner, whether the DACL is protected, and every entry
# (account, rights, inheritance), compared structurally, so it does not depend on how the SDDL text happens to be spelled.
function Get-AdminOnlyDifference([string]$Path, [bool]$Container, [string]$OwnerSid = 'S-1-5-32-544') {
    $sec = if ($Container) { [IO.Directory]::GetAccessControl($Path) } else { [IO.File]::GetAccessControl($Path) }
    $diff = @()
    $owner = $sec.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($owner -ne $OwnerSid) { $diff += "owner is $owner, not $OwnerSid" }
    if (-not $sec.AreAccessRulesProtected) { $diff += 'the permissions are inherited from the parent folder' }
    $inh = if ($Container) { 'ContainerInherit, ObjectInherit' } else { 'None' }
    $want = @("S-1-5-18;2032127;$inh;None;Allow", "S-1-5-32-544;2032127;$inh;None;Allow", "S-1-5-32-545;1179817;$inh;None;Allow")
    $have = @($sec.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]) | ForEach-Object {
        "$($_.IdentityReference.Value);$([int]$_.FileSystemRights);$($_.InheritanceFlags);$($_.PropagationFlags);$($_.AccessControlType)" })
    if ($have.Count -ne $want.Count) { $diff += "$($have.Count) permission entries instead of $($want.Count)" }
    foreach ($h in $have) { if ($want -notcontains $h) { $diff += "unexpected entry $h" } }
    foreach ($w in $want) { if ($have -notcontains $w) { $diff += "missing entry $w" } }
    return ($diff -join '; ')
}

# Writes the descriptor to ONE existing file or folder and reads it back; throws when anything differs. Refuses a link (the permission call
# would follow it and change the target). The DACL goes first: it hands Administrators full control, which is what the owner change needs.
# The owner is only written when it is not already the wanted one.
function Set-AdminOnlySecurity([string]$Path, [bool]$Container, [string]$OwnerSid = 'S-1-5-32-544') {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$Path is a junction or symbolic link: its permissions are not changed." }
    if ([bool]$item.PSIsContainer -ne $Container) { throw "$Path is not a $(if ($Container) { 'folder' } else { 'file' })." }
    try {
        $item.SetAccessControl((New-AdminOnlySecurity $Container))
        if ($item.GetAccessControl().GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $OwnerSid) {
            $own = if ($Container) { New-Object Security.AccessControl.DirectorySecurity } else { New-Object Security.AccessControl.FileSecurity }
            $own.SetOwner([Security.Principal.SecurityIdentifier]$OwnerSid)
            try { $item.SetAccessControl($own) }
            catch {
                # Not every token may name every account as owner without the restore privilege; icacls enables the privileges it needs. One item
                # only (/L: a link itself, never its target; no /T), exit code checked, and the read-back below decides.
                & (Join-Path ([Environment]::SystemDirectory) 'icacls.exe') $Path /setowner "*$OwnerSid" /L | Out-Null
                if ($LASTEXITCODE -ne 0) { throw $_.Exception }
            }
        }
    } catch {
        $why = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
        throw "Could not set the permissions of ${Path}: $why"
    }
    if (Test-Link $Path) { throw "$Path became a junction or symbolic link while its permissions were set." }
    $diff = Get-AdminOnlyDifference $Path $Container $OwnerSid
    if ($diff) { throw "The permissions of $Path are not what was written ($diff). Now: $(Get-OwnerAndSddl $Path)" }
}

# CreateDirectoryW, called without Add-Type: Add-Type compiles into the TEMP folder of the user, where a normal program of the same user could swap
# the result before this elevated process loads it. Small dynamic types are built in memory instead (no file is written anywhere): two lookups
# (GetModuleHandleW / GetProcAddress, which need no error code) find kernel32!CreateDirectoryW, and a delegate type marked SetLastError calls it.
# (A SetLastError flag cannot be put on a dynamic P/Invoke method, so the error code would be lost; on a delegate it is an ordinary attribute.)
# The error code is what tells "it was already there" (ERROR_ALREADY_EXISTS, 183) from every other failure.
function Get-CreateDirectoryApi {
    if ($script:CreateDirectoryApi) { return $script:CreateDirectoryApi }
    $asm = [AppDomain]::CurrentDomain.DefineDynamicAssembly((New-Object Reflection.AssemblyName 'QuickSweepNative'), [Reflection.Emit.AssemblyBuilderAccess]::Run)
    $mod = $asm.DefineDynamicModule('QuickSweepNative', $false)
    $lt = $mod.DefineType('Lookup', 'Public, Class')
    $a = $lt.DefinePInvokeMethod('GetModuleHandleW', 'kernel32.dll', 'Public, Static, PinvokeImpl', [Reflection.CallingConventions]::Standard,
        [IntPtr], [Type[]]@([string]), [Runtime.InteropServices.CallingConvention]::Winapi, [Runtime.InteropServices.CharSet]::Unicode)
    $a.SetImplementationFlags([Reflection.MethodImplAttributes]::PreserveSig)
    $b = $lt.DefinePInvokeMethod('GetProcAddress', 'kernel32.dll', 'Public, Static, PinvokeImpl', [Reflection.CallingConventions]::Standard,
        [IntPtr], [Type[]]@([IntPtr], [string]), [Runtime.InteropServices.CallingConvention]::Winapi, [Runtime.InteropServices.CharSet]::Ansi)
    $b.SetImplementationFlags([Reflection.MethodImplAttributes]::PreserveSig)
    $lookup = $lt.CreateType()
    $dt = $mod.DefineType('CreateDirectoryW', 'Class, Public, Sealed, AnsiClass, AutoClass', [MulticastDelegate])
    $ufp = [Runtime.InteropServices.UnmanagedFunctionPointerAttribute]
    $dt.SetCustomAttribute((New-Object Reflection.Emit.CustomAttributeBuilder($ufp.GetConstructor([Type[]]@([Runtime.InteropServices.CallingConvention])),
        [object[]]@([Runtime.InteropServices.CallingConvention]::Winapi),
        [Reflection.FieldInfo[]]@($ufp.GetField('SetLastError'), $ufp.GetField('CharSet')), [object[]]@($true, [Runtime.InteropServices.CharSet]::Unicode))))
    $ctor = $dt.DefineConstructor('RTSpecialName, HideBySig, Public', [Reflection.CallingConventions]::Standard, [Type[]]@([object], [IntPtr]))
    $ctor.SetImplementationFlags('Runtime, Managed')
    $inv = $dt.DefineMethod('Invoke', 'Public, HideBySig, NewSlot, Virtual', [bool], [Type[]]@([string], [IntPtr]))
    $inv.SetImplementationFlags('Runtime, Managed')
    $delegateType = $dt.CreateType()
    $module = $lookup::GetModuleHandleW('kernel32.dll')                  # kernel32 is loaded in every process: nothing is searched for or loaded
    $proc = $lookup::GetProcAddress($module, 'CreateDirectoryW')
    if ($module -eq [IntPtr]::Zero -or $proc -eq [IntPtr]::Zero) { throw 'kernel32!CreateDirectoryW was not found.' }
    $script:CreateDirectoryApi = [Runtime.InteropServices.Marshal]::GetDelegateForFunctionPointer($proc, $delegateType)
    return $script:CreateDirectoryApi
}

# Creates ONE folder with the protected DACL of the descriptor as part of the creation (SECURITY_ATTRIBUTES), so there is no moment at which it
# inherits anything. Returns $true when this call created it, $false when it was already there (ERROR_ALREADY_EXISTS: nothing was changed on it);
# any other failure throws. The parent must exist.
function New-FolderWithDescriptor([string]$Path) {
    $api = Get-CreateDirectoryApi
    $bytes = (New-AdminOnlySecurity $true).GetSecurityDescriptorBinaryForm()
    $m = [Runtime.InteropServices.Marshal]
    $sd = $m::AllocHGlobal($bytes.Length)
    $sa = $m::AllocHGlobal(24)
    try {
        $m::Copy($bytes, 0, $sd, $bytes.Length)
        for ($i = 0; $i -lt 24; $i += 4) { $m::WriteInt32($sa, $i, 0) }
        # SECURITY_ATTRIBUTES { DWORD nLength; LPVOID lpSecurityDescriptor; BOOL bInheritHandle } (the pointer sits after the DWORD, padded on 64-bit)
        $m::WriteInt32($sa, 0, $(if ([IntPtr]::Size -eq 8) { 24 } else { 12 }))
        $m::WriteIntPtr($sa, [IntPtr]::Size, $sd)
        $ok = $api.Invoke($Path, $sa); $err = $m::GetLastWin32Error()
    } finally { $m::FreeHGlobal($sa); $m::FreeHGlobal($sd) }
    if ($ok) { return $true }
    if ($err -eq 183) { return $false }
    throw "Could not create ${Path}: $((New-Object ComponentModel.Win32Exception $err).Message) (error $err)"
}

# A folder that is administrators-only from its first moment. Returns $true when it was created here and then completed (owner) and read back
# in full, owner included; $false when it already existed (it is then NOT adopted: the caller sets it aside).
function New-AdminOnlyFolder([string]$Path, [string]$OwnerSid = 'S-1-5-32-544') {
    if (-not (New-FolderWithDescriptor $Path)) { return $false }
    Set-AdminOnlySecurity $Path $true $OwnerSid
    return $true
}

# The top folder of a tree: the complete descriptor. Its contents are not touched (Set-AdminOnlyTree does those).
function Set-AdminOnlyAcl([string]$dir, [string]$OwnerSid = 'S-1-5-32-544') { Set-AdminOnlySecurity $dir $true $OwnerSid }

# Every file and folder below $dir, parents before children, WITHOUT following links: a junction or symbolic link anywhere throws (naming it)
# before the caller has changed anything. Items are .NET FileSystemInfo objects.
function Get-TreeItems([string]$dir) {
    $out = New-Object System.Collections.ArrayList
    $stack = New-Object System.Collections.Stack
    $stack.Push($dir)
    while ($stack.Count -gt 0) {
        $d = $stack.Pop()
        foreach ($e in (New-Object IO.DirectoryInfo $d).GetFileSystemInfos()) {
            if ($e.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "$($e.FullName) is a junction or symbolic link." }
            [void]$out.Add($e)
            if ($e -is [IO.DirectoryInfo]) { $stack.Push($e.FullName) }
        }
    }
    return $out.ToArray()
}

# Everything below a folder that THIS INSTALLER created or copied: the complete descriptor on every item, one by one in PowerShell (no recursive
# icacls, which follows junctions as an administrator). The owner is written explicitly because the default owner of what an elevated
# administrator creates can be that person's own account (a policy decides), and that account is also the one a normal program of the same
# user runs as: an owner may always rewrite the permissions of what it owns, so a file owned by the user's own account is not
# administrators-only no matter what its permission list says. Never call this on content that was already there before the install.
function Set-AdminOnlyTree([string]$dir, [string]$OwnerSid = 'S-1-5-32-544') {
    $items = @(Get-TreeItems $dir)
    foreach ($e in $items) { Set-AdminOnlySecurity $e.FullName ([bool]($e -is [IO.DirectoryInfo])) $OwnerSid }
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

# Renames a folder (or link, or file) out of the way under a unique name; nothing in it is read, changed or deleted. Returns the new name.
function Move-FolderAside([string]$dir) {
    $stamp = Get-Date -Format yyyyMMddHHmmss
    $aside = "$dir.untrusted-$stamp"
    for ($n = 2; Test-Exists $aside; $n++) { $aside = "$dir.untrusted-$stamp-$n" }
    try { Rename-Item -LiteralPath $dir -NewName (Split-Path $aside -Leaf) -ErrorAction Stop }
    catch { throw "$dir cannot be trusted (see the warnings above) and could not be renamed out of the way: $($_.Exception.Message) Rename it yourself, as an administrator, and run the installer again." }
    if (Test-Exists $dir) { throw "$dir is still there after it was renamed to $aside." }
    return $aside
}

# What may already be at $dir. A normal user can create C:\ProgramData\QuickSweep (and fill it) before the installer ever runs, so whatever is
# there is judged FIRST, with the checks the app applies to its own state folder (owner, permissions, no link anywhere inside, every item
# administrators-only) and is never re-owned, reset or deleted to make it pass. When it fails, the whole folder is renamed out of the way.
# Returns $null when there was nothing or it passed, otherwise the new name.
function Move-UntrustedFolderAside([string]$dir) {
    $it = Get-Item -LiteralPath $dir -Force -ErrorAction SilentlyContinue
    if (-not $it) { return $null }
    if ($it.Attributes -band [IO.FileAttributes]::ReparsePoint) { Write-Warning "      $dir is a junction or symbolic link" }
    elseif (-not $it.PSIsContainer) { Write-Warning "      $dir is a file, not a folder" }
    else {
        $ok = $false
        try { $ok = [bool]((Test-TrustedStateFolder $dir) -and (Test-AdminOnlyTree $dir)) } catch { Write-Warning "      could not check $dir : $($_.Exception.Message)" }
        if ($ok) { return $null }
    }
    $aside = Move-FolderAside $dir
    Write-Warning "      $dir was not administrators-only (see above). It was renamed to $aside (nothing in it was changed or deleted); a fresh folder is used."
    return $aside
}

# The state folder: what was there is judged first (above); a folder that passed keeps its contents as they are and only gets its own
# descriptor renewed; otherwise it is created new, administrators-only from its first moment. If somebody creates the folder between the
# check and the creation (ERROR_ALREADY_EXISTS), that folder is by definition not the installer's and not checked: it is set aside like any
# untrusted one and the creation is tried once more. Ends with the full check of the whole tree.
# Returns the name an untrusted folder was moved to, or $null.
function Initialize-StateFolder([string]$dir, [string]$OwnerSid = 'S-1-5-32-544', [string[]]$SubFolders = @('public')) {
    $aside = Move-UntrustedFolderAside $dir
    if (Test-Exists $dir) { Set-AdminOnlyAcl $dir $OwnerSid }
    elseif (-not (New-AdminOnlyFolder $dir $OwnerSid)) {
        $aside = Move-FolderAside $dir
        Write-Warning "      $dir appeared while the installer was running. It was renamed to $aside (nothing in it was read, changed or deleted); a fresh folder is used."
        if (-not (New-AdminOnlyFolder $dir $OwnerSid)) { throw "$dir was created by somebody else again while the installer was running. Run the installer again." }
    }
    foreach ($s in $SubFolders) {
        $sub = Join-Path $dir $s
        if (-not (Test-Exists $sub) -and -not (New-AdminOnlyFolder $sub $OwnerSid)) { throw "$sub appeared inside the new administrators-only folder; run the installer again." }
    }
    if (-not ((Test-TrustedStateFolder $dir) -and (Test-AdminOnlyTree $dir))) { throw "$dir is not administrators-only even though it was just checked or created (see the warnings above); Quick Sweep will not use it." }
    return $aside
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
Set-AdminOnlyAcl $installDir                       # the complete descriptor, written before anything is copied in (what arrives inherits it)
robocopy $Source $installDir /MIR /NFL /NDL /NJH /NJS /NP /XF install-state.json | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with code $LASTEXITCODE" }
Set-AdminOnlyTree $installDir                       # every copied file and folder gets the complete descriptor, one by one (a link anywhere below stops the install)
if (-not (Test-AdminOnlyTree $installDir)) { throw "What was copied to $installDir is not administrators-only (see the warnings above); the logon task was not changed." }
# files from a downloaded zip carry the "downloaded from the internet" mark; the copy in Program Files should not
foreach ($e in @(Get-TreeItems $installDir)) { if ($e -is [IO.FileInfo]) { Unblock-File -LiteralPath $e.FullName -ErrorAction SilentlyContinue } }

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
# Any user can create C:\ProgramData\QuickSweep and fill it before this installer runs, so what is already there is checked FIRST and is never
# re-owned, reset or deleted to make it pass: a folder that fails (not owned by Administrators/SYSTEM, a link anywhere, a non-administrator
# who can write) is renamed aside whole and a new one is created. Only what this installer creates or copies itself gets its permissions set.
$null = Initialize-StateFolder $dataDir

# --- settings from the earlier name: only from a folder that passes the same trust test, copies only, never over what exists ---------
$oldFound = (Get-ScheduledTask -TaskName $oldTask -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $oldInstall) -or (Test-Path -LiteralPath $oldData) -or (Test-Path -LiteralPath $oldLnk)
$oldDataTrusted = $false
if ($oldFound) {
    Write-Host '      Found the earlier install of this program (PC Care): moving its settings.'
    if ((Test-TrustedStateFolder $oldData) -and (Test-AdminOnlyTree $oldData)) {
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
            Set-AdminOnlySecurity $dst $false         # the copy is this installer's own: the complete file descriptor (nothing else in the folder is touched)
            $copied++
        }
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
Set-AdminOnlySecurity $state $false                # written after the tree was protected: it gets the file descriptor too (the check before the task is registered looks at it)

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
    # the earlier per-user folder is writable by a normal program: an administrator does not delete inside a folder like that (a link could be put in
    # its place while this runs), so it is left for the owner to delete
    if (Test-Exists $oldUserDir) { Write-Host "      The earlier per-user folder $oldUserDir was left in place (nothing is deleted as an administrator inside a folder a normal program can write to); delete it yourself when you like." }
}

if (-not $NoStart) { Start-ScheduledTask -TaskName $taskName; Write-Host 'Quick Sweep is starting in the tray.' }
Write-Host ''
Write-Host "Installed. It starts at every logon. Open it from the tray icon or the Start menu ('Quick Sweep')."
try { Stop-Transcript | Out-Null } catch { }

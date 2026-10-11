<#
 End-to-end check of game detection and the load-triggered Game Mode, with stand-ins:
   FakeGame   a 200 MB off-screen window that counts as a game (it lives in a folder added to ExtraGameFolders)
   ffmpeg     a CPU burner named ffmpeg.exe: background work Game Mode may slow down
   loadgen    the same burner under another name: load that is NOT background work, so it must be left alone
 It runs its own isolated --dev instance of Quick Sweep with a scratch data folder (never the installed one), with short
 timings and a low CPU limit so about half the CPU is enough to trigger. No sounds, no visible windows.
 Build the stand-ins first:  dotnet build tests/Burner -p:AssemblyName=ffmpeg  (and =loadgen), tests/FakeGame
 Usage: .\e2e-game.ps1 [-Exe <QuickSweep.exe>] [-Data <scratch folder>] [-Root <stand-in folder>]
#>
param(
    [string]$Exe = (Join-Path (Split-Path $PSScriptRoot) 'src\QuickSweep\bin\Release\net10.0-windows\QuickSweep.exe'),
    [string]$Data = (Join-Path $env:TEMP 'QuickSweep-e2e\data'),
    [string]$Root = (Join-Path $env:TEMP 'QuickSweep-e2e\testgame'),
    [switch]$VerifyOnly,   # only evaluate the log and sessions left by an earlier run
    [switch]$IUnderstandThisSlowsRealBackgroundWork
)
$ErrorActionPreference = 'Stop'
if (-not $VerifyOnly -and -not $IUnderstandThisSlowsRealBackgroundWork) {
    Write-Host 'REFUSED: while the stand-in game runs, Game Mode lowers EVERY real background job on this PC (ffmpeg, node, chrome, python, AI-agent children), not only the stand-ins. Do not run this on a machine somebody is working on. Pass -IUnderstandThisSlowsRealBackgroundWork to run it anyway.'
    exit 2
}
$game = Join-Path $Root 'FakeGame\FakeGame.exe'
$ffmpeg = Join-Path $Root 'bin\ffmpeg\ffmpeg.exe'
$loadgen = Join-Path $Root 'bin\loadgen\loadgen.exe'
foreach ($f in $Exe, $game, $ffmpeg, $loadgen) { if (-not (Test-Path $f)) { throw "missing $f" } }
New-Item -ItemType Directory -Force $Data | Out-Null
$fail = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fail++ } }
function Prio($p) { try { $p.Refresh(); if ($p.HasExited) { 'exited' } else { [string]$p.PriorityClass } } catch { 'unknown' } }

$flag = Join-Path $Data 'public\gaming.json'
$log = Join-Path $Data 'quicksweep.log'
if (-not $VerifyOnly) {
# scratch settings: short timings, low CPU limit, the stand-in folder counts as a game folder
@{
    GameModeAuto = $true; NotifyGameMode = $false; NotifyAfterGame = $false; AlertsEnabled = $false
    GameCpuPct = 40; GameGpuPct = 99; GameMinFreeRamGb = 0.5; GameCommitPct = 99; GameReadyPct = 0
    GameTriggerSeconds = 8; GameReleaseSeconds = 25; GameCooldownSeconds = 15; GameMinContributionPct = 5
    ExtraGameFolders = @($Root)
} | ConvertTo-Json | Set-Content (Join-Path $Data 'settings.json') -Encoding UTF8
foreach ($f in 'quicksweep.log', 'sessions.json', 'public\gaming.json', 'gamemode-state.json') { try { [System.IO.File]::Delete((Join-Path $Data $f)) } catch { } }

$inst = Start-Process $Exe -ArgumentList '--dev', "--data=$Data" -PassThru
Start-Sleep -Seconds 7
try {
    "=== A: a render (ffmpeg) runs while the game loads the PC -> Game Mode slows it, restores it when the game closes"
    $burner = Start-Process $ffmpeg -ArgumentList 100, 6 -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 4
    Check 'before the game: ffmpeg is at normal priority, no gaming.json' ((Prio $burner) -eq 'Normal' -and -not (Test-Path $flag))
    $g = Start-Process $game -ArgumentList 75 -PassThru
    $sawFlag = $false; $sawIdle = $false; $flagText = ''
    $until = (Get-Date).AddSeconds(72)
    while ((Get-Date) -lt $until) {
        Start-Sleep -Seconds 3
        if (Test-Path $flag) { $sawFlag = $true; if (-not $flagText) { $flagText = Get-Content $flag -Raw } }
        if ((Prio $burner) -eq 'Idle') { $sawIdle = $true }
    }
    Check 'gaming.json exists while the game runs' $sawFlag
    if ($flagText) { $j = $flagText | ConvertFrom-Json; Check "gaming.json content (game=$($j.game), exe=$($j.exe), since=$($j.since))" ($j.exe -eq 'FakeGame.exe' -and $j.game -and $j.since) }
    Check 'ffmpeg was slowed to Idle during the game' $sawIdle
    $g.WaitForExit(30000) | Out-Null
    Start-Sleep -Seconds 6
    Check 'after the game: ffmpeg is back at normal priority' ((Prio $burner) -in 'Normal', 'exited')
    Check 'after the game: gaming.json is gone' (-not (Test-Path $flag))
    if (-not $burner.HasExited) { Stop-Process -Id $burner.Id -Force }

    "=== B: heavy load that is not background work (started through WMI, so it is not below any Claude process) -> nothing may be touched"
    $r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = "`"$loadgen`" 60 6" }
    $lg = Get-Process -Id $r.ProcessId
    $g2 = Start-Process $game -ArgumentList 36 -PassThru
    Start-Sleep -Seconds 30
    Check 'loadgen (not background work) stays at normal priority' ((Prio $lg) -eq 'Normal')
    $g2.WaitForExit(30000) | Out-Null
    if (-not $lg.HasExited) { Stop-Process -Id $lg.Id -Force }
    Start-Sleep -Seconds 4

    # is this script running below an AI-agent process (claude, the default agent entry)?
    $underClaude = $false; $cur = $PID
    for ($i = 0; $i -lt 12 -and $cur; $i++) {
        $pr = Get-CimInstance Win32_Process -Filter "ProcessId=$cur" -ErrorAction SilentlyContinue
        if (-not $pr) { break }
        if ($pr.Name -like 'claude*') { $underClaude = $true; break }
        $cur = $pr.ParentProcessId
    }
    "=== C: the same load started as a child of an agent app counts as agent work (this script runs under Claude: $underClaude)"
    $lg2 = Start-Process $loadgen -ArgumentList 60, 6 -PassThru -WindowStyle Hidden
    $g3 = Start-Process $game -ArgumentList 36 -PassThru
    $sawIdle2 = $false
    for ($i = 0; $i -lt 10; $i++) { Start-Sleep -Seconds 3; if ((Prio $lg2) -eq 'Idle') { $sawIdle2 = $true } }
    if ($underClaude) { Check 'an agent child process is slowed' $sawIdle2 }
    else { Check 'not below an agent here: the process is left alone' (-not $sawIdle2) }
    $g3.WaitForExit(30000) | Out-Null
    if (-not $lg2.HasExited) { Stop-Process -Id $lg2.Id -Force }
    Start-Sleep -Seconds 4
}
finally {
    if (-not $inst.HasExited) { Stop-Process -Id $inst.Id -Force }
}
}   # end of: if (-not $VerifyOnly)

"=== what Quick Sweep logged"
Get-Content $log | Select-String 'game started|game mode|no background|restored|gaming flag' | ForEach-Object { '  ' + $_.Line.Substring(11) }
"=== session summaries"
$sf = Join-Path $Data 'sessions.json'
$s = if (Test-Path $sf) { @((Get-Content $sf -Raw | ConvertFrom-Json).Items) } else { @() }
foreach ($e in $s) { "  $($e.Game): $($e.DurationSec) s, slowed $($e.BackgroundLowered) job(s)"; $e.GameModeLog | ForEach-Object { "      $_" } }
$a = $s | Where-Object { $_.GameModeLog.Count -gt 0 } | Select-Object -First 1
Check 'session summary records "slowed ffmpeg ... CPU" and the restore' ($a -and ($a.GameModeLog -join ' ') -match 'slowed ffmpeg.*CPU' -and ($a.GameModeLog -join ' ') -match 'restored')
# Scenarios B and C last under a minute, so only A is kept as a session (shorter blips are ignored on purpose); their
# priorities were checked live above. Other agents' real background work may be slowed too on a busy PC: that is correct.
Check 'every recorded action names what was slowed and why' (@($s | ForEach-Object { $_.GameModeLog } | Where-Object { $_ -match 'slowed' -and $_ -notmatch ': (CPU|GPU|only|game threads)' }).Count -eq 0)
if ($fail -eq 0) { 'ALL CHECKS PASSED' } else { "$fail CHECK(S) FAILED"; exit 1 }

<#
 Live check of the single-instance channel and the state-folder writes against an isolated --dev instance (never the
 installed one; scratch data folder). What it proves with real processes:
   1. a request file carrying a "dump:<path>" command is ignored, consumed, and nothing is written at that path
   2. a legitimate page request still opens the window
   3. --dump=<file> works without the instance ever being given a path (it answers into its own state folder)
   4. when the state folder's public\ subfolder has been swapped for a junction, the elevated-side writes (gaming.json)
      are refused and nothing lands in the junction's target
 Needs the stand-in game from tests/FakeGame built into <Root>\FakeGame (see e2e-game.ps1). No sounds.
#>
param(
    [string]$Exe = (Join-Path (Split-Path $PSScriptRoot) 'src\QuickSweep\bin\Release\net10.0-windows\QuickSweep.exe'),
    [string]$Data = (Join-Path $env:TEMP 'QuickSweep-e2e\data'),
    [string]$Root = (Join-Path $env:TEMP 'QuickSweep-e2e\testgame')
)
$ErrorActionPreference = 'Stop'
$game = Join-Path $Root 'FakeGame\FakeGame.exe'
foreach ($f in $Exe, $game) { if (-not (Test-Path $f)) { throw "missing $f" } }
$fail = 0
function Check($name, $ok) { if ($ok) { "  PASS  $name" } else { "  FAIL  $name"; $script:fail++ } }
function DevProcs { Get-Process QuickSweep -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -eq $Exe } catch { $false } } }

New-Item -ItemType Directory -Force $Data | Out-Null
@{ GameModeAuto = $false; AlertsEnabled = $false; ExtraGameFolders = @($Root) } | ConvertTo-Json | Set-Content (Join-Path $Data 'settings.json') -Encoding UTF8
foreach ($f in 'quicksweep.log', 'open.txt', 'dump.json') { [System.IO.File]::Delete((Join-Path $Data $f)) }
$log = Join-Path $Data 'quicksweep.log'
$pub = Join-Path $Data 'public'
$target = Join-Path (Split-Path $Data) 'attack-target'
$evil = Join-Path (Split-Path $Data) 'evil-dump.txt'
[System.IO.File]::Delete($evil)
New-Item -ItemType Directory -Force $target | Out-Null
Get-ChildItem $target -Force | ForEach-Object { [System.IO.File]::Delete($_.FullName) }

$inst = Start-Process $Exe -ArgumentList '--dev', "--data=$Data" -PassThru
Start-Sleep -Seconds 7
try {
    $ev = [System.Threading.EventWaitHandle]::OpenExisting('Local\QuickSweep.Open.Dev')

    "=== 1. a request file with a dump command"
    [System.IO.File]::WriteAllText((Join-Path $Data 'open.txt'), "dump:$evil")
    [void]$ev.Set(); Start-Sleep -Seconds 2
    Check 'the file named in the command was not created' (-not (Test-Path $evil))
    Check 'the request file was consumed' (-not (Test-Path (Join-Path $Data 'open.txt')))
    Check 'the instance logged that it ignored an unknown request' ((Get-Content $log -Raw) -match 'ignored unknown request')

    "=== 2. a legitimate request"
    [System.IO.File]::WriteAllText((Join-Path $Data 'open.txt'), 'memory')
    [void]$ev.Set(); Start-Sleep -Seconds 2
    Check 'a known page key opens the main window on that page' ((Get-Content $log -Raw) -match 'main window opened \(page memory\)')

    "=== 3. --dump=<file>"
    $out = Join-Path (Split-Path $Data) 'dump-copy.json'
    [System.IO.File]::Delete($out)
    $c = Start-Process $Exe -ArgumentList '--dev', "--data=$Data", "--dump=$out" -PassThru; $c.WaitForExit(30000) | Out-Null
    Check 'the client got a copy of the instance dump' ((Test-Path $out) -and ((Get-Content $out -Raw) -match 'StateFolder'))
    Check 'the instance itself wrote only dump.json in its own state folder' (Test-Path (Join-Path $Data 'dump.json'))

    "=== 4. public\ replaced by a junction to somewhere else, then a game starts"
    if (Test-Path $pub) { cmd /c rmdir "$pub" | Out-Null }
    cmd /c mklink /J "$pub" "$target" | Out-Null
    Check 'the junction is in place' ((Get-Item $pub -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
    $g = Start-Process $game -ArgumentList 30 -PassThru
    Start-Sleep -Seconds 14
    Check 'nothing was written into the junction target' (@(Get-ChildItem $target -Force).Count -eq 0)
    Check 'the instance logged that it refused the gaming flag' ((Get-Content $log -Raw) -match 'gaming flag not written: folder .*link')
    $g.WaitForExit(30000) | Out-Null
}
finally {
    cmd /c rmdir "$pub" 2>$null | Out-Null          # removes only the junction, never the target
    if (-not $inst.HasExited) { Stop-Process -Id $inst.Id -Force }
}
Check 'the junction target is untouched after the run' (@(Get-ChildItem $target -Force).Count -eq 0)
if ($fail -eq 0) { 'ALL CHECKS PASSED' } else { "$fail CHECK(S) FAILED"; exit 1 }

<#
 Maintainer script: turns the frames written by "QuickSweep.exe --shots=<dir> --demo --gif" into docs\screenshots\flyout.gif.
 Needs ffmpeg (on PATH, or pass -Ffmpeg). Only used when the README pictures are refreshed; users never need it.
   .\tools\make-gif.ps1 -Frames <dir>\gif-frames -Out docs\screenshots\flyout.gif
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Frames,
    [string]$Out = (Join-Path $PSScriptRoot '..\docs\screenshots\flyout.gif'),
    [string]$Ffmpeg = 'ffmpeg',
    [int]$Fps = 12,
    [int]$Width = 340
)
$ErrorActionPreference = 'Stop'
$filter = "fps=$Fps,scale=${Width}:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=96:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle"
& $Ffmpeg -y -hide_banner -loglevel error -framerate $Fps -i (Join-Path $Frames 'f%03d.png') -vf $filter -loop 0 -map_metadata -1 -fflags +bitexact -flags:v +bitexact $Out
if ($LASTEXITCODE -ne 0) { throw 'ffmpeg failed' }
"{0}: {1:N0} KB" -f $Out, ((Get-Item $Out).Length / 1KB)

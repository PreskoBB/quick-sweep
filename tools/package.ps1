<#
 Packs the release zip from an already published .\dist (run build.ps1 first):

   QuickSweep-1.0.6.zip
     Install Quick Sweep.bat   double-click: asks for admin, then runs install.ps1
     install.ps1  uninstall.ps1
     README.txt  LICENSE  THIRD-PARTY-NOTICES.md  licenses\
     app\                  the published program

   .\tools\package.ps1 -Version 1.0.6
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Dist = (Join-Path $PSScriptRoot '..\dist'),
    [string]$OutDir = (Join-Path $PSScriptRoot '..\release')
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not (Test-Path (Join-Path $Dist 'QuickSweep.exe'))) { throw "No QuickSweep.exe in $Dist. Run build.ps1 first." }
$name = "QuickSweep-$Version"
$stage = Join-Path $OutDir $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'app') | Out-Null
Copy-Item (Join-Path $Dist '*') (Join-Path $stage 'app') -Recurse
foreach ($f in 'Install Quick Sweep.bat', 'install.ps1', 'uninstall.ps1', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'CHANGELOG.md') { Copy-Item (Join-Path $root $f) $stage }
Copy-Item (Join-Path $root 'licenses') (Join-Path $stage 'licenses') -Recurse
@"
Quick Sweep $Version

Install:   double-click "Install Quick Sweep.bat" and click Yes. Answer the questions in the window (Enter accepts the defaults).
Uninstall: open PowerShell as administrator in this folder and run .\uninstall.ps1
More:      the README on the project page explains what Quick Sweep does, why it needs administrator rights, and what it keeps private.

Quick Sweep is released under the MIT license (LICENSE). Bundled libraries: THIRD-PARTY-NOTICES.md.
"@ | Set-Content (Join-Path $stage 'README.txt') -Encoding ASCII
$zip = Join-Path $OutDir "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name.zip" | Set-Content (Join-Path $OutDir "$name.zip.sha256") -Encoding ASCII
"{0}  ({1:N1} MB)  sha256 {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $hash

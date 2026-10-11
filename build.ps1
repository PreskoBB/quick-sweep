<#
 Builds Quick Sweep and publishes it to .\dist (framework-dependent, ReadyToRun; needs the .NET 10 Desktop Runtime).
 Optional parameters keep caches off the system drive and stamp a version:
   .\build.ps1 -NuGetCache D:\nuget\packages -Version 1.2.0
#>
[CmdletBinding()]
param(
    [string]$NuGetCache = $env:NUGET_PACKAGES,
    [string]$Output = (Join-Path $PSScriptRoot 'dist'),
    [string]$Version
)
$ErrorActionPreference = 'Stop'
if ($NuGetCache) { $env:NUGET_PACKAGES = $NuGetCache }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:MSBUILDDISABLENODEREUSE = '1'

$proj = Join-Path $PSScriptRoot 'src\QuickSweep\QuickSweep.csproj'
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
$extra = @()
if ($Version) { $extra += "-p:Version=$Version" }
dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true @extra `
    --disable-build-servers -nodeReuse:false -o $Output
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }
Write-Host "Published to $Output"

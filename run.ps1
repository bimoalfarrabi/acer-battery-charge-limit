<#
    run.ps1 - build then launch the app. One command for the edit/preview loop.
    Uses the same Windows-local artifacts path as build.ps1 (see the note there:
    mt.exe cannot handle the WSL UNC source path).

    The running app must be stopped BEFORE the build, not after: the build
    overwrites BattCtl.App.exe and a live process holds a lock on it.
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$ArtifactsPath = "$env:LOCALAPPDATA\BattCtl\artifacts"
)

$ErrorActionPreference = 'Stop'

Get-Process BattCtl.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -ArtifactsPath $ArtifactsPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$flavor = if ($Configuration -eq 'Release') { 'release' } else { 'debug' }
$exe = Join-Path $ArtifactsPath "bin/BattCtl.App/${flavor}_win-x64/BattCtl.App.exe"
if (-not (Test-Path $exe)) { throw "Build output not found: $exe" }

Start-Process -FilePath $exe
Write-Host "Launched: $exe"

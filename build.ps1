<#
    build.ps1 - build the WinUI 3 app.

    The sources live on a WSL UNC path (\\wsl.localhost\...). With
    WindowsAppSDKSelfContained enabled, the Windows SDK manifest step runs
    mt.exe, which cannot parse a UNC path and fails with c1010070. Redirecting
    obj/ and bin/ to a Windows-local directory fixes it, so the artifacts path
    below is required, not a convenience.
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$ArtifactsPath = "$env:LOCALAPPDATA\BattCtl\artifacts"
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src/BattCtl.App/BattCtl.App.csproj'

& dotnet build $project -c $Configuration -p:Platform=x64 --artifacts-path $ArtifactsPath
exit $LASTEXITCODE

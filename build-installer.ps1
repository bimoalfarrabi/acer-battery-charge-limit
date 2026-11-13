<#
    build-installer.ps1 - publish the app and build the Inno Setup installer.

    Publishes self-contained to a local artifacts path, then runs ISCC.exe with
    /DMyAppVersion and /DPublishDir. Needs Inno Setup 6.7+ installed
    (https://jrsoftware.org/isdl.php); the release CI uses the runner's copy.
#>
param(
    [string]$Version = '1.2.0',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$ArtifactsPath = "$env:LOCALAPPDATA\BattCtl\installer-artifacts"
)
$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$project = Join-Path $repoRoot 'src/BattCtl.App/BattCtl.App.csproj'
$publishDir = Join-Path $ArtifactsPath 'publish\BattCtl.App\release_win-x64'
$issPath = Join-Path $repoRoot 'installer/BattCtl.iss'

& dotnet publish $project -c $Configuration -p:Platform=x64 -p:Version=$Version `
    -p:WindowsAppSDKSelfContained=true -p:SelfContained=true -p:PublishTrimmed=false `
    --artifacts-path $ArtifactsPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$required = @('BattCtl.App.exe', 'BattCtl.App.dll', 'resources.pri', 'Microsoft.WinUI.dll')
$missing = @($required | Where-Object { -not (Test-Path (Join-Path $publishDir $_)) })
if ($missing.Count -gt 0) {
    Write-Error "Missing from publish output: $($missing -join ', ')"
    exit 1
}

$iscc = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    Write-Error 'ISCC.exe not found. Install Inno Setup 6.7.0 from https://jrsoftware.org/isdl.php'
    exit 1
}

& $iscc "/DMyAppVersion=$Version" "/DPublishDir=$publishDir" $issPath
exit $LASTEXITCODE

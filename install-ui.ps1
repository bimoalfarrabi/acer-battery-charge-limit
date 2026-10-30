<#
    install-ui.ps1 - deploy the WinUI 3 front end and refresh the worker.

    Run this once (self-elevates via UAC). It is separate from
    battctl-install.cmd, which only handles the CLI/worker plumbing.

    What it does:
      * publishes the app self-contained (no Windows App SDK runtime install)
      * copies it to C:\Program Files\BattCtl.App
      * refreshes the worker in C:\Program Files\battctl, so the SYSTEM task
        picks up the new `snapshot` command
      * creates a Start Menu shortcut

    Re-run after any UI change. Safe to run repeatedly.
#>
[CmdletBinding()]
param(
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Elevating...'
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($NoShortcut) { $argList += '-NoShortcut' }
    Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs
    exit 0
}

$repoRoot   = $PSScriptRoot
$project    = Join-Path $repoRoot 'src/BattCtl.App/BattCtl.App.csproj'
$installDir = 'C:\Program Files\BattCtl.App'
$workerSrc  = Join-Path $repoRoot 'battctl-worker.ps1'
$workerDst  = 'C:\Program Files\battctl\battctl-worker.ps1'
$cliSrc     = Join-Path $repoRoot 'battctl.ps1'
$cliDst     = 'C:\Program Files\battctl\battctl.ps1'
$artifacts  = Join-Path $env:LOCALAPPDATA 'BattCtl\publish'

Write-Host 'Publishing...'
# mt.exe cannot read a UNC path, so artifacts must land on a local drive.
& dotnet publish $project -c Release -p:Platform=x64 --artifacts-path $artifacts `
    -p:WindowsAppSDKSelfContained=true -p:SelfContained=true -p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$published = Join-Path $artifacts 'publish/BattCtl.App/release_win-x64'
if (-not (Test-Path $published)) { throw "Publish output not found: $published" }
if (-not (Test-Path (Join-Path $published 'resources.pri'))) {
    throw "resources.pri missing from publish output; the app would launch blank. Aborting."
}

Write-Host "Installing to $installDir ..."
Get-Process BattCtl.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

if (-not (Test-Path $installDir)) { New-Item -ItemType Directory -Path $installDir | Out-Null }
Copy-Item -Path (Join-Path $published '*') -Destination $installDir -Recurse -Force

if (Test-Path $workerSrc) {
    Copy-Item -Path $workerSrc -Destination $workerDst -Force
    Write-Host "Worker refreshed: $workerDst"
}

# The CLI and the app share a mailbox mutex. Deploying one without the other
# reintroduces the overlap that made 1 in 10 concurrent calls time out, so both
# are always copied together.
if (Test-Path $cliSrc) {
    Copy-Item -Path $cliSrc -Destination $cliDst -Force
    Write-Host "CLI refreshed: $cliDst"
}

if (-not $NoShortcut) {
    $startMenu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
    $link = Join-Path $startMenu 'Acer Battery.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($link)
    $shortcut.TargetPath = Join-Path $installDir 'BattCtl.App.exe'
    $shortcut.WorkingDirectory = $installDir
    $shortcut.Description = 'Battery charge limit'
    $shortcut.Save()
    Write-Host "Shortcut created: $link"
}

Write-Host ''
Write-Host 'Installed.'
Write-Host "  app    : $installDir\BattCtl.App.exe"
Write-Host "  worker : $workerDst"
Write-Host ''
Write-Host 'Verify the SYSTEM snapshot path with:'
Write-Host '  & "C:\Program Files\battctl\battctl.ps1" status'
Write-Host '  (then open the app and confirm the charge-limit toggle reflects reality)'
exit 0

<#
    uninstall.ps1 - removes everything battctl installed. Self-elevates via UAC.
      * unregisters the "BattCtl" and "BattCtlResume" scheduled tasks
      * deletes C:\Program Files\battctl
      * deletes C:\Program Files\BattCtl.App and its Start Menu shortcut
      * removes the HKCU Run entry for each real user profile
    By default your settings and logs (C:\ProgramData\battctl and
    %LOCALAPPDATA%\BattCtl.App) are kept; pass -RemoveData to delete them too.
#>
param(
    [switch]$KeepData,
    [switch]$RemoveData
)
$ErrorActionPreference = 'Continue'

if ($KeepData -and $RemoveData) {
    Write-Error '-KeepData and -RemoveData are mutually exclusive.'
    exit 1
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Elevating (UAC)...'
    $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
    )
    exit $p.ExitCode
}

Unregister-ScheduledTask -TaskName 'BattCtl' -Confirm:$false -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName 'BattCtlResume' -Confirm:$false -ErrorAction SilentlyContinue
$TargetDir = 'C:\Program Files\battctl'
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (-not [string]::IsNullOrEmpty($machinePath)) {
    $pathEntries = @($machinePath -split ';' | Where-Object { $_ -ne '' })
    $targetPathKey = $TargetDir.TrimEnd([char]'\')
    $remainingPathEntries = @($pathEntries | Where-Object { $_.TrimEnd([char]'\') -ine $targetPathKey })
    if ($remainingPathEntries.Count -ne $pathEntries.Count) {
        [Environment]::SetEnvironmentVariable('Path', ($remainingPathEntries -join ';'), 'Machine')
    }
}
Remove-Item 'C:\Program Files\battctl' -Recurse -Force -ErrorAction SilentlyContinue

if ($RemoveData) {
    Remove-Item 'C:\ProgramData\battctl' -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- desktop app -----------------------------------------------------------
# Never force-kill the tray app: a killed process leaves a ghost tray icon and
# can leave the worker mailbox mid-write. The Inno uninstaller checks the same
# mutex; this PowerShell path keeps the same rule.
$app = Get-Process BattCtl.App -ErrorAction SilentlyContinue
if ($app) {
    Write-Host 'Acer Battery is running. Quit it from the tray menu, then run this again.'
    exit 1
}
Remove-Item 'C:\Program Files\BattCtl.App' -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Acer Battery.lnk') -Force -ErrorAction SilentlyContinue
if ($RemoveData) {
    Remove-Item (Join-Path $env:LOCALAPPDATA 'BattCtl.App') -Recurse -Force -ErrorAction SilentlyContinue
}

# Launch-at-login lives in HKCU and points at the app we just removed, so it is
# always removed; only the data directories are gated by -RemoveData.
$runKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
Get-ChildItem 'Registry::HKEY_USERS' -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -match '^S-1-5-21-' } |
    ForEach-Object {
        $sid = $_.PSChildName
        $key = "Registry::HKEY_USERS\$sid\$runKey"
        if (Test-Path $key) {
            Remove-ItemProperty -Path $key -Name 'BattCtl.App' -ErrorAction SilentlyContinue
        }
    }

Get-ChildItem (Join-Path $env:SystemDrive 'Users') -Directory -ErrorAction SilentlyContinue |
    ForEach-Object {
        $hive = Join-Path $_.FullName 'NTUSER.DAT'
        if (-not (Test-Path $hive)) { return }

        # Only profiles that are not already loaded need mounting.
        $mountName = 'BattCtlUninstallProbe'
        $null = reg.exe load "HKU\$mountName" "$hive" 2>&1
        if ($LASTEXITCODE -eq 0) {
            $k = "Registry::HKEY_USERS\$mountName\$runKey"
            if (Test-Path $k) {
                Remove-ItemProperty -Path $k -Name 'BattCtl.App' -ErrorAction SilentlyContinue
            }
            [gc]::Collect()
            Start-Sleep -Milliseconds 200
            $null = reg.exe unload "HKU\$mountName" 2>&1
        }
    }

Write-Host 'Uninstalled: BattCtl tasks, C:\Program Files\battctl, C:\Program Files\BattCtl.App,'
Write-Host '            Start Menu shortcut, launch-at-login entries.'
if ($RemoveData) {
    Write-Host 'Also removed: C:\ProgramData\battctl, %LOCALAPPDATA%\BattCtl.App.'
} else {
    Write-Host 'Kept your settings and logs (C:\ProgramData\battctl, %LOCALAPPDATA%\BattCtl.App);'
    Write-Host 'pass -RemoveData to delete them too.'
}
exit 0

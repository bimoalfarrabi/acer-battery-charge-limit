<#
    unprovision.ps1 - removes what the Inno Setup installer provisioned.
    Called by the uninstaller, which is already elevated: this script must not
    self-elevate. The Inno uninstaller removes {app} itself, so this script only
    handles what Setup cannot: the SYSTEM tasks, the PATH entry and {autopf}\battctl.
    -KeepData (default) leaves C:\ProgramData\battctl and %LOCALAPPDATA%\BattCtl.App.
    -RemoveData deletes them and the HKCU Run entry for every real user profile.
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

Unregister-ScheduledTask -TaskName 'BattCtl' -Confirm:$false -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName 'BattCtlResume' -Confirm:$false -ErrorAction SilentlyContinue

$BattCtlDir = 'C:\Program Files\battctl'
$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if (-not [string]::IsNullOrEmpty($machinePath)) {
    $pathEntries = @($machinePath -split ';' | Where-Object { $_ -ne '' })
    $targetPathKey = $BattCtlDir.TrimEnd([char]'\')
    $remainingPathEntries = @($pathEntries | Where-Object { $_.TrimEnd([char]'\') -ine $targetPathKey })
    if ($remainingPathEntries.Count -ne $pathEntries.Count) {
        [Environment]::SetEnvironmentVariable('Path', ($remainingPathEntries -join ';'), 'Machine')
    }
}
Remove-Item $BattCtlDir -Recurse -Force -ErrorAction SilentlyContinue

if ($RemoveData) {
    Remove-Item 'C:\ProgramData\battctl' -Recurse -Force -ErrorAction SilentlyContinue
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
        $mountName = 'BattCtlUnprovisionProbe'
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
exit 0

<#
    battctl-install.ps1 — one-time elevated setup.
      * copies the worker to C:\Program Files\battctl  (admin-only, safe for SYSTEM to run)
      * creates the mailbox C:\ProgramData\battctl\mailbox and grants the invoking user Modify
      * registers the on-demand scheduled task "BattCtl" (SYSTEM, highest privileges, 60s cap)
    Self-elevates via UAC on the first run. Re-run it any time to update.
#>
$ErrorActionPreference = 'Stop'

$SourceDir = Split-Path -Parent $PSCommandPath
$TargetDir = 'C:\Program Files\battctl'
$Mailbox   = 'C:\ProgramData\battctl\mailbox'
$TaskName  = 'BattCtl'
$WorkerSrc = Join-Path $SourceDir 'battctl-worker.ps1'
$WorkerDst = Join-Path $TargetDir 'battctl-worker.ps1'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Elevating (UAC)...'
    $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
    )
    exit $p.ExitCode
}

if (-not (Test-Path $WorkerSrc)) { Write-Error "worker source not found: $WorkerSrc"; exit 1 }

New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
New-Item -ItemType Directory -Force -Path $Mailbox   | Out-Null
Copy-Item -Path $WorkerSrc -Destination $WorkerDst -Force

# Mailbox must be writable by the normal user (medium IL); worker dir stays admin-only.
$me = "$env:USERDOMAIN\$env:USERNAME"
$null = icacls $Mailbox /grant "${me}:(OI)(CI)M"
$null = icacls (Split-Path $Mailbox -Parent) /grant "${me}:(OI)(CI)M"

$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument ('-NoProfile -ExecutionPolicy Bypass -File "' + $WorkerDst + '"')
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Seconds 60) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null

# Allow the normal (medium-IL) user to trigger the task without a UAC prompt.
try {
    $sddl = 'D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;IU)'
    $svc  = New-Object -ComObject 'Schedule.Service'
    $svc.Connect()
    $root = $svc.GetFolder('\')
    $t = $root.GetTask($TaskName)
    $t.SetSecurityDescriptor($sddl, 0)
    Write-Host 'Task security descriptor updated (Interactive Users may Run).'
} catch {
    Write-Host ("Note: could not set task SDDL automatically: " + $_.Exception.Message)
}

Write-Host ''
Write-Host 'Installed:'
Write-Host ("  worker : $WorkerDst")
Write-Host ("  mailbox: $Mailbox")
Write-Host ("  task   : $TaskName  (SYSTEM / highest / on-demand)")
Write-Host ''
Write-Host 'Test with:  battctl status'
exit 0

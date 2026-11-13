<#
    battctl-install.ps1 - one-time elevated setup.
      * copies the worker to C:\Program Files\battctl  (admin-only, safe for SYSTEM to run)
      * creates the mailbox C:\ProgramData\battctl\mailbox and grants the invoking user Modify
      * registers the on-demand scheduled task "BattCtl" (SYSTEM, highest privileges, 60s cap)
      * registers the boot/resume scheduled task "BattCtlResume" (SYSTEM, highest privileges)
    Self-elevates via UAC on the first run. Re-run it any time to update.
    -Quiet skips self-elevation and the interactive output (used by the Inno Setup
    installer, which is already elevated and captures no console).
#>
param(
    [switch]$Quiet,
    [switch]$ProvisionOnly
)
$ErrorActionPreference = 'Stop'

# All user-facing messages go through here so -Quiet can silence them without
# scattering conditionals over every line.
function Say([string]$Message) {
    if (-not $Quiet) { Write-Host $Message }
}

$SourceDir = Split-Path -Parent $PSCommandPath
$TargetDir = 'C:\Program Files\battctl'
$Mailbox   = 'C:\ProgramData\battctl\mailbox'
$TaskName  = 'BattCtl'
$ResumeTaskName = 'BattCtlResume'
$WorkerSrc = Join-Path $SourceDir 'battctl-worker.ps1'
$WorkerDst = Join-Path $TargetDir 'battctl-worker.ps1'
$FrontendPsSrc  = Join-Path $SourceDir 'battctl.ps1'
$FrontendCmdSrc = Join-Path $SourceDir 'battctl.cmd'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
           ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $Quiet) {
    Say 'Elevating (UAC)...'
    $p = Start-Process powershell.exe -Verb RunAs -Wait -PassThru -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
    )
    exit $p.ExitCode
}

New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
New-Item -ItemType Directory -Force -Path $Mailbox   | Out-Null

# The Inno Setup installer already copied the worker and CLI into place, so
# -ProvisionOnly skips the source checks and copy and only does the plumbing
# (tasks, PATH, mailbox ACL).
if (-not $ProvisionOnly) {
    if (-not (Test-Path $WorkerSrc)) { Write-Error "worker source not found: $WorkerSrc"; exit 1 }
    if (-not (Test-Path $FrontendPsSrc)) { Write-Error "front-end source not found: $FrontendPsSrc"; exit 1 }
    if (-not (Test-Path $FrontendCmdSrc)) { Write-Error "command wrapper source not found: $FrontendCmdSrc"; exit 1 }

    Copy-Item -Path $WorkerSrc -Destination $WorkerDst -Force
    Copy-Item -Path $FrontendPsSrc -Destination (Join-Path $TargetDir 'battctl.ps1') -Force
    Copy-Item -Path $FrontendCmdSrc -Destination (Join-Path $TargetDir 'battctl.cmd') -Force
}

$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
$targetPathKey = $TargetDir.TrimEnd([char]'\')
$pathEntries = @($machinePath -split ';' | Where-Object { $_ -ne '' })
$targetOnPath = $false
foreach ($entry in $pathEntries) {
    if ($entry.TrimEnd([char]'\') -ieq $targetPathKey) {
        $targetOnPath = $true
        break
    }
}
if ($targetOnPath) {
    Say "$TargetDir is already on the Machine PATH."
} else {
    if ([string]::IsNullOrEmpty($machinePath)) {
        $newMachinePath = $TargetDir
    } elseif ($machinePath.EndsWith(';')) {
        $newMachinePath = $machinePath + $TargetDir
    } else {
        $newMachinePath = $machinePath + ';' + $TargetDir
    }
    [Environment]::SetEnvironmentVariable('Path', $newMachinePath, 'Machine')
    Say "Added $TargetDir to the Machine PATH."
}

# Mailbox must be writable by the normal user (medium IL); worker dir stays admin-only.
$me = "$env:USERDOMAIN\$env:USERNAME"
$null = icacls $Mailbox /grant "${me}:(OI)(CI)M"
$null = icacls (Split-Path $Mailbox -Parent) /grant "${me}:(OI)(CI)M"

$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument ('-NoProfile -ExecutionPolicy Bypass -File "' + $WorkerDst + '"')
$resumeAction = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument ('-NoProfile -ExecutionPolicy Bypass -File "' + $WorkerDst + '" -Action restore')
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Seconds 60) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null

function Grant-TaskInteractiveUsersRun([string]$Name) {
    # Allow the normal (medium-IL) user to trigger the task without a UAC prompt.
    try {
        $sddl = 'D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;IU)'
        $svc  = New-Object -ComObject 'Schedule.Service'
        $svc.Connect()
        $root = $svc.GetFolder('\')
        $t = $root.GetTask($Name)
        $t.SetSecurityDescriptor($sddl, 0)
        Say ("Task security descriptor updated (Interactive Users may Run): " + $Name)
    } catch {
        Say ("Note: could not set task SDDL automatically for " + $Name + ": " + $_.Exception.Message)
    }
}

Grant-TaskInteractiveUsersRun $TaskName

$boot = New-ScheduledTaskTrigger -AtStartup
$boot.Delay = 'PT20S'
$cls = Get-CimClass -Namespace root/Microsoft/Windows/TaskScheduler -ClassName MSFT_TaskEventTrigger
$evt = New-CimInstance -CimClass $cls -ClientOnly
$evt.Enabled = $true
$evt.Delay = 'PT20S'
$evt.Subscription = '<QueryList><Query Id="0" Path="System"><Select Path="System">*[System[Provider[@Name=''Microsoft-Windows-Power-Troubleshooter''] and EventID=1]]</Select></Query></QueryList>'
Register-ScheduledTask -TaskName $ResumeTaskName -Action $resumeAction -Principal $principal -Settings $settings `
    -Trigger @($boot, $evt) -Force | Out-Null
Grant-TaskInteractiveUsersRun $ResumeTaskName

Say ''
Say 'Installed:'
Say ("  worker : $WorkerDst")
Say ("  mailbox: $Mailbox")
Say ("  task   : $TaskName  (SYSTEM / highest / on-demand)")
Say ("  resume : $ResumeTaskName  (SYSTEM / highest / boot and resume)")
Say ''
Say 'Test with:  battctl status (smoke test; a new terminal may be required for battctl to be found)'
exit 0

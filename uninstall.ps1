<#
    uninstall.ps1 - removes everything battctl installed. Self-elevates via UAC.
      * unregisters the "BattCtl" and "BattCtlResume" scheduled tasks
      * deletes C:\Program Files\battctl
      * deletes C:\ProgramData\battctl (mailbox)
#>
$ErrorActionPreference = 'Continue'

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
Remove-Item 'C:\ProgramData\battctl'   -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'Uninstalled: BattCtl task, C:\Program Files\battctl, C:\ProgramData\battctl.'
exit 0

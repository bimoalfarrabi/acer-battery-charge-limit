<#
    provision.ps1 - post-install plumbing for the Inno Setup installer.
    Called by Setup, which is already elevated: this script must not self-elevate.
    The work itself (worker copy, tasks, SDDL, mailbox ACL, PATH) is done by
    battctl-install.ps1; -Quiet keeps it silent and skips its self-elevation.
#>
$ErrorActionPreference = 'Stop'

$installScript = Join-Path $PSScriptRoot 'battctl-install.ps1'
if (-not (Test-Path $installScript)) {
    Write-Error "provision source not found: $installScript"
    exit 1
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installScript -Quiet -ProvisionOnly
exit $LASTEXITCODE

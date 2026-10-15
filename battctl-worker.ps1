<#
    battctl-worker.ps1
    Executed by the "BattCtl" scheduled task as NT AUTHORITY\SYSTEM.
    Pinned to Windows PowerShell 5.1 (Get-WmiObject is unavailable in PS7).

    Interface (root\WMI : BatteryControl):
        SetBatteryHealthControl(uBatteryNo=1, uFunctionMask=1, uFunctionStatus=0|1, uReservedIn[5])
        GetBatteryHealthControlStatus(uBatteryNo=1, uFunctionQuery=1, uReserved[2])
      .uReturn[0]         = 0 on success
      .uFunctionStatus[0] = 1 -> limit ON (80%) | 0 -> limit OFF (100%)

    Mailbox (C:\ProgramData\battctl\mailbox):
        request.txt : "<nonce>|<command>"        command in {status,on,off,toggle}
        result.txt  : "<nonce>|<exitcode>" NL <message lines...>

    Exit: 0 ok | 1 wmi/provider failure | 2 feature absent | 4 bad request
#>
$ErrorActionPreference = 'Stop'

$Mailbox = 'C:\ProgramData\battctl\mailbox'
$ReqPath = Join-Path $Mailbox 'request.txt'
$ResPath = Join-Path $Mailbox 'result.txt'

$script:msg = New-Object System.Collections.Generic.List[string]
function Say([string]$s) { $script:msg.Add([string]$s) }
function Emit([string]$nonce, [int]$code) {
    $lines = @("$nonce|$code") + $script:msg
    [IO.File]::WriteAllLines($ResPath, $lines, (New-Object System.Text.UTF8Encoding($false)))
    exit $code
}

# ---------------- parse request ---------------------------------------------
$nonce = ''; $command = ''
try { $req = [IO.File]::ReadAllText($ReqPath) } catch { Say 'ERROR: cannot read request file.'; Emit '' 1 }
$parts = $req.Trim() -split '\|', 2
if ($parts.Count -ge 1) { $nonce   = $parts[0].Trim() }
if ($parts.Count -ge 2) { $command = $parts[1].Trim().ToLower() }
if ($nonce -notmatch '^[0-9A-Za-z]{4,64}$' -or @('status', 'on', 'off', 'toggle') -notcontains $command) {
    Say 'ERROR: malformed request.'
    Emit $nonce 4
}

# ---------------- WMI --------------------------------------------------------
$bc = $null
try { $bc = Get-WmiObject -Namespace root\wmi -Class BatteryControl -ErrorAction Stop } catch { $bc = $null }
if ($null -eq $bc) {
    Say 'ERROR: root\WMI:BatteryControl instance not found.'
    Say '       This firmware does not expose the charge limit, or the WMI provider is not responding.'
    Emit $nonce 2
}

function Read-Limit {
    $r = $bc.GetBatteryHealthControlStatus([byte]1, [byte]1, [byte[]]@(0, 0))
    if ([int]$r.uReturn[0] -ne 0) { return $null }
    return [int]$r.uFunctionStatus[0]
}
function Write-Limit([int]$on) {
    $r = $bc.SetBatteryHealthControl([byte]1, [byte]1, [byte]$on, [byte[]]@(0, 0, 0, 0, 0))
    return ([int]$r.uReturn -eq 0)
}
function Describe([int]$s) {
    if ($s -eq 1) { return 'ON  (charge limited to 80%)' }
    if ($s -eq 0) { return 'OFF (charges to 100%)' }
    return "UNKNOWN ($s)"
}

$before = $null
try { $before = Read-Limit } catch { $before = $null }

switch ($command) {
    'status' {
        $now = Read-Limit
        if ($null -eq $now) { Say 'ERROR: could not read charge-limit status.'; Emit $nonce 1 }
        Say ('Battery Charge Limit: ' + (Describe $now))
        Emit $nonce 0
    }
    'on'  { $target = 1 }
    'off' { $target = 0 }
    'toggle' {
        if ($null -eq $before) { Say 'ERROR: could not read current state for toggle.'; Emit $nonce 1 }
        if ($before -eq 1) { $target = 0 } else { $target = 1 }
    }
}

# Unconditional write, then read-back verify (catches silent "ret=0 but EC did not apply").
if (-not (Write-Limit $target)) {
    Say ('ERROR: SetBatteryHealthControl(' + $target + ') returned failure.')
    Emit $nonce 1
}
Start-Sleep -Milliseconds 700
$after = Read-Limit
if ($null -eq $after) { Say 'ERROR: wrote value but read-back failed.'; Emit $nonce 1 }
if ($after -ne $target) {
    Say ('ERROR: wrote ' + (Describe $target) + ' but read-back is ' + (Describe $after) + '.')
    Emit $nonce 1
}
$state = 'set'
if ($null -ne $before -and $before -eq $target) { $state = 'already set' }
Say ('Battery Charge Limit: ' + $state + ' -> ' + (Describe $after))
Emit $nonce 0

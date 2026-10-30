param(
    [ValidateSet('', 'status', 'on', 'off', 'toggle', 'restore', 'snapshot')]
    [string]$Action = ''
)

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
$DataDir = 'C:\ProgramData\battctl'
$DesiredPath = Join-Path $DataDir 'desired.txt'
$RestoreLogPath = Join-Path $DataDir 'restore.log'

$script:msg = New-Object System.Collections.Generic.List[string]
function Say([string]$s) { $script:msg.Add([string]$s) }
function Emit([string]$nonce, [int]$code) {
    $lines = @("$nonce|$code") + $script:msg
    [IO.File]::WriteAllLines($ResPath, $lines, (New-Object System.Text.UTF8Encoding($false)))
    exit $code
}

function Log-Direct([string]$s) {
    if ([string]::IsNullOrEmpty($Action)) { return }
    try {
        New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
        $line = '[{0}] {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $s
        Add-Content -LiteralPath $RestoreLogPath -Value $line -Encoding ASCII
        $lines = [IO.File]::ReadAllLines($RestoreLogPath)
        if ($lines.Count -gt 200) {
            $keep = $lines[($lines.Count - 200)..($lines.Count - 1)]
            [IO.File]::WriteAllLines($RestoreLogPath, $keep, (New-Object System.Text.ASCIIEncoding))
        }
    } catch {
        # Logging must not change the worker exit code or operation outcome.
        Write-Debug $_.Exception.Message
    }
}

function Direct-Exit([int]$code, [string]$message) {
    if (-not [string]::IsNullOrEmpty($message)) { Log-Direct $message }
    exit $code
}

function Save-DesiredState([int]$state) {
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    [IO.File]::WriteAllText($DesiredPath, [string]$state, (New-Object System.Text.UTF8Encoding($false)))
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

function Get-NullableNumber($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $null }
    try {
        $number = [long]$property.Value
        if ($number -ge 4294967295) { return $null }
        return $number
    } catch { return $null }
}

function Get-NullableBoolean($Value) {
    if ($null -eq $Value) { return $null }
    try { return [bool]$Value } catch { return $null }
}

function Get-SnapshotRestoreLog {
    $entries = New-Object 'System.Collections.Generic.List[object]'
    try {
        if (-not (Test-Path -LiteralPath $RestoreLogPath -PathType Leaf)) { return ,@() }
        $lines = [IO.File]::ReadAllLines($RestoreLogPath)
        $valid = $true
        for ($i = 0; $i -lt $lines.Length; $i++) {
            $match = [regex]::Match($lines[$i], '^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] (.*)$')
            if (-not $match.Success) { $valid = $false; break }
            $parsedAt = [DateTime]::MinValue
            $ok = [DateTime]::TryParseExact(
                $match.Groups[1].Value,
                'yyyy-MM-dd HH:mm:ss',
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::None,
                [ref]$parsedAt
            )
            if (-not $ok) { $valid = $false; break }
            $entries.Add([pscustomobject][ordered]@{
                at = $parsedAt.ToString('yyyy-MM-ddTHH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
                message = $match.Groups[2].Value
            })
        }
        if (-not $valid) { return ,@() }
        $entryArray = $entries.ToArray()
        if ($entryArray.Count -gt 20) {
            return ,$entryArray[($entryArray.Count - 20)..($entryArray.Count - 1)]
        }
        return ,$entryArray
    } catch {
        return ,@()
    }
}

function Get-SnapshotJson([bool]$supported) {
    $desiredValue = $null
    try {
        $desiredText = [IO.File]::ReadAllText($DesiredPath)
        if ($desiredText.Trim() -match '^[01]$') { $desiredValue = ([int]$desiredText.Trim() -eq 1) }
    } catch { $desiredValue = $null }

    $restoreEntries = Get-SnapshotRestoreLog
    $installedAt = $null
    try {
        if (Test-Path -LiteralPath $DataDir -PathType Container) {
            $installedAt = (Get-Item -LiteralPath $DataDir).CreationTime.ToString(
                'yyyy-MM-ddTHH:mm:ss', [Globalization.CultureInfo]::InvariantCulture
            )
        }
    } catch { $installedAt = $null }

    $limitOn = $null
    if ($supported) {
        try {
            $limitValue = Read-Limit
            if ($null -ne $limitValue) { $limitOn = ([int]$limitValue -eq 1) }
        } catch { $limitOn = $null }
    }

    $battery = [ordered]@{
        present = $false
        percent = $null
        state = $null
        powerOnline = $null
        designCapacityMWh = $null
        fullChargeCapacityMWh = $null
        wearPercent = $null
        cycleCount = $null
        voltageMv = $null
        rateMw = $null
        remainingCapacityMWh = $null
        estimatedMinutes = $null
    }

        $staticData = $null
        try { $staticData = Get-WmiObject -Namespace root\WMI -Class BatteryStaticData -ErrorAction Stop | Select-Object -First 1 } catch { $staticData = $null }

        $cycleCountData = $null
        try { $cycleCountData = Get-WmiObject -Namespace root\WMI -Class BatteryCycleCount -ErrorAction Stop | Select-Object -First 1 } catch { $cycleCountData = $null }

        $fullCapacityData = $null
        try { $fullCapacityData = Get-WmiObject -Namespace root\WMI -Class BatteryFullChargedCapacity -ErrorAction Stop | Select-Object -First 1 } catch { $fullCapacityData = $null }

        $batteryStatusData = $null
        try { $batteryStatusData = Get-WmiObject -Namespace root\WMI -Class BatteryStatus -ErrorAction Stop | Select-Object -First 1 } catch { $batteryStatusData = $null }

        $win32Battery = $null
        try { $win32Battery = Get-WmiObject -Namespace root\cimv2 -Class Win32_Battery -ErrorAction Stop | Select-Object -First 1 } catch { $win32Battery = $null }

        $designCapacity = Get-NullableNumber $staticData 'DesignedCapacity'
        $cycleCount = Get-NullableNumber $cycleCountData 'CycleCount'
        $fullChargeCapacity = Get-NullableNumber $fullCapacityData 'FullChargedCapacity'
        $remainingCapacity = Get-NullableNumber $batteryStatusData 'RemainingCapacity'
        $voltage = Get-NullableNumber $batteryStatusData 'Voltage'
        $rate = Get-NullableNumber $batteryStatusData 'Rate'
        $estimatedPercent = Get-NullableNumber $win32Battery 'EstimatedChargeRemaining'
        $win32State = Get-NullableNumber $win32Battery 'BatteryStatus'
        $charging = $null
        $discharging = $null
        $powerOnline = $null
        if ($null -ne $batteryStatusData) {
            $charging = Get-NullableBoolean $batteryStatusData.Charging
            $discharging = Get-NullableBoolean $batteryStatusData.Discharging
            $powerOnline = Get-NullableBoolean $batteryStatusData.PowerOnline
        }

        $state = 'Unknown'
        if ($null -ne $batteryStatusData) {
            if ($null -ne $charging -and $charging) {
                $state = 'Charging'
            } elseif ($null -ne $discharging -and $discharging) {
                $state = 'Discharging'
            } elseif ($null -ne $charging -and $null -ne $discharging) {
                $state = 'NotCharging'
            }
        } elseif ($null -ne $win32State) {
            switch ([int]$win32State) {
                1 { $state = 'Discharging' }
                { $_ -in 6, 7, 8, 9 } { $state = 'Charging' }
                { $_ -in 2, 3, 11 } { $state = 'NotCharging' }
                default { $state = 'Unknown' }
            }
        }

        $percent = $null
        # Match the OS taskbar/Settings reading when available; capacity math is only a fallback.
        if ($null -ne $estimatedPercent -and $estimatedPercent -ge 0 -and $estimatedPercent -le 100) {
            $percent = [int]$estimatedPercent
        } elseif ($null -ne $remainingCapacity -and $null -ne $fullChargeCapacity -and $fullChargeCapacity -ne 0) {
            $percent = [int][Math]::Round(
                (([double]$remainingCapacity / [double]$fullChargeCapacity) * 100),
                [MidpointRounding]::AwayFromZero
            )
        }

        $wearPercent = $null
        if ($null -ne $designCapacity -and $designCapacity -ne 0 -and
            $null -ne $fullChargeCapacity -and $fullChargeCapacity -ne 0) {
            if ($fullChargeCapacity -gt $designCapacity) {
                $wearPercent = 0
            } else {
                $wearPercent = [Math]::Round((1 - ([double]$fullChargeCapacity / [double]$designCapacity)) * 100, 1)
            }
        }

        $estimatedMinutes = $null
        if ($state -eq 'Discharging' -and $null -ne $rate -and $rate -lt 0 -and $null -ne $remainingCapacity) {
            $estimatedMinutes = [long][Math]::Round(([Math]::Abs([double]$remainingCapacity) / [Math]::Abs([double]$rate)) * 60)
        }

        $battery.present = ($null -ne $staticData -or $null -ne $fullCapacityData -or
            $null -ne $batteryStatusData -or $null -ne $win32Battery)
        $battery.percent = $percent
        $battery.state = $state
        $battery.powerOnline = $powerOnline
        $battery.designCapacityMWh = $designCapacity
        $battery.fullChargeCapacityMWh = $fullChargeCapacity
        $battery.wearPercent = $wearPercent
        $battery.cycleCount = $cycleCount
        $battery.voltageMv = $voltage
        $battery.rateMw = $rate
        $battery.remainingCapacityMWh = $remainingCapacity
        $battery.estimatedMinutes = $estimatedMinutes
    $snapshot = [ordered]@{
        schema = 2
        limitOn = $limitOn
        desired = $desiredValue
        supported = [bool]$supported
        battery = $battery
        restoreLog = $restoreEntries
        installedAt = $installedAt
    }
    $json = ConvertTo-Json -InputObject $snapshot -Depth 8 -Compress
    if ($json.Contains("`r") -or $json.Contains("`n")) { $json = $json -replace '[\r\n]', '' }
    if ($json.Contains("`r") -or $json.Contains("`n")) { throw 'Snapshot JSON contains a newline.' }
    return $json
}

function Write-SnapshotResult([string]$json, [int]$code) {
    if ($directMode) {
        Write-Output $json
        exit $code
    }
    Say $json
    Emit $nonce $code
}

$directMode = -not [string]::IsNullOrEmpty($Action)
$command = ''
if ($directMode) {
    $command = $Action.ToLower()
} else {
    # ---------------- parse request ---------------------------------------------
    $nonce = ''; $command = ''
    try { $req = [IO.File]::ReadAllText($ReqPath) } catch { Say 'ERROR: cannot read request file.'; Emit '' 1 }
    $parts = $req.Trim() -split '\|', 2
    if ($parts.Count -ge 1) { $nonce   = $parts[0].Trim() }
    if ($parts.Count -ge 2) { $command = $parts[1].Trim().ToLower() }
    if ($nonce -notmatch '^[0-9A-Za-z]{4,64}$' -or @('status', 'on', 'off', 'toggle', 'snapshot') -notcontains $command) {
        Say 'ERROR: malformed request.'
        Emit $nonce 4
    }
}

if ($directMode -and $command -eq 'restore') {
    $desiredText = $null
    try { $desiredText = [IO.File]::ReadAllText($DesiredPath) } catch { $desiredText = $null }
    if ($null -eq $desiredText -or $desiredText.Trim() -notmatch '^[01]$') {
        Direct-Exit 0 'no saved desired state; nothing to do'
    }
    $desired = [int]$desiredText.Trim()
} else {
    $desired = $null
}

# ---------------- WMI --------------------------------------------------------
$bc = $null
$attempts = 1
if ($directMode -and $command -eq 'restore') { $attempts = 10 }
for ($attempt = 1; $attempt -le $attempts -and $null -eq $bc; $attempt++) {
    try { $bc = Get-WmiObject -Namespace root\wmi -Class BatteryControl -ErrorAction Stop } catch { $bc = $null }
    if ($null -eq $bc -and $attempt -lt $attempts) { Start-Sleep -Seconds 3 }
}
if ($null -eq $bc) {
    if ($command -eq 'snapshot') {
        $snapshotJson = Get-SnapshotJson $false
        Write-SnapshotResult $snapshotJson 2
    }
    if ($directMode) {
        Direct-Exit 2 'root\WMI:BatteryControl instance not found; provider did not become ready.'
    } else {
        Say 'ERROR: root\WMI:BatteryControl instance not found.'
        Say '       This firmware does not expose the charge limit, or the WMI provider is not responding.'
        Emit $nonce 2
    }
}


if ($command -eq 'snapshot') {
    $snapshotCode = 0
    $snapshotJson = $null
    try { $snapshotJson = Get-SnapshotJson $true } catch {
        $snapshotCode = 1
        $snapshotJson = Get-SnapshotJson $false
    }
    Write-SnapshotResult $snapshotJson $snapshotCode
}

if ($directMode -and $command -eq 'restore') {
    $actual = $null
    try { $actual = Read-Limit } catch { $actual = $null }
    if ($null -eq $actual) {
        Direct-Exit 1 'could not read current state for restore'
    }
    if ($actual -eq $desired) {
        Direct-Exit 0 'already at desired state'
    }
    $writeOk = $false
    try { $writeOk = Write-Limit $desired } catch { $writeOk = $false }
    if (-not $writeOk) {
        Direct-Exit 1 ('restore write failed for ' + (Describe $desired))
    }
    Start-Sleep -Milliseconds 700
    try { $actual = Read-Limit } catch { $actual = $null }
    if ($null -eq $actual) {
        Direct-Exit 1 'restore wrote value but read-back failed'
    }
    if ($actual -ne $desired) {
        Direct-Exit 1 ('restore mismatch: wanted ' + (Describe $desired) + ', got ' + (Describe $actual))
    }
    Direct-Exit 0 ('restored desired state: ' + (Describe $actual))
}

$before = $null
if ($command -ne 'snapshot') {
    try { $before = Read-Limit } catch { $before = $null }
}

switch ($command) {
    'status' {
        $now = Read-Limit
        if ($null -eq $now) {
            if ($directMode) { Direct-Exit 1 'could not read charge-limit status' }
            Say 'ERROR: could not read charge-limit status.'; Emit $nonce 1
        }
        $statusMessage = 'Battery Charge Limit: ' + (Describe $now)
        if ($directMode) { Direct-Exit 0 $statusMessage }
        Say $statusMessage
        Emit $nonce 0
    }
    'on'  { $target = 1 }
    'off' { $target = 0 }
    'toggle' {
        if ($null -eq $before) {
            if ($directMode) { Direct-Exit 1 'could not read current state for toggle' }
            Say 'ERROR: could not read current state for toggle.'; Emit $nonce 1
        }
        if ($before -eq 1) { $target = 0 } else { $target = 1 }
    }
}

# Unconditional write, then read-back verify (catches silent "ret=0 but EC did not apply").
if (-not (Write-Limit $target)) {
    $writeError = 'ERROR: SetBatteryHealthControl(' + $target + ') returned failure.'
    if ($directMode) { Direct-Exit 1 $writeError }
    Say $writeError
    Emit $nonce 1
}
Start-Sleep -Milliseconds 700
$after = Read-Limit
if ($null -eq $after) {
    if ($directMode) { Direct-Exit 1 'wrote value but read-back failed' }
    Say 'ERROR: wrote value but read-back failed.'; Emit $nonce 1
}
if ($after -ne $target) {
    $verifyError = 'ERROR: wrote ' + (Describe $target) + ' but read-back is ' + (Describe $after) + '.'
    if ($directMode) { Direct-Exit 1 $verifyError }
    Say $verifyError
    Emit $nonce 1
}
$desiredSaveError = $null
try { Save-DesiredState $target } catch { $desiredSaveError = $_.Exception.Message }
if ($null -ne $desiredSaveError) {
    if ($directMode) { Direct-Exit 1 ('could not save desired state: ' + $desiredSaveError) }
    Say ('ERROR: could not save desired state: ' + $desiredSaveError)
    Emit $nonce 1
}
$state = 'set'
if ($null -ne $before -and $before -eq $target) { $state = 'already set' }
$successMessage = 'Battery Charge Limit: ' + $state + ' -> ' + (Describe $after)
if ($directMode) { Direct-Exit 0 $successMessage }
Say $successMessage
Emit $nonce 0

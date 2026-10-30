<#
    battctl.ps1 - user-level front end. Runs at medium integrity (NO UAC).
    Drops a nonce-tagged request in the mailbox, fires the "BattCtl" scheduled
    task (runs as SYSTEM), waits for the matching result and prints it.

    Exit codes:  0 ok | worker's own code | 3 task timeout | 4 mailbox/task missing
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Command = 'status'
)

$Command = "$Command".ToLowerInvariant()
if (@('status', 'on', 'off', 'toggle') -notcontains $Command) {
    [Console]::Error.WriteLine("battctl: unknown command '$Command'.")
    [Console]::Error.WriteLine('usage: battctl [status|on|off|toggle]')
    exit 4
}

$Mailbox   = 'C:\ProgramData\battctl\mailbox'
$ReqPath   = Join-Path $Mailbox 'request.txt'
$ResPath   = Join-Path $Mailbox 'result.txt'
$TaskName  = 'BattCtl'
$TimeoutSec = 20
$Nonce     = [guid]::NewGuid().ToString('N').Substring(0, 16)

if (-not (Test-Path $Mailbox)) {
    [Console]::Error.WriteLine("battctl: mailbox not found ($Mailbox).")
    [Console]::Error.WriteLine('         Run battctl-install first.')
    exit 4
}

# The mailbox is a single request/result slot, so two callers overlap: caller B
# overwrites request.txt while the worker is serving caller A, and the worker is
# registered with -MultipleInstances IgnoreNew so only one run happens. A caller
# lost this way waits out the full timeout (observed: 1 in 10 concurrent calls).
# Serialise across processes. Global\ so an elevated caller and a medium-IL UI
# share the same lock. AbandonedMutexException means the previous owner died;
# the mutex is still granted, so proceed.
$MutexName = 'Global\battctl-mailbox'
$MailboxMutex = New-Object System.Threading.Mutex($false, $MutexName)
$HaveMutex = $false
try {
    $HaveMutex = $MailboxMutex.WaitOne([TimeSpan]::FromSeconds($TimeoutSec))
} catch [System.Threading.AbandonedMutexException] {
    $HaveMutex = $true
}
if (-not $HaveMutex) {
    [Console]::Error.WriteLine('battctl: another battctl request is in progress.')
    [Console]::Error.WriteLine('         Try again in a moment.')
    exit 3
}

try {
    Remove-Item $ResPath -Force -ErrorAction SilentlyContinue
    try {
        [IO.File]::WriteAllText($ReqPath, "$Nonce|$Command", (New-Object System.Text.UTF8Encoding($false)))
    } catch {
        [Console]::Error.WriteLine("battctl: cannot write request: $($_.Exception.Message)")
        exit 4
    }

    $null = (schtasks.exe /run /tn $TaskName 2>&1)
    if ($LASTEXITCODE -ne 0) {
        [Console]::Error.WriteLine("battctl: could not start scheduled task '$TaskName' (schtasks exit $LASTEXITCODE).")
        [Console]::Error.WriteLine('         Is it installed / permitted? Run battctl-install.')
        exit 4
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $done = $null
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $ResPath) {
            $txt = $null
            try { $txt = [IO.File]::ReadAllText($ResPath) } catch { $txt = $null }
            if ($txt) {
                $lines = $txt -split "`r?`n"
                $head  = $lines[0] -split '\|', 2
                if ($head[0] -eq $Nonce) { $done = $lines; break }
            }
        }
        Start-Sleep -Milliseconds 150
    }

    if ($null -eq $done) {
        [Console]::Error.WriteLine("battctl: timed out after ${TimeoutSec}s waiting for '$TaskName'.")
        [Console]::Error.WriteLine("         The WMI provider may be stuck. Try:  schtasks /end /tn $TaskName")
        exit 3
    }

    $code = [int](($done[0] -split '\|', 2)[1])
    for ($i = 1; $i -lt $done.Count; $i++) {
        if ($done[$i] -ne '') { [Console]::Out.WriteLine($done[$i]) }
    }
    Remove-Item $ReqPath, $ResPath -Force -ErrorAction SilentlyContinue
    exit $code
} finally {
    if ($HaveMutex) { $MailboxMutex.ReleaseMutex() }
    $MailboxMutex.Dispose()
}

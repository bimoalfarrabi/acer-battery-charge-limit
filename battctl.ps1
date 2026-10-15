<#
    battctl.ps1 — user-level front end. Runs at medium integrity (NO UAC).
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

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace BattCtl.App.Services;

public sealed class BattCtlClient
{
    private const string MailboxPath = @"C:\ProgramData\battctl\mailbox";
    private const string TaskName = "BattCtl";

    // Shared with the PowerShell CLI (battctl.ps1). The mailbox is a single
    // request/result slot and the task is registered with -MultipleInstances
    // IgnoreNew, so two overlapping callers make one wait out the full timeout.
    // Measured: 3/15 concurrent CLI calls timed out while the app was polling,
    // 0/15 with it stopped. NOTE: mutual exclusion is NOT the fix — holding the
    // mailbox starves the other side (measured: 5/15 failed on "in progress").
    // The app instead defers to the CLI: it takes the mutex only for a moment as a
    // "is anyone else using the mailbox" probe, and backs off when it is held.
    private const string MailboxMutexName = @"Global\battctl-mailbox";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan StabilityInterval = TimeSpan.FromMilliseconds(60);
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    public async Task<Snapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        // The worker emits a usable, degraded snapshot on exit 1 (provider failure) and
        // exit 2 (no BatteryControl instance): battery telemetry is still readable
        // unprivileged, so discarding the payload would hide data the user needs.
        // Only a missing or unparseable payload is a real failure.
        var result = await ExecuteAsync("snapshot", cancellationToken, allowSnapshotDegraded: true).ConfigureAwait(false);
        if (result.PayloadLines.Count < 1)
        {
            throw new BattCtlException(BattCtlError.BadRequest, 4, "The worker returned no snapshot payload.");
        }

        try
        {
            return JsonSerializer.Deserialize<Snapshot>(result.PayloadLines[0])
                ?? throw new JsonException("The snapshot payload was JSON null.");
        }
        catch (JsonException exception)
        {
            var error = result.ExitCode == 1 ? BattCtlError.ProviderFailure : BattCtlError.BadRequest;
            throw new BattCtlException(error, result.ExitCode, "The worker returned an invalid snapshot.", exception);
        }
    }

    public async Task<BattCtlResult> SendCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (command is not ("on" or "off" or "toggle" or "status"))
        {
            throw new ArgumentOutOfRangeException(nameof(command), command, "Command must be on, off, toggle, or status.");
        }

        var result = await ExecuteAsync(command, cancellationToken, allowSnapshotDegraded: false).ConfigureAwait(false);
        return new BattCtlResult(result.ExitCode, string.Join(Environment.NewLine, result.PayloadLines));
    }

    private async Task<WorkerResponse> ExecuteAsync(string command, CancellationToken cancellationToken, bool allowSnapshotDegraded)
    {
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                // Never contend with another caller: the CLI is the primary
                // interface and the user is waiting on it. One cheap probe per
                // attempt, then back off instead of starving it.
                if (await IsMailboxBusyAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new BattCtlException(BattCtlError.MailboxBusy, 3, "Another battctl request is in progress.");
                }

                var response = await RunRequestAsync(command, cancellationToken).ConfigureAwait(false);
                if (response is not null)
                {
                    // Snapshot exit 1 and 2 both carry a valid degraded payload; parse it.
                    var degradedButUsable = allowSnapshotDegraded && response.ExitCode is 1 or 2;
                    if (response.ExitCode != 0 && !degradedButUsable)
                    {
                        throw CreateWorkerException(response.ExitCode, response.PayloadLines);
                    }

                    return response;
                }
            }

            throw new BattCtlException(BattCtlError.Timeout, 3, "The BattCtl task did not return a result within 20 seconds after retrying.");
        }
        finally
        {
            _requestLock.Release();
        }
    }

    // True when another process holds the mailbox mutex. A missing mutex means no
    // CLI has run yet, so there is nothing to defer to.
    private static async Task<bool> IsMailboxBusyAsync(CancellationToken cancellationToken)
    {
        Mutex? mutex;
        try
        {
            mutex = Mutex.OpenExisting(MailboxMutexName);
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }

        using (mutex)
        {
            bool acquired;
            try
            {
                acquired = await Task.Run(() => mutex.WaitOne(ProbeTimeout), cancellationToken).ConfigureAwait(false);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died holding it; we were granted ownership.
                acquired = true;
            }

            if (acquired)
            {
                mutex.ReleaseMutex();
                return false;
            }

            return true;
        }
    }

    private static async Task<WorkerResponse?> RunRequestAsync(string command, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(MailboxPath))
        {
            throw new BattCtlException(BattCtlError.NotInstalled, 4, $"BattCtl mailbox directory was not found: {MailboxPath}");
        }

        var requestPath = Path.Combine(MailboxPath, "request.txt");
        var resultPath = Path.Combine(MailboxPath, "result.txt");
        var nonce = Guid.NewGuid().ToString("N")[..16];

        try
        {
            await Task.Run(() => File.Delete(resultPath), cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(requestPath, $"{nonce}|{command}", Utf8WithoutBom, cancellationToken).ConfigureAwait(false);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new BattCtlException(BattCtlError.NotInstalled, 4, "BattCtl mailbox directory is unavailable.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new BattCtlException(BattCtlError.NotInstalled, 4, "BattCtl mailbox is not accessible.", exception);
        }

        await StartScheduledTaskAsync(cancellationToken).ConfigureAwait(false);

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < RequestTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var content = await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false);
                var lines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                if (lines.Length > 0)
                {
                    var header = lines[0].Split('|', 2);
                    if (header.Length == 2 && StringComparer.Ordinal.Equals(header[0], nonce))
                    {
                        if (!int.TryParse(header[1], out var exitCode))
                        {
                            throw new BattCtlException(BattCtlError.BadRequest, 4, "The worker returned a malformed result header.");
                        }

                        var payload = lines.Skip(1).ToList();
                        while (payload.Count > 0 && payload[^1].Length == 0)
                        {
                            payload.RemoveAt(payload.Count - 1);
                        }

                        // The worker writes result.txt with File.WriteAllLines, which does not
                        // publish atomically: a reader can observe the matching header while the
                        // payload is still being flushed. Require the content to be unchanged
                        // across a short gap before trusting it.
                        await Task.Delay(StabilityInterval, cancellationToken).ConfigureAwait(false);
                        string confirm;
                        try
                        {
                            confirm = await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false);
                        }
                        catch (FileNotFoundException)
                        {
                            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (!StringComparer.Ordinal.Equals(confirm, content))
                        {
                            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        return new WorkerResponse(exitCode, payload);
                    }
                }
            }
            catch (FileNotFoundException)
            {
                // The worker has not produced the result yet.
            }
            catch (DirectoryNotFoundException)
            {
                throw new BattCtlException(BattCtlError.NotInstalled, 4, "BattCtl mailbox directory disappeared while waiting for a result.");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task StartScheduledTaskAsync(CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("/run");
        process.StartInfo.ArgumentList.Add("/tn");
        process.StartInfo.ArgumentList.Add(TaskName);

        try
        {
            if (!process.Start())
            {
                throw new BattCtlException(BattCtlError.TaskStartFailed, 4, "Could not start schtasks.exe.");
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BattCtlException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new BattCtlException(BattCtlError.TaskStartFailed, 4, "Could not start the BattCtl scheduled task.", exception);
        }

        if (process.ExitCode != 0)
        {
            throw new BattCtlException(BattCtlError.TaskStartFailed, 4, $"schtasks.exe exited with code {process.ExitCode}.");
        }
    }

    private static BattCtlException CreateWorkerException(int exitCode, IReadOnlyList<string> payloadLines)
    {
        var message = string.Join(Environment.NewLine, payloadLines);
        var error = exitCode switch
        {
            1 => BattCtlError.ProviderFailure,
            2 => BattCtlError.Unsupported,
            3 => BattCtlError.Timeout,
            4 => BattCtlError.BadRequest,
            _ => BattCtlError.BadRequest
        };

        return new BattCtlException(error, exitCode, string.IsNullOrWhiteSpace(message) ? $"BattCtl worker failed with exit code {exitCode}." : message);
    }

    private sealed record WorkerResponse(int ExitCode, List<string> PayloadLines);
}

public sealed record BattCtlResult(int ExitCode, string Message);

public enum BattCtlError
{
    NotInstalled,
    TaskStartFailed,
    Timeout,
    Unsupported,
    ProviderFailure,
    BadRequest,
    MailboxBusy
}

public sealed class BattCtlException : Exception
{
    public BattCtlException(BattCtlError error, int exitCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
        ExitCode = exitCode;
    }

    public BattCtlError Error { get; }
    public int ExitCode { get; }
}

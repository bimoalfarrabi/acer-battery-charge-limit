using System.Text.Json;
using BattCtl.App.Services;
using Microsoft.UI.Dispatching;

namespace BattCtl.App.ViewModels;

// Single source of truth for the three pages and the tray tooltip.
// Every worker round trip costs ~930ms, so: cached first paint, optimistic
// toggle, and reconciliation against the authoritative read-back.
// Battery telemetry that the OS gives us for free (percent, state, rate, wear)
// comes from the BatteryMonitor instead, so it updates without touching the
// worker mailbox; the worker stays authoritative for limit, GPU, voltage and
// cycle count.
public sealed class MainViewModel : ObservableObject
{
    private const string UnknownValue = "\u2014"; // em dash, only ever a data placeholder glyph

    private readonly BattCtlClient _client;
    private readonly SnapshotCache _cache;
    private readonly CancellationTokenSource _shutdownTokenSource = new();

    private Snapshot? _snapshot;
    private BatteryReading? _live;
    private bool _refreshInFlight;
    private bool? _pendingLimitTarget;
    private string? _errorText;
    private string? _limitMessage;
    private bool _limitMessageIsError;
    private bool? _pendingGpuTarget;
    private string? _gpuMessage;
    private bool _gpuMessageIsError;
    private bool _reapplyPending;
    private string? _reapplyMessage;
    private bool _reapplySucceeded;

    public MainViewModel(BattCtlClient client, SnapshotCache cache, DispatcherQueue dispatcher)
    {
        _client = client;
        _cache = cache;
        Telemetry = new BatteryMonitor(dispatcher);
        Telemetry.ReadingChanged += OnTelemetryReading;
        Telemetry.Start();
    }

    public BatteryMonitor Telemetry { get; }

    public void Dispose()
    {
        Telemetry.ReadingChanged -= OnTelemetryReading;
        Telemetry.Dispose();
        Shutdown();
    }

    private void OnTelemetryReading(object? sender, BatteryReading reading)
    {
        _live = reading;

        // Only the projections that read the live value; never a blanket raise.
        RaisePropertyChanged(nameof(PercentText));
        RaisePropertyChanged(nameof(HasPercent));
        RaisePropertyChanged(nameof(StateText));
        RaisePropertyChanged(nameof(PowerText));
        RaisePropertyChanged(nameof(StateSummaryText));
        RaisePropertyChanged(nameof(RateText));
        RaisePropertyChanged(nameof(RateLineText));
        RaisePropertyChanged(nameof(RemainingCapacityText));
        RaisePropertyChanged(nameof(EstimatedTimeText));
        RaisePropertyChanged(nameof(FullChargeCapacityText));
        RaisePropertyChanged(nameof(DesignCapacityText));
        RaisePropertyChanged(nameof(WearText));
        RaisePropertyChanged(nameof(HealthText));
        RaisePropertyChanged(nameof(HealthValue));
        RaisePropertyChanged(nameof(HasWear));
        RaisePropertyChanged(nameof(HealthSummaryText));
        RaisePropertyChanged(nameof(HasBattery));
        RaisePropertyChanged(nameof(ShowContent));
        RaisePropertyChanged(nameof(TrayTooltip));

        // The pages paint imperatively from UpdateValues(), so a property-name
        // raise alone would leave the live percent and rate frozen on screen.
        // ValuesUpdated is the repaint signal for both pages; it must fire for
        // telemetry exactly as it does for a worker snapshot.
        ValuesUpdated?.Invoke();
    }

    public CancellationToken ShutdownToken => _shutdownTokenSource.Token;

    // ---- Raw state ------------------------------------------------------

    public Snapshot? Snapshot
    {
        get => _snapshot;
        private set => SetProperty(ref _snapshot, value);
    }

    public bool HasSnapshot => _snapshot is not null;

    public string? ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                RaisePropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => _errorText is not null;

    // Which top-level surface a page should show. The live reading alone is
    // enough to show real content, and the worker snapshot keeps that true for
    // the very first paint before the first telemetry read lands.
    public bool HasBattery => _live?.HasValue == true || _snapshot?.Battery?.Present == true;
    public bool ShowContent => HasBattery;

    // ---- Battery projections ---------------------------------------------

    private BatteryInfo? Battery => _snapshot?.Battery;

    // The live reading, when a read has actually landed. Field-level ownership:
    // while the monitor is healthy it owns these fields outright, and the
    // worker snapshot is only the fallback for the very first paint.
    private BatteryReading? Live => _live?.HasValue == true ? _live : null;

    private int? MergedPercent => Live is { } live ? live.Percent : Battery?.Percent;

    private BatteryState MergedState => Live is { } live
        ? live.State
        : Battery?.State is string state ? ParseState(state) : BatteryState.Unknown;

    private static BatteryState ParseState(string state) => state switch
    {
        "Charging" => BatteryState.Charging,
        "Discharging" => BatteryState.Discharging,
        "NotCharging" => BatteryState.NotCharging,
        _ => BatteryState.Unknown
    };

    private bool? MergedPowerOnline => Live is { } live ? live.PowerOnline : Battery?.PowerOnline;

    private double? MergedWear => Live is { } live ? live.WearPercent : Battery?.WearPercent;

    private int? MergedEstimatedMinutes => Live is { } live
        ? live.EstimatedMinutes
        : Battery?.EstimatedMinutes;

    public string PercentText => MergedPercent?.ToString() ?? UnknownValue;
    public bool HasPercent => MergedPercent is not null;

    public string StateText => MergedState switch
    {
        BatteryState.Charging => "Charging",
        BatteryState.Discharging => "Discharging",
        BatteryState.NotCharging => "Not charging",
        _ => "Unknown"
    };

    public string PowerText => MergedPowerOnline switch
    {
        true => "AC connected",
        false => "On battery",
        null => UnknownValue
    };

    public string StateSummaryText
    {
        get
        {
            if (MergedState == BatteryState.Unknown && MergedPowerOnline is null)
            {
                return UnknownValue;
            }

            var state = MergedState == BatteryState.Unknown ? null : StateText;
            var power = MergedPowerOnline is null ? null : PowerText;
            return string.Join(" \u00B7 ", new[] { state, power }.Where(s => s is not null));
        }
    }

    public string WearText => MergedWear is double wear ? $"{wear:0.0}%" : UnknownValue;

    public string HealthText => MergedWear is double wear ? $"{100.0 - wear:0.0}%" : UnknownValue;

    // Floor for the progress bar binding only; HasWear keeps the bar hidden
    // when wear is unknown, so this 0 never reaches a text label.
    public double HealthValue => MergedWear is double wear ? Math.Clamp(100.0 - wear, 0, 100) : 0;

    public bool HasWear => MergedWear is not null;

    public string CycleCountText => Battery?.CycleCount?.ToString() ?? UnknownValue;

    public string VoltageText => Battery?.VoltageMv is int mv ? $"{mv / 1000.0:0.00} V" : UnknownValue;

    public string RateText => Live is { } live
        ? live.RateWatts is double watts ? $"{watts:0.0} W" : UnknownValue
        : Battery?.RateMw is int mw ? $"{mw / 1000.0:0.0} W" : UnknownValue;

    public string DesignCapacityText => (Live?.DesignCapacityMWh ?? Battery?.DesignCapacityMWh) is int d
        ? $"{d:N0} mWh"
        : UnknownValue;

    public string FullChargeCapacityText => (Live?.FullChargeCapacityMWh ?? Battery?.FullChargeCapacityMWh) is int f
        ? $"{f:N0} mWh"
        : UnknownValue;

    public string RemainingCapacityText => (Live?.RemainingCapacityMWh ?? Battery?.RemainingCapacityMWh) is int r
        ? $"{r:N0} mWh"
        : UnknownValue;

    public string EstimatedTimeText
    {
        get
        {
            if (MergedEstimatedMinutes is not int minutes)
            {
                return UnknownValue;
            }

            var span = TimeSpan.FromMinutes(minutes);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours} h {span.Minutes} min"
                : $"{span.Minutes} min";
        }
    }

    // Only the numbers, or the one fact the line above cannot express. The line
    // above already names the state and the power source, so repeating
    // "Discharging" or "not charging" here would say the same thing twice.
    public string RateLineText
    {
        get
        {
            var rate = Live?.RateWatts;

            switch (MergedState)
            {
                case BatteryState.Charging:
                    return rate is double chargeWatts ? $"+{chargeWatts:0.0} W" : string.Empty;

                case BatteryState.Discharging:
                    if (rate is not double dischargeWatts)
                    {
                        return string.Empty;
                    }

                    var line = $"\u2212{Math.Abs(dischargeWatts):0.0} W";
                    if (MergedEstimatedMinutes is int dischargeMinutes)
                    {
                        line += $" \u00B7 {FormatLeft(dischargeMinutes)} left";
                    }

                    return line;

                // AC with no flow: the limit is why it stopped, and that is the
                // one thing the state line cannot say. Without a confirmed limit
                // there is nothing honest to add, so the page collapses the line.
                case BatteryState.NotCharging when MergedPowerOnline == true:
                    return _snapshot?.LimitOn switch
                    {
                        true => "Holding at the 80% limit",
                        false => "Charging to 100%",
                        null => string.Empty
                    };

                default:
                    return string.Empty;
            }
        }
    }

    private static string FormatLeft(int minutes)
    {
        var span = TimeSpan.FromMinutes(Math.Max(0, minutes));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{span.Minutes}m";
    }

    public string HealthSummaryText => MergedWear is double wear
        ? $"{100.0 - wear:0.0}% of design capacity remains"
        : "Wear could not be read";

    // ---- Charge limit ----------------------------------------------------

    public bool IsLimitPending => _pendingLimitTarget is not null;

    // What the switch shows: the user's optimistic choice while pending,
    // otherwise the last confirmed state. This is what keeps the flip instant.
    public bool LimitDisplayValue => _pendingLimitTarget ?? _snapshot?.LimitOn ?? false;

    public bool LimitToggleEnabled =>
        !IsLimitPending && _snapshot?.Supported == true && _snapshot?.LimitOn is not null;

    public string LimitStateText
    {
        get
        {
            if (_pendingLimitTarget is bool target)
            {
                return target ? "Turning the limit on\u2026" : "Turning the limit off\u2026";
            }

            return _snapshot?.LimitOn switch
            {
                true => "Charging stops at 80%.",
                false => "The battery charges to 100%.",
                null when _snapshot?.Supported == false =>
                    "State unknown. The worker could not reach the firmware charge-limit interface; battery data above is still live.",
                null => "State unknown. The worker has not reported the limit yet.",
            };
        }
    }

    public string? LimitMessage
    {
        get => _limitMessage;
        private set
        {
            if (SetProperty(ref _limitMessage, value))
            {
                RaisePropertyChanged(nameof(HasLimitMessage));
            }
        }
    }

    public bool HasLimitMessage => _limitMessage is not null;

    public bool LimitMessageIsError
    {
        get => _limitMessageIsError;
        private set => SetProperty(ref _limitMessageIsError, value);
    }

    public string SavedLimitText => _snapshot?.Desired switch
    {
        true => "On (80%)",
        false => "Off (100%)",
        null => "Unknown"
    };

    // ---- GPU --------------------------------------------------------------

    public bool IsGpuPending => _pendingGpuTarget is not null;

    public bool GpuPresent => _snapshot?.Gpu?.Present == true;

    // What the switch shows: the user's optimistic choice while pending,
    // otherwise the last confirmed state.
    public bool GpuDisplayValue => _pendingGpuTarget ?? _snapshot?.Gpu?.Enabled ?? false;

    public bool GpuToggleEnabled =>
        !IsGpuPending && _snapshot?.Gpu?.Present == true &&
        _snapshot?.Gpu?.Enabled is not null && _snapshot?.Gpu?.DrivingDisplay != true;

    public bool ShowGpuRetry =>
        GpuPresent && _snapshot?.Gpu?.Enabled is null && _pendingGpuTarget is null;

    public string GpuStateText
    {
        get
        {
            if (_pendingGpuTarget is bool target)
            {
                return target ? "Turning the GPU on\u2026" : "Turning the GPU off\u2026";
            }

            var gpu = _snapshot?.Gpu;
            if (gpu is null || gpu.Present != true)
            {
                return "State unknown. The worker has not reported the GPU.";
            }

            if (gpu.Enabled is null)
            {
                return "State unknown. The worker could not read the GPU state.";
            }

            if (gpu.DrivingDisplay == true)
            {
                return "On. This GPU is driving a display, so it cannot be turned off.";
            }

            if (gpu.Enabled == true)
            {
                return "On. Available to apps that need it.";
            }

            return gpu.ProblemCode is int code && code != 22
                ? $"Off. The device reports a problem (code {code})."
                : "Off. Apps use the Intel GPU. Screens wired to the GPU stay dark.";
        }
    }

    public string? GpuMessage
    {
        get => _gpuMessage;
        private set
        {
            if (SetProperty(ref _gpuMessage, value))
            {
                RaisePropertyChanged(nameof(HasGpuMessage));
            }
        }
    }

    public bool HasGpuMessage => _gpuMessage is not null;

    public bool GpuMessageIsError
    {
        get => _gpuMessageIsError;
        private set => SetProperty(ref _gpuMessageIsError, value);
    }

    public void DismissGpuMessage() => GpuMessage = null;

    // A guard refusal is the safety design working, not a failure, so it is a
    // warning carrying the worker's own words.
    public async Task RequestGpuChangeAsync(bool target)
    {
        if (_pendingGpuTarget is not null || target == _snapshot?.Gpu?.Enabled)
        {
            return;
        }

        _pendingGpuTarget = target;
        GpuMessage = null;
        RaiseAllPropertiesChanged();

        try
        {
            await _client.SendCommandAsync(target ? "gpu:on" : "gpu:off", _shutdownTokenSource.Token);
            var snapshot = await _client.GetSnapshotAsync(_shutdownTokenSource.Token);
            ApplySnapshot(snapshot, persist: true);

            if (snapshot.Gpu?.Enabled == target)
            {
                GpuMessage = null;
            }
            else
            {
                GpuMessageIsError = false;
                GpuMessage = "The change was sent, but the worker did not confirm it. The switch shows the last confirmed state.";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (BattCtlException exception) when (exception.Error == BattCtlError.GpuRefused)
        {
            GpuMessageIsError = false;
            GpuMessage = exception.Message;
        }
        catch (BattCtlException exception)
        {
            GpuMessageIsError = true;
            GpuMessage = FriendlyGpuMessage(exception);
        }
        finally
        {
            _pendingGpuTarget = null;
            RaiseAllPropertiesChanged();
        }
    }

    // ---- Settings ---------------------------------------------------------

    public bool ReapplyPending
    {
        get => _reapplyPending;
        private set
        {
            if (SetProperty(ref _reapplyPending, value))
            {
                RaisePropertyChanged(nameof(CanReapply));
            }
        }
    }

    public bool CanReapply => !ReapplyPending && _snapshot?.Desired is not null;

    public string? ReapplyMessage
    {
        get => _reapplyMessage;
        private set
        {
            if (SetProperty(ref _reapplyMessage, value))
            {
                RaisePropertyChanged(nameof(HasReapplyMessage));
            }
        }
    }

    public bool HasReapplyMessage => _reapplyMessage is not null;

    public bool ReapplySucceeded
    {
        get => _reapplySucceeded;
        private set => SetProperty(ref _reapplySucceeded, value);
    }

    public IReadOnlyList<string> RestoreLogLines
    {
        get
        {
            var log = _snapshot?.RestoreLog;
            if (log is null || log.Count == 0)
            {
                return Array.Empty<string>();
            }

            // The contract delivers oldest first; the viewer shows newest first.
            return log.AsEnumerable().Reverse().Select(FormatLogEntry).ToList();
        }
    }

    public bool HasRestoreLog => RestoreLogLines.Count > 0;

    public bool ShowLimitRetry =>
        HasSnapshot && _snapshot?.LimitOn is null && _pendingLimitTarget is null;

    public void DismissLimitMessage() => LimitMessage = null;

    public void DismissError() => ErrorText = null;

    public void DismissReapply() => ReapplyMessage = null;

    public string TrayTooltip
    {
        get
        {
            var percent = MergedPercent;
            if (percent is null && _snapshot?.Battery is not { } battery)
            {
                return "Battery Charge Limit";
            }

            var limit = _snapshot?.LimitOn switch
            {
                true => "limit on",
                false => "limit off",
                null => "limit unknown"
            };

            return $"{percent?.ToString() ?? UnknownValue}% \u00B7 {StateText.ToLowerInvariant()} \u00B7 {limit}";
        }
    }

    // ---- Lifecycle --------------------------------------------------------

    // Paints the cached snapshot synchronously, then refreshes in the background.
    public async Task InitializeAsync()
    {
        var cached = _cache.Load();
        if (cached is not null)
        {
            ApplySnapshot(cached.Snapshot, persist: false, asOf: cached.SavedAt);
        }

        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_refreshInFlight || _shutdownTokenSource.IsCancellationRequested)
        {
            return;
        }

        _refreshInFlight = true;
        try
        {
            var snapshot = await _client.GetSnapshotAsync(_shutdownTokenSource.Token);
            ApplySnapshot(snapshot, persist: true);
            ErrorText = null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (BattCtlException exception) when (exception.Error == BattCtlError.MailboxBusy)
        {
            // Transient contention with a CLI call. Keep whatever is on screen and
            // stay quiet; the next poll retries. Turning this into a banner would
            // make a normal overlap look like a failure.
        }
        catch (BattCtlException exception)
        {
            ErrorText = FriendlyMessage(exception);
        }
        catch (Exception exception)
        {
            ErrorText = $"Unexpected error: {exception.Message}";
        }
        finally
        {
            _refreshInFlight = false;
            RaiseAllPropertiesChanged();
        }
    }

    // Optimistic toggle: the switch has already moved when this is called.
    public async Task RequestLimitChangeAsync(bool target)
    {
        // Programmatic sync from the binding lands here too; ignore echoes and
        // anything that arrives while a round trip is in flight.
        if (_pendingLimitTarget is not null || target == _snapshot?.LimitOn)
        {
            return;
        }

        _pendingLimitTarget = target;
        LimitMessage = null;
        RaiseAllPropertiesChanged();

        try
        {
            await _client.SendCommandAsync(target ? "on" : "off", _shutdownTokenSource.Token);
            var snapshot = await _client.GetSnapshotAsync(_shutdownTokenSource.Token);
            ApplySnapshot(snapshot, persist: true);

            if (snapshot.LimitOn == target)
            {
                LimitMessage = null;
            }
            else
            {
                LimitMessageIsError = false;
                LimitMessage = "The change was sent, but the worker did not confirm it. The switch shows the last confirmed state.";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (BattCtlException exception)
        {
            LimitMessageIsError = true;
            LimitMessage = FriendlyMessage(exception);
        }
        finally
        {
            // Clearing the pending target rebinds the switch to the confirmed
            // state, which animates it back on disagreement.
            _pendingLimitTarget = null;
            RaiseAllPropertiesChanged();
        }
    }

    public async Task ToggleLimitFromTrayAsync()
    {
        if (_pendingLimitTarget is not null || _snapshot?.Supported != true || _snapshot.LimitOn is null)
        {
            return;
        }

        await RequestLimitChangeAsync(!_snapshot.LimitOn.Value);
    }

    // Re-asserts the saved (desired) state. The IPC client only exposes
    // on/off/toggle/status, so re-apply sends the desired state explicitly.
    public async Task ReapplySavedLimitAsync()
    {
        if (_snapshot?.Desired is not bool desired || ReapplyPending)
        {
            return;
        }

        ReapplyPending = true;
        ReapplyMessage = null;
        try
        {
            await _client.SendCommandAsync(desired ? "on" : "off", _shutdownTokenSource.Token);
            var snapshot = await _client.GetSnapshotAsync(_shutdownTokenSource.Token);
            ApplySnapshot(snapshot, persist: true);

            ReapplySucceeded = snapshot.LimitOn == desired;
            ReapplyMessage = ReapplySucceeded
                ? $"Saved limit re-applied: {(desired ? "on" : "off")}."
                : "The command was sent, but the worker did not confirm the saved state.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (BattCtlException exception)
        {
            ReapplySucceeded = false;
            ReapplyMessage = FriendlyMessage(exception);
        }
        finally
        {
            ReapplyPending = false;
            RaiseAllPropertiesChanged();
        }
    }

    public string GetStatusJson()
    {
        return _snapshot is null
            ? "{ }"
            : JsonSerializer.Serialize(_snapshot, new JsonSerializerOptions { WriteIndented = true });
    }

    public void Shutdown()
    {
        _shutdownTokenSource.Cancel();
    }

    private void ApplySnapshot(Snapshot snapshot, bool persist, DateTime? asOf = null)
    {
        Snapshot = snapshot;
        if (persist)
        {
            _cache.Save(snapshot);
        }

        ValuesUpdated?.Invoke();
        RaiseAllPropertiesChanged();
    }

    // Pages use this to cross-fade value text that actually changed.
    public event Action? ValuesUpdated;

    private static string FormatLogEntry(RestoreLogEntry entry)
    {
        var time = DateTime.TryParse(entry.At, out var at) ? at.ToString("HH:mm:ss") : entry.At ?? string.Empty;
        return $"{time}  {entry.Message}";
    }

    private static string FriendlyMessage(BattCtlException exception)
    {
        return exception.Error switch
        {
            BattCtlError.NotInstalled => "The BattCtl service is not installed on this machine.",
            BattCtlError.TaskStartFailed => "The BattCtl scheduled task could not be started.",
            BattCtlError.Timeout => "The BattCtl service did not answer in time.",
            BattCtlError.Unsupported => "This firmware does not expose a charge limit.",
            BattCtlError.ProviderFailure => "The battery provider returned an error.",
            // Transient: another battctl process holds the mailbox. Say nothing
            // alarming \u2014 the next refresh will pick it up.
            BattCtlError.MailboxBusy => "BattCtl is busy with another request; this reading may be a moment old.",
            // The deployed worker predates the snapshot command; say so plainly.
            BattCtlError.BadRequest when exception.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase) =>
                "The installed BattCtl worker is outdated and does not know the snapshot command. Re-run the installer to update it.",
            _ => exception.Message
        };
    }

    private static string FriendlyGpuMessage(BattCtlException exception)
    {
        return exception.Error switch
        {
            // The battery-specific "Unsupported" text does not fit here.
            BattCtlError.Unsupported => "No controllable dedicated GPU was found on this machine.",
            BattCtlError.NotInstalled => "The BattCtl service is not installed on this machine.",
            BattCtlError.TaskStartFailed => "The BattCtl scheduled task could not be started.",
            BattCtlError.Timeout => "The BattCtl service did not answer in time.",
            BattCtlError.ProviderFailure => "The GPU provider returned an error.",
            BattCtlError.MailboxBusy => "BattCtl is busy with another request; try again in a moment.",
            _ => exception.Message
        };
    }
}

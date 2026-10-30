using System.Text.Json;
using BattCtl.App.Services;

namespace BattCtl.App.ViewModels;

// Single source of truth for the three pages and the tray tooltip.
// Every worker round trip costs ~930ms, so: cached first paint, optimistic
// toggle, and reconciliation against the authoritative read-back.
public sealed class MainViewModel : ObservableObject
{
    private const string UnknownValue = "\u2014"; // em dash, only ever a data placeholder glyph

    private readonly BattCtlClient _client;
    private readonly SnapshotCache _cache;
    private readonly CancellationTokenSource _shutdownTokenSource = new();

    private Snapshot? _snapshot;
    private bool _refreshInFlight;
    private bool? _pendingLimitTarget;
    private bool _isInitialLoading;
    private bool _isRefreshing;
    private string? _errorText;
    private string? _limitMessage;
    private bool _limitMessageIsError;
    private DateTime _lastUpdated;
    private bool _reapplyPending;
    private string? _reapplyMessage;
    private bool _reapplySucceeded;

    public MainViewModel(BattCtlClient client, SnapshotCache cache)
    {
        _client = client;
        _cache = cache;
    }

    public CancellationToken ShutdownToken => _shutdownTokenSource.Token;

    // ---- Raw state ------------------------------------------------------

    public Snapshot? Snapshot
    {
        get => _snapshot;
        private set => SetProperty(ref _snapshot, value);
    }

    public bool HasSnapshot => _snapshot is not null;

    public bool IsInitialLoading
    {
        get => _isInitialLoading;
        private set => SetProperty(ref _isInitialLoading, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

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

    public string LastUpdatedText => _lastUpdated == default
        ? string.Empty
        : $"Updated {_lastUpdated:HH:mm:ss}";

    // Which top-level surface a page should show.
    public bool ShowSkeleton => !HasSnapshot && IsInitialLoading;
    public bool ShowContent => HasSnapshot;
    public bool ShowLoadError => !HasSnapshot && !IsInitialLoading && HasError;

    // ---- Battery projections ---------------------------------------------

    private BatteryInfo? Battery => _snapshot?.Battery;

    public string PercentText => Battery?.Percent?.ToString() ?? UnknownValue;
    public bool HasPercent => Battery?.Percent is not null;

    public string StateText => Battery?.State switch
    {
        "Charging" => "Charging",
        "Discharging" => "Discharging",
        "NotCharging" => "Not charging",
        _ => "Unknown"
    };

    public string PowerText => Battery?.PowerOnline switch
    {
        true => "AC connected",
        false => "On battery",
        null => UnknownValue
    };

    public string StateSummaryText
    {
        get
        {
            if (Battery?.State is null && Battery?.PowerOnline is null)
            {
                return UnknownValue;
            }

            var state = Battery?.State is null ? null : StateText;
            var power = Battery?.PowerOnline is null ? null : PowerText;
            return string.Join(" \u00B7 ", new[] { state, power }.Where(s => s is not null));
        }
    }

    public string WearText => Battery?.WearPercent is double wear ? $"{wear:0.0}%" : UnknownValue;

    public string HealthText => Battery?.WearPercent is double wear ? $"{100.0 - wear:0.0}%" : UnknownValue;

    public double HealthValue => Battery?.WearPercent is double wear ? Math.Clamp(100.0 - wear, 0, 100) : 0;

    public bool HasWear => Battery?.WearPercent is not null;

    public string CycleCountText => Battery?.CycleCount?.ToString() ?? UnknownValue;

    public string VoltageText => Battery?.VoltageMv is int mv ? $"{mv / 1000.0:0.00} V" : UnknownValue;

    public string RateText => Battery?.RateMw is int mw ? $"{mw / 1000.0:0.0} W" : UnknownValue;

    public string DesignCapacityText => Battery?.DesignCapacityMWh is int d ? $"{d:N0} mWh" : UnknownValue;

    public string FullChargeCapacityText => Battery?.FullChargeCapacityMWh is int f ? $"{f:N0} mWh" : UnknownValue;

    public string RemainingCapacityText => Battery?.RemainingCapacityMWh is int r ? $"{r:N0} mWh" : UnknownValue;

    public string EstimatedTimeText
    {
        get
        {
            if (Battery?.EstimatedMinutes is not int minutes)
            {
                return UnknownValue;
            }

            var span = TimeSpan.FromMinutes(minutes);
            return span.TotalHours >= 1
                ? $"{(int)span.TotalHours} h {span.Minutes} min"
                : $"{span.Minutes} min";
        }
    }

    public string HealthSummaryText => Battery?.WearPercent is double wear
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
            if (_snapshot?.Battery is not { } battery)
            {
                return "Battery Charge Limit";
            }

            var limit = _snapshot.LimitOn switch
            {
                true => "limit on",
                false => "limit off",
                null => "limit unknown"
            };

            var percent = battery.Percent?.ToString() ?? UnknownValue;
            return $"{percent}% \u00B7 {StateText.ToLowerInvariant()} \u00B7 {limit}";
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
        else
        {
            IsInitialLoading = true;
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
        IsRefreshing = true;
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
            IsRefreshing = false;
            IsInitialLoading = false;
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
        _lastUpdated = asOf ?? DateTime.Now;
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
            // alarming — the next refresh will pick it up.
            BattCtlError.MailboxBusy => "BattCtl is busy with another request; this reading may be a moment old.",
            // The deployed worker predates the snapshot command; say so plainly.
            BattCtlError.BadRequest when exception.Message.Contains("malformed", StringComparison.OrdinalIgnoreCase) =>
                "The installed BattCtl worker is outdated and does not know the snapshot command. Re-run the installer to update it.",
            _ => exception.Message
        };
    }
}

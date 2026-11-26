using BattCtl.App.Services;

namespace BattCtl.App.ViewModels;

// Read-only history state for the History page. The page binds properties and
// paints the chart imperatively on ViewChanged (the ValuesUpdated pattern).
// Every public method must be called on the UI thread; without
// ConfigureAwait(false) the continuations stay there, so property updates and
// ViewChanged are always safe.
public sealed class HistoryViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan CacheWindow = TimeSpan.FromDays(90);

    // Summary shape before the first load completes: unknown tokens, no
    // fabricated zeros.
    private static readonly HistorySummary EmptySummary = new()
    {
        OnBatteryText = HistoryAnalysis.UnknownToken,
        OnAcText = HistoryAnalysis.UnknownToken,
        LowestLevelText = HistoryAnalysis.UnknownToken,
        EnergyUsedText = HistoryAnalysis.UnknownToken,
        EnergyEstimated = false,
        CoverageText = string.Empty,
        HasGaps = false,
    };

    private readonly HistoryStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _disposal = new();

    private IReadOnlyList<HistorySample> _cache = Array.Empty<HistorySample>();
    private (DateTime LastWriteUtc, long TotalBytes)? _stamp;
    private bool _cacheLoaded;
    private HistoryRange _appliedRange = (HistoryRange)(-1);
    private int _skippedLines;
    private DateTimeOffset? _earliestAvailable;
    private string? _loadError;
    private bool _disposed;

    private HistoryRange _selectedRange = HistoryRange.Day;
    private bool _isLoading;
    private HistoryView? _view;

    public HistoryViewModel(HistoryStore store)
    {
        _store = store;
    }

    public event EventHandler? ViewChanged;

    public HistoryRange SelectedRange
    {
        get => _selectedRange;
        private set => SetProperty(ref _selectedRange, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool HasData { get; private set; }
    public bool HasError { get; private set; }
    public bool IsEmpty { get; private set; }
    public bool IsRangeEmpty { get; private set; }

    // Malformed lines were skipped but usable samples were read; the page shows
    // the "incomplete" note. Distinct from HasError (nothing usable / IO failed).
    public bool IsPartialParse { get; private set; }

    public string? ErrorMessage { get; private set; }

    // Set only when the selected range is empty but older history exists.
    public string EarliestDataText { get; private set; } = string.Empty;

    public HistoryView? View => _view;

    public IReadOnlyList<HistorySession> Sessions => _view?.Sessions ?? Array.Empty<HistorySession>();

    public HistorySummary Summary => _view?.Summary ?? EmptySummary;

    // First call after page load: read the 90-day window once, then apply the
    // selected range. Later calls with an unchanged store stamp and unchanged
    // range are no-ops.
    public Task EnsureLoadedAsync() => RunAsync(forceReload: false, alwaysReapply: false);

    // Range switch: recompute from the cache; touch the disk only when the
    // stamp says the sampler wrote since the last read.
    public Task SetRangeAsync(HistoryRange range)
    {
        if (_disposed || range == SelectedRange)
        {
            return Task.CompletedTask;
        }

        SelectedRange = range;
        return RunAsync(forceReload: false, alwaysReapply: true);
    }

    // "Check again": force a full re-read even when the stamp is unchanged.
    public Task RefreshAsync() => RunAsync(forceReload: true, alwaysReapply: true);

    // Window activation: reload only when the stamp changed; otherwise nothing.
    public Task RefreshFromStampAsync() => RunAsync(forceReload: false, alwaysReapply: false);

    private async Task RunAsync(bool forceReload, bool alwaysReapply)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token);
            var cancellationToken = linked.Token;

            await _gate.WaitAsync(cancellationToken);
            try
            {
                var stamp = _store.GetStamp();
                var rangeChanged = _appliedRange != _selectedRange || _view is null;
                var reloaded = false;

                var upToDate = _cacheLoaded && stamp == _stamp && !rangeChanged;
                if (upToDate && !forceReload && !alwaysReapply)
                {
                    return;
                }

                if (forceReload || !_cacheLoaded || stamp != _stamp)
                {
                    await LoadCacheAsync(stamp, cancellationToken);
                    reloaded = true;
                }

                if (reloaded || alwaysReapply || rangeChanged)
                {
                    ApplyRange();
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed mid-load: nothing to paint.
        }
        catch (Exception)
        {
            // The history page must never take the app down; leave the last
            // good view on screen. A load error is reported through the store
            // result, not an exception.
        }
    }

    private async Task LoadCacheAsync((DateTime LastWriteUtc, long TotalBytes)? stamp, CancellationToken cancellationToken)
    {
        var to = DateTimeOffset.Now;
        var from = to - CacheWindow;

        IsLoading = true;
        try
        {
            var result = await _store.LoadAsync(from, to, cancellationToken);

            _cache = result.Samples;
            _stamp = stamp;
            _skippedLines = result.SkippedLines;
            _earliestAvailable = result.EarliestAvailable;
            _loadError = result.DirectoryMissing ? null : result.ErrorMessage;
            _cacheLoaded = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyRange()
    {
        var to = DateTimeOffset.Now;
        var from = to - WindowSpan(SelectedRange);

        var inRange = new List<HistorySample>();
        foreach (var sample in _cache)
        {
            if (sample.Timestamp is DateTimeOffset timestamp && timestamp >= from && timestamp <= to)
            {
                inRange.Add(sample);
            }
        }

        _view = HistoryAnalysis.BuildView(SelectedRange, inRange, from, to);
        _appliedRange = SelectedRange;

        var cacheHasSamples = _cache.Count > 0;
        HasData = cacheHasSamples;
        IsEmpty = _cache.Count == 0 && _loadError is null;
        IsRangeEmpty = cacheHasSamples && inRange.Count == 0;
        IsPartialParse = _skippedLines > 0 && cacheHasSamples;
        HasError = _loadError is not null;
        ErrorMessage = _loadError;
        EarliestDataText = IsRangeEmpty && _earliestAvailable is DateTimeOffset earliest
            ? $"History begins {earliest.ToLocalTime():MMM d, HH:mm}."
            : string.Empty;

        RaiseAllPropertiesChanged();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private static TimeSpan WindowSpan(HistoryRange range) => range switch
    {
        HistoryRange.Day => TimeSpan.FromHours(24),
        HistoryRange.Week => TimeSpan.FromDays(7),
        HistoryRange.Month => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(90),
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposal.Cancel();
        _disposal.Dispose();
    }
}

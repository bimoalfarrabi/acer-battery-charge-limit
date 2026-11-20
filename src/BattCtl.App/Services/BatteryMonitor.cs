using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Windows.Devices.Power;
using Windows.System.Power;

namespace BattCtl.App.Services;

public enum BatteryState
{
    Unknown,
    Charging,
    Discharging,
    NotCharging,
}

public sealed class BatteryReading
{
    public int? Percent { get; init; }
    public BatteryState State { get; init; }
    public bool? PowerOnline { get; init; }
    public double? RateWatts { get; init; }
    public int? RemainingCapacityMWh { get; init; }
    public int? FullChargeCapacityMWh { get; init; }
    public int? DesignCapacityMWh { get; init; }
    public double? WearPercent { get; init; }
    public int? EstimatedMinutes { get; init; }
    public bool HasValue { get; init; }
}

// Realtime battery telemetry, read straight from the OS (no worker round trip).
// PowerManager events fire on background threads; every ReadingChanged raise is
// coalesced and hopped onto the DispatcherQueue the VM was built with.
public sealed class BatteryMonitor : IDisposable
{
    private static readonly TimeSpan WallGapThreshold = TimeSpan.FromSeconds(5);

    private readonly DispatcherQueue _dispatcher;

    // Held in a field: if the Battery object is collected, the WinRT event
    // registration for ReportUpdated is silently dropped with it.
    private readonly Battery? _battery;
    private DispatcherQueueTimer? _timer;
    private bool _sampling;
    private bool _dispatchPending;
    private bool _disposed;

    // Sleep detection: wall clock and stopwatch stamped at each sample. If the
    // wall clock jumped while the stopwatch did not, the machine slept.
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private TimeSpan _lastStopwatch = TimeSpan.Zero;
    private DateTime _lastWall = DateTime.UtcNow;

    public BatteryMonitor(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        try
        {
            _battery = Battery.AggregateBattery;
        }
        catch
        {
            _battery = null;
        }

        Current = new BatteryReading();
    }

    public BatteryReading Current { get; private set; }

    // Always raised on the UI thread captured in the constructor.
    public event EventHandler<BatteryReading>? ReadingChanged;

    // Subscribes the OS events and creates the 1 Hz staleness timer. The timer
    // runs until SetSampling(false) stops it, so the dashboard is live from
    // startup even before the window reports its first visibility state.
    public void Start()
    {
        PowerManager.RemainingChargePercentChanged += OnPowerManagerChanged;
        PowerManager.BatteryStatusChanged += OnPowerManagerChanged;
        PowerManager.PowerSupplyStatusChanged += OnPowerManagerChanged;
        PowerManager.RemainingDischargeTimeChanged += OnPowerManagerChanged;
        if (_battery is not null)
        {
            _battery.ReportUpdated += OnReportUpdated;
        }

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.IsRepeating = true;
        _timer.Tick += OnTimerTick;
        if (_sampling)
        {
            _timer.Start();
        }

        // Paint the first reading without waiting for an event or a tick.
        RequestDispatch();
    }

    // Window visible && the battery page is the one on screen.
    public void SetSampling(bool enabled)
    {
        _sampling = enabled;
        if (_timer is null)
        {
            return;
        }

        if (enabled && !_timer.IsRunning)
        {
            _timer.Start();
        }
        else if (!enabled && _timer.IsRunning)
        {
            _timer.Stop();
        }
    }

    // Forces an immediate read and raises even when nothing changed. Must be
    // called on the UI thread (window Activated, resume reconcile).
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        ReadAndRaise(force: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            PowerManager.RemainingChargePercentChanged -= OnPowerManagerChanged;
            PowerManager.BatteryStatusChanged -= OnPowerManagerChanged;
            PowerManager.PowerSupplyStatusChanged -= OnPowerManagerChanged;
            PowerManager.RemainingDischargeTimeChanged -= OnPowerManagerChanged;
        }
        catch
        {
        }

        if (_battery is not null)
        {
            try
            {
                _battery.ReportUpdated -= OnReportUpdated;
            }
            catch
            {
            }
        }

        if (_timer is not null)
        {
            try
            {
                _timer.Tick -= OnTimerTick;
                _timer.Stop();
            }
            catch
            {
            }
        }
    }

    private void OnPowerManagerChanged(object? sender, object args) => RequestDispatch();

    private void OnReportUpdated(Battery sender, object args) => RequestDispatch();

    private void OnTimerTick(DispatcherQueueTimer sender, object args)
    {
        var stopGap = _stopwatch.Elapsed - _lastStopwatch;
        var wallGap = DateTime.UtcNow - _lastWall;
        _lastStopwatch = _stopwatch.Elapsed;
        _lastWall = DateTime.UtcNow;

        // Wall clock jumped while the stopwatch barely advanced: the machine
        // slept between ticks. Force a fresh read and raise unconditionally,
        // because a percent that changed during sleep must still repaint.
        if (wallGap >= WallGapThreshold && stopGap <= TimeSpan.FromSeconds(2))
        {
            Refresh();
            return;
        }

        // Bounds worst-case staleness if the OS events are quiet.
        RequestDispatch();
    }

    private void RequestDispatch()
    {
        if (_disposed || _dispatchPending)
        {
            return;
        }

        _dispatchPending = true;
        if (!_dispatcher.TryEnqueue(() =>
            {
                _dispatchPending = false;
                ReadAndRaise(force: false);
            }))
        {
            _dispatchPending = false;
        }
    }

    // Runs on the UI thread. Reads fresh (never a captured stale value) and
    // raises only when something at display granularity actually changed.
    private void ReadAndRaise(bool force)
    {
        if (_disposed)
        {
            return;
        }

        var reading = Read();
        if (force || Changed(reading, Current))
        {
            Current = reading;
            ReadingChanged?.Invoke(this, reading);
        }
    }

    private static BatteryReading Read()
    {
        try
        {
            BatteryReport? report = null;
            try
            {
                report = Battery.AggregateBattery.GetReport();
            }
            catch
            {
                report = null;
            }

            int? percent = null;
            try
            {
                var pmPercent = PowerManager.RemainingChargePercent;
                if (pmPercent is >= 0 and <= 100)
                {
                    percent = pmPercent;
                }
            }
            catch
            {
                // PowerManager unavailable: fall back to the report ratio.
                if (report?.FullChargeCapacityInMilliwattHours is int fullCapacity && fullCapacity > 0 &&
                    report.RemainingCapacityInMilliwattHours is int remaining && remaining >= 0)
                {
                    percent = Math.Clamp((int)Math.Round(remaining * 100.0 / fullCapacity), 0, 100);
                }
            }

            var status = report?.Status;
            var state = status switch
            {
                BatteryStatus.Charging => BatteryState.Charging,
                BatteryStatus.Discharging => BatteryState.Discharging,
                BatteryStatus.Idle => BatteryState.NotCharging,
                _ => BatteryState.Unknown
            };

            bool? powerOnline;
            try
            {
                powerOnline = PowerManager.PowerSupplyStatus switch
                {
                    PowerSupplyStatus.NotPresent => null,
                    PowerSupplyStatus.Adequate => true,
                    PowerSupplyStatus.Inadequate => true,
                    _ => false // NotRequired
                };
            }
            catch
            {
                powerOnline = null;
            }

            // Real zero is a real zero (Idle on AC reports 0 mW); null is unknown.
            double? rateWatts = report?.ChargeRateInMilliwatts is int milliwatts
                ? milliwatts / 1000.0
                : null;

            int? estimatedMinutes = null;
            if (state == BatteryState.Discharging)
            {
                try
                {
                    var dischargeTime = PowerManager.RemainingDischargeTime;
                    // TimeSpan.MaxValue is the OS "unknown" sentinel, not zero.
                    if (dischargeTime != TimeSpan.MaxValue && dischargeTime <= TimeSpan.FromDays(365))
                    {
                        estimatedMinutes = (int)Math.Round(dischargeTime.TotalMinutes);
                    }
                }
                catch
                {
                }
            }

            int? design = report?.DesignCapacityInMilliwattHours;
            int? fullCharge = report?.FullChargeCapacityInMilliwattHours;
            int? remainingCapacity = report?.RemainingCapacityInMilliwattHours;

            // design == full means the controller reports a fresh pack; a
            // confident "0% wear" would be a lie, so leave it unknown instead.
            double? wear = design is int d && fullCharge is int f && d != 0 && f != 0 && d != f
                ? Math.Round((1.0 - (double)f / d) * 100.0, 1)
                : null;

            return new BatteryReading
            {
                Percent = percent,
                State = state,
                PowerOnline = powerOnline,
                RateWatts = rateWatts,
                RemainingCapacityMWh = remainingCapacity,
                FullChargeCapacityMWh = fullCharge,
                DesignCapacityMWh = design,
                WearPercent = wear,
                EstimatedMinutes = estimatedMinutes,
                HasValue = true,
            };
        }
        catch
        {
            // A battery API must never take the app down; degrade and retry on
            // the next event or tick.
            return new BatteryReading
            {
                Percent = null,
                State = BatteryState.Unknown,
                HasValue = false,
            };
        }
    }

    // Compares at displayed granularity only: integer percent, state, power,
    // rate to one decimal, remaining mWh, minutes, wear to one decimal.
    private static bool Changed(BatteryReading candidate, BatteryReading current)
    {
        return candidate.HasValue != current.HasValue
            || candidate.Percent != current.Percent
            || candidate.State != current.State
            || candidate.PowerOnline != current.PowerOnline
            || Round1(candidate.RateWatts) != Round1(current.RateWatts)
            || candidate.RemainingCapacityMWh != current.RemainingCapacityMWh
            || candidate.EstimatedMinutes != current.EstimatedMinutes
            || Round1(candidate.WearPercent) != Round1(current.WearPercent);
    }

    private static double? Round1(double? value) => value is double v ? Math.Round(v, 1) : null;
}

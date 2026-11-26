using BattCtl.App.Services;
using BattCtl.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace BattCtl.App.Views;

// Idle and unknown session kinds read as secondary ink so charging and
// discharging, the two kinds that move the level, carry the primary ink.
public sealed class HistoryKindBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var primary = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        return value is HistorySessionKind kind
            && kind is HistorySessionKind.Charging or HistorySessionKind.Discharging
            ? primary
            : secondary;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotSupportedException();
    }
}

// History page: the chart and tiles are painted imperatively on ViewChanged
// (the ValuesUpdated pattern), because the view object is replaced on each
// rebuild. Simple flags stay on x:Bind. No live tier: the sampler is the
// installed background task, not the app.
public sealed partial class HistoryPage : Page
{
    private bool _initializingRange;
    private string _latestReadout = string.Empty;

    public HistoryPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public HistoryViewModel ViewModel => App.History;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ViewChanged += OnViewChanged;
        InitRangeSelector();

        // Always paint once: when the VM was loaded by an earlier visit the
        // cached view is applied without a new ViewChanged.
        await ViewModel.EnsureLoadedAsync();
        UpdateView();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ViewChanged -= OnViewChanged;
    }

    private void OnViewChanged(object? sender, EventArgs e)
    {
        UpdateView();
    }

    private void InitRangeSelector()
    {
        _initializingRange = true;
        try
        {
            DayRadio.IsChecked = ViewModel.SelectedRange == HistoryRange.Day;
            WeekRadio.IsChecked = ViewModel.SelectedRange == HistoryRange.Week;
            MonthRadio.IsChecked = ViewModel.SelectedRange == HistoryRange.Month;
            QuarterRadio.IsChecked = ViewModel.SelectedRange == HistoryRange.Quarter;
        }
        finally
        {
            _initializingRange = false;
        }
    }

    private async void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializingRange)
        {
            return;
        }

        var range = ReferenceEquals(sender, DayRadio) ? HistoryRange.Day
            : ReferenceEquals(sender, WeekRadio) ? HistoryRange.Week
            : ReferenceEquals(sender, MonthRadio) ? HistoryRange.Month
            : HistoryRange.Quarter;

        await ViewModel.SetRangeAsync(range);
        UpdateView();
    }

    private void UpdateView()
    {
        var view = ViewModel.View;
        var summary = ViewModel.Summary;
        var sampleCount = view?.Samples.Count ?? 0;

        FirstRunCard.Visibility = ViewModel.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        ContentRoot.Visibility = ViewModel.IsEmpty ? Visibility.Collapsed : Visibility.Visible;

        // Chart states: two or more samples draws, one shows the note, zero in
        // a non-empty store shows the range-empty note.
        var showChart = !ViewModel.IsRangeEmpty && sampleCount >= 2;
        Chart.Visibility = showChart ? Visibility.Visible : Visibility.Collapsed;
        OneSampleNote.Visibility = !ViewModel.IsRangeEmpty && sampleCount == 1 ? Visibility.Visible : Visibility.Collapsed;
        RangeEmptyNote.Visibility = ViewModel.IsRangeEmpty ? Visibility.Visible : Visibility.Collapsed;
        EarliestDataText.Text = ViewModel.EarliestDataText;

        TilesBlock.Visibility = ViewModel.IsRangeEmpty ? Visibility.Collapsed : Visibility.Visible;

        // Tiles: the summary object is rebuilt every time, so they are painted
        // here rather than bound.
        ValueFade.SetText(OnBatteryValue, summary.OnBatteryText);
        ValueFade.SetText(OnAcValue, summary.OnAcText);
        ValueFade.SetText(LowestValue, summary.LowestLevelText);
        ValueFade.SetText(EnergyValue, summary.EnergyUsedText);
        ValueFade.SetText(EnergyUsedLabel, summary.EnergyEstimated ? "Energy used (est.)" : "Energy used");

        CoverageText.Text = summary.HasGaps ? summary.CoverageText : string.Empty;
        CoverageText.Visibility = summary.HasGaps ? Visibility.Visible : Visibility.Collapsed;

        // Sessions.
        SessionsList.ItemsSource = ViewModel.Sessions;
        NoSessionsNote.Visibility = ViewModel.Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EnergyNote.Visibility = summary.EnergyEstimated ? Visibility.Visible : Visibility.Collapsed;

        // Readout and chart data. The chart's x window is computed here so the
        // newest sample sits at the right edge and trailing gaps stay visible.
        _latestReadout = view?.LatestReadoutText ?? string.Empty;
        ReadoutText.Text = _latestReadout;

        if (view is not null)
        {
            var to = DateTimeOffset.Now;
            var from = to - SpanFor(ViewModel.SelectedRange);
            Chart.SetData(view.Samples, ViewModel.SelectedRange, from, to, view.HasLimitGuide);
            LimitLegendItem.Visibility = view.HasLimitGuide && showChart
                ? Visibility.Visible
                : Visibility.Collapsed;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Chart, view.RangeAutomationText);
        }
        else
        {
            LimitLegendItem.Visibility = Visibility.Collapsed;
        }

        // Non-blocking messages, the existing InfoBar pattern.
        PartialParseBar.IsOpen = ViewModel.IsPartialParse && !ViewModel.IsEmpty;
        HistoryErrorBar.Message = ViewModel.ErrorMessage ?? "Could not read the history file.";
        HistoryErrorBar.IsOpen = ViewModel.HasError;
    }

    private static TimeSpan SpanFor(HistoryRange range) => range switch
    {
        HistoryRange.Day => TimeSpan.FromHours(24),
        HistoryRange.Week => TimeSpan.FromDays(7),
        HistoryRange.Month => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(90),
    };

    private void Chart_HoverChanged(object? sender, HistoryHover? hover)
    {
        if (hover?.Sample is { } sample && sample.Timestamp is DateTimeOffset timestamp)
        {
            ReadoutText.Text = FormatHover(sample, timestamp);
            return;
        }

        if (hover is { GapStart: DateTimeOffset start, GapEnd: DateTimeOffset end })
        {
            ReadoutText.Text = $"{start.ToLocalTime():ddd HH:mm} to {end.ToLocalTime():ddd HH:mm}, not recorded";
            return;
        }

        ReadoutText.Text = _latestReadout;
    }

    // Same voice as the latest readout: time, level, power, state. Commas,
    // no fabricated part: a null field simply does not appear.
    private static string FormatHover(HistorySample sample, DateTimeOffset timestamp)
    {
        var parts = new List<string>
        {
            timestamp.ToLocalTime().ToString("ddd HH:mm"),
            sample.Pct is int pct ? $"{pct}%" : HistoryAnalysis.UnknownToken,
        };

        if (sample.Ac is bool ac)
        {
            parts.Add(ac ? "on AC" : "on battery");
        }

        parts.Add(sample.State switch
        {
            "Charging" => "charging",
            "Discharging" => "discharging",
            "NotCharging" => "not charging",
            _ => "unknown state",
        });

        return string.Join(", ", parts);
    }

    private void CheckAgain_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.RefreshAsync();
    }

    private void PartialParseBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        PartialParseBar.IsOpen = false;
    }

    private void HistoryErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        HistoryErrorBar.IsOpen = false;
    }
}

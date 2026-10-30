using BattCtl.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace BattCtl.App.Views;

public sealed partial class DashboardPage : Page
{
    private Storyboard? _skeletonPulse;

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public MainViewModel ViewModel => App.ViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ValuesUpdated += UpdateValues;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateValues();
        SyncInfoBars();
        SyncSkeleton();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ValuesUpdated -= UpdateValues;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _skeletonPulse?.Stop();
    }

    private void UpdateValues()
    {
        ValueFade.SetRunText(PercentRun, PercentBlock, ViewModel.PercentText);
        ValueFade.SetText(StateSummary, ViewModel.StateSummaryText);
        ValueFade.SetText(LimitState, ViewModel.LimitStateText);
        ValueFade.SetText(HealthValue, ViewModel.HealthText);
        ValueFade.SetText(CyclesValue, ViewModel.CycleCountText);
        ValueFade.SetText(VoltageValue, ViewModel.VoltageText);
        UpdatedCaption.Text = ViewModel.LastUpdatedText;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("" or nameof(MainViewModel.HasLimitMessage)
            or nameof(MainViewModel.HasError) or nameof(MainViewModel.ShowSkeleton)
            or nameof(MainViewModel.LimitMessageIsError)))
        {
            return;
        }

        SyncInfoBars();
        SyncSkeleton();
    }

    private void SyncInfoBars()
    {
        LimitInfoBar.Severity = ViewModel.LimitMessageIsError
            ? InfoBarSeverity.Error
            : InfoBarSeverity.Warning;
        LimitInfoBar.Message = ViewModel.LimitMessage ?? string.Empty;
        LimitInfoBar.IsOpen = ViewModel.HasLimitMessage;

        RefreshInfoBar.Message = ViewModel.ErrorText ?? string.Empty;
        RefreshInfoBar.IsOpen = ViewModel.HasError && ViewModel.HasSnapshot;

        // An unknown limit must not read as "Off": no label when unconfirmed.
        var known = ViewModel.Snapshot?.LimitOn is not null;
        LimitSwitch.OnContent = known ? "On" : string.Empty;
        LimitSwitch.OffContent = known ? "Off" : string.Empty;
    }

    // A gentle pulse marks the first-ever load; it only runs while the
    // skeleton is on screen, which is one worker round trip, once.
    private void SyncSkeleton()
    {
        if (ViewModel.ShowSkeleton && SkeletonPanel.XamlRoot is not null)
        {
            if (_skeletonPulse is null)
            {
                var animation = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.45,
                    Duration = new Duration(TimeSpan.FromMilliseconds(900)),
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                Storyboard.SetTarget(animation, SkeletonPanel);
                Storyboard.SetTargetProperty(animation, nameof(Opacity));
                _skeletonPulse = new Storyboard();
                _skeletonPulse.Children.Add(animation);
            }

            _skeletonPulse.Begin();
        }
        else
        {
            _skeletonPulse?.Stop();
        }
    }

    private void LimitSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsLimitPending)
        {
            return;
        }

        _ = ViewModel.RequestLimitChangeAsync(LimitSwitch.IsOn);
    }

    private void CheckAgain_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.RefreshAsync();
    }

    private void TryAgain_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.RefreshAsync();
    }

    private void LimitInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        ViewModel.DismissLimitMessage();
    }

    private void RefreshInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        ViewModel.DismissError();
    }
}

using BattCtl.App.Services;
using BattCtl.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BattCtl.App.Views;

public sealed partial class DashboardPage : Page
{
    private bool _suppressGpuToggle;
    private bool _gpuConfirmOpen;
    private string? _dismissedUpdateVersion;

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
        App.UpdateService.PropertyChanged += OnUpdateServicePropertyChanged;
        UpdateValues();
        SyncInfoBars();
        SyncUpdateBar();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ValuesUpdated -= UpdateValues;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        App.UpdateService.PropertyChanged -= OnUpdateServicePropertyChanged;
    }

    private void OnUpdateServicePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or nameof(UpdateService.State) or nameof(UpdateService.LatestVersion))
        {
            DispatcherQueue.TryEnqueue(SyncUpdateBar);
        }
    }

    private void SyncUpdateBar()
    {
        var version = App.UpdateService.LatestVersion;
        var available = App.UpdateService.IsUpdateAvailable && version != _dismissedUpdateVersion;
        if (available)
        {
            UpdateAvailableBar.Message = $"Version {version} is available.";
        }

        UpdateAvailableBar.IsOpen = available;
    }

    private void UpdateAvailableBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        _dismissedUpdateVersion = App.UpdateService.LatestVersion;
        UpdateAvailableBar.IsOpen = false;
    }

    private void ViewInSettings_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.NavigateTo(typeof(SettingsPage));
    }

    private void UpdateValues()
    {
        ValueFade.SetRunText(PercentRun, PercentBlock, ViewModel.PercentText);
        ValueFade.SetText(StateSummary, ViewModel.StateSummaryText);
        SetOptionalLine(RateLine, ViewModel.RateLineText);
        ValueFade.SetText(LimitState, ViewModel.LimitStateText);
        ValueFade.SetText(GpuState, ViewModel.GpuStateText);
        ValueFade.SetText(HealthValue, ViewModel.HealthText);
        ValueFade.SetText(CyclesValue, ViewModel.CycleCountText);
        ValueFade.SetText(VoltageValue, ViewModel.VoltageText);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("" or nameof(MainViewModel.HasLimitMessage)
            or nameof(MainViewModel.HasError)
            or nameof(MainViewModel.LimitMessageIsError)
            or nameof(MainViewModel.HasGpuMessage) or nameof(MainViewModel.GpuMessageIsError)
            or nameof(MainViewModel.GpuPresent) or nameof(MainViewModel.IsGpuPending)
            or nameof(MainViewModel.ShowGpuRetry)))
        {
            return;
        }

        SyncInfoBars();
    }

    // A line that can legitimately be empty (AC with no flow, or an unconfirmed
    // limit) must collapse rather than leave a 2px-stacked gap under the state.
    private static void SetOptionalLine(TextBlock block, string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            block.Text = string.Empty;
            block.Visibility = Visibility.Collapsed;
            return;
        }

        block.Visibility = Visibility.Visible;
        ValueFade.SetText(block, value);
    }

    private void SyncInfoBars()
    {
        LimitInfoBar.Severity = ViewModel.LimitMessageIsError
            ? InfoBarSeverity.Error
            : InfoBarSeverity.Warning;
        LimitInfoBar.Message = ViewModel.LimitMessage ?? string.Empty;
        LimitInfoBar.IsOpen = ViewModel.HasLimitMessage;

        if (!_gpuConfirmOpen)
        {
            GpuInfoBar.Severity = ViewModel.GpuMessageIsError
                ? InfoBarSeverity.Error
                : InfoBarSeverity.Warning;
            GpuInfoBarMessage.Text = ViewModel.GpuMessage ?? string.Empty;
            GpuInfoBar.IsOpen = ViewModel.HasGpuMessage;
        }

        RefreshInfoBar.Message = ViewModel.ErrorText ?? string.Empty;
        RefreshInfoBar.IsOpen = ViewModel.HasError && ViewModel.HasSnapshot;

        // An unknown limit must not read as "Off": no label when unconfirmed.
        var known = ViewModel.Snapshot?.LimitOn is not null;
        LimitSwitch.OnContent = known ? "On" : string.Empty;
        LimitSwitch.OffContent = known ? "Off" : string.Empty;

        var gpuKnown = ViewModel.Snapshot?.Gpu?.Enabled is not null;
        GpuSwitch.OnContent = gpuKnown ? "On" : string.Empty;
        GpuSwitch.OffContent = gpuKnown ? "Off" : string.Empty;
    }

    private void LimitSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsLimitPending)
        {
            return;
        }

        _ = ViewModel.RequestLimitChangeAsync(LimitSwitch.IsOn);
    }

    private void GpuSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressGpuToggle || ViewModel.IsGpuPending)
        {
            return;
        }

        if (GpuSwitch.IsOn)
        {
            // Enabling cannot blank a display; no confirmation needed.
            _ = ViewModel.RequestGpuChangeAsync(true);
            return;
        }

        // Disabling gets an inline confirm. Snap the switch back to On first:
        // nothing has happened until the user commits.
        _suppressGpuToggle = true;
        try { GpuSwitch.IsOn = true; } finally { _suppressGpuToggle = false; }
        _gpuConfirmOpen = true;
        GpuInfoBar.Severity = InfoBarSeverity.Warning;
        GpuInfoBarMessage.Text = "The GPU stays off until you switch it back on, including after a restart. Apps run on the Intel GPU, and screens wired to the GPU go dark. If an external screen is your only display, use the built-in screen to turn the GPU back on.";
        GpuInfoBar.IsOpen = true;
        GpuConfirmButtons.Visibility = Visibility.Visible;
    }

    private void CloseGpuConfirm()
    {
        _gpuConfirmOpen = false;
        GpuConfirmButtons.Visibility = Visibility.Collapsed;
        // The confirm text bypassed the view-model message, so close the bar
        // explicitly; DismissGpuMessage() alone is a no-op when it is already null.
        GpuInfoBar.IsOpen = false;
    }

    private void GpuConfirmOff_Click(object sender, RoutedEventArgs e)
    {
        CloseGpuConfirm();
        _ = ViewModel.RequestGpuChangeAsync(false);
    }

    private void GpuConfirmCancel_Click(object sender, RoutedEventArgs e)
    {
        CloseGpuConfirm();
        ViewModel.DismissGpuMessage();
    }

    private void GpuRefresh_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.RefreshAsync();
    }

    private void GpuInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        CloseGpuConfirm();
        ViewModel.DismissGpuMessage();
    }

    private void CheckAgain_Click(object sender, RoutedEventArgs e)
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

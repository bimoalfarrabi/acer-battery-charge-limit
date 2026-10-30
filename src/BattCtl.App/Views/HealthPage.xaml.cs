using BattCtl.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BattCtl.App.Views;

public sealed partial class HealthPage : Page
{
    public HealthPage()
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
        SyncInfoBar();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ValuesUpdated -= UpdateValues;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void UpdateValues()
    {
        ValueFade.SetText(DesignValue, ViewModel.DesignCapacityText);
        ValueFade.SetText(FullValue, ViewModel.FullChargeCapacityText);
        ValueFade.SetText(WearValue, ViewModel.WearText);
        ValueFade.SetText(HealthSummary, ViewModel.HealthSummaryText);
        ValueFade.SetText(StateValue, ViewModel.StateText);
        ValueFade.SetText(PowerValue, ViewModel.PowerText);
        ValueFade.SetText(CycleValue, ViewModel.CycleCountText);
        ValueFade.SetText(VoltageValue, ViewModel.VoltageText);
        ValueFade.SetText(RateValue, ViewModel.RateText);
        ValueFade.SetText(RemainingValue, ViewModel.RemainingCapacityText);
        ValueFade.SetText(EstimatedValue, ViewModel.EstimatedTimeText);
        UpdatedCaption.Text = ViewModel.LastUpdatedText;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or nameof(MainViewModel.HasError))
        {
            SyncInfoBar();
        }
    }

    private void SyncInfoBar()
    {
        RefreshInfoBar.Message = ViewModel.ErrorText ?? string.Empty;
        RefreshInfoBar.IsOpen = ViewModel.HasError && ViewModel.HasSnapshot;
    }

    private void TryAgain_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.RefreshAsync();
    }

    private void RefreshInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        ViewModel.DismissError();
    }
}

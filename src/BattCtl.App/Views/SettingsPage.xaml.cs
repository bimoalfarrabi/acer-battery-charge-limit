using System.Reflection;
using BattCtl.App.Services;
using BattCtl.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace BattCtl.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly StartupService _startupService = new();
    private bool _syncingStartupSwitch;

    public SettingsPage()
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

        _syncingStartupSwitch = true;
        StartupSwitch.IsOn = _startupService.IsEnabled();
        _syncingStartupSwitch = false;

        VersionText.Text = $"Version {GetVersion()}";
        UpdateValues();
        SyncReapplyInfoBar();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ValuesUpdated -= UpdateValues;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void UpdateValues()
    {
        ValueFade.SetText(SavedLimitValue, ViewModel.SavedLimitText);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or nameof(MainViewModel.HasReapplyMessage))
        {
            SyncReapplyInfoBar();
        }
    }

    private void SyncReapplyInfoBar()
    {
        ReapplyInfoBar.Severity = ViewModel.ReapplySucceeded ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ReapplyInfoBar.Message = ViewModel.ReapplyMessage ?? string.Empty;
        ReapplyInfoBar.IsOpen = ViewModel.HasReapplyMessage;
    }

    private void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingStartupSwitch)
        {
            return;
        }

        try
        {
            _startupService.SetEnabled(StartupSwitch.IsOn);
        }
        catch (Exception exception)
        {
            _syncingStartupSwitch = true;
            StartupSwitch.IsOn = !StartupSwitch.IsOn;
            _syncingStartupSwitch = false;
            StartupInfoBar.Message = $"Could not update the login entry: {exception.Message}";
            StartupInfoBar.IsOpen = true;
        }
    }

    private void Reapply_Click(object sender, RoutedEventArgs e)
    {
        _ = ViewModel.ReapplySavedLimitAsync();
    }

    private void CopyStatus_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(ViewModel.GetStatusJson());
        Clipboard.SetContent(package);
        Clipboard.Flush();

        CopyStatusCaption.Text = "Copied to clipboard.";
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            CopyStatusCaption.Text = "Copies the latest snapshot as JSON, for troubleshooting.";
        };
        timer.Start();
    }

    private void StartupInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        StartupInfoBar.IsOpen = false;
    }

    private void ReapplyInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        ViewModel.DismissReapply();
    }

    private static string GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "unknown";
    }
}

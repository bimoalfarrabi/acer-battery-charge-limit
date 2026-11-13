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
    private bool _syncingAutoCheckSwitch;
    private IProgress<double> _downloadProgress = null!;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        _downloadProgress = new Progress<double>(value =>
        {
            if (Updates.State == UpdateState.Downloading)
            {
                SyncUpdateCard();
            }
        });
    }

    public MainViewModel ViewModel => App.ViewModel;

    public UpdateService Updates => App.UpdateService;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ValuesUpdated += UpdateValues;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Updates.PropertyChanged += OnUpdateServicePropertyChanged;

        _syncingStartupSwitch = true;
        StartupSwitch.IsOn = _startupService.IsEnabled();
        _syncingStartupSwitch = false;

        _syncingAutoCheckSwitch = true;
        AutoCheckSwitch.IsOn = Updates.AutoCheckEnabled;
        _syncingAutoCheckSwitch = false;

        VersionText.Text = $"Version {GetVersion()}";
        UpdateValues();
        SyncReapplyInfoBar();
        SyncUpdateCard();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ValuesUpdated -= UpdateValues;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Updates.PropertyChanged -= OnUpdateServicePropertyChanged;
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

    private void OnUpdateServicePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or nameof(UpdateService.State)
            or nameof(UpdateService.DownloadProgress) or nameof(UpdateService.LastChecked)
            or nameof(UpdateService.LatestVersion) or nameof(UpdateService.Message)
            or nameof(UpdateService.AutoCheckEnabled))
        {
            SyncUpdateCard();
        }
    }

    private void SyncUpdateCard()
    {
        UpdateButton.Content = Updates.State switch
        {
            UpdateState.Checking => "Checking…",
            UpdateState.Available => "Download & install",
            UpdateState.Downloading => "Cancel",
            UpdateState.Ready => "Install and restart",
            _ => "Check for updates"
        };

        UpdateButton.IsEnabled = Updates.State switch
        {
            UpdateState.Checking or UpdateState.Installing => false,
            UpdateState.Downloading => true,
            UpdateState.Available or UpdateState.Ready => true,
            _ => Updates.CanCheck
        };

        UpdateStatusLine.Text = Updates.State switch
        {
            UpdateState.Idle => Updates.LastChecked is null
                ? "Last checked: —"
                : $"Last checked {Updates.LastChecked.Value.LocalDateTime:HH:mm}.",
            UpdateState.Checking => "Checking for updates…",
            UpdateState.UpToDate => Updates.LastChecked is null
                ? "You're up to date."
                : $"You're up to date. Last checked {Updates.LastChecked.Value.LocalDateTime:HH:mm}.",
            UpdateState.Available => Updates.LatestVersion is null
                ? "An update is available."
                : $"Version {Updates.LatestVersion} is available (you have {Updates.CurrentVersion})",
            UpdateState.Downloading => Updates.LatestVersion is null
                ? "Downloading…"
                : $"Downloading {Updates.LatestVersion}… {Updates.DownloadProgress:0}%",
            UpdateState.Ready => Updates.LatestVersion is null
                ? "Update downloaded."
                : $"Version {Updates.LatestVersion} downloaded",
            UpdateState.Installing => "Installing…",
            UpdateState.Error => "Couldn't check for updates.",
            UpdateState.Offline => Updates.LastChecked is null
                ? "No connection."
                : $"No connection. Last checked {Updates.LastChecked.Value.LocalDateTime:HH:mm}.",
            UpdateState.RateLimited => "GitHub is rate-limiting checks. Try again later.",
            _ => "Last checked: —"
        };

        switch (Updates.State)
        {
            case UpdateState.Available:
                UpdateInfoBar.Severity = InfoBarSeverity.Informational;
                UpdateInfoBar.Title = "Update available";
                UpdateInfoBar.Message = string.Empty;
                UpdateNotesText.Text = Updates.ReleaseNotes ?? "No release notes were published.";
                UpdateInfoBar.IsClosable = false;
                UpdateInfoBar.IsOpen = true;
                break;
            case UpdateState.Ready:
                UpdateInfoBar.Severity = InfoBarSeverity.Informational;
                UpdateInfoBar.Title = "Ready to install";
                UpdateInfoBar.Message = string.Empty;
                UpdateNotesText.Text = string.Empty;
                UpdateInfoBar.IsClosable = false;
                UpdateInfoBar.IsOpen = true;
                break;
            case UpdateState.Error:
                UpdateInfoBar.Severity = InfoBarSeverity.Error;
                UpdateInfoBar.Title = "Update failed";
                UpdateInfoBar.Message = Updates.Message ?? string.Empty;
                UpdateNotesText.Text = string.Empty;
                UpdateInfoBar.IsClosable = true;
                UpdateInfoBar.IsOpen = true;
                break;
            case UpdateState.Offline:
            case UpdateState.RateLimited:
                UpdateInfoBar.Severity = InfoBarSeverity.Warning;
                UpdateInfoBar.Title = string.Empty;
                UpdateInfoBar.Message = Updates.State == UpdateState.Offline
                    ? "You appear to be offline."
                    : "GitHub is rate-limiting checks. Try again later.";
                UpdateNotesText.Text = string.Empty;
                UpdateInfoBar.IsClosable = true;
                UpdateInfoBar.IsOpen = true;
                break;
            default:
                UpdateInfoBar.IsOpen = false;
                break;
        }
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        switch (Updates.State)
        {
            case UpdateState.Downloading:
                Updates.CancelDownload();
                break;
            case UpdateState.Ready:
                _ = Updates.InstallAsync();
                break;
            case UpdateState.Available:
                _ = Updates.DownloadAsync(_downloadProgress, ViewModel.ShutdownToken);
                break;
            default:
                _ = Updates.CheckForUpdatesAsync(manual: true, ViewModel.ShutdownToken);
                break;
        }
    }

    private void AutoCheckSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingAutoCheckSwitch)
        {
            return;
        }

        Updates.AutoCheckEnabled = AutoCheckSwitch.IsOn;
    }

    private void UpdateInfoBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        UpdateInfoBar.IsOpen = false;
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

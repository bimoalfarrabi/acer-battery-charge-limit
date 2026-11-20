using BattCtl.App.Views;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace BattCtl.App;

public sealed partial class MainWindow : Window
{
    private readonly AppWindow _appWindow;

    // Debounce for the opportunistic worker refresh on Activated: at most one
    // ~930ms mailbox round trip per minute, no matter how often the window is
    // focused. MinValue means "unknown", which counts as stale.
    private DateTime _lastWorkerRefresh = DateTime.MinValue;

    private bool _navigating;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Battery Charge Limit";

        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow = appWindow;
        // The exe icon covers Explorer and shortcuts; SetIcon covers the live
        // window/taskbar entry for this unpackaged app.
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        // 1000x520 fits the Dashboard with no scroll: title, percent block,
        // limit card, GPU card, and the at-a-glance tiles. A 10-second glance
        // utility should not have to be scrolled. Health and Settings are taller
        // and scroll inside their own ScrollViewers.
        appWindow.Resize(new SizeInt32(1000, 520));

        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }

        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }

        // The app lives in the tray: closing the window hides it, quitting
        // happens from the tray menu.
        Closed += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            AppWindow.Hide();
        };

        // Battery telemetry runs on its own 1 Hz monitor; the worker snapshot
        // is only refreshed on activation (debounced), never on a timer here.
        appWindow.Changed += (_, _) => UpdateSampling();
        Activated += (_, _) =>
        {
            UpdateSampling();
            if (appWindow.IsVisible)
            {
                App.ViewModel?.Telemetry.Refresh();
                TryDebouncedWorkerRefresh();
            }
        };

        ContentFrame.Navigated += (_, _) => OnFrameNavigated();

        SelectInitialPage();
        ContentFrame.Navigate(_initialPage);
        UpdateSampling();
    }

    private readonly System.Type _initialPage = GetInitialPage();

    private static System.Type GetInitialPage()
    {
        foreach (var arg in Environment.GetCommandLineArgs())
        {
            if (arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
            {
                return arg["--page=".Length..].ToLowerInvariant() switch
                {
                    "health" => typeof(HealthPage),
                    "settings" => typeof(SettingsPage),
                    _ => typeof(DashboardPage)
                };
            }
        }

        return typeof(DashboardPage);
    }

    private void SelectInitialPage()
    {
        NavigationViewControl.SelectedItem = _initialPage == typeof(HealthPage) ? NavigationViewControl.MenuItems[1]
            : _initialPage == typeof(SettingsPage) ? NavigationViewControl.MenuItems[2]
            : DashboardItem;
    }

    // Telemetry samples only while the window is visible, not minimized, and a
    // battery page is on screen. Settings needs no sampling.
    private void UpdateSampling()
    {
        var minimized = _appWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized;

        var shouldSample = _appWindow.IsVisible && !minimized
            && (ContentFrame.CurrentSourcePageType == typeof(DashboardPage)
                || ContentFrame.CurrentSourcePageType == typeof(HealthPage));

        App.ViewModel?.Telemetry.SetSampling(shouldSample);
    }

    private void TryDebouncedWorkerRefresh()
    {
        if (App.ViewModel is null ||
            DateTime.UtcNow - _lastWorkerRefresh < TimeSpan.FromSeconds(60))
        {
            return;
        }

        _lastWorkerRefresh = DateTime.UtcNow;
        _ = App.ViewModel.RefreshAsync();
    }

    private void OnFrameNavigated()
    {
        UpdateSampling();

        // Arriving on Health after a long tray-hidden stretch: the worker data
        // may predate sleep or the whole session, so pull one fresh snapshot.
        if (ContentFrame.CurrentSourcePageType == typeof(HealthPage))
        {
            TryDebouncedWorkerRefresh();
        }
    }

    private void NavigationViewControl_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        var pageType = item.Tag?.ToString() switch
        {
            "Dashboard" => typeof(DashboardPage),
            "Health" => typeof(HealthPage),
            "Settings" => typeof(SettingsPage),
            _ => null
        };

        NavigateTo(pageType);
    }

    internal void NavigateTo(System.Type? pageType)
    {
        if (pageType is null || ContentFrame.CurrentSourcePageType == pageType || _navigating)
        {
            return;
        }

        _navigating = true;
        try
        {
            var item = pageType == typeof(SettingsPage) ? NavigationViewControl.MenuItems[2]
                : pageType == typeof(HealthPage) ? NavigationViewControl.MenuItems[1]
                : (object)DashboardItem;
            if (!ReferenceEquals(NavigationViewControl.SelectedItem, item))
            {
                // Setting SelectedItem raises SelectionChanged, which re-enters
                // this method; the _navigating guard stops the second navigation.
                NavigationViewControl.SelectedItem = item;
            }

            if (ContentFrame.CurrentSourcePageType != pageType)
            {
                ContentFrame.Navigate(pageType);
            }
        }
        finally
        {
            _navigating = false;
        }
    }
}

using BattCtl.App.Views;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace BattCtl.App;

public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueueTimer _refreshTimer;
    private bool _navigating;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Battery Charge Limit";

        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        // 1000x460 fits the Dashboard exactly (content ends at 431 + 24 page
        // padding). Health and Settings are taller and scroll inside their
        // ScrollViewers instead of leaving a void here.
        appWindow.Resize(new SizeInt32(1000, 460));

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

        // Poll at most every 5s while visible; stop entirely when hidden or
        // minimized. Each poll is a ~930ms mailbox round trip, so visibility
        // gating is what keeps the app cheap.
        _refreshTimer = DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(5);
        _refreshTimer.Tick += (_, _) => _ = App.ViewModel.RefreshAsync();

        appWindow.Changed += (_, _) => UpdatePolling(appWindow);
        Activated += (_, _) =>
        {
            UpdatePolling(appWindow);
            if (appWindow.IsVisible)
            {
                _ = App.ViewModel.RefreshAsync();
            }
        };

        SelectInitialPage();
        ContentFrame.Navigate(_initialPage);
        UpdatePolling(appWindow);
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

    private void UpdatePolling(AppWindow appWindow)
    {
        var minimized = appWindow.Presenter is OverlappedPresenter presenter
            && presenter.State == OverlappedPresenterState.Minimized;

        if (appWindow.IsVisible && !minimized)
        {
            if (!_refreshTimer.IsRunning)
            {
                _refreshTimer.Start();
            }
        }
        else if (_refreshTimer.IsRunning)
        {
            _refreshTimer.Stop();
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

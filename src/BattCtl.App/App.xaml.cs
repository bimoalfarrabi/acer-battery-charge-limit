using BattCtl.App.Services;
using BattCtl.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.UI.ViewManagement;

namespace BattCtl.App;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    public static MainViewModel ViewModel { get; private set; } = null!;
    public static UpdateService UpdateService { get; private set; } = null!;

    private TrayService? _tray;

    public App()
    {
        InitializeComponent();

        // A tray utility has no console; keep a crash log next to the snapshot cache.
        UnhandledException += (_, eventArgs) => LogFatal("UnhandledException", eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            LogFatal("AppDomain.UnhandledException", eventArgs.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            LogFatal("UnobservedTaskException", eventArgs.Exception);

        var background = new UISettings().GetColorValue(UIColorType.Background);
        RequestedTheme = (background.R + background.G + background.B) / 3 < 128
            ? ApplicationTheme.Dark
            : ApplicationTheme.Light;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // The view model must exist before the window navigates: DashboardPage
        // and HealthPage bind App.ViewModel during x:Bind's initial evaluation,
        // which runs inside the Frame navigation below.
        ViewModel = new MainViewModel(
            new BattCtlClient(),
            new SnapshotCache(),
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("The UI thread has no DispatcherQueue."));
        UpdateService = new UpdateService();

        ShowMainWindow();

        _tray = new TrayService();
        try
        {
            _tray.Initialize();
            // Tray events fire on the tray message-loop thread; hop to the UI thread.
            _tray.ShowRequested += () => MainWindow?.DispatcherQueue.TryEnqueue(ShowMainWindow);
            _tray.QuitRequested += () => MainWindow?.DispatcherQueue.TryEnqueue(Quit);
            _tray.ToggleRequested += () => MainWindow?.DispatcherQueue.TryEnqueue(
                async () => await ViewModel.ToggleLimitFromTrayAsync());
        }
        catch (Exception exception)
        {
            LogFatal("TrayService.Initialize", exception);
            _tray = null;
        }

        if (_tray is not null)
        {
            ViewModel.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName is "" or nameof(MainViewModel.TrayTooltip))
                {
                    _tray.SetTooltip(ViewModel.TrayTooltip);
                }
            };
            _tray.SetTooltip(ViewModel.TrayTooltip);
        }

        // A second launch (or the single-instance redirect in Program) shows the
        // window; Program's own handler only calls Activate(), which cannot
        // unhide a tray-hidden window.
        AppInstance.GetCurrent().Activated += (_, _) =>
            MainWindow?.DispatcherQueue.TryEnqueue(ShowMainWindow);

        _ = ViewModel.InitializeAsync();

        // One silent update check per launch, fire-and-forget: never block
        // first paint on the network.
        if (UpdateService.AutoCheckEnabled)
        {
            _ = UpdateService.CheckForUpdatesAsync(manual: false, ViewModel.ShutdownToken);
        }
    }

    public void ShowMainWindow()
    {
        if (MainWindow is null)
        {
            MainWindow = new MainWindow();
        }

        MainWindow.AppWindow.Show();
        MainWindow.Activate();
    }

    public void Quit()
    {
        // Unsubscribes OS events and stops the telemetry timer; the window's
        // Closed handler only hides the window, so this is the one teardown path.
        // Dispose also cancels the shutdown token.
        ViewModel.Dispose();
        _tray?.Dispose();
        Environment.Exit(0);
    }

    private static void LogFatal(string source, Exception? exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BattCtl.App");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}: {exception}\n\n");
        }
        catch
        {
        }
    }
}

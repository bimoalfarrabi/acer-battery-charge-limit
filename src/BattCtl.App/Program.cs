using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace BattCtl.App;

public static class Program
{
    private static IntPtr _redirectEventHandle;

    [global::System.STAThread]
    private static int Main(string[] args)
    {
        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectIfNeeded())
        {
            return 0;
        }

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });

        return 0;
    }

    private static bool RedirectIfNeeded()
    {
        var activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey("BattCtl.SingleInstance");

        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += OnActivated;
            return false;
        }

        _redirectEventHandle = CreateEvent(IntPtr.Zero, manualReset: true, initialState: false, name: null);
        if (_redirectEventHandle == IntPtr.Zero)
        {
            return true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await keyInstance.RedirectActivationToAsync(activationArguments).AsTask().ConfigureAwait(false);
            }
            finally
            {
                SetEvent(_redirectEventHandle);
            }
        });

        const uint CoWaitDefault = 0;
        const uint Infinite = 0xFFFFFFFF;
        _ = CoWaitForMultipleObjects(CoWaitDefault, Infinite, 1, new[] { _redirectEventHandle }, out _);
        CloseHandle(_redirectEventHandle);
        _redirectEventHandle = IntPtr.Zero;
        return true;
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        var window = App.MainWindow;
        if (window is not null)
        {
            window.DispatcherQueue.TryEnqueue(window.Activate);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(IntPtr eventHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint flags, uint timeout, ulong count, IntPtr[] handles, out uint index);
}

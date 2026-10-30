using System.Runtime.InteropServices;

namespace BattCtl.App.Services;

public interface ITrayService
{
    void Show();
    void Hide();
    void SetTooltip(string text);
    event Action? ToggleRequested;
}

// Raw Shell_NotifyIcon behind the ITrayService seam.
//
// Why not H.NotifyIcon.WinUI 2.3.2: on this stack (WinAppSDK 1.7.250606001,
// Windows 11 26H2) TaskbarIcon.ForceCreate() takes the process down with a
// stowed 0xc000027b in CoreMessagingXP.dll, whether the icon is declared in
// XAML or built in code, before or after window creation. The library stays
// out until that is fixed upstream; this implementation is ~200 lines with no
// dependency and handles the classic edge cases by hand: Explorer restarts
// (TaskbarCreated), menu dismissal (SetForegroundWindow + WM_NULL), icon
// lifetime (DestroyIcon), and NIM_DELETE on Dispose so no ghost icon remains.
public sealed class TrayService : ITrayService, IDisposable
{
    private const uint WmTrayIcon = 0x8000 + 1;
    private const uint WmQuit = 0x0012;
    private const uint WmNull = 0x0000;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const int NimAdd = 0;
    private const int NimModify = 1;
    private const int NimDelete = 2;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x10;
    private const int SmCxSmIcon = 49;
    private const int SmCySmIcon = 50;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;
    private const uint MfString = 0x0000;
    private const uint MfSeparator = 0x0800;

    private const int CmdShow = 1;
    private const int CmdToggle = 2;
    private const int CmdQuit = 3;

    private static readonly WndProcDelegate WndProc = WndProcImpl;
    private static TrayService? _current;

    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private uint _taskbarCreatedMessage;
    private string _tooltip = "Battery Charge Limit";
    private bool _visible;

    public event Action? ToggleRequested;
    public event Action? ShowRequested;
    public event Action? QuitRequested;

    public void Initialize()
    {
        _current = this;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "TrayIcon" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("The tray icon message window did not start.");
        }
    }

    public void Show()
    {
        _visible = true;
        NotifyIcon(NimAdd);
    }

    public void Hide()
    {
        _visible = false;
        NotifyIcon(NimDelete);
    }

    public void SetTooltip(string text)
    {
        _tooltip = text.Length > 127 ? text[..127] : text;
        if (_visible)
        {
            NotifyIcon(NimModify);
        }
    }

    public void Dispose()
    {
        if (_visible)
        {
            NotifyIcon(NimDelete);
            _visible = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }

        if (_thread is not null && !_thread.Join(TimeSpan.FromSeconds(2)))
        {
            // The loop thread is a background thread; process exit reclaims it.
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        _current = null;
    }

    private void MessageLoop()
    {
        var className = "BattCtlTrayWindow";
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        var wndClass = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProc),
            lpszClassName = className,
            hInstance = GetModuleHandle(null),
        };
        RegisterClass(ref wndClass);

        _hwnd = CreateWindowEx(0, className, className, 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, wndClass.hInstance, IntPtr.Zero); // HWND_MESSAGE

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico");
        var iconSize = GetSystemMetrics(SmCxSmIcon);
        var iconSizeY = GetSystemMetrics(SmCySmIcon);
        _hIcon = LoadImageW(IntPtr.Zero, iconPath, ImageIcon, iconSize, iconSizeY, LrLoadFromFile);

        _ready.Set();

        _visible = true;
        NotifyIcon(NimAdd);

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    private void NotifyIcon(int command)
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uCallbackMessage = WmTrayIcon,
            hIcon = _hIcon,
            szTip = _tooltip,
        };

        if (command == NimDelete)
        {
            Shell_NotifyIconW(NimDelete, ref data);
            return;
        }

        data.uFlags = NifMessage | NifIcon | NifTip;
        Shell_NotifyIconW(command, ref data);
    }

    private static IntPtr WndProcImpl(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        var self = _current;

        if (self is not null && message == WmTrayIcon)
        {
            var mouse = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouse == WmLButtonUp)
            {
                self.ShowRequested?.Invoke();
            }
            else if (mouse == WmRButtonUp)
            {
                self.ShowContextMenu();
            }

            return IntPtr.Zero;
        }

        if (self is not null && message == self._taskbarCreatedMessage && self._visible)
        {
            self.NotifyIcon(NimAdd);
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        AppendMenu(menu, MfString, CmdShow, "Open Battery Charge Limit");
        AppendMenu(menu, MfString, CmdToggle, "Toggle 80% charge limit");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, CmdQuit, "Quit");

        GetCursorPos(out var point);

        // The foreground-window shuffle is required or the menu never dismisses
        // when the user clicks elsewhere (classic Win32 tray-menu bug).
        SetForegroundWindow(hwnd: _hwnd);
        var command = TrackPopupMenuEx(menu, TpmReturnCmd | TpmRightButton, point.X, point.Y, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);

        switch ((int)command)
        {
            case CmdShow:
                ShowRequested?.Invoke();
                break;
            case CmdToggle:
                ToggleRequested?.Invoke();
                break;
            case CmdQuit:
                QuitRequested?.Invoke();
                break;
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG message, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);
}

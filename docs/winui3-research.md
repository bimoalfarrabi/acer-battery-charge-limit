# WinUI 3 (Windows App SDK 1.7) — Unpackaged Desktop App Research

**Target environment**

| Item | Value |
|---|---|
| OS | Windows 11 26H2, build 26300 |
| .NET SDK | 9.0.320 |
| Windows App SDK | **1.7.250606001** = *version 1.7.3* (see [1.7 release notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-7)) |
| Packaging | `WindowsPackageType=None` (unpackaged) |
| Build host | Linux/WSL, `dotnet.exe` |
| Current stable WinAppSDK (context) | 2.6.4 (released 2026-10-27) — **not** what this app uses; everything below is written for the 1.7 line |

**Version notes that matter for this document**

- `Window.SystemBackdrop` → **Windows App SDK 1.3+** ("Starting with Windows App SDK 1.3, you can apply material by setting the `Window.SystemBackdrop` property… This is the recommended way"). Applies to 1.3 → 1.8 and 2.x. → *applies to 1.7.*
- `TitleBar` **control** (`Microsoft.UI.Xaml.Controls.TitleBar`) → **new in 1.7** (introduced in 1.7 Preview 1, GA in 1.7.1). → *available in 1.7.3.*
- `Window.AppWindow` → 1.3+; the XAML `Window` and `AppWindow` **share the same `AppWindowTitleBar` object starting in 1.4**. → *applies to 1.7.*
- `MicaKind.BaseAlt` (Mica Alt) → WinAppSDK 1.1+, Windows 11 build 22000+.
- `PublishSingleFile` for unpackaged WinUI 3 → supported **from 1.5**, only when *both* unpackaged and self-contained.

---

## 1. Mica / backdrop

### 1.1 Which API to use

Two APIs exist. Use the **XAML `SystemBackdrop`** property; the `MicaController` API is the lower-level escape hatch.

| API | Since | Use |
|---|---|---|
| `Window.SystemBackdrop = new MicaBackdrop()` | 1.3 | Recommended for most apps |
| `MicaController` + `SystemBackdropConfiguration` | 1.0/1.1 | Only when you need per-window policy (`IsInputActive`, `IsHighContrast`, custom tint/fallback) or non-XAML hosts |

Docs: [System backdrops (Mica/Acrylic)](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops) — "Starting with Windows App SDK 1.3 … This is the recommended way to apply a material for most apps."

### 1.2 Minimal, correct code (1.7, unpackaged)

```csharp
using Microsoft.UI.Composition.SystemBackdrops;   // MicaKind, MicaController, DesktopAcrylicController
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;                    // MicaBackdrop, DesktopAcrylicBackdrop

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        TrySetBackdrop();
    }

    private void TrySetBackdrop()
    {
        if (MicaController.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        }
        else if (DesktopAcrylicController.IsSupported())
        {
            // Windows 10 fallback; also covers Win11 builds where Mica is unavailable.
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        // else: leave SystemBackdrop null -> window paints the theme's solid
        // SolidBackgroundFillColorBase, which is exactly Mica's own fallback color.
    }
}
```

XAML equivalent:

```xml
<Window ...>
    <Window.SystemBackdrop>
        <MicaBackdrop Kind="BaseAlt" />
    </Window.SystemBackdrop>
    ...
</Window>
```

### 1.3 What `Window.SystemBackdrop` requires

- **WinAppSDK 1.3+** at build and runtime. `SystemBackdrop` is a property on `Microsoft.UI.Xaml.Window`, type `Microsoft.UI.Xaml.Media.SystemBackdrop`.
- A **valid backdrop target**. In XAML that is the `Window` itself, which implements `ICompositionSupportsSystemBackdrop`. (Only relevant if you use the controller API directly.)
- A **`SystemBackdropConfiguration`** — only if you use `MicaController` directly; `MicaBackdrop` creates and manages one for you (`SystemBackdrop.GetDefaultSystemBackdropConfiguration`).
- A **`DispatcherQueue`** on the UI thread — again only for the controller path: `DispatcherQueue.EnsureSystemDispatcherQueue()`.

Source: [system-backdrops.md, "In order to use the backdrop material in your app, the following items are required"](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops).

### 1.4 Does Mica work unpackaged?

**Yes.** There is **no packaging requirement anywhere in the API surface or the docs' requirement list** — the only gate is `MicaController.IsSupported()` (OS support). The official unpackaged reference is the Win32 Mica sample (`Samples/Mica/cpp-win32`), which is by definition an unpackaged desktop app and uses `MicaController` directly. `MicaBackdrop` is the same controller wrapped in a XAML type.

**UNVERIFIED:** I could not empirically run this on build 26300 / 1.7.250606001. Everything above is documentation- and source-derived. If it fails, the failure mode is a *missing* backdrop, not a crash (see next section).

### 1.5 Does it "silently fall back"?

Two distinct fallback behaviours — know which is which.

**(a) `MicaBackdrop` does NOT check `IsSupported()`.** Verified in the WinUI source, [`MicaBackdrop.cpp`](https://github.com/microsoft/microsoft-ui-xaml/blob/main/controls/dev/Materials/MicaBackdrop/MicaBackdrop.cpp):

```cpp
void MicaBackdrop::OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
{
    __super::OnTargetConnected(target, xamlRoot);
    auto newController = MicaController();          // no IsSupported() check
    newController.Kind(Kind());
    auto configuration = systemBackdrop.GetDefaultSystemBackdropConfiguration(target, xamlRoot);
    m_controllers.push_back(std::make_unique<ControllerEntry>(target, newController, configuration));
}
```

`AddSystemBackdropTarget` returns `bool`, and the return value is **ignored**. So on an unsupported OS you get a silent no-op: no exception, no backdrop, the window keeps its solid theme background. **That is why you must guard with `MicaController.IsSupported()` yourself** if you care about the fallback looking deliberate (e.g. falling back to Acrylic instead of nothing).

**(b) Even when supported, Mica degrades to a solid colour.** From [Mica material](https://learn.microsoft.com/en-us/windows/apps/design/style/mica), Mica/Mica Alt render as `SolidBackgroundFillColorBase` / `SolidBackgroundFillColorBaseAlt` when:

- the user turns off **transparency** in Settings → Personalization → Color
- **Battery Saver** is active
- the app runs on **low-end hardware**
- an app window **deactivates**
- the Windows version is **below 22000**

So on a laptop on battery saver (very likely for a *battery* utility!) Mica will look flat. Design for that: never rely on Mica for contrast.

### 1.6 Detecting support and the fallback chain

```csharp
if (MicaController.IsSupported())          // Win11 22000+ and capable hardware
    SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
else if (DesktopAcrylicController.IsSupported())
    SystemBackdrop = new DesktopAcrylicBackdrop();
else
    SystemBackdrop = null;                 // solid SolidBackgroundFillColorBase
```

This is exactly the chain used by the official WinUI Gallery sample [`SampleSystemBackdropsWindow.xaml.cs`](https://github.com/microsoft/WinUI-Gallery/blob/main/WinUIGallery/SampleSupport/SamplePages/SampleSystemBackdropsWindow.xaml.cs) ("Mica isn't supported. Trying Acrylic." → "Acrylic Base isn't supported. Switching to default color.").

`MicaController.IsSupported()` is `public static bool IsSupported()` — "Determines whether the mica material is supported on the current operating system." ([API docs](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.composition.systembackdrops.micacontroller.issupported)).

### 1.7 Does Mica need `ExtendsContentIntoTitleBar = true`?

**For the client area: no. To look right: yes.**

With the default system title bar, Mica is drawn behind `Window.Content` only; the title bar strip is painted by the system with its own background, producing a visible seam and a title bar that does not match the client area. The docs are explicit:

> "If you have applied Mica in the client area of your window, you can **extend it into the title bar area and make your caption buttons transparent** for the Mica to show through."
> — [Title bar customization → Color and transparency in caption buttons](https://learn.microsoft.com/en-us/windows/apps/develop/title-bar)

```csharp
ExtendsContentIntoTitleBar = true;
AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
AppWindow.TitleBar.ButtonHoverBackgroundColor = null;   // null = system default
AppWindow.TitleBar.ButtonPressedBackgroundColor = null;
```

**Implication:** setting `ExtendsContentIntoTitleBar = true` hands you ownership of the title bar: you must supply drag regions, reserve space for the caption buttons (`LeftInset`/`RightInset`), and — if you put interactive controls up there — declare pass-through regions with `InputNonClientPointerSource.SetRegionRects`. In 1.7 the `TitleBar` control does all of this for you (section 2).

Also: only these four `AppWindowTitleBar` colour properties honour alpha, and only when `ExtendsContentIntoTitleBar == true`. All other colour properties ignore alpha; with `ExtendsContentIntoTitleBar == false` alpha is ignored for *all* of them.

---

## 2. Title bar

### 2.1 Recommendation for 1.7

**Use the `TitleBar` control** (`Microsoft.UI.Xaml.Controls.TitleBar`, new in 1.7) rather than hand-rolling `SetTitleBar()` + `InputNonClientPointerSource`. It gives you the system caption buttons, correct RTL mirroring, theme/High-Contrast response, drag regions and accessibility for free, and it is what the current docs point at first.

Raw `ExtendsContentIntoTitleBar` + `SetTitleBar(UIElement)` still works and is the fallback if you need a layout the control can't express (or if you ever downgrade below 1.7).

### 2.2 Compact custom title bar that works with Mica, RTL and High Contrast

```xml
<Window
    x:Class="BattCtl.MainWindow"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <Window.SystemBackdrop>
        <MicaBackdrop Kind="Base" />
    </Window.SystemBackdrop>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>

        <!-- TitleBar reserves the caption-button area itself, and mirrors
             automatically when FlowDirection is RightToLeft. -->
        <TitleBar x:Name="AppTitleBar"
                  Title="Battery Charge Limit"
                  Subtitle="ACER"
                  IsPaneToggleButtonVisible="False"
                  IsBackButtonVisible="False">
            <TitleBar.IconSource>
                <FontIconSource Glyph="&#xE83F;" />
            </TitleBar.IconSource>
        </TitleBar>

        <Frame x:Name="RootFrame" Grid.Row="1" />
    </Grid>
</Window>
```

```csharp
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Must be set in the constructor, not later: otherwise the default
        // system title bar flashes before being hidden.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // Let Mica show through the caption buttons.
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
    }
}
```

Notes that matter:

- **`ExtendsContentIntoTitleBar` must be set in XAML? No** — the docs warn: "`ExtendsContentIntoTitleBar` shows in the XAML IntelliSense for `Window`, but **setting it in XAML causes an error**. Set this property in code instead."
- **Set it in the constructor**, not in `Loaded`, or the system title bar is shown first and then hidden (visible flicker).
- **`PreferredHeightOption` throws if set before `ExtendsContentIntoTitleBar == true`.** Order matters:
  ```csharp
  ExtendsContentIntoTitleBar = true;
  if (ExtendsContentIntoTitleBar) AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
  ```
  Use `Tall` only if you put interactive content (search box, account picture) in the bar; the control's default height is fine for title + subtitle.
- **High contrast:** do not hard-code `ButtonBackgroundColor`. Mica already degrades to a solid colour in HC, but the caption-button colours do not follow automatically. Check HC and reset:
  ```csharp
  var hc = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast;
  AppWindow.TitleBar.ButtonBackgroundColor = hc ? null : Colors.Transparent;
  ```
  (`Microsoft.UI.System.ThemeSettings.CreateForWindowId(AppWindow.Id).HighContrast` is the WinAppSDK-native equivalent, used by the WinUI Gallery.)
- **RTL:** the `TitleBar` control reverses its layout when `FlowDirection` is `RightToLeft`, and the caption buttons are placed by `AppWindowTitleBar` — so you do **not** need manual left/right logic. If you hand-roll, you must use *two* padding columns sized from `TitleBar.LeftInset` / `RightInset`; using margins instead breaks RTL and breaks transparent caption buttons.
- **`AppWindow.TitleBar` is `null` on Windows 10.** Guard with `AppWindowTitleBar.IsCustomizationSupported()` if you ever ship to Win10.

### 2.3 Manual pattern (pre-1.7, or when you need full control)

```xml
<Grid x:Name="AppTitleBar" Height="48">
    <Grid.ColumnDefinitions>
        <ColumnDefinition x:Name="LeftPaddingColumn"  Width="0" />
        <ColumnDefinition x:Name="IconColumn"         Width="Auto" />
        <ColumnDefinition x:Name="TitleColumn"        Width="Auto" />
        <ColumnDefinition x:Name="LeftDragColumn"     Width="*" />
        <ColumnDefinition x:Name="RightDragColumn"    Width="*" MinWidth="48" />
        <ColumnDefinition x:Name="RightPaddingColumn" Width="0" />
    </Grid.ColumnDefinitions>
    <Image   Grid.Column="1" Source="Assets/AppIcon.png" Width="16" Height="16" />
    <TextBlock Grid.Column="2" x:Name="TitleText" VerticalAlignment="Center" />
</Grid>
```

```csharp
private void UpdateTitleBarInsets()
{
    double scale = AppTitleBar.XamlRoot.RasterizationScale;
    LeftPaddingColumn.Width  = new GridLength(AppWindow.TitleBar.LeftInset  / scale);
    RightPaddingColumn.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
}
```

`AppWindow` uses **physical pixels**; divide by `XamlRoot.RasterizationScale` for DIPs. Recompute on `AppTitleBar.SizeChanged`, not on the window's `Changed` event (the element is not yet resized during maximize/minimize).

---

## 3. Tray icon

WinUI 3 still has **no built-in tray icon** in 1.7. Three real options:

| Option | Verdict |
|---|---|
| `H.NotifyIcon.WinUI` | **Recommended.** Maintained, XAML-native, handles the Win32 details |
| Raw `Shell_NotifyIcon` P/Invoke | Viable, ~120 lines, you own every edge case |
| `WinForms NotifyIcon` (`UseWindowsForms`) | Works but drags the whole WinForms stack in; not recommended |

### 3.1 `H.NotifyIcon.WinUI` — current state (verified 2026-10-30)

| Fact | Value |
|---|---|
| Latest **stable** | **2.4.1** |
| Latest prerelease | 2.5.0-beta.3 (2026-09-12) |
| Repo activity | last push 2026-10-26, 770 stars, 2 open issues → **actively maintained** |
| `2.4.1` target frameworks | `net10.0-windows10.0.17763` **only** |
| `2.4.1` dependency | `Microsoft.WindowsAppSDK >= 1.6.250108002`, `H.NotifyIcon >= 2.4.1` |
| **Last version with a `net9.0` TFM** | **2.3.2** (`net8.0-windows10.0.17763` + `net9.0-windows10.0.17763`) |

**Decision for this app (.NET 9 + WinAppSDK 1.7): pin `H.NotifyIcon.WinUI` `2.3.2`.**

```xml
<PackageReference Include="H.NotifyIcon.WinUI" Version="2.3.2" />
```

Reason: `2.4.1` is `net10.0`-only, so a `net9.0-windows...` project cannot consume it (NU1202). `2.3.2` declares `Microsoft.WindowsAppSDK >= 1.6.250108002` — an open lower bound, so 1.7.250606001 satisfies it. **UNVERIFIED:** that 2.3.2 was *tested* against 1.7.250606001 specifically; it was built against 1.6. If XAML compilation against 1.7 fails, that is the first thing to bisect.

**UNVERIFIED / risk:** open issue [HavenDV/H.NotifyIcon#271 "Accidentally unusable on WinUI platform"](https://github.com/HavenDV/H.NotifyIcon/issues/271) reports `OverlappedPresenter.SetBorderAndTitleBar` throwing `wil::ResultException` in the **`ContextMenuMode="SecondWindow"`** code path. Avoid `SecondWindow`; the default (`PopupMenu`, a native Win32 menu) does not hit that path.

### 3.2 Minimal usage in an unpackaged app (matches the official Windowless sample)

`App.xaml`:

```xml
<Application x:Class="BattCtl.App" ...>
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
                <ResourceDictionary Source="TrayResources.xaml" />
            </ResourceDictionary.MergedDictionaries>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

`TrayResources.xaml`:

```xml
<ResourceDictionary
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:tb="using:H.NotifyIcon">

    <XamlUICommand x:Key="ShowWindowCommand" Label="Open Battery Charge Limit" />
    <XamlUICommand x:Key="ExitCommand"       Label="Exit" />

    <tb:TaskbarIcon x:Key="TrayIcon"
                    Visibility="Visible"
                    ToolTipText="Battery Charge Limit"
                    IconSource="Assets\TrayIcon.ico"
                    NoLeftClickDelay="True"
                    LeftClickCommand="{StaticResource ShowWindowCommand}">
        <tb:TaskbarIcon.ContextFlyout>
            <MenuFlyout>
                <MenuFlyoutItem Command="{StaticResource ShowWindowCommand}" />
                <MenuFlyoutSeparator />
                <MenuFlyoutItem Command="{StaticResource ExitCommand}" />
            </MenuFlyout>
        </tb:TaskbarIcon.ContextFlyout>
    </tb:TaskbarIcon>
</ResourceDictionary>
```

`App.xaml.cs`:

```csharp
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

public partial class App : Application
{
    private Window? _window;
    private TaskbarIcon? _tray;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _tray = (TaskbarIcon)Resources["TrayIcon"];
        _tray.ForceCreate();   // required: no XAML host exists to auto-create it

        ((XamlUICommand)Resources["ShowWindowCommand"]).ExecuteRequested += (_, _) => ShowWindow();
        ((XamlUICommand)Resources["ExitCommand"]).ExecuteRequested += (_, _) => ExitApp();
    }

    private void ShowWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closed += (_, e) => { e.Handled = true; _window.AppWindow.Hide(); };
        }
        _window.AppWindow.Show();
        _window.Activate();
    }

    private void ExitApp()
    {
        _tray?.Dispose();
        Environment.Exit(0);
    }
}
```

Key details: `ForceCreate()` is mandatory in a windowless app; `IconSource` accepts a `.ico` path (`\Assets\TrayIcon.ico`); set `Visibility="Visible"`; the icon must be a **multi-resolution .ico** (16/20/24/32 px) or Windows will upscale and it will look wrong at 150% DPI.

### 3.3 Raw Win32 `Shell_NotifyIcon` — minimum viable

```csharp
using System.Runtime.InteropServices;

internal static class TrayNative
{
    public const int WM_APP = 0x8000;
    public const int WM_TRAYICON = WM_APP + 1;

    public const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    public const int NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    public const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
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

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type,
                                           int cx, int cy, uint load);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    public const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10, LR_DEFAULTSIZE = 0x40;
}
```

```csharp
// Setup — hwnd must be the WinUI window's HWND (WinRT.Interop.WindowNative.GetWindowHandle(window))
var nid = new NOTIFYICONDATA
{
    cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
    hWnd = hwnd,
    uID = 1,
    uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
    uCallbackMessage = WM_TRAYICON,
    hIcon = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_DEFAULTSIZE),
    szTip = "Battery Charge Limit",
};
Shell_NotifyIconW(NIM_ADD, ref nid);
```

**Gotchas (all real, all painful):**

1. **Message loop integration.** `Shell_NotifyIcon` posts `uCallbackMessage` to `hWnd`, so you need a window procedure. WinUI's `Window` does not let you subclass its WndProc directly — use `SetWindowSubclass` (`Comctl32`) on the HWND obtained via `WindowNative.GetWindowHandle(window)`, or create a dedicated **message-only window** (`HWND_MESSAGE`) and run its own message pump on a background thread. The message-only-window approach is cleaner and survives the WinUI window being hidden.
2. **`hWnd` must be valid at `NIM_ADD` time** — so call it *after* `Activate()`, never in the `Window` constructor.
3. **Icon lifetime.** If you use `LoadImageW` you own the `HICON` — `DestroyIcon` it on exit. If you use `ExtractIconEx`/`LoadIcon`, do not destroy system icons. Using `AppWindow.SetIcon(path)` sets the *window* icon, not the tray icon; they are separate.
4. **Cleanup.** `NIM_DELETE` on exit, or the icon lingers as a ghost until the user hovers the tray. Hook `AppWindow.Closing` **and** `AppDomain.ProcessExit`; `Environment.Exit` skips normal shutdown paths.
5. **DPI.** Pass explicit `cx`/`cy` from `GetSystemMetrics(SM_CXSMICON)` rather than `LR_DEFAULTSIZE` if you support 150%+ scaling.
6. **Right-click menu.** Build a Win32 `HMENU` (`CreatePopupMenu` + `TrackPopupMenuEx` with `TPM_RETURNCMD`), not a WinUI `MenuFlyout` — a `MenuFlyout` needs a `XamlRoot`, and a tray click has none. You must call `SetForegroundWindow(hwnd)` before `TrackPopupMenuEx` or the menu will not dismiss when you click elsewhere (classic Win32 bug).
7. **Explorer restart.** Handle the `TaskbarCreated` broadcast message (`RegisterWindowMessage("TaskbarCreated")`) and re-`NIM_ADD` — otherwise the icon vanishes when Explorer crashes or restarts.

### 3.4 Recommendation

**Use `H.NotifyIcon.WinUI` 2.3.2.** Reasons, in order:

1. It already solves #7 (Explorer restart), #6 (native menu with correct dismiss), #3/#4 (icon lifetime, disposal), and the `Guid`-per-icon registration that keeps the user's "show in tray" preference across restarts — all of which are the parts people get wrong by hand.
2. It is a single `PackageReference` with no WinForms dependency.
3. It supports the exact scenario this app needs: windowless tray-only process (`TaskbarIcon.ForceCreate()`, `Window.Hide()`), plus `EfficiencyModeUtilities.SetEfficiencyMode(true)` which is genuinely useful for a background battery daemon.
4. Raw `Shell_NotifyIcon` is ~150 lines plus a subclassed WndProc plus a second message pump — more surface area than the whole rest of the app.

Caveat to design around: pin 2.3.2, avoid `ContextMenuMode="SecondWindow"` (issue #271), and treat the library as a replaceable seam behind your own `ITrayIcon` interface so a future 2.5.x / .NET 10 move is a one-file change.

---

## 4. Single instance

### 4.1 Is `AppInstance` usable without MSIX?

**Yes.** `Microsoft.Windows.AppLifecycle.AppInstance` is a Windows App SDK API, not a package-identity API. `FindOrRegisterForKey`, `GetCurrent`, `GetActivatedEventArgs`, `RedirectActivationToAsync`, `ProcessId` and `IsCurrent` are all documented as applying to Windows App SDK 1.0 → 2.0 with **no packaging condition**. The official how-to ([Create a single-instanced WinUI app with C#](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-single-instance)) never mentions MSIX and works from a plain `dotnet run`.

**Caveat:** some *adjacent* lifecycle features are identity-dependent — notably `ActivationRegistrationManager` (file/protocol/startup-task registration) and rich `AppActivationArguments` kinds. For plain "second launch → focus first window", no identity is needed. `GetActivatedEventArgs().Kind` will be `Launch` for a command-line launch of an unpackaged app.

### 4.2 Exact pattern

`csproj`:

```xml
<PropertyGroup>
  <DefineConstants>$(DefineConstants);DISABLE_XAML_GENERATED_MAIN</DefineConstants>
</PropertyGroup>
```

`Program.cs` (verbatim shape from the official how-to):

```csharp
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

public class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        bool isRedirect = DecideRedirection();

        if (!isRedirect)
        {
            Application.Start(_ =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }
        return 0;
    }

    private static bool DecideRedirection()
    {
        AppActivationArguments args = AppInstance.GetCurrent().GetActivatedEventArgs();
        AppInstance keyInstance = AppInstance.FindOrRegisterForKey("BattCtl.SingleInstance");

        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += OnActivated;
            return false;
        }

        RedirectActivationTo(args, keyInstance);
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr attrs, bool manualReset, bool initialState, string name);
    [DllImport("kernel32.dll")] private static extern bool SetEvent(IntPtr h);
    [DllImport("ole32.dll")]   private static extern uint CoWaitForMultipleObjects(
        uint flags, uint ms, ulong count, IntPtr[] handles, out uint index);
    [DllImport("user32.dll")]  private static extern bool SetForegroundWindow(IntPtr hWnd);

    private static IntPtr redirectEventHandle = IntPtr.Zero;

    public static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
    {
        redirectEventHandle = CreateEvent(IntPtr.Zero, true, false, null);
        Task.Run(() =>
        {
            keyInstance.RedirectActivationToAsync(args).AsTask().Wait();
            SetEvent(redirectEventHandle);
        });

        const uint CWMO_DEFAULT = 0, INFINITE = 0xFFFFFFFF;
        _ = CoWaitForMultipleObjects(CWMO_DEFAULT, INFINITE, 1,
                                     new[] { redirectEventHandle }, out _);

        var process = Process.GetProcessById((int)keyInstance.ProcessId);
        SetForegroundWindow(process.MainWindowHandle);
    }

    private static void OnActivated(object sender, AppActivationArguments args) { }
}
```

Why the `CreateEvent` + `CoWaitForMultipleObjects` dance: `RedirectActivationToAsync` is async but must be *awaited to completion* or the redirect fails; awaiting it on the STA blocks the STA; `CoWaitForMultipleObjects` is a **non-blocking-to-COM** wait that still pumps. Do not replace it with `.Wait()` on the STA thread.

The `SetForegroundWindow` call is what actually brings the existing window forward. Note it can be refused by Windows' foreground lock; if the window still doesn't come forward, that is a Windows focus-stealing policy, not a bug in this code.

### 4.3 Named mutex — when to use it

Still the standard fallback when you cannot take the `DISABLE_XAML_GENERATED_MAIN` + custom `Main` route (e.g. a shared `Program` you don't control). Its limitations, precisely:

- It only **detects** a second instance; it cannot hand the activation arguments over.
- It **cannot raise the existing window**. The usual workaround is a named pipe or a window-message broadcast (`RegisterWindowMessage` + `PostMessage(HWND_BROADCAST, ...)`) so the first instance restores itself. Without that, launching the app a second time appears to do nothing — a bad user experience.
- It is **per-session** unless you prefix with `Global\`, and `Global\` requires care with multiple user sessions.
- `Mutex` abandoned-mutex (`AbandonedMutexException`) must be handled or a crashed first instance blocks all future launches.

Given that `AppInstance` works unpackaged, **do not use a mutex here.**

---

## 5. Self-contained / no-runtime-install

### 5.1 The two independent switches

| Property | What it bundles | Required for "no installs on target" |
|---|---|---|
| `WindowsAppSDKSelfContained` | The **Windows App SDK** runtime (Microsoft.WindowsAppRuntime, WinUI binaries) | ✅ yes |
| `SelfContained` | The **.NET 9** runtime | ✅ yes |

They are orthogonal. Setting only `WindowsAppSDKSelfContained` still requires the .NET 9 desktop runtime on the machine, and vice versa. The docs say so explicitly: ".NET apps need to be published as self-contained as well to be fully self-contained."

### 5.2 Working csproj for this app

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <UseWinUI>true</UseWinUI>
    <EnableMsixTooling>true</EnableMsixTooling>

    <!-- Unpackaged -->
    <WindowsPackageType>None</WindowsPackageType>

    <!-- No runtime installs on the target machine -->
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
    <SelfContained>true</SelfContained>

    <!-- Leave trimming OFF (see known-broken list) -->
    <PublishTrimmed>false</PublishTrimmed>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="1.7.250606001" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.4948" />
    <PackageReference Include="H.NotifyIcon.WinUI" Version="2.3.2" />
  </ItemGroup>
</Project>
```

Build (from WSL, targeting Windows):

```bash
dotnet.exe publish -c Release -r win-x64 --self-contained true \
  -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false
```

### 5.3 `PublishSingleFile` — does it work with WinUI 3?

**Yes, but only in one exact configuration**, and the docs contradict each other on this, so here is both sides:

- [`Distribute an unpackaged WinUI 3 app`](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app) (ms.date **2026-05-29**): "Starting with Windows App SDK 1.5, **unpackaged, self-contained** WinUI 3 apps support the .NET `PublishSingleFile` deployment model… `PublishSingleFile` is **not** supported for packaged apps (MSIX or packaged with external location) or for framework-dependent apps. Both conditions — unpackaged **and** self-contained — are required."
- [`Windows App SDK deployment guide for self-contained apps`](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps): "`dotnet publish` bundles managed assemblies but **cannot produce a single-file EXE** for WinUI 3 apps — the native Windows App SDK runtime dependencies must remain as separate files."

These reconcile only if you read the second as "without `IncludeAllContentForSelfExtract`": with that property the native dependencies *are* bundled and extracted to a temp directory at first launch. **My reading, not a doc statement.**

The documented required property set:

```xml
<PropertyGroup>
  <WindowsPackageType>None</WindowsPackageType>
  <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
  <SelfContained>true</SelfContained>
  <EnableMsixTooling>true</EnableMsixTooling>
  <IncludeAllContentForSelfExtract>true</IncludeAllContentForSelfExtract>
  <PublishSingleFile>true</PublishSingleFile>
</PropertyGroup>
```

A build-time validation target `WindowsAppSDKSingleFileVerifyConfiguration` **errors** if `EnableMsixTooling`, `WindowsPackageType=None` or `IncludeAllContentForSelfExtract` is missing, and **warns** if `WindowsAppSDKSelfContained` or `SelfContained` is absent.

**Recommendation for this app: `PublishSingleFile=false`.** A battery utility is a small folder; single-file buys you nothing and costs you a first-launch extraction to `%TEMP%`, a `MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY` env-var dependency if you ever disable the UndockedRegFreeWinRT auto-initializer, and `AppWindow.SetIcon`/`ms-appx://` path breakage. Ship a folder (or a WiX/Inno wrapper around the folder) instead.

### 5.4 The bootstrap auto-initializer — and when it is *not* used

`Microsoft.WindowsAppRuntime.Bootstrap.dll` is the **framework-dependent** path only. Verified from the SDK targets, [`Microsoft.WindowsAppSDK.BootstrapCommon.targets`](https://github.com/microsoft/WindowsAppSDK/blob/main/build/NuSpecs/Microsoft.WindowsAppSDK.BootstrapCommon.targets):

```xml
<PropertyGroup Condition="'$(WindowsAppSdkBootstrapInitialize)'=='' and '$(WindowsAppSDKSelfContained)'!='true' and '$(WindowsPackageType)'=='None' and ('$(OutputType)'=='Exe' or '$(OutputType)'=='Winexe')">
    <WindowsAppSdkBootstrapInitialize>true</WindowsAppSdkBootstrapInitialize>
</PropertyGroup>
```

So:

| Configuration | Bootstrap auto-init | UndockedRegFreeWinRT auto-init |
|---|---|---|
| `WindowsPackageType=None`, framework-dependent | **on** (`WindowsAppSdkBootstrapInitialize=true`) | off |
| `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true` | **off** (condition excludes it) | **on** (`WindowsAppSdkUndockedRegFreeWinRTInitialize=true`) |
| MSIX packaged | off (DeploymentManager init instead) | off |

**Therefore: if you go self-contained, do not set `WindowsAppSdkBootstrapInitialize` at all** — it is already skipped, and forcing it true makes your app look for an installed framework package it doesn't need. Conversely, if you ship framework-dependent, you must either leave the auto-initializer alone or call `MddBootstrapInitialize2` yourself (`<WindowsAppSdkBootstrapInitialize>false</WindowsAppSdkBootstrapInitialize>`).

The self-contained analogue you *can* control is `WindowsAppSdkUndockedRegFreeWinRTInitialize` (defaults `true` when self-contained + `WindowsPackageType=None` + `OutputType` is `Exe`/`WinExe`). Leave it `true`.

### 5.5 Known-broken combinations

| Combination | Status | Source |
|---|---|---|
| `PublishSingleFile=true` + **framework-dependent** WinAppSDK | **Broken** — file-not-found / XAML parse failure at launch. Microsoft: "not officially supported with either WindowsAppSdkSelfContained or SelfContained set to false." | [microsoft-ui-xaml#9658](https://github.com/microsoft/microsoft-ui-xaml/issues/9658) (closed as by-design) |
| `PublishSingleFile=true` + `WindowsAppSDKSelfContained=true` + **no** `IncludeAllContentForSelfExtract` | **Broken** — build error from `WindowsAppSDKSingleFileVerifyConfiguration` | unpackage-winui-app.md |
| `PublishSingleFile=true` + `RuntimeIdentifiers` (plural) | **Broken/open** | [WindowsAppSDK#3539](https://github.com/microsoft/WindowsAppSDK/issues/3539) (open) |
| `PublishTrimmed=true` + `SystemBackdrop` subclass defined in a **class library** | **`InvalidCastException`** at runtime; fixed by adding `Microsoft.Windows.CsWinRT` to the library project | [WindowsAppSDK#4802](https://github.com/microsoft/WindowsAppSDK/issues/4802) |
| `PublishTrimmed=true` (WinUI generally) | Known crashes in release; MS docs tell you to set `PublishTrimmed=false` | single-instance how-to, "known issues with trimmed apps" |
| `WindowsAppSDKSelfContained` on a **library** project | Wrong — self-contained is app-project-only | deploy-self-contained-apps.md |
| Self-contained + `PublishSingleFile` + **packaged** app | Not supported | unpackage-winui-app.md |
| Self-contained + `SelfContained=false` | Builds, but needs .NET 9 runtime on target — not "no install" | deploy-self-contained-apps.md |

Also: a small number of WinAppSDK APIs need **separate MSIX packages** even when self-contained (e.g. `PushNotificationManager` needs the *Singleton* package). `AppNotificationManager` does not. For this app, irrelevant — but if you add toast notifications, use `AppNotificationManager`, not `PushNotificationManager`.

---

## 6. Tray + startup without MSIX

### 6.1 HKCU Run vs Task Scheduler

| | **HKCU `...\CurrentVersion\Run`** | **Task Scheduler** |
|---|---|---|
| Setup cost | one registry write | XML task definition + `schtasks` or COM |
| Visible in Settings → Apps → Startup | **Yes** | No (appears under Task Scheduler only) |
| Visible in Task Manager → Startup apps | **Yes** | No |
| User can disable it | Yes, toggle in Settings/Task Manager | Only by editing the task |
| Runs **elevated** at logon | No | **Yes** (`RunLevel=HighestAvailable`) |
| Can delay / retry | No | Yes (`Delay`, `RestartOnFailure`) |
| Needs admin to create | No (HKCU) | Yes for a `\` root task; no for a user task in some configs |

**Recommendation: HKCU Run.** This app is a per-user battery utility that does not need elevation at logon (writing the EC register does, but that is a separate elevated action the user triggers). HKCU Run gives the user a discoverable, reversible off switch in two places — which is the single biggest support-burden reducer for a login item.

```csharp
using Microsoft.Win32;

const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
const string ValueName = "BatteryChargeLimit";

static void SetRunAtLogin(bool enabled, string exePath)
{
    using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)!;
    if (enabled) key.SetValue(ValueName, $"\"{exePath}\" --tray");
    else key.DeleteValue(ValueName, throwOnMissingValue: false);
}
```

Quote the path (`"C:\...\BatteryChargeLimit.exe"`), always — unquoted paths with spaces are both a correctness bug and a classic privilege-escalation vector. Write the **absolute** path, never a relative one; HKCU Run has no working-directory guarantee.

### 6.2 Does an HKCU Run entry show up in Task Manager's Startup tab?

**Yes.** Task Manager's *Startup apps* tab and Settings → Apps → Startup read the same backing store, and the registry Run keys are documented as one of that store's locations. Microsoft's own support article lists them:

> "The startup applications in Windows can be stored in specific registry locations… For applications that run at startup for the current user, the registry path is `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`."
> — [Configure Startup applications in Windows](https://support.microsoft.com/en-us/windows/configure-startup-applications-in-windows-115a420a-0bff-4a6f-90e0-1934c844e473)

and

> "The Task Manager provides the same list of startup applications offered by Settings."

Practical caveats:

- **Startup impact** shows as *Not measured* until Windows has observed a few logons; that is normal, not a bug.
- If the value name or path is wrong you get the Settings row **"We couldn't find this app"** — the doc describes exactly this failure.
- Task Manager shows the **value name**, not the file description. Name the value something human-readable (`Battery Charge Limit`), not `BatteryChargeLimit`.
- The **packaged-vs-unpackaged** difference: a *packaged* app registers startup tasks through its manifest (`windows.startupTask`) and appears with its package display name and icon, and its enabled/disabled state is stored in the package's `StartupTask` state rather than the registry. An unpackaged app has no manifest, so it is just a Run value — same list, but no icon and no publisher string. That is expected, not a defect.
- **Windows 11 26H2 specifics: UNVERIFIED.** I found no doc describing a behavioural change to Run-key startup visibility in 26H2. The mechanism above is the documented one as of the current docs.

---

## 7. Fluent 2 design tokens

Sources: [Fluent 2 — Typography](https://fluent2.microsoft.design/typography), [Layout](https://fluent2.microsoft.design/layout), [Elevation](https://fluent2.microsoft.design/elevation), [Design tokens](https://fluent2.microsoft.design/design-tokens), plus the actual WinUI resource dictionaries in [`microsoft/microsoft-ui-xaml`](https://github.com/microsoft/microsoft-ui-xaml/tree/main/controls/dev/CommonStyles).

### 7.1 Type ramp (Windows / Segoe UI Variable)

Fluent 2's Windows column, verbatim:

| Fluent 2 name | Weight | Size / line height |
|---|---|---|
| Caption | Regular ("small" optical) | 12 / 16 |
| Body | Regular | 14 / 20 |
| Body Strong | Semibold ("text" optical) | 14 / 20 |
| Body Large | Regular | 18 / 24 |
| Subtitle | Semibold ("display" optical) | 20 / 28 |
| Title | Semibold ("display") | 28 / 36 |
| Large Title | Semibold ("display") | 40 / 52 |
| Display | Semibold ("display") | 68 / 92 |

WinUI's own ramp (verified in [`TextBlock_themeresources.xaml`](https://github.com/microsoft/microsoft-ui-xaml/blob/main/controls/dev/CommonStyles/TextBlock_themeresources.xaml)) uses different *style keys* and **omits Caption Strong**:

```xml
<x:Double x:Key="CaptionTextBlockFontSize">12</x:Double>
<x:Double x:Key="BodyTextBlockFontSize">14</x:Double>
<x:Double x:Key="BodyLargeTextBlockFontSize">18</x:Double>
<x:Double x:Key="SubtitleTextBlockFontSize">20</x:Double>
<x:Double x:Key="TitleTextBlockFontSize">28</x:Double>
<x:Double x:Key="TitleLargeTextBlockFontSize">40</x:Double>
<x:Double x:Key="DisplayTextBlockFontSize">68</x:Double>
```

Style → weight mapping as shipped:

| Style key | FontSize | Weight |
|---|---|---|
| `CaptionTextBlockStyle` | 12 | Normal |
| `BodyTextBlockStyle` | 14 | Normal |
| `BodyStrongTextBlockStyle` | 14 | **SemiBold** (inherits `BaseTextBlockStyle`) |
| `BodyLargeTextBlockStyle` | 18 | Normal |
| `BodyLargeStrongTextBlockStyle` | 18 | **SemiBold** |
| `SubtitleTextBlockStyle` | 20 | SemiBold |
| `TitleTextBlockStyle` | 28 | SemiBold |
| `TitleLargeTextBlockStyle` | 40 | SemiBold |
| `DisplayTextBlockStyle` | 68 | SemiBold |

`BaseTextBlockStyle` sets `FontFamily="XamlAutoFontFamily"`, `FontSize=14`, `FontWeight=SemiBold`, `TextTrimming=CharacterEllipsis`, `TextWrapping=Wrap`, `LineStackingStrategy=MaxHeight`, `TextLineBounds=Full`.

**Important, and easy to get wrong:** WinUI's ramp styles **do not set `LineHeight` at all**. The "/16", "/20", "/28" numbers in the design docs are the *natural* line heights of Segoe UI Variable at those sizes, produced by `LineStackingStrategy=MaxHeight`. If you set `LineHeight` explicitly you will get different (usually too tight or too loose) spacing. **To match Fluent 2 exactly, copy the WinUI styles and change only `FontSize`/`FontWeight` — leave `LineHeight` unset.**

If you must have exact pixel line heights (e.g. a pixel-diff test), the mapping is `LineHeight = size * 1.333` for Caption (16/12), `1.428` for Body (20/14), `1.333` for Body Large (24/18), `1.4` for Subtitle (28/20), `1.286` for Title (36/28), `1.3` for Title Large (52/40), `1.353` for Display (92/68) — but note these are the *design intent*, not what the framework does by default.

**Caption Strong does not exist in WinUI.** You must define it:

```xml
<Style x:Key="CaptionStrongTextBlockStyle" TargetType="TextBlock"
       BasedOn="{StaticResource CaptionTextBlockStyle}">
    <Setter Property="FontWeight" Value="SemiBold" />
</Style>
```

### 7.2 Corner radius

Fluent 2 / Fluent UI global tokens ([`borderRadius.ts`](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/borderRadius.ts)):

| Token | Value |
|---|---|
| `borderRadiusNone` | 0 |
| `borderRadiusSmall` | **2** |
| `borderRadiusMedium` | **4** |
| `borderRadiusLarge` | **6** |
| `borderRadiusXLarge` | **8** |
| `borderRadius2XLarge` | 12 |
| `borderRadius3XLarge` | 16 |
| `borderRadiusCircular` | 10000 |

WinUI exposes exactly two, verified in [`CornerRadius_themeresources.xaml`](https://github.com/microsoft/microsoft-ui-xaml/blob/main/controls/dev/CommonStyles/CornerRadius_themeresources.xaml):

```xml
<CornerRadius x:Key="ControlCornerRadius">4,4,4,4</CornerRadius>   <!-- = Fluent2 medium -->
<CornerRadius x:Key="OverlayCornerRadius">8,8,8,8</CornerRadius>   <!-- = Fluent2 xLarge -->
```

Practical mapping: **controls → 4**, **flyouts/dialogs/popups → 8**, **cards → 8** (Fluent 2 uses xLarge for cards), inline chips/badges → 2–4.

### 7.3 Spacing scale

Fluent 2 global ramp is a 4 px base with three off-grid values (2, 6, 10) for icon alignment:

| Token | px | | Token | px |
|---|---|---|---|---|
| `sizeNone` | 0 | | `size200` | 20 |
| `size20` | 2 | | `size240` | 24 |
| `size40` | 4 | | `size280` | 28 |
| `size60` | 6 | | `size320` | 32 |
| `size80` | 8 | | `size360` | 36 |
| `size100` | 10 | | `size400` | 40 |
| `size120` | 12 | | `size480` | 48 |
| `size160` | 16 | | `size520` | 52 |
| | | | `size560` | 56 |

WinUI has **no built-in spacing token set** — there are no `SpacingSmall`/`SpacingMedium` resources. Define your own; the practical subset is 4 / 8 / 12 / 16 / 24 / 32:

```xml
<x:Double x:Key="SpaceXS">4</x:Double>
<x:Double x:Key="SpaceS">8</x:Double>
<x:Double x:Key="SpaceM">12</x:Double>
<x:Double x:Key="SpaceL">16</x:Double>
<x:Double x:Key="SpaceXL">24</x:Double>
<x:Double x:Key="SpaceXXL">32</x:Double>
```

Breakpoints, if you need them: small 320–479, medium 480–639, large 640–1023, x-large 1024–1365, xx-large 1366–1919, xxx-large 1920+.

### 7.4 Elevation / shadows

Fluent 2 defines elevation as **two stacked shadows** whose blur and Y-offset scale with a multiplier *n*:

- Shadow 1: blur = `1 × n`, x = 0, y = `0.5 × n`, opacity **14%** light / **28%** dark
- Shadow 2: blur = `1 × n`, x = 0, y = `0.5 × n`, opacity **14%** light / **14%** dark

Documented levels: **shadow2**, **shadow4**, **shadow8**, **shadow16**, **shadow28**, **shadow64**. Usage per the Fluent 2 elevation page:

| Level | Used for |
|---|---|
| shadow2 | cards without an edge; FAB when pressed |
| shadow4 | cards, grid items, list items |
| shadow8 | (mid-level surfaces) |
| shadow16 / shadow28 | flyouts, dialogs, command surfaces |
| shadow64 | (top-level / modal) |

On colour surfaces, opacity is adjusted by surface luminosity:
`shadow1 opacity = round(42 − 0.116 × L)`, `shadow2 opacity = round(34 − 0.09 × L)`, where `L = 0.2126R + 0.7152G + 0.0722B`.

**WinUI does not expose elevation tokens.** The WinUI mechanism is `ThemeShadow` + `UIElement.Translation.Z`:

```xml
<Grid x:Name="Card" CornerRadius="8" Padding="16"
      Background="{ThemeResource CardBackgroundFillColorDefaultBrush}">
    <Grid.Shadow>
        <ThemeShadow />
    </Grid.Shadow>
    <Grid.Translation>
        <Vector3 x:Key="Z" Z="8" />   <!-- set in code instead; see below -->
    </Grid.Translation>
</Grid>
```

```csharp
using System.Numerics;
using Microsoft.UI.Xaml.Media;

Card.Shadow = new ThemeShadow();
Card.Translation = new Vector3(0, 0, 8);   // "shadow8"-ish elevation
```

The docs' rule of thumb: **"For most Popup UI, the recommended default elevation relative to the app background content is 32 effective pixels."** So: cards → Z = 8, flyouts/popups → Z = 32. `ThemeShadow` only produces **rectangular** shadows; for anything else you need `DropShadow` (Composition).

Note `ThemeShadow` requires the caster to be in a `Popup` or to have receivers registered; inside a normal page it works against the app background automatically.

### 7.5 Accent colour ramp

WinUI exposes seven colour resources, all `Windows.UI.Color`, all `{ThemeResource}`-able. Verified in [`FrameworkTheming.cpp`](https://github.com/microsoft/microsoft-ui-xaml/blob/main/dxaml/xcp/components/theming/FrameworkTheming.cpp):

```
SystemAccentColor
SystemAccentColorDark1   SystemAccentColorDark2   SystemAccentColorDark3
SystemAccentColorLight1  SystemAccentColorLight2  SystemAccentColorLight3
```

plus `SystemColorControlAccentColor` (the same accent as a `Color`, for control templates).

Semantics: `Light1..3` are progressively **lighter** (for dark theme / hover states), `Dark1..3` progressively **darker** (for light theme / pressed states). The mapping to WinRT is `UIColorType.AccentLight1/2/3` and `AccentDark1/2/3`.

**Building an accessible tint.** The system ramp is *not* guaranteed to hit contrast ratios — it is a lightness ladder, not an accessibility ladder. Do this instead:

```csharp
using Windows.UI;
using Windows.UI.ViewManagement;

static Color AccessibleAccent(double targetContrast, Color background, bool lighten)
{
    var ui = new UISettings();
    Color accent = ui.GetColorValue(UIColorType.Accent);

    // Walk the ladder away from the background until contrast is met.
    Color[] ladder = lighten
        ? new[] { accent,
                  ui.GetColorValue(UIColorType.AccentLight1),
                  ui.GetColorValue(UIColorType.AccentLight2),
                  ui.GetColorValue(UIColorType.AccentLight3) }
        : new[] { accent,
                  ui.GetColorValue(UIColorType.AccentDark1),
                  ui.GetColorValue(UIColorType.AccentDark2),
                  ui.GetColorValue(UIColorType.AccentDark3) };

    foreach (var c in ladder)
        if (ContrastRatio(c, background) >= targetContrast) return c;

    return lighten ? Colors.White : Colors.Black;
}

static double ContrastRatio(Color a, Color b)
{
    double la = RelativeLuminance(a), lb = RelativeLuminance(b);
    return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
}

static double RelativeLuminance(Color c)
{
    static double Ch(byte v)
    {
        double s = v / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
    return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
}
```

Rules of thumb that matter more than the maths:

- **Never put accent-coloured body text on a Mica/Acrylic surface.** Microsoft's own guidance: "We don't recommend placing accent-colored text on your acrylic surfaces because these combinations are likely to not pass minimum contrast ratio requirements at the default 14px font size."
- Use `SystemAccentColor` as a **fill** and white/`TextOnAccentFillColorPrimaryBrush` as the label; that pairing is contrast-checked by the system.
- For focus rings use `FocusVisualPrimaryBrush` / `FocusVisualSecondaryBrush`, not raw accent.
- In **High Contrast**, ignore the accent ladder entirely and use `SystemColorHighlightColor` / `SystemColorHighlightTextColor`.

### 7.6 Segoe UI Variable optical sizes — what WinUI does for you

Segoe UI Variable has two axes: `wght` and `opsz` (8pt → 36pt). Per [Typography in Windows](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography):

> "The optical size axis (`opsz`) is **automatic and on by default**… When using **XAML common controls**, the Segoe UI Variable font will be selected by default for supported languages. When this font or another variable font with an optical axis is used, **the optical size will automatically match the requested font-size**."

So:

| Context | Who sets the optical size |
|---|---|
| Any WinUI `TextBlock`/`Control` with default `FontFamily` (`XamlAutoFontFamily` → `"Segoe UI Variable"` on Win11) | **WinUI / DirectWrite, automatically.** Set `FontSize` and the correct optical instance is chosen. |
| Explicit `FontFamily="Segoe UI Variable Text"` or `"Segoe UI Variable Display"` | **You**, manually — this *pins* the optical size and defeats auto-selection. **Don't do it.** |
| `WebView2` content | **You** — in CSS: `font-family: "Segoe UI Variable", "Segoe UI", sans-serif;` (the variable font then auto-selects opsz from `font-size`) |
| Custom DirectWrite/Composition text | **You** — request the `Segoe UI Variable` family and let DWrite pick opsz from the font size |
| `FontFamily="Segoe UI"` (old family) | N/A — no optical axis, no auto scaling |

The naming `"Regular small"`, `"Semibold text"`, `"Semibold display"` in the Fluent 2 table is the *design* vocabulary for which optical instance is intended at that size — not a `FontFamily` you should type. The rule Fluent uses: **opsz "small" ≤ 12 px, "text" 14–18 px, "display" ≥ 20 px.**

Also, hard floors from the same doc: "Minimum values: **14px Semibold, 12px Regular** — text smaller than these sizes and weights are illegible in some languages." Do not go below 12 px anywhere.

### 7.7 Where the WinUI theme resources actually live

- **Design-time copies** (readable, for copying values): `C:\Program Files (x86)\Windows Kits\10\DesignTime\CommonConfiguration\Neutral\UAP\<SDK version>\Generic\themeresources.xaml` and `generic.xaml`. These are *not* used at runtime.
- **Runtime**: the dictionaries live inside the WinUI runtime; `{ThemeResource ...}` resolves in-process. You cannot "read" them at runtime as files.
- **Public source (the real answer):** [`microsoft/microsoft-ui-xaml/controls/dev/CommonStyles/`](https://github.com/microsoft/microsoft-ui-xaml/tree/main/controls/dev/CommonStyles) — `TextBlock_themeresources.xaml`, `CornerRadius_themeresources.xaml`, `Button_themeresources.xaml`, etc. This is the authoritative, MIT-licensed source and it is what I quoted above.
- Docs index: [XAML theme resources](https://learn.microsoft.com/en-us/windows/apps/develop/platform/xaml/xaml-theme-resources).

---

## 8. What not to do — silent launch failures and blank windows

Ordered by how often they bite, with the exact symptom.

### 8.1 Missing or stale `resources.pri`

**Symptom:** process starts, no window, exits immediately — or a window with unstyled/blank content, or `XamlParseException` "Cannot locate resource from 'ms-appx:///…'".

`resources.pri` carries compiled XAML resource lookup (including `XamlControlsResources`). It is generated at build time and **must sit next to the exe**. It goes missing when:

- you copy only the `.exe` out of `bin\`
- you publish with `--no-build` ([microsoft-ui-xaml#9660](https://github.com/microsoft/microsoft-ui-xaml/issues/9660), open)
- you use `PublishSingleFile` without `IncludeAllContentForSelfExtract` extraction configured correctly
- your installer filters `*.pri` out

**Do:** always deploy the whole output folder; verify `resources.pri` exists next to the exe in CI; never hand-copy the exe.

### 8.2 `DISABLE_XAML_GENERATED_MAIN` misconfiguration

Two failure modes, both fatal and both confusing:

1. **Defined but no `Main` of your own** → build error `CS5001: Program does not contain a static 'Main' method`. (Annoying but loud.)
2. **Defined, `Main` exists, but it skips the required bootstrap** → process starts and dies silently with no window and no exception dialog. You **must** have:
   ```csharp
   WinRT.ComWrappersSupport.InitializeComWrappers();   // ← forgetting this = silent death
   Application.Start(_ => { /* set DispatcherQueueSynchronizationContext */ _ = new App(); });
   ```
   Forgetting `InitializeComWrappers` is the classic blank-window cause. Forgetting to set `SynchronizationContext` makes async continuations resume on the wrong thread and produces random `RPC_E_WRONG_THREAD` later.

**Do:** if you define the constant, copy the whole `Program.cs` shape from section 4.2 rather than writing it from scratch.

### 8.3 `XamlControlsResources` not merged

**Symptom:** `XamlParseException` at startup — "The resource 'X' could not be found" — or completely unstyled controls (default `TextBlock` sizes, invisible `Button` chrome).

Every WinUI 3 app's `App.xaml` **must** merge it:

```xml
<Application.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <XamlControlsResources xmlns="using:Microsoft.UI.Xaml.Controls" />
            <!-- your dictionaries after this one -->
        </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
</Application.Resources>
```

Two ways this breaks: (a) omitted entirely (e.g. you replaced `App.xaml` with a minimal version); (b) placed **after** your own dictionaries, so your `{StaticResource}` lookups in those dictionaries fail because `StaticResource` resolves at parse time in declaration order.

**Do:** `XamlControlsResources` first, always. Anything you reference from it must come after it.

### 8.4 `MicaController` / backdrop throwing

**Symptom:** `COMException` / `InvalidCastException` / `ArgumentException` on `new MicaController()` or on assignment, usually at window construction → app exits before showing anything.

Causes seen in the wild:

- Creating a controller **before** the window has a valid HWND / before `Activate()`.
- Calling `AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>())` **without `using WinRT;`** — the `As<T>()` extension is what makes the cast work.
- A `SystemBackdrop` **subclass defined in a class library** combined with `PublishTrimmed=true` → `InvalidCastException: Specified cast is not valid` ([WindowsAppSDK#4802](https://github.com/microsoft/WindowsAppSDK/issues/4802)). Fix: `PublishTrimmed=false`, or add `Microsoft.Windows.CsWinRT` to the library.
- Mixing WinAppSDK versions between the app project and a packaging project ([microsoft-ui-xaml#8495](https://github.com/microsoft/microsoft-ui-xaml/issues/8495) — the reported "Mica crashes" turned out to be exactly this).
- Customising `TintColor`/`TintOpacity`/`LuminosityOpacity`/`FallbackColor` on a controller and then expecting the theme to still be handled — after you touch any of those four, the controller **stops** applying Light/Dark defaults on theme change. You must update them yourself.

**Do:** prefer `Window.SystemBackdrop = new MicaBackdrop()` (it manages the controller lifetime), guard with `IsSupported()`, never subclass `SystemBackdrop` from a library, and keep one WinAppSDK version across the solution.

### 8.5 `AppWindow` null / `TitleBar` null before `Activate()`

**Symptom:** `NullReferenceException` on `AppWindow.TitleBar.…` or `AppWindow.TitleBar` is `null`.

- `Window.AppWindow` exists from WinAppSDK **1.3**, but the underlying HWND only exists once the window is created. Reading it in a field initialiser or before `InitializeComponent()` can give you nothing useful.
- **`AppWindow.TitleBar` returns `null` on Windows 10** ([microsoft-ui-xaml#6101](https://github.com/microsoft/microsoft-ui-xaml/issues/6101), [#7328](https://github.com/microsoft/microsoft-ui-xaml/issues/7328)). Always gate:
  ```csharp
  if (AppWindowTitleBar.IsCustomizationSupported())
  {
      AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
  }
  ```
- `XamlRoot` is `null` until the element is in the visual tree — i.e. **not** in the constructor. `root.XamlRoot` right after `InitializeComponent()` is `null` by design ([microsoft-ui-xaml#4242](https://github.com/microsoft/microsoft-ui-xaml/issues/4242)). Use the `Loaded` event.
- `AppWindow.Presenter` can be `null` with NativeAOT in release mode ([microsoft-ui-xaml#10471](https://github.com/microsoft/microsoft-ui-xaml/issues/10471)).

**Do:** construct the window, then `Activate()`, then touch `AppWindow`/`TitleBar`; use `Loaded` for anything needing `XamlRoot`; guard `IsCustomizationSupported()`.

### 8.6 DPI and manifest issues

**Symptom:** blurry UI, wrong-sized window, hit-testing offset by the DPI scale, or a window that renders at 96 DPI on a 150% monitor.

Unpackaged WinUI 3 apps have **no package manifest**, so DPI awareness comes **only** from `app.manifest` (the application manifest embedded in the exe). The WinUI template ships:

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <assemblyIdentity version="1.0.0.0" name="BattCtl.app"/>
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/PM</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2, PerMonitor</dpiAwareness>
    </windowsSettings>
  </application>
</assembly>
```

and `csproj`:

```xml
<ApplicationManifest>app.manifest</ApplicationManifest>
```

Things that break:

- **Omitting `<ApplicationManifest>`** → the app is DPI-unaware, everything is bitmap-scaled and blurry.
- **Setting DPI awareness programmatically instead** (`SetProcessDpiAwarenessContext`) *after* WinUI has initialised → fails or is ignored; the manifest wins because it is applied at process start.
- **`AppWindow` uses physical pixels.** `AppWindow.Move/Resize`, `TitleBar.LeftInset`, `RightInset`, `Height` are all physical. Mixing them with XAML DIPs without dividing by `XamlRoot.RasterizationScale` gives wrong geometry — this is the single most common custom-title-bar bug.
- **Multi-monitor DPI changes** without handling `XamlRoot.Changed` → stale layout. (And note open issue [microsoft-ui-xaml#12464](https://github.com/microsoft/microsoft-ui-xaml/issues/12464): an unpackaged WinUI 3 window can keep a stale frame after moving to a monitor with a different scale on 2.3.1/2.6.4 — **UNVERIFIED** whether 1.7.3 is affected.)
- **Missing `<assemblyIdentity>`** or a malformed manifest → the exe fails to start with `ERROR_SXS_CANT_GEN_ACTCTX` (0xc0150002), which looks like a missing-DLL error and sends people down the wrong path.

### 8.7 Others worth pre-empting

| Pitfall | Symptom | Fix |
|---|---|---|
| `WindowsPackageType` not `None` | `REGDB_E_CLASSNOTREG` / app looks for package identity at launch | set it, and pick the *Unpackaged* launch profile in VS |
| Bootstrapper can't find the runtime | exits before UI, error only in **Event Viewer → Application** | install the runtime, or go `WindowsAppSDKSelfContained` |
| `Microsoft.WindowsAppRuntime.Bootstrap.dll` not next to exe (framework-dependent) | silent exit | deploy the whole folder |
| Two `Main` methods | `CS0017` | only one `DISABLE_XAML_GENERATED_MAIN` path |
| `Window.Content` never set / `InitializeComponent` not called | blank window | check the XAML `x:Class` matches the code-behind namespace |
| `App.xaml` `x:Class` mismatch | `XamlParseException` at startup | match exactly |
| Tray icon created before `Activate()` | icon never appears | create after the HWND exists |
| `Environment.Exit` in a tray app | tray ghost icon | `TaskbarIcon.Dispose()` / `NIM_DELETE` first |

---

## 9. Verification status

| Claim | Status | Evidence |
|---|---|---|
| `Window.SystemBackdrop` is 1.3+ | ✅ verified | API docs "Applies to 1.3…2.0"; system-backdrops.md |
| `MicaBackdrop` does not check `IsSupported()` | ✅ verified | `MicaBackdrop.cpp` source |
| Mica→solid fallback conditions (transparency off, battery saver, low-end HW, inactive, <22000) | ✅ verified | mica.md |
| Mica works unpackaged | ✅ documented / ⚠️ not run on target | no packaging gate in docs or API; unpackaged Win32 Mica sample |
| `TitleBar` control is new in 1.7 | ✅ verified | 1.7 Preview 1 release notes API list |
| Mica needs `ExtendsContentIntoTitleBar` to reach the title bar | ✅ verified | title-bar.md TIP + caption-button transparency section |
| `H.NotifyIcon.WinUI` 2.3.2 is the last `net9.0` build | ✅ verified | NuGet nuspec TFM + dependency dump |
| `H.NotifyIcon.WinUI` 2.3.2 works on 1.7.250606001 | ⚠️ **UNVERIFIED** | min dep is `>= 1.6.250108002`; not explicitly tested |
| `H.NotifyIcon` `SecondWindow` mode crash | ⚠️ reported, open | issue #271 (WinAppSDK 2.3.1, VS 2026) |
| `AppInstance` works unpackaged | ✅ documented / ⚠️ not run | API docs list no packaging condition; official how-to |
| `WindowsAppSdkBootstrapInitialize` is skipped when self-contained | ✅ verified | `BootstrapCommon.targets` condition |
| `PublishSingleFile` supported unpackaged+self-contained | ✅ documented, ⚠️ docs contradict | unpackage-winui-app.md vs deploy-self-contained-apps.md |
| `PublishSingleFile` + framework-dependent is broken | ✅ verified | microsoft-ui-xaml#9658 (by-design) |
| `PublishTrimmed` + `SystemBackdrop` subclass in lib breaks | ✅ verified | WindowsAppSDK#4802 |
| HKCU Run entries appear in Task Manager Startup tab | ✅ verified | Microsoft support article |
| Fluent 2 type ramp values | ✅ verified | fluent2.microsoft.design/typography (Windows column) |
| WinUI ramp omits `LineHeight` and Caption Strong | ✅ verified | `TextBlock_themeresources.xaml` |
| Corner radius 2/4/6/8, WinUI 4/8 | ✅ verified | `borderRadius.ts`, `CornerRadius_themeresources.xaml` |
| Spacing ramp | ✅ verified | fluent2.microsoft.design/layout |
| Elevation multipliers + shadow2/4/8/16/28/64 | ✅ verified | fluent2.microsoft.design/elevation |
| Accent ramp names | ✅ verified | `FrameworkTheming.cpp` |
| `opsz` is automatic in XAML | ✅ verified | typography.md |
| Windows 11 26H2 startup-app changes | ⚠️ **UNVERIFIED** | no doc found |
| 1.7.3 affected by microsoft-ui-xaml#12464 (stale frame on DPI change) | ⚠️ **UNVERIFIED** | issue names 2.3.1 and 2.6.4, not 1.7.x |

---

## Appendix — sources

**Microsoft Learn**
- [System backdrops (Mica/Acrylic)](https://learn.microsoft.com/en-us/windows/apps/develop/ui/system-backdrops)
- [Mica material](https://learn.microsoft.com/en-us/windows/apps/design/style/mica)
- [Title bar customization](https://learn.microsoft.com/en-us/windows/apps/develop/title-bar)
- [TitleBar control](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/title-bar)
- [Create a single-instanced WinUI 3 app with C#](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-single-instance)
- [App instancing with the app lifecycle API](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/applifecycle/applifecycle-instancing)
- [Windows App SDK deployment guide for self-contained apps](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
- [Distribute an unpackaged WinUI 3 app](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app)
- [Windows App SDK deployment overview](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/deploy-overview)
- [Use the Windows App SDK runtime for apps packaged with external location or unpackaged](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/use-windows-app-sdk-run-time)
- [Windows App SDK 1.7 release notes](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/release-notes/windows-app-sdk-1-7)
- [Typography in Windows](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography)
- [XAML theme resources](https://learn.microsoft.com/en-us/windows/apps/develop/platform/xaml/xaml-theme-resources)
- [ThemeShadow](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.media.themeshadow)
- [Configure Startup applications in Windows](https://support.microsoft.com/en-us/windows/configure-startup-applications-in-windows-115a420a-0bff-4a6f-90e0-1934c844e473)

**Source / samples**
- [microsoft/microsoft-ui-xaml — CommonStyles](https://github.com/microsoft/microsoft-ui-xaml/tree/main/controls/dev/CommonStyles)
- [MicaBackdrop.cpp](https://github.com/microsoft/microsoft-ui-xaml/blob/main/controls/dev/Materials/MicaBackdrop/MicaBackdrop.cpp)
- [WinUI Gallery — SampleSystemBackdropsWindow](https://github.com/microsoft/WinUI-Gallery/blob/main/WinUIGallery/SampleSupport/SamplePages/SampleSystemBackdropsWindow.xaml.cs)
- [WindowsAppSDK — BootstrapCommon.targets](https://github.com/microsoft/WindowsAppSDK/blob/main/build/NuSpecs/Microsoft.WindowsAppSDK.BootstrapCommon.targets)
- [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) / [Windowless WinUI sample](https://github.com/HavenDV/H.NotifyIcon/tree/master/src/apps/H.NotifyIcon.Apps.WinUI.Windowless)
- [Fluent UI — borderRadius.ts](https://github.com/microsoft/fluentui/blob/master/packages/tokens/src/global/borderRadius.ts)

**Fluent 2**
- [Typography](https://fluent2.microsoft.design/typography) · [Layout](https://fluent2.microsoft.design/layout) · [Elevation](https://fluent2.microsoft.design/elevation) · [Design tokens](https://fluent2.microsoft.design/design-tokens)

# Design brief — Acer Battery UI

## What this is

A small Windows utility that replaces Acer Care Center's battery panel, which
hangs on "still initializing". It does one job: show the battery honestly and
let the user flip the 80% charge limit. It is a *utility*, not a dashboard and
not a marketing page. It lives in the tray and is opened for ten seconds at a
time.

## The user

One person: the owner of this laptop, a developer, running dark mode at 100%
scale on a 1920x1080 panel. They already use the CLI. They are not a
first-time Windows user. They will notice if the app looks like a web page in a
window, and they will notice if it lies about battery state.

## Hard environment facts (measured, not assumed)

| fact | value |
|------|-------|
| screen | 1920x1080, **100% scale** — 1 device pixel per DIP, no fractional scaling to hide behind |
| theme | **dark mode is active** (`AppsUseLightTheme=0`, `SystemUsesLightTheme=0`) |
| transparency | enabled — Mica is available |
| system accent | blue/indigo ramp, light `#D7E0F3` → dark `#111455` |
| mailbox round trip | **~930 ms** per request |

Design dark first. Light mode must work but is secondary. Because the scale is
1.0, there is no sub-pixel softening: a 1px border is exactly 1px, a misaligned
baseline is visible, and a 12px label really is 12px. Precision matters more
here than on a 150% laptop.

## The 930 ms constraint — this is the design problem

Every state change takes about a second. A switch that freezes for a second
feels broken; a switch that responds instantly and then settles feels solid.

Therefore:

- **Optimistic toggle.** The switch moves on tap. A pending treatment (subtle
  dim, progress ring on the control, not a page-level spinner) covers the
  round trip. If the read-back disagrees, the switch animates back and a
  non-blocking inline message explains it.
- **Cached first paint.** Persist the last snapshot; render it immediately on
  open, then refresh in the background and cross-fade the changed values.
  The app must never open to an empty skeleton after the first run.
- **No blocking spinners** for routine refresh. Only the true first launch
  (no cache) gets a skeleton, and it should be a shape-matched skeleton, not a
  centred ring.

## Scope (agreed)

Dashboard, Health, Settings — three pages. Charge limit on/off, battery
percent, health/wear, design vs full capacity, cycle count, tray icon with a
quick toggle, launch-at-login. Plus About, the restore log, a manual re-apply,
and status export, since the scope was set to "compact lengkap".

## What the app must never do

- Never show a number it does not have. `null` renders as an em-dash or an
  explicit "unknown", never as `0` and never as a guess.
- Never claim the limit is on when the read-back failed. The authoritative
  value comes from the worker; the UI is a view.
- Never block the window while waiting on the mailbox.
- Never show a modal dialog for a recoverable condition.
- Never impersonate Acer. No Acer logo, no Acer wordmark, no Care Center
  visual pastiche. This is a third-party replacement; it needs its own neutral
  identity. Do not copy their icon.

## Resolved technical decisions (from verified research)

Read `docs/winui3-research.md` for detail; these are settled, do not re-litigate:

- **Backdrop**: `MicaBackdrop { Kind = MicaKind.BaseAlt }` guarded by
  `MicaController.IsSupported()`, then `DesktopAcrylicController.IsSupported()`,
  then a solid fallback. Mica **silently no-ops** when unsupported — it never
  throws, so an unguarded assignment looks fine and renders flat. Mica also
  degrades under **battery saver**, which is a real state for a battery app:
  design a solid fallback that looks intentional, not broken.
- **Title bar**: the WinAppSDK 1.7 `TitleBar` control is already in place.
  `ExtendsContentIntoTitleBar = true` must be set in the constructor, then
  `SetTitleBar()` in code. System caption buttons are transparent over Mica.
  You may restyle the title bar contents but keep the control.
- **Tray**: pin `H.NotifyIcon.WinUI` **2.3.2** (verified: 2.4.1 is net10-only
  and fails restore on net9 with NU1202). Needs `ForceCreate()` in a
  windowless app and `Dispose()` before exit, or a ghost icon remains in the
  tray after quit.
- **Startup**: HKCU `Run` key, no admin, and it does appear in Task Manager's
  Startup tab. `StartupService` exists.
- **Type ramp**: Caption 12/16, Body 14/20, BodyStrong 14/20 SemiBold,
  BodyLarge 18/24, Subtitle 20/28 SemiBold, Title 28/36 SemiBold,
  TitleLarge 40/52, Display 68/92 SemiBold. There is **no Caption Strong** in
  WinUI — define it yourself if you need it.
- **Radii**: 2/4/6/8/12/16. Cards 8, controls 4 (`ControlCornerRadius`).
- **Spacing**: 4px base. WinUI has no spacing tokens — define 4/8/12/16/24/32.
- **Elevation**: `ThemeShadow` + `Translation.Z`. Cards Z=8, flyouts Z=32.
  ThemeShadow is rectangular only — it will not follow a rounded card.
- **Accent**: `SystemAccentColor` + `Light1..3` / `Dark1..3` are a *lightness*
  ladder, not an accessibility ladder. Verify WCAG contrast yourself; never put
  accent-coloured body text on Mica.

## Open questions to resolve in design

- How much health data belongs on the dashboard versus only on Health, given
  the user opens this for ten seconds.
- Whether the tray flyout should host the toggle, or only the tray menu.
- Icon identity. It must not be Acer's. Propose a mark; a battery glyph that is
  clearly not theirs is enough.

## Non-goals

- No fan/performance controls. No WMI surface is verified for them and
  guessing at NitroSense internals is how machines get bricked.
- No telemetry, no network, no accounts.

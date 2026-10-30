# acer-battery-charge-limit

Control the Acer **Battery Charge Limit** (80% "health mode") -- turn it on/off and read
its state **without** the buggy Acer Care Center UI.

Acer Care Center sometimes hangs at *"still initializing"* (notably after hibernate/resume),
which makes the battery-limit toggle unusable. The limit itself is a firmware/EC flag exposed
over WMI, so we can drive it directly and skip the UI entirely.

Two front ends ship here:

| | |
|---|---|
| `battctl` | command line: `status` / `on` / `off` / `toggle` |
| **Acer Battery** (`src/BattCtl.App`) | a Fluent WinUI 3 desktop app with a tray icon |

Both talk to the same SYSTEM-scheduled-task worker, so neither can be broken by a hung
Care Center.

```
> battctl status
Battery Charge Limit: ON  (charge limited to 80%)

> battctl off
Battery Charge Limit: set -> OFF (charges to 100%)
```

## Desktop app

A native WinUI 3 app in the Windows 11 Fluent style (Mica backdrop, Segoe UI Variable,
Fluent card and spacing tokens), built as the replacement for Care Center's battery panel.

- **Dashboard** -- battery percentage, charge state, and the 80% limit switch.
- **Health** -- design vs full-charge capacity, wear, cycle count, voltage, charge rate.
- **Settings** -- launch at login, re-apply the saved limit, restore log, JSON export.
- **Tray** -- icon with a quick-toggle menu and a live tooltip.

It reads battery telemetry even when the firmware charge-limit interface is unreachable, and
it degrades honestly instead of guessing: an unknown limit state is shown as unknown, never
as "off", and a value the firmware does not report is shown as an em-dash, never as `0`.

Every worker round trip costs about a second, so the app paints the last known reading
immediately, updates in the background, and moves the switch optimistically before the
firmware confirms it.

Install the CLI first, then the app:

```powershell
.\battctl-install.cmd     # CLI + worker + scheduled tasks
.\install-ui.ps1          # app; also refreshes the worker and CLI
```

`install-ui.ps1` deploys the app **and** the worker/CLI together on purpose -- they share the
mailbox protocol described below, and deploying only half reintroduces the concurrency bug
fixed in this version.

The app is self-contained: no Windows App SDK runtime needs to be installed separately.
Closing the window keeps it in the tray; use **Quit** in the tray menu to exit. On Windows 11
new tray icons start in the overflow flyout (`^`) -- drag it onto the taskbar to pin it.

### Building from source

Needs the .NET 9 SDK and the Windows 10/11 SDK.

A plain `dotnet build` **fails** when the sources live on a WSL UNC path, because the Windows
SDK manifest step (`mt.exe`) cannot parse UNC paths and returns `c1010070`. Use the wrapper,
which redirects build output to a local drive and keeps the sources in WSL:

```powershell
.\build.ps1        # build only
.\run.ps1          # build + launch (stops a running instance first)
```

Name conflicts: the app is unpackaged and uses `AppInstance` for single-instance redirect.

CI runs `dotnet build` on `windows-latest` for every change under `src/`, and fails if
`resources.pri` or the WinUI assemblies are missing from the output — a missing `resources.pri`
makes the app exit silently or open an unstyled window.

## Requirements

- Windows 10/11, an Acer laptop whose firmware exposes the WMI charge-limit interface
- Administrator rights **only for the one-time install** (afterwards: no UAC prompts)

Verified on **Acer Nitro AN515-57** (BIOS V1.22). Likely works on many modern Acer
Aspire / Swift / Nitro / Predator models -- see [Supported models](#supported-models).

## Install

Clone (or download) this repo, then run the installer once:

```powershell
git clone https://github.com/bimoalfarrabi/acer-battery-charge-limit
cd acer-battery-charge-limit
.\battctl-install.cmd        # one UAC prompt
```

The installer:

- copies the worker and front end to `C:\Program Files\battctl\` (the worker is admin-only, so `SYSTEM` can run it safely),
- creates the mailbox `C:\ProgramData\battctl\mailbox\` and grants *you* modify access,
- registers the on-demand scheduled task **`BattCtl`** (runs as `SYSTEM`, highest privileges, 60 s cap),
- registers the boot/resume task **`BattCtlResume`**, which re-applies the saved state,
- adds `C:\Program Files\battctl` to the system `PATH`.

After installation, `battctl` is available in any **new** terminal (already-open shells keep the old
`PATH`). To update after `git pull`, re-run `.\battctl-install.cmd`. To run `battctl` from **WSL**,
see [WSL usage](#wsl-usage).

## Usage

```powershell
battctl status     # show current state          (default)
battctl on         # enable the 80% charge limit
battctl off        # disable it (charge to 100%)
battctl toggle     # flip the current state
```

`battctl on` and `battctl off` remember your choice in
`C:\ProgramData\battctl\desired.txt`. The saved state is re-applied automatically after
hibernate or sleep and at boot. For troubleshooting the re-assert, see
`C:\ProgramData\battctl\restore.log`.

Exit codes:

| Code | Meaning |
|------|---------|
| `0`  | success |
| `1`  | WMI/provider failure |
| `2`  | feature not present on this model (no `BatteryControl` instance) |
| `3`  | timed out waiting for the task (WMI provider stuck -- `schtasks /end /tn BattCtl`), or another battctl request held the mailbox |
| `4`  | bad argument, or the tool is not installed |

## How it works

The limit is a firmware flag reachable through WMI:

- namespace `root\WMI`, class **`BatteryControl`** (ACPI instance `ACPI\PNP0C14\APGe_0`)
- read : `GetBatteryHealthControlStatus(uBatteryNo=1, uFunctionQuery=1, ...)`
  -> `.uFunctionStatus[0]` = `1` (ON / 80%) or `0` (OFF / 100%)
- write: `SetBatteryHealthControl(uBatteryNo=1, uFunctionMask=1, uFunctionStatus=0|1, ...)`

The WMI instance is **only visible to an elevated caller**, which is why the work runs inside a
`SYSTEM` scheduled task rather than prompting for UAC on every call. Each front end drops a
nonce-tagged request into a mailbox file, triggers the task, waits for the matching result and
prints it.

```
you -> battctl.ps1 / Acer Battery -> request.txt -> [BattCtl task / SYSTEM] -> WMI BatteryControl
                                            ^                                      |
                                            +------------ result.txt <-------------+
```

The mailbox is a **single request/result slot**, and the task is registered with
`-MultipleInstances IgnoreNew`, so two overlapping callers would make one wait out the full
timeout. A cross-process mutex (`Global\battctl-mailbox`) serialises it. Measured on the test
machine before the fix: 1-3 of every 10-15 back-to-back calls timed out at 20 s; after it,
0 of 20.

The worker is pinned to **Windows PowerShell 5.1** (`powershell.exe`), because
`Get-WmiObject` -- the call verified to work with this provider -- does not exist in PowerShell 7.

### The snapshot command

Besides `status`/`on`/`off`/`toggle`, the worker accepts an internal `snapshot` command that
returns one line of JSON with the charge-limit state plus battery telemetry:

```json
{"schema":2,"limitOn":true,"desired":true,"supported":true,
 "battery":{"present":true,"percent":79,"state":"NotCharging","powerOnline":true,
            "designCapacityMWh":57488,"fullChargeCapacityMWh":47463,"wearPercent":17.4,
            "cycleCount":44,"voltageMv":16337,"rateMw":null,
            "remainingCapacityMWh":37745,"estimatedMinutes":null},
 "restoreLog":[...],"installedAt":"2026-10-15T08:24:20"}
```

Telemetry (percentage, health, cycles, voltage) comes from `root\WMI` battery classes and
`Win32_Battery`, which are readable **without elevation**. Only `BatteryControl` needs SYSTEM.
That split is deliberate: when the Acer provider is stuck, the app still shows battery health
and reports the limit state as unknown.

Two details worth knowing:

- `percent` prefers `Win32_Battery.EstimatedChargeRemaining`, so it always agrees with the
  Windows taskbar. Deriving it from capacity (37745/47463 = 79.5%) would round to 80 and
  disagree with an OS that says 79.
- `CycleCount` lives in `root\WMI:BatteryCycleCount`, not in `BatteryStaticData`.

## Supported models

If `battctl status` exits `2`, your firmware does not expose `BatteryControl`. Known-working
Acer families (from the Linux [`acer-wmi-battery`](https://github.com/frederik-h/acer-wmi-battery)
driver, which talks to the same interface) include Aspire 3/5/7, Swift 3, Nitro AN515-x and
some Predator models. Reports for other models are welcome.

## Caveats

- The flag lives in EC RAM and may be **lost on a full battery drain / EC reset** -- just run
  `battctl on` again.
- Repeated toggling is safe: it's an EC RAM write, not a flash write. The tool is idempotent.
- Safe to use while Care Center is stuck *"still initializing"*: the WMI provider is served by the
  service stack (`ACCSvc.exe`), not the hung UI process (`ACCStd.exe`).

## Known limitations

- The charge-limit flag **is lost after hibernate on the tested machine**. The installer registers
  the `BattCtlResume` task, which re-applies your last chosen state after resume and at boot.

## Uninstall

```powershell
.\uninstall.cmd        # one UAC prompt
```

Removes both scheduled tasks, `C:\Program Files\battctl`, `C:\ProgramData\battctl`,
`C:\Program Files\BattCtl.App`, the Start Menu shortcut, and the launch-at-login entry
for every user profile.

## WSL usage

A wrapper is included at [`wsl/battctl`](wsl/battctl). It calls the fixed install path
`C:\Program Files\battctl\battctl.cmd`, so it needs no editing:

```bash
cp wsl/battctl ~/.local/bin/battctl && chmod +x ~/.local/bin/battctl
battctl status
```

UAC prompts are invisible from a WSL terminal, which is why the tool uses a `SYSTEM` task
instead of self-elevation -- see [How it works](#how-it-works). The desktop app itself is a
Windows program: build it with `build.ps1`/`run.ps1` from WSL, but launch it on the Windows side.

## Project layout

```
battctl.ps1            CLI front end (medium integrity, no UAC)
battctl-worker.ps1     worker, runs as SYSTEM via the BattCtl task
battctl-install.*      one-time CLI/worker/task setup
install-ui.ps1         desktop app install (+ refreshes worker and CLI)
build.ps1, run.ps1     source build wrappers (WSL UNC safe)
src/BattCtl.App/       WinUI 3 app: Views, ViewModels, Services, Styles, Assets
docs/                  UI architecture contract, design brief, WinUI 3 research
.github/workflows/     lint.yml (PowerShell lint), build-app.yml (WinUI build)
```

## Design notes

`docs/UI-ARCHITECTURE.md` is the binding contract between the worker and the app (mailbox
protocol, snapshot schema, verified telemetry sources). `docs/DESIGN-BRIEF.md` records the UI
direction and constraints. `docs/winui3-research.md` collects verified WinUI 3 /
Windows App SDK 1.7 findings for unpackaged apps -- including why the app hand-rolls
`Shell_NotifyIcon` instead of using a tray library, which crashed with `0xc000027b` on this
stack.

## Credits

Reverse-engineered from these projects, which use the same WMI interface:

- [rafradek/Acer-Predator-Scripts](https://github.com/rafradek/Acer-Predator-Scripts)
- [supesonly/Acer-P-Helper](https://github.com/supesonly/Acer-P-Helper)
- [frederik-h/acer-wmi-battery](https://github.com/frederik-h/acer-wmi-battery) (Linux driver)

## License

[MIT](LICENSE)

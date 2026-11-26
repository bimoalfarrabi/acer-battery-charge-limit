# Acer Battery UI — architecture contract

Replacement front end for Acer Care Center's battery panel, which hangs on
"still initializing". The UI never touches WMI directly: the WMI provider is
only visible to an elevated caller, so every firmware read and write goes
through the existing `BattCtl` SYSTEM scheduled task.

Battery telemetry is the exception: `Windows.System.Power`,
`Windows.Devices.Power`, and `GetSystemPowerStatus` are readable from a medium
IL process, so the app reads them itself and the worker is left with the
firmware state and the few fields only SYSTEM can see.

```
BattCtl.App (WinUI 3, medium IL)
        |  reads directly: PowerManager, Battery.GetReport(), GetSystemPowerStatus
        |  reads directly: history\battery-*.jsonl  (history tier)
        |  writes request.txt, triggers schtasks, polls result.txt  (worker tier only)
        v
C:\ProgramData\battctl\mailbox\   <-- nonce-scoped mailbox
        ^
        |  battctl-worker.ps1 runs as NT AUTHORITY\SYSTEM
        v
root\WMI:BatteryControl + root\WMI:Battery* telemetry
```

The CLI (`battctl.ps1` / `battctl.cmd`) stays byte-compatible. The UI adds the
`snapshot` and `gpu:*` worker commands and does not change any existing one. The
history sampler adds a direct-mode `sample` command; it is never part of the
mailbox.

## Mailbox protocol (v2, backward compatible)

`request.txt` — UTF-8, no BOM, single line:

```
<nonce>|<command>
```

`nonce` matches `^[0-9A-Za-z]{4,64}$`. `command` is one of
`status`, `on`, `off`, `toggle`, `restore`, `snapshot`,
`gpu:status`, `gpu:on`, `gpu:off`.

`result.txt` — UTF-8, no BOM, line-oriented:

```
<nonce>|<exitcode>
<payload line 1>
<payload line 2>
...
```

Exit codes are unchanged from v1:

| code | meaning |
|------|---------|
| 0 | success |
| 1 | WMI / provider failure |
| 2 | `BatteryControl` instance absent (firmware has no charge limit); no NVIDIA dGPU found (`gpu:*`) |
| 3 | front end timed out waiting for the task |
| 4 | bad request / mailbox or task missing |
| 5 | refused by a GPU safety guard (`gpu:off` while the GPU drives a display, or with no other live adapter) |

### `snapshot` payload

For `snapshot` the payload is exactly one line containing a single JSON object.
The UI parses line 2 and ignores the rest. Shape:

```json
{
  "schema": 3,
  "limitOn": true,
  "desired": true,
  "supported": true,
  "battery": {
    "present": true,
    "percent": 80,
    "state": "NotCharging",
    "powerOnline": true,
    "designCapacityMWh": 57000,
    "fullChargeCapacityMWh": 48900,
    "wearPercent": 14.2,
    "cycleCount": 132,
    "voltageMv": 16800,
    "rateMw": 0,
    "remainingCapacityMWh": 39120,
    "estimatedMinutes": null
  },
  "gpu": {
    "present": true,
    "name": "NVIDIA GeForce RTX 3060 Laptop GPU",
    "enabled": true,
    "instanceId": "PCI\\VEN_10DE&DEV_2520&SUBSYS_153A1025&REV_A1\\4&27C18CFB&0&0008",
    "drivingDisplay": false,
    "problemCode": 0,
    "error": null
  },
  "restoreLog": [
    { "at": "2026-10-30T07:03:06", "message": "restored desired state: ON  (charge limited to 80%)" }
  ],
  "installedAt": "2026-10-16T08:40:00"
}
```

The `gpu` object is always present with all seven keys; `null` means unknown.
It is populated independently of `BatteryControl`, so it carries real data even
in the degraded (`supported: false`, exit 2) snapshot: GPU state comes from PnP
and `Win32_VideoController`, not from the Acer provider.

`gpu:status` / `gpu:on` / `gpu:off` enable or disable the dedicated GPU
(`PCI\VEN_10DE&DEV_*`, Display class) with `Enable-PnpDevice` /
`Disable-PnpDevice`. Windows persists the PnP state across reboots, so there is
**no GPU restore at boot and no desired-state file for the GPU**: a blind
boot-time disable is the one scenario that can leave the user with no screen,
and the charge-limit restore must never gain that failure mode. `gpu:off` is
refused (exit 5) when the NVIDIA adapter is driving a display or when no other
adapter is; `gpu:on` never refuses.

### Verified telemetry sources (2026-10-30, Acer Nitro AN515-57, AP18E8M)

Probed live on this machine. These facts override any assumption:

| source | available | values observed |
|--------|-----------|-----------------|
| `root\WMI : BatteryStaticData` | yes | `DesignedCapacity` 57488 mWh, `DeviceName` `AP18E8M`, `SerialNumber` 8737 |
| `root\WMI : BatteryFullChargedCapacity` | yes | `FullChargedCapacity` 47463 mWh |
| `root\WMI : BatteryStatus` | yes | `RemainingCapacity` 37745, `Voltage` 16337 mV, `PowerOnline` True, `Charging` False, `Discharging` False, `Rate` **empty/null** |
| `root\WMI : BatteryCycleCount` | yes | `CycleCount` 44 |
| `root\WMI : BatteryTemperature` | **no instance** | — |
| `root\WMI : BatteryRuntime` | yes but useless | `EstimatedRuntime` 4294967295 (0xFFFFFFFF = unknown sentinel) |
| `root\cimv2 : Win32_Battery` | yes | `EstimatedChargeRemaining` 79, `BatteryStatus` 2, `DesignCapacity`/`FullChargeCapacity` **null** |

Consequences the worker MUST implement:

- **`CycleCount` lives in `root\WMI : BatteryCycleCount`, not in `BatteryStaticData`.** `BatteryStaticData` has no such property; reading it there silently yields null.
- **`Win32_Battery.BatteryStatus` 2 does NOT mean charging.** The enum is:
  `1` discharging, `2` AC connected but not necessarily charging, `3` fully charged,
  `4` low, `5` critical, `6` charging, `7` charging+high, `8` charging+low,
  `9` charging+critical, `10` undefined, `11` partially charged.
  With the 80% limit on and the pack at 79%, the correct state is `2` → `NotCharging`,
  which is exactly what this machine reports. Mapping `2` to `Charging` is a bug.
- **Prefer the root\WMI `Charging` / `Discharging` booleans** for `state`; fall back to the
  `Win32_Battery` enum only when those classes are unavailable.
- **`Rate` is empty while on AC**, so `rateMw` and `estimatedMinutes` are `null` in the
  common case. That is correct, not a failure. Never substitute the `4294967295` sentinel
  from `BatteryRuntime.EstimatedRuntime` — treat any value ≥ 4294967295 as unknown.
- **`percent` — the OS value wins.** `Win32_Battery.EstimatedChargeRemaining` is the
  primary source; compute from `RemainingCapacity / FullChargedCapacity` only when that
  is null or outside 0..100.
  *Why:* on 2026-10-30 the raw ratio was `37745 / 47463 = 79.5251%`, which rounds to
  **80**, while Windows itself reported **79** in the taskbar and Settings. Showing 80
  next to a taskbar that says 79 destroys trust in every other number in the app.
  Consistency with the OS beats arithmetic elegance. Never show a percent that
  disagrees with the Windows taskbar.
- **Do not add a temperature field.** `BatteryTemperature` has no instance on this machine;
  the UI must not depend on it.

Expected snapshot on this machine right now: `limitOn: true`, `desired: true`,
`supported: true`, `battery.percent: 79`, `battery.state: "NotCharging"`,
`battery.powerOnline: true`, `designCapacityMWh: 57488`, `fullChargeCapacityMWh: 47463`,
`wearPercent: 17.4`, `cycleCount: 44`, `voltageMv: 16337`, `rateMw: null`,
`estimatedMinutes: null`, `gpu.present: true`, `gpu.enabled: true`,
`gpu.drivingDisplay: false`, `gpu.problemCode: 0`.

### Protocol rules

Rules the worker must honour:

- Every field is always present. Use `null` for genuinely unknown values, never
  omit a key — the UI binds without null checks per field.
- `state` is one of `Charging`, `Discharging`, `NotCharging`, `Unknown`,
  derived from `BatteryStatus.BatteryStatus`.
- `wearPercent` is `(1 - fullCharge / design) * 100`, one decimal, `null` when
  either capacity is 0 or absent.
- `estimatedMinutes` is `null` unless the battery is discharging at a known
  rate.
- `restoreLog` is the last 20 entries, newest last, oldest first.
- If the provider is unreachable, still emit valid JSON with
  `"supported": false`, `"limitOn": null`, `"battery": { "present": false, ... }`
  and exit `2` — the UI shows a "not supported" state rather than a crash.
- Writes (`on`, `off`, `toggle`) additionally refresh `desired.txt` and, for
  `snapshot`, must be side-effect free: `snapshot` never writes the EC.
- GPU writes (`gpu:on`, `gpu:off`) never touch `desired.txt` or the EC. Windows
  persists the PnP device state itself, so there is no GPU desired-state file and
  no boot/resume GPU restore.

### Concurrency

The worker runs under `-MultipleInstances IgnoreNew`. A second `schtasks /run`
while a snapshot is in flight is a no-op, so the front end must tolerate
`result.txt` never appearing and retry after its timeout.

### Three data tiers

Battery telemetry has three tiers with different latencies, and the UI keeps them
apart instead of polling one snapshot for everything.

**Live tier (in-process, `Services/BatteryMonitor.cs`).** Percent, charge state,
AC line, charge rate, remaining/full/design capacity, wear, and time remaining
are read directly by the app through `Windows.System.Power.PowerManager`,
`Windows.Devices.Power.Battery.GetReport()`, and `GetSystemPowerStatus`. No
capability, no package identity, no worker, no scheduled task.

- WinRT events (`RemainingChargePercentChanged`, `BatteryStatusChanged`,
  `PowerSupplyStatusChanged`, `RemainingDischargeTimeChanged`,
  `Battery.ReportUpdated`) give the push edge.
- A 1 Hz sample bounds worst-case staleness; it is sub-microsecond and only
  re-raises when a *displayed* value changed, so a tick that changes nothing
  touches no binding.
- Sampling runs only while the window is visible, not minimized, and a battery
  page (Dashboard or Health) is showing. Settings does not sample. This is the
  live tier only; the History page uses the persisted history tier below.
- A wall-clock vs stopwatch gap on the tick detects resume and forces a
  re-read; no window subclassing or `WM_POWERBROADCAST` is needed.

**Worker tier (SYSTEM, mailbox).** Only what genuinely needs SYSTEM remains:
charge limit state, GPU state, voltage, cycle count, and the restore log.

- No periodic poll. One snapshot on launch, one on resume, one on window
  activation (debounced to 60 s), one on the first Health navigation per
  session when the cache is older than 60 s, and one after every limit/GPU
  command as the reconcile read-back.
- The volatile battery fields the worker still reports are **ignored** once the
  live tier has produced a reading. Field ownership is per-field, not
  last-writer-wins, so a ~1 s stale worker value can never make the percent
  stutter backwards.

Measured on this machine: `BatteryReport` returned `Status=Idle`,
`ChargeRateInMilliwatts=0`, `remaining=45507`, `full=45507`,
`design=57488`; `PowerManager.RemainingDischargeTime` reported
`TimeSpan.MaxValue` while not discharging. `ChargeRateInMilliwatts == 0` while
`Idle` on AC is a real zero, not a missing value; `TimeSpan.MaxValue` is the
unknown sentinel for discharge time; `BatteryFlag` cannot express "not
charging", so the state comes from `BatteryReport.Status`.

**History tier (SYSTEM task, JSONL).** The separate `BattCtlHistory` task runs
`battctl-worker.ps1 -Action sample` every five minutes as `SYSTEM`. It appends one
record per sample to `C:\ProgramData\battctl\history\battery-yyyyMMdd.jsonl`,
keeps 90 days, and never touches the mailbox or `BatteryControl`. The app reads
the files read-only through `Services/HistoryStore.cs`.

- Record schema v1 is separate from snapshot schema 3: `v`, `t` (UTC), `pct`,
  `state`, `ac`, `rateMw`, `remMWh`, `fullMWh`, `designMWh`, `cycleCount`,
  `limitSet`.
- `limitSet` comes from `desired.txt`; it means "the 80% limit was set", not a
  firmware read-back.
- Gaps are real: sleep, shutdown, or a stopped task produce no rows. The chart
  breaks the line instead of interpolating.
- The History page does not use the live tier or `UpdateSampling`; it loads the
  selected range, builds sessions and the summary in `Services/HistoryAnalysis.cs`,
  and draws a custom chart in `Views/HistoryChart.xaml`.
- Full format, retention, and install rules: `docs/BATTERY-HISTORY-SPEC.md`.

### Measured cost (2026-10-30, this machine)

A full mailbox round trip via `battctl.ps1 status` takes **~930 ms** (5 runs:
1145, 912, 932, 926, 931 ms), almost all of it Windows PowerShell 5.1 startup
plus the scheduled-task launch. A `snapshot` will be somewhat slower.

Design consequences — the UI must not treat ~1 s as instant:

- Show the last known worker state immediately on open (persist it to
  `%LOCALAPPDATA%\BattCtl.App\last-snapshot.json`), then reconcile in the
  background. The live tier paints the battery card on the first frame, so
  there is no skeleton to wait through.
- A toggle must be optimistic: flip the switch, show a pending state on the
  control, and reconcile with the authoritative read-back. Never leave the user
  staring at a frozen switch for a second.
- A pending ring belongs on the control that is mid-action, never on the page
  header. Routine reconciliation shows no spinner at all, and no page carries
  an "Updated HH:mm:ss" caption — the values themselves are the freshness
  signal.

## Project layout

```
src/BattCtl.App/
  BattCtl.App.csproj          net9.0-windows10.0.26100.0, WinUI 3, unpackaged
  Program.cs                  STAThread entry, ComWrappers, DispatcherQueue
  App.xaml(.cs)               resources, Mica, single-instance activation
  MainWindow.xaml(.cs)        shell: NavigationView + Mica backdrop
  Views/                      DashboardPage, HealthPage, HistoryPage,
                              HistoryChart, SettingsPage
  Styles/                     Fluent token overrides, control styles
  Services/
    BattCtlClient.cs          mailbox IPC, nonce, retry, error mapping
    SnapshotModel.cs          JSON DTOs matching the contract above
    HistoryStore.cs           read-only JSONL history reader
    HistoryModels.cs          history DTOs, sessions, summary
    HistoryAnalysis.cs        sessions, summary, coverage, formatting
    StartupService.cs         HKCU Run key registration
    TrayService.cs            tray icon + quick toggle
  ViewModels/
    MainViewModel.cs          live battery + worker state
    HistoryViewModel.cs       cached history range, load/refresh state
  Assets/
build.ps1                     dotnet build -c Release -p:Platform=x64
```

Constraints:

- Unpackaged (`WindowsPackageType=None`), self-contained Windows App SDK so no
  runtime install is required.
- Windows PowerShell 5.1 must keep working; the UI must never require PS7.
- The app must degrade to a read-only "unsupported firmware" state instead of
  throwing when the worker exits `2`.
- No UI thread blocking: every mailbox round trip is `async`.

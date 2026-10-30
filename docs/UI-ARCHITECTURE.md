# Acer Battery UI — architecture contract

Replacement front end for Acer Care Center's battery panel, which hangs on
"still initializing". The UI never touches WMI directly: the WMI provider is
only visible to an elevated caller, so every read and write goes through the
existing `BattCtl` SYSTEM scheduled task.

```
BattCtl.App (WinUI 3, medium IL)
        |  writes request.txt, triggers schtasks, polls result.txt
        v
C:\ProgramData\battctl\mailbox\   <-- nonce-scoped mailbox
        ^
        |  battctl-worker.ps1 runs as NT AUTHORITY\SYSTEM
        v
root\WMI:BatteryControl + root\WMI:Battery* telemetry
```

The CLI (`battctl.ps1` / `battctl.cmd`) stays byte-compatible. The UI adds one
new worker command and does not change any existing one.

## Mailbox protocol (v2, backward compatible)

`request.txt` — UTF-8, no BOM, single line:

```
<nonce>|<command>
```

`nonce` matches `^[0-9A-Za-z]{4,64}$`. `command` is one of
`status`, `on`, `off`, `toggle`, `restore`, `snapshot`.

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
| 2 | `BatteryControl` instance absent (firmware has no charge limit) |
| 3 | front end timed out waiting for the task |
| 4 | bad request / mailbox or task missing |

### `snapshot` payload

For `snapshot` the payload is exactly one line containing a single JSON object.
The UI parses line 2 and ignores the rest. Shape:

```json
{
  "schema": 2,
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
  "restoreLog": [
    { "at": "2026-10-30T07:03:06", "message": "restored desired state: ON  (charge limited to 80%)" }
  ],
  "installedAt": "2026-10-16T08:40:00"
}
```

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
`estimatedMinutes: null`.

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

### Concurrency

The worker runs under `-MultipleInstances IgnoreNew`. A second `schtasks /run`
while a snapshot is in flight is a no-op, so the front end must tolerate
`result.txt` never appearing and retry after its timeout. The UI polls a
snapshot at most every 5 s while the window is visible, and stops polling when
it is hidden or minimized.

### Measured cost (2026-10-30, this machine)

A full mailbox round trip via `battctl.ps1 status` takes **~930 ms** (5 runs:
1145, 912, 932, 926, 931 ms), almost all of it Windows PowerShell 5.1 startup
plus the scheduled-task launch. A `snapshot` will be somewhat slower.

Design consequences — the UI must not treat ~1 s as instant:

- Show the last known snapshot immediately on open (persist it to
  `%LOCALAPPDATA%\BattCtl.App\last-snapshot.json`), then refresh in the
  background and cross-fade when the new value lands.
- A toggle must be optimistic: flip the switch, show a pending state on the
  control, and reconcile with the authoritative read-back. Never leave the user
  staring at a frozen switch for a second.
- Never show a blocking spinner for routine refreshes; only the first-ever load
  with no cached snapshot gets a skeleton.

## Project layout

```
src/BattCtl.App/
  BattCtl.App.csproj          net9.0-windows10.0.26100.0, WinUI 3, unpackaged
  Program.cs                  STAThread entry, ComWrappers, DispatcherQueue
  App.xaml(.cs)               resources, Mica, single-instance activation
  MainWindow.xaml(.cs)        shell: NavigationView + Mica backdrop
  Views/                      DashboardPage, HealthPage, SettingsPage
  Styles/                     Fluent token overrides, control styles
  Services/
    BattCtlClient.cs          mailbox IPC, nonce, retry, error mapping
    SnapshotModel.cs          JSON DTOs matching the contract above
    StartupService.cs         HKCU Run key registration
    TrayService.cs            tray icon + quick toggle
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

# Dedicated GPU enable/disable — implementation spec

Status: approved design, not yet implemented. Target machine: Acer Nitro AN515-57,
NVIDIA GeForce RTX 3060 Laptop GPU (`PCI\VEN_10DE&DEV_2520&SUBSYS_153A1025&REV_A1\4&27C18CFB&0&0008`),
Intel UHD Graphics drives the panel (Optimus hybrid).

## Goal

Let the user turn the dedicated NVIDIA GPU off to save battery and turn it back on.
The change is a PnP device disable/enable, performed by the existing SYSTEM worker.
No Acer WMI, no MUX switch, no reboot, no boot-time restore.

## Mechanism (decided)

- `Disable-PnpDevice` / `Enable-PnpDevice` on the Display-class device whose
  `InstanceId` matches `^PCI\\VEN_10DE&DEV_`.
- Read state with `Get-PnpDevice -InstanceId <id>`: `Status -eq 'OK'` = enabled,
  `Problem -eq 22` (`CM_PROB_DISABLED`) = disabled.
- `drivingDisplay` = the matching `Win32_VideoController` has
  `CurrentHorizontalResolution -gt 0`.
- Do **not** use `AcerGamingFunction`, `SetGamingMiscSetting`, or
  `AcerBiosConfigurationTool` for this. They do not expose a dGPU disable on this model.
- Do **not** add a `gpu:toggle` command, a desired-state file, a restore-log entry,
  a re-apply button, or a boot/resume GPU restore. Windows persists the PnP state;
  a blind boot-time disable is the one scenario that can leave a user with no screen.

## Worker protocol

Commands are whole tokens, so the existing `-split '\|', 2` parser is untouched:

- `gpu:status` — report current state
- `gpu:on` — enable the dGPU
- `gpu:off` — disable the dGPU

Exit codes:

| code | meaning |
|------|---------|
| 0 | success, including idempotent "already enabled/disabled" |
| 1 | operation failure: cmdlet threw, verify mismatch, read-back failed, PnpDevice module missing |
| 2 | no NVIDIA dGPU found |
| 4 | bad request (existing) |
| 5 | refused by a safety guard |

Message lines (mailbox and direct mode):

```
gpu:status → Dedicated GPU: enabled -> NVIDIA GeForce RTX 3060 Laptop GPU
gpu:off    → Dedicated GPU: disabled -> NVIDIA GeForce RTX 3060 Laptop GPU   (or "already disabled")
gpu:on     → Dedicated GPU: enabled -> NVIDIA GeForce RTX 3060 Laptop GPU    (or "already enabled")
```

Refusal lines:

```
REFUSED: the NVIDIA GPU is driving a display; disabling it would blank that screen.
REFUSED: no other display adapter is driving a screen; disabling the GPU could leave you with no display.
```

## Safety guards (all mandatory)

- **G1** — refuse `gpu:off` when `drivingDisplay == true` (exit 5).
- **G2** — refuse `gpu:off` when no non-NVIDIA `Win32_VideoController` has
  `CurrentHorizontalResolution > 0` (exit 5).
- **G3** — `gpu:on` never refuses. Enabling cannot blank a display; it is the way back.
- **G4** — do not enumerate or kill processes. If the cmdlet throws or the verify
  loop disagrees, exit 1 with the real message.
- **G5** — already in target state: skip the write, exit 0, "already enabled/disabled".
- **G6** — absent device: exit 2; snapshot `gpu.present = false`; UI hides the card.
- **G7** — no boot/resume restore, no desired-state file.
- **G8** — `Get-PnpDevice` missing: exit 1 with a clear message.
- **G9** — UI confirmation on disable only (inline, not a modal).

## Snapshot schema

Bump `schema` to `3`. Add a top-level `gpu` object, always present, all seven keys
always emitted, `null` = unknown:

```json
"gpu": {
  "present": true,
  "name": "NVIDIA GeForce RTX 3060 Laptop GPU",
  "enabled": true,
  "instanceId": "PCI\\VEN_10DE&DEV_2520&SUBSYS_153A1025&REV_A1\\4&27C18CFB&0&0008",
  "drivingDisplay": false,
  "problemCode": 0,
  "error": null
}
```

The object is populated inside `Get-SnapshotJson`, so it appears in both the
supported and the degraded (`$supported = $false`) payloads. GPU reads do not
depend on `BatteryControl`.

## Worker changes (`battctl-worker.ps1`)

1. `param` `ValidateSet` (L2): add `'gpu:status','gpu:on','gpu:off'`.
2. Mailbox whitelist (L296): add the same three.
3. After `Get-NullableBoolean` (L96), add:
   - `Find-NvidiaGpu` — `Get-PnpDevice -Class Display -PresentOnly`, filter
     `InstanceId -match '^PCI\\VEN_10DE&DEV_'`, return the first or `$null`.
   - `Get-GpuState` — returns an ordered dictionary with the seven keys. Reads
     `Status`, `Problem`, `FriendlyName`, `InstanceId`; probes
     `Win32_VideoController` for `drivingDisplay`. Never throws; on error sets
     `error` and leaves the rest `$null`.
   - `Set-GpuEnabled([bool]$enable)` — calls `Disable-PnpDevice`/`Enable-PnpDevice`
     with `-Confirm:$false -ErrorAction Stop`, then a bounded verify loop
     (10 × 500 ms) comparing `Get-GpuState().enabled` to the target. Returns
     `@{ ok; code; message }`.
   - `Get-GpuSnapshotObject` — thin wrapper returning `Get-GpuState` (keeps the
     snapshot call site readable).
   - `Invoke-GpuCommand([string]$cmd)` — implements G1/G2/G3/G5/G6, uses
     `Say`+`Emit` in mailbox mode and `Direct-Exit` in direct mode, and always
     terminates.
4. Gate the `BatteryControl` block (L313–333) with
   `if ($command -notlike 'gpu:*') { ... }`. A GPU command must work when
   `$bc` is `$null`.
5. In `Get-SnapshotJson` (L261–269): `schema = 3` and
   `gpu = (Get-GpuSnapshotObject)`.
6. Add the GPU dispatch **between L369 and L371**, before `$before = Read-Limit`:

   ```powershell
   if ($command -like 'gpu:*') {
       Invoke-GpuCommand $command
   }
   ```

   This is the highest-risk placement: if a `gpu:*` command falls through to the
   `switch` at L376, `$target` is `$null`, `Write-Limit ([int]$null)` writes `0`,
   and the charge limit is silently turned **off**. Every GPU branch must terminate.

## CLI changes (`battctl.ps1`)

- Whitelist (L15): add `gpu:status`, `gpu:on`, `gpu:off`.
- Usage (L17): `usage: battctl [status|on|off|toggle|gpu:status|gpu:on|gpu:off]`.
- Exit-code comment (L6): add `5 refused`.

## C# changes

### `Services/SnapshotModel.cs`

- `Snapshot`: add `[JsonPropertyName("gpu")] public GpuInfo? Gpu { get; set; }`.
- New `GpuInfo` class with seven nullable properties:
  `Present` (bool?), `Name` (string?), `Enabled` (bool?), `InstanceId` (string?),
  `DrivingDisplay` (bool?), `ProblemCode` (int?), `Error` (string?).

### `Services/BattCtlClient.cs`

- `SendCommandAsync` whitelist: add `gpu:status`, `gpu:on`, `gpu:off`.
- `BattCtlError`: add `GpuRefused`.
- `CreateWorkerException`: map exit `5` to `GpuRefused`.

### `ViewModels/MainViewModel.cs`

Mirror the `_pendingLimitTarget` pattern:

- `_pendingGpuTarget` (bool?), `_gpuMessage` (string?), `_gpuMessageIsError` (bool).
- `IsGpuPending`, `GpuPresent`, `GpuDisplayValue`, `GpuToggleEnabled`,
  `ShowGpuRetry`, `GpuStateText`, `GpuMessage`, `HasGpuMessage`,
  `GpuMessageIsError`, `DismissGpuMessage()`.
- `GpuDisplayValue => _pendingGpuTarget ?? _snapshot?.Gpu?.Enabled ?? false`.
- `GpuToggleEnabled => !IsGpuPending && _snapshot?.Gpu?.Present == true &&
  _snapshot?.Gpu?.Enabled is not null && _snapshot?.Gpu?.DrivingDisplay != true`.
- `GpuStateText`:
  - pending: `Turning the GPU off…` / `Turning the GPU on…`
  - `Gpu == null` or `Present != true`: `State unknown. The worker has not reported the GPU.`
  - `Enabled == null`: `State unknown. The worker could not read the GPU state.`
  - `DrivingDisplay == true`: `On. This GPU is driving a display, so it cannot be turned off.`
  - `Enabled == true`: `On. Available to apps that need it.`
  - `Enabled == false` and `ProblemCode` not 22: `Off. The device reports a problem (code N).`
  - `Enabled == false`: `Off. Apps use the Intel GPU. Screens wired to the GPU stay dark.`
- `RequestGpuChangeAsync(bool target)` — optimistic, sends `gpu:on`/`gpu:off`,
  refreshes the snapshot, reconciles, clears the pending target in `finally`.
  On `BattCtlException` with `GpuRefused`, set `GpuMessageIsError = false` and use
  the worker's message (a guard doing its job is a warning, not an error).
- `FriendlyGpuMessage(BattCtlException)` — do not reuse `FriendlyMessage`; its
  `Unsupported` text is battery-specific. Map `Unsupported` to
  `No controllable dedicated GPU was found on this machine.` and the rest to the
  existing friendly strings.

### `Views/DashboardPage.xaml`

Insert a card between the charge-limit `Border` (ends L72) and the "At a glance"
`Grid` (L74). Hidden when `GpuPresent` is false.

- Title: `Dedicated GPU`
- State line: `GpuState`, `CaptionSecondaryTextBlockStyle`, `TextWrapping="Wrap"`,
  `AutomationProperties.LiveSetting="Polite"`.
- `ProgressRing` on `IsGpuPending`, `AutomationProperties.Name="Changing GPU state"`.
- `ToggleSwitch x:Name="GpuSwitch"`, `OnContent="On"`, `OffContent="Off"`,
  `AutomationProperties.Name="Dedicated GPU"`, `IsOn="{x:Bind ViewModel.GpuDisplayValue, Mode=OneWay}"`,
  `IsEnabled="{x:Bind ViewModel.GpuToggleEnabled, Mode=OneWay}"`,
  `Toggled="GpuSwitch_Toggled"`.
- Resting description: `Turn it off to save battery. Apps run on the slower Intel GPU, and anything that requires the dedicated GPU stops working.`
- `Check again` button on `ShowGpuRetry`, `Click="GpuRefresh_Click"`.
- `InfoBar x:Name="GpuInfoBar" IsClosable="True" Closed="GpuInfoBar_Closed"` with
  `Content` = a `StackPanel` containing `GpuInfoBarMessage` (TextBlock) and
  `GpuConfirmButtons` (StackPanel, collapsed by default) with
  `Turn off GPU` (`GpuConfirmOff_Click`) and `Keep it on` (`GpuConfirmCancel_Click`).

### `Views/DashboardPage.xaml.cs`

- `UpdateValues`: `ValueFade.SetText(GpuState, ViewModel.GpuStateText)`.
- `OnViewModelPropertyChanged` filter: add `HasGpuMessage`, `GpuMessageIsError`,
  `GpuPresent`, `IsGpuPending`, `ShowGpuRetry`.
- `SyncInfoBars`: when `_gpuConfirmOpen` is false, set `GpuInfoBar.Severity`
  (Error when `GpuMessageIsError`, else Warning), `GpuInfoBarMessage.Text`,
  `GpuInfoBar.IsOpen = ViewModel.HasGpuMessage`. Blank `GpuSwitch.OnContent` /
  `OffContent` when `Snapshot?.Gpu?.Enabled is null`.
- `GpuSwitch_Toggled`:
  - ignore when `_suppressGpuToggle` or `ViewModel.IsGpuPending`;
  - target On → `RequestGpuChangeAsync(true)` immediately;
  - target Off → snap `GpuSwitch.IsOn` back to `true` under `_suppressGpuToggle`,
    set `_gpuConfirmOpen = true`, show the Warning InfoBar with the confirm copy,
    show `GpuConfirmButtons`.
- `GpuConfirmOff_Click`: close the confirm, `RequestGpuChangeAsync(false)`.
- `GpuConfirmCancel_Click`: close the confirm, no command.
- `GpuRefresh_Click`: `RefreshAsync()`.
- `GpuInfoBar_Closed`: clear `_gpuConfirmOpen`, hide the buttons, `DismissGpuMessage()`.

Confirm copy (Warning InfoBar):

```
The GPU stays off until you switch it back on, including after a restart. Apps run on the Intel GPU, and screens wired to the GPU go dark. If an external screen is your only display, use the built-in screen to turn the GPU back on.
```

Unconfirmed write (Warning):

```
The change was sent, but the worker did not confirm it. The switch shows the last confirmed state.
```

## Docs

- `docs/UI-ARCHITECTURE.md`: add the three commands, exit 5, the `gpu` JSON shape,
  schema 3, and the "no GPU restore at boot" rationale.
- `README.md`: add the three commands to the CLI table and mention the GPU card.

## Non-goals

- No tray menu item. The tray is the battery-limit app; a stale GPU toggle would
  claim state it may not have. Optionally append `gpu on`/`gpu off` to the tooltip
  from the last snapshot, information only.
- No Settings card. One control, one surface.
- No Acer WMI, no MUX switch, no reboot requirement.
- No process enumeration or killing.

## Test plan

1. Discovery: `Get-PnpDevice -Class Display -PresentOnly | ? InstanceId -match 'VEN_10DE'`
   → one hit, Status OK, Problem 0.
2. Snapshot: `battctl snapshot` (or the app's Copy status as JSON) → `schema == 3`,
   all seven `gpu` keys present, `enabled == true`, `drivingDisplay == false`.
3. `battctl gpu:status` → exit 0, one message line.
4. `battctl gpu:off` → exit 0; re-run status → disabled; `Get-PnpDevice` shows
   Status Error, Problem 22; Intel still drives the display.
5. `battctl gpu:on` → exit 0; status → enabled; `nvidia-smi` works again.
6. Idempotency: `gpu:off` twice → second is exit 0 "already disabled".
7. Regression: after a `gpu:*` command, `battctl status` still reports the charge
   limit unchanged (the fall-through hazard).
8. Guard G1/G2: plug an external monitor into the dGPU-wired HDMI, wait for NVIDIA
   to show a resolution, `battctl gpu:off` → exit 5, display untouched.
9. No-BatteryControl path: code review + a targeted run; `Invoke-GpuCommand` must
   be reachable with `$bc = $null`.
10. UI: card visible, toggle works, confirm appears on disable, cancel keeps it on,
    error/refusal surfaces as Warning, unknown state blanks the On/Off labels.
11. Old app + new worker: old app opens, ignores `gpu` (System.Text.Json default).
    New app + old worker: `Gpu == null`, card hidden, gpu commands exit 4 and the
    existing "outdated worker" message appears.

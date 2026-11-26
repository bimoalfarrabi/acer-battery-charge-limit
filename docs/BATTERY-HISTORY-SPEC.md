# Battery history — implementation spec

Status: approved design, implementation in progress.
Target machine: Acer Nitro AN515-57, Windows 11, Acer Battery 1.3.0.

## Goal

Show the user how the battery was used over time: state-of-charge over a selected
range, charging and discharging sessions, and a small summary. History must keep
recording while the app is closed (tray quit or never launched), so the sampler
lives in the SYSTEM worker and the app only reads the result.

## Decisions

- **One writer.** A new scheduled task `BattCtlHistory` runs
  `battctl-worker.ps1 -Action sample` every 5 minutes as `SYSTEM`.
- **No mailbox, no `BatteryControl`.** `sample` is a direct-mode command and
  dispatches before the Acer provider is opened. It works on models with no
  charge-limit interface.
- **Daily JSONL**, one file per local day under
  `C:\ProgramData\battctl\history\battery-yyyyMMdd.jsonl`, UTF-8 without BOM.
- **90 days retention.** The sampler deletes day files older than 90 days.
- **The app is read-only.** It reads the files directly from
  `Environment.SpecialFolder.CommonApplicationData`; no new worker command, no
  mailbox traffic, no app-side sampler.
- **No new dependency.** The chart is drawn with `Canvas`, `Line`, `Rectangle`,
  `Ellipse`, and `TextBlock`. The solution must build with only the existing
  `Microsoft.WindowsAppSDK` and `Microsoft.Windows.SDK.BuildTools` packages.
- **No snapshot schema bump.** `snapshot` stays `schema: 3`; the history record
  has its own `v: 1`.
- **Honest gaps.** Sleep, shutdown, and a missing task produce gaps. The UI breaks
  the line and never fills the gap with a straight segment.
- **`limitSet` is the desired state, not the firmware read-back.** It comes from
  `C:\ProgramData\battctl\desired.txt`. The 80% guide is drawn only where
  `limitSet` is true, and is labelled "80% limit (set)".

## Data file

Path:

```
C:\ProgramData\battctl\history\battery-yyyyMMdd.jsonl
```

One JSON object per line, compact, no trailing commas:

```json
{"v":1,"t":"2026-11-26T07:25:00Z","pct":79,"state":"NotCharging","ac":true,"rateMw":null,"remMWh":36221,"fullMWh":45553,"designMWh":57488,"cycleCount":44,"limitSet":true}
```

Fields:

| key | type | meaning |
|---|---|---|
| `v` | int | record schema, `1` |
| `t` | string | UTC, `yyyy-MM-ddTHH:mm:ssZ`, invariant culture |
| `pct` | int? | OS percentage 0..100, null when unknown |
| `state` | string | `Charging`, `Discharging`, `NotCharging`, `Unknown` |
| `ac` | bool? | `PowerOnline` |
| `rateMw` | int? | `BatteryStatus.Rate`; often null on AC |
| `remMWh` | int? | `BatteryStatus.RemainingCapacity` |
| `fullMWh` | int? | `BatteryFullChargedCapacity.FullChargedCapacity` |
| `designMWh` | int? | `BatteryStaticData.DesignedCapacity` |
| `cycleCount` | int? | `BatteryCycleCount.CycleCount` |
| `limitSet` | bool? | `desired.txt` is `1` / `0`; null when unreadable |

Rules:

- Every key is present. Unknown is `null`, never `0`.
- `pct` prefers `Win32_Battery.EstimatedChargeRemaining`; capacity math is only a
  fallback, matching `UI-ARCHITECTURE.md`.
- `state` reuses the snapshot derivation (`BatteryStatus.Charging/Discharging`,
  fallback `Win32_Battery.BatteryStatus`).
- If the battery is absent or `pct` is null, do not write a line. Exit `0`.
- A write failure exits `1`; the task's last result reflects it.
- Append with `[IO.File]::AppendAllText` and a bounded retry when the file is
  being read. A malformed or partial last line is tolerated by the reader.
- Retention runs on each sample: delete `battery-*.jsonl` whose filename date is
  older than `today - 90 days`. Never touch other files.

## Task

Name: `BattCtlHistory`.

Action:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Program Files\battctl\battctl-worker.ps1" -Action sample
```

Settings:

- Principal `SYSTEM`, `ServiceAccount`, `RunLevel Highest`.
- Trigger `New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1)`
  with `-RepetitionInterval (New-TimeSpan -Minutes 5)` and
  `-RepetitionDuration (New-TimeSpan -Days 3650)`. The duration must be bounded:
  `[TimeSpan]::MaxValue` serializes to `P99999999DT23H59M59S`, which Task
  Scheduler rejects with `0x80041318` (verified on this machine). Ten years is
  effectively indefinite, and every install or update re-registers the task.
- `-AllowStartIfOnBatteries -DontStopIfGoingOnBatteries`
- `-ExecutionTimeLimit (New-TimeSpan -Minutes 2)`
- `-MultipleInstances IgnoreNew`
- `-StartWhenAvailable`
- Register with `-ErrorAction Stop` so a rejected task fails the install
  instead of printing a success summary.
- Grant Interactive Users read/run on this task as well, so a non-elevated
  `schtasks /query /tn BattCtlHistory` can see it. Running a sample by hand is
  harmless and useful for support.

Registration lives in `battctl-install.ps1`, so the Inno provision path and a
source checkout both get it. It runs in `-ProvisionOnly` mode too. The installer
creates `C:\ProgramData\battctl\history` and grants the invoking user read
access; the worker inherits SYSTEM full control. The app reads as the normal
user, never writes.

Uninstall removes the task in both `uninstall.ps1` and
`installer/unprovision.ps1`. `-RemoveData` deletes `C:\ProgramData\battctl`,
which includes history. The Inno uninstall prompt mentions battery history.

`install-ui.ps1` re-runs `battctl-install.ps1 -Quiet -ProvisionOnly` after
copying the worker, so a source checkout that only runs `install-ui.ps1` gets the
task.

## Worker changes (`battctl-worker.ps1`)

1. Add `'sample'` to the `ValidateSet` (L2-4).
2. Extract the battery WMI block from `Get-SnapshotJson` (L373-461) into
   `Get-BatteryTelemetry`, returning the same ordered dictionary. `Get-SnapshotJson`
   calls it. This keeps the snapshot output byte-compatible.
3. Add `$HistoryDir = Join-Path $DataDir 'history'` and
   `$HistoryRetentionDays = 90`.
4. Add `Add-HistorySample`:
   - `Get-BatteryTelemetry`
   - `limitSet` from `$DesiredPath`
   - build the ordered record, `ConvertTo-Json -Compress -Depth 4`
   - ensure the directory exists, append one line with a `Global\battctl-history`
     mutex (5 s wait), retry once, no BOM
   - prune old daily files
5. Dispatch in direct mode before the `$bc` search (around L514-516):

   ```powershell
   if ($directMode -and $command -eq 'sample') {
       try { Add-HistorySample; exit 0 } catch { exit 1 }
   }
   ```

6. Do not add `sample` to the mailbox whitelist (L499). Direct mode only.
7. Keep Windows PowerShell 5.1 syntax. No ternary, no `??`, no `??=`.
8. `Add-HistorySample` is an approved verb + singular noun for
   PSScriptAnalyzer. No new warnings that fail lint.

## C# changes

### New `Services/HistoryModels.cs`

`HistorySample` maps the JSON above with `[JsonPropertyName]`. Also define:

```csharp
public enum HistoryRange { Day, Week, Month, Quarter }
public enum HistorySessionKind { Charging, Discharging, AcIdle, NotCharging, Unknown }

public sealed class HistorySession
{
    public HistorySessionKind Kind { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public TimeSpan Duration { get; init; }
    public int? StartPercent { get; init; }
    public int? EndPercent { get; init; }
    public double? EnergyWh { get; init; }
    public bool EnergyEstimated { get; init; }
    public bool EndsInGap { get; init; }
    public string KindText { get; init; }
    public string TimeText { get; init; }
    public string DurationText { get; init; }
    public string LevelText { get; init; }
    public string LevelDetailText { get; init; }
    public string EnergyText { get; init; }
    public string GapText { get; init; }
    public DateTimeOffset SortKey => Start;
}

public sealed class HistorySummary
{
    public string OnBatteryText { get; init; }
    public string OnAcText { get; init; }
    public string LowestLevelText { get; init; }
    public string EnergyUsedText { get; init; }
    public bool EnergyEstimated { get; init; }
    public string CoverageText { get; init; }
    public bool HasGaps { get; init; }
}

public sealed class HistoryView
{
    public IReadOnlyList<HistorySample> Samples { get; init; }
    public IReadOnlyList<HistorySession> Sessions { get; init; }
    public HistorySummary Summary { get; init; }
    public DateTimeOffset? EarliestSample { get; init; }
    public DateTimeOffset? LatestSample { get; init; }
    public bool HasLimitGuide { get; init; }
    public string LatestReadoutText { get; init; }
    public string RangeAutomationText { get; init; }
}
```

### New `Services/HistoryStore.cs`

- Resolve `Environment.GetFolderPath(SpecialFolder.CommonApplicationData)` +
  `battctl/history`.
- `LoadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken)` enumerates
  `battery-*.jsonl` covering the range (filename date, local day), reads with
  `FileShare.ReadWrite | FileShare.Delete`, parses each line, skips malformed
  lines, and returns samples sorted by timestamp plus a skipped-line count.
- Distinguish states: directory missing, empty range, unreadable/permission
  failure. Never throw into the UI.
- `GetStamp()` returns the newest file's `LastWriteTimeUtc` + length so the view
  model can skip a reload when the sampler has not written.
- Never open a file for write.

### New `Services/HistoryAnalysis.cs`

Pure functions over the sorted samples. No UI types.

- Derive a session kind from `state` + `ac`:
  `Charging`, `Discharging`, `NotCharging` + `ac` -> `AcIdle`,
  `NotCharging` without AC -> `NotCharging`, else `Unknown`.
- Build contiguous runs. Break on kind change or a gap > 10 minutes. A run ending
  at a gap sets `EndsInGap`; the row appends `, then a gap`.
- Duration = sum of intervals between consecutive samples in the run, capped at
  10 minutes per interval, and including the interval that leads into the next
  run or the following gap. That attribution is what makes `On battery` +
  `On AC` add up to the recorded time in the coverage line.
- Percent delta and energy:
  - Use `remMWh` endpoints when both exist.
  - Fallback: `(deltaPercent / 100) * fullMWh`, using the first sample's full
    capacity for the run.
  - Mark `EnergyEstimated` when the fallback was used.
  - Idle rows with no delta show `held at NN%` when flat within 1 point;
    otherwise show the range.
- Summary for the range:
  - `On battery`: sum of Discharging session durations.
  - `On AC`: sum of Charging and AC-idle session durations.
  - `Lowest level`: minimum percent in range.
  - `Energy used`: sum of discharging energy; label `(est.)` if any fallback.
  - `CoverageText`: `Recorded 21 h of the last 24 h.` Only when gaps exist. The
    recorded time is the sum of the capped sample intervals, so it equals
    `On battery` + `On AC` whenever every run is one of those kinds. A window
    edge with no data, or an internal hole over 10 minutes, sets `HasGaps`.
  - Durations use `H h MM min`; days are added for ranges longer than 24 h.
- Downsample for the chart only when asked by the chart control; keep the
  original samples in `HistoryView`.

Preformatted copy contract for the session rows:

- `KindText`: `Charging`, `Discharging`, `On AC, not charging`, `Not charging`,
  `Unknown`.
- `TimeText`: `Tue 09:15 to 11:40`; crossing midnight `Tue 09:15 to Wed 01:20`.
- `LevelText`: `+36%`, `−21%` (U+2212), `held at 80%` for a flat idle run,
  `steady at 80%` for a flat charging/discharging run.
- `LevelDetailText`: `41% to 77%`; flat `held at 80 percent` or
  `steady at 80 percent`.
- `EnergyText`: `+17.8 Wh` for charging, `−9.4 Wh` for discharging, with the
  ` (est.)` suffix when the percent fallback was used, and the unknown token for
  an idle run.

### New `ViewModels/HistoryViewModel.cs`

App-level singleton, created in `App.OnLaunched` before the window.

- `SelectedRange` (default `Day`), `IsLoading`, `HasData`, `HasError`, `IsEmpty`,
  `ErrorMessage`, `EarliestDataText`.
- `HistoryView? View`; `IReadOnlyList<HistorySession> Sessions`; `HistorySummary Summary`.
- `event EventHandler? ViewChanged` for imperative painting, matching the
  existing `ValuesUpdated` pattern.
- `Task EnsureLoadedAsync()`: load the 90-day window once, cache by `GetStamp()`,
  then apply the selected range.
- `Task SetRangeAsync(HistoryRange)`: recompute from the cached samples; no file
  read unless the stamp changed.
- `Task RefreshAsync()`: force a read for the "Check again" button.
- `void Dispose()`: cancel in-flight loads.
- All UI property updates happen on the dispatcher thread. No `DataContext`.
- `IsEmpty` means no samples at all; a range with no samples but existing history
  uses a separate range-empty state (`EarliestDataText`).

### `Views/HistoryPage.xaml(.cs)`

Layout and visual rules are in the design note below. The page exposes
`public HistoryViewModel ViewModel => App.History;`, uses `x:Bind` for simple
properties, and `{Binding}` inside the session `ListView` `DataTemplate`, matching
`SettingsPage.xaml`. On `Loaded`, `await ViewModel.EnsureLoadedAsync()`. On
`ViewChanged`, call `Chart.SetData(...)` and update the readout. The range
selector is handled in code-behind and calls `SetRangeAsync`.

### `Views/HistoryChart.xaml(.cs)`

A `UserControl` with a `Canvas`. `SetData(IReadOnlyList<HistorySample> samples,
HistoryRange range, DateTimeOffset from, DateTimeOffset to, bool showLimitGuide)`
draws:

- y grid at 0/25/50/75/100, x labels, 2 px step line, 4 px state strip, gaps.
- state colors: charging = success, discharging = secondary/strong neutral,
  AC idle = long dashes in a neutral. Never color-only: dash pattern and the
  strip carry the state too. The idle dashes are deliberately coarser than the
  fine-dotted 80% guide, because an idle plateau sits exactly on that guide.
- The neutral area fill is skipped for a run narrower than 4% of the plot (or
  24 px): at that width the polygon collapses into a vertical slab that reads as
  a bar rather than as area.
- 80% dashed guide only where `limitSet` is true.
- pointer hover: vertical guide, dot, and the page readout updates; no flyout.
- `AutomationProperties.Name` includes the range and the real min/max.
- re-render on `SizeChanged` and theme change. Resolve brushes through
  `{ThemeResource}` where possible; verify 3:1 stroke contrast in both themes.

### `MainWindow` and `App`

- Add `NavigationViewItem Content="History" Tag="History"` between Health and
  Settings, glyph `&#xE81C;` (verify it renders), and `--page=history`.
- Replace the hard-coded `MenuItems[1]/[2]` maps with a `Tag` lookup so inserting
  the item cannot silently break Settings/Health navigation.
- Do not add History to `UpdateSampling`; it does not use the 1 Hz live tier.
- On window `Activated`, if `CurrentSourcePageType == typeof(HistoryPage)`, call
  `App.History.RefreshFromStampAsync()` (a stamp check, not a full reload).
- `App.OnLaunched` creates `App.History = new HistoryViewModel(new HistoryStore())`
  before the window; `App.Quit` disposes it.

## UI design note

Window 1000x520, content 952x472, page `ScrollViewer` + `StackPanel Padding=24
Spacing=20`. Above the fold: title (36), chart card (~300), four tiles (62).
Sessions card scrolls below.

- Chart card: header `Charge level` + range selector; 872x192 plot; x labels;
  legend and persistent readout. One neutral 8% area fill, no gradients.
- Range selector: `24 h`, `7 d`, `30 d`, `90 d`, native `RadioButton`s in a joined
  strip, default `24 h`; keyboard arrows and Space work.
- Tiles: `On battery`, `On AC`, `Lowest level`, `Energy used (est.)`.
- Sessions: `Sessions` card, newest first, row = state, time range, duration,
  level change, energy. `ListView` `MaxHeight=240`, no selection. Empty state
  `No sessions in this range.`
- States: first run, range empty, one sample, gaps, 80% plateau, unreadable file,
  loading. First-run copy:

  `No history yet.`
  `Samples are recorded every 5 minutes while Windows is awake.`
  `History covers times when Windows is awake. Gaps mean the PC was asleep or off.`

- Default readout: `Latest 14:35, 80%, on AC, not charging.` Not "Now".
- Error: InfoBar `Could not read the history file.` with `Check again`; partial
  parse: `History file is incomplete. Showing what could be read.`
- Never show a fabricated number. Unknown uses the app's existing unknown token.
- No page-level spinner; a 16 px ring on the chart card header while the first
  load is running.
- Dark first. Verify light mode and 100% scale.

## Docs to update

- `README.md`: Desktop app feature list, install task list, data path, 90-day
  local history, uninstall behavior.
- `docs/UI-ARCHITECTURE.md`: third data tier (worker-written JSONL, app-read),
  file path, retention, project layout.
- `docs/DESIGN-BRIEF.md`: scope is four pages now; add History.
- This file: keep it in sync if the implementation changes a decision.

## Test plan

1. `battctl-worker.ps1 -Action sample` as the user writes exactly one valid JSONL
   line and exits 0; run it twice, two lines.
2. Task registration in `battctl-install.ps1` creates `BattCtlHistory` with the
   5-minute repetition and SYSTEM principal; `-ProvisionOnly` also registers it.
3. `schtasks /run /tn BattCtlHistory` adds a line; wait one interval and confirm
   a second line.
4. `install-ui.ps1` re-provisions the task after a worker copy.
5. Uninstall removes the task; `-RemoveData` removes the history directory.
6. App builds with `build.ps1` and launches with `run.ps1` and `--page=history`.
7. UI states: empty, one sample, range empty, gaps, 80% plateau, read error,
   24h/7d/30d/90d, dark/light, keyboard range selector.
8. Chart: line breaks at gaps; no 80% guide when `limitSet` is false or null;
   hover readout matches the nearest sample; min/max automation text is real.
9. `lint.yml` and `build-app.yml` pass. No test project exists; these checks plus
   the manual matrix are the verification.
10. Old worker + new app: the History page shows the first-run/empty state, not an
    error. New worker + old app: no visible change.

## Non-goals

- No CLI `battctl history` command.
- No CSV/JSON export in v1; the JSONL is readable.
- No cloud sync, telemetry, or per-user history.
- No app-side sampling or second writer.
- No rate/power chart; `Rate` is null on AC.
- No temperature (no WMI instance on the target machine).
- No wear/cycle trend UI in v1; the records already carry the fields for later.

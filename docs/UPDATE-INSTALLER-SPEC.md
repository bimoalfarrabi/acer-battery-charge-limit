# In-app update, Inno Setup installer, and release CI — implementation spec

Status: approved design, not yet implemented. Target: Acer Battery (BattCtl.App) +
BattCtl worker/CLI, Windows 10/11 x64.

## Decisions

- Installer/uninstaller: **Inno Setup 6.7.0**, per-machine, `WizardStyle=modern dynamic`,
  GUI wizard + built-in GUI uninstaller, silent upgrade support.
- Update source: **GitHub Releases API** (`/repos/bimoalfarrabi/acer-battery-charge-limit/releases/latest`),
  download `BattCtl-Setup-<version>.exe`, verify SHA-256, run it.
- Update check: once at startup in the background (non-blocking) + a manual
  **Check for updates** button in Settings. Auto-check can be turned off.
- No tray balloon, no auto-download, no forced relaunch.

## 1. Versioning

One version, three consumers (app, installer, worker/CLI).

- Add `Directory.Build.props` at the repo root:

  ```xml
  <Project>
    <PropertyGroup>
      <Version>1.2.0</Version>
      <IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
    </PropertyGroup>
  </Project>
  ```

  The local default is a placeholder; CI overrides it with `-p:Version=<tag without v>`.
- CI validates the tag against `^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$` before publishing.
- `Services/AppInfo.cs` reads `AssemblyInformationalVersionAttribute`, strips any
  `+<metadata>` suffix, and exposes `Version` (string) and `SemanticVersion` (`Version?`).
  Fall back to `AssemblyName.Version`.
- The installer writes `version.txt` to `{app}\version.txt` and
  `{autopf}\battctl\version.txt`. The app can show the worker version in About later;
  no snapshot schema change.

## 2. Inno Setup installer

Files:

- `installer/BattCtl.iss` — the installer script.
- `installer/provision.ps1` — idempotent post-install plumbing (tasks, PATH, mailbox ACL).
  It can call the existing `battctl-install.ps1` or be a thin wrapper around it.
- `installer/unprovision.ps1` — removes tasks, PATH entry, and (optionally) data.
- `build-installer.ps1` — local build wrapper (publish + ISCC).

`BattCtl.iss` requirements:

- `AppId={{...}}` stable GUID, `AppName=Acer Battery`, `AppVersion={#MyAppVersion}`,
  `AppPublisher=bimoalfarrabi`, `DefaultDirName={autopf}\BattCtl.App`,
  `PrivilegesRequired=admin`, `ArchitecturesAllowed=x64compatible`,
  `ArchitecturesInstallIn64BitMode=x64compatible`.
- `WizardStyle=modern dynamic`, `WizardBackColor`/`WizardBackColorDynamicDark` if
  available in 6.7, `SetupIconFile=..\src\BattCtl.App\Assets\TrayIcon.ico`,
  `UninstallDisplayIcon={app}\BattCtl.App.exe`.
- `CloseApplications=no`, `RestartApplications=no`, `AppMutex=BattCtl.App.Running`.
- `OutputBaseFilename=BattCtl-Setup-{#MyAppVersion}`, `Compression=lzma2`,
  `SolidCompression=yes`, `DisableProgramGroupPage=yes`.
- `[Files]`:
  - app publish output (`{#PublishDir}\*`) to `{app}`;
  - `battctl-worker.ps1`, `battctl.ps1`, `battctl.cmd`, `wsl\battctl` to `{autopf}\battctl`;
  - `battctl-install.ps1` to `{app}\install`;
  - `version.txt` to `{app}` and `{autopf}\battctl`.
- `[Dirs]`: `{commonappdata}\battctl\mailbox` with `Permissions: users-modify`.
  Never grant modify on `{app}`.
- `[Icons]`: Start Menu shortcut `Acer Battery`.
- `[InstallDelete]`: `Type: filesandordirs; Name: "{app}"` only. Never point at
  `{autopf}\battctl` or `C:\ProgramData\battctl`.
- `[Run]`:
  - `powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{app}\install\battctl-install.ps1" -Quiet`
    (add a `-Quiet` switch to the script; it is already elevated, so it must not
    self-elevate again);
  - `{app}\BattCtl.App.exe` with `Flags: nowait runasoriginaluser` so a silent
    upgrade relaunches the app unelevated.
- `[UninstallRun]`: `powershell.exe ... unprovision.ps1 -KeepData` (or `-RemoveData`
  based on the user's choice).
- `[Code]`:
  - `PrepareToInstall`: if the `BattCtl.App.Running` mutex is held, show
    "Acer Battery is running. Quit it from the tray menu, then run the installer again."
    and abort. The in-app updater quits the app before launching Setup, so silent
    upgrades pass.
  - `InitializeUninstall`: `MsgBox('Also remove your settings and logs?', mbConfirmation, MB_YESNO)`
    → set a flag for `[UninstallRun]` (`-RemoveData` vs `-KeepData`). Default is keep.
- Silent flags used by the updater: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-`.
  Interactive users get the wizard.

`provision.ps1`:

- Idempotent. Registers `BattCtl` and `BattCtlResume` as SYSTEM/highest, sets the
  task SDDL so Interactive Users may run, adds `{autopf}\battctl` to Machine PATH
  using `DoNotExpandEnvironmentNames` semantics, creates the mailbox and grants the
  invoking user Modify.
- Reuse `battctl-install.ps1` where possible; do not reimplement SDDL in Pascal.

`unprovision.ps1`:

- Unregisters both tasks, removes the PATH entry, removes `{autopf}\battctl`.
- `-KeepData` (default): leaves `C:\ProgramData\battctl` and `%LOCALAPPDATA%\BattCtl.App`.
- `-RemoveData`: also removes `C:\ProgramData\battctl` and `%LOCALAPPDATA%\BattCtl.App`,
  and the HKCU Run entry for every real user profile (reuse `uninstall.ps1` logic).

## 3. Release CI

Add `.github/workflows/release.yml`:

- Triggers: `push: tags: ['v*']` and `workflow_dispatch` with a `version` input.
- `permissions: contents: write`.
- Steps:
  1. checkout;
  2. setup-dotnet 9;
  3. resolve version: tag (`v` stripped) or input; validate semver; fail early;
  4. `dotnet restore`;
  5. `dotnet publish src/BattCtl.App/BattCtl.App.csproj -c Release -p:Platform=x64
     -p:Version=$version -p:WindowsAppSDKSelfContained=true -p:SelfContained=true
     -p:PublishTrimmed=false --artifacts-path $env:RUNNER_TEMP\artifacts`;
  6. verify payload (`BattCtl.App.exe`, `BattCtl.App.dll`, `resources.pri`,
     `Microsoft.WinUI.dll`);
  7. install Inno Setup 6.7.0 pinned (`choco install innosetup --version=6.7.0 -y`
     or `winget install --id JRSoftware.InnoSetup --version 6.7.0 --exact`);
  8. build installer: `ISCC.exe /DMyAppVersion=$version /DPublishDir=<publish>
     installer\BattCtl.iss`;
  9. generate `SHA256SUMS.txt` with `Get-FileHash -Algorithm SHA256`;
  10. create a portable zip of the publish output;
  11. upload artifacts;
  12. create the GitHub Release with `softprops/action-gh-release@v3`
      (`generate_release_notes: true`, `prerelease: ${{ contains(github.ref_name, '-') }}`),
      attaching the installer, portable zip, and `SHA256SUMS.txt`.
- Use one explicit `--artifacts-path` under `$env:RUNNER_TEMP`; do not rely on
  `$env:LOCALAPPDATA`.
- A prerelease tag (`v1.2.0-rc.1`) must create a prerelease so `/releases/latest`
  still returns the previous stable.
- Test with `workflow_dispatch` and a `v1.2.0-rc.1` dry run before a real tag.

## 4. In-app updater

New `Services/UpdateService.cs` (`ObservableObject`, owns its own state; do not bloat
`MainViewModel`):

- `UpdateState`: `Idle`, `Checking`, `UpToDate`, `Available`, `Downloading`, `Ready`,
  `Installing`, `Error`, `Offline`, `RateLimited`.
- Properties: `State`, `CurrentVersion`, `LatestVersion`, `ReleaseNotes`,
  `DownloadProgress`, `Message`, `MessageIsError`, `LastChecked`, `AutoCheckEnabled`.
- `CheckForUpdatesAsync(bool manual, CancellationToken)`:
  - `GET https://api.github.com/repos/bimoalfarrabi/acer-battery-charge-limit/releases/latest`
    with `User-Agent: BattCtl/<version>` and `Accept: application/vnd.github+json`.
  - Parse `tag_name`, `body`, `assets[]` (`name`, `browser_download_url`, `size`,
    `digest`). Find `BattCtl-Setup-*.exe`.
  - Compare `tag_name` (strip `v`) to `AppInfo.SemanticVersion`.
  - Cache the result for 6 hours in `%LOCALAPPDATA%\BattCtl.App\update-cache.json`.
  - One attempt per launch. Automatic checks are silent on failure; manual checks
    surface the error.
  - 403 with `X-RateLimit-Remaining: 0` → `RateLimited` (manual) or silent `Idle`
    (automatic).
- `DownloadAsync(IProgress<double>, CancellationToken)`:
  - Download to `%LOCALAPPDATA%\BattCtl.App\updates\BattCtl-Setup-<version>.exe.part`,
    then rename.
  - Verify SHA-256 against the asset `digest` (`sha256:...`) or `SHA256SUMS.txt`
    using `SHA256.HashData` + `CryptographicOperations.FixedTimeEquals`.
  - On mismatch, delete the file and set `Error`.
  - Cancel deletes the `.part` file.
- `InstallAsync()`:
  - Gracefully quit the app (`App.Quit()`), then launch the installer with
    `UseShellExecute = true`, `Verb = "runas"`, arguments
    `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-`.
  - UAC cancel (`Win32Exception.NativeErrorCode == 1223`) → quiet message, keep the
    downloaded file, retry skips the download.
  - The installer's `[Run]` relaunches the app with `runasoriginaluser`.
- Security wording: SHA-256 proves integrity, not authenticity. The authenticity
  anchor is TLS to github.com plus the release attestation. No "install anyway"
  bypass. Code signing is a follow-up (SignPath Foundation for OSS).

## 5. UI

### Settings — Updates card

Place directly above the **About** card. Title `Updates`, subtitle
`Checks GitHub for new versions. Runs automatically once at startup.`

State matrix:

| state | button | status line | ring | InfoBar |
|---|---|---|---|---|
| idle (never checked) | `Check for updates` | `Last checked: —` | hidden | none |
| checking | `Checking…` (disabled) | `Checking for updates…` | visible | none |
| up to date | `Check for updates` | `You're up to date. Last checked HH:mm.` | hidden | none |
| update available | `Download & install` | `Version X.Y.Z is available (you have X.Y.W)` | hidden | Informational, title `Update available`, first line of release notes, scrollable notes (MaxHeight 160) |
| downloading | `Cancel` | `Downloading X.Y.Z… 42%` | visible | none |
| ready to install | `Install and restart` | `Version X.Y.Z downloaded` | hidden | Informational, `Ready to install` |
| installing | disabled | `Installing…` | visible | none |
| error | `Check for updates` (retry) | `Couldn't check for updates.` | hidden | Error, actual reason, action `Try again` |
| offline | `Check for updates` | `No connection. Last checked HH:mm.` | hidden | Warning, `You appear to be offline` (closable) |
| rate limited | `Check for updates` | `GitHub is rate-limiting checks. Try again later.` | hidden | Warning |

Add an `Auto-check for updates at startup` ToggleSwitch (default on) below the
action row. Persist in `%LOCALAPPDATA%\BattCtl.App\update-settings.json`.

### Dashboard — update available

An `InfoBar` pinned as the first element under the header, `Severity=Informational`,
`IsClosable=True`, copy `Version X.Y.Z is available.` with a `View in Settings`
button. No direct install from the Dashboard. If closed, it does not return until a
new version appears.

### About copy

Change the About card from "no data leaves the machine" to:

> Everything runs locally. The only network request is an update check to GitHub
> Releases, which you can turn off in Updates.

Amend `docs/DESIGN-BRIEF.md`'s "No telemetry, no network, no accounts" non-goal to
name the single exception precisely.

## 6. App lifecycle changes

- Add a named mutex `BattCtl.App.Running` in `Program.Main` (or `App`), released on
  quit. Inno's `AppMutex` uses it.
- Make `App.Quit()` internal/public so `UpdateService` can quit gracefully before
  launching the installer. It already disposes the tray, so no ghost icon.
- `MainWindow.Closed` keeps hiding to tray; do not change that.
- Replace `Stop-Process -Force` in `install-ui.ps1` and `uninstall.ps1` with a
  mutex check + a clear "quit from the tray" message. The Inno installer/uninstaller
  is the new path; the PowerShell scripts remain as fallback.

## 7. File list

Phase 1 (versioning + installer + CI):

- `Directory.Build.props` (new)
- `installer/BattCtl.iss` (new)
- `installer/provision.ps1` (new)
- `installer/unprovision.ps1` (new)
- `build-installer.ps1` (new)
- `.github/workflows/release.yml` (new)
- `battctl-install.ps1` — add `-Quiet` (no self-elevation, no prompts)
- `uninstall.ps1` — add `-KeepData` / `-RemoveData`
- `README.md` — installer, release, SmartScreen/checksum notes

Phase 2 (updater + UI):

- `src/BattCtl.App/Services/AppInfo.cs` (new)
- `src/BattCtl.App/Services/UpdateService.cs` (new)
- `src/BattCtl.App/Views/SettingsPage.xaml(.cs)` — Updates card
- `src/BattCtl.App/Views/DashboardPage.xaml(.cs)` — update InfoBar
- `src/BattCtl.App/App.xaml.cs` — startup check, public Quit, named mutex
- `src/BattCtl.App/Program.cs` — named mutex
- `src/BattCtl.App/MainWindow.xaml.cs` — no change to hide-to-tray
- `docs/DESIGN-BRIEF.md` — network exception
- `README.md` — update section

## 8. Test plan

1. `dotnet build` clean; PSScriptAnalyzer clean.
2. `build-installer.ps1` produces `BattCtl-Setup-<version>.exe`; install on a clean
   VM; verify app, worker, CLI, tasks, PATH, mailbox ACL, Start Menu shortcut.
3. Silent upgrade from v1.1.0: app running → updater quits it → installer
   `/VERYSILENT` → app relaunches unelevated; no ghost tray icon; `C:\ProgramData\battctl`
   preserved.
4. Uninstall: tasks gone, PATH entry gone, app dir gone; data kept by default;
   "Also remove my settings and logs" removes it.
5. Updater: up-to-date, available, download progress, cancel, hash mismatch,
   missing asset, rate limit, offline, UAC cancel, full install/relaunch.
6. Release workflow: `workflow_dispatch` with `1.2.0`; artifact + checksum match;
   prerelease tag does not disturb `/releases/latest`.
7. Regression: charge limit and GPU commands still work after upgrade.

## 9. Non-goals

- No MSIX, no Store, no WinGet publishing (follow-up).
- No auto-download, no forced relaunch, no tray balloon.
- No transactional rollback; provisioning is idempotent and the recovery is
  "run the installer again".
- No code signing in this pass; document the SmartScreen warning and the checksum.

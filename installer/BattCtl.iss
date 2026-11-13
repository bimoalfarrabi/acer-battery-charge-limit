; BattCtl.iss - Inno Setup script for the Acer Battery desktop app.
; Build with ISCC.exe /DMyAppVersion=<version> /DPublishDir=<publish output>,
; see build-installer.ps1 or .github/workflows/release.yml.

#define MyAppName "Acer Battery"
#define MyAppPublisher "bimoalfarrabi"
#define MyAppExeName "BattCtl.App.exe"

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "publish"
#endif

[Setup]
AppId={{6A5B4C9E-1D72-4E8F-9B3A-7C21D0F5E8A4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\BattCtl.App
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern dynamic
SetupIconFile=..\src\BattCtl.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=no
RestartApplications=no
AppMutex=BattCtl.App.Running
OutputDir=output
OutputBaseFilename=BattCtl-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
DisableProgramGroupPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs
Source: "..\battctl-worker.ps1"; DestDir: "{autopf}\battctl"
Source: "..\battctl.ps1"; DestDir: "{autopf}\battctl"
Source: "..\battctl.cmd"; DestDir: "{autopf}\battctl"
Source: "..\wsl\battctl"; DestDir: "{autopf}\battctl"
Source: "..\battctl-install.ps1"; DestDir: "{app}\install"
Source: "..\installer\provision.ps1"; DestDir: "{app}\install"
Source: "..\installer\unprovision.ps1"; DestDir: "{app}\install"

[Dirs]
; The mailbox must be writable by the normal user (medium IL); {app} stays admin-only.
Name: "{commonappdata}\battctl\mailbox"; Permissions: users-modify

[Icons]
Name: "{autoprograms}\Acer Battery"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"

[InstallDelete]
; Clear stale files from the previous {app} contents only. Never point this at
; {autopf}\battctl or C:\ProgramData\battctl - they hold user state.
Type: filesandordirs; Name: "{app}"

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install\provision.ps1"""; Flags: runhidden
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\install\unprovision.ps1"" {code:UnprovisionArgs}"; Flags: runhidden; RunOnceId: "BattCtlUnprovision"

[Code]
// version.txt: written at install time from {#MyAppVersion} so the app and the
// worker can report their version without reading the assembly.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    SaveStringToFile(ExpandConstant('{app}\version.txt'), '{#MyAppVersion}' + #13#10, False);
    SaveStringToFile(ExpandConstant('{autopf}\battctl\version.txt'), '{#MyAppVersion}' + #13#10, False);
  end;
end;

// Data stays on disk by default; the uninstall prompt below decides whether to
// remove it, and the [UninstallRun] entry receives the matching switch.
var
  RemoveDataOnUninstall: Boolean;

function UnprovisionArgs(Param: string): string;
begin
  if RemoveDataOnUninstall then
    Result := '-RemoveData'
  else
    Result := '-KeepData';
end;

function PrepareToInstall(var NeedsRestart: Boolean): string;
begin
  Result := '';
  if CheckForMutexes('BattCtl.App.Running') then
  begin
    Result := 'Acer Battery is running. Quit it from the tray menu, then run the installer again.';
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;

  // The uninstaller must never force-kill the tray app: a killed process leaves
  // a ghost tray icon and can leave the worker mailbox mid-write.
  if CheckForMutexes('BattCtl.App.Running') then
  begin
    MsgBox('Acer Battery is running. Quit it from the tray menu, then uninstall again.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  RemoveDataOnUninstall := False;
  if UninstallSilent then
    Exit;
  if MsgBox('Also remove your settings and logs?', mbConfirmation, MB_YESNO) = IDYES then
    RemoveDataOnUninstall := True;
end;

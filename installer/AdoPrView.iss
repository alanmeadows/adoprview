; Per-user installer for the self-contained Windows x64 build. Built by
; .github/scripts/New-ReleasePackage.ps1, which defines AppVersion, StageDir,
; OutputDir and OutputName. User data in %LOCALAPPDATA%\AdoPrView (settings,
; token, WebView2 profiles, Copilot run history) is never installed, changed
; or removed by this installer.
#ifndef AppVersion
  #error AppVersion must be defined
#endif

[Setup]
AppId={{5D2F66AD-977D-4B94-BD69-094B8FD6E532}
AppName=ADO PR View
AppVersion={#AppVersion}
AppVerName=ADO PR View {#AppVersion}
AppPublisher=ADO PR View contributors
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\ADO PR View
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
SetupIconFile=..\src\AdoPrView\app.ico
UninstallDisplayIcon={app}\AdoPrView.exe
UninstallDisplayName=ADO PR View
OutputDir={#OutputDir}
OutputBaseFilename={#OutputName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no

[Files]
Source: "{#StageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\ADO PR View"; Filename: "{app}\AdoPrView.exe"; WorkingDir: "{app}"

[Run]
Filename: "{app}\AdoPrView.exe"; Description: "Launch ADO PR View"; Flags: nowait postinstall skipifsilent

[Code]
const
  DashboardOpen = 'ADO PR View is running, or setup could not confirm that it is closed. ' +
    'Exit every running dashboard (tray icon > Exit), let it finish any Copilot shutdown it reports, then run this again. ' +
    'Nothing was changed.';

{ True when any AdoPrView.exe is running or the process list cannot be read.
  Setup never closes the dashboard itself. }
function DashboardMayBeRunning(): Boolean;
var
  Locator, Service, Processes: Variant;
begin
  Result := True;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''AdoPrView.exe''');
    Result := Processes.Count > 0;
  except
    Result := True;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if DashboardMayBeRunning() then
    Result := DashboardOpen
  else
    Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  Result := not DashboardMayBeRunning();
  if (not Result) and (not UninstallSilent) then
    MsgBox(DashboardOpen, mbError, MB_OK);
end;

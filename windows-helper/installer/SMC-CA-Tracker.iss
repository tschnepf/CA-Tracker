; SMC CA Tracker installer (Inno Setup 6, https://jrsoftware.org/isinfo.php).
; Built by build.ps1 -Installer, which publishes the app (self-contained, so PCs don't need .NET)
; into ..\dist and passes the version. Installs per user — no administrator rights needed — and
; updates in place: the app checks the company's update location and runs a newer installer quietly.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{6B0E3E7A-5C1D-4F7B-9A57-2C4D7B1E8F31}
AppName=SMC CA Tracker
AppVersion={#AppVersion}
AppPublisher=SMC
DefaultDirName={localappdata}\Programs\SMC CA Tracker
DefaultGroupName=SMC CA Tracker
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=SMC-CA-Tracker-Setup-{#AppVersion}
SetupIconFile=..\src\SmcCaTracker.App\Assets\app.ico
UninstallDisplayIcon={app}\SmcCaTracker.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; The app holds this mutex while running; setup waits for (or closes) it.
AppMutex=SMC-CA-Tracker
CloseApplications=yes
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\dist\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion
; The company's settings, when IT provides one (see company\ca-tracker-company.example.json).
Source: "..\company\ca-tracker-company.json"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{userprograms}\SMC CA Tracker"; Filename: "{app}\SmcCaTracker.exe"
Name: "{userdesktop}\SMC CA Tracker"; Filename: "{app}\SmcCaTracker.exe"; Tasks: desktopicon

[Registry]
; smc-ca-tracker:// links (the buttons in Workload Tracker's lists) open the app, for this Windows user only.
; The app also writes this at startup when it is missing or names a copy that is gone.
Root: HKCU; Subkey: "Software\Classes\smc-ca-tracker"; ValueType: string; ValueName: ""; ValueData: "URL:SMC CA Tracker"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\smc-ca-tracker"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\smc-ca-tracker\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\SmcCaTracker.exe"",0"
Root: HKCU; Subkey: "Software\Classes\smc-ca-tracker\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\SmcCaTracker.exe"" ""%1"""

[Run]
; Also runs after a quiet update, so the app comes back by itself.
Filename: "{app}\SmcCaTracker.exe"; Description: "Start SMC CA Tracker"; Flags: nowait postinstall

[Code]
function WebView2Installed(): Boolean;
var
  Version: String;
begin
  Result :=
    RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) or
    RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version);
  Result := Result and (Version <> '') and (Version <> '0.0.0.0');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WizardSilent() and not WebView2Installed() then
    MsgBox('CA Tracker needs the Microsoft Edge WebView2 Runtime, which this PC does not seem to have.' + #13#10 +
      'Install it from https://developer.microsoft.com/microsoft-edge/webview2/ (Evergreen Runtime).',
      mbInformation, MB_OK);
end;

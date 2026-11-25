; Inno Setup script for Glance. Build the package first, then compile this:
;     .\build\publish.ps1
;     .\build\make-installer.ps1
#define AppName "Glance"
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#define AppUrl "https://github.com/ZanDvorsak/Glance"

[Setup]
; keep this GUID stable: it identifies the app for upgrades and uninstall
AppId={{6B3F2D0E-5A1C-4C8E-9E57-2F0B7A41D9C3}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppName}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=Glance-Setup-{#AppVersion}
SetupIconFile=..\assets\glance.ico
UninstallDisplayIcon={app}\Glance.App.exe
InfoBeforeFile=before-install.txt
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
; the runner and editor are stopped by the [Code] section below
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\dist\Glance\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Glance.App.exe"
Name: "{autoprograms}\{#AppName} Runner (tray)"; Filename: "{app}\Glance.Runner.exe"; Comment: "Keeps your theme on the display; asks for administrator rights"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Glance.App.exe"; Tasks: desktopicon

[Run]
; runasoriginaluser: start the editor with the normal user token, not the installer's administrator one
Filename: "{app}\Glance.App.exe"; Description: "Start Glance"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; remove the "start at login" task if the user enabled it, and stop anything still running
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Glance Runner"" /F"; Flags: runhidden; RunOnceId: "DelTask"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Glance.App.exe /F"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Glance.Runner.exe /F"; Flags: runhidden; RunOnceId: "KillRunner"

[Code]
// Files cannot be replaced while the apps run, so stop them before installing or upgrading.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Glance.App.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Glance.Runner.exe /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

; Inno Setup script for Glance. Build the package first, then compile this:
;     .\build\publish.ps1
;     .\build\make-installer.ps1
;
; Glance installs a Windows service, "GlanceService" (LocalSystem, automatic start), that owns the USB display and reads the
; sensors; the editor and the tray app run with the user's normal rights. See docs/design/runner-service.md.
; Everything this script starts while elevated is a Windows tool (sc.exe, net.exe, schtasks.exe, taskkill.exe, cmd.exe /d):
; no .NET program runs elevated with the installing user's environment (review H6/H1).
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
; the service, the tray apps and the editor are stopped by the [Code] section below
CloseApplications=no

[Tasks]
Name: "trayatsignin"; Description: "Show my saved theme on the display when I sign in (starts the Glance tray app for every user)"
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\dist\Glance\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Glance.App.exe"
Name: "{autoprograms}\{#AppName} tray app"; Filename: "{app}\Glance.Runner.exe"; Comment: "Shows your saved theme on the display"
Name: "{commonstartup}\{#AppName} tray app"; Filename: "{app}\Glance.Runner.exe"; Comment: "Shows your saved theme on the display"; Tasks: trayatsignin
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Glance.App.exe"; Tasks: desktopicon

[Run]
; runasoriginaluser: start the tray app and the editor with the normal user token, not the installer's administrator one
Filename: "{app}\Glance.Runner.exe"; Flags: nowait runasoriginaluser skipifsilent; Tasks: trayatsignin
Filename: "{app}\Glance.App.exe"; Description: "Start Glance"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; net.exe waits until the service has stopped (it blanks the display), then the service is removed
Filename: "{sys}\net.exe"; Parameters: "stop GlanceService"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete GlanceService"; Flags: runhidden waituntilterminated; RunOnceId: "DelService"
; the elevated sign-in task of Glance 0.1, in case it is still there
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Glance Runner"" /F"; Flags: runhidden; RunOnceId: "DelTask"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Glance.App.exe /F"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Glance.Runner.exe /F"; Flags: runhidden; RunOnceId: "KillRunner"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Glance.Service.exe /F"; Flags: runhidden; RunOnceId: "KillService"

[UninstallDelete]
; only the service's own log, never a whole folder: %ProgramData%\Glance could be a link planted by a user
Type: files; Name: "{commonappdata}\Glance\service.log"
Type: dirifempty; Name: "{commonappdata}\Glance"

[Code]
const
  GENERIC_READ = $80000000;
  GENERIC_WRITE = $40000000;
  OPEN_EXISTING = 3;
  // SECURITY_SQOS_PRESENT with SECURITY_ANONYMOUS (0): whoever serves the pipe can never act as this elevated installer
  SECURITY_SQOS_PRESENT = $00100000;
  ERROR_SERVICE_DOES_NOT_EXIST = 1060;

function CreateFileW(lpFileName: String; dwDesiredAccess, dwShareMode: Cardinal; lpSecurityAttributes: Integer;
  dwCreationDisposition, dwFlagsAndAttributes: Cardinal; hTemplateFile: Integer): Integer;
  external 'CreateFileW@kernel32.dll stdcall';
function WriteFile(hFile: Integer; const lpBuffer: AnsiString; nNumberOfBytesToWrite: Cardinal;
  var lpNumberOfBytesWritten: Cardinal; lpOverlapped: Integer): Boolean;
  external 'WriteFile@kernel32.dll stdcall';
function CloseHandle(hObject: Integer): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function WaitNamedPipeW(lpNamedPipeName: String; nTimeOut: Cardinal): Boolean;
  external 'WaitNamedPipeW@kernel32.dll stdcall';

function RunHidden(const Exe, Params: String): Integer;
var
  Code: Integer;
begin
  if not Exec(Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then Code := -1;
  Log(Exe + ' ' + Params + ' -> ' + IntToStr(Code));
  Result := Code;
end;

function Sc(const Params: String): Integer;
begin
  Result := RunHidden(ExpandConstant('{sys}\sc.exe'), Params);
end;

// tasklist always exits 0, so look for the name in its output; cmd /d skips the user's AutoRun commands
function IsRunning(const Image: String): Boolean;
begin
  Result := RunHidden(ExpandConstant('{cmd}'), '/d /s /c ""' + ExpandConstant('{sys}\tasklist.exe') + '" /FI "IMAGENAME eq ' + Image +
    '" /NH | "' + ExpandConstant('{sys}\find.exe') + '" /I "' + Image + '""') = 0;
end;

// Glance 0.1.1's runner runs elevated and listens on \\.\pipe\Glance.Runner.Control. Asking it to EXIT lets it blank the
// display before it closes; taskkill (in PrepareToInstall) is only the fallback.
procedure AskOldRunnerToExit();
var
  H, I: Integer;
  Written: Cardinal;
  Asked: Boolean;
begin
  Asked := False;
  if WaitNamedPipeW('\\.\pipe\Glance.Runner.Control', 2000) then
  begin
    H := CreateFileW('\\.\pipe\Glance.Runner.Control', GENERIC_READ or GENERIC_WRITE, 0, 0, OPEN_EXISTING, SECURITY_SQOS_PRESENT, 0);
    if H <> -1 then
    begin
      Asked := WriteFile(H, 'EXIT' + #10, 5, Written, 0);
      CloseHandle(H);
      Log('Asked the old Glance runner to exit: ' + IntToStr(Ord(Asked)));
    end;
  end;
  if Asked then
    for I := 1 to 25 do
    begin
      if not IsRunning('Glance.Runner.exe') then Exit;
      Sleep(200);
    end;
end;

// Files cannot be replaced while Glance runs, so stop everything before installing or upgrading.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  AskOldRunnerToExit();
  // the elevated sign-in task of Glance 0.1: the service replaces it
  RunHidden(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "Glance Runner" /F');
  // an earlier service (upgrade): net.exe waits until it has stopped, which blanks the display
  RunHidden(ExpandConstant('{sys}\net.exe'), 'stop GlanceService');
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/IM Glance.App.exe /F');
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/IM Glance.Runner.exe /F');
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/IM Glance.Service.exe /F');
  Result := '';
end;

// Creates (or updates) the service, its recovery actions, its permissions and its pre-shutdown timeout, then starts it.
// The binary path is quoted, so the space in "Program Files" can never be used to start another program.
procedure InstallService();
var
  Bin: String;
begin
  Bin := 'binPath= "\"' + ExpandConstant('{app}\Glance.Service.exe') + '\"" start= auto obj= LocalSystem DisplayName= "Glance display service"';
  if Sc('query GlanceService') = ERROR_SERVICE_DOES_NOT_EXIST then
    Sc('create GlanceService ' + Bin)
  else
    Sc('config GlanceService ' + Bin);
  Sc('description GlanceService "Drives the Glance USB display and reads the hardware sensors. Themes are drawn by the signed-in user''s Glance apps."');
  // restart after a crash: 5 s, 10 s, then every minute; the count resets after a day
  Sc('failure GlanceService reset= 86400 actions= restart/5000/restart/10000/restart/60000');
  // Windows' default rules plus "start" (RP) for interactive users, so the editor starts it without a prompt; stopping needs an administrator
  Sc('sdset GlanceService D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)');
  // how long Windows waits for the display to be blanked at shutdown (milliseconds)
  if not RegWriteDWordValue(HKLM, 'SYSTEM\CurrentControlSet\Services\GlanceService', 'PreshutdownTimeout', 5000) then
    Log('Could not set PreshutdownTimeout');
  Sc('start GlanceService');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then InstallService();
end;

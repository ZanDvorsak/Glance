; Inno Setup script for Glance. Build the package first, then compile this:
;     .\build\publish.ps1
;     .\build\make-installer.ps1
;
; Glance installs a Windows service, "GlanceService" (LocalSystem, automatic start), that owns the USB display and reads the
; sensors; the editor and the tray app run with the user's normal rights. See docs/design/runner-service.md.
; Everything this script starts while elevated is a Windows tool (sc.exe, net.exe, schtasks.exe, taskkill.exe, icacls.exe,
; cmd.exe /d): no .NET program runs elevated with the installing user's environment (review H6/H1).
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
; SYSTEM runs Glance.Service.exe from the install folder, so it is always Program Files, never a folder picked by hand (a folder
; created under C:\ or on a data drive inherits "Modify" for all users) and never an earlier custom folder (review H1)
DefaultDirName={autopf}\{#AppName}
DisableDirPage=yes
UsePreviousAppDir=no
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

[InstallDelete]
; files of older versions that this version no longer ships (an elevated helper among them)
Type: files; Name: "{app}\Glance.SensorHost.*"

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
; no recovery restart while the service is being removed (L4); net.exe waits until it has stopped (it blanks the display)
Filename: "{sys}\sc.exe"; Parameters: "failure GlanceService reset= 0 actions= """""; Flags: runhidden waituntilterminated; RunOnceId: "NoRestart"
Filename: "{sys}\net.exe"; Parameters: "stop GlanceService"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete GlanceService"; Flags: runhidden waituntilterminated; RunOnceId: "DelService"
; the elevated sign-in task of Glance 0.1, in case it is still there
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""Glance Runner"" /F"; Flags: runhidden; RunOnceId: "DelTask"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Glance.App.exe /F"; Flags: runhidden; RunOnceId: "KillApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Glance.Runner.exe /F"; Flags: runhidden; RunOnceId: "KillRunner"
Filename: "{sys}\taskkill.exe"; Parameters: "/IM Glance.Service.exe /F"; Flags: runhidden; RunOnceId: "KillService"

[Code]
const
  GENERIC_READ = $80000000;
  GENERIC_WRITE = $40000000;
  OPEN_EXISTING = 3;
  // SECURITY_SQOS_PRESENT with SECURITY_ANONYMOUS (0): whoever serves the pipe can never act as this elevated installer
  SECURITY_SQOS_PRESENT = $00100000;
  INVALID_FILE_ATTRIBUTES = $FFFFFFFF;
  ERROR_SERVICE_DOES_NOT_EXIST = 1060;
  ERROR_SERVICE_ALREADY_RUNNING = 1056;
  ERROR_SERVICE_NOT_ACTIVE = 1062;

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
function GetFileAttributesW(lpFileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

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

// Shows (and logs) a step that failed, with its exit code (L4).
procedure ServiceStepFailed(const Step: String; Code: Integer);
begin
  Log('Service step failed: ' + Step + ' (exit code ' + IntToStr(Code) + ')');
  SuppressibleMsgBox('Glance could not ' + Step + ' (exit code ' + IntToStr(Code) + ').' + #13#10#13#10 +
    'The display and the full sensors need the Glance service. Restart Windows and run this setup again; if it keeps failing, ' +
    'the setup log in %TEMP% has the details.', mbError, MB_OK, IDOK);
end;

// tasklist always exits 0, so look for the name in its output; cmd /d skips the user's AutoRun commands
function IsRunning(const Image: String): Boolean;
begin
  Result := RunHidden(ExpandConstant('{cmd}'), '/d /s /c ""' + ExpandConstant('{sys}\tasklist.exe') + '" /FI "IMAGENAME eq ' + Image +
    '" /NH | "' + ExpandConstant('{sys}\find.exe') + '" /I "' + Image + '""') = 0;
end;

// Waits until no Glance.Service.exe runs any more (net.exe can give up before the service has stopped).
function WaitForServiceStopped(Seconds: Integer): Boolean;
var
  I: Integer;
begin
  for I := 1 to Seconds * 2 do
  begin
    if not IsRunning('Glance.Service.exe') then
    begin
      Result := True;
      Exit;
    end;
    Sleep(500);
  end;
  Result := not IsRunning('Glance.Service.exe');
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

// Glance 0.1's logon task starts Glance.Runner.exe with highest privileges and the user's environment; delete it and check (M3).
procedure RemoveOldTask();
begin
  RunHidden(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "Glance Runner" /F');
  if RunHidden(ExpandConstant('{sys}\schtasks.exe'), '/Query /TN "Glance Runner"') = 0 then
    SuppressibleMsgBox('The "Glance Runner" sign-in task of Glance 0.1 could not be removed. It would start the Glance tray app ' +
      'with administrator rights. The Glance service removes it at its next start; you can also delete it in Task Scheduler.',
      mbError, MB_OK, IDOK);
end;

// An earlier Glance service (upgrade): no recovery restart, a clean stop (it blanks the display), and only then a kill (L4).
procedure StopOldService();
begin
  if Sc('query GlanceService') = ERROR_SERVICE_DOES_NOT_EXIST then Exit;
  Sc('failure GlanceService reset= 0 actions= ""');
  RunHidden(ExpandConstant('{sys}\net.exe'), 'stop GlanceService');
  if not WaitForServiceStopped(20) then
    RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/IM Glance.Service.exe /F');   // no restart follows: the actions are off
end;

// Files cannot be replaced while Glance runs, so stop everything before installing or upgrading.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  AskOldRunnerToExit();
  RemoveOldTask();
  StopOldService();
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/IM Glance.App.exe /F');
  RunHidden(ExpandConstant('{sys}\taskkill.exe'), '/IM Glance.Runner.exe /F');
  Result := '';
end;

// H1: only SYSTEM and Administrators may change the program folder; users may read and run. Whatever the folder inherited
// before (other software can loosen Program Files rules), the explicit rules replace it; Windows passes them on to every file.
function LockInstallFolder(): Boolean;
var
  Code: Integer;
begin
  Code := RunHidden(ExpandConstant('{sys}\icacls.exe'), '"' + ExpandConstant('{app}') + '" /inheritance:r /grant:r ' +
    '*S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX *S-1-15-2-1:(OI)(CI)RX');
  if Code = 0 then Code := RunHidden(ExpandConstant('{sys}\icacls.exe'), '"' + ExpandConstant('{app}') + '" /setowner *S-1-5-32-544 /T /C /Q');
  Result := Code = 0;
  if not Result then ServiceStepFailed('make the Glance folder changeable by administrators only', Code);
end;

// Creates (or updates) the service, its recovery actions, its permissions and privileges, then starts it. The binary path is
// quoted, so the space in "Program Files" can never be used to start another program. The service sets its own pre-shutdown
// timeout through the service API at start (L5).
procedure InstallService();
var
  Bin: String;
  Code: Integer;
begin
  if not LockInstallFolder() then Exit;   // never register a SYSTEM service in a folder users might change
  Bin := 'binPath= "\"' + ExpandConstant('{app}\Glance.Service.exe') + '\"" start= auto obj= LocalSystem DisplayName= "Glance display service"';
  if Sc('query GlanceService') = ERROR_SERVICE_DOES_NOT_EXIST then
    Code := Sc('create GlanceService ' + Bin)
  else
    Code := Sc('config GlanceService ' + Bin);
  if Code <> 0 then
  begin
    ServiceStepFailed('register the Glance service', Code);   // for example 1072: still marked for deletion until a restart
    Exit;
  end;
  Sc('description GlanceService "Drives the Glance USB display and reads the hardware sensors. Themes are drawn by the signed-in user''s Glance apps."');
  // Windows' default rules plus "start" (RP) for interactive users, so the editor starts it without a prompt; stopping needs an administrator
  Code := Sc('sdset GlanceService D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)');
  if Code <> 0 then ServiceStepFailed('set the Glance service''s permissions', Code);
  // L7: its own service SID, and only the privileges it needs (LocalSystem would otherwise keep every privilege)
  Code := Sc('sidtype GlanceService unrestricted');
  if Code <> 0 then ServiceStepFailed('set the Glance service''s SID type', Code);
  Code := Sc('privs GlanceService SeChangeNotifyPrivilege/SeCreateGlobalPrivilege');
  if Code <> 0 then ServiceStepFailed('limit the Glance service''s privileges', Code);
  // restart after a crash: 5 s, 10 s, then every minute; the count resets after a day
  Code := Sc('failure GlanceService reset= 86400 actions= restart/5000/restart/10000/restart/60000');
  if Code <> 0 then ServiceStepFailed('set the Glance service''s recovery actions', Code);
  Code := Sc('start GlanceService');
  if (Code <> 0) and (Code <> ERROR_SERVICE_ALREADY_RUNNING) then ServiceStepFailed('start the Glance service', Code);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then InstallService();
end;

// Removes the service's log, but never through a link: %ProgramData%\Glance could be a junction a user planted (N6).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir: String;
  Attrs: Cardinal;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Dir := ExpandConstant('{commonappdata}\Glance');
  Attrs := GetFileAttributesW(Dir);
  if (Attrs = INVALID_FILE_ATTRIBUTES) or ((Attrs and FILE_ATTRIBUTE_REPARSE_POINT) <> 0) then Exit;
  DeleteFile(Dir + '\service.log');
  DeleteFile(Dir + '\service.old.log');
  RemoveDir(Dir);
end;

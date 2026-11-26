# Design: move the elevated work into a Windows service

Status: accepted for implementation on branch `claude/feature-runner-service-8fa395` (from `integration/high-fixes`).
Problem: review H6/H1 (`docs/reviews/review-autostart-task.md`, "Fixes after review", H1). The logon task starts the elevated
runner with the user's own environment, so `CORECLR_ENABLE_PROFILING`/`CORECLR_PROFILER_PATH`, `DOTNET_DiagnosticPorts` and other
`DOTNET_`/`COMPlus_` knobs that any normal program can write to `HKCU\Environment` load code into an administrator process before
`Main`. The runner cannot block them.

Goal: **no Glance process that runs with more rights than the signed-in user ever starts with that user's environment, and no
privileged process parses files the user controls.**

## 1. Summary of the decisions

| Topic | Decision |
|---|---|
| Split | New `Glance.Service` (LocalSystem, automatic start) owns the USB display, the sensors and shutdown/sleep blanking. The tray app `Glance.Runner` becomes `asInvoker` and renders the user's theme. |
| Who renders | **The user's own processes** (tray or editor). They stream finished frames to the service. The service never reads a theme, an image, a font or a video. |
| Per-user data | Stays in `%AppData%\Glance` of each user and is only ever read by that user's processes. Nothing moves to `%ProgramData%`. |
| Which user's screen | The client in the **active console session** (the person at the physical screen). Other sessions are refused. Nobody signed in: the display stays dark. |
| Pipes | `Glance.Service.Display` (frames and display commands) and `Glance.Sensors` (read-only sensor feed), created by the service at start with an explicit DACL; clients verify that the server is SYSTEM or Administrators; remote clients denied. The tray's own control pipe is per session and user-only. |
| `LIVE` with a folder | Goes from the editor to the user's tray (same user, same rights), never to the service. SYSTEM never touches a path a user named. |
| FFmpeg | Runs only in the user's processes, as the user. Never as SYSTEM. |
| Shutdown/sleep | The service handles `SERVICE_CONTROL_PRESHUTDOWN` (5 s pre-shutdown timeout), `SERVICE_CONTROL_POWEREVENT` and `SERVICE_CONTROL_SESSIONCHANGE` through `DisplayPowerPolicy`. The user side keeps its own `SessionPowerWatcher` as a second line. |
| Install | Inno Setup creates the service with `sc.exe` (no .NET code runs elevated during setup), sets recovery actions, description, the service DACL and the pre-shutdown timeout, then starts it. |
| Upgrade from 0.1.1 | Sends `EXIT` to the old elevated runner over its control pipe, waits, falls back to `taskkill`, deletes the old "Glance Runner" logon task. |
| Start/stop from the editor | Interactive users may **start** and query the service (service DACL). **Stopping** needs an administrator: one UAC prompt for Windows' own `sc.exe stop`. |
| Tray at sign-in | A shortcut in the common Startup folder (installer option, on by default). It is not elevated, so the user's environment is harmless. No scheduled task. |
| Portable zip | No service, and **no elevated mode any more**. The tray and editor drive the display directly over USB as the normal user, with the limited (non-admin) sensors. |
| `Glance.SensorHost` | Removed (an elevated process started with the user's environment, same problem as H1). |
| Old scheduled task code | The runner's `--install-autostart`/`--remove-autostart` modes are removed. The editor keeps a "remove the old auto-start task" button for PCs that still have one. |

## 2. Split

```
                    Session 0 (SYSTEM, environment from the Service Control Manager)
  Glance.Service ─ LocalSensorSource (LibreHardwareMonitor + PawnIO) ─► pipe "Glance.Sensors" (JSON line/s, read only)
                 └ DisplayServer ─ Ax206Display (USB) ◄─ pipe "Glance.Service.Display" (binary: HELLO, FRAME, BRIGHTNESS, BLANK, STATUS)
                 └ power/session handling: PRESHUTDOWN, POWEREVENT, SESSIONCHANGE -> DisplayPowerPolicy

                    User session N (normal user, asInvoker)
  Glance.Runner (tray) ─ reads %AppData%\Glance (theme, settings, assets), runs ffmpeg as the user
                       └ DisplayPusher ─ RemoteFrameDisplay ─► "Glance.Service.Display"   (or Ax206Display directly, portable)
                       └ SensorHub ◄─ "Glance.Sensors" (or local non-admin sensors)
                       └ control pipe "Glance.Runner.Control.<session>" ◄─ editor (LIVE, PAGE, PAUSE, ...)
  Glance.App (editor)  ─ the same, when no tray runs: its own DisplayPusher through the same connector
```

**Why the user side renders, not the service.** The task's first idea was a service that also owns the display loop. That needs the
theme (JSON) and its assets (images, GIFs, fonts, videos) inside the service, and every way to get them there makes SYSTEM parse
user-controlled input:

- reading the user's profile as SYSTEM opens path tricks (junctions, `..`, absolute asset paths such as `C:\Windows\...`),
  and runs Skia's image and font parsers as SYSTEM on files a user chose;
- sending a `.glance` package over the pipe is safer for paths (`ThemePackage` import is already defensive) but still feeds
  SYSTEM's Skia and, for video, ffmpeg with user files;
- `%ProgramData%` with tight ACLs either stops users from saving themes or makes it a shared writable folder again.

Streaming frames removes the whole class: the only data SYSTEM accepts is a fixed-size RGB565 frame (exactly 480×320×2 bytes),
one brightness byte and a few opcodes. The cost is small: the display takes ~2.5 frames/s, a frame is 300 KiB, so about
750 KiB/s over a local pipe, and the rendering CPU is spent in the user's process exactly as it is today in the editor.

It also reuses tested code instead of forking it: the user side keeps the full `DisplayPusher` (page rotation, pinning,
identical-frame skipping, forced refresh, render-error vs USB-error split, `ShouldReopen`). The pusher only sees another
`IFrameDisplay`, `RemoteFrameDisplay`, whose `Blit` is a request to the service that returns after the USB transfer, so the
timing and error reporting stay the same as with a local display.

**The service is a display and sensor server, nothing more.** It opens the USB device lazily on the first frame and releases it
again when idle (see 6), so AIDA64 can still use the display when no Glance client is streaming.

## 3. Session 0, per-user data, multi-user PCs

- **Themes and settings stay per user** in `%AppData%\Glance`. Only that user's tray and editor read them, with that user's rights.
  The service has no idea what a theme is.
- **Which user's theme shows:** the one rendered in the **active console session** (`WTSGetActiveConsoleSessionId`). The service
  looks up each client's session (`GetNamedPipeClientSessionId`) and refuses display commands from any other session with a clear
  reason ("another session is using the display"). That is the person in front of the physical screen; with fast user switching
  the new console user takes over at once, and the old session's tray gets refusals and waits.
- **Before anyone signs in, and after sign-out:** the display stays dark (as today: the runner only started at sign-in).
  When the driving session signs out or disconnects from the console, the service blanks at once instead of waiting for the idle rule.
- **RDP:** a remote session is not the console session, so it cannot drive the display. That is intended (the screen is attached
  to the console).
- **Security of reading user files as SYSTEM:** not applicable any more; the service never opens a user path. The only files the
  service writes are its log, in a folder it creates with a protected admin-only DACL and checks with `FolderTrust` before every
  start (see 9).

## 4. Pipe security

### Service pipes (`Glance.Service.Display`, `Glance.Sensors`)

- **Created by the service at start**, before anyone can sign in (automatic start). DACL, built in one tested place
  (`ServicePipeSecurity`):
  - Deny **NETWORK** (`S-1-5-2`) everything: remote clients are refused by the kernel before the service sees them.
  - Allow **the server's own account** (SYSTEM for the service) full control, which includes creating more instances.
  - Allow **Administrators** and **INTERACTIVE** (`S-1-5-4`) read/write + synchronize only: no `CreateNewInstance`,
    no `ChangePermissions`, no `TakeOwnership`, so no user can add an instance of the service's pipe or change its rules.
  - Nobody else (no Everyone, Users, Authenticated Users, Anonymous).
- **Authorization per request** (`DisplayAccess`, pure and tested): `STATUS` from any connected client; `HELLO`, `FRAME`,
  `BRIGHTNESS`, `BLANK` only from a client whose session is the active console session. A client whose session cannot be
  determined (for example a remote one that got past the ACL) is refused. The service has no `EXIT` or file command at all;
  stopping it goes through the Service Control Manager.
- **Squatting.** If the service is stopped, a normal user could create a pipe called `Glance.Service.Display` first. Clients
  therefore check **who owns the server end** right after connecting (`PipeServerCheck`): the owner must be SYSTEM or
  Administrators, which a non-elevated process cannot set. A squatter can only make the display unavailable (the service fails
  to create its pipe and logs it); it cannot receive frames or feed fake sensor values to a client.
- **Input limits.** The display protocol is binary with a 5-byte header (opcode + length); the length is checked against the
  opcode's exact size before anything is read (a frame must be exactly 307 200 bytes, brightness 1 byte, others 0-4 bytes), so a
  client cannot make the service allocate more than one frame per connection.

### The tray's control pipe (`Glance.Runner.Control.<session id>`)

- Served by the **non-elevated** tray, for the editor of the same user. Named per session so two signed-in users do not collide.
- DACL: the current user only (as today). Clients connect with `PipeOptions.CurrentUserOnly`, which makes .NET check that the
  server end belongs to the same user, so another user who squats the name gets no connection.
- `LIVE <folder> <theme>` keeps its meaning: the tray (same user, same rights) renders the theme from that folder. The folder is
  never sent to the service. This is no privilege boundary any more, so no path checks are needed beyond today's.

## 5. FFmpeg

FFmpeg runs only where the theme is rendered: in the tray or the editor, **as the user**, on the user's own media. The service never
starts a process. `Ffmpeg.Find`'s elevated rule (`FolderTrust.CheckExecutable(exe, TrustScope.FolderFiles)`) stays in Core for any
elevated caller, but after this change no Glance process renders while elevated, so the rule no longer triggers in normal use. The
"Install FFmpeg for all users" path still works (it is a normal machine-wide winget install); a per-user install is now enough too.

Rejected alternative: `WTSQueryUserToken` + `CreateProcessAsUser` from the service. It would keep a SYSTEM process in charge of
starting user-chosen binaries and parsing their output, needs a lot of untestable P/Invoke, and is unnecessary once rendering
happens on the user side.

## 6. Display ownership, idle and errors (service side)

- **Open lazily**, on the first `FRAME` or `BRIGHTNESS` from the console session, and apply the last brightness right after opening.
- **Release when idle:** clients refresh at least every 10 s (`DisplayPusher`'s forced refresh), so 20 s without a frame means the
  client is gone (crashed, paused, signed out without a clean exit). The service then blanks and closes the device. A client's
  explicit `BLANK` (sent by `DisplayPusher.Stop(blank: true)`, tray Pause, editor "Display off") blanks and closes at once.
- **USB errors:** a failed transfer is returned to the client as an error (its pusher shows it as `LastError` and reopens via
  `ShouldReopen`), and the service closes the device so the next frame opens it fresh (unplug/replug).
- One lock guards the device; requests are serialized. The power handler waits for that lock only within its budget, otherwise
  it marks the blank as owed and the next request (or the 2 s supervisor tick) pays it, as `RunnerContext` does today.

## 7. Shutdown, sleep, sign-out

The service registers its own control handler (`RegisterServiceCtrlHandlerEx`; .NET's `ServiceBase` does not offer pre-shutdown, and this avoids a new package) and accepts
`STOP | PRESHUTDOWN | POWEREVENT | SESSIONCHANGE`. A pure, tested mapping (`ServiceControlMap`) turns control codes into events:

| Control | Event | Action (via `DisplayPowerPolicy`) |
|---|---|---|
| `PRESHUTDOWN` (0x0F), `SHUTDOWN` (0x05) | `PowerEvent.SessionEnding` | blank once, stop, never reopen; report stop-pending with wait hints, then stopped |
| `STOP` (0x01) | `PowerEvent.Quit` | blank once, stop |
| `POWEREVENT` + `PBT_APMSUSPEND` | `PowerEvent.Suspend` | blank and hold, within 1.5 s, synchronously in the handler |
| `POWEREVENT` + `PBT_APMRESUMEAUTOMATIC`/`PBT_APMRESUMESUSPEND` | `PowerEvent.Resume` | allow frames again; the clients reopen |
| `SESSIONCHANGE` + `WTS_SESSION_LOGOFF` / `WTS_CONSOLE_DISCONNECT` | session left the console | blank now if it was the driving session |

- **Pre-shutdown timeout:** 5 000 ms, written by the installer to `HKLM\SYSTEM\CurrentControlSet\Services\GlanceService\PreshutdownTimeout`.
  The blank needs about 1 s; the bound inside is still `PowerBlank.SessionEndBudget` (4 s).
- **Heal a missed wake:** the supervisor tick calls `NoteAwakeTick`, as in the runner.
- **Second line:** the user-side tray and editor keep `SessionPowerWatcher` + `DisplayPowerPolicy`; on sign-out or sleep they stop
  their pusher with a blank, which reaches the service as `BLANK`. Either side alone blanks the display.
- **Fast Startup ("hybrid shutdown")** hibernates session 0 instead of stopping services. The user session is signed out first,
  which blanks through both lines (tray `WM_ENDSESSION` and the service's `WTS_SESSION_LOGOFF`). Needs a manual check.

## 8. Install, upgrade, uninstall

Service name `GlanceService`, display name "Glance display service", `start= auto`, LocalSystem, binary
`"{app}\Glance.Service.exe"` (Program Files, admin-only; quoted).

- **Install** (`installer/Glance.iss`, `[Run]`, all `runhidden`, before the post-install editor start):
  1. `sc.exe create GlanceService binPath= "\"{app}\Glance.Service.exe\"" start= auto DisplayName= "Glance display service"`
     (on upgrade the service exists already; `sc.exe config` with the same values is run as well).
  2. `sc.exe description ...`, `sc.exe failure GlanceService reset= 86400 actions= restart/5000/restart/10000/restart/60000`.
  3. `sc.exe sdset GlanceService <ServiceSecurity.Sddl>`: the Windows default plus **start** (`RP`) for interactive users.
  4. `PreshutdownTimeout` in the registry (`[Registry]`).
  5. `sc.exe start GlanceService`.
  Only Windows' own `sc.exe` runs elevated: no .NET program is started elevated with the installing user's environment during setup.
- **Upgrade from 0.1.1** (`PrepareToInstall`, before any file is replaced):
  1. Ask the old elevated runner to exit cleanly (it blanks the display): a hidden Windows PowerShell one-liner sends `EXIT` to
     `\\.\pipe\Glance.Runner.Control`, then wait up to 5 s for `Glance.Runner.exe` to end, then `taskkill /F` as a fallback.
  2. `schtasks /Delete /TN "Glance Runner" /F` (the old elevated logon task).
  3. `sc.exe stop GlanceService` (newer upgrades) and wait for it to stop; kill trays and editors of all sessions.
- **Uninstall** (`[UninstallRun]`): stop and delete the service, delete the old task, kill trays and editors.
  `%AppData%\Glance` stays (as today). `%ProgramData%\Glance` (the service log) is removed.
- **The editor's "Background service" window** becomes service status (installed? running? device state, driving session) with
  **Start** (no prompt, thanks to the service DACL) and **Stop** (one UAC prompt, `sc.exe stop GlanceService`; `sc.exe` is a
  System32 binary, so nothing of Glance's is elevated). It also shows whether this session's tray runs, starts it, and offers to
  remove a leftover "Glance Runner" task (one UAC prompt, `schtasks.exe`, as before).

Why stop needs admin: any interactive user stopping the service would turn off the display and sensors for whoever is at the
console. Starting it is harmless. Releasing the display for AIDA64 does not need a stop: pausing the tray releases the device (6).

## 9. Service log

`%ProgramData%\Glance\service.log`. A normal user can create folders in `%ProgramData%`, so a user could pre-create
`%ProgramData%\Glance` (or a junction there) and make SYSTEM write into a file of their choice. The service therefore creates the
folder itself with a protected DACL (SYSTEM and Administrators full control, Users read) and, whether it created it or not, only
writes there when `FolderTrust.CheckFolder` says only administrators can change it. Otherwise it does not write a log file, and
`STATUS` says why. The tray keeps writing its own `%AppData%\Glance\runner.log` as the user.

## 10. Sensors without the editor; no service installed

- With the service installed and running, sensors are read all the time by SYSTEM and served to any interactive client: the tray
  renders with full sensors, and the editor shows them too, with or without the tray.
- Without the service (portable zip, or service stopped), the tray and the editor fall back to the local non-admin sensors
  (GPU, drives, memory, network; no CPU temperature) and to opening the USB display directly as the user (no admin needed for
  libusb-win32). The tray's menu and tooltip say "Service not running: limited sensors".
- **Portable elevated mode is dropped.** Keeping an opt-in elevated runner would keep the H1 hole for exactly the people least
  likely to know about it. Portable users who want full sensors install Glance with the installer.

## 11. What changes in code

| Where | Change |
|---|---|
| `Core/Service/` (new) | `ServicePipeSecurity` (DACLs), `PipeServerCheck` (owner check), `DisplayAccess` (authorization), `DisplayWire` (binary protocol), `DisplayServer` (service side), `RemoteFrameDisplay` (client `IFrameDisplay`), `DisplayConnector` (service first, USB fallback), `ServiceControlMap` (control codes to events), `ServiceSecurity` (service SDDL and names) |
| `Core/Sensors` | `SensorPipeServer` takes a `PipeSecurity`; `PipeSensorClient` verifies the server owner; `SensorHub` moves from the editor to Core (tray and editor share it) |
| `Core/Control` | per-session control pipe name; `ControlPipeClient` uses `CurrentUserOnly` |
| `Glance.Service` (new) | native service entry (`StartServiceCtrlDispatcher`, `RegisterServiceCtrlHandlerEx`), wiring, `--console` mode for development |
| `Glance.Runner` | `asInvoker`; no sensor pipe, no autostart modes; renders through the connector; menu shows service state |
| `Glance.App` | Background service window rewritten; sensor helper button removed; display through the connector |
| `Glance.SensorHost` | removed |
| installer, `build/publish.ps1` | service install/upgrade/uninstall; publish `Glance.Service` |

## 12. Decisions for the user

1. **Portable elevated runner dropped** (10). Alternative: keep it behind an explicit "run elevated" option with the H1 warning.
2. **Display dark before sign-in** (3). Alternative: an administrator publishes one theme to an admin-only `%ProgramData%` folder
   that the service renders before sign-in; that brings rendering (Skia) back into SYSTEM, for admin-controlled files only.
3. **Interactive users may start the service but not stop it** (8). Alternative: also grant stop (`WP`), which removes the UAC
   prompt but lets any signed-in user switch off the display and sensors for the console user.
4. **Tray starts at sign-in for every user** through the common Startup folder, on by default (installer checkbox). Users can turn
   it off in Task Manager's Startup apps.
5. **The user side renders** (2), so the display shows a user's theme only while their tray or editor runs.
6. **Console session wins** (3). Alternative: the first client keeps the display until it lets go.

## 13. Proof

Filled in during implementation: for every behaviour, the failing test output first, then the passing output.

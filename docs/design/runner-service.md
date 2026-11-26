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
- **No impersonation (added during implementation).** .NET pipe clients default to the *Impersonation* level, so whoever serves a
  pipe name can call `ImpersonateNamedPipeClient` and act as the connecting user, before any owner check runs. A user who squats
  `Glance.Service.Display` while the service is stopped could then act as another signed-in user. All Glance clients (display,
  sensors, tray control) therefore connect at `TokenImpersonationLevel.Anonymous`. The service does not need the client's token:
  it takes the session from `GetNamedPipeClientSessionId`. Proven with two tests that play the squatter (see Proof).
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
elevated caller, but after this change no Glance process renders while elevated, so the rule no longer triggers in normal use.
The editor's FFmpeg button and the setup check now install FFmpeg **per user** (no UAC prompt), and the FFmpeg row is Ok for any
FFmpeg this user can find (`ServiceChecks.DiagnoseFfmpeg`).

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

- **Install** (`installer/Glance.iss`, `[Code]` at `ssPostInstall`, every tool hidden):
  1. `sc.exe create GlanceService binPath= "\"{app}\Glance.Service.exe\"" start= auto obj= LocalSystem DisplayName= "Glance display service"`
     (on upgrade the service exists already, so `sc.exe config` with the same values runs instead).
  2. `sc.exe description ...`, `sc.exe failure GlanceService reset= 86400 actions= restart/5000/restart/10000/restart/60000`.
  3. `sc.exe sdset GlanceService <ServiceNames.ServiceSddl>`: the Windows default plus **start** (`RP`) for interactive users.
  4. `PreshutdownTimeout` = 5000 in the service's registry key (`RegWriteDWordValue`, after the service exists).
  5. `sc.exe start GlanceService`.
  6. The tray app starts as the original (non-elevated) user, and a shortcut in the common Startup folder starts it at every sign-in
     (task "trayatsignin", on by default).
  Only Windows tools run elevated (`sc.exe`, `net.exe`, `schtasks.exe`, `taskkill.exe`, `cmd.exe /d`): no .NET program is started
  elevated with the installing user's environment during setup. `ServiceLayoutTests` checks that the script uses the tested SDDL.
- **Upgrade from 0.1.1** (`PrepareToInstall`, before any file is replaced):
  1. Ask the old elevated runner to exit cleanly (it blanks the display): the setup script itself opens
     `\\.\pipe\Glance.Runner.Control` with `CreateFileW` (kernel32, `SECURITY_SQOS_PRESENT` + anonymous, so whoever serves that pipe can
     never act as the elevated installer) and writes `EXIT`, then waits up to 5 s for `Glance.Runner.exe` to end.
     *Changed from the first draft:* the draft used a PowerShell one-liner. PowerShell is a .NET Framework program, and
     `COR_ENABLE_PROFILING`/`COR_PROFILER_PATH` from the user's environment would load a profiler into it while elevated: the H1 hole again.
  2. `schtasks /Delete /TN "Glance Runner" /F` (the old elevated logon task).
  3. `net.exe stop GlanceService` (newer upgrades; it waits until the service has stopped and blanked), then `taskkill /F` for trays,
     editors and the service in every session.
- **Uninstall** (`[UninstallRun]`): `net stop` and `sc delete` the service, delete the old task, kill trays and editors.
  `%AppData%\Glance` stays (as today). Only `%ProgramData%\Glance\service.log` and then the folder if empty are removed, never a
  folder tree (the folder could be a link planted by a user).
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
  libusb-win32). The tray's menu shows "Glance service: not installed (basic sensors, display over USB)" or "not running ...".
- **Portable elevated mode is dropped.** Keeping an opt-in elevated runner would keep the H1 hole for exactly the people least
  likely to know about it. Portable users who want full sensors install Glance with the installer.

## 11. What changes in code

| Where | Change |
|---|---|
| `Core/Service/` (new) | `ServicePipeSecurity` (DACLs), `PipeServerCheck` (owner check), `DisplayAccess` (authorization), `DisplayWire` (binary protocol), `DisplayServer` (service side), `RemoteFrameDisplay` (client `IFrameDisplay`), `DisplayConnector` (service first, USB fallback), `ServiceControlMap` (control codes to events), `ServiceNames` (names, service SDDL and its check), `ServiceControl` (query/start without elevation), `NativeSessions`, `ServiceLog` |
| `Core/Setup` | `ServiceChecks` (service, sensors and FFmpeg rows of the setup check); `SetupContext` carries the service state and whether the tray runs |
| `Core/Sensors` | `SensorPipeServer` takes a `PipeSecurity`; `PipeSensorClient` verifies the server owner and connects anonymously; `SensorHub` moves from the editor to Core (tray and editor share it) |
| `Core/Control` | per-session control pipe name; `ControlPipeClient` uses `CurrentUserOnly` and the anonymous level; `LiveSendGate` texts say "tray app" |
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
7. **Version bumped to 0.2.0** in `src/Directory.Build.props`, so the installer is `Glance-Setup-0.2.0.exe` and upgrades 0.1.1.
8. **FFmpeg is installed per user** from the editor now (5). The machine-wide script is kept in Core but no longer offered.
9. **Not removed yet:** the 0.1 install-path helpers in `AutostartTask` and `RunnerEnvironment` (unused, with their tests). Left for a
   cleanup so this change stays reviewable; only the task-removal parts are still used.

## 13. Proof

Method: every new rule got its test first, against a stub with today's behaviour (or `NotImplementedException` for new types),
the run was kept, then the code was written and the same tests were run again. Logs are kept outside the repo. Commands:
`dotnet build src/Glance.sln -c Release -p:SkipHost=true` (0 errors; the only warnings are the existing `SKPath` obsolete warnings) and
`dotnet test src/Glance.Tests -c Release`.

**Baseline** (`integration/high-fixes`, 9bf03b0): 542 / 542 passed.

### Service rules, display server and client (commit d3bf668)

59 new tests in `RunnerServiceTests.cs` (`ServiceRulesTests`), `DisplayServiceTests.cs` and `ServiceLayoutTests.cs`.

- **Before: 53 failed, 6 passed.** The 6 that passed are guards that must hold either way: STATUS is open to any client, four
  "other control codes do nothing" cases, and Windows reporting a local client's session. Examples of the failures:
  - `Only_system_or_administrators_count_as_the_service`: `Assert.False() Failure` (any pipe owner was trusted).
  - `The_sensor_client_ignores_a_feed_that_is_not_served_by_the_service`: `Assert.False() Failure` (a squatter's values were shown).
  - `No_Glance_program_asks_for_administrator_rights`: `Assert.DoesNotContain() Failure: Sub-string found` (`requireAdministrator`
    in `Glance.Runner` and `Glance.SensorHost`).
  - `Only_the_console_session_may_drive_the_display`, `Control_codes_become_power_events`, `Interactive_users_may_start_the_service...`:
    assertion failures against the permissive stubs; `DisplayWire`/`DisplayServer`/`RemoteFrameDisplay` tests: `NotImplementedException`.
- **After: 54 / 54** for the rules and the server (one test helper was fixed in between: it waited 5 s for a status connection to
  close, which a status query never does). The 5 layout tests stayed red until the tray and installer commits (below).
- Existing tests adjusted on purpose: the two sensor-pipe tests in `ImporterAndRendererTests.cs` now pass the test user as the trusted
  owner (their server runs as that user, not as the service); `H1_elevated_programs_turn_startup_hooks_off` checks `Glance.Service`
  instead of the removed `Glance.SensorHost`.

### Setup check rows and texts (commit 0cdd8b3)

20 new tests in `ServiceChecksTests.cs`.

- **Before: 19 failed, 1 passed** (the passing one: an unknown service state code maps to Unknown, which the stub returned anyway).
  For example `Nothing_asks_for_administrator_rights_or_mentions_the_old_runner`: `Assert.DoesNotContain() Failure: Sub-string found`
  ("administrator rights"); `FFmpeg_installed_for_the_user_alone_is_enough_now`: `Assert.Equal() Failure` (Warning instead of Ok).
- **After: 20 / 20.**
- `A_rejection_suggests_restarting_the_runner_after_an_update` was changed to expect "restart the Glance tray app":
  before the text change `Assert.Contains() Failure: Sub-string not found`, after it passes.
  `Connected_with_the_runner_using_it_is_ok_and_says_so` was changed to the new wording "the service or the tray app".

### No impersonation by a pipe server (commit 0c7de7d)

2 new tests act as a squatter: they serve the pipe name, read what the client sends, then call `RunAsClient`.

- **Before: 2 failed.** Both `A_squatter_on_the_display_pipe_cannot_act_as_the_user` and
  `A_squatter_on_the_tray_control_pipe_cannot_act_as_the_user`: `Assert.NotEqual() Failure: Strings are equal`,
  expected not `S-1-5-21-…-1001`, actual `S-1-5-21-…-1001` (the squatter acted as the test user). The control-pipe case existed
  before this branch.
- **After: 2 / 2**, and `Windows_reports_the_session_of_a_local_pipe_client` now connects anonymously too and still passes, so the
  service's session check works with anonymous clients.

### Installer, packaging and layout (commit c94557d)

- **Before** (at d3bf668): `No_Glance_program_asks_for_administrator_rights`, `The_elevated_helpers_are_gone_and_the_service_is_built`
  (publish script), `The_installer_creates_the_service_with_the_tested_rules` and `The_installer_retires_the_old_elevated_runner`
  failed. (`The_service_does_not_load_startup_hooks` passed once the project existed.)
- **After: all 5 pass; full suite 623 / 623.**
- The installer script was compiled with Inno Setup 6 (`ISCC`) against a placeholder package folder: "Successful compile". The resulting
  setup was deleted and never run; nothing was installed.

### Guard: the service log folder (docs commit)

`An_admin_only_folder_in_ProgramData_is_trusted_with_the_real_parent_permissions` reads this PC's real `C:\ProgramData` and `C:\`
permissions and checks that an admin-only `%ProgramData%\Glance` is trusted while a user-writable one is not. It passed at once
(it guards the design assumption; it is not a bug proof).

### Mutation check

Each rule was broken on purpose and the service tests (82 at the time) were run:

| Mutation | Caught by |
|---|---|
| M1 another session may draw | 6 tests (access rule, connector, refusal) |
| M2 no NETWORK deny on the pipes | 2 (pipe rules) |
| M3 any pipe owner trusted | 4 (squatted display pipe, connector, sensor feed, owner rule) |
| M4 display client allows impersonation | 1 (squatter on the display pipe) |
| M5 power event does not blank | 3 (sleep, shutdown, service stop) |
| M6 an idle client is never blanked | 1 |
| M7 PRESHUTDOWN ignored | 1 |
| M8 frame length not checked | 2 (wire format, server) |
| M9 frames drawn while asleep | 2 |
| M10 sign-out of the drawing session ignored | 1 |
| M11 interactive users may stop the service | 2 (SDDL rule, installer text) |
| M12 log written into a user-writable folder | 1 |

All 12 were caught. Pitfall seen: the script restored each file with its older time stamp, so the incremental build after the
last mutation kept the mutated DLL and one test failed until the sources were touched and rebuilt. (The mutation results are not
affected: every mutation itself made its file newer than the last build.) After a clean rebuild the suite is green again.

### Final

**624 / 624** (542 before this branch + 82 new). Build of the whole solution: 0 errors (4 existing `SKPath` obsolete warnings).

### Not verified here (needs the user)

Nothing was installed, started, stopped or registered on this PC, nothing ran elevated, and the real display and the running runner
were not touched. So these are unverified: the service entry (`NativeService`) under the real Service Control Manager, its start
before sign-in, PRESHUTDOWN/POWEREVENT/SESSIONCHANGE delivery and timing, LibreHardwareMonitor and USB inside session 0, the installer's
`sc.exe` steps and upgrade path, the tray app at sign-in, and every visual result on the display. See the manual test plan.

## 14. Manual test plan

Use a PC with the display attached, the libusb-win32 driver and PawnIO. Start with Glance 0.1.1 installed and its "start at sign-in" option on.

1. **Build the installer:** `.\build\publish.ps1`, then `.\build\make-installer.ps1` → `dist\Glance-Setup-0.2.0.exe`.
2. **Upgrade from 0.1.1:** with the 0.1.1 runner showing your theme, run the setup.
   - Expect: the display goes black when setup starts (the old runner got `EXIT`), no "Glance Runner" task afterwards
     (`schtasks /Query /TN "Glance Runner"` → not found), `Glance.Runner.exe` running as you **without** elevation (Task Manager >
     Details > "Elevated" column = No), your theme back on the display within a few seconds, full CPU sensors in the editor.
   - `sc qc GlanceService`: `START_TYPE AUTO_START`, `SERVICE_START_NAME LocalSystem`, `BINARY_PATH_NAME` in quotes.
     `sc qfailure GlanceService`: three restart actions. `sc sdshow GlanceService`: contains `(A;;CCLCSWRPLOCRRC;;;IU)`.
     `reg query HKLM\SYSTEM\CurrentControlSet\Services\GlanceService /v PreshutdownTimeout` → `0x1388`.
   - `%ProgramData%\Glance\service.log` exists, starts with "Glance service ... starting as NT AUTHORITY\SYSTEM", "Display pipe ready",
     "Sensors ready (n)". Its folder: owner SYSTEM or Administrators, Users read only.
3. **Fresh install** (on a PC or VM without Glance): same checks; the display stays dark until you sign in.
4. **Before sign-in:** restart the PC and wait at the sign-in screen. Expect: the display is dark; `service.log` shows the service started
   (after sign-in, check the time stamps). Sign in: the tray app starts and your theme appears.
5. **Sensors without the editor:** close the editor. The tray menu says "Glance service: running, showing session N ...", the theme
   shows CPU temperature and fans. Open the editor: the bottom bar says "Sensors: Glance service (n)".
6. **Editor:** Send to display while the tray app runs → live edits appear (through the tray app). Exit the tray app (Background
   service > Stop the tray app) and Send again → the editor draws through the service directly. Display off → black.
7. **Background service window:** Stop the service → one UAC prompt for Windows' `sc.exe`, display goes black, sensors
   drop to basic. Start the service → no prompt, sensors come back. Setup check rows say the same.
8. **AIDA64:** pause the tray app (tray menu > Pause display) and stop sending in the editor; after about 20 s AIDA64 can open the display.
9. **Sleep:** with the theme showing, Start > Power > Sleep. Expect black before the PC sleeps; after wake the theme returns within a few
   seconds. `service.log`: "Windows Suspend: BlankAndHold", "Windows Resume: Reopen".
10. **Shutdown and restart:** with the theme showing, shut down (and separately, restart). Expect the display black before the PC turns
    off. `service.log` after the next start: "Glance service stopping (SessionEnding)", "Display: Blanked". Try once with Fast Startup on
    (default) and once off.
11. **Sign-out and fast user switching:** sign out → display black ("Session N left the console"). With two accounts, switch user:
    the second user's tray app takes over; the first user's tray menu says "Another user is at the console".
12. **Squatting (optional, as a normal user):** `sc stop GlanceService` (admin), then as a normal user run a small program that creates a
    pipe named `Glance.Service.Display`; the tray app must not send it frames (its status says it uses USB directly or fails), and
    `sc start GlanceService` logs that the display pipe could not be created and keeps retrying.
13. **Uninstall:** Settings > Apps > Glance > Uninstall. Expect: display black, `sc query GlanceService` → 1060 (does not exist), no tray
    app, no Startup shortcut, `%ProgramData%\Glance` gone (or only other files left), `%AppData%\Glance` kept.

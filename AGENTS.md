# AGENTS.md

Guidance for AI coding agents (and humans) working on **Glance**: a Windows app that designs and runs live hardware
screens on small USB LCDs (AX206-style, USB ID `1908:0102`, 480x320). It replaces AIDA64's LCD customizer.

Read this first, then `README.md` (user-facing) and `docs/PROTOCOL.md` (the display's USB protocol).

## Ground rules

- **This runs on a real person's PC with real hardware.** The Glance service drives their display and reads their sensors.
  Do not install software, push to GitHub, publish releases, create scheduled tasks or change system settings
  without being asked. Installs go through `winget` and only on an explicit yes. Never install, start or stop the Glance service
  (or any service or scheduled task) on this PC unless asked.
- **Keep personal and third-party files out of git.** `Themes/` (the owner's own theme and artwork) and
  `Driver isntaller/` (a third-party binary, folder name is misspelled on purpose, it is the real name) are ignored by
  anchored rules in `.gitignore`. Never loosen those to unanchored patterns: `Themes/` would also hide `src/Glance.Core/Themes/`.
- **Ask before anything outward-facing** (push, release, upload). Local commits are fine when the work is verified.
- Report what you actually verified. Several things here can only be judged by looking at the physical display;
  say when you could not look.

## Repo map

| Path | What it is |
|---|---|
| `src/Glance.Core` | Everything that is not UI: USB driver, renderer, theme model, sensors, presets, importer, setup checks |
| `src/Glance.App` | Avalonia 12 editor (code-built UI, no XAML beyond `App.axaml`) |
| `src/Glance.Runner` | WinForms tray app, normal user rights: renders the saved theme and sends frames to the service |
| `src/Glance.Service` | Windows service (LocalSystem): owns the USB display, reads the sensors, blanks on shutdown/sleep |
| `src/Glance.Tests` | xUnit tests for `Glance.Core` |
| `src/Glance.Demo`, `src/UsbProbe` | Small diagnostic tools, not shipped |
| `build/` | `publish.ps1` (package + zip), `make-installer.ps1` (Inno Setup) |
| `installer/` | Inno Setup script and the "before you start" text |
| `assets/` | App icon (`glance.ico`, generated from code) |
| `docs/PROTOCOL.md` | Reverse-engineered display protocol and measured limits |

Key files: `Core/Themes/{Theme,ThemePage,Widget,ThemeRenderer,ThemePackage}.cs`, `Core/Display/DisplayPusher.cs`,
`Core/Usb/Ax206Display.cs`, `Core/Presets/{WidgetPresets,ThemeTemplates}.cs`, `Core/Import/AidaImporter.cs`,
`Core/Setup/SetupChecks.cs`, `App/MainWindow.cs` (large, the editor's brain), `App/Views/PropertiesPanel.cs`,
`Runner/RunnerContext.cs`, `Core/Service/{DisplayServer,RemoteFrameDisplay,DisplayConnector,DisplayAccess,ServicePipeSecurity}.cs`,
`Service/NativeService.cs`.

## Build, run, package (PowerShell on Windows)

`dotnet` and `git` are installed but may not be on PATH in a fresh shell. Use:

```powershell
$env:Path += ";C:\Program Files\dotnet;C:\Program Files\Git\cmd"
cd src
dotnet build Glance.sln -c Release
```

- Editor: `src\Glance.App\bin\Release\net8.0\Glance.App.exe`. Tray app: `src\Glance.Runner\bin\Release\net8.0-windows\Glance.Runner.exe`.
  Without an installed service both drive the display over USB directly, with the basic sensors. Only the installer installs the service.
- Package: `.\build\publish.ps1` (self-contained, ~215 MB folder in `dist\Glance`, ~90 MB zip; `-FrameworkDependent` is small).
  Installer: `.\build\make-installer.ps1` (needs Inno Setup 6). `dist/` is git-ignored.
- Version lives in `src/Directory.Build.props` only.
- Handy start options for the editor: `--setup`, `--new`, `--gallery` open those windows immediately.

### Things that break builds
- **A running tray app or editor locks its own output files**: exit it first (the tray app answers `EXIT` on its control pipe).
  An installed Glance service runs from Program Files, not from `bin`, so it does not block builds; but an old elevated 0.1 runner
  that still runs cannot be killed from a normal shell ("Access is denied"). `publish.ps1` fails the same way if anything runs from
  `dist\Glance`. (`-p:SkipHost=true` is still accepted but no longer changes anything.)
- After any change under `Glance.Core`, a running package in `dist\` is stale until you re-publish and restart.

## Architecture

Three processes cooperate; all reference `Glance.Core`. Design and reasons: `docs/design/runner-service.md`.

```
Glance.Service (LocalSystem, Windows service, session 0; environment from the Service Control Manager)
  LocalSensorSource -> SensorPipeServer  --pipe "Glance.Sensors" (1 JSON line/s, read only)----> PipeSensorClient -> SensorHub
  DisplayServer -> Ax206Display (USB)    <--pipe "Glance.Service.Display" (binary DisplayWire)--- RemoteFrameDisplay <- DisplayPusher
  power/session: PRESHUTDOWN, POWEREVENT, SESSIONCHANGE -> ServiceControlMap -> DisplayPowerPolicy

Glance.Runner (tray, normal user, one per session)          Glance.App (editor, normal user)
  reads %AppData%\Glance, renders the theme (DisplayPusher)    RunnerClient --pipe "Glance.Runner.Control.<session>.<sid>"--> tray
  serves the control pipe for this session's editor           draws itself through DisplayConnector when no tray runs
```

- **The service never renders.** It never reads a theme, an image, a font or a video, and never starts a process. The user's tray app or
  editor renders with `DisplayPusher` and sends finished 480x320 RGB565 frames; `RemoteFrameDisplay` is just another `IFrameDisplay`.
  `DisplayConnector.Open()` (used by `DisplayPusher`'s public constructor) picks the service, or USB directly when there is no service.
  Keep it that way: anything that makes SYSTEM parse user files brings back the class of bug the service exists to remove.
- **Who may draw:** only a client in the active console session (`DisplayAccess`, per request; session from `GetNamedPipeClientSessionId`).
  Clients that came through the network redirector (also `\localhost`) are closed before a byte is read (`NativeSessions.IsRemoteClient`).
  `STATUS` is open to any connected client, but other sessions only learn "display in use/released" and the power state. There is no
  `EXIT` or file command on the service pipe; stopping goes through the SCM.
- **Pipe rules** (`ServicePipeSecurity`, tested): deny NETWORK; the server account full control; Administrators and INTERACTIVE read/write
  only (no `CreateNewInstance`, `ChangePermissions`, `TakeOwnership`). Every server (display, sensors, tray control) creates its pipes
  through `ServicePipeFactory`: the first instance with `FirstPipeInstance` (a squatter's name is refused, never joined) and every instance
  checked to be owned by this account. Clients check the server end's owner (`PipeServerCheck`: SYSTEM or Administrators, so a squatter is
  never sent a frame or believed) and connect at `TokenImpersonationLevel.Anonymous` so no server can act as them. The tray's control pipe
  is `Glance.Runner.Control.<session>.<user SID>`, current-user only, NETWORK denied; its client uses `ControlPipeClient.Options`
  (`CurrentUserOnly`) + Anonymous.
- **Display server behaviour** (`DisplayServer`, tested over real pipes with `FakeDisplay`): opens the device on the first frame and applies
  the last brightness; `BLANK` blanks and releases at once; 20 s without a frame blanks and releases (clients refresh every 10 s), so
  AIDA64 can take the display when no Glance client draws; a USB error is returned to the client and closes the device; sign-out or
  console disconnect of the drawing session blanks at once; a power event that finds the device busy is paid by the transfer holding it,
  right after it ends. At most 4 connections per session and 16 in all; a connection silent for 30 s is closed. Repeated log lines go
  through `LogLimiter` (once a minute).
- **Connector rule** (`DisplayConnector`, tested): the service when it answers; USB directly only when the service is not installed or is
  stopped. A running or starting service that does not answer yet is retried, never bypassed, and the tray hands a USB display back to
  the service once it answers (`DisplayPusher.ViaService`).
- **Sensors:** LibreHardwareMonitor (needs admin + the PawnIO driver for CPU/motherboard data) runs in the service. The sensor pipe is
  created first and serves `[]` until it is ready. `SensorHub` (Core) in the tray and the editor prefers the service's feed and falls back
  to basic local sensors; the feed counts as gone after 5 s without data. Drive-space sensors come from `DriveSensors`.
- **Tray control pipe commands** (one line each, reply `OK ...`): `STATUS`, `RELOAD`, `PAUSE`, `RESUME`, `EXIT`, `NEXT`, `PREV`, `PAGE <n>`
  (0 releases), `BRIGHTNESS <0-7>`, `LIVE <base64 folder> <base64 theme json>` (show the editor's unsaved theme; `-` for no folder),
  `LIVE_CLEAR`. Build and parse them only with `Glance.Core.Control.ControlCommand` (shared by both sides and tested).
- Single tray app per session (mutex `Local\Glance.Runner`). It starts at sign-in from a shortcut in the common Startup folder (installer
  option). There is no scheduled task any more. A leftover Glance 0.1 "Glance Runner" task would start the tray elevated: the tray then
  relaunches itself with normal rights or refuses (`TrayStart`, tested), the service deletes the task at every start (`LegacyTask`), the
  installer deletes and checks it, and the editor's Background service window offers to remove it.
- **Service control:** `ServiceControl` (Core) queries and starts the service without elevation (the installer's service DACL,
  `ServiceNames.ServiceSddl`, gives interactive users "start"); stopping runs Windows' `sc.exe stop GlanceService` through one UAC prompt.
- **Service entry:** `Glance.Service/NativeService.cs` uses `StartServiceCtrlDispatcher`/`RegisterServiceCtrlHandlerEx` directly because
  `ServiceBase` cannot accept pre-shutdown. `Main` first calls `SetDefaultDllDirectories(System32 | app dir)`; `ServiceMain` then opens the
  log, checks its own folder (`ServiceStartGuard` + `FolderTrust.CheckExecutable(..., FolderTree)`) and refuses to run from a folder anyone
  else can change. Power events are handled synchronously in the handler (bounded); stop and pre-shutdown report stop-pending, blank on a
  worker and report stopped. It sets its pre-shutdown timeout with `ChangeServiceConfig2W` at start. `--console` exists in debug builds only.
- **Service privileges:** `sc sidtype unrestricted` and only `ServiceNames.RequiredPrivileges` (`SeChangeNotifyPrivilege`, `SeCreateGlobalPrivilege`).
  If a sensor needs more, add it there and in the installer, and say which sensor.
- **Install folder:** always Program Files (`DisableDirPage`), and the installer resets its rules with `icacls` (SYSTEM/Administrators
  change, users read and run) before it registers the service.
- **Service log:** `%ProgramData%\Glance\service.log` (rolls to `service.old.log` at 1 MiB), opened only by the service itself and only
  written when `FolderTrust.CheckFolder` says the folder is admin-only (the service creates it with a protected DACL); otherwise the reason
  goes to the Application event log (source "Glance"). The tray app logs to `%AppData%\Glance\runner.log` as the user.
- **Native DLLs:** `Glance.Core` imports by name from System32 only (`[assembly: DefaultDllImportSearchPaths(System32)]`; `libusb0.dll` lives there).
- **Sibling programs:** `DevPaths.FindSibling` finds the editor or tray next to the running exe; only a development build (running from
  `\bin\`) also searches the repository's build output, never above the folder with `Glance.sln`.
- **Trust checks:** `Glance.Core.Security.FolderTrust` is the one engine for "may an elevated process start or trust this?"
  (owner and ACL allow-list, no links, fixed local drive, complete chain to the drive root). Use it, do not add another.
- **No Glance process runs elevated with a user's environment** (review H6/H1). Nothing has `requireAdministrator` (a test checks every
  manifest); the installer only starts Windows tools while elevated. `StartupHookSupport` stays off for the service and the tray app.
- **Settings and themes:** `%AppData%\Glance` (`settings.json`, `Themes\<name>\theme.json` + `assets\`), read only by that user's processes.
  On first use, the old `%AppData%\MiniLcd` folder is copied over (the app used to be called MiniLcd).

## The display (read `docs/PROTOCOL.md`)

- Bulk transfer over libusb-win32 (`libusb0.dll`, driver installed with Zadig). Frames are **RGB565 big-endian**, 480x320.
- **Only whole-screen updates work.** Partial rectangles time out and draw nothing (measured on the real display). A full frame takes ~400 ms, so ~2.5 updates/s max.
  `DisplayPusher` therefore skips frames identical to the last one and force-refreshes every 10 s. Do not build features that assume faster updates.
- On exit/pause the display is blanked (black frame, backlight 0), otherwise it keeps its last picture. Reload/error recovery deliberately does not blank (flicker).
- **Shutdown, restart, sign-out and sleep** also blank it (the USB port often stays powered, so the last frame would stay lit), on two lines:
  the **service** (pre-shutdown, power event and session change, through `ServiceControlMap` -> `DisplayServer.HandlePower`/`SessionLeft`,
  bounded by `PowerBlank.OwnerBudgetFor`; the pre-shutdown timeout is 5 s, set by the installer) and the **user side** (tray app or editor),
  whose pusher blanks through the service on its way out. On the user side, `SessionPowerWatcher`
  (Core) runs a hidden top-level window on its own thread and turns `WM_ENDSESSION`/`WM_POWERBROADCAST` into `PowerEvent`s; the handler runs
  before the message is answered (Windows waits for it) but never longer than `PowerBlank.BudgetFor` (4 s session end, 1.5 s sleep), with a
  shutdown block reason set during a session end and always removed. `DisplayPowerPolicy` (pure, tested) decides: session end/quit -> blank once,
  stop and never reopen (also while asleep, so an installer can close the tray app); suspend -> blank and hold; resume -> reopen (the tray app's
  `Supervise` does it, the editor retries for about a minute). Owners update the policy first, then blank with `PowerBlank.Run`: a bounded
  lock wait, then the loop's own `DisplayPusher.Stop(true, timeout)` with what is left of `OwnerBudgetFor(e)`. If that returns false the
  pusher is remembered as still closing and the display is not reopened until `IsClosed`. If the owner's lock is busy the owner pays the blank itself.
  Every open checks `MayDrive` before and right after opening; that includes the restart of a dead loop. The editor keeps all of this in
  `PusherSlot` (Core, tested with `FakeDisplay`); the tray app's `Supervise` checks the policy before its `ShouldReopen` restart.
  A missed wake notice heals after 30 s awake (`NoteAwakeTick`), or at once on tray Resume/Reload or the `RESUME` command.
  In the editor, Avalonia closes the window on an OS shutdown, so `Closing` usually does the session-end blank (off the UI thread;
  `Program.Main` waits for it); the watcher covers sleep and is the backup. Out of scope: Modern Standby timing (`PBT_APMSUSPEND` can come late),
  monitor off and lock (no `GUID_CONSOLE_DISPLAY_STATE` handling yet). Only verifiable by hand: shut down / sleep with the display attached and look.
- While a `DisplayPusher` runs, only its loop touches the device. `Stop(blank, timeout)` asks the loop to blank (skipped if the last
  transfer failed) and close; it returns false if a transfer is still stuck, and `IsClosed` turns true when the loop lets go.
  Use `DisplayPusher.QuickStopTimeout` (2 s) for sleep/shutdown. `LastError` is USB trouble (owners reopen via `DisplayPusher.ShouldReopen`);
  `LastRenderError` is a theme that fails to draw (shown, never a reason to reopen). Tests drive it with `FakeDisplay` and short `PusherTiming`.
- AIDA64 uses the same device; only one program can hold it. The service lets go of it 20 s after the last frame (tray paused, editor not sending).
- Pages on the display: `DisplayPusher.NextPageIndex` is the pure rotation logic (shared with the virtual display window).

## Core concepts

- **Theme -> Pages -> Widgets.** `Widget` is one flat type (`Kind` enum + properties) so JSON and the property editor stay simple.
  `Theme.Widgets` is an alias for the *active editor page's* widgets. Old single-page theme files are migrated in `Theme.Normalize`.
- **JSON compatibility matters:** users have saved themes. New `Widget`/`Theme` properties need sensible defaults, and enums serialize as strings.
- **`ThemeRenderer`** draws one page to a Skia canvas; the same renderer serves the editor preview, the real display and the virtual display.
  It keeps per-instance caches (images, video processes, graph history), so create one per consumer and dispose it.
- **Presets** (`WidgetPresets`) are groups of widgets wired to the real sensors found on this PC (via `AidaSensorMap` lookups);
  **templates** (`ThemeTemplates`) compose presets into whole themes. Widgets from one preset share `Widget.Group` and move together in the editor.
- **`AidaImporter`** reads `.axlcd` (text, one item per line, BGR colour ints) and maps AIDA item codes to sensors. Pages become pages.
- **`ThemePackage`** reads/writes `.glance` (zip with `theme.json` + `assets/`). Import is defensive on purpose (path traversal, drive letters, size/entry caps, only `theme.json` and `assets/*`). Keep it that way.
- **Undo/redo** is snapshot based (`Theme.ToSnapshot/ApplySnapshot`), debounced; all pages and widgets are hooked (`HookAll`).
- **Video** is decoded by an `ffmpeg` child process (`-re` is required or it plays several times too fast); FFmpeg is optional and found automatically (app folder, PATH, winget machine and user folders).
  Video is decoded only in user-side processes (tray app, editor), as the user; the service never starts ffmpeg.
  `VideoSource` finds and starts ffmpeg on its own thread (never the UI thread); frames are reference counted, so draw them only
  through an `AcquireFrame()` lease. While a video widget is resized, the renderer keeps the running decoder and stretches its frames;
  a decoder for the new size starts once the size has been stable for 500 ms. A stopped ffmpeg is retried after 5 s, doubling per failed run up to 60 s (logged once to runner.log)
  (`docs/design/fix-video-frames.md`). `Ffmpeg.Find` keeps its
  elevated rule for any elevated caller: it only accepts a copy that only administrators can change
  (`FolderTrust.CheckExecutable(exe, TrustScope.FolderFiles)`: the exe, the files next to it, its folder and every folder above it, no links). The editor's
  FFmpeg button and the setup check install per user (no prompt). `SetupChecks.MachineInstallScript` (machine scope, resets the package
  folder's permissions) is kept but no longer offered,
  because winget moves the files there from the user's temp folder with the user's access still on them.

## Conventions and gotchas

- .NET 8, C# with nullable enabled, root namespace `Glance.*`. UI is built in code; the property editor is **reflection based**:
  `PropertiesPanel` rows refer to `Widget` property names, so keep names and `PropertyChanged` names in sync.
- **Avalonia 12:** `ColorPicker` is a separate package and needs the explicit `StyleInclude` in `App.axaml`. Never set `Foreground = null`
  (it makes text invisible). `Window.Theme` clashes with our `Theme` type, so the editor aliases it (`AppTheme`).
- Skia text and `SKPaint` etc. are `IDisposable`; use `using`. Frame buffers are BGRA premultiplied and converted per frame.
- **File encoding:** read and write source with `[IO.File]::ReadAllText` / `WriteAllText` and `new UTF8Encoding($false)`.
  `Get-Content -Raw` + `Set-Content` in Windows PowerShell 5.1 corrupts non-ASCII characters (it once turned `°` into `Â°`).
- PowerShell 5.1 quirks: no `&&`; here-string terminators must start at column 0; avoid nested quotes inside `$(...)` in one-liners.
- Elevation: only the service has administrator rights (LocalSystem, environment from the SCM). The tray app and the editor are `asInvoker`
  and must never run elevated (the installer starts them with `runasoriginaluser`). Never add a `requireAdministrator` manifest (a test fails).
- Do not block the UI thread with USB, process or registry work (setup checks run in `Task.Run`).
- **Every `await` in `Glance.Core` needs `ConfigureAwait(false)`** (CA2007 is a build error there, and `ConfigureAwaitGuardTests` scans
  the source). The tray app and the editor call Core from their UI threads and block on it; an await that resumes on the caller's
  context deadlocks (that left the display black in 0.2.0, `docs/design/fix-tray-deadlock.md`). Never block on a task
  (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`) without a timeout; test such code on `UiLikeContext` (tests), not only under xUnit.
  The tray app opens the display with `TrayDisplayOpener` (Core, tested), never on its UI thread; blocking waits in Core need a timeout
  or a `// sync-wait: <why>` comment (`ConfigureAwaitGuardTests` scans for `.Wait()`, `.Result`, `GetResult()`).
- Rendering races: the editor mutates the theme while a pusher renders it; renderers catch `InvalidOperationException` and retry next frame.

## Verifying changes

```powershell
dotnet test src/Glance.Tests -c Release      # ~610 tests, about 15 seconds, no hardware or service needed
```

- `src/Glance.Tests` (xUnit) covers the hardware-free logic: page rotation, setup diagnoses, `.glance` import safety, undo,
  theme snapshots and old-file migration, presets and templates (every preset on a typical fake PC and on a PC with no sensors),
  the AIDA64 importer and sensor mapping, pixel-level rendering checks (bars, thresholds, alignment, gauges), colour conversion and the sensor pipe.
  `TestSupport.cs` has `Fake.Typical()` (a realistic LibreHardwareMonitor-style sensor list) and helpers to render a theme to a bitmap.
- Tests never use the real pipe names (`SensorPipeServer`, `PipeSensorClient`, `DisplayServer`, `RemoteFrameDisplay` and `ControlPipeClient` take a
  name) so they cannot collide with a running service or tray app. `DisplayServiceTests` runs the display server and client over real pipes with
  `FakeDisplay`, fake session lookups and a fake clock; the server then runs as the test user, so tests pass that user as the trusted pipe owner.
- **Not testable here (needs an installed service):** the service entry (`NativeService`), its real start before sign-in, PRESHUTDOWN timing,
  the installer's `sc.exe` steps, upgrade from 0.1.1. `ServiceLayoutTests` only check that the installer text matches the tested rules.
- CI (`.github/workflows/ci.yml`) builds the solution and runs the tests on every push.
- When you fix a bug or add logic, add a test. A good habit: break the code on purpose and check a test fails (a "mutation check");
  this found a real gap once (the stale-sensor-feed rule was untested).
- **Not testable here:** anything involving the physical display (timing, colours, rotation, blanking), real sensors, drivers or installers.
  Confirm those by running the app and looking, or report them as unverified. Editor windows can be captured with `PrintWindow` for a visual check.
## Git

- `main` on `https://github.com/ZanDvorsak/Glance` (HTTPS; Git Credential Manager holds the login).
  Pushing from a non-interactive shell works with the saved login; a first-time sign-in needs the user's own terminal.
- Small, descriptive commits (what and why). Verify the build before committing; do not commit `dist/`, `bin/`, `obj/`, captures or personal themes.

## Known gaps / ideas

- No Linux/macOS support yet (needs libusb 1.0 USB backend and per-OS sensors); the code is Windows-only today.
- Installer and exes are unsigned (SmartScreen will warn); no GitHub release has been published.
- Full-screen video is limited to ~2.5 fps by USB; GIFs and small animations look better.
- UI code (`Glance.App`) has no automated tests; its logic should live in `Glance.Core` where it can be tested.
- The service has never been installed and run on a real PC by an agent (that needs admin rights and a real install); see the manual
  test plan in `docs/design/runner-service.md`. Before anyone signs in the display stays dark (decision for the user: an admin-chosen
  boot theme would need rendering in SYSTEM again).
- **TODO:** make the live display a widget, so people without the hardware can still see their screen (for example docked in the editor or a small always-on-top window). The virtual display window already renders the theme with live sensors, so reuse that renderer.

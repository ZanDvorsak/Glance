# AGENTS.md

Guidance for AI coding agents (and humans) working on **Glance**: a Windows app that designs and runs live hardware
screens on small USB LCDs (AX206-style, USB ID `1908:0102`, 480x320). It replaces AIDA64's LCD customizer.

Read this first, then `README.md` (user-facing) and `docs/PROTOCOL.md` (the display's USB protocol).

## Ground rules

- **This runs on a real person's PC with real hardware.** The runner drives their display and reads their sensors.
  Do not install software, push to GitHub, publish releases, create scheduled tasks or change system settings
  without being asked. Installs go through `winget` and only on an explicit yes.
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
| `src/Glance.Runner` | Elevated WinForms tray app that owns the display and serves sensors |
| `src/Glance.SensorHost` | Older standalone elevated sensor feed (the runner supersedes it) |
| `src/Glance.Tests` | xUnit tests for `Glance.Core` |
| `src/Glance.Demo`, `src/UsbProbe` | Small diagnostic tools, not shipped |
| `build/` | `publish.ps1` (package + zip), `make-installer.ps1` (Inno Setup) |
| `installer/` | Inno Setup script and the "before you start" text |
| `assets/` | App icon (`glance.ico`, generated from code) |
| `docs/PROTOCOL.md` | Reverse-engineered display protocol and measured limits |

Key files: `Core/Themes/{Theme,ThemePage,Widget,ThemeRenderer,ThemePackage}.cs`, `Core/Display/DisplayPusher.cs`,
`Core/Usb/Ax206Display.cs`, `Core/Presets/{WidgetPresets,ThemeTemplates}.cs`, `Core/Import/AidaImporter.cs`,
`Core/Setup/SetupChecks.cs`, `App/MainWindow.cs` (large, the editor's brain), `App/Views/PropertiesPanel.cs`,
`Runner/RunnerContext.cs`.

## Build, run, package (PowerShell on Windows)

`dotnet` and `git` are installed but may not be on PATH in a fresh shell. Use:

```powershell
$env:Path += ";C:\Program Files\dotnet;C:\Program Files\Git\cmd"
cd src
dotnet build Glance.sln -c Release
```

- Editor: `src\Glance.App\bin\Release\net8.0\Glance.App.exe`. Runner (asks for admin): `src\Glance.Runner\bin\Release\net8.0-windows\Glance.Runner.exe`.
- Package: `.\build\publish.ps1` (self-contained, ~215 MB folder in `dist\Glance`, ~90 MB zip; `-FrameworkDependent` is small).
  Installer: `.\build\make-installer.ps1` (needs Inno Setup 6). `dist/` is git-ignored.
- Version lives in `src/Directory.Build.props` only.
- Handy start options for the editor: `--setup`, `--new`, `--gallery` open those windows immediately.

### Things that break builds
- **A running elevated runner locks its own output files**, and a normal shell cannot kill an elevated process
  ("Access is denied"). Stop it with its control pipe instead (below), or build with `-p:SkipHost=true`
  / build only the projects you need. `publish.ps1` fails the same way if the editor or runner runs from `dist\Glance`.
- After any change under `Glance.Core`, a running package in `dist\` is stale until you re-publish and restart.

## Architecture

Two processes cooperate; both reference `Glance.Core`.

```
Glance.Runner (admin, tray)                      Glance.App (normal user, editor)
  LocalSensorSource -> SensorPipeServer  --pipe "Glance.Sensors" (1 JSON line/s)-->  PipeSensorClient -> SensorHub
  DisplayPusher (renders + sends frames)  <--pipe "Glance.Runner.Control"----------  RunnerClient
  watches %AppData%\Glance for theme/settings changes and reloads
```

- **Sensors:** LibreHardwareMonitor (needs admin + the PawnIO driver for CPU/motherboard data). `SensorHub` in the editor prefers the
  runner's feed and falls back to basic local sensors; the feed counts as gone after 5 s without data. Drive-space sensors come from `DriveSensors`.
- **Control pipe commands** (one line each, reply `OK ...`): `STATUS`, `RELOAD`, `PAUSE`, `RESUME`, `EXIT`, `NEXT`, `PREV`, `PAGE <n>` (0 releases),
  `BRIGHTNESS <0-7>`, `LIVE <base64 folder> <base64 theme json>` (show the editor's unsaved theme; `-` for no folder), `LIVE_CLEAR`.
  Build and parse them only with `Glance.Core.Control.ControlCommand` (shared by both sides and tested). Pipes are limited to the current Windows user.
- Single runner per session (mutex `Local\Glance.Runner`). Auto-start is a scheduled task named `Glance Runner` created only from the editor on request.
- **Runner command-line modes** (no tray, no display, no mutex; outcome in `runner.log`): `--install-autostart --user DOMAIN\name`
  registers the task through the Task Scheduler COM API, built in memory from the runner's own path, then reads it back;
  `--remove-autostart` deletes it. Exit codes are `AutostartTask.Exit` (0 ok, 10-20 failures, each with a message). The editor
  starts install mode through UAC, but removes the task with `schtasks /Delete` so it never elevates a possibly untrusted copy.
  Auto-start is refused unless `FolderTrust.CheckExecutable` says only administrators can change the runner and everything
  in its folder, and unless the signed-in user is an administrator.
- **Trust checks:** `Glance.Core.Security.FolderTrust` is the one engine for "may an elevated process start this?"
  (owner and ACL allow-list, no links, fixed local drive, complete chain to the drive root). Use it, do not add another.
- The runner's environment comes from the user (autostart passes it on). `StartupHookSupport` is off for the runner and the
  sensor host; profiler and diagnostics variables (`CORECLR_*`, `DOTNET_DiagnosticPorts`) cannot be blocked in-process and
  are only logged as a warning. See the H1 notes in `docs/reviews/review-autostart-task.md`.
- **Settings and themes:** `%AppData%\Glance` (`settings.json`, `Themes\<name>\theme.json` + `assets\`). On first use, the old
  `%AppData%\MiniLcd` folder is copied over (the app used to be called MiniLcd).

## The display (read `docs/PROTOCOL.md`)

- Bulk transfer over libusb-win32 (`libusb0.dll`, driver installed with Zadig). Frames are **RGB565 big-endian**, 480x320.
- **Only whole-screen updates work.** Partial rectangles time out and draw nothing (measured on the real display). A full frame takes ~400 ms, so ~2.5 updates/s max.
  `DisplayPusher` therefore skips frames identical to the last one and force-refreshes every 10 s. Do not build features that assume faster updates.
- On exit/pause the display is blanked (black frame, backlight 0), otherwise it keeps its last picture. Reload/error recovery deliberately does not blank (flicker).
- **Shutdown, restart, sign-out and sleep** also blank it (the USB port often stays powered, so the last frame would stay lit). `SessionPowerWatcher`
  (Core) runs a hidden top-level window on its own thread and turns `WM_ENDSESSION`/`WM_POWERBROADCAST` into `PowerEvent`s; the handler runs
  before the message is answered (Windows waits for it) but never longer than `PowerBlank.BudgetFor` (4 s session end, 1.5 s sleep), with a
  shutdown block reason set during a session end and always removed. `DisplayPowerPolicy` (pure, tested) decides: session end/quit -> blank once,
  stop and never reopen (also while asleep, so an installer can close the runner); suspend -> blank and hold; resume -> reopen (the runner's
  `Supervise` does it, the editor retries for about a minute). Owners update the policy first, then blank with `PowerBlank.Run`: a bounded
  lock wait, then the loop's own `DisplayPusher.Stop(true, timeout)` with what is left of `OwnerBudgetFor(e)`. If that returns false the
  pusher is remembered as still closing and the display is not reopened until `IsClosed`. If the owner's lock is busy the owner pays the blank itself.
  Every open checks `MayDrive` before and right after opening; that includes the restart of a dead loop. The editor keeps all of this in
  `PusherSlot` (Core, tested with `FakeDisplay`); the runner's `Supervise` checks the policy before its `ShouldReopen` restart.
  A missed wake notice heals after 30 s awake (`NoteAwakeTick`), or at once on tray Resume/Reload or the `RESUME` command.
  In the editor, Avalonia closes the window on an OS shutdown, so `Closing` usually does the session-end blank (off the UI thread;
  `Program.Main` waits for it); the watcher covers sleep and is the backup. Out of scope: Modern Standby timing (`PBT_APMSUSPEND` can come late),
  monitor off and lock (no `GUID_CONSOLE_DISPLAY_STATE` handling yet). Only verifiable by hand: shut down / sleep with the display attached and look.
- While a `DisplayPusher` runs, only its loop touches the device. `Stop(blank, timeout)` asks the loop to blank (skipped if the last
  transfer failed) and close; it returns false if a transfer is still stuck, and `IsClosed` turns true when the loop lets go.
  Use `DisplayPusher.QuickStopTimeout` (2 s) for sleep/shutdown. `LastError` is USB trouble (owners reopen via `DisplayPusher.ShouldReopen`);
  `LastRenderError` is a theme that fails to draw (shown, never a reason to reopen). Tests drive it with `FakeDisplay` and short `PusherTiming`.
- AIDA64 uses the same device; only one program can hold it.
- The display and runner pages: `DisplayPusher.NextPageIndex` is the pure rotation logic (shared with the virtual display window).

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
  In an elevated process (the runner) `Ffmpeg.Find` only accepts a copy that only administrators can change
  (`FolderTrust.CheckExecutable(exe, TrustScope.FolderFiles)`: the exe, the files next to it, its folder and every folder above it, no links); rejections go to `runner.log`. The editor's
  "Install for all users" runs winget with `--scope machine` elevated and then resets the package folder's permissions,
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
- Elevation: the runner needs admin for sensors, the editor must not run elevated (the installer and tray "Open editor" start it de-elevated).
- Do not block the UI thread with USB, process or registry work (setup checks run in `Task.Run`).
- Rendering races: the editor mutates the theme while a pusher renders it; renderers catch `InvalidOperationException` and retry next frame.

## Verifying changes

```powershell
dotnet test src/Glance.Tests -c Release      # ~270 tests, about 5 seconds, no hardware needed
```

- `src/Glance.Tests` (xUnit) covers the hardware-free logic: page rotation, setup diagnoses, `.glance` import safety, undo,
  theme snapshots and old-file migration, presets and templates (every preset on a typical fake PC and on a PC with no sensors),
  the AIDA64 importer and sensor mapping, pixel-level rendering checks (bars, thresholds, alignment, gauges), colour conversion and the sensor pipe.
  `TestSupport.cs` has `Fake.Typical()` (a realistic LibreHardwareMonitor-style sensor list) and helpers to render a theme to a bitmap.
- Tests never use the real pipe names (`SensorPipeServer`/`PipeSensorClient` take a name) so they cannot collide with a running runner.
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
- **TODO:** make the live display a widget, so people without the hardware can still see their screen (for example docked in the editor or a small always-on-top window). The virtual display window already renders the theme with live sensors, so reuse that renderer.

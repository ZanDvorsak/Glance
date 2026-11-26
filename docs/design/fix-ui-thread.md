# Blocking work on the UI threads (review M7, autostart review L6, renderer review L5)

Branch `claude/fix-ui-thread-280814`, based on `main` at `8d2d13c` (after the runner/service redesign).

M7 in `docs/reviews/review-full-codebase.md` found process, pipe, USB and large-file work on the editor's UI thread. It was
written against the old design (an elevated runner started by a scheduled task). This note re-assesses it on the current
`main`, lists what the redesign already removed, proves what is left, and records the fix.

## 1. How it was proven

The editor (`Glance.App`) and the tray app (`Glance.Runner`) have no test project of their own. Two kinds of test prove the
problem and the fix:

- **`UiThreadAuditTests`: an analyzer-style check over the source of both programs.** It has 21 rules, one per blocking API
  (open or stop the display, list processes, ask the Service Control Manager, start `schtasks`, a control-pipe round trip,
  unzip, the AIDA64 import, `Theme.Save`, a network copy, a whole-file read, the FFmpeg search, a tree delete, the tray's
  display open/stop, file reads, and so on). A call passes only when it is:
  - inside work that runs off the UI thread (`Task.Run(...)`, `SerialWork.Run(...)`, `new BackgroundProbe<T>(...)`);
  - inside a method marked `[WorkerThread]`, which may itself only be called from such places (a second test checks this);
  - or on a line that says why it may block (`// ui-thread-ok: reason`), so every exception carries its reason.

  The scanner blanks comments and string literals first, and three tests check the scanner itself on a sample.
- **`UiThreadTests`: timing tests on a stand-in UI thread** (`TestUiThread`: one thread with a synchronization context, so
  `await` comes back to it as it does with Avalonia's dispatcher). Each test measures how long a handler holds that thread
  when the work behind it is slow (fake displays from `DisplayPusherTests`, a sleeping open, a 300 MB file), first called
  directly as the old code did, then through the new seam.

### Before (the audit run against `main`'s `Glance.App` and `Glance.Runner`)

18 of the 21 rules fail on `main`; the 3 that pass are the items the redesign already fixed (section 2).

| Rule | Where on `main` | What it costs on the UI thread |
|---|---|---|
| display-open | `MainWindow.cs:1510` `StartSending`, `:1587` `ResumeSendingAfterSleep` | `_display.TryOpen`: the service pipe (0.5 s connect, up to 10 s for its hello), then the SCM, or USB |
| display-release | `MainWindow.cs:1447` `TurnDisplayOff`, `:1519` `ToggleSend` | `_display.Release(StopTimeout)`: up to 18 s with a stuck transfer, ~0.5-1 s normally |
| display-restart | `MainWindow.cs:1490` `RestartDeadPusher` (from the 1 s tick) | a 2 s bounded stop, then an open |
| tray-process-scan | `MainWindow.cs:1031, 1386, 1389, 1445, 1522`, `ServiceWindow.cs:73` | `RunnerClient.IsRunning` lists every process on the PC; `SlowTick` did it **every second** |
| service-control | `MainWindow.cs:1031` `CurrentSetupContext` | `ServiceControl.Query()` (SCM) |
| setup-context | `SetupWindow.cs:59` `RefreshAsync` (**every 3 s**) and `MaybeShowSetupOnFirstRunAsync` | the two above, through the `_context()` delegate |
| package-import | `MainWindow.cs:801` (`ImportPackage`, from Open and the command line) | unzips a `.glance` of up to 1 GB |
| aida-import | `MainWindow.cs:935, 944` | the AIDA64 import, twice, copying its pictures |
| theme-save | `MainWindow.cs:859` (Save), `:946` (AIDA64 import) | copies every picture and video into the theme folder, then flushes |
| network-copy | `MainWindow.cs:979` `LocalizePick` | copies a picked file from a network share |
| image-read | `MainWindow.cs:1012` `AddWidgetAsync` | reads a whole picture to learn its size |
| ffmpeg-search | `MainWindow.cs:1371` (`SlowTick`), `:1407` | while FFmpeg is missing, a full search of PATH and the winget folders every 5 s, even with no video in the theme |
| folder-delete | `MainWindow.cs:805, 953` | deletes the AIDA64 preview folder or a half-imported package |
| tray-launch | `ServiceWindow.cs:146` | ShellExecute of the tray app |
| tray-display-open | `RunnerContext.cs:195` `Supervise` (**every 2 s**) | opens the service pipe or USB; while the service runs but does not answer yet, 0.5 s of every 2 s |
| tray-display-stop | `RunnerContext.cs:234` `DisposePusher` (from `Supervise`, `Reload`, `SetPaused`, `MoveToServiceIfOnUsb`), `:290` | up to 18 s with a stuck transfer; the tray menu and the editor's PAUSE/RESUME/EXIT/RELOAD (queued to the UI thread) wait |
| tray-service-pipe | `RunnerContext.cs:293` `PayOwedBlank` | `DisplayConnector.TryBlank` |
| tray-file-read | `RunnerContext.cs:68, 111, 134` | settings and theme reads, which retry for up to a second when the file is locked |

Full output: the test log is reproduced by checking out `main`'s `src/Glance.App` and `src/Glance.Runner` over this branch and
running `dotnet test src/Glance.Tests -c Release --filter UiThreadAuditTests` (`Failed: 18, Passed: 8`; the 8 are the 3
passing rules, the 2 `[WorkerThread]` checks, which have nothing to check on `main`, and the 3 scanner self-tests).

### Timing (stand-in UI thread, this PC)

| Case | Called directly (the old code) | Through the new seam |
|---|---|---|
| Release a display whose transfer is stuck (1.5 s fake; real ones up to 18 s) | UI held **1487-1497 ms** | 1 ms (`SerialWork`) |
| Open a display that takes 1 s (pipe timeout, USB) | UI held **1002-1011 ms** | 1 ms (`SerialWork`) |
| Save a theme with a 300 MB video (fast NVMe disk; slower disks scale up) | UI held **102-196 ms** | 2 ms (`ThemeSaveJob`) |
| Restart a dead display loop whose reopen takes 0.8 s | (the same open as above) | under 150 ms (`SerialWork`) |
| Five 1 s ticks asking a probe that takes 0.5 s | (5 x 0.5 s) | under 150 ms, one probe run (`BackgroundProbe`) |

### After

`dotnet test src/Glance.Tests -c Release`: **829 passed, 0 failed** (791 on `main` + 38 new: 26 audit, 12 timing and behaviour).
All 21 audit rules and both `[WorkerThread]` checks pass.

### Mutation checks (each fix broken on purpose; a test must fail)

| Mutation | Caught by |
|---|---|
| `SerialWork.Run` runs the work inline on the caller | 5 of 12 `UiThreadTests` fail |
| `SerialWork` items are not chained (no ordering) | `SerialWork_runs_items_one_at_a_time_in_order` |
| `BackgroundProbe.Poke` calls the probe on the caller | `A_slow_probe_never_holds_the_UI_thread...` |
| A probe that started before `Report` overwrites it | `A_fresh_report_wins_over_an_older_probe` |
| `ThemeSaveJob.SaveAsync` saves on the caller | `Saving_a_theme_with_a_big_video...` (first run: **not caught**, see below) |
| `ThemeSaveJob.Apply` overwrites a path the user changed meanwhile | `An_edit_made_while_saving_is_kept_and_reported` |
| `ToggleSend` releases the display directly again | audit `display-release` |
| The tray's `Supervise` calls `SuperviseNow` directly again | audit `WorkerThread_methods_are_only_called_off_the_UI_thread(Glance.Runner)` |
| `SetupWindow` calls `_context()` on the UI thread again | audit `setup-context` (first run: **not caught**, see below) |

Two mutations survived the first run and the tests were tightened before the commit:
- On this disk 300 MB copies in about 140 ms, under the 150 ms "responsive" bound, so saving on the caller still passed. The
  test now also requires the seam to hold the thread for under a quarter of the direct save.
- The scanner cannot follow a delegate: `SetupWindow._context` is `MainWindow.CurrentSetupContext` (`[WorkerThread]`) passed in.
  The `setup-context` rule now covers `_context()` calls by name.

## 2. Obsolete: removed by the redesign (with evidence)

| Original item | Now on `main` | Evidence |
|---|---|---|
| M7: `ServiceWindow.Sync()` every 1 s started `schtasks.exe` and sent a blocking `STATUS` | No `Sync()`. `Refresh` runs every 2 s and does all of its work in one `Task.Run` (`ServiceControl.Query`, `DisplayConnector.ServiceStatus`, `RunnerClient.IsRunning`); the old-task check `Autostart.IsInstalled` runs once, when the window opens, in `Task.Run` (`CheckOldTask`) | `ServiceWindow.cs:69-91, 153-158` on `main`; audit rules `autostart-schtasks`, `tray-pipe`, `service-pipe` pass on `main` |
| Autostart review **L6**: `schtasks.exe` on the UI thread every second | Same as above: there is no per-second call any more | same |
| M7: the setup context called `Autostart.IsInstalled()` on the UI thread | `SetupContext` no longer has an auto-start field (the redesign changed it to `(SensorsFromService, SensorCount, Service, TrayRunning)`), so `schtasks` is gone. **But** `CurrentSetupContext` still asked the SCM and listed processes on the UI thread every 3 s: fixed here (`setup-context`, `service-control`, `tray-process-scan`) | `MainWindow.cs:1031`, `SetupWindow.cs:59` on `main` |
| M7: `ToggleSend`/`TurnDisplayOff` open and close USB on the UI thread | Changed, not gone: the display now opens through `DisplayConnector` (service pipe first, USB only without a service), still on the UI thread, and `Release` still waited up to 18 s. Fixed here | `display-open`, `display-release` |
| M7: `.glance` import, AIDA64 import twice, Save with large videos | All still on the UI thread on `main`; fixed here | `package-import`, `aida-import`, `theme-save` |
| Renderer review **L5**: a file check once a second per picture on the UI thread | Already fixed by the renderer branch (M10/M11): the stat runs in the background and the renderer never waits for it | `RendererReviewFixTests.L5_a_slow_file_check_does_not_block_rendering` on `main` (before: 6 s blocked; after: ~35 ms) |
| Export | Already in `Task.Run` on `main` | `MainWindow.cs:784` |

## 3. What changed

### New in `Glance.Core` (tested)
- **`Threading/SerialWork`**: runs blocking work on the thread pool, one item at a time, in the order asked, never on the
  caller's thread; a failing item faults only its own task. Awaiting it on the UI thread returns to the UI thread.
- **`Threading/BackgroundProbe<T>`**: a slow yes/no answer read without waiting (`Value`); `Poke` starts at most one check;
  `Report(value)` from a fresh check wins over an older one still running.
- **`Threading/WorkerThreadAttribute`**: marks methods that block; the audit enforces where they are called from.
- **`Themes/ThemeSaveJob`**: `Begin` (UI thread) takes a detached copy of the theme; `Run` (worker) is `Theme.Save` on the copy;
  `Apply` (UI thread) gives the live theme the new paths and folder, as `Theme.Save` would have, except where the user changed
  a path meanwhile, and reports `ChangedWhileSaving`. `Theme.Save` itself is unchanged.

### Editor (`MainWindow`)
- **Send to display / Display off / dead-loop restart / reopen after wake** run on one `SerialWork` (`_displayWork`), so a
  release always finishes before the next open, and unticking always queues a release (it also closes an open still running).
  Every toggle bumps `_sendGeneration`; a slow open or blank whose toggle is no longer the latest changes no UI. Status says
  "Connecting to the display..." / "Turning the display off..."; Display off is disabled while it runs.
- **Marshalling:** Avalonia controls refuse other threads, so `OpenPusher` (now `[WorkerThread]`) no longer reads
  `_pinDisplay`: the pin and brightness are read on the UI thread (`OpenArgsNow`) and handed to the worker item. Everything after
  an `await` runs on the UI thread again.
- **"Is the tray app running?"** in the 1 s `SlowTick` is a `BackgroundProbe` (at most a tick old); `ToggleSend` and Display off
  ask freshly on the worker and `Report` the answer. **FFmpeg present?** is a probe too, and is only asked while the page has a
  video widget.
- **Open, Save, Save as, AIDA64 import, `.glance` import** run in `Task.Run` (Save through `ThemeSaveJob`). While one runs, the
  file buttons and Undo/Redo are disabled (`BeginFileWork`), the status bar says what is happening ("Saving ...",
  "Unpacking ...", "Reading ...", "Importing ..."), and a second request says to wait. Closing the window while a save or
  import copies files is refused with a message, except when Windows shuts down. A `.glance` named on the command line is
  unpacked after the window has opened (before, it blocked start-up before the window existed).
- **Network picks** (`PickedFile.Localize`) and **picture sizes** (`ImageSize`) run in `Task.Run`.
- **Setup check**: the context is built inside the `Task.Run` that runs the checks.

### Service window
No process scan in the constructor (the tray state says "checking..." until the first refresh), the tray app is started in
`Task.Run`, and the three action buttons are disabled at once while an action runs (before, a second click went through until
the next refresh).

### Tray app (`RunnerContext`)
`Supervise`, `Reload` and the hand-over to the service post to a `SerialWork` (`SuperviseNow`, `ReloadNow`, `MoveToServiceNow`);
requests that pile up while a transfer is stuck collapse into one. Status text goes back to the UI thread with `BeginInvoke`.
The menu's Next/Previous page go through the worker too, because the worker can hold `_gate` for seconds. The power-event path
is unchanged: when it finds `_gate` busy it leaves the blank owed, and the next `SuperviseNow` pays it (before: the UI thread).
`Quit` still blanks on the UI thread on its way out (marked `ui-thread-ok`), after setting `_quitting` so the worker starts
nothing new. Shared state: `_paused` and `_quitting` are volatile, the settings re-read time is an `Interlocked` tick count,
and the watcher list is locked.

## 4. Left as they are (with reasons)
- `LoadInitialTheme` reading a `theme.json` and the tray's constructor reading settings and the theme: start-up, before any
  window or message loop exists (`ui-thread-ok`).
- `Process.Start` of winget, Zadig, URLs and the editor: returns as soon as the process starts; the waits are already async.
- `_settings.Save()` after a save or import, and in `Closing`: one small file.
- `AppPaths.FindTrayExe()` in the service window and `FindZadig()` in the setup window: local file checks.
- The renderer starting FFmpeg and calling `Ffmpeg.Find()` on the UI thread while it draws a video widget: review M9, handled
  by the video session. Note that `Ffmpeg.Find` holds a lock while it searches, so the renderer can wait for a background search;
  that search used to run on the UI thread anyway.
- `ExportAsync` reads the live theme from `Task.Run` while the user may edit it (a data race, not UI blocking): **follow-up**,
  it could export a `ThemeSaveJob`-style detached copy.
- The tray's `STATUS` command reads `_statusItem.Text` from a pipe thread (as before).

## 5. Limits of the audit
It is a text scanner, not a compiler: it follows `Task.Run`/`SerialWork.Run`/`BackgroundProbe` lambdas and `[WorkerThread]`
bodies by brackets, not by data flow. It cannot see a blocking call reached through a delegate or a method of another class
that has no rule (hence the `setup-context` rule), or a `ContinueWith` callback after a `Task.Run`. A new blocking API needs a
new rule. The timing tests cover the seams; the editor's own handlers are only covered by the audit and by hand.

## 6. Coordination
The M5+M13 session edits the live-send and service-status paths. This branch does not change what is sent: `StartLive`,
`StopLive`, `PushLive`, `LiveSendGate`, `CheckService` and the tray's `Execute` are untouched. It changes `ToggleSend`,
`TurnDisplayOff`, `StartSending`, `RestartDeadPusher`, `ResumeSendingAfterSleep` and `SlowTick` in `MainWindow`, and `Supervise`,
`Reload`, `DisposePusher`, `PayOwedBlank`, `MoveToServiceIfOnUsb`, `SetStatus`, `Quit` and the constructor in `RunnerContext`:
expect textual conflicts there if that branch touches the same lines.

## 7. Manual checks (not done here: they need the editor on screen, the display, or the service)
1. Editor, Send to display ticked and unticked quickly several times: the window never freezes; the display ends in the state
   of the checkbox; the status matches the last click.
2. Display off while the editor drives the display, and while the tray app does: screen goes black, status says so.
3. Unplug the display while sending: the window keeps responding; the status shows the error and, after replugging, reopens.
4. Save As of a theme with a large video to a new folder: the status says "Saving ...", the file buttons and Undo grey out, the
   preview keeps animating; afterwards the theme points into the new `assets` folder. Move a widget during the save: the status
   says the change is not in the file yet; Save again keeps it.
5. Try to close the editor during that save: it stays open with a message; close again afterwards.
6. Open a large `.glance` (and `Glance.App.exe some.glance` from a terminal): the window opens at once and shows "Unpacking ...".
7. Import an AIDA64 theme: "Reading ...", the preview, "Importing ...", no freeze.
8. Setup check and Background service windows open: the editor keeps responding; the tray state shows "checking..." briefly.
9. Tray app: with the display unplugged or the service stopped, the tray menu opens at once and Pause/Resume/Next page respond;
   the status line still updates; Exit still blanks the display.
10. Sleep and wake while the editor sends: the display comes back (the reopen now runs on the worker).

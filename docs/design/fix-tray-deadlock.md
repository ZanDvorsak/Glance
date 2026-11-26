# Fix: the display stays black with Glance 0.2.0 installed (tray app deadlock)

Branch `claude/fix-tray-deadlock-a14e66`, on top of `main` 8d2d13c.

## What was seen on the real PC

- `GlanceService` runs as LocalSystem. `service.log` shows "starting", "Display pipe ready", "Sensors ready (285)" and nothing
  more: never "Display opened for session 1".
- From session 1 the service answers STATUS with `display released; brightness 7; awake`: it works, it has simply never
  received a frame.
- The tray app logged only "Tray app starting" and "Loaded theme ...", used about 1.3 s of CPU in several minutes, never
  created its control pipe, and its windows still answer `WM_NULL` (an STA thread blocked in a wait that still pumps sent messages).

## Bug 1: sync-over-async on the UI thread deadlocks (tray app and editor)

**Cause.** `RunnerContext`'s constructor forces a WinForms handle (`_ = _ui.Handle`), which installs
`WindowsFormsSynchronizationContext` on the UI thread, then calls `Supervise()` before the control pipe, the timers and the
service check start. `Supervise()` creates `new DisplayPusher(...)` -> `DisplayConnector.Open()` -> `RemoteFrameDisplay.Connect()`
-> `Request(Hello)`, which blocked with `.GetAwaiter().GetResult()` on `_pipe.WriteAsync` and `DisplayWire.ReadReplyAsync`.
`ReadReplyAsync` and `ReadUpTo` awaited without `ConfigureAwait(false)` (there was none anywhere in `Glance.Core`), so the
continuation after the pipe read was posted to the UI thread, which was blocked waiting for that continuation. Permanent
deadlock: the constructor never returned and no frame was ever sent. The 10 s reply timeout could not help, because the
cancelled read's continuation also needs the UI thread.

The editor has the same path: **Send to display** with no tray app running calls `PusherSlot.TryOpen` -> `new DisplayPusher`
-> `DisplayConnector.Open()` on Avalonia's UI thread, and later requests (brightness, blank) on a connection also went
through `Request`. So the editor would have frozen for good on Send to display through the service.

Unit tests missed it because xUnit runs tests without a single-threaded `SynchronizationContext`.

**Proof (before).** `UiLikeContext` (in `UiThreadDeadlockTests.cs`) is a context like WinForms' and Avalonia's: posted work
runs only when its own thread pumps. Tests run the real client code on such a thread against a `DisplayServer` with a
`FakeDisplay` on a random pipe name, with an 8 s watchdog. On the old code (`main` 8d2d13c):

| Test | Before |
|---|---|
| `Connecting_to_the_service_on_a_UI_thread_returns_instead_of_deadlocking` | **failed**: "RemoteFrameDisplay.Connect (Hello) deadlocked on a UI-like thread: still blocked after 8 s (2 callbacks were posted back to the blocked UI thread)" |
| `The_tray_apps_first_open_on_its_UI_thread_reaches_the_display` (what `RunnerContext`'s constructor did) | **failed**: deadlocked, 4 callbacks posted |
| `Requests_made_on_a_UI_thread_after_connecting_also_return` (SetBrightness/Blit/Blank) | **failed**: deadlocked, 6 callbacks posted |
| `The_service_status_query_returns_on_a_UI_thread` | **failed**: deadlocked |
| `A_service_that_never_answers_times_out_on_a_UI_thread_instead_of_hanging_for_ever` (1 s reply timeout) | **failed**: still blocked after 8 s, the timeout never fired |
| `The_editors_Send_to_display_through_the_service_works_on_its_UI_thread` (`UiThreadAuditTests`) | **failed**: deadlocked |
| `The_editors_restart_of_a_dead_loop_through_the_service_returns_on_its_UI_thread` | **failed**: deadlocked |

With a **real** `WindowsFormsSynchronizationContext`: a small probe (outside the repo) creates a WinForms `Control` handle on
an STA thread, exactly like `RunnerContext`, and calls `RemoteFrameDisplay.Connect` against a fake service on a random pipe
name with a 2 s reply timeout. Old `Glance.Core.dll`:

```
context: System.Windows.Forms.WindowsFormsSynchronizationContext
DEADLOCKED: still blocked after 10,0 s (reply timeout was 2 s)
```

**Fix.**
- `RemoteFrameDisplay.Request` no longer blocks on async I/O from the caller's thread. `Exchange` runs the write and the read
  on the thread pool (`Task.Run`, so no caller context is ever captured) and waits with a real bound: the reply timeout
  cancels the pipe I/O, and if even that does not complete, the wait gives up 2 s later and closes the pipe. The connection
  is then marked broken and the caller gets "The Glance service did not answer in time."
- Every `await` in `Glance.Core` now has `ConfigureAwait(false)`: `DisplayWire`, `DisplayServer`, `SensorPipeServer`,
  `PipeSensorClient`, `SensorHub`, `LocalSensorSource` (22 awaits). Either change alone ends this deadlock; both are kept
  so a future sync wait on Core code cannot bring it back.

**Proof (after).** All the tests above pass; nothing is posted back to the UI-like context (`Posted == 0` is asserted), and the
1 s reply timeout fires in about 1 s on a UI-like thread. The WinForms probe with the new `Glance.Core.dll`:

```
context: System.Windows.Forms.WindowsFormsSynchronizationContext
FINISHED in 0,1 s, error: none
```

## Bug 2: the tray app did display work on its UI thread, in its constructor, before anything else

Even without the deadlock, the constructor opened the display before the control pipe, the timers and the service check
existed, and `Supervise` (a WinForms timer, UI thread) kept opening it there, holding the lock for the connect and the hello.
A failed open was stored in `_lastError` and never logged, which is why `runner.log` said nothing.

**Fix (`RunnerContext`).**
- The constructor starts the file watchers, the control pipe, the power watcher, the timers and the service check first, and
  the display last.
- `Supervise` never opens the display itself: it starts `BackgroundOpener` (new, `Glance.Core.Display`), which runs the open on
  the thread pool, one attempt at a time, and hands the result to `Opened`. `Opened` takes the lock briefly and keeps the new
  pusher only when the policy still allows driving, the user has not paused and no other pusher runs; otherwise it stops it
  (blanking for sleep, shutdown and pause), as the old code did right after opening. Then it marshals `Supervise` back to the
  UI thread for the tray status ("Opening the display..." while it runs).
- A failed open is logged in `runner.log` ("Could not open the display: ...") at most once a minute per distinct message
  (`LogLimiter`), and a successful one says whether it went through the Glance service or USB.
- A power event during a background open finds nothing to blank; `Opened` sees the policy and blanks the new pusher.

**Proof.** The tray app itself has no automated tests (WinForms). `BackgroundOpener` is tested on a UI-like thread: `TryStart`
returns at once while the open blocks, a second attempt is refused while one runs, the result arrives on another thread, a
failure is reported and allows the next attempt, and a pusher nobody takes is stopped (the device is let go). These are new
code, so there is no "before". The tray app was **not** run here: one runs on this PC already (single-instance mutex) and
must not be stopped.

## Bug 3: the service did not log why a client got nothing

`DisplayServer` stored a refusal or a failed USB open only in `_lastError`/the reply, so `service.log` could not tell
"no client ever came" from "clients came and were refused" or "USB failed".

**Fix.** It now logs `Refused <op> from session <n>: <reason>` and `Could not open the display for session <n>: <error>`,
rate-limited by the server's existing `LogLimiter` (once a minute per distinct line). STATUS is never refused, so the tray's
status checks do not log.

**Proof.** `The_service_logs_a_refused_client_once_a_minute_not_on_every_request` and
`The_service_logs_when_it_cannot_open_the_USB_display` failed before (no such line) and pass after (exactly one line for three
attempts).

## Audit: every other synchronous wait that can run on a UI thread

| Site | Verdict | Proof |
|---|---|---|
| `RemoteFrameDisplay.Request` (all of Connect, QueryStatus, Blit, SetBrightness, Blank, `DisplayConnector.TryBlank`) | **Deadlock**, fixed (Bug 1) | tests above, before/after |
| `ControlPipeClient.cs:43` `task.Wait(timeout) ? task.Result` | Safe: `StreamReader.ReadLineAsync` uses `ConfigureAwait(false)` inside the BCL, and `Task.Wait(timeout)` never runs the task inline; disposing the pipe after a timeout returns | `The_editors_tray_command_returns_on_its_UI_thread_when_the_tray_app_never_answers` (null in < 4 s) and `..._gets_its_reply_on_its_UI_thread`, both pass before and after. Unchanged. |
| `ThemeRenderer.cs:765` `_pending.Wait(waitMs)` | Safe: the task is `Task.Run` over synchronous code (a file stat), waited with a timeout | `The_renderers_file_checks_return_on_a_UI_thread`. Unchanged. |
| `SessionPowerWatcher.cs:90` `ready.Wait()` | Safe: waits for its own new thread to create the hidden window, which needs nothing from the caller | `Starting_the_session_power_watcher_on_a_UI_thread_returns`. Unchanged. |
| `DisplayPusher.cs:309` `_task.Wait(timeout)` | Safe: the loop is a `LongRunning` thread with no context; its blank through the service now runs on the pool anyway | `Stopping_a_pusher_from_a_UI_thread_returns_and_blanks_through_the_service`. Unchanged. |
| `MainWindow.cs:1537` `PendingExitBlank?.Wait(...)` | Safe: a `Task.Run` over `PusherSlot.Release` (synchronous), bounded, called from `Program.Main` after Avalonia's loop ended | by reading; `Release` itself is tested on a UI-like thread. Unchanged. |
| Editor Send to display: `PusherSlot.TryOpen` / `RestartIfDead` -> `DisplayConnector.Open()` on Avalonia's UI thread | **Deadlock** (same as Bug 1), fixed by the Core fix | `The_editors_Send_to_display_through_the_service_works_on_its_UI_thread`, `The_editors_restart_of_a_dead_loop_...`, before/after |
| `MainWindow.cs:1460/1475` `t.Result` | Safe: inside `ContinueWith`, the task has finished | by reading |
| `Autostart.cs:29`, `ServiceRuntime.cs:78` `Task.WaitAll`/`drain.Wait` | Safe: BCL `ReadToEndAsync` (no context capture), bounded, run via `Task.Run` (editor) or in the service (no UI) | by reading |
| Tray `Supervise` -> `PayOwedBlank` -> `DisplayConnector.TryBlank()` and `DisposePusher` -> `Stop` on the UI thread | No deadlock any more; still blocking but bounded (pipe connect 0.5 s + reply timeout; stop up to its timeout) | covered by the Request tests; see follow-ups |

## Guard against a regression

- `src/Glance.Core/Core.globalconfig` makes CA2007 ("call ConfigureAwait on the awaited task") an **error** for Glance.Core
  (`GlobalAnalyzerConfigFiles` in the csproj). Mutation check: removing the `ConfigureAwait(false)` in `DisplayWire.ReadUpTo`
  fails the build with `error CA2007` at that line.
- `ConfigureAwaitGuardTests` checks that the rule stays switched on and scans every `.cs` file in Glance.Core for an await
  without `ConfigureAwait(false)` (comments and strings ignored; the scanner has its own cases).

## Follow-ups (not done here)

- The editor still opens and stops the display on its UI thread (`StartSending`, `ToggleSend`, `RestartIfDead`). It no longer
  deadlocks, but a service that does not answer freezes the window for up to the reply timeout. Moving it to a background open
  like the tray app is UI work in `MainWindow.cs`.
- The tray's `PayOwedBlank` and `DisposePusher` on its UI thread are bounded but can still block it for seconds.

## How to check it on the real PC

After reinstalling with this build:
1. Within a few seconds of the install finishing (or of signing in), the display shows the theme.
2. `%ProgramData%\Glance\service.log` shows "Display opened for session 1".
3. `%AppData%\Glance\runner.log` shows "Display opened through the Glance service", and the tray menu shows the theme and fps.
4. Exit the tray app, open the editor and click **Send to display**: the display shows the editor's theme, and the editor stays responsive.

## Test totals

Before this branch: 791 tests. After: 819 (28 new), all passing, run three times in a row.

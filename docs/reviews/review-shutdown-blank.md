# Code review: blank the display at shutdown, sign-out and sleep (`claude/feature-shutdown-blank-60ded2`)

**Date:** 2026-11-26
**Change:** commit `df02e6d` "Turn the display off at shutdown, sign-out and sleep; bring it back on wake" (base `main` at `0a9f10b`).
**Files:** `src/Glance.Core/Display/{SessionPowerWatcher,DisplayPowerPolicy}.cs` (new), `src/Glance.Runner/RunnerContext.cs`,
`src/Glance.App/MainWindow.cs`, `src/Glance.Tests/PowerTests.cs` (new), `AGENTS.md`, `docs/TUTORIAL.md`.
**Reviewer:** an independent reviewer who did not write the change. No code was changed.

**How to read a finding:**
- **Confirmed**: the code path exists exactly as described.
- **Plausible**: the outcome depends on Windows or Avalonia behaviour that was not observed here (no shutdown, sleep or sign-out was performed).

Nothing here was tested on the physical display, and this PC was not shut down, restarted, put to sleep or signed out.

---

## Verdict: merge after fixes

The mechanism is right and carefully built. A hidden **top-level** window on its own thread receives `WM_ENDSESSION` and
`WM_POWERBROADCAST`. The display is blanked synchronously before the message is answered. A small, pure, tested policy decides
"blank once, never reopen after a session end, hold while asleep". The runner's only open path (`Supervise`) and the editor's only
open path (`TryStartPusher`) both check that policy under the same lock the event handler uses. I found no high-severity problem.

Two medium issues should be fixed **as part of merging with `claude/fix-display-loop-693991`**:
- **M1**: after that merge, a stalled display can keep the session-end handler busy for 18 s or more. That is well past Windows' budget, and the shutdown block reason is set the whole time.
- **M2**: that branch adds a second way to open the display in the editor (`RestartDeadPusher`) that skips the new power gate. The textual merge is clean, so nobody will notice.

The low findings can follow. After the fixes, run the manual test plan below. Only looking at the display can confirm the feature.

---

## Build and test results

Run in the worktree on 2026-11-26:

| Step | Result |
|---|---|
| `dotnet build src/Glance.sln -c Release -p:SkipHost=true` | **Succeeded**, 0 errors, 31 warnings. One warning is new: `CA1416` for `Thread.SetApartmentState` at `SessionPowerWatcher.cs:73`. The rest were already there. |
| `dotnet test src/Glance.Tests -c Release` | **Passed 273/273** in about 2 s. That includes 20 new tests in `PowerTests.cs`. |
| Mutation check 1 | `DisplayPowerPolicy.cs:59` changed to always return `BlankAndStop`. `Session_end_while_asleep_needs_no_second_blank_and_wake_does_not_reopen` **failed**, as it should. |
| Mutation check 2 | `SessionPowerWatcher.Raise` changed to run the handler on the thread pool (asynchronously). Only `A_shutdown_block_reason_is_set_only_while_the_session_end_is_handled` failed. `Messages_reach_the_handler_on_the_watcher_thread_before_the_window_answers` **still passed** (see L6). |
| After the mutations | Both files were restored byte for byte, the tests passed 273/273 again, and `git status` was clean. |
| Trial merges (`git merge-tree --write-tree`, nothing written to the worktree) | Merged one at a time with this branch, each sibling merges **cleanly**: `fix-display-loop`, `fix-live-empty-folder`, `fix-autostart-task`, `fix-saveas-assets`, `fix-ffmpeg-elevation`. Merging all six together was not tried. |

---

## Findings

| ID | Severity | Summary |
|---|---|---|
| M1 | Medium | The session-end and suspend handlers have no time limit. After the display-loop merge, they can run 18 s or more while holding a shutdown block reason |
| M2 | Medium | Merge hazard: `RestartDeadPusher` from `fix-display-loop` opens the display without the power gate |
| L1 | Low | A missed resume leaves the runner dark for good. A session end while "suspended" does not quit the runner. The TUTORIAL fix (Reload) does not help |
| L2 | Low | The editor stops trying to reopen after about 7.5 s after wake, which may be too short after hibernate |
| L3 | Low | Avalonia probably closes the editor during `WM_QUERYENDSESSION`, so the editor's blank usually runs on the UI thread from `Closing`, with no time limit (plausible) |
| L4 | Low | The shutdown block reason is set only inside `WM_ENDSESSION`. Its benefit is not verified, and it can make a slow blank visible to the user |
| L5 | Low | Modern Standby and display-off or lock are not covered. This PC supports only S3, so it works here |
| L6 | Low | The "synchronous delivery" test does not detect an asynchronous handler |
| N1-N6 | Nit | Platform annotation, logging, misleading editor message, Restart Manager in the editor, WALKTHROUGH note, obsolete constant |

### M1. The session-end and suspend handlers have no time limit. After the display-loop merge, they can run 18 s or more (confirmed in the code; the effect on Windows is plausible)

- **Where:**
  - `src/Glance.Runner/RunnerContext.cs:184-191`: `OnPowerEvent` takes `lock (_gate)`, then `DisposePusher(blank: true)` → `DisplayPusher.Stop(true)`.
  - `src/Glance.App/MainWindow.cs:1399-1402`: `lock (_powerGate)`, then `ReleasePusher()` → `Dispose()`.
  - `src/Glance.Core/Display/SessionPowerWatcher.cs:131-137`: `Raise` holds a shutdown block reason for the whole handler.
  - On `claude/fix-display-loop-693991`, `DisplayPusher.StopTimeout = TimeSpan.FromSeconds(6 * 3)`, and `Stop(bool)` waits that long.
- **Evidence:**
  - On `main` and this branch, `Stop` waits at most 2 s for the loop, then sends a black frame and sets the backlight to 0. Each transfer has a 3 s USB timeout.
  - After the merge, the loop does the blank itself and `Stop` waits up to 18 s for it. Its own comment estimates the worst stall at about 3 transfer timeouts (about 9 s).
  - The runner's handler first has to get `_gate`. The UI thread can hold that lock for a long time:
    - `Supervise` holds it while it calls `Ax206Display.Open()` and `SetBrightness` (USB, 3 s timeouts each).
    - `Reload` holds it during `DisposePusher(blank: false)`, which can take 2 s now and 18 s after the merge.
- **Windows' budget:** about 5 s for `WM_ENDSESSION` (the `WaitToKillAppTimeout` / hung-app logic), and about 2 s for `PBT_APMSUSPEND`.
- **Failure scenario:** the display has stopped answering (a USB stall, or a hub that went to sleep early), and the user shuts down from Start.
  1. The handler sits in `Stop` for 9 to 18 s with a block reason set ("Turning off the Glance display").
  2. Windows either shows the "apps are preventing shutdown" screen, which can wait for the user who has already walked away, or kills the process mid-transfer.
  3. Either way the display is not blanked, and the shutdown may be delayed or held.
  4. For sleep, the system suspends while the handler is mid-transfer. The blank then completes after wake, followed by a reopen. That is harmless but untidy.
- **In the normal case this is fine:** the loop finishes its current frame (≤ ~400 ms) and blanks (~400 ms), well inside the budget. The problem is only the stalled path.
- **Remedial action:**
  1. Add a bounded stop for power events. On the merged `DisplayPusher`, make `Stop(bool blank, TimeSpan timeout)` public, or add `StopForPowerEvent()`. Use about 3 s for a session end and about 1.5 s for suspend, and log when it returns `false`.
  2. In the runner, do not wait behind the UI thread for an unbounded time:
     - Update the policy first (`DisplayPowerPolicy` has its own lock, so `MayDrive` turns false at once).
     - Then take the pusher with `Interlocked.Exchange(ref _pusher, null)`, as the editor already does, or use `Monitor.TryEnter(_gate, 1000)`.
     - Blank that instance.
  3. After the merge, add a test: drive `DisplayPusher` with a fake `IFrameDisplay` whose transfers block, and assert that the power-event stop returns within its limit.

### M2. Merge hazard: `RestartDeadPusher` from `fix-display-loop` opens the display without the power gate (confirmed in the trial merge)

- **Where:** on `claude/fix-display-loop-693991`:
  - `src/Glance.App/MainWindow.cs`, status timer: `if (_pusher is { IsAlive: false } dead) RestartDeadPusher(dead);` (line 1255 in the trial merge).
  - `RestartDeadPusher`: `dead.Stop(blank: false); _pusher = null; ... _pusher = new DisplayPusher(...)` (lines 1350-1357 in the trial merge).
- **Evidence:** this branch made `TryStartPusher` the only place the editor opens the display. That method checks `_power.MayDrive` under `_powerGate`, and `ReleasePusher` swaps `_pusher` atomically. The sibling's new method writes `_pusher` with plain assignments, opens a `DisplayPusher` directly, and never looks at `_powerGate` or `MayDrive`. `git merge-tree` reports no conflict, so this survives the merge silently.
- **Failure scenario:**
  1. The editor's loop faults (`IsAlive == false`) just before Windows suspends or ends the session.
  2. On the watcher thread, `ReleasePusher` takes `dead` and blanks it.
  3. On the UI thread, the timer is already inside `RestartDeadPusher` and opens a new pusher. The display lights up again after the session-end or sleep blank: exactly what the change request forbids.
  4. The two plain writes can also leave a live pusher that nobody references (the USB device stays busy until exit).
  This is rare (it needs a faulted loop at the same moment as a power event), but it breaks the guarantee.
- **Remedial action:** when merging, rewrite `RestartDeadPusher` as:
  ```csharp
  if (Interlocked.CompareExchange(ref _pusher, null, dead) != dead) return;   // someone else released it
  dead.Stop(blank: false);
  if (!TryStartPusher(out var err)) { _send.IsChecked = false; _status.Text = "...could not be reopened: " + err; }
  ```
  This keeps one gate for every open. In `RunnerContext` the sibling's `!_pusher.IsAlive` branch sits after the `MayDrive` guard inside `lock (_gate)`, so the runner is fine.

### L1. A missed resume leaves the runner dark for good, and a session end while "suspended" does not quit it (confirmed)

- **Where:**
  - `DisplayPowerPolicy.cs:55-59`: `SessionEnding` while suspended returns `None`.
  - `DisplayPowerPolicy.cs:61-70`: only `Resume` clears `_suspended`.
  - `RunnerContext.cs:193-198`: `Quit` is requested only for `BlankAndStop`.
  - `RunnerContext.cs:149`: `Supervise` never opens while `!MayDrive`.
- **Evidence:** nothing but `PowerEvent.Resume` clears the suspended state. `Reload theme`, the `RESUME` pipe command and unplugging and replugging the display all go through `Supervise`, which returns early while `MayDrive` is false.
- **Failure scenarios:**
  - A `PBT_APMSUSPEND` arrives but the matching resume is never delivered, for example because the suspend was vetoed or failed in a driver, or because of a Windows quirk. The display then stays black until the runner is restarted. The TUTORIAL tells the user to "unplug and replug it, or use Reload theme", which will not help in this state.
  - Restart Manager (an installer closing the app) sends `WM_ENDSESSION` while `_suspended` is stuck. The policy returns `None`, so the runner does not quit and the installer has to kill it.
- **Remedial action:**
  - In the runner, request `Quit` for any `PowerEvent.SessionEnding` (`if (e == PowerEvent.SessionEnding) _ui.BeginInvoke(Quit);`), whatever the action.
  - Add a self-heal. WinForms timers do not tick while the PC sleeps, so if `_power.Suspended` has been true across, say, 15 consecutive supervisor ticks (30 s awake), call `_power.Handle(PowerEvent.Resume)` and log it. Put this as a pure method on `DisplayPowerPolicy` (for example `NoteAwake(TimeSpan)`) so it can be tested.
  - Let "Reload theme" clear a stale suspend too.
  - Update the TUTORIAL troubleshooting line to match.

### L2. The editor stops trying to reopen too early after wake (confirmed)

- **Where:** `src/Glance.App/MainWindow.cs:1415-1426` (`ResumeSendingAfterSleep`: 5 attempts, 1.5 s apart).
- **Evidence:** after about 7.5 s the editor gives up, unticks **Send to display** and shows an error. After hibernate, or with a hub that powers down, USB re-enumeration plus the libusb-win32 filter can take longer than that. The runner, by contrast, retries every 2 s for ever.
- **Failure scenario:** the user wakes a PC that was hibernating while the editor was sending without a runner. The display comes back after 9 s, but the editor has already stopped trying, and the user has to tick Send again.
- **Remedial action:**
  - Keep retrying while `_send.IsChecked == true` and `MayDrive` is true, with backoff (1.5, 3, 5 s ... up to about 60 s in total).
  - Show "Waiting for the display after wake..." meanwhile.
  - Untick only at the end.

### L3. Avalonia probably closes the editor during `WM_QUERYENDSESSION`, so the editor's session-end blank usually runs from `Closing` (plausible)

- **Where:** `src/Glance.App/MainWindow.cs:159-166` (`Closing`: `StopLive`, `Handle(Quit)`, `ReleasePusher`, `_powerWatcher.Dispose`).
- **Evidence:**
  - Avalonia 12's `Avalonia.Win32.dll` handles `WM_QUERYENDSESSION` (it raises `ShutdownRequested`).
  - `Avalonia.Controls.dll` has `WindowCloseReason.OSShutdown`.
  - `ClassicDesktopStyleApplicationLifetime` closes the windows on an OS shutdown request. The closing happens on the UI thread while Avalonia is answering `WM_QUERYENDSESSION`, before the session end is certain, and before the watcher's `WM_ENDSESSION`.
  - `Closing` then blanks synchronously on the UI thread, with no time limit (2 s now, 18 s after the merge).
- **Consequences:**
  - The result is still a blank display, which is good.
  - But the watcher is only a backup for session end in the editor. The comment "the display is blanked here, not on the UI thread" (`MainWindow.cs:1394`) describes the sleep path, not shutdown.
  - If another app then vetoes the shutdown, the editor is already closed. That part is not new: Avalonia did this before.
- **Remedial action:**
  - Confirm with the manual test (the editor sending, then shut down).
  - Use the bounded stop from M1 in `Closing` too.
  - Add one sentence to AGENTS.md: "In the editor, Avalonia closes the window on an OS shutdown, so `Closing` usually does the session-end blank; the watcher covers sleep and acts as a backup".

### L4. The shutdown block reason is set only inside `WM_ENDSESSION` (plausible)

- **Where:** `SessionPowerWatcher.cs:112-113` (`WM_QUERYENDSESSION` returns 1 and does nothing) and `SessionPowerWatcher.cs:133-136`.
- **Evidence:**
  - `ShutdownBlockReasonCreate` is documented for apps that are *about to* do work that should not be interrupted. The shutdown UI collects block reasons when apps are slow to answer the session messages.
  - Here it is created after the end is already decided. In the normal case (under 1 s) it has no visible effect.
  - In the stalled case (M1) it changes a quiet kill into a named entry on the "apps are preventing shutdown" screen, which may wait for user input.
  - The test only proves that it is created and destroyed, not that Windows does anything useful with it.
- **Remedial action:** decide deliberately.
  - Either keep it and bound the handler (M1), so it can never hold a shutdown for long,
  - or drop it and rely on the time limit.
  - If you keep it, add the manual check "shut down while the display is unplugged mid-frame" from the test plan.

### L5. Modern Standby and display-off or lock are not covered (plausible)

- **Where:** `PowerMessages.Translate` (`SessionPowerWatcher.cs:11-27`) maps only `PBT_APMSUSPEND` and the resume codes.
- **Evidence:**
  - On S0 Low Power Idle (Modern Standby) PCs, the screen goes off and desktop apps are throttled. `PBT_APMSUSPEND` can arrive late (when the system really enters standby), so the LCD can stay lit for minutes after the screen goes dark.
  - Locking the PC or letting the monitor sleep does not blank the LCD at all. That is out of scope, but users may expect it.
  - `powercfg /a` on this PC reports **only S3** (no S0ix, no hibernate, no Fast Startup), so the change works as intended here.
- **Remedial action:**
  - Document the limitation.
  - Optionally use `RegisterPowerSettingNotification(GUID_CONSOLE_DISPLAY_STATE)` on the watcher window and add a `DisplayOff/DisplayOn` event to the policy. That would also give an optional "turn the LCD off with the monitor" setting.

### L6. The "synchronous delivery" test does not detect an asynchronous handler (confirmed by a mutation check)

- **Where:** `src/Glance.Tests/PowerTests.cs:137-152`.
- **Evidence:** with `Raise` changed to `ThreadPool.QueueUserWorkItem(_ => _handler(e))`, this test still passed: the thread-pool item won the race against the assert. Only the block-reason test caught the mutation, and that was luck. "The display is blanked before Windows gets the answer" is the property the whole change depends on.
- **Remedial action:** make the handler slow, so an asynchronous version cannot pass:
  ```csharp
  using var w = new SessionPowerWatcher(e => { Thread.Sleep(150); lock (seen) seen.Add(e); });
  SendMessageW(w.WindowHandle, PowerMessages.WM_ENDSESSION, 1, 0);
  Assert.Equal(new[] { PowerEvent.SessionEnding }, seen);   // must already be there when SendMessage returns
  ```

### Nits

- **N1. Platform annotation:**
  - `SessionPowerWatcher` sits in `Glance.Core` (`net8.0`, not `net8.0-windows`) and P/Invokes `user32`. That causes a new `CA1416` warning at `SessionPowerWatcher.cs:73`.
  - **Action:** add `[SupportedOSPlatform("windows")]` to the class (and to `PowerTests`), or guard with `OperatingSystem.IsWindows()`.
- **N2. Logging:**
  - `RunnerContext.OnPowerEvent` (`:192`) logs only *after* blanking. If Windows kills the process mid-blank, `runner.log` shows nothing, and the TUTORIAL points users to that log.
  - The editor swallows a watcher start failure without a trace (`MainWindow.cs:1371-1372`).
  - **Action:** log "Windows SessionEnding: blanking" before `DisposePusher`, and log the result after it (with the `Stop` return value after the merge). Write the editor's failure to the status bar or a debug log.
- **N3. Misleading editor message:**
  - After `Closing` or a session end, `TryStartPusher` says "Windows is going to sleep or ending the session" (`MainWindow.cs:1380`) even when the cause was something else.
  - **Action:** use `_power.Ended ? "The session is ending." : "Windows is asleep."`.
- **N4. Restart Manager in the editor:**
  - If Restart Manager sends `WM_ENDSESSION` (`ENDSESSION_CLOSEAPP`) and Avalonia does not close the window, the editor keeps running but can never drive the display again (`Ended` is permanent).
  - **Action:** on `BlankAndStop` from `SessionEnding`, post `Close()` to the UI thread, as the runner posts `Quit`.
- **N5. Docs:**
  - `docs/WALKTHROUGH.md` §4.3 and §4.7 still say the display is blanked only on exit or pause.
  - **Action:** add one line pointing to `SessionPowerWatcher` (or to `docs/walkthroughs/shutdown-blank.md`).
- **N6. Obsolete constant:**
  - `PBT_APMRESUMECRITICAL` has not been sent since Windows Vista. It is harmless.
  - **Action:** add a comment saying it is kept for completeness.

---

## Merging with the sibling branches

| Branch | Overlap with this branch | Textual merge (trial) | Semantic notes |
|---|---|---|---|
| `fix-display-loop-693991` | `DisplayPusher.cs`, `Ax206Display.cs` (not touched here), `RunnerContext.Supervise`, `MainWindow` status timer and `ToggleSend` area | Clean | **M1** (`Stop` waits up to 18 s) and **M2** (`RestartDeadPusher` skips the gate). Blanking still happens synchronously in the normal case: `Stop(blank: true)` sets `_blankOnStop`, cancels, and the loop wakes from `WaitOne` at once, finishes its transfer and calls `Close()` → `Blank()` → `Dispose()` before `Stop` returns. If `Stop` times out, the blank happens later on the loop thread, which may be after the process has been killed. In the runner, the sibling's `!_pusher.IsAlive` branch comes after the `MayDrive` guard, which is correct. |
| `fix-live-empty-folder-5aa7c3` | `RunnerContext.Execute` (rewritten to `ControlCommand`), `MainWindow.PushLive`, `AGENTS.md` control-pipe paragraph | Clean | None. Commands that can open the display (`RELOAD`, `RESUME`) still go through `Supervise`. |
| `fix-autostart-task-0011d2` | `Runner/Program.cs` (this branch does not touch it) | Clean | The new task keeps `LogonType=InteractiveToken`, so the runner still runs in the user's interactive session and its hidden window gets the session messages. A task that ran "whether the user is logged on or not" (session 0) would **not** get them; keep it interactive. |
| `fix-saveas-assets-ee5527` | `Theme.cs` only | Clean | None |
| `fix-ffmpeg-elevation-a3dfcc` | One line in the `RunnerContext` constructor, `MainWindow.InstallFfmpeg` | Clean | None |

**Recommended merge order:** this branch first, then `fix-display-loop` with the M1 and M2 fixes in the merge commit (or in a follow-up commit right after it). Then the rest. After all six are merged, rebuild and run the tests. Merging them all together was not tried here.

---

## Test gaps

1. **The owner logic is not tested.** "`Supervise` does not open while `!MayDrive`", "session end → `Quit`" and "editor resume retries" live in `RunnerContext` and `MainWindow`, which have no tests. Removing the `MayDrive` guard at `RunnerContext.cs:149` fails no test. Move the owner state machine into Core (for example `PowerAwareDisplayOwner` with a pusher factory) and test it with the `IFrameDisplay` fake that arrives with `fix-display-loop`.
2. **The synchronous-delivery test is weak** (L6).
3. **No test for the time limit** of the power-event stop against a stalled fake display (M1).
4. **No test for a stuck suspend**, self-heal, or `SessionEnding` while suspended → quit (L1).
5. **No test** that the watcher survives `Dispose` while a handler is running, or that `Dispose` from the handler thread does not wait for itself (the code handles this at `SessionPowerWatcher.cs:143`; a test would protect it).
6. **No real-Windows test** that a hidden top-level window gets *broadcast* messages, as opposed to messages sent directly with `SendMessage`. The `GA_PARENT == desktop` assertion is a good stand-in. A real check needs the manual plan.

---

## Manual test plan (for the owner, on the real PC with the display attached)

Before each run, note the time and afterwards read `%AppData%\Glance\runner.log`.

| # | Setup | Action | Expected |
|---|---|---|---|
| 1 | Runner started by the scheduled task at logon, theme showing | Start → Power → **Shut down** | The display goes black and the backlight goes off within about 1 s, before the PC powers off. The log has `Windows SessionEnding: BlankAndStop`, then `Runner exiting`. The next morning the display is still dark. |
| 2 | Same | Start → Power → **Restart** | It blanks at restart. After logon the runner starts and the theme comes back. |
| 3 | Same | **Sign out** | It blanks. It stays dark at the sign-in screen. After signing in again the theme comes back. |
| 4 | Same | **Sleep** (Start → Power → Sleep), wait 1 minute, wake | It blanks before the PC sleeps. After wake the theme is back within about 2-5 s. The log has `Windows Suspend: BlankAndHold`, then `Windows Resume: Reopen`, then `Display opened`. |
| 5 | Same, tray **Pause display** first | Sleep and wake | It stays black after wake (still paused). Tray **Resume display** brings it back. |
| 6 | Runner running | Open Notepad, type text (unsaved), shut down, then click **Cancel** on the "apps are preventing shutdown" screen | The runner keeps showing the theme (a cancelled shutdown sends `WM_ENDSESSION` FALSE, which is ignored). If Windows still sent TRUE to the runner, it blanks and exits. Note which happened. |
| 7 | **No runner**; editor open with **Send to display** ticked | Shut down | It blanks. The editor closes (probably from `Closing`, see L3). |
| 8 | No runner; editor sending | Sleep, then wake | The status says "Display off while Windows sleeps". After wake, sending resumes within about 2-8 s ("Sending to the display..."). |
| 9 | No runner; editor sending; unplug the display during sleep | Wake, wait 10 s, plug it in | Today the editor gives up after about 7.5 s and unticks Send (L2). The runner, in the same test, picks the display up again when it is plugged in. |
| 10 | Editor sending live **through** the runner | Sleep, then wake; shut down | The runner handles both. The editor does not try to open the display itself (`_liveToRunner`). |
| 11 | Neither runner nor editor running | Shut down | The display keeps its last frame. That is expected and documented: nothing is running to blank it. |
| 12 | Runner running | From an admin prompt run `shutdown /s /t 0`, and on another occasion `shutdown /r /t 0` | Same as 1 and 2. |
| 13 (optional) | Runner running | Upgrade with the installer while the runner runs (Restart Manager closes it) | It blanks, the runner exits, and the installer does not have to kill it. |

This PC supports S3 sleep only (`powercfg /a`), so hibernate, hybrid sleep, Fast Startup and Modern Standby cannot be tested here.
If the change is shipped to other PCs, repeat 1 and 4 on a laptop with Modern Standby and on a PC with Fast Startup enabled.

---

## What was checked and found sound

- **The right kind of window.** `CreateWindowExW` with no parent and no `WS_VISIBLE` (`SessionPowerWatcher.cs:91`) makes a hidden **top-level** window, and the test checks that its parent is the desktop window. Hidden top-level windows get `WM_QUERYENDSESSION`, `WM_ENDSESSION` and `WM_POWERBROADCAST`; message-only (`HWND_MESSAGE`) windows do not. Before this change the runner had no top-level window of its own that handled them. `_ui` is a parentless WinForms `Control`, which WinForms parks under a message-only parking window, and the `NotifyIcon` window passes everything to `DefWindowProc`.
- **Its own thread and message loop.** The handler does not depend on the runner's UI thread (WinForms) or the editor's dispatcher (Avalonia) being free. `SystemEvents` was rightly avoided: its events can be marshalled to other threads, which makes "finish before answering" hard to guarantee.
- **The elevated runner from a scheduled task.** The task uses `InteractiveToken` (on `main` and on `fix-autostart-task`), so the runner lives on the user's interactive desktop and its window gets the session messages. User Interface Privilege Isolation (UIPI) does not filter these system messages.
- **Synchronous blanking.** `Raise` calls the handler inline inside the window procedure, and the window procedure answers only afterwards (`return 0` for `WM_ENDSESSION`). The runner and the editor both blank inside the handler and only *post* follow-up work (`BeginInvoke(Quit)`, `Dispatcher.UIThread.Post`), so they never wait on the UI thread.
- **`ShutdownBlockReasonCreate`/`Destroy`** are called on the thread that owns the window, are paired in `finally`, and are not used for sleep.
- **`WM_QUERYENDSESSION`** always returns TRUE, so the app never blocks shutdown. `WM_ENDSESSION` with FALSE (a cancelled shutdown) is ignored.
- **Duplicate notifications.** `PBT_APMRESUMEAUTOMATIC` followed by `PBT_APMRESUMESUSPEND` reopens only once. Duplicate suspends and session ends act once.
- **Fast Startup (hybrid shutdown)** logs the user session off, so the same `WM_ENDSESSION` path runs. Nothing extra is needed.
- **Races in the runner.**
  - `DisplayPowerPolicy.Handle` and `DisposePusher` run under `_gate`, and so does the `MayDrive` check in `Supervise`. The check and the open cannot interleave with an event.
  - The file-watcher reload (`Reload` → `Supervise`) and the pipe commands `RELOAD`, `RESUME` and `PAUSE` all funnel into `Supervise`.
  - `Quit` runs only on the UI thread (tray **Exit**, `EXIT` through `BeginInvoke`, session end through `BeginInvoke`). `_quitting` makes it idempotent, and `Handle(Quit)` returning `None` after a session end prevents a second blank. Because `_pusher` is already null, a second blank was impossible anyway.
- **Races in the editor.**
  - `ReleasePusher` uses `Interlocked.Exchange`, so exactly one caller disposes.
  - `TryStartPusher` checks `MayDrive` under `_powerGate`, the same lock the event handler holds.
  - `ApplyPins` reads `_pusher` once.
  - `_resumeAfterSleep` is touched only on the watcher thread.
  - No lock cycle: the watcher thread never waits on the UI thread, and the pusher loop does not need either lock.
- **Life cycle.** The delegate passed to Windows is kept in a field (so the GC cannot collect it). The window class name is unique per instance (a GUID), so tests and two owners in one process cannot collide. `Dispose` posts `WM_CLOSE`, waits up to 1 s, and skips the wait when called on the watcher's own thread.
- **Resume reopens through the normal path.** The runner's `Supervise` (immediately, then every 2 s) and the editor's `TryStartPusher` both reopen this way. Nothing reuses a USB handle that was open across sleep, because the pusher is disposed at suspend.
- **Docs.**
  - The AGENTS.md paragraph matches the code.
  - The TUTORIAL log hint matches the real log line (`$"Windows {e}: {action}"` → `Windows SessionEnding: BlankAndStop`).
  - "Reload theme" in the troubleshooting text is correct when the device simply did not come back. It does not help in the stuck-suspend case (L1).

---

## Proof: the bug existed and is fixed (partly provable here)

**The bug** (reported by the user): after shutting down from the Start menu, the display kept its last frame all night.

**Evidence on unfixed `main` (`0a9f10b`).** Searching all of `src` for every session-end or power hook gave **no matches**:

```
git grep -n -E "ENDSESSION|QUERYENDSESSION|SessionEnding|SessionEnded|PowerModeChanged|POWERBROADCAST|ShutdownBlockReason" main -- src
(nothing)
```

On `main`, the runner blanks the display in only two places, both on its own UI paths:
- `RunnerContext.cs:145`, Pause.
- `RunnerContext.cs:284`, `Quit()` (tray Exit or the `EXIT` command).

When Windows ends the session, it terminates the process without running either one. So the frame stays on a display whose USB port is still powered, which matches what the user saw.

**After the fix (`df02e6d`):**
- The same search finds the new hooks: `SessionPowerWatcher.cs` (15 matches) and `DisplayPowerPolicy.cs`.
- The 20 new `PowerTests` pass (`Passed: 20, Total: 20`; the full suite is 273/273). They check:
  - that the watcher window is a real top-level window, so Windows broadcasts reach it;
  - the shutdown, sleep and resume decisions;
  - that nothing blanks twice;
  - that the display is not reopened after a session-end blank.

**Not provable here:** a real shutdown, restart, sign-out or sleep, and what the physical display shows. To confirm the bug is gone, follow the manual test plan above. The short version: with the runner running, shut down from Start and check that the display goes black; then sleep the PC, wake it, and check that the theme comes back.

---

## Fixes after review (phase 1)

**Date:** 2026-11-26. **By:** the author of the change (not the reviewer). The findings above are unchanged.
M2 is left for phase 2 (it needs `claude/fix-display-loop-693991` merged first). This round does not use that branch's
`Stop(bool blank, TimeSpan timeout)` yet. The bounded stop lives in this branch's own `PowerBlank.RunWithDeadline` and will move to
the public timeout during integration.

**Method (the user's rule for every bug):**
1. The new API was first added as **stubs that behave like the reviewed code**: `PowerBlank.Run` locked and stopped without a limit,
   the watcher ignored its budgets, `NoteAwakeTick` did nothing, `ResumeRetry` was 5 × 1.5 s, and the policy still returned `None`
   for a session end while asleep.
2. The new tests were run against that, and the failures were captured (`dotnet test --filter "FullyQualifiedName~Power"`:
   **Failed: 8, Passed: 21, Total: 29**).
3. Then the fixes were made, and the same tests ran again (**Passed: 29, Total: 29**). The full suite is **282/282** (273 before this round).
   `dotnet build src/Glance.sln -c Release -p:SkipHost=true` succeeds. The `CA1416` warning for `SessionPowerWatcher` is gone, and no new warnings were added.

### Proof per finding

| Finding | Test | Before (stub = reviewed behaviour) | After |
|---|---|---|---|
| M1 (stalled display) | `PowerBlankTests.Gives_up_on_a_stalled_display_within_its_budget` | `Assert.Equal() Failure: Expected: TimedOut, Actual: Blanked` after **5 s** | Passed in 324 ms |
| M1 (busy owner lock) | `PowerBlankTests.Does_not_wait_for_ever_behind_a_busy_owner` | `Expected: Busy, Actual: NothingOpen` after **5 s** (it waited for the lock) | Passed in 127 ms |
| M1 / L4 (Windows always answered, block reason always released) | `SessionPowerWatcherTests.Windows_gets_its_answer_within_the_budget_even_if_the_handler_hangs(sessionEnd: True)` | `answered after 5007 ms` | Passed in 335 ms, no block reason left |
| M1 (sleep) | same test, `(sessionEnd: False)` | `answered after 5009 ms` | Passed in 342 ms |
| M1 (budgets) | `PowerBlankTests.Budgets_fit_inside_what_Windows_allows` | (passed: only checks the constants) | Passed |
| L1 (session end while asleep) | `DisplayPowerPolicyTests.Session_end_while_asleep_still_stops_so_the_owner_exits_and_wake_does_not_reopen` (replaces `Session_end_while_asleep_needs_no_second_blank_and_wake_does_not_reopen`, which asserted the buggy `None`) | `Expected: BlankAndStop, Actual: None` | Passed |
| L1 (missed wake notice) | `DisplayPowerPolicyTests.A_missed_wake_notice_heals_after_enough_awake_ticks` | `Expected: Reopen, Actual: None` | Passed |
| L1 (heal rules) | `DisplayPowerPolicyTests.Awake_ticks_do_nothing_when_awake_or_after_the_session_ended_and_restart_with_each_sleep` | `Expected: Reopen, Actual: None` | Passed |
| L2 (editor gives up too early) | `PowerBlankTests.Wake_retries_last_long_enough_for_a_hibernate_resume` | `retries for 7,5 s only` | Passed (1.5 s, 3 s, then 11 × 5 s = 59.5 s) |
| L6 (sync test too weak) | `SessionPowerWatcherTests.Handler_has_finished_when_the_window_answers` (150 ms handler) | Passed on the reviewed code, which is correct because it is synchronous. With `Raise` changed to `ThreadPool.QueueUserWorkItem(_ => _handler(e))` (the reviewer's mutation), it **failed**: `Expected: [SessionEnding], Actual: []`. The old `Messages_reach_the_handler_...` test still passed on that mutation, which reproduces L6 | Passed |

**Mutation checks on the fixed code** (each change was reverted byte for byte afterwards):

| Mutation | Tests that failed |
|---|---|
| A. The watcher answers without waiting for the handler (thread pool) | `Handler_has_finished_when_the_window_answers`, `Messages_reach_the_handler_...`, `A_shutdown_block_reason_is_set_only_...` |
| B. The block reason is not removed when the handler overruns | `Windows_gets_its_answer_..._hangs(sessionEnd: True)`, `A_shutdown_block_reason_is_set_only_...` |
| C. `PowerBlank.Run` waits for the owner's lock without a limit | `Does_not_wait_for_ever_behind_a_busy_owner` |
| D. `RunWithDeadline` waits for the stop without a limit | `Gives_up_on_a_stalled_display_...`, both `Windows_gets_its_answer_..._hangs` |
| E. The wake retries go back to 7.5 s | `Wake_retries_last_long_enough_for_a_hibernate_resume` |
| F. No self-heal | `A_missed_wake_notice_heals_...`, `Awake_ticks_do_nothing_...` |
| G. A session end while asleep returns `None` again | `Session_end_while_asleep_still_stops_...` |

### What changed, per finding

- **M1:**
  - `SessionPowerWatcher.Raise` now runs the handler on a helper thread and waits at most `PowerBlank.BudgetFor(e)`: 4 s for `WM_ENDSESSION`, 1.5 s otherwise. The constructor can override this for tests.
  - The shutdown block reason is removed in `finally`, so it always goes when Windows gets its answer.
  - Owners update the policy first. The policy has its own lock, so `MayDrive` turns false at once. Then they call `PowerBlank.Run(gate, take, OwnerBudgetFor(e))`:
    - It waits for the owner's lock for at most a third of the budget.
    - `take` detaches the pusher under the lock.
    - The stop and blank run outside the lock, with the remaining time as the deadline.
  - Results are `Blanked`, `NothingOpen`, `TimedOut` (the blank goes on in the background) or `Busy`.
  - In the runner, `Busy` sets `_blankOwed`. Whoever holds `_gate` then pays it in `PayOwedBlank`: `Supervise`'s `!MayDrive` branch (a reload ends in `Supervise`) and `Quit`. It uses `Ax206Display.TryBlank` if the pusher is already gone.
  - Both `Supervise` and the editor's `TryStartPusher` check `MayDrive` again right after opening, and close a display that was opened during a power event.
  - In phase 2, `PowerBlank.RunWithDeadline(() => p.Stop(true), ...)` becomes the display-loop branch's `Stop(true, timeout)`.
- **L1:**
  - The first session end or quit now always returns `BlankAndStop`, even while asleep.
  - The runner requests `Quit` for every `SessionEnding`.
  - `DisplayPowerPolicy.NoteAwakeTick` runs on the runner's 2 s supervisor timer and heals a stuck sleep after 15 awake ticks (30 s). WinForms timers do not tick while the PC sleeps.
  - Tray **Reload theme**, tray **Resume display** and the `RESUME` command (all through `SetPaused(false)`) clear a stale sleep state at once.
  - A display replug is covered by the 30 s heal.
  - The automatic file-watcher reload does *not* clear it. A file change in the seconds between `PBT_APMSUSPEND` and the real sleep must not relight the display.
  - The TUTORIAL troubleshooting text was rewritten to match.
- **L2:** the editor uses `ResumeRetry.Delays()` (about 60 s). It shows "Waiting for the display after wake..." meanwhile and unticks **Send to display** only at the end.
- **L3:**
  - The editor's `Closing` now detaches the pusher and blanks it on a background task (`PendingExitBlank`), so the UI thread stays free.
  - `Program.Main` waits for that task after the Avalonia lifetime ends, for at most `PowerBlank.SessionEndBudget`.
  - The watcher path is bounded by `PowerBlank.Run`.
  - AGENTS.md now says that Avalonia's `Closing` usually does the session-end blank in the editor, and the watcher is the backup.
- **L4:** the block reason stays in `WM_ENDSESSION`, deliberately (the reasons are in the `SessionPowerWatcher` doc comment):
  - At `WM_QUERYENDSESSION` the shutdown can still be cancelled and nothing is being done yet. A registered reason would put Glance on Windows' "apps are preventing shutdown" screen for nothing.
  - Inside the bounded `WM_ENDSESSION` handler it can never hold a shutdown for longer than the 4 s budget.
  - If blanking is slow, it names the work instead of leaving an anonymous hang.
  - Manual check 14 below covers this.
- **L5:** documented as out of scope in AGENTS.md and the TUTORIAL: Modern Standby timing, monitor off and lock. `GUID_CONSOLE_DISPLAY_STATE` support would need a new event and probably a setting, so it is not trivial.
- **L6:** the new slow-handler test, proven above.
- **N1:** `[SupportedOSPlatform("windows")]` is on `SessionPowerWatcher` and `SessionPowerWatcherTests`. The editor guards the calls with `OperatingSystem.IsWindows()`.
- **N2:** the runner logs `Windows {e}: {action}` *before* blanking and `Windows {e}: blank {result}` after it. The editor reports a watcher start failure in the status bar and in `Trace`, and traces each power event.
- **N3:** the editor's "cannot open now" message depends on the state: "The editor is closing or Windows is ending the session." or "Windows is going to sleep.".
- **N4:** on `SessionEnding` the editor posts `Close()` to the UI thread (installers or Restart Manager).
- **N5:** `docs/WALKTHROUGH.md` §4.3 and §4.7 now point to the shutdown and sleep blank.
- **N6:** `PBT_APMRESUMECRITICAL` was removed, with a comment explaining why.

### Still open

- **M2** (phase 2): rewrite `RestartDeadPusher` through `TryStartPusher` when `fix-display-loop` is merged, and switch to its public `Stop(blank, timeout)`.
- **The owner logic in the runner and editor is still not unit tested** (test gap 1): `Supervise`'s guards, `PayOwedBlank`, `Quit` on session end, and the editor's retry loop. Their decisions now sit in tested Core pieces (`DisplayPowerPolicy`, `PowerBlank`, `ResumeRetry`), but the wiring is only checked by reading it and by the manual plan.
- **`docs/walkthroughs/shutdown-blank.md`** describes the code as reviewed. It does not mention `PowerBlank`, the self-heal or the bounded handlers yet.
- **The manual test plan above still applies.** Add:
  - **14.** With the runner running, unplug the display while it shows a frame and shut down at once. Windows must not wait more than about 4 s for Glance. `runner.log` shows `blank TimedOut` or `blank Blanked`.
  - **15.** Sleep and wake, then check that `runner.log` has no "No wake-up notice" line. That line would mean the wake notice was missed and the 30 s heal fired.


---

## Phase 2: merged display loop

**Date:** 2026-11-26. **By:** the author of the change. The reviewer's findings above are unchanged.

### The merge

- **Merged:** `claude/fix-display-loop-693991` at `3af052b`, with a merge commit (no rebase).
- **Merged in automatically:** `DisplayPusher.cs`, `IFrameDisplay.cs`, `Ax206Display.cs`, `Theme.cs`, `Glance.Core.csproj` (`InternalsVisibleTo`), the display-loop tests and its review and walkthrough.
- **Three conflicts were resolved by hand:**

| File | Conflict | Resolution |
|---|---|---|
| `AGENTS.md` | Both branches added a paragraph to "The display" | Kept both, then updated the shutdown paragraph to describe the merged design |
| `src/Glance.Runner/RunnerContext.cs` (`Supervise`, open path) | This branch's "re-check `MayDrive` right after opening" against the display-loop branch's "do not open while `_closingPusher` is not `IsClosed`" | Both: the open happens only when the old pusher has let go, and the re-check follows it. The re-check closes with `QuickStopTimeout` |
| `src/Glance.App/MainWindow.cs` (`TurnDisplayOff`, `ToggleSend`) | This branch's `ReleasePusher`/`TryStartPusher` against the display-loop branch's `StopPusher`/`DisplayStillClosing`/`RestartDeadPusher` | Replaced by one Core class, `PusherSlot`, below. `_pusher` is now a read-only view of `PusherSlot.Current` |

- **Also adapted, without a conflict:**
  - `DisposePusher` in the runner takes an optional timeout and records a stuck pusher through `RememberClosing`.
  - `PayOwedBlank` uses the quick stop. If the old pusher is still closing, it asks that loop to blank as well (`Stop(true, TimeSpan.Zero)`) instead of opening the device.

### M2 and the switch to the public `Stop` timeout

- **New `PusherSlot` (Core)** owns the editor's display:
  - `TryOpen` refuses while `!MayDrive` and while a stopped pusher still holds the device. It checks `MayDrive` again right after opening.
  - `Release` detaches the pusher with `Interlocked.Exchange` and calls `Stop(blank, timeout)`. When that returns false, the pusher is remembered as still closing.
  - `BlankForPowerEvent(e)` calls `PowerBlank.Run`, using the owner budget.
  - `RestartIfDead` is the M2 fix:
    - `Interlocked.CompareExchange(ref _current, null, dead)` detaches the dead pusher; if someone else released it first, it does nothing.
    - It stops the dead pusher (blank only when not allowed to drive).
    - It reopens through `TryOpen`, so the same policy and "still closing" checks apply.
    - The result is `NotNow` while asleep or after a session end; the editor then lets the wake-up path reopen it.
- **The editor's `RestartDeadPusher`** is now a thin switch over `RestartIfDead`.
- **The runner's dead-loop restart** was already behind the policy: the `!MayDrive` guard comes before the `ShouldReopen` branch in `Supervise`, all under `_gate`. A comment now says so. A new open is followed by the `MayDrive` re-check.
- **`PowerBlank.Run`** now takes the pusher, not an `Action`, and calls the display loop's public `DisplayPusher.Stop(blank: true, timeout)`:
  - It waits at most a third of the budget for the owner's lock.
  - It stops with the rest of `OwnerBudgetFor(e)`: 3.7 s for a session end, 1.2 s for sleep.
  - **When `Stop` returns false**, it calls `stillClosing(p)`. The editor stores the pusher in the slot; the runner stores it in `_closingPusher`. Neither reopens the display until `IsClosed`, so the display is never reopened straight away.
- **Removed:** my own "stop with a deadline" helper, `PowerBlank.RunWithDeadline`. `SessionPowerWatcher` keeps a small private bound on the *handler*, so Windows always gets its answer and the block reason is always removed. It is no longer used to stop the display.
- **Not used:** `DisplayPusher.QuickStopTimeout` (2 s) for the power path, because it is longer than the 1.5 s sleep budget. It is used where only the UI thread waits (closing the editor, re-checks after opening).

### Proof

Tests first, against the merged code before the fix. `RestartIfDead` was a copy of the merged `RestartDeadPusher` (stop the dead pusher, open a new `DisplayPusher` directly). `BlankForPowerEvent` was the phase-1 path (the default `Stop` inside a deadline, with nothing remembered):

| Test | Before | After |
|---|---|---|
| `DeadPusherRestartTests.A_dead_pusher_is_not_restarted_while_Windows_sleeps_or_after_the_session_ended(e: Suspend)` | **Failed:** `Expected: NotNow, Actual: Reopened` | Passed |
| same, `(e: SessionEnding)` | **Failed:** `Expected: NotNow, Actual: Reopened` | Passed |
| `PowerBlankTimingTests.A_stalled_display_is_released_within_the_budget_and_not_reopened_while_it_is_still_held(e: SessionEnding)` | **Failed:** `Assert.NotNull() Failure: Value is null`. Timed out in 3701 ms, but the still-held device was not remembered | Passed: `TimedOut after 3713 ms (budget 4000 ms)` |
| same, `(e: Suspend)` | **Failed:** the same, after 1200 ms | Passed: `TimedOut after 1214 ms (budget 1500 ms)` |
| `PowerBlankTimingTests.After_a_stuck_sleep_blank_the_display_is_reopened_only_once_the_old_loop_lets_go` | **Failed:** `Assert.False() Failure`. It reopened straight after wake, while the old loop still held the device | Passed |
| `DeadPusherRestartTests.A_dead_pusher_is_restarted_while_awake` | Passed | Passed |
| `DeadPusherRestartTests.A_dead_pusher_already_released_by_a_power_event_is_left_alone` | Passed | Passed |
| `PowerBlankTimingTests.A_working_display_is_blanked_well_within_the_budget` (both events) | Passed: 406 / 405 ms | Passed: **407 ms** (session end), **405 ms** (sleep), exactly one blank, display closed |

Fakes: the display-loop branch's `FakeDisplay`. A stalled display has `BlitDelayMs = 10 000`. A working one has `BlitDelayMs = 400`, about the real full-frame time. A dead loop has `SizeThrows = true`.

The phase-1 `PowerBlankTests` now drive real pushers over `FakeDisplay`. A stalled stop gives up within a 300 ms budget and reports the pusher as still closing. A normal stop blanks once and closes.

**Mutation checks:**

| Mutation | Result |
|---|---|
| `TryOpen` ignores the power policy | The 2 M2 tests fail |
| A timed-out power stop is not remembered | The 3 "stalled" tests fail |
| `RestartIfDead` clears `_current` without the compare-exchange | **No test fails** |

The compare-exchange is defensive. Today every caller that could race with it either runs on the same UI thread (`Release`, `TryOpen`) or updates the policy first (the power event), so `TryOpen` refuses anyway. A deterministic test would need a hook inside the method, so this is left as a known gap.

**Totals:**
- `dotnet build src/Glance.sln -c Release -p:SkipHost=true` succeeds with 0 errors. No new warnings in the changed files.
- `dotnet test src/Glance.Tests -c Release` was run **3 times in a row**, because these tests depend on timing: **310/310** each time, about 8 s.

### Changes to the manual test plan

The 13 steps above still apply. Changes and additions:
- **Step 9** (editor sending, display unplugged during sleep): the editor now keeps trying for about 60 s after wake (phase 1, L2), not 7.5 s. Plugging the display in within that minute brings it back.
- **Step 14** (phase 1, the display unplugged mid-frame, then shut down): `runner.log` now shows `Windows SessionEnding: blank TimedOut` followed by `The display did not stop in time; it will close when its last transfer gives up`. Windows should not wait more than about 4 s.
- **16 (new):** with the runner running, unplug the display, sleep at once, wake, and plug it back in. The runner must not log "busy" errors. It waits for the old loop ("the display is finishing its last transfer") and then reopens within a few seconds.
- **17 (new, the editor without the runner, M2):** hard to trigger by hand, because the loop dying needs a fault inside the loop. It is covered by the `DeadPusherRestartTests`; nothing to do by hand.

### Still open

- The runner's wiring (`Supervise`, `PayOwedBlank`, `Quit`) has no unit tests. The editor's display logic does now (`PusherSlot`).
- `docs/walkthroughs/shutdown-blank.md` still describes the phase-0 code.

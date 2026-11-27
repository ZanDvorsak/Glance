# Review: live theme lease, ordered tray link, tray detection (fix for M5 and M13)

- **Verdict:** merge after fixes (M1, M2 are small; M3 must be done when merging with the deadlock fix)
- **Build/tests:** build 0 errors (162 warnings, CA1416/nullable notes); 817/817 passed in 3 runs in a row (~16 s each)
- **Proof:**
  - Before: at `eb7ff86` (extraction only) the 17 original `LiveLeaseTests` gave **12 failed, 5 passed**, exactly the failures listed in `docs/design/fix-live-lease-pipes.md` (LIVE_CLEAR applied before LIVE, 3 connections for 3 commands, heartbeat `ERR unknown command`, presence by process name); rest of the suite 791/791
  - Before: the 8 tests for new API do not compile there (expected); the extraction changed no tray/link behaviour except two small things (nits)
  - After: all 25 `LiveLeaseTests` pass at `ff0ebee`; every "obsolete" claim checked on `main` (all named tests, `DisplayAccess` message, `LogLimiter`, installer `taskkill` of editor and tray exist)
- **Kept:** tray pipe rules (current user only, NETWORK denied, `FirstPipeInstance` via `ServicePipeFactory`, owner check, remote clients closed, max 4); client `CurrentUserOnly` + `Anonymous`; nothing new waits on async code on a UI thread (`Send`/`Probe` only run in `Task.Run`; exit wait runs after the dispatcher stopped)

## Findings
- **Medium M1** — Two editors that are both live now fight every ~1.5 s: a `LIVE` from another connection takes the lease, the first editor's `LIVE_KEEP` gets `ERR no live theme`, `Lost()` resends, and so on; the display flips between two themes (`TrayLiveState.cs:59`, `MainWindow.cs:1469`; the editor has no single-instance guard) → answer `LIVE_KEEP` differently when another connection holds the theme (e.g. `ERR live taken`) and let that editor stop live with a message instead of resending; test with two `TrayLink`s
- **Medium M2** — When the lease ends (tray restart, paused/hung editor, wake from sleep) the editor resends only `LIVE`; the page pin and brightness from `StartLive` are not sent again, so the pin is silently lost. A `PAGE` with no `LIVE` that sticks (editor gone between them, or `LIVE` rejected) sets a pin that no connection owns and never ends (`TrayLiveState.cs:64`, `MainWindow.cs:1469`) → on `Lost()` resend `PAGE` + `BRIGHTNESS` before `LIVE` (or carry the pin in `LIVE`), and tie the pin to the sending connection like the theme
- **Medium M3 (merge)** — `claude/fix-tray-deadlock-a14e66` makes CA2007 an error in Core: a trial build of this branch with its `Core.globalconfig` fails on 4 awaits in `TrayControlServer.cs:58,68,88,92`, and `TrayLink.cs:80` (`await foreach`) will trip its source-scan test → add `.ConfigureAwait(false)` (also on `ReadAllAsync()`)
- **Low L1** — Lease and heartbeat use wall-clock `DateTime.UtcNow` (`TrayLiveState.cs:32`, `MainWindow.cs:1465`, `LiveKeepAlive.cs:23`): a clock set back stops heartbeats and expiry until it catches up; a forward jump or wake from sleep ends the lease on the first tick, so the saved theme flashes before the resend → use `Environment.TickCount64`/`Stopwatch`, and renew the lease on resume
- **Low L2** — `ToggleSend` decides on a cached presence up to ~3 s old, `Unknown` for ~1 s after start (`MainWindow.cs:1554`): start the tray and tick Send at once and the editor opens the display next to the tray; the first click after start says "Still looking" → probe fresh off the UI thread when Send is ticked (the ui-thread branch's `_displayWork.Run(...)` with `RunnerClient.Probe`)
- **Low L3** — A "dropped" connection is not closed at once: the link's pipe is synchronous, so `Dispose` waits for the pending read (SafeHandle ref count) and the tray keeps that connection until it replies; a hung tray collects up to 4 (`TrayLink.cs:102`, by reading, not run) → open with `PipeOptions.Asynchronous` and cancel the read before disposing
- Nits: LIVE parsing moved inside the tray's `_gate` and presence starts `Unknown` (extraction was not 100% behaviour-free); order is guaranteed for receipt, but `PAUSE`/`RESUME`/`RELOAD` are still applied later via `BeginInvoke`; `FakeTray` re-implements `RunnerContext`'s wiring, so `ExpireLive` in `Supervise` and STATUS renewing the lease are untested (only a source-text check, which is brittle); note says 158 warnings, a fresh build shows 162

## Merge conflicts
- `claude/fix-tray-deadlock-a14e66`: conflict in `RunnerContext.cs` (fields; display open moved to `BackgroundOpener`): its `Opened` must use `_liveState.PinnedPage`, and its `_live`/`_pinPage`/`ControlLoop` code must go to `TrayLiveState`/`TrayControlServer`; plus M3
- `claude/fix-ui-thread-280814`: conflicts in `MainWindow.cs` (6 hunks), `ServiceWindow.cs`, `RunnerContext.cs`; it still calls the removed `RunnerClient.IsRunning` (`_trayProbe`, `StartSendingOrLiveAsync`, `TurnDisplayOff`, `CurrentSetupContext`) → use `RunnerClient.Probe()` on `_displayWork` and `Presence`, which also fixes L2

## Manual checks for the user
- Tray running, editor live with an unsaved change, end `Glance.App.exe` in Task Manager: the saved theme is back within ~1 s (`runner.log`: "its connection closed")
- Pause `Glance.App.exe` in a debugger while live with a page pinned: saved theme within ~7 s; after resuming, check the edits and the pin both come back (M2 expects the pin not to)
- Two editors, both with Send to display ticked: watch for the display flipping between them (M1)
- Exit and restart the tray while live, and sleep/wake the PC while live: edits return within a few seconds
- Tray of another account (`runas`) in your session: Send to display says "another account" and does not fight it

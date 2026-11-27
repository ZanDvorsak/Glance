# Fix: live theme lease, ordered control channel, tray detection (review M5, M13)

Branch `claude/fix-live-lease-pipes-4301b2`, from `main` at 8d2d13c (the Windows service design, `docs/design/runner-service.md`).
Findings: `docs/reviews/review-full-codebase.md` M5 and M13. Both were written against the old elevated runner; this note first
checks every part against the current code, lists what the redesign already removed (with the evidence), then proves and fixes
what was left.

## 1. Re-assessment against `main` (8d2d13c)

### M5: a stale unsaved live theme can stay on the display

| Part of the finding | Still there on `main`? | Evidence |
|---|---|---|
| `LIVE_CLEAR` is sent fire-and-forget when the editor closes | **Yes** | `MainWindow.cs` `Closing` -> `StopLive()` -> `Task.Run(() => RunnerClient.Send(LiveClear))`. `Program.Main` waits only for `PendingExitBlank`, so the process can end before the task even connects. |
| Each command uses its own connection, so the order is not guaranteed | **Yes** | Every `RunnerClient.Send` opened a new `NamedPipeClientStream`, each from its own `Task.Run` (`StartLive`, `StopLive`, `ApplyPins`, brightness slider, `PushLive`). The tray handles each connection on its own task (`RunnerContext.Handle`). A `LIVE` that waits for the tray's lock (up to ~2 s while it reopens the display) is overtaken by a `LIVE_CLEAR` sent after it. |
| `_live` and `_pinPage` never expire | **Yes** | `RunnerContext.cs` set them on `LIVE`/`PAGE` and cleared them only on `LIVE_CLEAR`. An editor that crashed, was killed, or lost its `LIVE_CLEAR` left the unsaved theme (and its pinned page) until the tray restarted. |
| `Reload()` does not clear them, so saving does not help | Yes, **kept on purpose** | While the editor is live, a save reloads the tray and the editor keeps streaming the same theme; clearing it there would flicker back to the saved file for a moment. With the lease below, "saving does not help" no longer matters: the live theme ends by itself within seconds once the editor is gone. |
| "The installer kills the editor" | Obsolete as a separate case | The installer kills trays and editors together (`Glance.iss`, review L4 of the service review), so no tray is left holding the theme. Covered anyway by the lease. |

M6 (a failed live send is never retried) is not part of this task; it was fixed earlier by `LiveSendGate` (`LiveSendTests`).

### M13: machine-wide pipes, "runner running" by process name

| Part of the finding | Still there on `main`? | Evidence |
|---|---|---|
| Pipe names are global; neither side uses `CurrentUserOnly` | **Obsolete** for the tray pipe | Name is `Glance.Runner.Control.<session>.<user SID>` (`ServiceNames.TrayControlPipe`; test `ReviewFixTests.L8_the_tray_pipe_name_carries_the_session_and_the_user`). Server DACL is current user only, NETWORK denied (`ServicePipeSecurity.CurrentUserOnly`; `L1_the_tray_control_pipe_denies_the_network`). Client connects with `PipeOptions.CurrentUserOnly` + `Anonymous` (`T1_the_editor_only_talks_to_a_tray_app_of_its_own_user`, `DisplayServiceTests.A_squatter_on_the_tray_control_pipe_cannot_act_as_the_user`). |
| (same, service pipes) | **By design**, protected | `Glance.Service.Display` and `Glance.Sensors` are one per machine because there is one service. They have an explicit DACL, `FirstPipeInstance`, and clients check the owner is SYSTEM/Administrators (`DisplayServiceReviewTests.M1_*`, `DisplayServiceTests.A_squatted_display_pipe_is_not_used`, `The_sensor_client_ignores_a_feed_that_is_not_served_by_the_service`). |
| `IsRunning` looks at process names in every session | **Half fixed, rest still there** | `RunnerClient.IsRunning` already filtered by session, but still decided by the process name alone. A `Glance.Runner` process that does not answer this editor (another account in the same session, one still starting, one whose pipe is taken) counted as "running": the editor then streamed into nothing and never drew itself. |
| A runner elevated as a *different admin account* uses that account's SID and `%AppData%`; the editor thinks it runs | Elevation part **obsolete**; the "other account" part **still there** | Nothing runs elevated any more: no `requireAdministrator` (`RunnerServiceTests.No_Glance_program_asks_for_administrator_rights`), and an elevated tray relaunches itself as the user or refuses (`ReviewFixTests.M3_the_tray_does_not_run_elevated_when_it_could_run_as_the_user`). But a tray started as another account in this session (for example with `runas`) still made `IsRunning` true. |
| A second signed-in user's runner cannot create the pipe and logs every second | **Obsolete** | Per-session-and-user names (above), the first-instance flag (`ServicePipeFactory`; `DisplayServiceReviewTests.M1_L8_a_pipe_name_someone_else_serves_is_refused_by_every_Glance_server`), and the error line goes through `LogLimiter` (once a minute). Kept by the move into Core: new guard `LiveLeaseTests.The_tray_control_server_does_not_join_a_squatted_name`. |
| Another account that creates the pipe name first receives the editor's theme and paths | **Obsolete** | `CurrentUserOnly` on the client (T1) means .NET refuses a server end owned by another account; `Anonymous` means a squatter cannot act as the editor. The new client (`TrayLink`) uses the same options: new guard `M13_a_squatter_on_the_tray_pipe_cannot_act_as_the_editor_through_the_link`. |
| A clear message when the display belongs to another signed-in user | **Obsolete** (already there) | The service refuses any session that is not at the console with a reason: "Another user is at the console. Only the session at the console (session N) may use the display" (`DisplayAccess.Deny`; `RunnerServiceTests.Only_the_console_session_may_drive_the_display`, `DisplayServiceTests.Another_session_is_refused_with_a_reason_and_never_opens_the_device`). The tray shows it as "Waiting for the display: ..." and the editor in its status bar (`PusherSlot.TryOpen` passes the message on). |
| A clear message when the *tray* belongs to another account | **Still missing** | Fixed below (`TrayState.OtherAccount`). |

## 2. Proof on the current code

Method (as in `runner-service.md`): first a commit that only moves the code into Core with **today's behaviour**
(eb7ff86: `TrayControlServer`, `TrayLiveState`, `TrayLink`, `TrayPresence`; build green, 791/791 tests), so the old behaviour can be
tested. Then the tests (`src/Glance.Tests/LiveLeaseTests.cs`), run against that commit, then the fix, then the same tests again.

**Before** (eb7ff86 + the tests; `dotnet test --filter LiveLeaseTests`): **17 tests, 12 failed, 5 passed**. The 5 that passed are
guards that must hold either way (another connection closing keeps the live theme; no answer and no process means "none"; the link
reaches a restarted tray; the moved server still refuses a squatted name and keeps the pipe rules).

| Test | Failure on the old behaviour |
|---|---|
| `M5_the_live_theme_ends_when_the_editor_that_sent_it_goes_away` (real pipe; the "editor" sends `LIVE` and disappears) | `the editor's unsaved theme stayed on the display after the editor was gone` |
| `M5_the_live_theme_ends_after_the_lease_without_a_word_from_the_editor` | `a live theme nobody keeps alive did not end` |
| `M5_a_heartbeat_keeps_the_live_theme` | `Expected: "OK"  Actual: "ERR unknown command"` |
| `M5_a_heartbeat_without_a_live_theme_asks_the_editor_to_send_it_again` | `Expected: "ERR no live theme"  Actual: "ERR unknown command"` |
| `M5_the_editors_page_pin_ends_with_its_live_theme` | `Assert.True() Failure` (closing the connection changed nothing) |
| `M5_live_clear_never_overtakes_the_live_before_it` (real pipe; the tray needs a moment for `LIVE`) | `Expected: [Live, LiveClear]  Actual: [LiveClear, Live]`, so the unsaved theme stayed |
| `M5_the_editors_commands_share_one_connection` | `Expected: 1  Actual: 3` (three commands, three connections) |
| `M5_closing_the_link_delivers_what_is_still_queued` | `Filter not matched in collection: []` (closing returned before `LIVE_CLEAR` reached the tray) |
| `M13_a_tray_process_that_does_not_answer_is_not_used` | `Expected: NotAnswering  Actual: Answering` |
| `M13_a_tray_of_another_account_in_this_session_is_told_apart` | `Expected: OtherAccount  Actual: Answering` |
| `M13_a_tray_that_answers_is_used_whatever_its_process_is_called` | `Expected: Answering  Actual: None` |
| `M13_the_probe_asks_the_tray_over_its_pipe` (real pipe) | `Expected: Answering  Actual: None` |

One test needed a correction before it proved anything: the first version of the ordering test released `LIVE` as soon as
`LIVE_CLEAR` *arrived*, and the woken `LIVE` could still take the lock first, so it passed on the old code. It now holds `LIVE` until
`LIVE_CLEAR` has been *applied* (or 1.5 s), which is the real race (a `LIVE` waiting for the tray's lock).

**After** (the fix): **the same 17 pass**, plus 8 tests for the new parts (25 in `LiveLeaseTests`), each run 5 times in a row
without a failure. Mutation check: removing the line that renews the lease on a command from the owner makes
`M5_a_heartbeat_keeps_the_live_theme` fail.

Full suite: baseline 791/791 on `main`; after the fix **817/817** (25 new in `LiveLeaseTests`, 1 new row `LIVE_KEEP` in
`ControlCommandTests`). Build: 0 errors; 158 warnings (152 before; the new ones are `CA1416` "Windows only" notes on the
Windows-only P/Invoke and identity calls, like the existing ones).

## 3. The fix

### Tray side: the live theme is leased (`TrayLiveState`, Core)

- `LIVE` makes the sending **connection** the owner. The theme and the editor's page pin end on `LIVE_CLEAR` (from any connection,
  as before), **when the owner connection closes** (Windows closes a process's pipe when it exits, crashes or is killed), or after
  **5 s without any command on it** (a hung editor). The tray then shows the saved theme again and logs why.
- New command `LIVE_KEEP`: `OK` while this connection holds the live theme, `ERR no live theme` otherwise (the tray restarted, the
  connection was replaced). Any command from the owner renews the lease.
- `RunnerContext` uses it under its lock: `Execute(line, connection)`, `OnControlClosed(connection)`, and `ExpireLive()` at the start of
  every `Supervise` tick (2 s), so the display is back on the saved theme at most ~7 s after a hung editor, and at once after a
  closed one.
- The control-pipe server moved to Core (`TrayControlServer`) unchanged in its rules (current user only, NETWORK denied, first
  instance, remote clients closed, at most 4 instances, log once a minute); it numbers connections and reports when each ends.

### Editor side: one ordered connection (`TrayLink`, Core)

- Every command goes through one queue on one background worker and one connection: each is sent after the reply to the one
  before. `Post` (fire and forget, still ordered), `SendAsync`, and `Send` (blocking, off the UI thread only).
- No reply within 5 s (the tray may hold its lock ~2 s while it reopens the display) counts as no answer, and the connection is
  dropped so a late reply can never be read as the next command's. A connection that broke since its last use is replaced once.
- `Close(timeout)` takes no more commands, delivers what is queued, then hangs up. The editor's `Closing` queues `LIVE_CLEAR` (via
  `StopLive`) and then closes the link on a worker; `Program.Main` waits for it together with the exit blank, within one shared
  4 s budget. If it does not make it, the tray still ends the live theme when the editor's connection closes.
- Heartbeat: while live, the editor's 500 ms live timer sends `LIVE_KEEP` every 1.5 s (`LiveKeepAlive`, one at a time). On
  `ERR no live theme` it calls `LiveSendGate.Lost()`, so the current theme is sent again at once. Other replies (an older tray's
  `ERR unknown command`) do not trigger resends.
- Same protection as before: `CurrentUserOnly` + `Anonymous`.

### Editor side: "is the tray there" by a round trip (`TrayPresence`, Core)

- `RunnerClient.Presence` is the cached result of `TrayPresence.Probe`: `STATUS` over the link. `OK` means `Answering`. Only without
  an answer does it look at `Glance.Runner` processes in this session and who owns them (`OpenProcessToken`): this user's means
  `NotAnswering`, another account's (or one Windows will not tell, such as an elevated one) means `OtherAccount`, none means `None`.
- The probe runs in the background (`RefreshPresence`, at most every 2 s, from the editor's 1 s tick); nothing on the UI thread waits
  for the pipe. The Background service window probes inside its existing `Task.Run`.
- `ToggleSend`: `Answering` -> live through the tray; `None` -> the editor draws itself; `NotAnswering` / `OtherAccount` / `Unknown`
  -> Send stays off and the status bar says why (`TrayPresence.Explain`), instead of streaming into nothing or fighting the other
  tray for the display.
- While live, the session ends live sending when the tray is gone or belongs to another account; a tray that is only slow keeps
  the link (the live gate already says "did not answer").

## 4. Files

- New: `src/Glance.Core/Control/{TrayControlServer,TrayLiveState,TrayLink,TrayPresence,LiveKeepAlive}.cs`,
  `src/Glance.Tests/LiveLeaseTests.cs`.
- Changed: `ControlCommand` (`LIVE_KEEP`), `LiveSendGate` (`Lost`), `RunnerContext` (uses the Core classes, lease expiry in
  `Supervise`), `RunnerClient` (link, presence), `MainWindow` (ordered posts, heartbeat, presence, exit wait; small edits only, the M7
  session works in the same file), `ServiceWindow` (two lines: presence instead of the process name), `AGENTS.md`.

## 5. Not verified here (manual checks)

Nothing in this branch was run against the real tray, editor or display (the task rules forbid starting them). Please check:

1. **Editor crash:** tray running, editor Send to display (live), make an unsaved change, end `Glance.App.exe` in Task Manager.
   The display should return to the saved theme within about a second (and the tray menu should stop saying "Live preview").
   `runner.log`: "The editor's live preview ended (its connection closed)".
2. **Normal close:** same, but close the editor window; the saved theme should be back at once.
3. **Hung editor:** hard to cause on purpose; a debugger pause of `Glance.App.exe` works: within ~7 s the saved theme returns
   (`runner.log`: "no word from the editor"), and after resuming the editor its edits come back within ~2 s.
4. **Tray restart while live:** exit the tray from its menu and start it again while the editor is live; the edits should
   reappear within a few seconds without touching the editor.
5. **Page pin:** pin a page in the editor while live, close the editor; the tray should rotate pages again.
6. **No tray:** exit the tray, wait 3 s, tick Send to display: the editor draws the display itself (as before).
7. **Tray of another account (optional):** `runas /user:<other> Glance.Runner.exe` in your session, then tick Send to display:
   the status bar should name "another account", and the display should not flicker between two themes.

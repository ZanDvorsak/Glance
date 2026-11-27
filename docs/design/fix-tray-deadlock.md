# Fix: display stays black with Glance 0.2.0 (tray deadlock)

Cause: the tray app opened the display on its WinForms UI thread; `RemoteFrameDisplay.Request` blocked on async pipe I/O whose
continuations (no `ConfigureAwait(false)` in Core) were posted back to that blocked thread. The editor's Send to display did the same
on Avalonia's UI thread. Tests use `UiLikeContext` (a never-pumped UI-like thread) and a fake service on a random pipe.

## Bugs (failed before on `main` 8d2d13c, pass after)

- Deadlock on connect: `Connecting_to_the_service_on_a_UI_thread_returns_instead_of_deadlocking`: "deadlocked on a UI-like thread: still blocked after 8 s".
- Tray's first open: `The_tray_apps_first_open_on_its_UI_thread_reaches_the_display`: deadlocked.
- Later requests: `Requests_made_on_a_UI_thread_after_connecting_also_return`: deadlocked.
- Status query: `The_service_status_query_returns_on_a_UI_thread`: deadlocked.
- Reply timeout never fired: `A_service_that_never_answers_times_out_on_a_UI_thread_instead_of_hanging_for_ever`: still blocked after 8 s (1 s timeout).
- Editor Send to display: `The_editors_Send_to_display_through_the_service_works_on_its_UI_thread`: deadlocked.
- Editor dead-loop restart: `The_editors_restart_of_a_dead_loop_through_the_service_returns_on_its_UI_thread`: deadlocked.
- Service silent on refusals: `The_service_logs_a_refused_client_once_a_minute_not_on_every_request`: no log line.
- Service silent on USB open failure: `The_service_logs_when_it_cannot_open_the_USB_display`: no log line.
- Real WinForms probe (outside the repo): "DEADLOCKED: still blocked after 10 s" before, "FINISHED in 0.1 s" after.

## Sync-over-async sites

- `RemoteFrameDisplay.Request`: fixed. The pipe exchange runs on the thread pool, with a timeout that works.
- All 22 `await`s in Core: `ConfigureAwait(false)` added. CA2007 is a build error in Core, and `ConfigureAwaitGuardTests` scans the source.
- `RunnerContext`: the display opens off the UI thread (`TrayDisplayOpener`, Core) after the control pipe and timers start; a failed open is logged once a minute.
- `DisplayServer`: logs refusals and failed USB opens, rate-limited.
- `ControlPipeClient.cs:43`: safe, unchanged (tested: returns null within the timeout on a UI-like thread).
- `ThemeRenderer.cs:765`, `SessionPowerWatcher.cs:90`, `DisplayPusher.cs:309`: safe, unchanged (each tested on a UI-like thread).
- `MainWindow.cs:1537`, `Autostart.cs:29`, `ServiceRuntime.cs:78`: safe by reading (bounded, no UI context needed), unchanged.

## Follow-ups

- Editor still opens/stops the display on its UI thread, and the tray still blanks and stops it on its UI thread: no deadlock, but up to a reply timeout of blocking.

## Check after reinstall

- The display shows the theme within seconds; `service.log` has "Display opened for session 1"; editor Send to display works.

Tests: 819/819 (28 new).

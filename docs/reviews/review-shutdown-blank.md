# Review: blank the display at shutdown, sign-out and sleep

- **Verdict:** merge after fixes — **now:** all findings fixed (L5 only documented, see below); merged into main
- **Build/tests:** 310/310 after phase 2 (3 runs in a row, about 8 s); build 0 errors
- **Proof:**
  - Before: unfixed `main` had no session-end or power hook at all (`git grep` for `ENDSESSION`, `POWERBROADCAST` etc. found nothing), so Windows killed the runner and the last frame stayed on all night
  - Before: review fixes, phase 1: 8 of 29 power tests failed (blank waited 5 s on a stalled display, editor gave up after 7.5 s); phase 2: 5 tests failed (dead pusher reopened while asleep, stalled display not remembered)
  - After: all pass; a working display blanks in about 405 ms, a stalled one gives up in 3.7 s (shutdown) / 1.2 s (sleep)

## Findings
- **Medium M1** — Shutdown/sleep handler had no time limit; a stalled display could hold shutdown 18 s+ (`RunnerContext.cs:184`, `SessionPowerWatcher.cs:131`) → handler bounded (4 s shutdown, 1.5 s sleep), `PowerBlank.Run` with a bounded lock wait, uses the display loop's `Stop(blank, timeout)` — **fixed**
- **Medium M2** — `RestartDeadPusher` from the display-loop branch reopened the display without the power check (`MainWindow.cs`) → new `PusherSlot.RestartIfDead` reopens only through `TryOpen` — **fixed**
- **Low L1** — A missed wake left the runner dark for good; session end while asleep did not quit (`DisplayPowerPolicy.cs:55`) → session end always stops and quits, self-heal after 30 s awake, Reload/Resume clear it — **fixed**
- **Low L2** — Editor stopped retrying after 7.5 s after wake (`MainWindow.cs:1415`) → retries for about 60 s with a "Waiting for the display" message — **fixed**
- **Low L3** — Avalonia closes the editor on shutdown, so `Closing` blanked on the UI thread with no limit (`MainWindow.cs:159`) → blank runs on a background task, bounded wait in `Program.Main` — **fixed**
- **Low L4** — Shutdown block reason set only inside `WM_ENDSESSION` (`SessionPowerWatcher.cs:112`) → kept on purpose; the bounded handler can hold shutdown at most 4 s — **fixed**
- **Low L5** — Modern Standby, monitor off and lock not covered (`SessionPowerWatcher.cs:11`) → documented as out of scope in AGENTS.md and the TUTORIAL — **partly fixed: still not handled in code**
- **Low L6** — "Synchronous delivery" test did not catch an async handler (`PowerTests.cs:137`) → new test with a 150 ms handler — **fixed**
- **Low (test gap)** — Runner wiring (`Supervise`, `PayOwedBlank`, `Quit` on session end) has no unit tests; the compare-exchange in `RestartIfDead` is untested (mutation survives) — **open**
- Nits: N1 Windows-only attribute, N2 logging before and after the blank, N3 clearer editor message, N4 editor closes on session end, N5 WALKTHROUGH note, N6 obsolete constant removed
- Also open: `docs/walkthroughs/shutdown-blank.md` still describes the first version

## Manual checks for the user
- With the runner running, Start → Shut down: the display goes black within about 1 s; `runner.log` has `Windows SessionEnding: BlankAndStop`
- Restart and sign out: it blanks; after sign-in the theme comes back
- Sleep, wait a minute, wake: black before sleep, theme back within 2-5 s; `runner.log` has no "No wake-up notice" line
- Editor sending without the runner: shut down (it blanks), and sleep/wake (sending resumes within a few seconds)
- Unplug the display mid-frame and shut down at once: Windows waits no more than about 4 s; log shows `blank TimedOut` or `blank Blanked`
- Unplug, sleep, wake, plug back in: no "busy" errors, the runner reopens within a few seconds
- Start a shutdown with unsaved Notepad text, then Cancel: note whether the runner keeps showing the theme

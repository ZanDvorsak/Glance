# Review: Live preview of themes without a folder (H2)

- **Verdict:** merge after fixes — **now:** all findings fixed (M1, M2, L1-L4), plus a lost-reply bug found while fixing; merged into main
- **Build/tests:** 310/310 pass on the branch after the fixes (build: 0 errors)
- **Proof:**
  - Before: on unfixed `main`, a theme without a folder was sent as `LIVE  <json>` (empty field), which the runner split into 2 parts and answered `ERR unknown command`
  - Before: the review-fix tests failed 9 of 55 control-pipe tests on the branch code
  - After: the 4 H2 tests pass (295/295); after the review fixes, 310/310

## Findings
- **Medium M1** — seven editor call sites still built raw command strings, against the new `AGENTS.md` rule (`MainWindow.cs`, `ServiceWindow.cs`) → all use `ControlCommand`; `Send(string)` is gone; a test scans for raw strings — **fixed**
- **Medium M2** — a failed live send was never retried, and its warning never went away (`MainWindow.cs:1335`) → new `LiveSendGate` retries on the next tick and restores the normal status after a success — **fixed**
- **Low L1** — the warning could be a false alarm when the runner was busy for more than 2 s (`RunnerClient.cs:16`) → LIVE waits 1 s to connect and 5 s for the reply; "did not answer" and "rejected" get different messages — **fixed**
- **Low L2** — a new editor talking to an old, still-running runner fails for themes without a folder, with no hint why → the rejection message says to restart the runner after an update — **fixed**
- **Low L3** — overlapping live sends could reach the runner out of order (`MainWindow.cs:1338`) → one send at a time, always the newest snapshot — **fixed**
- **Low L4** — test gaps: a missing verb in the word table and the runner's dispatch were untested → a test now loops over every verb — **fixed** (the dispatch in `RunnerContext.Execute` stays untested, as the review allowed)
- **Extra (found while fixing)** — the editor never got any reply from the runner: the pipe closed before the writer flushed (`RunnerClient.Send`) → write raw bytes, reader uses `leaveOpen` — **fixed**
- Nits: fixed: N4 (comment on the empty-folder check); open: N1 (namespace `Glance.Core.Control`), N2 (pipe name defined twice), N3 (`ToString` returns the whole theme)

## Manual checks for the user
- With the runner running, click **New**, pick a template, click **Send to display**: the display shows the new theme.
- The status bar then says "Showing your edits on the display through the background runner." with no warning.
- The Background service window shows the runner's status text instead of just "Running.".

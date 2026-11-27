# Review: Display loop resilience (H4, H5, M1)

- **Verdict:** merge after fixes (small ones) — **now:** all findings fixed except Low 6; Medium 1 fixed in the merge commit `dac7f4a`; merged into main
- **Build/tests:** 272/272 pass on the branch after the fixes (about 5 s, 3 runs in a row)
- **Proof:**
  - Before: H4 race test with `main`'s page selection failed 3/3 (`ArgumentOutOfRangeException` after 421,903 to 850,468 frames)
  - Before: H5 (USB closed mid-transfer) and M1 (handle leak on a failed open) shown from `main`'s code only, no hardware; review-fix tests: 5 of 9 failed
  - After: the branch's loop passed 3/3 (30-39 million frames, no exception); review-fix tests 9/9; full suite 272/272

## Findings
- **Medium 1** — after merging shutdown-blank, the editor's `RestartDeadPusher` could reopen the display during sleep or after the session ended (`MainWindow.cs:1343-1358`) → replaced by the tested `PusherSlot`, which reopens only through the power-policy check — **fixed** (in merge `dac7f4a`)
- **Medium 2** — `Stop`'s bound was fixed at 18 s, and blanking a stalled display took ~9 s, too long for sleep (`DisplayPusher.cs:39,205-235`) → public `Stop(blank, timeout)`; sleep and shutdown pass a short bound; no black frame after a failed transfer — **fixed**
- **Medium 3** — a render error on every frame made the runner reopen USB every ~8 s, forever (`DisplayPusher.cs:187-193`, `RunnerContext.cs:157-161`) → separate `LastRenderError`; the tested `ShouldReopen` reopens only for USB errors — **fixed**
- **Low 1** — a theme whose pages are all `null` froze the display silently (`Theme.cs:42-52`) → `Normalize` drops null pages; an empty theme is reported after 1 s — **fixed** (null widgets left to full-review M3)
- **Low 2** — the undo/redo race test passed even with the H4 bug put back (`DisplayPusherTests.cs:162-180`) → new fast-loop test fails 5/5 on the old bug; `StablePages` tested on its own — **fixed** (`Theme` itself is still not safe for other background readers)
- **Low 3** — the "Stop timed out" contract was untested (`DisplayPusher.cs:220-235`) → new test — **fixed**
- **Low 4** — callers ignored `Stop`'s result and reopened while the old loop still held the device → wait until `IsClosed` before reopening — **fixed**
- **Low 5** — a repeating `InvalidOperationException` was swallowed with no error and no back-off (`DisplayPusher.cs:186`) → reported and backed off after 10 in a row — **fixed**
- **Low 6** — the UI thread can block up to 18 s on a stop, and the editor opens USB on the UI thread (`MainWindow.cs`, `RunnerContext.cs:141-171`) → `StopAsync`, open off the UI thread — **open** (tracked as full-review M7/L8)
- **Low 7** — after a dead loop, the runner's status said "Waiting for the display: not found" (`RunnerContext.cs:151-165`) → the real reason is shown — **fixed**
- **Low (test gaps)** — no test that a failed `SetBrightness` is retried; `Ax206Display`'s disposed flag and lock are checked only by reading (need hardware) — **open**
- Nits: fixed: `_cts` disposed, `UnexpectedErrors` read-only, `IFrameDisplay` internal, `Faulted` set only when not stopped, wider slow-transfer test margin, honest comments on `StablePages`/`ApplySnapshot`, AGENTS.md test count; open: `Glance.Core.Usb` still depends on `Glance.Core.Display`

## Manual checks for the user
- With the runner on, unplug and replug the display: it should come back by itself.
- Put the PC to sleep and wake it: the display should go black quickly and come back after waking.
- Shut down or sign out: the display should go black without a long "Turning off the Glance display" wait.
- Undo and redo quickly while sending to the display: it should keep updating.
- Load a theme with a broken widget: the editor shows an orange "Theme error", and `runner.log` shows no repeated "reopening" lines.

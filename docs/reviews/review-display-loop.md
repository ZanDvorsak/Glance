# Code review: display loop resilience (H4, H5, M1)

**Branch:** `claude/fix-display-loop-693991` (one commit, `88bc1cf`, on top of `main` at `0a9f10b`)
**Date:** 2026-11-26
**Scope:** the whole diff (8 files, +545/-62) and the code around it: `DisplayPusher`, the new `IFrameDisplay`, `Ax206Display`,
`Theme.ApplySnapshot`, the runner's `Supervise`, the editor's pusher handling, and the 11 new tests. Also checked: how the change
merges with the five sibling branches, and whether the new `Stop` works for `claude/feature-shutdown-blank-60ded2`.
**Method:** read every changed line and its callers; built and ran the tests several times; mutation checks (code broken on purpose,
tests run, code restored with `git checkout` and `git status` confirmed clean); trial merges with `git merge-tree` (no refs or
working files changed), then a build and test run of the combined tree in a scratch folder.
**Not done:** nothing was run against the physical display, and neither the editor nor the runner was started. Every timing below that
involves real USB comes from the code and `docs/PROTOCOL.md` (3 s transfer timeout, about 400 ms per frame). None of it was measured.

---

## Verdict: merge after fixes (small ones)

The change does what the brief asked for, and the core of it is correct:

- The loop can no longer die silently.
- While the loop runs, only the loop touches the device. `Stop` cannot return `true` before the device is closed.
- `Ax206Display` refuses to hand a closed handle to libusb.
- `ApplySnapshot` never leaves `Pages` empty.
- Both handle leaks in M1 are closed.

The tests are well aimed, and most of them fail without the fix. The normal stop is faster and more reliable than before, which helps
the shutdown-blank branch.

Fix two things before, or together with, merging the shutdown-blank branch:

1. **Medium 1:** after that merge, the editor's new `RestartDeadPusher` bypasses the power policy. Git will not flag this: the text
   merges cleanly.
2. **Medium 2:** `Stop`'s bound cannot be chosen by the caller. With a stalled display, blanking costs about 9 s, which is too long
   for sleep.

Medium 3 (the runner reopens USB forever on a render bug) and the low findings can be follow-ups.

## Build and test results

| Check | Result |
|---|---|
| `dotnet build src/Glance.sln -c Release -p:SkipHost=true` | Succeeded, 0 errors. 30 warnings, all already on `main`; none are in the changed code (rebuilt with `--no-incremental` to check). |
| `dotnet test src/Glance.Tests -c Release` | **264/264 passed**, 6-7 s. Up from 253 tests and about 2 s, because three tests wait out the 1 s error back-off and one waits for a 2.6 s fake transfer. |
| Flakiness | 7 full runs plus 4 runs of only `DisplayPusherTests`: all green. Per-test times are stable (the slow-transfer test takes 2.6 s, each recovery test about 1 s). |
| Mutation checks | See [Test quality](#test-quality). Catch-all removed, `Stop`'s close removed, and `ApplySnapshot` reverted to `Clear()` + `Add`: 4 tests fail, as they should. The race test does **not** catch the original H4 race (Low 2). |
| Trial merge with all five sibling branches | All merge without textual conflicts, in this order: live-empty-folder, ffmpeg-elevation, shutdown-blank, saveas-assets, autostart-task. The combined tree builds and **437/437 tests pass**. |

---

## Findings

### Medium

#### Medium 1. After merging shutdown-blank, `RestartDeadPusher` can reopen the display while Windows sleeps or after the session ended
- **Where:** `src/Glance.App/MainWindow.cs:1247,1343-1358` (this branch). In the trial merge they are at lines 1255 and 1359.
- **Evidence:** the shutdown-blank branch routes every write to the editor's `_pusher` through two helpers:
  - `ReleasePusher()`, which uses `Interlocked.Exchange` and can run on the watcher thread;
  - `TryStartPusher()`, which takes `_powerGate` and checks `_power.MayDrive`.

  This branch adds a third writer that does neither:
  ```csharp
  dead.Stop(blank: false);
  _pusher = null;
  try { _pusher = new DisplayPusher(() => _theme, _hub, _settings.Brightness) { PinnedPage = DesiredPin }; ... }
  ```
  `git merge-tree` reports no conflict, because the two branches touch different lines.
- **Scenario:** the editor's pusher has faulted (rare: its setup failed). Windows goes to sleep, and `OnPowerEvent` blanks and
  releases. On the next `SlowTick`, the UI thread still finds a dead pusher (or had already captured `dead`) and opens a new
  `DisplayPusher`. The display lights up during sleep, or after `PowerEvent.Quit`/`SessionEnding`. That breaks the "nothing reopens
  after the session ended" guarantee of `DisplayPowerPolicy`.
- **Remedial action:** when merging, rewrite `RestartDeadPusher` on top of the helpers:
  ```csharp
  if (Interlocked.CompareExchange(ref _pusher, null, dead) != dead) return;   // someone else already released it
  dead.Stop(blank: false);
  if (!TryStartPusher(out var err)) { _send.IsChecked = false; _status.Text = "... could not be reopened: " + err; }
  ```
  Add a line to the merge commit saying why.

#### Medium 2. `Stop`'s bound is fixed at 18 s, and blanking a stalled display costs about 9 s, too long for sleep and shutdown
- **Where:** `src/Glance.Core/Display/DisplayPusher.cs:39` (`StopTimeout`), `:205-210` (`Close`), `:224-235` (`Stop`; the
  overload that takes a timeout is `internal`). Callers: `RunnerContext.cs:171`, and in the merged shutdown branch, `OnPowerEvent`,
  which runs on the watcher thread **while holding `_gate`**.
- **Evidence (from the code, not measured):**

  | State when `Stop(blank: true)` is called | Time until `Stop` returns |
  |---|---|
  | Loop waiting between frames | It wakes at once (`token.WaitHandle.WaitOne`). Black frame (~400 ms) + backlight: **~0.5 s** |
  | Loop in the middle of a frame | Rest of the frame (up to ~400 ms) + black frame + backlight: **under 1 s** |
  | Display stalled (transfers time out) | 3 s for the transfer in flight + 3 s for the black frame's first write + 3 s for the backlight command: **~9 s** |
  | Worse than that | `Stop` returns `false` at 18 s, and the loop closes the device later |

  For comparison, the old `Stop` could wait out a 1 s `Thread.Sleep` back-off, then blanked from the caller's thread *while the loop
  could still be sending*. So the normal case is better now: faster, and blanking is serialized after the last frame. That is good for
  the shutdown-blank branch. Only the stalled case is a problem. Windows allows about 2 s for `PBT_APMSUSPEND`. During a session end
  the watcher shows a shutdown block reason, and meanwhile the runner's UI thread waits on `_gate` in `Supervise`.
- **Scenario:** the display has stopped answering (a known AX206 failure, see review M12) and the user puts the PC to sleep. The
  handler blocks for about 9 s, and Windows suspends partway through. At shutdown the user sees "Turning off the Glance display" for
  about 9 s, for a display that cannot be blanked anyway.
- **Remedial action:**
  - Make `Stop(bool blank, TimeSpan timeout)` public, and have the power handlers pass about 2-3 s.
  - In `Close()`, skip `Blank()` when the loop's last transfer failed with an `IOException`. A display that just timed out will not take
    a black frame, and this cuts the stalled case from ~9 s to ~3 s.
  - Add a test for both.

#### Medium 3. A render error that repeats every frame now makes the runner reopen USB every ~8 s, forever
- **Where:** `DisplayPusher.cs:187-193` (the catch-all sets `LastError`), `RunnerContext.cs:157-161` (reopen after 6 s of `LastError`).
- **Evidence:** `Supervise` cannot tell a USB error from a render error, because both arrive in `LastError`. Before this change such an
  exception killed the loop (the H4 bug). Now the loop survives, but `LastError` is set on every frame. `ThemeRenderer.Render` has no
  per-widget `try`, so one bad widget fails the whole frame, every frame.
- **Scenario:** a theme value that makes Skia or the renderer throw (for example a null in theme JSON, see review M3, or a sensor
  source that throws). Every 6 s plus one tick, the runner logs "Display kept failing; reopening", closes and reopens the USB device,
  and builds a new `ThemeRenderer`, which reloads images and restarts any ffmpeg video processes. The display meanwhile keeps its last
  picture. `runner.log` is never rotated (review L7), so it grows by about 10,000 lines a day.
- **Remedial action:**
  - Split the error signal, for example `LastUsbError` (`IOException` and `ObjectDisposedException` from the device) and
    `LastRenderError`, or one `LastError` with an `ErrorKind`.
  - Have `Supervise` reopen only on USB errors.
  - Show render errors in the status, and log each distinct message once.
  - Add a test: a throwing sensor source must never lead to a reopen decision. That decision is pure logic, so it can move into Core.

### Low

#### Low 1. A theme whose pages are all `null` freezes the display silently, the very symptom H4 was about
- **Where:** `DisplayPusher.cs:113,157,183`; `Theme.Normalize` (`Theme.cs:42-52`).
- **Evidence:** `StablePages` filters out `null` entries, which is right for the shrink race. But `"Pages": [null]` in a theme file
  survives `Normalize`, because it only adds a page when `Count == 0`. The loop then gets an empty array. It skips drawing, sets
  `LastError = null`, and does so on every frame. Filtering also shifts indexes, so with `[null, A, B]` a pin on page 2 shows B.
- **Scenario:** a hand-edited or imported theme (review M3) is loaded by the runner. The display keeps its old picture, the status is
  green, and nothing is logged.
- **Remedial action:** in `Normalize`, drop `null` pages (and `null` widgets), then guarantee one page. In the loop, if the copied list
  stays empty for more than about 1 s, set `LastError = "the theme has no pages"`.

#### Low 2. The undo/redo race test passes even with the original H4 bug restored
- **Where:** `src/Glance.Tests/DisplayPusherTests.cs:162-180`.
- **Evidence (mutation check):** I restored *both* halves of the old behaviour: `ApplySnapshot` back to `Clear()` + `Add`, and the page
  read back to `theme.Pages[Math.Min(_pageIndex, theme.Pages.Count - 1)]` on the live list. The test **passed 4 of 4 runs**. The loop
  looks at the theme only once every 200 ms, so it almost never lands in the microseconds where the list is empty. With
  `FrameIntervalMs` temporarily set to 1, the same test failed 3 of 3 runs (`UnexpectedErrors` was 1), and the fixed code passed 3 of 3
  at 1 ms. The deterministic tests (`ApplySnapshot_never_leaves_the_theme_without_pages`,
  `A_theme_with_no_pages_for_a_moment_does_not_fault_the_loop`) do cover the fix. So the commit message's "each fix was broken on purpose
  and a test failed" is true, but this particular test adds 1.5 s and no protection.
- **Remedial action:** let the internal constructor take the frame interval and the back-off (an internal options record), and run
  this test at 1 ms. Add a direct unit test of `StablePages`. Without those, rename the test as a smoke test.

#### Low 3. The "Stop timed out" contract is untested
- **Where:** `DisplayPusher.cs:220-235`. The internal `Stop(bool, TimeSpan)` exists for tests, but no test calls it.
- **Evidence:** nothing checks that `Stop` returns `false` with the device still open, and that the loop later blanks once (if asked)
  and disposes after its transfer, never during it. That is the subtlest promise in the change.
- **Remedial action:** a test with `BlitDelayMs = 1500`:
  1. `Stop(blank: true, 100 ms)` returns `false`, and `Disposes == 0`.
  2. Then `WaitUntil(Disposes == 1)`, and check `Blanks == 1`, `DisposedDuringTransfer == 0` and `UsedAfterDispose == 0`.
  3. A second `Stop` returns `true` and does nothing more.

#### Low 4. Every caller ignores `Stop`'s return value, so a timed-out stop is followed by a reopen that fails
- **Where:** `RunnerContext.cs:171`, `MainWindow.cs:1315,1346,1362`, and `Closing` at `:159`.
- **Evidence:** after `false`, the old loop still holds interface 0 until its transfer gives up.
  - The runner sets `_pusher = null` and tries to open on the next tick. That fails with "Could not claim the display interface"
    until the old loop finishes. It heals itself, but the status shows a misleading error.
  - In the editor, toggling Send off and on again shows that error and unchecks Send.
- **Remedial action:** when `Stop` returns `false`, keep the old pusher in a `_closing` field, log it, and skip opening until
  `!_closing.IsAlive`. Or give `DisplayPusher` a `Task Stopped` that owners can await.

#### Low 5. `InvalidOperationException` is still swallowed with no `LastError` and no back-off
- **Where:** `DisplayPusher.cs:186`.
- **Evidence:** excluding `ObjectDisposedException` was a good fix. But an `InvalidOperationException` that repeats on every frame
  still spins at 5 fps with a green status. The full review's L14 asked to count consecutive failures.
- **Remedial action:** count consecutive `InvalidOperationException`s. After about 10 in a row (~2 s), set `LastError` and apply the
  back-off. Reset the count after a good frame.

#### Low 6. The documented UI-thread block grew from 2 s to 18 s, and the editor opens USB on the UI thread
- **Where:** `MainWindow.cs:159,1315,1350,1362` (Dispose and open on the Avalonia UI thread), `RunnerContext.cs:141-171` (`Supervise`
  and `Reload` hold `_gate` on the WinForms UI thread while `Stop` waits).
- **Evidence:** in practice this is under 1 s, and about 9 s with a stalled display, much like the old ~2 s wait plus a blank from the
  caller's thread. But the bound is now 18 s, and `RestartDeadPusher` adds a new USB open (`ReadDimensions` and `SetBrightness`, up to
  3 s each) on the UI thread. This is already tracked as review M7/L8, so it is not a regression of this change.
- **Remedial action:** add a `StopAsync`. Swap the reference under the lock, and stop and open outside it (as L8 suggests). In the
  editor, open in `Task.Run` and post the result back.

#### Low 7. After a dead loop, the runner's status says "Waiting for the display: not found"
- **Where:** `RunnerContext.cs:151-156,164-165`.
- **Evidence:** the `!IsAlive` branch logs the pusher's error but does not copy it into `_lastError`, so the status line falls back to
  "not found" (or an older error) until the next tick reopens.
- **Remedial action:** set `_lastError = _pusher.LastError ?? "display loop stopped"` before `DisposePusher(blank: false)`.

### Nits

- `Faulted` (`DisplayPusher.cs:51`) is only read by tests: both owners use `IsAlive`. Use it in the runner's log line (stopped or
  faulted), or drop it. `Loop` also sets it when an exception escapes during a requested stop, which contradicts "without being stopped".
- `UnexpectedErrors` (`:54`) is a writable `internal` field. Make it a read-only property over a private counter.
- `IFrameDisplay` is `public`, but only the `internal` constructor takes it, and `Glance.Core.Usb` now depends on
  `Glance.Core.Display` for it. It could be `internal`, or live in `Glance.Core.Usb`.
- `_cts` is never disposed. Using `token.WaitHandle` now also creates a kernel event for each pusher (the full review already lists the
  undisposed CTS). Dispose it at the end of `Stop` once the task has completed.
- AGENTS.md still says "~240 tests, about 2 seconds". It is now 264 tests in about 7 s. An injectable back-off (see Low 2) would bring
  the time back down.
- `Stop_waits_for_a_slow_transfer_instead_of_closing_the_display_under_it` checks `>= 2000 ms` against a 2600 ms fake transfer, so a
  600 ms pause in the test thread would fail it. That is unlikely, but a wider gap (a 3000 ms transfer, checking `>= 2000`) costs little.
- `ApplySnapshot` is still not atomic: for one frame the list can mix old and new pages. That is harmless. The comment could say
  "never empty" rather than imply it is consistent.

---

## Merge notes (sibling branches)

| Branch | Shared files | Textual result | Semantic notes |
|---|---|---|---|
| `fix-saveas-assets-ee5527` | `Theme.cs` | Clean (it changes `Save`, this branch changes `ApplySnapshot`) | None. |
| `fix-live-empty-folder-5aa7c3` | `RunnerContext.cs`, `MainWindow.cs` | Clean | It moves `Execute` to `ControlCommand`. `Current` stays lock-free, so the loop still never takes `_gate`. |
| `fix-ffmpeg-elevation-a3dfcc` | `RunnerContext.cs`, `MainWindow.cs` | Clean | None found. |
| `fix-autostart-task-0011d2` | none | Clean | None. |
| `feature-shutdown-blank-60ded2` | `RunnerContext.cs`, `MainWindow.cs` | Clean | **Medium 1** (`RestartDeadPusher` bypasses the power gate) and **Medium 2** (stalled-display stop time). `Stop` now returns `bool`. The shutdown branch calls it as a statement, which still compiles. Its "blank once" logic works with the new `Close()`, which runs once anyway. |

Trial merge of all six (this branch plus the five): builds, 437/437 tests pass.

**Does the new `Stop` work for shutdown-blank?** Yes in the normal case, and better than `main`:
- `Stop` now wakes the loop at once instead of waiting out a `Thread.Sleep`.
- The black frame is sent on the loop thread after the last frame, so it cannot interleave with a frame or hit a closed handle.
- A second `Stop`/`Dispose` (a session end racing a quit) does nothing.

The one gap is the stalled display (Medium 2): the power handler needs a shorter bound and should skip a blank that cannot succeed.

---

## Test quality

Mutation checks (each time the code was restored with `git checkout`, then `git status` was confirmed clean):

| Code broken on purpose | Tests that failed |
|---|---|
| Catch-all narrowed to `IOException` only | `An_exception_from_the_theme_...`, `An_exception_from_the_renderer_...` |
| `if (finished) Close();` removed from `Stop` | `A_loop_that_fails_to_start_reports_it_and_is_closed_by_Stop` |
| `ApplySnapshot` back to `Clear()` + `Add` | `ApplySnapshot_never_leaves_the_theme_without_pages` |
| Original H4: live-list indexing plus the old `ApplySnapshot` | **none** at the shipped 200 ms frame interval (4 runs); the race test fails 3/3 at 1 ms (Low 2) |

Reading the other tests against `main`'s code:
- `Stop_waits_for_a_slow_transfer...` would fail on `main`, because the old 2 s wait disposes during the 2.6 s transfer
  (`DisposedDuringTransfer` would be 1).
- `Stop_with_blank_blanks_exactly_once_on_the_loop_thread...` would fail because of `BlankThread`.
- `A_theme_with_no_pages_for_a_moment...` fails if the `pages.Length > 0` guard is removed, because the catch-all counts the error.

`FakeDisplay` is a good, small fake: it records transfers in flight, use after dispose, and which thread blanked.

**Test gaps:**
1. The `Stop` timeout path (Low 3).
2. A shrink race at a realistic sampling rate (Low 2).
3. `StablePages` on its own.
4. Null pages in a theme file (Low 1).
5. A loop where `SetBrightness` fails, to check that it is retried. The code is right, but no test covers it.
6. `Ax206Display`'s disposed flag and lock. These need hardware, or a seam over `Libusb0`. Today they are verified only by reading.
7. The runner's `Supervise` and the editor's restart. They are UI code, so nothing covers them. The "should I reopen?" decision
   (dead, faulted, USB error for 6 s) is pure logic that could move to Core and be tested; that would also help Medium 3.

---

## What was checked and found sound

- **The loop owns the device.** While the task runs, only the loop thread calls `Blit`, `SetBrightness`, `Blank` or `Dispose`.
  - `Close()` runs from `Loop`'s `finally` (when stopping), or from `Stop` only after `_task.Wait` returned `true`. So `Stop` never
    returns `true` before the device is closed, and never closes it under a transfer.
  - `Close` is made idempotent by `Interlocked.Exchange(ref _closed, 1)`, so a double stop or dispose cannot blank or dispose twice.
- **Ordering of the blank flag.** `_blankOnStop` is a `volatile` write before `_cts.Cancel()`, and the loop reads it after it sees
  the cancellation, so a `Stop(blank: true)` is never lost. A later `Stop(true)` after a finished `Stop(false)` correctly does nothing.
- **No deadlocks.**
  - The loop never takes the runner's `_gate`, the shutdown branch's `_powerGate`, or a UI dispatcher.
  - `Current` and the editor's `() => _theme` are lock-free. The sensor sources take only their own short locks.
  - Holding `_gate` (or `_powerGate`) while `Stop` waits therefore delays other callers but cannot deadlock.
- **The faulted path.** A setup failure leaves the device open for `Stop`, which closes it (and blanks if asked) once the task has
  ended. The runner (`!IsAlive`, then dispose, then reopen on the next tick) and the editor both recover. `IsAlive` is
  `!_task.IsCompleted`, which is simple and race-free.
- **Back-off and wake-up.** `Math.Max(backoff, FrameIntervalMs - elapsed)` with `token.WaitHandle.WaitOne` keeps the 5 fps cap, sleeps
  1 s after errors, and wakes at once on `Stop`. `LongRunning` gives the loop its own thread instead of blocking a pool thread.
- **Brightness** is now recorded only after the USB call succeeds, so a failed change is retried (review L14).
- **`NextPageIndex`** gets a list overload and a guard for negative `current`. The `Theme` overload forwards to it, so
  `VirtualDisplayWindow` and `LogicTests` are unchanged. The virtual display renders on the UI thread, so it does not race.
- **`StablePages`.** `ToArray()` on a `Collection<T>` reads `Count` and then calls `CopyTo`. Growth throws `ArgumentException`
  (retried 3 times, then counted as an unexpected error); shrinking leaves trailing `null`s, which are filtered out.
- **`ApplySnapshot`.**
  - The page list goes from Reset+Add events to Replace/Add/Remove events. The editor's `HookAll` ignores the event kind and
    reconciles every page and widget, so undo hooks still work.
  - `ActivePageIndex` is still clamped. The existing `ApplySnapshot_keeps_the_instance_and_clamps_the_active_page` test passes.
  - The list is never empty (deterministic test).
- **M1.** `Ax206Display.Open` disposes when `ReadDimensions` throws. The pusher's constructor disposes when the first `SetBrightness`
  throws, and the public constructor cannot leak, because `Open()` either returns an owned display or throws.
- **`Ax206Display`.**
  - One lock covers each whole command (command block, data chunks, status), so `Dispose` cannot interleave inside a frame.
  - `_disposed` is only touched under the lock.
  - `Handle` throws `ObjectDisposedException` instead of passing `IntPtr.Zero` to native code.
  - `Blank()` stays best effort (`TryBlank` is unchanged).
- **Conventions.** Logic and tests are in Core, and the UI only wires it up. `InternalsVisibleTo` is used rather than widening the API.
  The JSON format is unchanged. The comments explain why. No new warnings.

---

## Proof: the bugs existed and are fixed

### H4: the display loop dies silently during undo/redo

A throwaway race test does this:
- One thread does undo and redo with the real `Theme.ApplySnapshot`, switching between a 2-page and a 3-page theme.
- The other thread picks the page exactly as the display loop does, as fast as possible for 3 s.
- An exception that the loop would not survive counts as a failure.

| Run | Page selection used | Result (3 runs each) |
|---|---|---|
| `main` (unfixed, `0a9f10b`) | `main`'s loop: `NextPageIndex(theme, ...)`, then `theme.Pages[Math.Min(i, Count - 1)]`, catching only `IOException` and `InvalidOperationException` | **Failed 3/3**: `After 421903 frames the display loop would have died silently with ArgumentOutOfRangeException: Index was out of range.` (other runs: 759,592 and 850,468 frames) |
| Branch (`88bc1cf`) | the branch's loop: `StablePages(theme)` copy, `NextPageIndex(pages, ...)` on the copy, `pages[Math.Clamp(...)]` | Passed 3/3: 29.8M, 39.2M and 38.7M frames, no exception of any kind, 0 empty copies |

The branch's own display tests also pass (`DisplayPusher*` and `Ax206*`: 11/11). They cover a renderer exception that no longer kills the loop and is reported in `LastError`, `Stop` with a slow fake transfer, a single blank on stop, and a failed first `SetBrightness`.

**Caveat found while proving:** the same race test with `main`'s page-selection code **still fails on the branch** (3/3, `ArgumentOutOfRangeException`). The fix makes the *loop* safe by copying the pages and catching everything. It does not make `Theme` itself safe to read from another thread: `ApplySnapshot` still shrinks `Pages` while another thread reads it. Any other background reader of `theme.Pages` (now or in future) can still hit this. This supports Low 2 above, and the full review's suggestion to hand background renderers an immutable snapshot.

### H5: USB closed while a transfer is still running

On `main`, this is shown from the code; it cannot be run without hardware, because `main` has no device interface to fake:
- `DisplayPusher.Stop`: `try { _task.Wait(2000); } catch { }`, then `if (blank) _lcd.Blank();`, then `_lcd.Dispose();`
- `Ax206Display`: `TimeoutMs = 3000`.

A single transfer can therefore outlast the 2 s wait, and `Blank()` and `Dispose()` then run while the loop thread is still inside `usb_bulk_write`.

On the branch, the slow-fake-transfer test confirms that `Stop` never disposes during a transfer. It passed, together with the other display tests (11/11).

### M1: a failed open leaks the USB handle

On `main` (code): `Open()` ends with `var d = new Ax206Display(h); d.ReadDimensions(); return d;` with no cleanup if `ReadDimensions` throws, and the pusher constructor calls `_lcd = Ax206Display.Open(); _lcd.SetBrightness(brightness);` with no cleanup either. On the branch, the "failing first `SetBrightness` disposes the device" test passes.

**Not provable here:** behaviour on the physical display, such as a real stall, unplugging it or replugging it.
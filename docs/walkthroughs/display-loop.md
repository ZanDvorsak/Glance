# Keeping the display loop alive: a walkthrough of the H4, H5 and M1 fix

This walkthrough is for a mid-level developer who wants to understand one change in Glance: the fix on branch
`claude/fix-display-loop-693991` (commit `88bc1cf`) for findings **H4**, **H5** and **M1** of `docs/reviews/review-full-codebase.md`.
It explains what went wrong, the threading ideas behind the fix, and then the code itself, with before and after where it helps.
It ends with five ways the change could be better.

It assumes you know C#, `Task` and `lock`. You do not need to know USB: section 2 explains what matters. For the project as a
whole, read `docs/WALKTHROUGH.md` first, especially section 4.3 on `DisplayPusher`.

**Contents**

1. [The problem](#1-the-problem)
2. [Technologies involved](#2-technologies-involved)
3. [High-level walkthrough](#3-high-level-walkthrough)
4. [Detailed code review](#4-detailed-code-review)
   - 4.1 [A seam for tests: `IFrameDisplay`](#41-a-seam-for-tests-iframedisplay)
   - 4.2 [A defensive driver: `Ax206Display`](#42-a-defensive-driver-ax206display)
   - 4.3 [Opening without leaking (M1)](#43-opening-without-leaking-m1)
   - 4.4 [A loop that cannot die quietly (H4)](#44-a-loop-that-cannot-die-quietly-h4)
   - 4.5 [Drawing from a stable list of pages](#45-drawing-from-a-stable-list-of-pages)
   - 4.6 [`ApplySnapshot` never empties the page list](#46-applysnapshot-never-empties-the-page-list)
   - 4.7 [Stopping: the loop owns the device (H5)](#47-stopping-the-loop-owns-the-device-h5)
   - 4.8 [Owners restart a dead pusher](#48-owners-restart-a-dead-pusher)
   - 4.9 [The tests](#49-the-tests)
5. [Self-review: five ways to improve this change](#5-self-review-five-ways-to-improve-this-change)

---

## 1. The problem

`DisplayPusher` runs a background loop that draws the theme and sends it to the 480x320 USB display, a full frame at a time
(about 400 ms per frame). Two programs use it: the elevated runner (tray app) and the editor, when no runner is running. The full
review found three problems in it.

**H4: the loop could die silently.** The loop caught only `IOException` and `InvalidOperationException`. Any other exception ended
the task, and nobody looked at the task again. The most likely trigger was undo:

- The editor's pusher draws the *same* `Theme` object that the UI thread edits.
- Undo calls `Theme.ApplySnapshot`, which did `Pages.Clear()` and then re-added the pages.
- If the loop read the pages in that instant, `theme.Pages[Math.Min(0, -1)]` threw `ArgumentOutOfRangeException`.
- The loop ended. The display froze on its last frame, and `LastError` stayed `null`, so the editor's status dot stayed green.
- The runner's supervisor only looked at `LastError`, so it never reopened the display.

**H5: `Stop` could close the USB handle under a running transfer.** Each USB transfer gives up after 3 s, and a frame is several
transfers. But `Stop` waited only 2 s for the loop. It then blanked the display and closed the handle *from the caller's thread*,
possibly while the loop was still inside `usb_bulk_write`. libusb working on a closed handle is a native access violation, which
.NET cannot catch, so the elevated runner would crash. And this happened on the error-recovery path, exactly when the display was
already misbehaving.

**M1: failed opens leaked a claimed interface.** If the first command after opening failed (common right after plugging the display
in), the claimed USB interface was never released. Every later open then failed with "busy" until the process exited.

---

## 2. Technologies involved

| Area | What it is | Why it matters here |
|---|---|---|
| **libusb-win32 bulk transfers** | `usb_bulk_write`/`usb_bulk_read` are *synchronous* native calls with a timeout (3000 ms here). | A transfer cannot be cancelled from .NET. The only safe way to stop is to let it finish or time out, and to never close the handle while it runs. |
| **`Task` with `TaskCreationOptions.LongRunning`** | Gives the work its own thread instead of a thread-pool thread. | The loop blocks for most of its life (USB and waits), so it should not hold a pool thread. |
| **`CancellationTokenSource` / `CancellationToken.WaitHandle`** | Cooperative cancellation. `token.WaitHandle.WaitOne(ms)` is a sleep that ends early when the token is cancelled. | Replaces `Thread.Sleep`, so `Stop` no longer waits out a sleep of up to 1 s. |
| **`volatile`, `Interlocked`** | Lock-free signalling between threads. `volatile` makes writes visible to the other thread; `Interlocked.Exchange` makes "do this once" atomic. | Flags such as `_blankOnStop`, and the run-once `Close()`. |
| **`lock` (`Monitor`)** | Mutual exclusion. | `Ax206Display` holds one lock around each complete USB command and around `Dispose`. |
| **`ObservableCollection<T>` / `List<T>`** | Not thread-safe. `Enumerable.ToArray()` on them reads `Count` and then calls `CopyTo`. | Copying the list while another thread changes it can throw, or leave `null`s at the end. Section 4.5 handles both. |
| **WinForms `Timer` / Avalonia `DispatcherTimer`** | Timers that tick on the UI thread. | They run the runner's `Supervise` (every 2 s) and the editor's `SlowTick` (every 1 s), which now restart a dead pusher. |
| **xUnit, fakes, `InternalsVisibleTo`** | Unit tests with a hand-written fake display. `InternalsVisibleTo` lets the test project call `internal` members. | The loop can be tested without hardware, without making the test-only constructor public. |

---

## 3. High-level walkthrough

The fix rests on one rule: **while the loop runs, only the loop touches the device.**

```
Before                                              After
------                                              -----
UI thread              loop thread                  UI thread              loop thread
Stop():                                             Stop(blank):
  cancel                 ...Blit (up to 3 s/transfer)  _blankOnStop = blank
  Wait(2 s)  ---timeout--->  still in usb_bulk_write  cancel  ------------> wakes up / finishes its transfer
  Blank()    ---------->  two threads on the device    Wait(<= 18 s)          leaves the while loop
  Dispose()  ---------->  handle closed under it!                             Close(): Blank() if asked, Dispose()
                                                    <-- task completed ------
                                                      Close()  (no-op: already done)
```

The rest of the change follows from that rule, or protects it:

1. **`Stop` only asks.** It sets the blank flag, cancels, and waits. The loop blanks and disposes on its way out.
2. **The loop never dies quietly.** A final `catch (Exception)` reports the error in `LastError`, backs off for 1 s and keeps going.
   If the loop still ends on its own (its setup failed), `IsAlive` turns false and `Faulted` turns true.
3. **Owners watch `IsAlive`.** The runner's `Supervise` and the editor's `SlowTick` reopen a dead pusher.
4. **The loop draws from a copy of the page list**, so the editor cannot shrink it mid-frame, and `ApplySnapshot` no longer empties
   the list.
5. **The driver defends itself too.** If anything ever does call `Ax206Display` after `Dispose`, it gets an `ObjectDisposedException`
   instead of a native crash.
6. **A small interface (`IFrameDisplay`)** lets tests drive the loop with a fake display.

A pusher now has these states:

```
 constructor --> Running --Stop()--> Stopping --loop exits--> Closed (blanked if asked, disposed)
                    |
                    +--setup throws--> Faulted (IsAlive = false, device still open)
                                            |
                                            +--Stop()--> Closed
```

---

## 4. Detailed code review

### 4.1 A seam for tests: `IFrameDisplay`

**File:** `src/Glance.Core/Display/IFrameDisplay.cs` (new)

The loop needs five things from a display, so that is all the interface has:

```csharp
public interface IFrameDisplay : IDisposable
{
    int Width { get; }
    int Height { get; }
    void Blit(int x, int y, int w, int h, byte[] rgb565Be);
    void SetBrightness(int level);
    /// <summary>Black frame and backlight off. Best effort: never throws.</summary>
    void Blank();
}
```

`Ax206Display` implements it, and `DisplayPusher` gets a second constructor. The public one keeps its signature, so the runner and
the editor did not change:

```csharp
public DisplayPusher(Func<Theme> theme, ISensorSource sensors, int brightness)
    : this(theme, sensors, brightness, Ax206Display.Open()) { }

internal DisplayPusher(Func<Theme> theme, ISensorSource sensors, int brightness, IFrameDisplay lcd)
```

The internal constructor is reachable from the tests through one MSBuild item in `Glance.Core.csproj`:

```xml
<InternalsVisibleTo Include="Glance.Tests" />
```

This is the "small interface plus a fake" pattern: the seam goes exactly where the hardware starts, and nothing else in the public
API changes.

### 4.2 A defensive driver: `Ax206Display`

**File:** `src/Glance.Core/Usb/Ax206Display.cs`

Even though the loop now owns the device, the driver was hardened on its own, so a future caller cannot crash the process. There are
three parts.

**One lock per complete command.** A frame is a command block, five 64 KiB data chunks and a status read, and it must not be cut in
the middle by `Dispose`:

```csharp
lock (_io)
{
    Write(cbw, 0, cbw.Length);
    for (int off = 0; off < rgb565Be.Length; off += ChunkSize)
        Write(rgb565Be, off, Math.Min(ChunkSize, rgb565Be.Length - off));
    ReadStatus();
}
```

`SetBrightness` and `ReadDimensions` take the same lock. `Blank()` calls `Blit` and then `SetBrightness`, two separate locked
commands, each wrapped in its own `try`.

**A disposed flag and a guarded handle.** Every transfer reads the handle through a property that refuses to return zero:

```csharp
private IntPtr Handle => _disposed || _handle == IntPtr.Zero ? throw new ObjectDisposedException(nameof(Ax206Display)) : _handle;
```

**`Dispose` under the same lock.** Before:

```csharp
public void Dispose()
{
    if (_handle == IntPtr.Zero) return;
    Libusb0.usb_release_interface(_handle, 0);
    Libusb0.usb_close(_handle);
    _handle = IntPtr.Zero;
}
```

After:

```csharp
public void Dispose()
{
    lock (_io)
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle == IntPtr.Zero) return;
        Libusb0.usb_release_interface(_handle, 0);
        Libusb0.usb_close(_handle);
        _handle = IntPtr.Zero;
    }
}
```

If another thread is in the middle of a frame, `Dispose` now waits for it. Each transfer gives up after 3 s, so the wait is bounded.
After `Dispose`, a transfer throws a managed exception instead of handing `IntPtr.Zero` to native code.

Why both this lock *and* loop ownership? They are two separate layers of protection. Ownership keeps the normal path simple and fast,
with no contention. The lock makes the driver safe to use without having to know about that rule.

### 4.3 Opening without leaking (M1)

There were two places where an object took a resource and then called something that could throw. The fix is the classic
`try { ... } catch { dispose; throw; }`.

In `Ax206Display.Open`, after the interface is claimed:

```csharp
var d = new Ax206Display(h);
// The first transfer can time out right after the display is plugged in. Release the claimed interface then,
// or every later open fails with "busy" until this process exits.
try { d.ReadDimensions(); }
catch { d.Dispose(); throw; }
return d;
```

In the `DisplayPusher` constructor. Before:

```csharp
_lcd = Ax206Display.Open();
_lcd.SetBrightness(brightness);
_task = Task.Run(Loop);
```

After:

```csharp
_lcd = lcd;
try { _lcd.SetBrightness(brightness); }
catch { _lcd.Dispose(); throw; }   // otherwise the claimed USB interface stays busy until the process exits
_task = Task.Factory.StartNew(Loop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
```

The constructor documents that it "takes ownership of `lcd`, also when this throws". That matters for the test, which passes a fake
whose `SetBrightness` throws and checks that the fake was disposed.

### 4.4 A loop that cannot die quietly (H4)

**File:** `src/Glance.Core/Display/DisplayPusher.cs`

The loop is now two methods. `Loop` is the task body, and it handles "the whole thing failed" and "clean up on the way out":

```csharp
private void Loop()
{
    try { Run(); }
    catch (Exception ex)
    {
        // everything inside a frame is caught in Run, so this is setup failing; report it so the owner restarts us
        _faulted = true;
        LastError = Describe(ex);
    }
    finally
    {
        // stopping: the loop owns the device, so it blanks and closes it here, after its last transfer.
        // Faulted without a stop: leave the device for Stop, which closes it once it sees the loop has ended.
        if (_cts.IsCancellationRequested) Close();
    }
}
```

`Run` holds the frame loop. The important part is the end of each frame. Before:

```csharp
catch (IOException ex) { LastError = ex.Message; Thread.Sleep(1000); }
catch (InvalidOperationException) { /* theme edited mid-frame; try again */ }
...
var wait = 200 - (int)(sw.ElapsedMilliseconds - frameStart); // cap at ~5 fps
if (wait > 0) Thread.Sleep(wait);
```

After:

```csharp
catch (IOException ex) { LastError = ex.Message; backoff = ErrorBackoffMs; }
catch (InvalidOperationException ex) when (ex is not ObjectDisposedException) { /* theme edited mid-frame; try again */ }
catch (Exception ex)
{
    // anything else (a bad theme value, a renderer bug): report it and keep going rather than freezing the display
    Interlocked.Increment(ref UnexpectedErrors);
    LastError = Describe(ex);
    backoff = ErrorBackoffMs;
}
...
var wait = Math.Max(backoff, FrameIntervalMs - (int)(sw.ElapsedMilliseconds - frameStart));
if (wait > 0) token.WaitHandle.WaitOne(wait);   // wakes straight away on Stop
```

There are four things to notice:

1. **A catch-all that keeps going.** Any error is shown in `LastError`, with its type name appended by `Describe` (for example
   `"... (Parameter 'index') (ArgumentOutOfRangeException)"`), and the loop backs off for 1 s. A frozen display now always shows up as an error.
2. **`ObjectDisposedException` is no longer swallowed.** It derives from `InvalidOperationException`, so the old catch for "theme
   edited mid-frame" also hid "the device is gone". The `when` filter sends it to the catch-all.
3. **No `Thread.Sleep`.** `token.WaitHandle.WaitOne(wait)` sleeps for the same time but returns at once when `Stop` cancels. That is
   why a normal stop is now faster than before.
4. **Brightness is retried.** `if (wanted != applied) { _lcd.SetBrightness(wanted); applied = wanted; }` records the level only after
   the call succeeded (it used to record it first, so a failed change was lost).

Two new properties tell owners what happened:

```csharp
public bool IsAlive => !_task.IsCompleted;
public bool Faulted => _faulted;
```

### 4.5 Drawing from a stable list of pages

The loop used to index `theme.Pages` directly, while the UI thread could change it:

```csharp
_pageIndex = NextPageIndex(theme, _pageIndex, ref pageSince, frameStart, _pinned, Interlocked.Exchange(ref _step, 0));
var page = theme.Pages[Math.Min(_pageIndex, theme.Pages.Count - 1)];
```

Now it copies the list once per frame, and everything in that frame uses the copy:

```csharp
var pages = StablePages(theme);
if (pages.Length > 0)   // only for an instant while the editor swaps pages; keep the last picture
{
    _pageIndex = NextPageIndex(pages, theme.AutoRotate, _pageIndex, ref pageSince, frameStart, _pinned, Interlocked.Exchange(ref _step, 0));
    var page = pages[Math.Clamp(_pageIndex, 0, pages.Length - 1)];
    ...
}
```

Copying a list that another thread is changing is still not safe in general, so `StablePages` handles the two ways it can go wrong:

```csharp
internal static ThemePage[] StablePages(Theme theme)
{
    for (int attempt = 0; ; attempt++)
    {
        try
        {
            var copy = theme.Pages.ToArray();
            // a page removed between reading the count and copying leaves an empty slot at the end
            return Array.IndexOf(copy, null) < 0 ? copy : Array.FindAll(copy, p => p is not null);
        }
        catch (ArgumentException) when (attempt < 3) { /* the list grew while copying; try again */ }
    }
}
```

`ToArray()` on a collection allocates `Count` slots and then calls `CopyTo`. If the list grew in between, `CopyTo` throws
`ArgumentException` and the copy is retried. If it shrank, the last slots stay `null`, and those are filtered out.

This is a pragmatic step, not a full immutable snapshot. The page *objects* are still live, and so are their `Widgets` collections.
The renderer's `foreach` over widgets still relies on the "collection was modified" `InvalidOperationException`. That is the case the
narrower catch is for.

`NextPageIndex` gets an overload over `IReadOnlyList<ThemePage>`. The old `Theme` overload forwards to it, so the virtual display
window and the existing tests did not change. It also gained a guard for a negative index: `if (current >= n || current < 0) current = 0;`.

### 4.6 `ApplySnapshot` never empties the page list

**File:** `src/Glance.Core/Themes/Theme.cs`

Before:

```csharp
Pages.Clear();
foreach (var p in t.Pages) Pages.Add(p);
```

After:

```csharp
// Replace page by page instead of Clear() + Add: a display pusher may be drawing this same theme on another thread,
// and the list must never be empty while it looks (Normalize guarantees the snapshot has at least one page).
for (int i = 0; i < t.Pages.Count; i++)
{
    if (i < Pages.Count) Pages[i] = t.Pages[i];
    else Pages.Add(t.Pages[i]);
}
while (Pages.Count > t.Pages.Count) Pages.RemoveAt(Pages.Count - 1);
```

The list is overwritten in place, grown, or trimmed from the end, so its count never drops below one. The two fixes overlap on
purpose: the loop would survive an empty list anyway (4.5), but other code that reads `theme.ActivePage` or `theme.Widgets` would not.

Changing how a collection is mutated changes the events it raises: there is no longer one `Reset`, but `Replace`, `Add` and `Remove`
events. That is safe here because the editor's undo wiring (`OnWidgetsChanged`, then `HookAll`) ignores the kind of event and
reconciles every page and widget. Always check the subscribers when you make a change like this.

### 4.7 Stopping: the loop owns the device (H5)

Before:

```csharp
public void Stop(bool blank)
{
    _cts.Cancel();
    try { _task.Wait(2000); } catch { }
    if (blank) _lcd.Blank();
    _lcd.Dispose();
}
```

After:

```csharp
public bool Stop(bool blank) => Stop(blank, StopTimeout);

internal bool Stop(bool blank, TimeSpan timeout)
{
    if (blank) _blankOnStop = true;   // set before cancelling, so the loop sees it when it wakes
    _cts.Cancel();
    bool finished;
    try { finished = _task.Wait(timeout); }
    catch (AggregateException) { finished = true; }   // Loop catches everything, but be safe
    if (finished) Close();   // no-op when the loop already closed it; needed when the loop had faulted on its own
    return finished;
}

private void Close()
{
    if (Interlocked.Exchange(ref _closed, 1) != 0) return;
    if (_blankOnStop) { try { _lcd.Blank(); } catch { /* best effort */ } }
    try { _lcd.Dispose(); } catch { /* best effort */ }
}
```

Walk through the cases:

- **Normal stop.** `Stop` cancels. The loop wakes (or finishes the frame it is sending), leaves `Run`, and its `finally` calls
  `Close()`, which blanks if asked and disposes. The task completes, `Stop`'s `Wait` returns `true`, and `Stop`'s own `Close()` does
  nothing.
- **The loop had already faulted.** Nobody has closed the device yet (the `finally` closes only when cancellation was requested).
  `Wait` returns at once and `Stop` calls `Close()` itself. That is safe, because the loop thread is gone.
- **A transfer is stuck.** `Wait` times out and `Stop` returns `false` *without touching the device*. The loop is still cancelled,
  so when the transfer gives up, it leaves the loop and its `finally` closes the device. It is never closed underneath a transfer.
- **Called twice.** `Interlocked.Exchange(ref _closed, 1)` lets only the first `Close()` act, so `Dispose()` after `Stop(false)` does
  not blank or dispose again.

How long can `Stop` take? The bound is `StopTimeout`, 18 s (6 transfers x 3 s). In practice (worked out from the code, not measured
on the device):

| Situation | `Stop(blank: true)` returns after |
|---|---|
| Loop waiting between frames | ~0.5 s (wakes at once, then a black frame and the backlight command) |
| Loop in the middle of a frame | under 1 s (the rest of that frame, then the blank) |
| Display stalled, every transfer times out | ~9 s (3 s for the transfer in flight, 3 s for the black frame, 3 s for the backlight) |

This matters for the sibling branch `feature-shutdown-blank`, which stops the pusher at shutdown and sleep. The normal case is better
than before: no sleep to wait out, and the black frame always comes after the last frame, never in parallel with it. The stalled case
is too slow for sleep. See self-review item 1.

### 4.8 Owners restart a dead pusher

**Runner** (`src/Glance.Runner/RunnerContext.cs`, in `Supervise`, every 2 s under `_gate`):

```csharp
else if (!_pusher.IsAlive)
{
    // the loop ended without being stopped; treat it like a failing display and reopen it on the next tick
    Log("Display loop stopped; reopening: " + (_pusher.LastError ?? "unknown error"));
    DisposePusher(blank: false);
}
```

`DisposePusher` calls `Stop(false)`, which returns at once for a dead loop. On the next tick, `_pusher is null`, so a new pusher is
opened. The branch sits *before* the existing "LastError for more than 6 s" branch, so a dead loop is noticed on the very next tick.

**Editor** (`src/Glance.App/MainWindow.cs`, in `SlowTick`, every 1 s on the UI thread):

```csharp
if (_pusher is { IsAlive: false } dead) RestartDeadPusher(dead);

private void RestartDeadPusher(DisplayPusher dead)
{
    var why = dead.LastError ?? "unknown error";
    dead.Stop(blank: false);   // the loop has ended, so this returns at once
    _pusher = null;
    try
    {
        _pusher = new DisplayPusher(() => _theme, _hub, _settings.Brightness) { PinnedPage = DesiredPin };
        _status.Text = "The display stopped (" + why + "); reopened it.";
    }
    catch (Exception ex)
    {
        _send.IsChecked = false;   // runs ToggleSend(false), which sets its own status; ours goes after it
        _status.Text = "The display stopped (" + why + ") and could not be reopened: " + ex.Message;
    }
}
```

Note the comment on `_send.IsChecked = false`. Unchecking the toggle re-enters `ToggleSend(false)`, which writes "Stopped sending.",
so the real message has to be set afterwards. This is the same trap as finding L9 in the full review.

### 4.9 The tests

**File:** `src/Glance.Tests/DisplayPusherTests.cs` (new, 11 tests)

`FakeDisplay` is the heart of them. It records what a real display would care about:

```csharp
public void Blit(int x, int y, int w, int h, byte[] rgb565Be)
{
    if (_disposed) Interlocked.Increment(ref _usedAfterDispose);
    _inTransfer = true;
    BlitStarted.Set();
    if (BlitDelayMs > 0) Thread.Sleep(BlitDelayMs);   // imitates a slow or stalled USB link
    Interlocked.Increment(ref _blits);
    _inTransfer = false;
    Log("blit");
}

public void Dispose()
{
    if (_inTransfer) Interlocked.Increment(ref _disposedDuringTransfer);
    DisposeThread = Environment.CurrentManagedThreadId;
    ...
}
```

With `DisposedDuringTransfer`, `UsedAfterDispose` and `BlankThread`, the H5 bug becomes a plain assertion. This test would fail
against the old `Stop`, because its 2 s wait would dispose in the middle of the 2.6 s transfer:

```csharp
var lcd = new FakeDisplay { BlitDelayMs = 2600 };
var p = new DisplayPusher(Ticking, NoSensors, 7, lcd);
Assert.True(lcd.BlitStarted.Wait(5000), "the loop never started a transfer");

var sw = Stopwatch.StartNew();
Assert.True(p.Stop(blank: false));
Assert.True(sw.ElapsedMilliseconds >= 2000, "Stop returned before the transfer could have finished");
Assert.Equal(0, lcd.DisposedDuringTransfer);
```

Two other tricks are worth copying:

- **A theme that changes every frame.** `Ticking()` is a clock with milliseconds (`"HH:mm:ss.fff"`), so the skip-identical-frames
  logic never stops the loop from sending.
- **Errors injected at the right layer.** One test throws from the theme callback, before rendering. Another uses a sensor source
  whose `Snapshot` throws, which fails from *inside* `ThemeRenderer.Render`. Both check that the loop keeps going and recovers once
  the error stops.

The commit says each fix was broken on purpose and a test failed. A review re-ran those mutation checks:

- Removing the catch-all, `Stop`'s `Close()` or the new `ApplySnapshot` makes the expected tests fail.
- The undo/redo stress test, however, still passes with the original H4 bug restored. The loop looks at the theme only every 200 ms,
  so it practically never lands in the microsecond window. The deterministic tests are the ones that actually cover the fix.
- Lesson: a race test needs a high sampling rate (see self-review item 4), or a deterministic hook into the window.

---

## 5. Self-review: five ways to improve this change

### 1. Let the caller bound `Stop`, and do not blank a display that just timed out

`StopTimeout` is 18 s, and the overload that takes a timeout is `internal`. At sleep, Windows allows about 2 s, but with a stalled
display `Stop(blank: true)` spends ~9 s, 6 s of which go to a black frame and a backlight command that cannot succeed. Make
`Stop(bool blank, TimeSpan timeout)` public for the power handlers. In `Close()`, skip `Blank()` when the loop's last transfer failed
with an `IOException`. That cuts the stalled case to about one transfer timeout. Consider a `StopAsync` as well, so the runner's
`Supervise` does not hold `_gate` on the UI thread while it waits (review L8).

### 2. Tell USB errors apart from render errors

`LastError` mixes "the display is not answering" with "this theme throws while drawing". The runner reopens USB after 6 s of any
error, so a theme bug now causes a USB reopen (plus a reload of images and video) about every 8 s, forever, with a log line each time.
Expose the kind of error (for example `LastUsbError` and `LastRenderError`), and reopen only for USB errors. The decision "should the
owner reopen?" is pure logic; move it into Core next to `NextPageIndex` and test it.

### 3. Close the remaining silent-freeze paths

- A theme file with `"Pages": [null]` passes `Normalize`. `StablePages` then filters out every page, and the loop draws nothing with
  `LastError = null`: a silent freeze, just as in H4. Drop `null` pages and widgets in `Normalize`.
- A repeating `InvalidOperationException` is still swallowed without an error or a back-off.

In both cases, count the consecutive frames that drew nothing and report an error after about 1-2 s. Going further, render from an
immutable per-frame snapshot of the theme, as self-review item 2 of `docs/WALKTHROUGH.md` suggests. Then the race-tolerant code could go.

### 4. Make the loop's timing injectable, so the tests are fast and actually catch races

The frame interval (200 ms) and the back-off (1 s) are constants. Because of that, the recovery tests each take about a second, the
suite went from about 2 s to about 7 s, and the undo/redo race test samples so rarely that it cannot catch the bug it is named after.
Pass an internal options record (frame interval, back-off, stop timeout) to the test constructor. Then run the stress test at a 1 ms
interval: a review checked that it then fails 3/3 runs against the old code and passes 3/3 against the new code. Add the missing test
for the `Stop`-timed-out contract: it returns `false`, and the loop later blanks and disposes, after its transfer.

### 5. Respect `Stop`'s answer, and fit the restart into the power policy

`Stop` now returns `false` when a transfer was stuck, but every caller ignores it and immediately opens a new pusher. That open fails
with "Could not claim the display interface" until the old loop lets go. Keep the old pusher in a "closing" slot and open again only
when it is no longer `IsAlive`. When `feature-shutdown-blank` is merged, rewrite the editor's `RestartDeadPusher` on top of that
branch's `ReleasePusher`/`TryStartPusher`, with a `CompareExchange` on `_pusher`. Otherwise a dead pusher can be reopened while
Windows sleeps or after the session ended. Git will merge the two branches without a conflict, so this has to be done by hand.

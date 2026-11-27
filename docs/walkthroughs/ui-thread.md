# Keeping the UI threads free: a walkthrough of the M7 fix

This walkthrough is for a mid-level developer who wants to understand one change in Glance: moving slow work (USB, named
pipes, process lists, the Service Control Manager, big file copies) off the UI threads of the editor (`Glance.App`, Avalonia)
and the tray app (`Glance.Runner`, WinForms). It covers review finding M7 and finding L6 of the autostart review. The change
is on branch `claude/fix-ui-thread-280814`; the author's own note is `docs/design/fix-ui-thread.md`, and the independent
review is `docs/reviews/review-ui-thread.md`.

It assumes you know C# and have written `async`/`await` code. You do not need to know Avalonia or WinForms internals.

**Contents**

1. [Technologies: UI threads, dispatchers and async/await](#1-technologies-ui-threads-dispatchers-and-asyncawait)
2. [The problem](#2-the-problem)
3. [The fix at a glance](#3-the-fix-at-a-glance)
4. [Code review with samples](#4-code-review-with-samples)
5. [How it was proven](#5-how-it-was-proven)
6. [Self-review: five ways to improve it](#6-self-review-five-ways-to-improve-it)

---

## 1. Technologies: UI threads, dispatchers and async/await

### One thread draws everything

Desktop UI frameworks are single-threaded. Avalonia and WinForms both have one **UI thread** that runs a **message loop**:
take the next message (mouse move, key press, "repaint", timer tick, "run this callback"), handle it, repeat. Every click
handler, every `DispatcherTimer.Tick` and every layout pass runs on that one thread, one after the other.

That gives the one rule that matters here: **while a handler runs, nothing else in that window happens.** If a click
handler waits 18 s for a USB transfer, the window does not repaint, does not react to the mouse, and after about 5 s
Windows marks it "Not responding". The tray app is the same: a WinForms `ContextMenuStrip` cannot open while the UI thread is
stuck in `Supervise`.

Controls also belong to their thread. Avalonia throws `InvalidOperationException` ("Call from invalid thread") when a
background thread reads or writes a control. WinForms is less strict for some properties, but the rule is the same.

### Dispatchers and synchronization contexts

To get work *onto* the UI thread from elsewhere you post a message to its loop:

- Avalonia: `Dispatcher.UIThread.Post(...)` / `InvokeAsync(...)`.
- WinForms: `control.BeginInvoke(...)` (async, returns at once) or `control.Invoke(...)` (waits for the UI thread).

Both frameworks also install a **`SynchronizationContext`** on their UI thread. It is the abstraction that `await` uses.

### What `await` actually does

```csharp
var result = await Task.Run(() => SlowThing());   // 1. SlowThing runs on a thread-pool thread
_status.Text = result;                           // 2. this line runs on the UI thread again
```

When the awaited task is not finished, the method returns to its caller at once, and the UI thread goes back to its loop.
When the task completes, the rest of the method is posted to the **captured `SynchronizationContext`**, so line 2 runs on
the UI thread again, where touching `_status` is allowed. You get a responsive UI and safe control access without writing
any `Post` calls yourself.

Two things follow:

- `async` alone does not move work anywhere. An `async` method runs synchronously until its first `await` of an unfinished
  task. Slow *synchronous* code (`File.Copy`, `Process.GetProcesses`, a blocking pipe read) has to be wrapped in
  `Task.Run` to leave the UI thread.
- **Sync-over-async deadlocks.** If the UI thread calls `task.Wait()`, `.Result` or `.GetAwaiter().GetResult()` on a task
  whose inner `await`s captured the UI context, the inner continuation is posted to the UI thread. That thread is blocked
  waiting for the task, so the task can never finish. This is the Glance 0.2.0 tray deadlock fixed on the sibling branch
  `claude/fix-tray-deadlock-a14e66` (`ConfigureAwait(false)` in `Glance.Core`). Moving the blocking call to a pool thread
  avoids it too: pool threads have no synchronization context, so there is nothing to deadlock on.

---

## 2. The problem

Review M7 found blocking work on both UI threads. The service redesign had already removed some of it (the
per-second `schtasks.exe` call of L6 is gone). The author re-checked the rest on current `main`. Some of what was left:

| Where | What blocked the UI thread |
|---|---|
| Editor: Send to display, Display off | `_display.TryOpen` (service pipe: 0.5 s connect, up to 10 s for the hello; or USB) and `_display.Release` (up to 18 s with a stuck transfer) |
| Editor: `SlowTick`, every second | `RunnerClient.IsRunning` lists every process on the PC; `Ffmpeg.Find()` searched PATH and winget folders every 5 s while FFmpeg was missing |
| Editor: Save, Open `.glance`, AIDA64 import | copying every picture and video, unzipping up to 1 GB, importing twice |
| Setup window, every 3 s | the setup context asked the Service Control Manager and listed processes |
| Tray app: `Supervise`, every 2 s | opening the display, and stopping it for up to 18 s, while the menu and the editor's PAUSE/RESUME waited behind it |

---

## 3. The fix at a glance

Three small, tested building blocks in `Glance.Core`, used by the editor and the tray:

- **`SerialWork`**: a queue that runs blocking items on the thread pool, **one at a time, in order**, and never on the
  caller's thread. Display open, release, restart and blank all go through one instance, so "release" always finishes before
  the next "open".
- **`BackgroundProbe<T>`**: a slow yes/no answer ("is the tray app running?", "is FFmpeg there?") that a timer can read
  without waiting. It starts at most one background check at a time.
- **`ThemeSaveJob`**: a save in three steps. It copies the theme on the UI thread, saves the copy on the pool, then applies
  the new paths back on the UI thread.

Plus `[WorkerThread]`, an attribute that marks methods which block. A source-scanning test, `UiThreadAuditTests`, checks
that known blocking APIs and `[WorkerThread]` methods are only called from off-thread code.

---

## 4. Code review with samples

### 4.1 `SerialWork`: ordered work off the UI thread

```csharp
public Task<T> Run<T>(Func<T> work)
{
    ArgumentNullException.ThrowIfNull(work);
    lock (_lock)
    {
        Interlocked.Increment(ref _pending);
        // not ExecuteSynchronously: even when the queue is empty, the item is queued to the pool, never run inline here
        var next = _tail.ContinueWith(_ =>
        {
            try { return work(); }
            finally { Interlocked.Decrement(ref _pending); }
        }, CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
        _tail = next;
        return next;
    }
}
```

Points worth noticing:

- **Chaining on `_tail`** gives the order. Each item is a continuation of the previous one, whether that one succeeded or
  failed (`ContinueWith` with no `OnlyOnRanToCompletion`). An exception therefore faults only its own task, and the next item
  still runs.
- **`TaskScheduler.Default`** matters. Without it, `ContinueWith` uses `TaskScheduler.Current`, which inside some callbacks
  can be a UI scheduler. Passing it explicitly means "always the pool".
- **No `ExecuteSynchronously`.** That option would let the item run inline on whatever thread completes the previous one,
  and when the queue is empty the "previous one" is `Task.CompletedTask`. Then the item would run on the caller, which is
  the UI thread.
- The returned task is awaited on the UI thread, so code after `await _displayWork.Run(...)` is back on the UI thread.

A simpler alternative is a `SemaphoreSlim(1)` with `await sem.WaitAsync()` inside `Task.Run`. That gives mutual exclusion but
**not FIFO order**, which is why a continuation chain was used.

### 4.2 Send to display: generations against stale results

Once the work is asynchronous, the user can click again while it runs. The editor handles that with a counter:

```csharp
private void ToggleSend(bool on)
{
    var generation = ++_sendGeneration;
    _ = _displayWork.Run(() => _display.Release(blank: true, DisplayPusher.StopTimeout));   // also releases an open still running
    StopLive();
    if (!on) { _status.Text = "Stopped sending."; return; }
    _ = StartSendingOrLiveAsync(generation);
}

private async Task StartSendingOrLiveAsync(int generation)
{
    _status.Text = "Connecting to the display...";
    var runner = await _displayWork.Run(() => RunnerClient.IsRunning);
    _trayProbe.Report(runner);
    if (generation != _sendGeneration) return;
    if (runner) { StartLive(); return; }
    await StartSendingAsync(generation);
}
```

- Every toggle queues a **release first**. Untick while an open is still running, and the release runs right after the
  open, so the device is never left claimed.
- `_sendGeneration` is only touched on the UI thread, so it needs no lock. After each `await`, the code checks whether
  its toggle is still the latest; if not, it changes no UI. This is the usual pattern against "a late answer overwrites a
  newer state".
- The `_ = ...` discards are fire-and-forget. That is fine for ordering, but an exception in them is never observed (review
  finding L1).

### 4.3 Reading controls before handing work over

`OpenPusher` used to read `DesiredPin`, which reads an Avalonia control. On a pool thread that throws. The fix reads
everything on the UI thread first and passes plain values to the worker:

```csharp
private (int Brightness, int? Pin) OpenArgsNow() => (_settings.Brightness, DesiredPin);
private (int Brightness, int? Pin) _openArgs;   // set by the _displayWork item that opens, just before it opens

[WorkerThread]
private DisplayPusher OpenPusher() => new(() => _theme, _hub, _openArgs.Brightness) { PinnedPage = _openArgs.Pin };

var open = OpenArgsNow();                                                  // UI thread
var (opened, error) = await _displayWork.Run(() => { _openArgs = open; return (_display.TryOpen(out var e), e); });
```

The field write and the read in `OpenPusher` both happen inside the same `SerialWork` item, so they cannot interleave with
another open. The general rule is: **snapshot UI state on the UI thread, hand values (not controls) to the worker.**

### 4.4 `BackgroundProbe`: a timer that never waits

```csharp
public bool Poke()
{
    if (Interlocked.Exchange(ref _running, 1) == 1) return false;
    int generation;
    lock (_lock) generation = _generation;
    var task = Task.Run(() =>
    {
        try
        {
            var v = _probe();
            lock (_lock) if (generation == _generation) _value = v;   // a Report made meanwhile is newer
        }
        catch { /* keep the last value; the next poke tries again */ }
        finally { Volatile.Write(ref _running, 0); }
    });
    lock (_lock) _last = task;
    return true;
}
```

The 1 s `SlowTick` calls `_trayProbe.Poke()` and reads `_trayProbe.Value`. The answer is at most a tick old, and the
process list never runs on the UI thread. `Interlocked.Exchange` keeps it to one check at a time, so a slow machine does
not pile up probes. `Report(value)` bumps `_generation`, so a fresh answer from `ToggleSend` wins over an older probe that
finishes later. That is the same stale-result problem as in 4.2, solved with the same counter idea.

### 4.5 `ThemeSaveJob`: saving a copy, then applying the result

Saving copies every picture and video into the theme folder. Doing that on the pool while the user keeps editing the *live*
theme would be a data race. So the job works on a detached copy:

```csharp
public static async Task<ThemeSaveJob> SaveAsync(Theme live, string path)
{
    var job = Begin(live, path);   // UI thread: JSON round trip of the live theme
    await Task.Run(job.Run);       // pool: Theme.Save on the copy (copies files, writes theme.json)
    job.Apply();                   // UI thread again: the live theme takes the new paths and folder
    return job;
}
```

`Apply` copies the new `ImagePath`/`FontFile` values onto the live widgets **only where the user did not change them
meanwhile**, and sets `ChangedWhileSaving` so the status can say "save again to keep them". While a save runs,
`BeginFileWork` disables the file buttons and Undo/Redo. Undo would swap in widgets the save does not know about. Closing
the window is refused unless Windows is shutting down.

### 4.6 The tray app: a worker for the supervisor

The tray had one 2 s timer calling `Supervise()`, which opened or stopped the display under `_gate`. Now:

```csharp
private void Supervise()
{
    if (_quitting || Interlocked.Exchange(ref _superviseQueued, 1) == 1) return;
    _work.Run(() => { Volatile.Write(ref _superviseQueued, 0); SuperviseNow(); });
}
```

- **Collapsing**: while a transfer is stuck, the timer keeps firing. The `_superviseQueued` flag makes sure at most one
  `SuperviseNow` waits in the queue instead of dozens.
- **Status back to the UI thread**: `SetStatus` now calls `_ui.BeginInvoke(...)`. It does not use `Invoke`: the worker holds
  `_gate`, and an `Invoke` that waits for a UI thread which itself waits for `_gate` is a classic lock-order deadlock.
- **Shared fields** that cross threads became `volatile` (`_paused`, `_quitting`) or `Interlocked` (the settings re-read
  time as ticks), and the watcher list is locked.
- `Quit` still blanks on the UI thread on the way out, marked `// ui-thread-ok:`. It now waits on `_gate` if the worker
  holds it (review L2).

### 4.7 Small windows, same rules

`ServiceWindow` no longer lists processes in its constructor; it shows "Tray app: checking..." until the first refresh,
which runs in `Task.Run`. A `BeginBusy()` guard disables all three action buttons *at once*, so a double click cannot start
two actions. `SetupWindow` builds its context inside the `Task.Run` that runs the checks, not before it.

---

## 5. How it was proven

- **Audit test** (`UiThreadAuditTests`): 21 rules, one regex per blocking API. The scanner blanks comments and strings,
  marks the text inside `Task.Run(...)`, `*Work.Run(...)`, `new BackgroundProbe<...>(...)` and `[WorkerThread]` bodies as
  "off the UI thread", and reports every other match unless the line carries `// ui-thread-ok: reason`. Against `main` it
  fails 18 of 21 rules (reproduced by the reviewer); on the branch all pass.
- **Timing tests** (`UiThreadTests`): `TestUiThread` is a thread with its own `SynchronizationContext`, so `await` comes back
  to it just as with Avalonia. Each test measures how long a handler holds it with slow fakes (a 1.5 s stuck transfer, a 1 s
  open, a 300 MB file): about 1-1.5 s called directly, under 150 ms through the seam.
- **Mutation checks**: the author broke each seam on purpose and checked that a test failed. Two mutations survived at first,
  and the tests were tightened.

A source scanner is a cheap, readable guard, but it is not a compiler. The reviewer got three blocking calls past it: one
inside an interpolated string, one posted back to the UI thread from inside `Task.Run`, and one with a space before `.Query(`.
Treat a green audit as "none of the known patterns", not "nothing blocks".

---

## 6. Self-review: five ways to improve it

1. **Wait for the display queue on exit.** `Closing` should queue the exit blank on `_displayWork` unconditionally, so a
   release or open that is still queued is finished and waited for (bounded) before the process ends (review M1).
2. **Observe fire-and-forget tasks.** Replace `_ = SomethingAsync()` with a helper that logs or shows the exception, so a
   failed open never leaves "Connecting to the display..." on screen for ever (review L1).
3. **Make the audit harder to fool, or use Roslyn.** Keep interpolation holes when blanking strings, treat
   `Post`/`InvokeAsync`/`BeginInvoke` lambdas as UI code, and add a rule for `.Wait()`, `.Result` and `GetResult()`.
   Better still, a small Roslyn analyzer can follow method groups and delegates that a regex cannot (review L3).
4. **Pass cancellation and show progress for long file work.** Saves and imports can take minutes on slow disks. A
   `CancellationToken` and an `IProgress<double>` through `ThemeSaveJob` and the importers would let the status show
   "Copying 3 of 12" and offer Cancel, instead of refusing to close.
5. **One design per problem across branches.** The sibling deadlock branch opens the tray's display with a
   `BackgroundOpener`; this branch runs the whole supervisor on `SerialWork`. Pick one when merging, keep the other branch's
   Core `ConfigureAwait(false)` fixes, and give the two `UiThreadAuditTests` classes distinct names (review M2, M3).

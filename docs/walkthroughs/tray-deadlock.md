# The tray deadlock: a walkthrough of the 0.2.0 "black display" fix

This walkthrough is for a mid-level developer. It explains why the Glance 0.2.0 tray app never put anything on the display, and how
one change fixed that. It covers what a `SynchronizationContext` is, what `ConfigureAwait(false)` really does, why a blocked UI thread
cannot run your continuation, how a WinForms (STA) thread waits, and how the named pipes between the tray app and the service behave.
It ends with five ways the change could still be better.

It assumes you know C# and `async`/`await` at the level of "I use them every day". You do not need to know the rest of Glance. For the big
picture, read `docs/WALKTHROUGH.md` and the architecture section of `AGENTS.md` (tray app, editor and service, and the pipes between them).

**The change:** branch `claude/fix-tray-deadlock-a14e66`, commits `932a35f` and `25f9c9f` on top of `main` `8d2d13c`.
**Files:** `src/Glance.Core/Service/{RemoteFrameDisplay,DisplayWire,DisplayServer}.cs`, `src/Glance.Core/Display/BackgroundOpener.cs`,
`src/Glance.Core/Core.globalconfig`, the `ConfigureAwait(false)` sweep in `src/Glance.Core/Sensors/*.cs`, `src/Glance.Runner/RunnerContext.cs`,
and the tests `src/Glance.Tests/{UiThreadDeadlockTests,UiThreadAuditTests,ConfigureAwaitGuardTests}.cs`.
**Author's proof:** `docs/design/fix-tray-deadlock.md`. **Independent review:** `docs/reviews/review-tray-deadlock.md`.

**Contents**

1. [The problem](#1-the-problem)
2. [Technologies involved](#2-technologies-involved)
3. [Why the tests did not catch it](#3-why-the-tests-did-not-catch-it)
4. [High-level walkthrough of the fix](#4-high-level-walkthrough-of-the-fix)
5. [Detailed code review](#5-detailed-code-review)
6. [Self-review: five ways to improve the change](#6-self-review-five-ways-to-improve-the-change)

---

## 1. The problem

In 0.2.0 the USB display moved behind a Windows service (LocalSystem). The user's tray app (`Glance.Runner`, a WinForms
`ApplicationContext`) renders the theme and sends whole frames to the service over the named pipe `Glance.Service.Display`. On the client
side, `RemoteFrameDisplay` is "just another `IFrameDisplay`": `DisplayPusher` calls `Blit(...)`, and that call blocks until the service
answers.

On the user's PC the display stayed black. No error appeared, and the tray app looked alive. This is what happened, in order:

1. `RunnerContext`'s constructor forced a window handle (`_ = _ui.Handle;`). Creating the first WinForms control installs a
   `WindowsFormsSynchronizationContext` on the current thread, which is the UI thread.
2. Still inside the constructor, it called `Supervise()`, which ran `new DisplayPusher(...)` → `DisplayConnector.Open()` →
   `RemoteFrameDisplay.Connect` → `Request(Hello)`.
3. `Request` was synchronous code over async I/O:

   ```csharp
   // main, RemoteFrameDisplay.Request
   using var cts = new CancellationTokenSource(_replyTimeout);
   _pipe.WriteAsync(bytes, cts.Token).AsTask().GetAwaiter().GetResult();
   reply = DisplayWire.ReadReplyAsync(_pipe, cts.Token).GetAwaiter().GetResult();
   ```

4. `ReadReplyAsync` is an `async` method in Glance.Core that awaits `ReadUpTo`, which awaits `stream.ReadAsync`. None of those awaits
   said `ConfigureAwait(false)`. So when the pipe read finished, each continuation was **posted to the UI thread's context**.
5. The UI thread was parked inside `GetResult()`, waiting for exactly that continuation. It never pumped messages again, so the
   continuation never ran. That is a deadlock, and it is permanent.
6. Even the 10 s reply timeout could not break it. The timer did cancel the token, but the code that reacts to the cancellation is also
   a continuation posted to the same blocked thread.
7. The constructor never returned. The control pipe for the editor, the file watchers and the timers that come after `Supervise()` never
   started, and no frame was ever sent.

The editor's "Send to display" had the same flaw on Avalonia's UI thread: `PusherSlot.TryOpen` → `new DisplayPusher` → the same `Connect`.

---

## 2. Technologies involved

| Area | Technology | Role in this change |
|---|---|---|
| Threading | **`SynchronizationContext`** (`WindowsFormsSynchronizationContext`, Avalonia's dispatcher context) | Decides where an `await` resumes. On a UI thread, it means "post back to me". |
| Async | **`await`, `ConfigureAwait(false)`**, `Task.Run`, `Task.Wait(timeout)`, `GetAwaiter().GetResult()` | The bug is sync-over-async; the fix is to stop resuming on the caller's context and to block only on pool work. |
| UI threads | **STA threads and message pumping** (WinForms `Application.Run`, Avalonia dispatcher) | Posted work runs only when the UI thread returns to its message loop. |
| IPC | **Named pipes** (`NamedPipeClientStream`/`NamedPipeServerStream`, `PipeOptions.Asynchronous`, overlapped I/O) | The tray app and editor talk to the service (`Glance.Service.Display`) and the editor to the tray (`Glance.Runner.Control.*`). |
| Analysis | **Roslyn analyzer CA2007** through a global analyzer config (`Core.globalconfig`) | Makes a missing `ConfigureAwait` a build error in Glance.Core. |
| Tests | **xUnit** plus a home-made `UiLikeContext` | Reproduces "a UI thread that is blocked and never pumps" without WinForms. |

### 2.1 `SynchronizationContext` in one paragraph

When you `await` an unfinished task, the compiler stores "the rest of the method" as a continuation. By default it also captures
`SynchronizationContext.Current`. When the task completes, the continuation is **posted** to that context instead of running on whatever
thread completed the task. On a UI thread the context is a message queue: `Post` adds a message, and the message runs the next time the
UI thread goes through its message loop. That is what makes `label.Text = await GetAsync();` safe in UI code: you come back to the UI thread.
On a thread-pool thread `Current` is null, and the continuation just runs on some pool thread.

### 2.2 `ConfigureAwait(false)`

`await task.ConfigureAwait(false)` means "do not capture the context; resume wherever the task completes". It applies only to **that one
await**. If a library method has ten awaits and nine say `ConfigureAwait(false)`, the tenth can still post back to the caller. That is
why the rule in library code is "every await" and not "most awaits". It also does not help if the task is already finished when you
await it (no continuation is needed), which is why a missing `ConfigureAwait` can hide for a long time on fast paths.

### 2.3 Sync-over-async, and why it deadlocks only on some threads

`task.GetAwaiter().GetResult()`, `task.Result` and `task.Wait()` block the current thread until the task finishes. Whether that is safe
depends on **who must run the task's continuations**:

- On a pool thread (no context): continuations run on other pool threads, the task finishes, and the wait returns.
- On a UI thread (a context that posts to itself): a continuation that captured the context needs *this* thread, and this thread is
  blocked. It is a deadlock.

So the same Core code worked in the service (no context), in xUnit (see section 3) and in the editor's background threads, and hung
only when the tray app's constructor or the editor's UI thread called it.

### 2.4 STA threads and message pumping

WinForms and Avalonia's Windows backend run their UI on an STA (single-threaded apartment) thread. When an STA thread blocks in
`Task.Wait`/`WaitHandle.WaitOne`, the CLR uses a *pumping* wait (`CoWaitForMultipleHandles`). That wait dispatches some COM and
`SendMessage` traffic so cross-apartment calls do not hang. It does **not** run messages that were `PostMessage`d, and that is how
`WindowsFormsSynchronizationContext.Post` delivers continuations. So "it pumps while waiting" does not save you here. It is also why a
blocking wait on a UI thread can cause surprising re-entrancy (a COM callback running in the middle of your method). That was true before
and after this change.

### 2.5 Named pipes as Glance uses them

- `RemoteFrameDisplay` opens the service pipe with `PipeOptions.Asynchronous`, so reads and writes are overlapped I/O and a
  `CancellationToken` really cancels them (the runtime calls `CancelIoEx`).
- `Connect(timeoutMs)` is synchronous and has its own timeout (500 ms): no pipe means `TimeoutException`, and `DisplayConnector`
  turns that into "use USB" or "the service is starting, try again".
- The wire format (`DisplayWire`) is one request header plus payload, then one reply (`status`, `length`, UTF-8 message). A
  480 x 320 RGB565 frame is 300 KB, so one `Blit` is one large write plus a short read.
- `ControlPipeClient` (editor → tray) opens its pipe **without** `Asynchronous`. Its `ReadLineAsync` is then a blocking read run on the
  pool, and the caller waits with `task.Wait(replyTimeoutMs)`. That is safe on a UI thread because BCL code uses `ConfigureAwait(false)`
  internally and the wait has a timeout. The author left it unchanged and added a test for it.

---

## 3. Why the tests did not catch it

The suite already ran `DisplayServer` and `RemoteFrameDisplay` against each other over real pipes (`DisplayServiceTests`). It missed the
bug for three reasons:

1. **xUnit test threads are not UI threads.** A plain `[Fact]` runs on a thread whose context either is null or posts to other worker
   threads. A continuation posted there runs on another thread, so a blocked test thread is never the only thread that can finish the
   task. The exact sequence "this thread is blocked, and only this thread may run the continuation" never happened.
2. **The tray app's constructor had no test.** `Glance.Runner` is UI code; only `Glance.Core` is tested. Opening the display inside a
   WinForms constructor existed nowhere in the suite.
3. **The service, by design, has no UI context either.** Manual runs of the service with a console client looked fine.

The fix adds `UiLikeContext` (in `UiThreadDeadlockTests.cs`). It is a `SynchronizationContext` whose `Post` only enqueues and whose
thread is busy running the test body, so nothing posted ever runs. In other words, a UI thread stuck in a constructor:

```csharp
public override void Post(SendOrPostCallback d, object? state) { Interlocked.Increment(ref _posted); _queue.Add((d, state)); }
...
var t = new Thread(() =>
{
    SynchronizationContext.SetSynchronizationContext(ctx);
    try { body(); } catch (Exception ex) { error = ex; }
}) { IsBackground = true, Name = "UI-like thread" };
t.SetApartmentState(ApartmentState.STA);
```

If the body is still blocked after the 8 s watchdog, the test fails. The harness then drains the queue on the test thread, so the
stuck thread can finish and does not leak into later tests. The `Posted` counter is a useful second signal: the tests assert it is 0, which
means "Glance.Core never needed the caller's UI thread", and that is stricter than "it did not hang this time".

On `8d2d13c` the seven deadlock tests all hit the watchdog, with 2 to 6 callbacks posted to the blocked thread. On the branch they all
pass.

---

## 4. High-level walkthrough of the fix

The change attacks the bug at four levels:

1. **Library rule (Glance.Core): never resume on the caller's context.** All 22 awaits in Core got `ConfigureAwait(false)`. CA2007 is
   now a build error for Core, and a source-scanning test checks that the rule stays switched on.
2. **The one sync-over-async site: never block on I/O that might need the caller.** `RemoteFrameDisplay.Request` now runs the pipe
   exchange inside `Task.Run`, so the I/O has no context at all, and waits for it with a real deadline.
3. **The tray app: never open the display on the UI thread.** The constructor starts the control pipe, the watchers and the timers
   first. The first open runs on the pool through `BackgroundOpener`, and `Opened` decides on the pool thread, under `_gate`, whether
   to keep the result.
4. **Observability.** The tray logs failed opens (once a minute per message). The service logs refused clients and failed USB opens
   (rate-limited by its `LogLimiter`). If this ever happens again, `service.log` and `runner.log` will say so.

Levels 1 and 2 overlap on purpose. With either one alone, `Connect` no longer deadlocks on a UI thread. The review checked this by
removing one `ConfigureAwait(false)` again: the deadlock tests still passed thanks to `Task.Run`, and the guard test failed on that
exact line.

---

## 5. Detailed code review

### 5.1 `RemoteFrameDisplay.Exchange`: blocking safely

```csharp
private (ReplyStatus Status, string Message) Exchange(byte[] bytes)
{
    var cts = new CancellationTokenSource(_replyTimeout);
    var io = Task.Run(async () =>
    {
        await _pipe.WriteAsync(bytes, cts.Token).ConfigureAwait(false);
        return await DisplayWire.ReadReplyAsync(_pipe, cts.Token).ConfigureAwait(false);
    });
    bool finished;
    try { finished = io.Wait(_replyTimeout + CancelGrace); }
    catch (AggregateException) { finished = true; }   // its exception is rethrown unwrapped below
    if (!finished)
    {
        try { _pipe.Dispose(); } catch { /* ends the stuck I/O; the connection is marked broken by the caller */ }
        io.ContinueWith(_ => cts.Dispose(), TaskScheduler.Default);
        throw new OperationCanceledException("The Glance service did not answer in time.");
    }
    cts.Dispose();
    return io.GetAwaiter().GetResult();
}
```

Things to notice:

- **`Task.Run` drops the context.** The lambda starts on a pool thread where `SynchronizationContext.Current` is null. Even an await
  without `ConfigureAwait(false)` inside it would resume on the pool. This is the robust way to call async code from code that must stay
  synchronous (here `IFrameDisplay.Blit` is synchronous by contract).
- **Two deadlines.** The token cancels the overlapped I/O at `_replyTimeout`. That normally completes the task with
  `OperationCanceledException`, `Wait` throws `AggregateException`, and `GetResult()` rethrows the original exception. `Request` turns it
  into `IOException("did not answer in time")` and marks the connection broken. If cancelling does not finish the I/O, `CancelGrace` (2 s)
  is the backstop: the pipe is disposed, which ends any pending I/O, and the caller gets the same exception.
- **`TaskScheduler.Default` on `ContinueWith`.** Without it, `ContinueWith` uses `TaskScheduler.Current`, which inside some UI-scheduled
  task could be the UI scheduler. Writing it out costs nothing.
- **Under `_io`.** `Exchange` runs under `Request`'s lock, so requests on one connection never overlap, and a reply is never read as the
  answer to the next request.
- Cost: one `Task.Run` per request, and frames go out at about 2.5 per second, so it does not matter.

Before the change, the timeout existed on paper only: on a UI thread the cancellation could not be observed. The test
`A_service_that_never_answers_times_out_on_a_UI_thread_instead_of_hanging_for_ever` uses a pipe server that reads and never answers, and
asserts that a 1 s reply timeout returns in under 5 s.

### 5.2 Making the rule stick: CA2007 and the guard test

```ini
# src/Glance.Core/Core.globalconfig
is_global = true
dotnet_diagnostic.CA2007.severity = error
```

```xml
<!-- Glance.Core.csproj -->
<GlobalAnalyzerConfigFiles Include="Core.globalconfig" />
```

The analyzer is the first line of defence: a new await without `ConfigureAwait` in Core does not compile. The review confirmed that with
a mutation (error CA2007 at `DisplayWire.cs(92,27)`). `ConfigureAwaitGuardTests` is the second line. It scans every `.cs` file under
`Glance.Core`, with comments and strings blanked out, finds each `await`, cuts out the awaited expression with a small bracket-depth
parser, and requires `.ConfigureAwait(false)` in it. A second test checks that the csproj and the globalconfig still switch CA2007 on, so
nobody can quietly turn the analyzer off. The scanner's own unit tests cover the tricky shapes: `while (await ... is { } line)`, two
awaits in one lambda, and `await` inside a comment or a string.

Two limits to know about:

- The guard rejects `ConfigureAwait(true)` too. That is right for today's Core. But code that *wants* to come back on the UI thread (for
  example a helper whose job is "do work on the pool, then apply on the UI thread") then has no legal place in Core. It must live in the
  UI project, or the guard needs an explicit, reviewed exception.
- It checks awaits only. A new `.Result` or `.Wait()` without a timeout is not caught, although `AGENTS.md` now forbids it.

### 5.3 The tray app: start the control pipe first, open in the background

Before (main):

```csharp
_supervisor.Tick += (_, _) => Supervise();
_supervisor.Start();
Supervise();                 // opened the display right here, on the UI thread

WatchFiles();
_ = Task.Run(ControlLoop);
```

After:

```csharp
WatchFiles();
_ = Task.Run(ControlLoop);
_powerWatcher = StartPowerWatcher();
_supervisor.Tick += (_, _) => Supervise();
...
CheckService();
_supervisor.Start();
Supervise();   // starts the first open in the background; returns at once
```

Even if something in the open were to hang again, the editor could still reach the tray, files would still be watched, and the
menu would still show a status. And `Supervise` no longer opens anything itself:

```csharp
else if (!_opener.IsOpening)
{
    _closingPusher = null;
    int brightness = _brightness;
    _opener.TryStart(() => new DisplayPusher(() => Current, _sensors, brightness), Opened);
}
```

`BackgroundOpener` is tiny on purpose: an `Interlocked.CompareExchange` flag for "one attempt at a time", `Task.Run` for the work, and a
rule that whoever receives the pusher owns it. If the callback throws, the opener stops the pusher so the device is never left claimed:

```csharp
try { done(pusher, error); }
catch { pusher?.Stop(blank: false, DisplayPusher.QuickStopTimeout); }   // nobody took it: never leave the device claimed
finally { Volatile.Write(ref _running, 0); }
```

### 5.4 `Opened`: deciding on another thread

Moving the open off the UI thread creates a new question: *the world may have changed while it was opening*. `Opened` runs on the pool
thread under `_gate` and checks, in order:

```csharp
if (opened is null)                 { _lastError = ...; /* log once a minute */ }
else if (!_power.MayDrive)          { /* Windows is sleeping or ending the session: blank and close it */ }
else if (_paused || _pusher is not null) { /* the user paused meanwhile: close it (blank if paused) */ }
else                                { opened.PinnedPage = _pinPage; opened.SetBrightness(_brightness); _pusher = opened; ... }
...
try { _ui.BeginInvoke(Supervise); } catch { /* the app is closing */ }
```

Why this is safe:

- **One lock for the shared state.** `_pusher`, `_lastError`, `_pinPage` and `_brightness` are written under `_gate` by the UI thread, by
  the control-pipe handlers and now by `Opened`. `_paused` became `volatile` because `SetPaused` writes it outside the lock.
- **The power policy has its own lock.** `OnPowerEvent` updates `_power` first and then blanks whatever `_pusher` holds. A display
  still being opened is not in `_pusher` yet, so the power event finds nothing to blank. `Opened` then sees `!MayDrive` and blanks it
  itself. Either order ends with the display black.
- **Only one opener.** `_pusher` is set only in `Opened`, and `Supervise` starts an open only while `_pusher` is null and no open is
  running, so `_pusher is not null` in `Opened` is a defensive branch.
- **Back to the UI for UI work.** The tray menu text is changed only on the UI thread: `Opened` posts `Supervise` with `BeginInvoke`
  (never `Invoke`, which would wait for the UI thread and bring back the same class of bug).

What it does not cover (see the review): `Opened` stops an unwanted pusher *while holding* `_gate`, with an 18 s timeout. `Quit` does not
wait for an open still running in the background.

### 5.5 Logging that would have shown the bug

The tray logs each distinct open failure once a minute (`_openLog`), and the service now logs refusals and USB failures:

```csharp
if (DisplayAccess.Deny(request.Op, session, _o.ConsoleSession()) is { } why)
{
    Log($"Refused {request.Op} from session {session?.ToString() ?? "unknown"}: {why}");   // rate-limited like every line here
    return (ReplyStatus.Denied, why);
}
```

`DisplayServer.Log` goes through a `LogLimiter` (one line per distinct message per minute; the table is cleared after 200 distinct
lines). Clients retry every 2 s, so without the limiter a missing display would write about 1,800 lines an hour. Two tests check
"three failures, one line".

### 5.6 The audit of other waits

The author listed every remaining blocking wait and tested the ones a UI thread can reach on `UiLikeContext`. Those are `ControlPipeClient.Send`,
`SessionPowerWatcher`'s start-up wait, `ThemeRenderer`'s file check, `DisplayPusher.Stop`, and the editor's `PusherSlot.TryOpen`/`Release`/`RestartIfDead`.
That pattern is worth copying: when you find one deadlock, list every `.Wait`/`.Result`/`GetResult` in the code and put a test or a
written reason next to each one.

---

## 6. Self-review: five ways to improve the change

1. **Do not hold `_gate` while stopping in `Opened`.** Take the decision under the lock, but call `opened.Stop(...)` after releasing it,
   and then take the lock again only to `RememberClosing`. A stalled transfer now freezes the tray menu and the editor's commands for up
   to 18 s. `PowerBlank.Run` already has this shape (take under the lock, blank outside).
2. **Make `Quit` wait for the opener.** Give `BackgroundOpener` a bounded `WaitIdle(TimeSpan)` and call it in `Quit` after the
   policy says "quit". Otherwise a display opened during exit can keep its first frame lit, and `Opened` touches the disposed `_sensors`.
3. **Test the tray's start-up, not only its parts.** The constructor order and `Opened`'s branches live in UI code and have no test. Move
   that policy into Core (for example a small `TrayDisplaySupervisor` that takes delegates for "open", "log" and "post to UI"), then run its
   constructor on `UiLikeContext` with a fake service that never answers and assert that the control loop started.
4. **Make the guard cover blocking waits, with a reviewed escape hatch.** Extend the scan to `.Result`, `.Wait()` without a timeout and
   `GetAwaiter().GetResult()` in Core, and allow a marker comment (for example `// ui-context-ok: <reason>`) that the test reports. That
   keeps `ConfigureAwait(true)` possible where it is truly needed. It also catches the sibling branch's `TrayLink.Send`, which blocks with
   no timeout.
5. **Offer an async path to UI callers.** `IFrameDisplay` is synchronous because the pusher's loop is. The editor, however, could
   `await Task.Run(() => _display.TryOpen(...))` and keep its UI thread free even when the service hangs (up to 0.5 s + 10 s + 2 s per
   request today). The sibling branch M7 moves this work to a `SerialWork` worker. When the two branches meet, use one mechanism for
   the tray (M7's worker or `BackgroundOpener`), not both.

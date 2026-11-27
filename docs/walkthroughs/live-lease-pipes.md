# Leasing the live theme and talking to the tray in order: a walkthrough

This walkthrough is for a mid-level developer who wants to understand the fix for findings **M5** and **M13** in
`docs/reviews/review-full-codebase.md`. Neither bug was a wrong line of code. Both came from assumptions about *time* and *identity*
between two processes: "the editor will always say goodbye", "commands arrive in the order I sent them", and "a process called
Glance.Runner is my tray app". The fix replaces each assumption with something the code can check.

It assumes you know C# and .NET, async/await and a UI framework. Named pipes, leases and channels are explained where they first
appear. For the bigger picture read `docs/WALKTHROUGH.md` (section 4.7, the runner and its control pipe) and
`docs/design/runner-service.md` (why a service owns the display and the tray app renders).

**The change:** branch `claude/fix-live-lease-pipes-4301b2`: `eb7ff86` (move the code into Core, no behaviour change) and `ff0ebee`
(the fix). Author's note with the proof: `docs/design/fix-live-lease-pipes.md`. Review: `docs/reviews/review-live-lease-pipes.md`.

**Contents**

1. [The problem](#1-the-problem)
2. [Technologies at a glance](#2-technologies-at-a-glance)
3. [High-level walkthrough](#3-high-level-walkthrough)
4. [Detailed code review](#4-detailed-code-review)
   - 4.1 [Extract first, then prove](#41-extract-first-then-prove)
   - 4.2 [The lease: `TrayLiveState`](#42-the-lease-traylivestate)
   - 4.3 [Knowing which connection is which: `TrayControlServer`](#43-knowing-which-connection-is-which-traycontrolserver)
   - 4.4 [Wiring it into the tray: `RunnerContext`](#44-wiring-it-into-the-tray-runnercontext)
   - 4.5 [One ordered connection: `TrayLink`](#45-one-ordered-connection-traylink)
   - 4.6 [Saying goodbye on exit](#46-saying-goodbye-on-exit)
   - 4.7 [The heartbeat: `LiveKeepAlive` and `LiveSendGate.Lost`](#47-the-heartbeat-livekeepalive-and-livesendgatelost)
   - 4.8 [Is the tray there? `TrayPresence`](#48-is-the-tray-there-traypresence)
   - 4.9 [The tests](#49-the-tests)
5. [Self-review: five ways to improve the change](#5-self-review-five-ways-to-improve-the-change)

---

## 1. The problem

Glance runs three processes. A Windows **service** owns the USB display. The **tray app** (`Glance.Runner`, normal user) renders the
saved theme and sends frames to the service. The **editor** (`Glance.App`) is where you design. While the tray app runs and you tick
**Send to display**, the editor does not draw itself; it streams your *unsaved* theme to the tray over a named pipe with `LIVE`, and the
tray shows that instead of the saved file. When you stop, the editor sends `LIVE_CLEAR`.

**M5:** the unsaved theme could stay on the display for good:

- `LIVE_CLEAR` was fire-and-forget (`Task.Run(() => Send(LiveClear))`) while the window closed; the process could end before it connected.
- Every command opened its own connection from its own task, and the tray handled each connection on its own task. A `LIVE` that waited
  for the tray's lock (up to ~2 s while it reopens the display) could be overtaken by the `LIVE_CLEAR` sent after it.
- The tray's `_live` and `_pinPage` never expired. An editor that crashed or was killed left its theme (and pinned page) until the tray restarted.

**M13:** "is the tray running?" was `Process.GetProcessesByName("Glance.Runner")` in this session. A tray of another account (started with
`runas`), or one still starting, counted as "running", so the editor streamed into nothing and never drew itself. Several other parts of
M13 (global pipe names, squatting, elevated runners) were already gone after the service redesign; the note lists each with the test
that proves it.

---

## 2. Technologies at a glance

| Area | Technology | Role in this change |
|---|---|---|
| IPC | **Named pipes** (`NamedPipeServerStream`/`NamedPipeClientStream`) | One line per command, one line per reply. A pipe end closes when its process exits, crashes or is killed; that is what the lease builds on. |
| Pipe security | `PipeSecurity` DACL, `PipeOptions.FirstPipeInstance`, `PipeOptions.CurrentUserOnly`, `TokenImpersonationLevel.Anonymous` | Only this user may connect; a squatter's name is refused; the client only talks to a server of its own user and never lets it impersonate. Unchanged, moved with the code. |
| Ordering | `System.Threading.Channels` (`Channel.CreateUnbounded`, `SingleReader`) | A queue with one consumer task: the simplest way to make "send in order, one at a time" true by construction. |
| Async results | `TaskCompletionSource<T>` with `RunContinuationsAsynchronously` | Each queued command gets a task the caller can await; continuations do not run on the pipe worker. |
| Leases | A timestamp plus an owner id | "You hold this until you stop renewing it or go away": the standard answer to "the client might never say goodbye". |
| Win32 interop | `OpenProcess`, `OpenProcessToken`, `WindowsIdentity` | Find out which account a `Glance.Runner` process runs as, only to *explain* a tray that does not answer. |
| UI threads | Avalonia `DispatcherTimer`/`Dispatcher.UIThread.Post`, WinForms timer | Nothing on a UI thread waits for the pipe; replies hop back with `Post`. |
| Tests | xUnit over **real pipes** with random names, plus a hand-moved clock | Ordering and "process went away" are tested on real pipes; lease timing with a fake clock. |

---

## 3. High-level walkthrough

```
Editor (Glance.App)                                         Tray app (Glance.Runner)
  MainWindow                                                  RunnerContext (under _gate)
    StartLive: Post RESUME, BRIGHTNESS, PAGE; PushLive          TrayLiveState: Live, PinnedPage, owner connection, last heard
    every 0.5 s: PushLive (LIVE if changed)                     Supervise every 2 s: ExpireLive (5 s lease)
                 KeepLive (LIVE_KEEP every 1.5 s)               OnControlClosed(conn): owner gone -> saved theme
    Closing: StopLive (LIVE_CLEAR), CloseLink (bounded)
  RunnerClient                                                TrayControlServer
    TrayLink: one queue, one worker, ONE connection  ----->     numbers each connection, calls closed(id) when it ends
    TrayPresence.Probe: STATUS round trip, then processes
```

Three ideas carry the whole fix:

1. **The connection is the editor.** The tray leases the live theme to the connection that sent `LIVE`. It ends on `LIVE_CLEAR`, when that
   connection closes, or after 5 s without a command on it.
2. **One connection, one queue.** The editor sends every command through one `TrayLink`: each goes out after the reply to the one before,
   so nothing can overtake anything.
3. **Ask, don't guess.** "Is the tray there?" is a `STATUS` round trip. Process names are used only to explain why there was no answer.

---

## 4. Detailed code review

### 4.1 Extract first, then prove

The old tray code lived in `RunnerContext`, a WinForms class that cannot be tested without starting the tray. The first commit (`eb7ff86`)
moves the pipe server, the live state, the client and the tray detection into `Glance.Core/Control` **with today's behaviour**, as stubs
that keep the old semantics:

```csharp
// eb7ff86: TrayLiveState kept the old behaviour so the tests could fail on it
case ControlVerb.LiveKeep:
    return "ERR unknown command";
...
public bool ConnectionClosed(long connection) => false;
public bool Expire() => false;
```

```csharp
// eb7ff86: TrayLink still opened one connection per command
public Task<string?> SendAsync(ControlCommand command) => Task.Run(() => ControlPipeClient.Send(_pipeName, command));
public bool Close(TimeSpan timeout) => true;
```

Then the tests were written against that commit: 12 of 17 fail with messages that describe the bug (for example
`Expected: [Live, LiveClear]  Actual: [LiveClear, Live]`). The second commit fills in the stubs and the same tests pass. This
"extract, prove, fix" order is worth copying whenever the buggy code sits in a class you cannot test: the proof is about the
*behaviour*, not about the refactoring.

A lesson from the note: the first ordering test passed on the old code. It released the slow `LIVE` as soon as `LIVE_CLEAR` *arrived*,
and the woken `LIVE` still won the lock. Holding `LIVE` until `LIVE_CLEAR` was *applied* reproduces the real race. A test that passes
before the fix proves nothing, so always run it against the old code.

### 4.2 The lease: `TrayLiveState`

```csharp
private long? _owner;          // the connection whose live theme is shown
private DateTime _lastHeard;   // the owner's last command

public string? Handle(ControlCommand command, long connection)
{
    if (Live is not null && _owner == connection) _lastHeard = _now();
    switch (command.Verb)
    {
        case ControlVerb.Live:
        {
            var t = new Theme { BaseFolder = command.Folder };
            t.ApplySnapshot(command.ThemeJson!);   // a bad theme throws before anything changes
            Live = t;
            _owner = connection;
            _lastHeard = _now();
            return "OK";
        }
        case ControlVerb.LiveKeep:
            return Live is not null && _owner == connection ? "OK" : NotHeldReply;
        ...
```

Points to notice:

- **Any** command from the owner renews the lease, not only `LIVE_KEEP`. A busy editor never needs a separate heartbeat.
- `LIVE_KEEP` answers a question as well as renewing: `ERR no live theme` means "I don't hold your theme" (the tray restarted, or the
  connection was replaced), which tells the editor to send it again.
- Ending the lease is one private method, used by `LIVE_CLEAR`, `ConnectionClosed` and `Expire`, so all three also release the page pin:

```csharp
private void End()
{
    Live = null;
    PinnedPage = null;
    _owner = null;
}
```

- The clock is injected (`Func<DateTime>? clock`), so tests move time by hand instead of sleeping. The class is deliberately not thread
  safe; the tray calls it under its own lock. Keeping the state machine free of locks and I/O is what makes it so easy to test.

### 4.3 Knowing which connection is which: `TrayControlServer`

The lease needs an identity for "the editor". The server gives every connection a number and reports when it ends:

```csharp
private async Task Handle(NamedPipeServerStream pipe)
{
    long id = Interlocked.Increment(ref _nextConnection);
    try
    {
        if (NativeSessions.IsRemoteClient(pipe)) return;   // L1: never from another PC, not even through \localhost
        ...
        while (await reader.ReadLineAsync(_cts.Token) is { } line)
        {
            string reply;
            try { reply = _execute(line.Trim(), id); } catch (Exception ex) { reply = "ERR " + ex.Message; }
            await writer.WriteLineAsync(reply);
        }
    }
    catch { /* client left */ }
    finally
    {
        Release(pipe);
        try { _closed?.Invoke(id); } catch (Exception ex) { _log("Control pipe: " + ex.Message); }
    }
}
```

Windows closes a process's pipe handles when it exits for any reason, so `ReadLineAsync` returns `null` (or throws) and `finally` runs.
That turns "the editor crashed" into an event the tray can act on at once, with no polling. The security rules moved unchanged
(current-user DACL, `FirstPipeInstance` through `ServicePipeFactory`, remote clients closed, at most 4 instances), and a test checks the
moved server still refuses a squatted name.

### 4.4 Wiring it into the tray: `RunnerContext`

The tray keeps one lock, `_gate`, around everything that touches the display loop. The new code uses the Core class under that lock:

```csharp
case ControlVerb.Page:        // PAGE <n> pins the display to page n (1-based); PAGE 0 releases it
case ControlVerb.Live:        // LIVE <base64 folder> <base64 theme json>: show the editor's unsaved theme
case ControlVerb.LiveKeep:    // the editor is still there
case ControlVerb.LiveClear:   // back to the saved theme, pin released
    lock (_gate)
    {
        var reply = _liveState.Handle(cmd, connection) ?? "ERR unknown command";
        ApplyPin();
        return reply;
    }
```

Expiry runs at the start of every 2 s `Supervise` tick, so a hung editor is noticed at most ~7 s after its last command:

```csharp
private void ExpireLive()
{
    if (!_liveState.Expire()) return;
    ApplyPin();
    Log("The editor's live preview ended (no word from the editor); showing the saved theme");
}
```

`STATUS` also passes through `_liveState.Handle`, so the editor's background presence probe counts as a sign of life.

### 4.5 One ordered connection: `TrayLink`

```csharp
private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });

public Task<string?> SendAsync(ControlCommand command)
{
    var item = new Item(command, new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously));
    if (!_queue.Writer.TryWrite(item)) item.Reply.TrySetResult(null);   // closed: nothing more goes out
    return item.Reply.Task;
}

private async Task Work()
{
    try
    {
        await foreach (var item in _queue.Reader.ReadAllAsync())
        {
            string? reply = null;
            try { reply = Exchange(item.Command); }
            finally { item.Reply.TrySetResult(reply); }
        }
    }
    finally { Drop(); }
}
```

Ordering is now a property of the structure, not of timing: one reader, one connection, and the next command is written only after the
previous reply. `RunContinuationsAsynchronously` matters: without it, whatever the caller chained onto the task would run *on the worker*
and could delay or block the queue.

The exchange itself is bounded, and a timeout throws the connection away:

```csharp
var read = _reader!.ReadLineAsync();
if (!read.Wait(_replyTimeout))
{
    Observe(read);
    Drop();          // a late reply must never be read as the next command's
    return null;
}
if (read.Result is { } reply) return reply;
Drop();              // the tray closed this connection (it exited or restarted)
```

Why drop instead of keep reading? On a request/reply stream with no message ids, a late reply would be read as the answer to the *next*
command. Dropping the connection is the cheapest way to resynchronise. A connection that broke since its last use is retried once on a
fresh one; a brand new connection that fails is the answer (`null`).

`Send` blocks (`SendAsync(...).GetAwaiter().GetResult()`), which is fine on a worker thread and **must never run on a UI thread**. In this
change it is only called from `Task.Run` (the presence probe and the Background service window). The editor's UI code uses `Post` and
`SendAsync(...).ContinueWith(... Dispatcher.UIThread.Post ...)`.

### 4.6 Saying goodbye on exit

`Closing` already ran `StopLive()`, which now *queues* `LIVE_CLEAR` behind any `LIVE` still on its way. Then it closes the link off the UI
thread, and `Program.Main` waits for it together with the display blank, within one shared budget:

```csharp
// MainWindow.Closing
PendingTrayClose = Task.Run(() => RunnerClient.CloseLink(TrayCloseBudget));

internal static void WaitForExitBlank()
{
    // both started while the window closed and run side by side: one shared budget, not one after the other
    var tasks = new[] { PendingExitBlank, PendingTrayClose }.OfType<Task>().ToArray();
    try { Task.WaitAll(tasks, PowerBlank.SessionEndBudget); } catch { /* best effort */ }
}
```

`TrayLink.Close` completes the channel (no new commands) and waits for the worker to drain it. If it is too slow, nothing is lost: the
process exits, its pipe closes, and the lease ends on the tray side anyway. That is the general pattern: **make the clean path likely
and the unclean path safe**.

### 4.7 The heartbeat: `LiveKeepAlive` and `LiveSendGate.Lost`

The editor's 500 ms live timer now also keeps the lease:

```csharp
private void KeepLive()
{
    if (!_liveToRunner || !_keepAlive.TryBegin(DateTime.UtcNow)) return;
    RunnerClient.SendAsync(ControlCommand.LiveKeep).ContinueWith(t =>
    {
        var reply = t.IsCompletedSuccessfully ? t.Result : null;
        Dispatcher.UIThread.Post(() => { if (_keepAlive.Complete(reply) && _liveToRunner) _liveGate.Lost(); });
    });
}
```

`LiveKeepAlive` is a tiny pure class: at most one heartbeat in flight, at most one every 1.5 s (three fit in the 5 s lease), and only the
exact `ERR no live theme` reply means "send again". An older tray answers `ERR unknown command`, and resending every 1.5 s would not help
it. `LiveSendGate.Lost()` forgets the last accepted snapshot (`_sent = null`), so the next tick sends the same theme again even though
nothing changed. Note `t.Result` is only read after `IsCompletedSuccessfully`, inside a continuation, so it never blocks.

### 4.8 Is the tray there? `TrayPresence`

```csharp
public static TrayState Decide(string? statusReply, IReadOnlyCollection<bool?> trayProcesses)
{
    if (statusReply is { } r && r.StartsWith("OK", StringComparison.Ordinal)) return TrayState.Answering;
    if (trayProcesses.Any(mine => mine == true)) return TrayState.NotAnswering;
    if (trayProcesses.Count > 0) return TrayState.OtherAccount;
    return TrayState.None;
}
```

The answer decides; processes only explain. `IsMine` opens the process with `PROCESS_QUERY_LIMITED_INFORMATION`, reads its token owner and
compares SIDs; when Windows refuses (another account, or an elevated process) it returns `null`, which counts as "not ours". The editor
never probes on the UI thread: `RefreshPresence` starts at most one probe every 2 s in `Task.Run` and stores the result in an `int` with
`Volatile.Write`; the UI reads `Presence`, which never blocks. `ToggleSend` then has four outcomes: live through the tray, draw itself,
or (for `NotAnswering`/`OtherAccount`/`Unknown`) stay off and say why, instead of fighting another program for the display.

### 4.9 The tests

`src/Glance.Tests/LiveLeaseTests.cs` mixes three kinds of tests:

- **Pure state tests** with a hand-moved clock (`M5_the_live_theme_ends_after_the_lease_without_a_word_from_the_editor`): fast and exact.
- **Real pipe tests** with a `FakeTray` that wires `TrayControlServer` + `TrayLiveState` the way `RunnerContext` does, on a random pipe
  name: an "editor" that sends `LIVE` and disposes its pipe (`SendLiveAndVanish`), the ordering race, one connection for many commands,
  and draining on close.
- **Guards** that must pass before and after (another connection closing leaves the theme alone; the moved server still refuses a squatter;
  a squatter cannot impersonate the editor through the new link).

The note also records a mutation check: removing the renewal line makes `M5_a_heartbeat_keeps_the_live_theme` fail.

---

## 5. Self-review: five ways to improve the change

1. **Handle two live editors.** Nothing stops two editor processes. Each heartbeat now gets `ERR no live theme` when the other one took
   the lease, resends, and takes it back, so the display flips between two themes every ~1.5 s. Give the "someone else holds it" case its
   own reply and let the losing editor stop live with a message. Add a test with two `TrayLink`s.
2. **Resend the whole live setup, not just the theme.** After a tray restart, a lease that expired, or a wake from sleep, `Lost()` resends
   `LIVE` only; the page pin and brightness that `StartLive` sent are gone. Either resend `PAGE`/`BRIGHTNESS` first or carry the pin inside
   `LIVE`, and tie the pin to the owning connection so a `PAGE` without a `LIVE` cannot leave an unowned pin behind.
3. **Use a monotonic clock.** Both the lease (`TrayLiveState`) and the heartbeat (`LiveKeepAlive`) use `DateTime.UtcNow`. A clock set back
   stops heartbeats and expiry until it catches up; a wake from sleep ends the lease on the first tick. `Environment.TickCount64` or a
   `Stopwatch` avoids clock changes; renewing the lease when the tray sees a resume avoids the flash after sleep.
4. **Probe fresh when the user clicks.** `ToggleSend` trusts a cached presence that can be ~3 s old (and `Unknown` right after start).
   Running `RunnerClient.Probe()` on a worker when Send is ticked, then continuing on the UI thread, removes both the "Still looking"
   message and the small window in which the editor opens the display next to a tray that just started.
5. **Make the link's pipe asynchronous and test the real wiring.** The link opens its pipe without `PipeOptions.Asynchronous`, so a timed
   out read keeps the handle alive until the tray replies, and "drop the connection" is not immediate. Cancelling the read before disposing
   makes it so. Separately, `FakeTray` copies `RunnerContext`'s wiring; moving `Execute`/`ExpireLive` into a small Core class that both the
   tray and the tests use would make the tests cover the code that actually runs, and remove the brittle source-text check.

# Turning the display off at shutdown and sleep: a walkthrough

This walkthrough is for a mid-level developer who wants to understand one change in Glance: blanking the USB display when Windows
shuts down, restarts, signs out or goes to sleep, and bringing it back after wake. It covers the Windows mechanisms involved,
how the fix is put together, and where it could be better.

It assumes you know C# and .NET threads and locks, and have used a UI framework. You do not need to know Win32 window messages:
they are explained where they first appear. For the rest of Glance, read `docs/WALKTHROUGH.md` first; section 4.3 (the display loop)
and section 4.7 (the runner) are the background for this change.

The change is commit `df02e6d` on `claude/feature-shutdown-blank-60ded2`. A review of it is in `docs/reviews/review-shutdown-blank.md`.

**Contents**

1. [The problem](#1-the-problem)
2. [Technologies at a glance](#2-technologies-at-a-glance)
3. [High-level walkthrough](#3-high-level-walkthrough)
4. [Detailed code review](#4-detailed-code-review)
   - 4.1 [From window messages to events: `PowerMessages`](#41-from-window-messages-to-events-powermessages)
   - 4.2 [A hidden window on its own thread: `SessionPowerWatcher`](#42-a-hidden-window-on-its-own-thread-sessionpowerwatcher)
   - 4.3 [The decision: `DisplayPowerPolicy`](#43-the-decision-displaypowerpolicy)
   - 4.4 [The runner](#44-the-runner)
   - 4.5 [The editor](#45-the-editor)
   - 4.6 [Tests](#46-tests)
   - 4.7 [Merging with the display-loop fix](#47-merging-with-the-display-loop-fix)
5. [Self-review: five ways to improve the change](#5-self-review-five-ways-to-improve-the-change)

---

## 1. The problem

The display is a small USB LCD. It has no power logic of its own: it shows whatever frame it last received, with the backlight at
whatever level it was given. Many PCs keep USB ports powered after shutdown (for charging phones, or because of the BIOS setting),
so when the user shut down from the Start menu, **the display kept showing its last frame all night**.

Glance already blanked the display (black frame, backlight 0) when the runner or editor exited normally. It did not blank at
shutdown, because the runner never heard about it:

- The runner is a WinForms tray app (an `ApplicationContext` with a `NotifyIcon`). It has **no window of its own** that handles session messages.
- Windows then ends the process, with no managed shutdown hook that is guaranteed to run.

The requirements were:

- Blank the display on **shutdown, restart and sign-out**.
- Blank on **sleep and hibernate**, and bring the theme back after **wake**.
- Do this both in the **runner** and in the **editor** when the editor drives the display itself.
- **No double blank**, and no race with the normal Quit.
- **Nothing may reopen the display** after a session-end blank.
- Put the decision logic in Core, with tests.

---

## 2. Technologies at a glance

| Area | Technology | What you need to know |
|---|---|---|
| Session end | **`WM_QUERYENDSESSION`, `WM_ENDSESSION`** | When the user shuts down, restarts or signs out, Windows sends every top-level window `WM_QUERYENDSESSION` ("may I?"), then `WM_ENDSESSION` with `wParam = TRUE` ("it is happening") or `FALSE` ("cancelled"). After an app returns from `WM_ENDSESSION`, Windows may end the process at any time, so cleanup must finish **inside** the handler. Windows allows only a few seconds for it. |
| Sleep and wake | **`WM_POWERBROADCAST`** | `wParam = PBT_APMSUSPEND` before sleep or hibernate (apps get about 2 s), and `PBT_APMRESUMEAUTOMATIC` (always) plus `PBT_APMRESUMESUSPEND` (when a user is present) after wake. |
| Who gets these messages | **Top-level windows**, visible or hidden | They are *broadcast* to top-level windows. A **message-only window** (`HWND_MESSAGE` parent), the usual choice for an invisible helper window, does **not** get broadcasts. |
| Win32 window basics | `RegisterClassEx`, `CreateWindowEx`, `GetMessage`/`DispatchMessage`, a window procedure | A window belongs to the thread that created it. Its messages are delivered only while that thread runs a message loop. `SendMessage` to a window runs its window procedure and returns its result. |
| Interop | **P/Invoke** (`DllImport`), delegates as callbacks | A delegate passed to native code as a function pointer must be kept alive (in a field), or the GC collects it and Windows calls freed memory. |
| A polite delay | **`ShutdownBlockReasonCreate`** | Gives a window a reason string that Windows shows on its "apps are preventing shutdown" screen if the app is slow to finish. |
| Runner UI thread | **WinForms** `ApplicationContext`, `Control.BeginInvoke`, `System.Windows.Forms.Timer` | `BeginInvoke` posts work to the UI thread without waiting. WinForms timers only tick while the UI thread pumps messages, and not while the PC sleeps. |
| Editor UI thread | **Avalonia 12** `Dispatcher.UIThread.Post` | The same idea as `BeginInvoke`. Avalonia also handles `WM_QUERYENDSESSION` itself and closes its windows on an OS shutdown (`WindowCloseReason.OSShutdown`). |
| Thread safety | `lock`, `Interlocked.Exchange` | `Interlocked.Exchange(ref field, null)` takes ownership of an object atomically, so exactly one thread can dispose it. |
| Windows power modes | **S3 sleep, hibernate, Fast Startup, Modern Standby** | "Shut down" with Fast Startup is a sign-out plus a kernel hibernate, so apps still get `WM_ENDSESSION`. Modern Standby (S0 Low Power Idle) turns the screen off long before apps get `PBT_APMSUSPEND`. |
| Tests | **xUnit** | The tests create real hidden windows and `SendMessage` the same messages Windows would send. Nothing is really shut down. |

---

## 3. High-level walkthrough

The change adds two classes to `Glance.Core/Display` and wires them into the two programs that can own the display.

```
                        Windows
     WM_ENDSESSION / WM_POWERBROADCAST (broadcast to top-level windows)
                           |
                           v
  SessionPowerWatcher  (hidden top-level window, own thread + message loop)
     PowerMessages.Translate(msg, wParam) -> PowerEvent { SessionEnding, Suspend, Resume }
     calls handler(e) synchronously; Windows waits for the window procedure to return
                           |
          +----------------+-----------------+
          v                                  v
  RunnerContext.OnPowerEvent          MainWindow.OnPowerEvent (editor)
    lock(_gate)                          lock(_powerGate)
      action = _power.Handle(e)            action = _power.Handle(e)
      Blank* -> DisposePusher(blank)       Blank* -> ReleasePusher()  (Interlocked)
    BeginInvoke(Quit | Supervise)        Post(status | ResumeSendingAfterSleep)

  DisplayPowerPolicy (pure, tested): SessionEnding/Quit -> BlankAndStop once, then Ended for good
                                     Suspend -> BlankAndHold;  Resume -> Reopen
                                     MayDrive = !Ended && !Suspended   (checked before every open)
```

There are three user stories.

**Shutdown, restart or sign-out.**
1. Windows sends `WM_QUERYENDSESSION`, and the watcher says yes.
2. Windows sends `WM_ENDSESSION(TRUE)`.
3. The watcher sets a shutdown block reason and calls the owner's handler on its own thread.
4. The handler asks the policy (`BlankAndStop`), stops the display loop and blanks the screen, all before returning.
5. Then it *posts* `Quit` to the UI thread and returns.
6. Windows may now end the process. The screen is already dark.

**Sleep and wake.**
1. `PBT_APMSUSPEND` → `BlankAndHold`: blank, release the USB device, and refuse to reopen it.
2. On wake, `PBT_APMRESUMEAUTOMATIC` → `Reopen`.
   - The runner's `Supervise` opens the display again. If the device is not back yet it keeps trying every 2 s.
   - The editor retries a few times if it was the one sending.

**The normal Quit racing a session end.** Both go through the same policy object. The first caller gets `BlankAndStop` and every
later one gets `None`, so the display is blanked exactly once. From then on `MayDrive` is false for good, so no code path can
reopen it.

The design follows Glance's usual rule (WALKTHROUGH §3.1): **decisions live in Core and are tested. The UI projects only wire them up.**

---

## 4. Detailed code review

### 4.1 From window messages to events: `PowerMessages`

**File:** `src/Glance.Core/Display/SessionPowerWatcher.cs` (top)

The first step is a pure function that turns raw message numbers into a small enum. Everything after it works with
`PowerEvent`, not with `wParam`s:

```csharp
public static PowerEvent? Translate(uint msg, nint wParam)
{
    switch (msg)
    {
        // WM_ENDSESSION with TRUE: the session really ends (shutdown, restart, sign-out, or an installer closing the app).
        // FALSE means a shutdown was cancelled after WM_QUERYENDSESSION; nothing was done then, so nothing to undo.
        case WM_ENDSESSION: return wParam != 0 ? PowerEvent.SessionEnding : null;
        case WM_POWERBROADCAST:
            return (int)wParam switch
            {
                PBT_APMSUSPEND => PowerEvent.Suspend,
                PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND or PBT_APMRESUMECRITICAL => PowerEvent.Resume,
                _ => null,
            };
        default: return null;
    }
}
```

Two design choices are worth noticing:

- **`WM_QUERYENDSESSION` maps to nothing.** At that point the shutdown can still be cancelled (another app may refuse), so blanking
  then would turn the display off for a shutdown that never happens. The work waits for `WM_ENDSESSION(TRUE)`, when the end is certain.
- **All resume codes map to `Resume`.** Windows sends two in a row after a normal wake. Removing the duplicate is the policy's job (4.3),
  not this function's.

### 4.2 A hidden window on its own thread: `SessionPowerWatcher`

**File:** `src/Glance.Core/Display/SessionPowerWatcher.cs`

This class is the heart of the change. It answers the question "how does a program with no window hear about shutdown?".

**1. It must be a top-level window, not a message-only window.** Message-only windows are the textbook choice for invisible helper
windows, but they do not receive broadcasts. The runner's existing `_ui` control is a parentless WinForms `Control`, and WinForms parks
such controls under a message-only window. That is part of why the runner never heard about shutdown. The watcher creates a plain
top-level window and simply never shows it:

```csharp
// No parent and no WS_VISIBLE: a hidden top-level window (WS_EX_TOOLWINDOW keeps it out of Alt+Tab even if shown).
var hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, _className, "Glance session watcher", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
```

**2. It has its own thread and message loop.** A window gets messages only while the thread that created it pumps messages. Putting
the window on the runner's WinForms thread or the editor's Avalonia thread would tie shutdown handling to those threads being free.
If the UI thread were busy, for example opening the USB device, the message would wait. So the constructor starts a dedicated thread,
creates the window there, signals that it is ready, and runs a classic `GetMessage` loop:

```csharp
_thread = new Thread(() =>
{
    try { _hwnd = CreateHiddenWindow(); }
    catch (Exception ex) { failure = ex; }
    finally { ready.Set(); }
    if (failure is null) Pump();
}) { IsBackground = true, Name = "Glance session/power watcher" };
_thread.SetApartmentState(ApartmentState.STA);
_thread.Start();
ready.Wait();
if (failure is not null) throw failure;
```

The constructor waits for `ready`, so when `new SessionPowerWatcher(...)` returns, the window exists. If creating it failed, the
exception is thrown back on the caller's thread, where the owner can log it. `IsBackground = true` means the thread never keeps the
process alive.

**3. Keep the callback delegate alive.** Windows calls the window procedure through a function pointer made from a delegate. If
nothing in .NET refers to that delegate, the GC may collect it while Windows still calls it, which is a crash that is very hard to
debug. The class keeps it in a field:

```csharp
private readonly WndProcDelegate _wndProc;   // kept in a field so the GC cannot collect it while Windows calls it
...
_wndProc = WndProc;
...
lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
```

**4. Do the work before answering.** This is the key property. Windows waits for the window procedure to return, so the handler is
called *inline*, not posted somewhere else:

```csharp
case PowerMessages.WM_QUERYENDSESSION:
    return 1; // never block shutdown; the work happens in WM_ENDSESSION when the end is certain
case PowerMessages.WM_ENDSESSION:
case PowerMessages.WM_POWERBROADCAST:
    if (PowerMessages.Translate(msg, wParam) is { } e) Raise(hwnd, e);
    return msg == PowerMessages.WM_POWERBROADCAST ? 1 : 0;
```

```csharp
private void Raise(IntPtr hwnd, PowerEvent e)
{
    bool blocking = e == PowerEvent.SessionEnding && ShutdownBlockReasonCreate(hwnd, _reason);
    try { _handler(e); }
    catch (Exception ex) { _log?.Invoke($"Handling {e} failed: {ex.Message}"); }
    finally { if (blocking) ShutdownBlockReasonDestroy(hwnd); }
}
```

There are three details here:

- **A shutdown block reason during a session end.** If blanking is slow, Windows shows "Turning off the Glance display" next to the
  app instead of an anonymous "this app is preventing shutdown". It is removed in `finally`. This only helps if the handler is quick
  (see suggestion 1 in section 5).
- **Exceptions never escape into Windows.** An exception thrown through a native callback would bring the process down. Both
  `WndProc` and `Raise` catch everything and log it.
- **The window procedure answers the documented values:** TRUE for `WM_QUERYENDSESSION` (never block), 0 for `WM_ENDSESSION`, and TRUE for `WM_POWERBROADCAST`.

**5. Shutting the watcher down.** `Dispose` posts `WM_CLOSE`. The window procedure destroys the window, `WM_DESTROY` posts
`WM_QUIT`, the loop ends and the class is unregistered. `Dispose` waits up to 1 s for the thread, but not when it is called *from* that
thread, because a thread cannot wait for itself:

```csharp
public void Dispose()
{
    var hwnd = _hwnd;
    if (hwnd != IntPtr.Zero) PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    if (Thread.CurrentThread != _thread) _thread.Join(1000);
}
```

The window class name includes a GUID (`"Glance.PowerWatcher." + Guid.NewGuid()`), so several watchers in one process (the tests
create many) never clash.

**Why not `Microsoft.Win32.SystemEvents`?** It also runs a hidden window and raises `SessionEnded` and `PowerModeChanged`. But it
decides for you which thread raises the event. In a WinForms app it may marshal the event to the UI thread, and then the "blank before
answering" guarantee depends on that thread being free. A dozen lines of Win32 buy full control over that.

### 4.3 The decision: `DisplayPowerPolicy`

**File:** `src/Glance.Core/Display/DisplayPowerPolicy.cs`

The watcher only reports what happened. The policy decides what to do, and it remembers the state. It is pure (no Windows calls),
thread-safe (its own `lock`) and fully tested:

```csharp
public PowerAction Handle(PowerEvent e)
{
    lock (_lock)
    {
        switch (e)
        {
            case PowerEvent.SessionEnding:
            case PowerEvent.Quit:
                if (_ended) return PowerAction.None;                       // the final blank already happened
                _ended = true;
                return _suspended ? PowerAction.None : PowerAction.BlankAndStop; // while asleep the display is already dark and released

            case PowerEvent.Suspend:
                if (_ended || _suspended) return PowerAction.None;
                _suspended = true;
                return PowerAction.BlankAndHold;

            case PowerEvent.Resume:
                // Windows sends two resume notifications (automatic, then "user present"); only the first one counts.
                if (_ended || !_suspended) return PowerAction.None;
                _suspended = false;
                return PowerAction.Reopen;
            ...
```

Read it as a tiny state machine with three states:

- **Running** goes to **Suspended** on `Suspend`.
- **Suspended** goes back to **Running** on `Resume`.
- **Running** or **Suspended** goes to **Ended** on `SessionEnding` or `Quit`.

**Ended** is terminal. It covers three requirements at once:

- **Blank once.** A session end and a normal Quit can both arrive (Windows on the watcher thread, the tray Exit on the UI thread).
  Whoever comes first gets `BlankAndStop`, and the other gets `None`.
- **Never reopen after a session end.** `MayDrive` is `!_ended && !_suspended`. Every place that opens the display checks it.
- **Duplicate notifications** (two resumes in a row) act only once.

Note that `Quit`, the program's own exit, is a `PowerEvent` too. That is what lets the policy settle the race with a session end:
both are just "the end", and only the first one counts.

### 4.4 The runner

**File:** `src/Glance.Runner/RunnerContext.cs`

The runner already had a lock, `_gate`, that protects `_pusher` (the `DisplayPusher` that renders and sends frames). The change makes
the policy part of the same lock, so "may I open?" and "Windows says stop" cannot interleave.

**1. The handler blanks on the watcher's thread and only posts follow-up work:**

```csharp
private void OnPowerEvent(PowerEvent e)
{
    PowerAction action;
    lock (_gate)
    {
        action = _power.Handle(e);
        if (action is PowerAction.BlankAndStop or PowerAction.BlankAndHold) DisposePusher(blank: true);
    }
    Log($"Windows {e}: {action}");
    switch (action)
    {
        case PowerAction.BlankAndStop: _ui.BeginInvoke(Quit); break;   // also covers an installer closing the app (Restart Manager)
        case PowerAction.BlankAndHold:
        case PowerAction.Reopen: _ui.BeginInvoke(Supervise); break;    // updates the status; after wake it reopens the display (and keeps retrying)
    }
}
```

`DisposePusher(blank: true)` calls `DisplayPusher.Stop(true)`. That stops the render loop, sends a black frame and turns the backlight
off, all before the method returns. Only then does the handler return to the watcher, which answers Windows.
`BeginInvoke` never waits for the UI thread. That matters: the UI thread could be busy, and a handler that waited for it could hang
the shutdown.

**2. The one place that opens the display checks the policy first.** `Supervise` runs every 2 s on the UI thread and is the only
code that creates a `DisplayPusher`. Reload, the file watcher and the pipe commands `RELOAD` and `RESUME` all end up here:

```csharp
private void Supervise()
{
    lock (_gate)
    {
        if (!_power.MayDrive) { DisposePusher(); SetStatus(_power.Ended ? "Display off: Windows is ending the session" : "Display off while Windows sleeps"); return; }
        if (_paused) { DisposePusher(); SetStatus("Display paused"); return; }
        if (_pusher is null)
        {
            try { _pusher = new DisplayPusher(() => Current, _sensors, _brightness) { PinnedPage = _pinPage }; ... }
```

Putting the guard at the top of the existing open path is the cheapest way to make "no reopening" hold for every caller, including
ones added later. After wake, `Reopen` posts `Supervise`, which opens the display immediately. If the USB device has not come back yet,
the 2 s timer simply tries again.

**3. Quit becomes idempotent and shares the policy:**

```csharp
private void Quit()
{
    if (_quitting) return;   // a session end and the EXIT command can both ask
    _quitting = true;
    ...
    lock (_gate) { if (_power.Handle(PowerEvent.Quit) == PowerAction.BlankAndStop) DisposePusher(); } // blank once, even if a session end got there first
    _powerWatcher?.Dispose();
    ...
}
```

`Quit` always runs on the UI thread (tray Exit directly, the `EXIT` command and the session end through `BeginInvoke`), so a plain
`bool` is enough for `_quitting`.

**Why this works for the elevated runner started by a scheduled task:** the task uses `LogonType=InteractiveToken`. The runner
therefore runs in the user's interactive session, on the user's desktop, where its hidden window gets the session broadcasts. Integrity
levels (UIPI) do not filter these system messages. A task set to "run whether the user is logged on or not" would run in session 0
and would not get them.

### 4.5 The editor

**File:** `src/Glance.App/MainWindow.cs`

The editor can drive the display itself when no runner is running. It uses the same watcher and policy, with its own lock
`_powerGate`.

**1. One way to open, one way to release.** Before this change, the `_pusher` field was assigned and disposed in several places.
The change funnels them into two helpers:

```csharp
/// <summary>Opens the display unless Windows is sleeping or ending the session.</summary>
private bool TryStartPusher(out string? error)
{
    lock (_powerGate)
    {
        if (!_power.MayDrive) { error = "Windows is going to sleep or ending the session."; return false; }
        try { _pusher = new DisplayPusher(() => _theme, _hub, _settings.Brightness) { PinnedPage = DesiredPin }; error = null; return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}

/// <summary>Stops the editor's own pusher and blanks the display. Safe from any thread; only one caller gets to dispose it.</summary>
private bool ReleasePusher()
{
    var p = Interlocked.Exchange(ref _pusher, null);
    p?.Dispose();                     // blanks the screen as it closes
    return p is not null;
}
```

`Interlocked.Exchange` is the key: the UI thread (the user clicks "Stop sending") and the watcher thread (Windows goes to sleep) can
both call `ReleasePusher`, but only one of them gets the object back, so only one disposes it.

A related small fix: `ApplyPins` now reads `_pusher` once into a local, because the field can become null on another thread between
the check and the use:

```csharp
if (_pusher is { } pusher) pusher.PinnedPage = DesiredPin;   // read once: a Windows sleep event can clear it from another thread
```

**2. The handler remembers whether to resume:**

```csharp
private void OnPowerEvent(PowerEvent e)
{
    PowerAction action;
    bool released = false;
    lock (_powerGate)
    {
        action = _power.Handle(e);
        if (action is PowerAction.BlankAndStop or PowerAction.BlankAndHold) released = ReleasePusher();
        if (action == PowerAction.BlankAndHold) _resumeAfterSleep = released;
    }
    if (action == PowerAction.BlankAndHold && released)
        Dispatcher.UIThread.Post(() => _status.Text = "Display off while Windows sleeps.");
    else if (action == PowerAction.Reopen && _resumeAfterSleep)
    {
        _resumeAfterSleep = false;
        Dispatcher.UIThread.Post(ResumeSendingAfterSleep);
    }
}
```

Unlike the runner, the editor only drives the display while the user has ticked **Send to display**. So it records whether it
actually released a pusher at suspend (`_resumeAfterSleep`), and after wake it reopens only in that case. When the editor sends
through the runner (`_liveToRunner`), it never owns the device, and the runner handles sleep.

**3. Retrying after wake:**

```csharp
private async void ResumeSendingAfterSleep()
{
    string? error = null;
    for (int attempt = 0; attempt < 5; attempt++)
    {
        await Task.Delay(1500);
        if (_send.IsChecked != true || _pusher is not null || _liveToRunner || !_power.MayDrive) return; // the user (or Windows) moved on
        if (TryStartPusher(out error)) { _status.Text = "Sending to the display..."; return; }
    }
    _status.Text = "Could not reopen the display after sleep: " + error;
    _send.IsChecked = false;
}
```

Each attempt first checks whether the world has changed: the user unticked Send, opened the display some other way, switched to the
runner, or Windows went back to sleep. That check is what keeps an `async void` retry loop from fighting the user.

**4. Closing the window.** `Closing` now records `Quit` in the policy before releasing the pusher, so nothing can reopen the display
while the editor shuts down:

```csharp
Closing += (_, _) =>
{
    StopLive();
    lock (_powerGate) _power.Handle(PowerEvent.Quit);   // nothing may reopen the display from here on
    ReleasePusher();
    _powerWatcher?.Dispose();
    _renderer.Dispose(); _hub.Dispose(); _settings.Save();
};
```

There is a subtlety here. Avalonia handles `WM_QUERYENDSESSION` itself and closes its windows on an OS shutdown. In practice the editor's
session-end blank therefore probably happens in this `Closing` handler, on the UI thread, slightly *before* the watcher's
`WM_ENDSESSION`. The watcher is then a backup for session end, and the main path for sleep. Either way, the policy guarantees only one blank.

### 4.6 Tests

**File:** `src/Glance.Tests/PowerTests.cs` (20 tests)

There are three layers, from pure to real.

**The policy:** plain state-machine tests, including a concurrency test showing that only one of 64 parallel "enders" gets to blank:

```csharp
[Fact]
public void Only_one_of_many_concurrent_enders_gets_to_blank()
{
    var p = new DisplayPowerPolicy();
    int blanks = 0;
    Parallel.For(0, 64, i =>
    {
        if (p.Handle(i % 2 == 0 ? PowerEvent.SessionEnding : PowerEvent.Quit) == PowerAction.BlankAndStop) Interlocked.Increment(ref blanks);
    });
    Assert.Equal(1, blanks);
}
```

**The translation:** each `PBT_*` code and `WM_ENDSESSION` TRUE/FALSE, plus codes that must be ignored (`PBT_POWERSETTINGCHANGE`,
`PBT_APMPOWERSTATUSCHANGE`).

**The real window.** These tests are the interesting ones. They create a real watcher and talk to it the way Windows would. Nothing
is shut down: `SendMessage` just delivers the same message to the window procedure. One test checks the property the whole design
depends on, a **top-level** window, by asking Windows for the window's parent:

```csharp
[Fact]
public void Window_is_hidden_and_top_level_so_it_receives_broadcasts()
{
    using var w = new SessionPowerWatcher(_ => { });
    Assert.NotEqual(IntPtr.Zero, w.WindowHandle);
    Assert.False(IsWindowVisible(w.WindowHandle));
    // a message-only window (HWND_MESSAGE) would not get WM_ENDSESSION / WM_POWERBROADCAST broadcasts
    Assert.Equal(GetDesktopWindow(), GetAncestor(w.WindowHandle, GA_PARENT));
}
```

Another uses `ShutdownBlockReasonQuery` from inside the handler to prove that the block reason exists *while* the session end is
handled, and is gone afterwards. CI runs on `windows-latest`, so these Win32 tests run there too.

A mutation check during review showed one weak spot. If `Raise` is changed to run the handler asynchronously, the test named
`Messages_reach_the_handler_on_the_watcher_thread_before_the_window_answers` still passes, because the thread pool wins the race
against the assert. Making the handler sleep briefly before recording would make it a real guard (see the review, L6).

What is **not** tested is the wiring in `RunnerContext` and `MainWindow`: that `Supervise` refuses to open while `!MayDrive`, or that
a session end leads to `Quit`. Those projects have no tests, which is suggestion 3 below.

### 4.7 Merging with the display-loop fix

A sibling branch, `claude/fix-display-loop-693991`, reworks `DisplayPusher`:

- The loop now owns the device.
- `Stop(blank)` asks the loop to finish.
- The loop blanks and closes the display on its way out.
- `Stop` waits up to `StopTimeout` (18 s) for this.

This branch deliberately did not touch `DisplayPusher` and only calls `Stop(blank: true)` and `Dispose()`.

After the merge, blanking still happens **before** the handler returns in the normal case:

1. `Stop` sets `_blankOnStop` and cancels.
2. The loop wakes immediately from `WaitOne` and finishes its current transfer.
3. The loop calls `Close()`, which runs `Blank()` and then `Dispose()`.
4. `Stop` returns.

That takes well under a second. The two things to fix at merge time are in the review:

- **M1:** with a stalled device, the 18 s wait is far beyond Windows' few-second budget.
- **M2:** the sibling's new `RestartDeadPusher` in the editor opens the display without `_powerGate`.

The textual merge is clean, so these are semantic issues that a merge tool will not flag.

---

## 5. Self-review: five ways to improve the change

These are the changes I would make next, roughly in order of value.

### 1. Give the power-event stop a deadline

The handler's time is not ours: Windows allows a few seconds for `WM_ENDSESSION` and about 2 s for `PBT_APMSUSPEND`. Today the
handler calls the general-purpose `Stop`, which can wait 2 s (and 18 s after the display-loop merge). In the runner it may also wait
for `_gate` behind a slow `Supervise` or `Reload` first. With a shutdown block reason set for the whole time, a stalled USB device
can put the user on the "apps are preventing shutdown" screen.

**Suggestion:** add `DisplayPusher.Stop(bool blank, TimeSpan timeout)` as a public method (it exists as `internal` on the sibling
branch) and use about 3 s for a session end and 1.5 s for suspend. In the runner:

1. Update the policy first. It has its own lock, so `MayDrive` turns false at once.
2. Take the pusher with `Interlocked.Exchange`, as the editor does, instead of waiting for `_gate` with no time limit.
3. Log whether the stop finished in time.

### 2. Recover from a missed resume

`Suspended` is cleared only by a `Resume` notification. If one is ever missed (a failed or vetoed suspend, or a driver quirk), the
runner stays dark until it is restarted. "Reload theme" and replugging do not help, because `Supervise` returns early while
`!MayDrive`. A session end while the state is stuck also returns `None`, so the runner does not quit.

**Suggestion:**
- Use the fact that WinForms timers do not tick during sleep. Add `DisplayPowerPolicy.NoteAwake(TimeSpan awakeFor)`, which clears a
  suspend that has lasted, say, 30 s of supervisor ticks.
- Always post `Quit` for `PowerEvent.SessionEnding`, whatever the action.
- Both are a few lines and easy to unit test.

### 3. Move the "owner" logic into Core so the wiring is tested

The policy is tested, but the code that *uses* it is not:
- `Supervise` checks `MayDrive`.
- `OnPowerEvent` disposes and then posts `Quit` or `Supervise`.
- The editor's `_resumeAfterSleep` and its retry loop.

Remove the guard at the top of `Supervise` and no test fails. The two owners also duplicate the same pattern with different locks.

**Suggestion:** add a `PowerAwareDisplayOwner` to `Glance.Core.Display`. It holds the lock, the policy and a pusher factory, and
exposes `TryOpen()`, `Release()` and `OnPowerEvent(e)`, which returns what to do next (quit, resupervise, resume). The runner and
editor keep only the thread marshalling. With the `IFrameDisplay` fake from the display-loop branch, you can test "no reopen after a
session end", "reopen after wake" and "the deadline holds with a stalled device" without hardware.

### 4. Make the synchronous-delivery test fail when it should

The property "the display is blanked before Windows gets the answer" is the reason the watcher exists. The test meant to guard it
passed even when the handler was made asynchronous (it lost a race only by luck).

**Suggestion:** give the test handler a small `Thread.Sleep(150)` before it records the event, and assert straight after `SendMessage`.
An asynchronous handler then fails every time. While there, add a test that `Dispose` from inside the handler does not deadlock.

### 5. Cover screen-off and Modern Standby, and say what is covered

On Modern Standby PCs the screen goes dark long before `PBT_APMSUSPEND` arrives, so the LCD can stay lit for minutes. Locking the PC
or letting the monitor sleep does not blank it at all. Some users will expect "the LCD follows the monitor".

**Suggestion:**
- Register the watcher window for `GUID_CONSOLE_DISPLAY_STATE` with `RegisterPowerSettingNotification`.
- Translate the values to new `DisplayOff`/`DisplayOn` events.
- Let the policy hold the display while the console display is off, behind a setting (default off, so the current behaviour does not change).
- Until then, say in the TUTORIAL that the display blanks on sleep, shutdown and sign-out, not when the monitor turns off.
- Add a manual test on a Modern Standby laptop to the release checklist. The development PC supports only S3 sleep.

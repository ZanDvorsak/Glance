using System.Runtime.InteropServices;
using Glance.Core.Display;

namespace Glance.Tests;

public class DisplayPowerPolicyTests
{
    [Fact]
    public void Starts_allowed_to_drive_the_display()
    {
        var p = new DisplayPowerPolicy();
        Assert.True(p.MayDrive);
        Assert.False(p.Ended);
        Assert.False(p.Suspended);
    }

    [Fact]
    public void Session_end_blanks_and_stops_and_never_allows_reopening()
    {
        var p = new DisplayPowerPolicy();
        Assert.Equal(PowerAction.BlankAndStop, p.Handle(PowerEvent.SessionEnding));
        Assert.False(p.MayDrive);
        Assert.True(p.Ended);
        // nothing afterwards brings it back
        Assert.Equal(PowerAction.None, p.Handle(PowerEvent.Resume));
        Assert.Equal(PowerAction.None, p.Handle(PowerEvent.Suspend));
        Assert.False(p.MayDrive);
    }

    [Fact]
    public void Final_blank_happens_once_when_session_end_and_quit_race()
    {
        var p = new DisplayPowerPolicy();
        Assert.Equal(PowerAction.BlankAndStop, p.Handle(PowerEvent.SessionEnding));
        Assert.Equal(PowerAction.None, p.Handle(PowerEvent.Quit));
        Assert.Equal(PowerAction.None, p.Handle(PowerEvent.SessionEnding));

        var q = new DisplayPowerPolicy();
        Assert.Equal(PowerAction.BlankAndStop, q.Handle(PowerEvent.Quit));
        Assert.Equal(PowerAction.None, q.Handle(PowerEvent.SessionEnding));
        Assert.False(q.MayDrive);
    }

    [Fact]
    public void Sleep_blanks_and_holds_then_wake_reopens()
    {
        var p = new DisplayPowerPolicy();
        Assert.Equal(PowerAction.BlankAndHold, p.Handle(PowerEvent.Suspend));
        Assert.False(p.MayDrive);
        Assert.True(p.Suspended);
        Assert.False(p.Ended);
        Assert.Equal(PowerAction.Reopen, p.Handle(PowerEvent.Resume));
        Assert.True(p.MayDrive);
    }

    [Fact]
    public void Duplicate_suspend_and_resume_notifications_act_once()
    {
        var p = new DisplayPowerPolicy();
        Assert.Equal(PowerAction.None, p.Handle(PowerEvent.Resume));        // resume without a suspend: nothing to do
        Assert.Equal(PowerAction.BlankAndHold, p.Handle(PowerEvent.Suspend));
        Assert.Equal(PowerAction.None, p.Handle(PowerEvent.Suspend));
        Assert.Equal(PowerAction.Reopen, p.Handle(PowerEvent.Resume));      // PBT_APMRESUMEAUTOMATIC
        Assert.Equal(PowerAction.None, p.Handle(PowerEvent.Resume));        // PBT_APMRESUMESUSPEND right after
        Assert.True(p.MayDrive);
    }

    [Fact]
    public void Session_end_while_asleep_still_stops_so_the_owner_exits_and_wake_does_not_reopen()
    {
        // review L1: with a stuck "asleep" state, an installer's WM_ENDSESSION must still make the runner quit
        var p = new DisplayPowerPolicy();
        p.Handle(PowerEvent.Suspend);
        Assert.Equal(PowerAction.BlankAndStop, p.Handle(PowerEvent.SessionEnding)); // the blank is a no-op (nothing open), the stop is not
        Assert.True(p.Ended);
        Assert.Equal(PowerAction.None, p.Handle(PowerEvent.Quit));
        Assert.Equal(PowerAction.None, p.Handle(PowerEvent.Resume));
        Assert.False(p.MayDrive);
    }

    [Fact]
    public void A_missed_wake_notice_heals_after_enough_awake_ticks()
    {
        // review L1: if Windows never sends the resume notification, the display must not stay dark for good
        var p = new DisplayPowerPolicy();
        p.Handle(PowerEvent.Suspend);
        for (int i = 0; i < 14; i++) Assert.Equal(PowerAction.None, p.NoteAwakeTick(15));
        Assert.Equal(PowerAction.Reopen, p.NoteAwakeTick(15));
        Assert.True(p.MayDrive);
        Assert.Equal(PowerAction.None, p.Handle(PowerEvent.Resume));   // the late real notice changes nothing
    }

    [Fact]
    public void Awake_ticks_do_nothing_when_awake_or_after_the_session_ended_and_restart_with_each_sleep()
    {
        var p = new DisplayPowerPolicy();
        for (int i = 0; i < 20; i++) Assert.Equal(PowerAction.None, p.NoteAwakeTick(3));   // not asleep
        p.Handle(PowerEvent.Suspend);
        p.NoteAwakeTick(3); p.NoteAwakeTick(3);
        p.Handle(PowerEvent.Resume);
        p.Handle(PowerEvent.Suspend);                                                        // a new sleep starts the count again
        Assert.Equal(PowerAction.None, p.NoteAwakeTick(3));
        Assert.Equal(PowerAction.None, p.NoteAwakeTick(3));
        Assert.Equal(PowerAction.Reopen, p.NoteAwakeTick(3));

        var ended = new DisplayPowerPolicy();
        ended.Handle(PowerEvent.Suspend);
        ended.Handle(PowerEvent.SessionEnding);
        for (int i = 0; i < 20; i++) Assert.Equal(PowerAction.None, ended.NoteAwakeTick(3));
        Assert.False(ended.MayDrive);
    }

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
}

public class PowerMessagesTests
{
    [Fact]
    public void End_session_true_is_a_session_end_and_false_is_a_cancelled_shutdown()
    {
        Assert.Equal(PowerEvent.SessionEnding, PowerMessages.Translate(PowerMessages.WM_ENDSESSION, 1));
        Assert.Null(PowerMessages.Translate(PowerMessages.WM_ENDSESSION, 0));
    }

    [Fact]
    public void Query_end_session_does_nothing_by_itself()
    {
        // the shutdown can still be cancelled at that point; the display is blanked in WM_ENDSESSION
        Assert.Null(PowerMessages.Translate(PowerMessages.WM_QUERYENDSESSION, 0));
    }

    [Theory]
    [InlineData(PowerMessages.PBT_APMSUSPEND, PowerEvent.Suspend)]
    [InlineData(PowerMessages.PBT_APMRESUMEAUTOMATIC, PowerEvent.Resume)]
    [InlineData(PowerMessages.PBT_APMRESUMESUSPEND, PowerEvent.Resume)]
    public void Power_broadcasts_map_to_sleep_and_wake(int pbt, PowerEvent expected) =>
        Assert.Equal(expected, PowerMessages.Translate(PowerMessages.WM_POWERBROADCAST, pbt));

    [Theory]
    [InlineData(0x8013)] // PBT_POWERSETTINGCHANGE
    [InlineData(0xA)]    // PBT_APMPOWERSTATUSCHANGE (on battery / plugged in)
    public void Other_power_broadcasts_are_ignored(int pbt) =>
        Assert.Null(PowerMessages.Translate(PowerMessages.WM_POWERBROADCAST, pbt));
}

public class PowerBlankTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(300);

    [Fact]
    public void Gives_up_on_a_stalled_display_within_its_budget()
    {
        // review M1: a display that stops answering must not hold Windows' shutdown or sleep
        var opener = new FakeOpener { Make = () => new FakeDisplay { BlitDelayMs = 5000 } };
        var p = opener.Open();
        Assert.True(opener.Displays[0].BlitStarted.Wait(5000));
        DisplayPusher? stuck = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = PowerBlank.Run(new object(), () => p, Budget, s => stuck = s);
        sw.Stop();
        Assert.Equal(PowerBlank.Result.TimedOut, result);
        Assert.Same(p, stuck);                 // the owner is told the device is still held
        Assert.False(p.IsClosed);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Does_not_wait_for_ever_behind_a_busy_owner()
    {
        // review M1: the runner's UI thread can hold its lock for seconds while it opens or reloads the display
        var gate = new object();
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() => { lock (gate) { held.Set(); release.Wait(5000); } }) { IsBackground = true };
        owner.Start();
        held.Wait();
        bool taken = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = PowerBlank.Run(gate, () => { taken = true; return (DisplayPusher?)null; }, Budget);
        sw.Stop();
        release.Set();
        Assert.Equal(PowerBlank.Result.Busy, result);
        Assert.False(taken);
        Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Blanks_normally_when_nothing_is_stuck()
    {
        var opener = new FakeOpener();
        var p = opener.Open();
        Assert.True(opener.Displays[0].BlitStarted.Wait(5000));
        Assert.Equal(PowerBlank.Result.Blanked, PowerBlank.Run(new object(), () => p, TimeSpan.FromSeconds(1.2), _ => Assert.Fail("not stuck")));
        Assert.Equal(1, opener.Displays[0].Blanks);
        Assert.True(p.IsClosed);
        Assert.Equal(PowerBlank.Result.NothingOpen, PowerBlank.Run(new object(), () => null, Budget));
    }

    [Fact]
    public void Budgets_fit_inside_what_Windows_allows()
    {
        Assert.True(PowerBlank.BudgetFor(PowerEvent.SessionEnding) <= TimeSpan.FromSeconds(4));   // WM_ENDSESSION: about 5 s
        Assert.True(PowerBlank.BudgetFor(PowerEvent.Suspend) <= TimeSpan.FromSeconds(1.5));       // PBT_APMSUSPEND: about 2 s
        foreach (var e in new[] { PowerEvent.SessionEnding, PowerEvent.Suspend })
            Assert.True(PowerBlank.OwnerBudgetFor(e) < PowerBlank.BudgetFor(e) && PowerBlank.OwnerBudgetFor(e) > TimeSpan.Zero);
    }

    [Fact]
    public void Wake_retries_last_long_enough_for_a_hibernate_resume()
    {
        // review L2: the editor gave up after 7.5 s; USB can take longer to come back after hibernate
        var delays = ResumeRetry.Delays().ToList();
        var total = TimeSpan.FromTicks(delays.Sum(d => d.Ticks));
        Assert.True(total >= TimeSpan.FromSeconds(45), $"retries for {total.TotalSeconds} s only");
        Assert.True(total <= TimeSpan.FromSeconds(90));
        Assert.True(delays[0] <= TimeSpan.FromSeconds(2));                       // the common case is still quick
        Assert.All(delays, d => Assert.True(d <= TimeSpan.FromSeconds(5)));      // and a late display is picked up soon
    }
}

/// <summary>Uses a real (hidden) window and sends it the messages Windows would send. Nothing is shut down or put to sleep.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public class SessionPowerWatcherTests
{
    [Fact]
    public void Handler_has_finished_when_the_window_answers()
    {
        // review L6: the display must be blank before Windows gets the answer; a slow handler exposes an asynchronous one
        var seen = new List<PowerEvent>();
        using var w = new SessionPowerWatcher(e => { Thread.Sleep(150); lock (seen) seen.Add(e); });
        SendMessageW(w.WindowHandle, PowerMessages.WM_ENDSESSION, 1, 0);
        lock (seen) Assert.Equal(new[] { PowerEvent.SessionEnding }, seen);
        SendMessageW(w.WindowHandle, PowerMessages.WM_POWERBROADCAST, PowerMessages.PBT_APMSUSPEND, 0);
        lock (seen) Assert.Equal(new[] { PowerEvent.SessionEnding, PowerEvent.Suspend }, seen);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Windows_gets_its_answer_within_the_budget_even_if_the_handler_hangs(bool sessionEnd)
    {
        // review M1: whatever the owner does, the watcher answers in time and always drops the shutdown block reason
        using var release = new ManualResetEventSlim();
        var budget = TimeSpan.FromMilliseconds(300);
        using var w = new SessionPowerWatcher(_ => release.Wait(5000), sessionEndBudget: budget, otherBudget: budget);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (sessionEnd) SendMessageW(w.WindowHandle, PowerMessages.WM_ENDSESSION, 1, 0);
        else SendMessageW(w.WindowHandle, PowerMessages.WM_POWERBROADCAST, PowerMessages.PBT_APMSUSPEND, 0);
        sw.Stop();
        uint n = 0;
        bool stillBlocking = ShutdownBlockReasonQuery(w.WindowHandle, IntPtr.Zero, ref n);
        release.Set();
        Assert.True(sw.ElapsedMilliseconds < 1500, $"answered after {sw.ElapsedMilliseconds} ms");
        Assert.False(stillBlocking);
    }

    [Fact]
    public void Window_is_hidden_and_top_level_so_it_receives_broadcasts()
    {
        using var w = new SessionPowerWatcher(_ => { });
        Assert.NotEqual(IntPtr.Zero, w.WindowHandle);
        Assert.False(IsWindowVisible(w.WindowHandle));
        // a message-only window (HWND_MESSAGE) would not get WM_ENDSESSION / WM_POWERBROADCAST broadcasts
        Assert.Equal(GetDesktopWindow(), GetAncestor(w.WindowHandle, GA_PARENT));
    }

    [Fact]
    public void Messages_reach_the_handler_on_the_watcher_thread_before_the_window_answers()
    {
        var seen = new List<PowerEvent>();
        int testThread = Environment.CurrentManagedThreadId, handlerThread = -1;
        using var w = new SessionPowerWatcher(e => { lock (seen) seen.Add(e); handlerThread = Environment.CurrentManagedThreadId; });

        // SendMessage returns only after the window procedure (and so the handler) finished
        Assert.Equal((IntPtr)1, SendMessageW(w.WindowHandle, PowerMessages.WM_POWERBROADCAST, PowerMessages.PBT_APMSUSPEND, 0));
        Assert.Equal(new[] { PowerEvent.Suspend }, seen);
        SendMessageW(w.WindowHandle, PowerMessages.WM_POWERBROADCAST, PowerMessages.PBT_APMRESUMEAUTOMATIC, 0);
        SendMessageW(w.WindowHandle, PowerMessages.WM_ENDSESSION, 0, 0);                    // cancelled shutdown: ignored
        Assert.Equal((IntPtr)1, SendMessageW(w.WindowHandle, PowerMessages.WM_QUERYENDSESSION, 0, 0)); // never blocks shutdown
        SendMessageW(w.WindowHandle, PowerMessages.WM_ENDSESSION, 1, 0);
        Assert.Equal(new[] { PowerEvent.Suspend, PowerEvent.Resume, PowerEvent.SessionEnding }, seen);
        Assert.NotEqual(testThread, handlerThread);
    }

    [Fact]
    public void A_shutdown_block_reason_is_set_only_while_the_session_end_is_handled()
    {
        bool? blockedDuring = null;
        IntPtr hwnd = IntPtr.Zero;
        using var w = new SessionPowerWatcher(_ => { uint n = 0; blockedDuring = ShutdownBlockReasonQuery(hwnd, IntPtr.Zero, ref n); }, "Turning off the test display");
        hwnd = w.WindowHandle;
        SendMessageW(hwnd, PowerMessages.WM_ENDSESSION, 1, 0);
        Assert.True(blockedDuring);
        uint len = 0;
        Assert.False(ShutdownBlockReasonQuery(hwnd, IntPtr.Zero, ref len));   // released straight after

        // sleep does not set one
        SendMessageW(hwnd, PowerMessages.WM_POWERBROADCAST, PowerMessages.PBT_APMSUSPEND, 0);
        Assert.False(blockedDuring);
    }

    [Fact]
    public void A_failing_handler_does_not_break_the_watcher()
    {
        int calls = 0;
        using var w = new SessionPowerWatcher(_ => { calls++; throw new InvalidOperationException("boom"); });
        SendMessageW(w.WindowHandle, PowerMessages.WM_POWERBROADCAST, PowerMessages.PBT_APMSUSPEND, 0);
        SendMessageW(w.WindowHandle, PowerMessages.WM_POWERBROADCAST, PowerMessages.PBT_APMRESUMEAUTOMATIC, 0);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Dispose_destroys_the_window()
    {
        var w = new SessionPowerWatcher(_ => { });
        var hwnd = w.WindowHandle;
        Assert.True(IsWindow(hwnd));
        w.Dispose();
        Assert.False(IsWindow(hwnd));
    }

    private const uint GA_PARENT = 1;
    [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern bool ShutdownBlockReasonQuery(IntPtr hwnd, IntPtr buffer, ref uint length);
}

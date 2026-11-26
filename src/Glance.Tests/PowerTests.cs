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
    public void Session_end_while_asleep_needs_no_second_blank_and_wake_does_not_reopen()
    {
        var p = new DisplayPowerPolicy();
        p.Handle(PowerEvent.Suspend);
        Assert.Equal(PowerAction.None, p.Handle(PowerEvent.SessionEnding)); // already dark and released
        Assert.True(p.Ended);
        Assert.Equal(PowerAction.None, p.Handle(PowerEvent.Resume));
        Assert.False(p.MayDrive);
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
    [InlineData(PowerMessages.PBT_APMRESUMECRITICAL, PowerEvent.Resume)]
    public void Power_broadcasts_map_to_sleep_and_wake(int pbt, PowerEvent expected) =>
        Assert.Equal(expected, PowerMessages.Translate(PowerMessages.WM_POWERBROADCAST, pbt));

    [Theory]
    [InlineData(0x8013)] // PBT_POWERSETTINGCHANGE
    [InlineData(0xA)]    // PBT_APMPOWERSTATUSCHANGE (on battery / plugged in)
    public void Other_power_broadcasts_are_ignored(int pbt) =>
        Assert.Null(PowerMessages.Translate(PowerMessages.WM_POWERBROADCAST, pbt));
}

/// <summary>Uses a real (hidden) window and sends it the messages Windows would send. Nothing is shut down or put to sleep.</summary>
public class SessionPowerWatcherTests
{
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

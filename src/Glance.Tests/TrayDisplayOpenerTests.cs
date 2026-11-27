using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Glance.Core.Display;
using Glance.Core.Sensors;
using Glance.Core.Themes;

namespace Glance.Tests;

/// <summary>
/// The tray app's display open (review of the deadlock fix, findings L1-L3): <see cref="TrayDisplayOpener"/> opens off the UI
/// thread and decides what to do with the result; RunnerContext only applies it. A fake owner stands in for RunnerContext.
/// </summary>
public sealed class TrayDisplayOpenerTests
{
    private static readonly PusherTiming Quick = new(FrameIntervalMs: 20, ErrorBackoffMs: 20, NoPagesReportMs: 100);
    private static readonly ISensorSource NoSensors = new FakeSource(Array.Empty<SensorInfo>());

    private sealed class FakeOwner : ITrayDisplayOwner
    {
        public object Gate { get; } = new();
        public DisplayPowerPolicy Power { get; } = new();
        public volatile bool IsPaused;
        public DisplayPusher? Pusher;
        public readonly List<DisplayPusher> Closing = new();
        public readonly List<string> Logs = new();
        public string? Failed;
        public int Changes, Adopts, Failures;
        public Action? OnChanged;

        public bool Paused => IsPaused;
        public bool HasPusher => Pusher is not null;
        public void Adopt(DisplayPusher pusher) { Pusher = pusher; Adopts++; }
        public void RememberClosing(DisplayPusher pusher) => Closing.Add(pusher);
        public void OpenFailed(string message) { Failed = message; Failures++; }
        public void Log(string line) { lock (Logs) Logs.Add(line); }
        public void Changed() { Interlocked.Increment(ref Changes); OnChanged?.Invoke(); }
        public string[] LogLines { get { lock (Logs) return Logs.ToArray(); } }
    }

    private static DisplayPusher Pusher(FakeDisplay lcd, int brightness = 4) => new(() => Theme.CreateDefault(null), NoSensors, brightness, lcd, Quick);

    // ---- L1: stopping an unwanted pusher never holds the tray's lock --------------------------------------------------------

    [Fact]
    public void A_pusher_opened_while_paused_is_stopped_without_holding_the_trays_lock()
    {
        var owner = new FakeOwner { IsPaused = true };   // the user paused while the display opened
        var lcd = new FakeDisplay(480, 320) { BlitDelayMs = 1500 };   // its first transfer stalls
        var opener = new TrayDisplayOpener(owner, (b, _) =>
        {
            var p = Pusher(lcd, b);
            lcd.BlitStarted.Wait(5000);   // hand it over mid-transfer, so its stop has to wait for the transfer
            return p;
        });
        Assert.True(opener.Start(4, null));
        Assert.True(lcd.BlitStarted.Wait(5000));
        Thread.Sleep(300);   // Opened is now stopping it

        // the tray's 2 s Supervise tick and the editor's NEXT/PAGE/LIVE take this lock; they must not wait for a stalled transfer
        bool free = Monitor.TryEnter(owner.Gate, 500);
        if (free) Monitor.Exit(owner.Gate);
        Assert.True(free, "the tray's lock was held while a stalled pusher was being stopped");

        Assert.True(SpinWait.SpinUntil(() => lcd.Disposes == 1 && !opener.IsOpening, 10_000));
        Assert.True(lcd.Blanks >= 1);   // paused: the screen goes black
        Assert.Equal(0, owner.Adopts);
    }

    // ---- L2: quitting waits for an open in flight, and Opened does nothing to the owner afterwards ---------------------------

    [Fact]
    public void Quitting_waits_for_an_open_in_flight_and_blanks_what_it_opened()
    {
        var owner = new FakeOwner();
        var lcd = new FakeDisplay(480, 320);
        var opener = new TrayDisplayOpener(owner, (b, _) => { Thread.Sleep(400); return Pusher(lcd, b); });   // a slow service
        Assert.True(opener.Start(4, null));

        bool idle = opener.Close(TimeSpan.FromSeconds(5));   // Quit, before it disposes the sensors

        Assert.True(idle, "Close returned while the open was still running");
        Assert.Equal(1, lcd.Disposes);    // the display it opened is closed before Quit goes on (and the sensors are disposed)
        Assert.True(lcd.Blanks >= 1);     // and blanked, so it does not keep its first frame lit
        Assert.Equal(0, owner.Adopts);
        Assert.Equal(0, owner.Changes);   // nothing is posted to a UI that is closing
        Assert.DoesNotContain(owner.LogLines, l => l.StartsWith("Display opened"));
    }

    [Fact]
    public void An_open_that_finishes_after_quit_only_closes_its_display()
    {
        var owner = new FakeOwner();
        var lcd = new FakeDisplay(480, 320);
        using var release = new ManualResetEventSlim();
        var opener = new TrayDisplayOpener(owner, (b, _) => { release.Wait(10_000); return Pusher(lcd, b); });
        Assert.True(opener.Start(4, null));

        Assert.False(opener.Close(TimeSpan.FromMilliseconds(200)));   // the quit budget ran out
        release.Set();

        Assert.True(SpinWait.SpinUntil(() => lcd.Disposes == 1, 10_000), "the late display was never closed");
        Assert.True(lcd.Blanks >= 1);
        Assert.Equal(0, owner.Adopts);
        Assert.Equal(0, owner.Changes);
        Assert.False(opener.Start(4, null));   // nothing opens after quit
    }

    // ---- L3: the open never runs on the caller's (UI) thread, and the tray keeps its start-up order --------------------------

    [Fact]
    public void Starting_an_open_on_the_UI_thread_returns_at_once()
    {
        var owner = new FakeOwner();
        using var release = new ManualResetEventSlim();
        int openThread = 0, uiThread = 0;
        var opener = new TrayDisplayOpener(owner, (b, _) =>
        {
            openThread = Environment.CurrentManagedThreadId;
            release.Wait(10_000);   // a service that takes its time
            throw new IOException("no display");
        });
        bool second = true;
        var o = UiLikeContext.Run(() =>
        {
            uiThread = Environment.CurrentManagedThreadId;
            Assert.True(opener.Start(4, null));
            second = opener.Start(4, null);
        }, TimeSpan.FromSeconds(8));
        release.Set();
        Assert.True(o.Finished);
        Assert.Null(o.Error);
        Assert.True(o.Took < TimeSpan.FromSeconds(1), $"the UI thread waited {o.Took.TotalSeconds:0.0} s");
        Assert.False(second);   // one attempt at a time
        Assert.True(SpinWait.SpinUntil(() => owner.Failures == 1, 5000));
        Assert.NotEqual(uiThread, openThread);
        Assert.Equal("no display", owner.Failed);
    }

    [Theory]
    [InlineData(false, false, false, "adopt")]
    [InlineData(true, false, false, "stop+blank")]    // Windows is sleeping or ending the session
    [InlineData(false, true, false, "stop+blank")]    // paused
    [InlineData(false, false, true, "stop")]          // another pusher already runs: no flicker
    public void An_opened_display_is_kept_only_when_nothing_changed_meanwhile(bool asleep, bool paused, bool alreadyOpen, string expected)
    {
        var owner = new FakeOwner { IsPaused = paused };
        if (asleep) owner.Power.Handle(PowerEvent.Suspend);
        var existing = alreadyOpen ? Pusher(new FakeDisplay(480, 320)) : null;
        owner.Pusher = existing;
        var lcd = new FakeDisplay(480, 320);
        var opener = new TrayDisplayOpener(owner, (b, _) => Pusher(lcd, b));
        try
        {
            Assert.True(opener.Start(4, null));
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref owner.Changes) > 0, 10_000));
            string actual = owner.Adopts == 1 ? "adopt" : lcd.Disposes == 1 ? (lcd.Blanks > 0 ? "stop+blank" : "stop") : "?";
            Assert.Equal(expected, actual);
        }
        finally { existing?.Stop(false, TimeSpan.FromSeconds(5)); owner.Pusher?.Stop(false, TimeSpan.FromSeconds(5)); }
    }

    private static string RunnerSource([CallerFilePath] string here = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "Glance.Runner", "RunnerContext.cs")));

    [Fact]
    public void The_tray_app_starts_its_control_pipe_and_timers_before_the_first_open()
    {
        // Glance 0.2.0 opened the display first in the constructor and never got to the control pipe. The order lives in
        // RunnerContext (WinForms, no tests), so check the source: the constructor body, in order.
        var src = RunnerSource();
        int ctor = src.IndexOf("public RunnerContext()", StringComparison.Ordinal);
        Assert.True(ctor >= 0);
        int end = src.IndexOf("\n    }", ctor, StringComparison.Ordinal);
        var body = src[ctor..end];
        int control = body.IndexOf("_control = StartControlServer()", StringComparison.Ordinal);   // TrayControlServer (live-lease branch)
        int timers = body.IndexOf("_supervisor.Start()", StringComparison.Ordinal);
        int firstOpen = Regex.Match(body, @"^\s*Supervise\(\);", RegexOptions.Multiline).Index;
        Assert.True(control > 0, "the constructor no longer starts the control pipe");
        Assert.True(timers > 0, "the constructor no longer starts the supervisor timer");
        Assert.True(firstOpen > 0, "the constructor no longer starts the first open");
        Assert.True(control < firstOpen, "the first Supervise() (display open) runs before the control pipe starts");
        Assert.True(timers < firstOpen, "the first Supervise() runs before the timers start");
    }

    [Fact]
    public void The_tray_app_creates_pushers_only_through_the_background_opener()
    {
        var src = RunnerSource();
        Assert.DoesNotMatch(@"new\s+DisplayPusher\s*\(", src);   // only OpenPusher creates one (target-typed new)
        Assert.DoesNotMatch(@"_pusher\s*=\s*new\b", src);
        Assert.Matches(@"DisplayPusher\s+OpenPusher\(int\s+\w+,\s*int\?\s+\w+\)\s*=>\s*new\(", src);
        Assert.Matches(@"new\s+TrayDisplayOpener\(\s*this\s*,\s*OpenPusher\s*\)", src);
        Assert.Equal(2, Regex.Matches(src, @"\bOpenPusher\b").Count);   // declared, and handed to the opener: never called on the UI thread
    }

    // ---- nits: the page pin reaches the open, and a failed open is retried soon -------------------------------------------

    [Fact]
    public void The_page_pin_is_passed_to_the_open()
    {
        var owner = new FakeOwner();
        int? pin = -1;
        var opener = new TrayDisplayOpener(owner, (b, p) => { pin = p; throw new IOException("no display"); });
        Assert.True(opener.Start(4, 2));
        Assert.True(SpinWait.SpinUntil(() => owner.Failures == 1, 5000));
        Assert.Equal(2, pin);
    }

    [Fact]
    public void A_failed_open_is_retried_within_a_second_not_on_the_next_2_s_tick()
    {
        var owner = new FakeOwner();
        var lcd = new FakeDisplay(480, 320);
        int attempts = 0;
        TrayDisplayOpener? opener = null;
        opener = new TrayDisplayOpener(owner, (b, _) => Interlocked.Increment(ref attempts) == 1 ? throw new IOException("service not ready") : Pusher(lcd, b));
        // what the tray's Supervise does when Changed() posts it: open again unless one runs or a pusher exists
        owner.OnChanged = () => { lock (owner.Gate) if (!owner.HasPusher && !opener.IsOpening) opener.Start(4, null); };
        Assert.True(opener.Start(4, null));
        try
        {
            Assert.True(SpinWait.SpinUntil(() => owner.Adopts == 1, 1500), $"no retry within 1.5 s ({attempts} attempts)");
            Assert.Equal(2, attempts);
        }
        finally { owner.Pusher?.Stop(false, TimeSpan.FromSeconds(5)); }
    }

    /// <summary>Records which page the pusher was on when its first frame was sent.</summary>
    private sealed class FirstFrameProbe : IFrameDisplay
    {
        public volatile DisplayPusher? Owner;
        public int FirstPage = -1;
        public readonly ManualResetEventSlim Sent = new();
        public int Width => 480;
        public int Height => 320;
        public void Blit(int x, int y, int w, int h, byte[] rgb565Be)
        {
            if (Sent.IsSet) return;
            SpinWait.SpinUntil(() => Owner is not null, 5000);
            FirstPage = Owner!.CurrentPageIndex;
            Sent.Set();
        }
        public void SetBrightness(int level) { }
        public void Blank() { }
        public void Dispose() { }
    }

    [Fact]
    public void A_pusher_opened_with_a_page_pin_shows_that_page_in_its_first_frame()
    {
        var theme = Theme.CreateDefault(null);
        theme.Pages.Add(new ThemePage { Name = "Two" });
        theme.Pages.Add(new ThemePage { Name = "Three" });
        var probe = new FirstFrameProbe();
        var p = new DisplayPusher(() => theme, NoSensors, 4, probe, Quick, pinnedPage: 2);
        probe.Owner = p;
        try
        {
            Assert.True(probe.Sent.Wait(5000));
            Assert.Equal(2, probe.FirstPage);
        }
        finally { p.Stop(false, TimeSpan.FromSeconds(5)); }
    }
}

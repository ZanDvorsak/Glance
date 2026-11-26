using System.Diagnostics;
using Glance.Core.Display;
using Glance.Core.Sensors;
using Glance.Core.Themes;
using static Glance.Tests.DisplayPusherTests;

namespace Glance.Tests;

/// <summary>
/// Tests for the findings of docs/reviews/review-display-loop.md. Each was run against the code before its fix
/// (it failed) and after (it passes); the outputs are recorded in that review's "Fixes after review" section.
/// </summary>
public class DisplayLoopReviewTests
{
    private static readonly ISensorSource NoSensors = new FakeSource(Array.Empty<SensorInfo>());

    /// <summary>Short timings so the tests do not wait out the real 1 s back-off.</summary>
    private static readonly PusherTiming Fast = new(FrameIntervalMs: 5, ErrorBackoffMs: 50, NoPagesReportMs: 100, InvalidOperationLimit: 10);

    // ---- Medium 2: a stalled display must not add a blank that cannot succeed --------------------------------------

    [Fact]
    public void Stop_skips_the_black_frame_when_the_display_has_just_failed()
    {
        var lcd = new FakeDisplay { BlitError = new IOException("USB write failed: timeout") };
        var p = new DisplayPusher(Ticking, NoSensors, 7, lcd, Fast);
        Assert.True(WaitUntil(() => p.LastError is not null), "the USB error never showed up");

        Assert.True(p.Stop(blank: true, TimeSpan.FromSeconds(2)));
        Assert.Equal(0, lcd.Blanks);     // a display that just timed out will not take a black frame either
        Assert.Equal(1, lcd.Disposes);
    }

    // ---- Low 3: the "Stop timed out" contract ------------------------------------------------------------------------

    [Fact]
    public void Stop_that_times_out_returns_false_and_the_loop_closes_the_display_after_its_transfer()
    {
        var lcd = new FakeDisplay { BlitDelayMs = 1500 };
        var p = new DisplayPusher(Ticking, NoSensors, 7, lcd, Fast);
        Assert.True(lcd.BlitStarted.Wait(5000), "the loop never started a transfer");

        var sw = Stopwatch.StartNew();
        Assert.False(p.Stop(blank: true, TimeSpan.FromMilliseconds(100)));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Stop took {sw.ElapsedMilliseconds} ms despite its 100 ms bound");
        Assert.Equal(0, lcd.Disposes);   // still in the transfer: not closed underneath it
        Assert.False(p.IsClosed);

        Assert.True(WaitUntil(() => lcd.Disposes == 1), "the loop never closed the display");
        Assert.True(p.IsClosed);
        Assert.Equal(1, lcd.Blanks);     // the slow transfer succeeded, so the requested blank still happens
        Assert.Equal(0, lcd.DisposedDuringTransfer);
        Assert.Equal(0, lcd.UsedAfterDispose);

        Assert.True(p.Stop(blank: true)); // a second stop does nothing more
        Assert.Equal(1, lcd.Blanks);
        Assert.Equal(1, lcd.Disposes);
    }

    // ---- Medium 3: render errors are not USB errors ------------------------------------------------------------------

    [Fact]
    public void A_render_error_is_not_reported_as_a_usb_error_and_never_asks_for_a_reopen()
    {
        var lcd = new FakeDisplay();
        var theme = Ticking();
        theme.Widgets.Add(new Widget { Kind = WidgetKind.Sensor, SensorId = "/amdcpu/0/load/0" });
        var p = new DisplayPusher(() => theme, new ThrowingSource(), 7, lcd, Fast);
        try
        {
            Thread.Sleep(500);   // many failed frames
            Assert.True(p.IsAlive);
            Assert.Null(p.LastError);
            Assert.Contains("sensor feed broke", p.LastRenderError);

            // the owners' rule, over a minute of failing frames: never reopen USB for a bad theme
            DateTime? since = null;
            var t0 = DateTime.UtcNow;
            for (int s = 0; s <= 60; s++)
                Assert.False(DisplayPusher.ShouldReopen(p.IsAlive, p.LastError, ref since, t0.AddSeconds(s), TimeSpan.FromSeconds(6)));
        }
        finally { p.Stop(blank: false); }
    }

    [Fact]
    public void ShouldReopen_reopens_a_dead_loop_at_once_and_a_failing_usb_link_after_the_grace_period()
    {
        DateTime? since = null;
        var t0 = new DateTime(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
        var grace = TimeSpan.FromSeconds(6);
        Assert.True(DisplayPusher.ShouldReopen(false, null, ref since, t0, grace));

        since = null;
        Assert.False(DisplayPusher.ShouldReopen(true, "USB write failed", ref since, t0, grace));
        Assert.False(DisplayPusher.ShouldReopen(true, "USB write failed", ref since, t0.AddSeconds(6), grace));
        Assert.True(DisplayPusher.ShouldReopen(true, "USB write failed", ref since, t0.AddSeconds(7), grace));

        since = t0;
        Assert.False(DisplayPusher.ShouldReopen(true, null, ref since, t0.AddSeconds(30), grace));   // healthy again
        Assert.Null(since);
    }

    // ---- Low 1: null pages ------------------------------------------------------------------------------------------

    [Fact]
    public void Loading_a_theme_drops_null_pages()
    {
        using var dir = new TempDir();
        var path = dir.Combine("theme.json");
        File.WriteAllText(path, """{ "Name": "t", "Pages": [ null, { "Name": "A" }, null, { "Name": "B" } ] }""");
        Assert.Equal(new[] { "A", "B" }, Theme.Load(path).Pages.Select(p => p.Name));

        File.WriteAllText(path, """{ "Name": "t", "Pages": [ null ] }""");
        var only = Assert.Single(Theme.Load(path).Pages);
        Assert.NotNull(only);

        var t = new Theme();
        t.ApplySnapshot("""{ "Name": "t", "Pages": [ null, { "Name": "C" } ] }""");
        Assert.Equal("C", Assert.Single(t.Pages).Name);
    }

    [Fact]
    public void A_theme_left_without_pages_is_reported_instead_of_freezing_silently()
    {
        var lcd = new FakeDisplay();
        var theme = new Theme();
        theme.Pages.Clear();
        var p = new DisplayPusher(() => theme, NoSensors, 7, lcd, Fast);
        try
        {
            Assert.True(WaitUntil(() => p.LastRenderError is not null, 3000), "an empty theme was never reported");
            Assert.Contains("no pages", p.LastRenderError);
            Assert.Null(p.LastError);   // nothing wrong with USB

            theme.Pages.Add(new ThemePage());
            Assert.True(WaitUntil(() => p.LastRenderError is null), "the report did not clear once the theme had a page");
        }
        finally { p.Stop(blank: false); }
    }

    // ---- Low 2: the undo/redo race, sampled fast enough to catch the old bug ----------------------------------------

    [Fact]
    public void Undo_redo_racing_a_fast_loop_never_faults_it()
    {
        // Empty pages and no wait between frames: the loop reads the page list as often as it can, so it lands in the
        // instant where the old ApplySnapshot had emptied it (the shipped 200 ms interval almost never did).
        var lcd = new FakeDisplay();
        var theme = new Theme();
        theme.Pages.Add(new ThemePage { Name = "Two", DurationSeconds = 1 });
        theme.Pages.Add(new ThemePage { Name = "Three", DurationSeconds = 1 });
        var three = theme.ToSnapshot();
        var one = new Theme { Name = "one" }.ToSnapshot();
        var p = new DisplayPusher(() => theme, NoSensors, 7, lcd, Fast with { FrameIntervalMs = 0 }) { PinnedPage = 2 };
        try
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; sw.ElapsedMilliseconds < 2000; i++) theme.ApplySnapshot(i % 2 == 0 ? one : three);
            Assert.True(p.IsAlive);
            Assert.Equal(0, p.UnexpectedErrors);
            Assert.Null(p.LastRenderError);
        }
        finally { p.Stop(blank: false); }
    }

    [Fact]
    public void StablePages_copies_the_list_and_drops_empty_slots()
    {
        var t = new Theme();
        t.Pages.Add(new ThemePage { Name = "B" });
        var copy = DisplayPusher.StablePages(t);
        t.Pages.RemoveAt(1);
        Assert.Equal(2, copy.Length);    // a copy: later changes do not reach it
        Assert.All(copy, Assert.NotNull);
    }

    // ---- Low 5: an InvalidOperationException on every frame ---------------------------------------------------------

    [Fact]
    public void An_InvalidOperationException_on_every_frame_is_reported_and_backed_off()
    {
        var lcd = new FakeDisplay();
        int calls = 0;
        var timing = Fast with { ErrorBackoffMs = 300 };
        var p = new DisplayPusher(() => { Interlocked.Increment(ref calls); throw new InvalidOperationException("Collection was modified"); }, NoSensors, 7, lcd, timing);
        try
        {
            Assert.True(WaitUntil(() => p.LastRenderError is not null, 3000), "a repeating InvalidOperationException was never reported");
            Assert.Contains("Collection was modified", p.LastRenderError);

            // backed off: about one attempt per 300 ms instead of one every 5 ms
            var before = Volatile.Read(ref calls);
            Thread.Sleep(900);
            var attempts = Volatile.Read(ref calls) - before;
            Assert.True(attempts <= 6, $"{attempts} attempts in 900 ms; the loop is not backing off");
        }
        finally { p.Stop(blank: false); }
    }
}

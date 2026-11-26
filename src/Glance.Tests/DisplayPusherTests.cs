using System.Diagnostics;
using Glance.Core.Display;
using Glance.Core.Sensors;
using Glance.Core.Themes;

namespace Glance.Tests;

/// <summary>A display that records what the pusher does to it. Transfers can be made slow to imitate a stalled USB link.</summary>
internal sealed class FakeDisplay : IFrameDisplay
{
    private readonly object _log = new();
    private volatile bool _inTransfer, _disposed;
    private int _blits, _blanks, _disposes, _disposedDuringTransfer, _usedAfterDispose;

    public int BlitDelayMs { get; set; }
    public Exception? BrightnessError { get; set; }
    /// <summary>Thrown by every Blit (after <see cref="BlitDelayMs"/>), like a display that stopped answering.</summary>
    public Exception? BlitError { get; set; }
    public bool SizeThrows { get; set; }
    public readonly ManualResetEventSlim BlitStarted = new();
    public readonly List<string> Calls = new();
    public int? BlankThread, DisposeThread;

    public int Width => SizeThrows ? throw new InvalidOperationException("no size") : 96;
    public int Height => 64;
    public int Blits => Volatile.Read(ref _blits);
    public int Blanks => Volatile.Read(ref _blanks);
    public int Disposes => Volatile.Read(ref _disposes);
    public int DisposedDuringTransfer => Volatile.Read(ref _disposedDuringTransfer);
    public int UsedAfterDispose => Volatile.Read(ref _usedAfterDispose);

    private void Log(string s) { lock (_log) Calls.Add(s); }

    public void Blit(int x, int y, int w, int h, byte[] rgb565Be)
    {
        if (_disposed) Interlocked.Increment(ref _usedAfterDispose);
        _inTransfer = true;
        BlitStarted.Set();
        if (BlitDelayMs > 0) Thread.Sleep(BlitDelayMs);
        if (BlitError is { } e) { _inTransfer = false; throw e; }
        Interlocked.Increment(ref _blits);
        _inTransfer = false;
        Log("blit");
    }

    public void SetBrightness(int level)
    {
        if (BrightnessError is { } e) throw e;
        if (_disposed) Interlocked.Increment(ref _usedAfterDispose);
        Log("brightness " + level);
    }

    public void Blank()
    {
        if (_disposed) Interlocked.Increment(ref _usedAfterDispose);
        BlankThread = Environment.CurrentManagedThreadId;
        Interlocked.Increment(ref _blanks);
        Log("blank");
    }

    public void Dispose()
    {
        if (_inTransfer) Interlocked.Increment(ref _disposedDuringTransfer);
        DisposeThread = Environment.CurrentManagedThreadId;
        _disposed = true;
        Interlocked.Increment(ref _disposes);
        Log("dispose");
    }
}

/// <summary>A sensor source whose snapshot can be made to throw, so the renderer fails from inside <c>Render</c>.</summary>
internal sealed class ThrowingSource : ISensorSource
{
    public volatile bool Throw = true;
    public IReadOnlyList<SensorInfo> Snapshot => Throw ? throw new NullReferenceException("sensor feed broke") : Fake.Typical();
    public void Update() { }
}

public class DisplayPusherTests
{
    private static readonly ISensorSource NoSensors = new FakeSource(Array.Empty<SensorInfo>());

    /// <summary>A short error back-off, so recovery tests do not wait out the real 1 s.</summary>
    private static readonly PusherTiming Quick = new(ErrorBackoffMs: 50);

    internal static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    /// <summary>A theme whose picture changes every frame, so the pusher keeps sending (identical frames are skipped).</summary>
    internal static Theme Ticking()
    {
        var t = new Theme();
        t.Widgets.Add(new Widget { Kind = WidgetKind.Clock, X = 0, Y = 0, Text = "HH:mm:ss.fff", FontSize = 40 });
        return t;
    }

    [Fact]
    public void An_exception_from_the_theme_is_reported_and_does_not_end_the_loop()
    {
        var lcd = new FakeDisplay();
        var theme = Ticking();
        var fail = true;
        var p = new DisplayPusher(() => Volatile.Read(ref fail) ? throw new ArgumentOutOfRangeException("index") : theme, NoSensors, 7, lcd, Quick);
        try
        {
            Assert.True(WaitUntil(() => p.LastRenderError is not null), "the error never showed up in LastRenderError");
            Assert.Contains("ArgumentOutOfRangeException", p.LastRenderError);
            Assert.Null(p.LastError);   // not a USB problem
            Assert.True(p.IsAlive);
            Assert.False(p.Faulted);

            Volatile.Write(ref fail, false);
            Assert.True(WaitUntil(() => p.LastRenderError is null && lcd.Blits > 0), "the loop did not recover");
            Assert.True(p.IsAlive);
        }
        finally { p.Stop(blank: false); }
    }

    [Fact]
    public void An_exception_from_the_renderer_is_reported_and_does_not_end_the_loop()
    {
        var lcd = new FakeDisplay();
        var theme = Ticking();
        theme.Widgets.Add(new Widget { Kind = WidgetKind.Sensor, SensorId = "/amdcpu/0/load/0" });
        var sensors = new ThrowingSource();
        var p = new DisplayPusher(() => theme, sensors, 7, lcd, Quick);
        try
        {
            Assert.True(WaitUntil(() => p.LastRenderError is not null), "the error never showed up in LastRenderError");
            Assert.Contains("sensor feed broke", p.LastRenderError);
            Assert.Null(p.LastError);
            Assert.True(p.IsAlive);

            sensors.Throw = false;
            Assert.True(WaitUntil(() => p.LastRenderError is null && lcd.Blits > 0), "the loop did not recover");
        }
        finally { p.Stop(blank: false); }
    }

    [Fact]
    public void A_theme_with_no_pages_for_a_moment_does_not_fault_the_loop()
    {
        var lcd = new FakeDisplay();
        var theme = Ticking();
        var page = theme.Pages[0];
        theme.Pages.Clear();   // what ApplySnapshot used to do for an instant
        var p = new DisplayPusher(() => theme, NoSensors, 7, lcd);
        try
        {
            Thread.Sleep(600);   // several frames with nothing to draw
            Assert.True(p.IsAlive);
            Assert.Null(p.LastError);
            Assert.Equal(0, p.UnexpectedErrors);
            Assert.Equal(0, lcd.Blits);   // keeps the last picture rather than drawing garbage

            theme.Pages.Add(page);
            Assert.True(WaitUntil(() => lcd.Blits > 0));
        }
        finally { p.Stop(blank: false); }
    }

    // The undo/redo race test lives in DisplayLoopReviewTests (Undo_redo_racing_a_fast_loop_never_faults_it): at the shipped
    // 200 ms frame interval it almost never caught the original bug, so it now runs the loop without a frame wait.

    [Fact]
    public void ApplySnapshot_never_leaves_the_theme_without_pages()
    {
        var theme = new Theme();
        theme.Pages.Add(new ThemePage { Name = "Two" });
        theme.Pages.Add(new ThemePage { Name = "Three" });
        var single = new Theme { Name = "single" };
        single.Pages[0].Name = "Only";
        int lowest = int.MaxValue;
        theme.Pages.CollectionChanged += (_, _) => lowest = Math.Min(lowest, theme.Pages.Count);

        theme.ApplySnapshot(single.ToSnapshot());
        Assert.True(lowest >= 1, "the page list was empty for a moment");
        Assert.Equal("Only", Assert.Single(theme.Pages).Name);

        var three = new Theme();
        three.Pages[0].Name = "A"; three.Pages.Add(new ThemePage { Name = "B" }); three.Pages.Add(new ThemePage { Name = "C" });
        theme.ApplySnapshot(three.ToSnapshot());
        Assert.True(lowest >= 1);
        Assert.Equal(new[] { "A", "B", "C" }, theme.Pages.Select(x => x.Name));
    }

    [Fact]
    public void Stop_waits_for_a_slow_transfer_instead_of_closing_the_display_under_it()
    {
        // one transfer longer than the old 2 s wait in Stop
        var lcd = new FakeDisplay { BlitDelayMs = 3000 };
        var p = new DisplayPusher(Ticking, NoSensors, 7, lcd);
        Assert.True(lcd.BlitStarted.Wait(5000), "the loop never started a transfer");

        var sw = Stopwatch.StartNew();
        Assert.True(p.Stop(blank: false));
        Assert.True(sw.ElapsedMilliseconds >= 2000, "Stop returned before the transfer could have finished");
        Assert.Equal(0, lcd.DisposedDuringTransfer);
        Assert.Equal(0, lcd.UsedAfterDispose);
        Assert.Equal(1, lcd.Disposes);
        Assert.Equal(0, lcd.Blanks);
        Assert.False(p.IsAlive);
    }

    [Fact]
    public void Stop_with_blank_blanks_exactly_once_on_the_loop_thread_before_closing()
    {
        var lcd = new FakeDisplay();
        var p = new DisplayPusher(Ticking, NoSensors, 7, lcd);
        Assert.True(WaitUntil(() => lcd.Blits > 0));

        var sw = Stopwatch.StartNew();
        Assert.True(p.Stop(blank: true));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"stopping took {sw.ElapsedMilliseconds} ms");
        p.Dispose();       // a second stop (blank again) must not touch the closed device
        p.Stop(blank: true);

        Assert.Equal(1, lcd.Blanks);
        Assert.Equal(1, lcd.Disposes);
        Assert.Equal(0, lcd.UsedAfterDispose);
        Assert.NotEqual(Environment.CurrentManagedThreadId, lcd.BlankThread);   // the loop did it, not the caller
        lock (lcd.Calls) Assert.Equal(new[] { "blank", "dispose" }, lcd.Calls.TakeLast(2));
    }

    [Fact]
    public void Dispose_blanks_and_closes_the_display()
    {
        var lcd = new FakeDisplay();
        var p = new DisplayPusher(Ticking, NoSensors, 7, lcd);
        Assert.True(WaitUntil(() => lcd.Blits > 0));
        p.Dispose();
        Assert.Equal(1, lcd.Blanks);
        Assert.Equal(1, lcd.Disposes);
    }

    [Fact]
    public void A_failing_first_brightness_command_closes_the_display()
    {
        var lcd = new FakeDisplay { BrightnessError = new IOException("USB write failed: timeout") };
        Assert.Throws<IOException>(() => new DisplayPusher(Ticking, NoSensors, 7, lcd));
        Assert.Equal(1, lcd.Disposes);
    }

    [Fact]
    public void A_loop_that_fails_to_start_reports_it_and_is_closed_by_Stop()
    {
        var lcd = new FakeDisplay { SizeThrows = true };
        var p = new DisplayPusher(Ticking, NoSensors, 7, lcd);
        Assert.True(WaitUntil(() => !p.IsAlive));
        Assert.True(p.Faulted);
        Assert.NotNull(p.LastError);
        Assert.Equal(0, lcd.Disposes);   // left for Stop, so it can still blank the screen

        Assert.True(p.Stop(blank: true));
        Assert.Equal(1, lcd.Blanks);
        Assert.Equal(1, lcd.Disposes);
    }

    [Fact]
    public void Page_rotation_over_a_page_list_clamps_a_stale_index()
    {
        long since = 0;
        var pages = new[] { new ThemePage(), new ThemePage() };
        Assert.Equal(0, DisplayPusher.NextPageIndex(pages, true, 5, ref since, 0, -1, 0));
        Assert.Equal(0, DisplayPusher.NextPageIndex(pages, true, -3, ref since, 0, -1, 0));
        Assert.Equal(1, DisplayPusher.NextPageIndex(pages, true, 0, ref since, 0, 7, 0));
    }
}

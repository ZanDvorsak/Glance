using System.Collections.Concurrent;
using System.Diagnostics;
using Glance.Core.Display;
using Glance.Core.Themes;
using Glance.Core.Threading;
using Xunit.Abstractions;

namespace Glance.Tests;

/// <summary>
/// A stand-in for the editor's UI thread: one thread with a synchronization context, so <c>await</c> comes back to it like
/// it does with Avalonia's dispatcher. <see cref="Blocked"/> measures how long a handler holds the thread before its first
/// real <c>await</c>, which is what the user feels as a frozen window.
/// </summary>
internal sealed class TestUiThread : IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
    private readonly Thread _thread;

    public TestUiThread()
    {
        _thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new Context(this));
            foreach (var (cb, state) in _queue.GetConsumingEnumerable()) cb(state);
        }) { IsBackground = true, Name = "test UI thread" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    private sealed class Context : SynchronizationContext
    {
        private readonly TestUiThread _ui;
        public Context(TestUiThread ui) => _ui = ui;
        public override void Post(SendOrPostCallback d, object? state) => _ui._queue.Add((d, state));
        public override SynchronizationContext CreateCopy() => this;
    }

    /// <summary>Runs a UI event handler on the UI thread; returns how long the UI thread was held and the handler's task.</summary>
    public (TimeSpan Blocked, Task Done) Blocked(Func<Task> handler)
    {
        var started = new TaskCompletionSource<(TimeSpan, Task)>();
        _queue.Add((_ =>
        {
            var sw = Stopwatch.StartNew();
            var t = handler();
            started.SetResult((sw.Elapsed, t));
        }, null));
        var (blocked, inner) = started.Task.GetAwaiter().GetResult();
        return (blocked, inner);
    }

    public void Dispose() => _queue.CompleteAdding();
}

/// <summary>
/// Review M7: the editor's blocking work goes through <see cref="SerialWork"/> and friends. Each test measures how long a
/// handler holds a (test) UI thread when the work behind it is slow, the same way the old code called it directly and the
/// way the fixed code hands it over.
/// </summary>
public class UiThreadTests
{
    private readonly ITestOutputHelper _out;
    public UiThreadTests(ITestOutputHelper output) => _out = output;

    private static readonly TimeSpan Responsive = TimeSpan.FromMilliseconds(150);

    // ---- SerialWork ----

    [Fact]
    public async Task SerialWork_never_runs_the_work_on_the_UI_thread()
    {
        using var ui = new TestUiThread();
        var work = new SerialWork();
        int workThread = 0, afterAwait = 0;
        var (blocked, done) = ui.Blocked(async () =>
        {
            await work.Run(() => { workThread = Environment.CurrentManagedThreadId; Thread.Sleep(1000); });
            afterAwait = Environment.CurrentManagedThreadId;
        });
        await done;
        _out.WriteLine($"UI thread held {blocked.TotalMilliseconds:0} ms for 1000 ms of work");
        Assert.True(blocked < Responsive, $"the UI thread was held {blocked.TotalMilliseconds:0} ms");
        Assert.NotEqual(ui.ThreadId, workThread);
        Assert.Equal(ui.ThreadId, afterAwait);   // UI updates after the await are back on the UI thread
    }

    [Fact]
    public async Task SerialWork_runs_items_one_at_a_time_in_order()
    {
        var work = new SerialWork();
        var log = new ConcurrentQueue<string>();
        int running = 0, overlap = 0;
        var tasks = Enumerable.Range(0, 20).Select(i => work.Run(() =>
        {
            if (Interlocked.Increment(ref running) > 1) Interlocked.Increment(ref overlap);
            Thread.Sleep(i % 3);
            log.Enqueue(i.ToString());
            Interlocked.Decrement(ref running);
        })).ToArray();
        Assert.True(work.Busy);
        await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => i.ToString()), log);
        Assert.Equal(0, overlap);
        Assert.False(work.Busy);
    }

    [Fact]
    public async Task SerialWork_a_failing_item_does_not_stop_the_next()
    {
        var work = new SerialWork();
        var bad = work.Run(new Action(() => throw new IOException("USB gone")));
        var good = work.Run(() => 42);
        await Assert.ThrowsAsync<IOException>(() => bad);
        Assert.Equal(42, await good);
    }

    // ---- the display: ToggleSend / TurnDisplayOff / StartSending / RestartDeadPusher ----

    [Fact]
    public async Task Releasing_a_stuck_display_freezes_a_direct_caller_but_not_through_SerialWork()
    {
        // A transfer that takes 1.5 s (a real stuck transfer can take 3 s each, and Stop waits up to 18 s).
        PusherSlot Stuck(out FakeOpener opener)
        {
            opener = new FakeOpener { Make = () => new FakeDisplay { BlitDelayMs = 1500 } };
            var slot = new PusherSlot(new DisplayPowerPolicy(), opener.Open);
            Assert.True(slot.TryOpen(out _));
            var d = opener;
            Assert.True(DisplayPusherTests.WaitUntil(() => { lock (d.Displays) return d.Displays.Count == 1 && d.Displays[0].BlitStarted.IsSet; }));
            return slot;
        }

        using var ui = new TestUiThread();

        // before: ToggleSend and TurnDisplayOff called _display.Release(...) on the UI thread
        var direct = Stuck(out _);
        var (before, _) = ui.Blocked(() => { direct.Release(blank: true, DisplayPusher.StopTimeout); return Task.CompletedTask; });

        // after: the release is a SerialWork item
        var slot = Stuck(out _);
        var work = new SerialWork();
        var (after, done) = ui.Blocked(async () => await work.Run(() => slot.Release(blank: true, DisplayPusher.StopTimeout)));
        await done;

        _out.WriteLine($"direct: UI held {before.TotalMilliseconds:0} ms; through SerialWork: {after.TotalMilliseconds:0} ms");
        Assert.True(before > TimeSpan.FromMilliseconds(800), "the fake display was not stuck, so the test proves nothing");
        Assert.True(after < Responsive, $"the UI thread was held {after.TotalMilliseconds:0} ms");
        Assert.Null(slot.Current);
    }

    [Fact]
    public async Task Opening_a_slow_display_freezes_a_direct_caller_but_not_through_SerialWork()
    {
        // Opening waits for the service's pipe (0.5 s connect, up to 10 s for its hello) or for USB.
        static PusherSlot Slow() => new(new DisplayPowerPolicy(), () => { Thread.Sleep(1000); return new FakeOpener().Open(); });

        using var ui = new TestUiThread();
        var direct = Slow();
        var (before, _) = ui.Blocked(() => { direct.TryOpen(out _); return Task.CompletedTask; });

        var slot = Slow();
        var work = new SerialWork();
        bool opened = false;
        var (after, done) = ui.Blocked(async () => opened = await work.Run(() => slot.TryOpen(out _)));
        await done;

        _out.WriteLine($"direct: UI held {before.TotalMilliseconds:0} ms; through SerialWork: {after.TotalMilliseconds:0} ms");
        Assert.True(before >= TimeSpan.FromMilliseconds(900));
        Assert.True(after < Responsive, $"the UI thread was held {after.TotalMilliseconds:0} ms");
        Assert.True(opened);
        direct.Release(blank: false, DisplayPusher.QuickStopTimeout);
        slot.Release(blank: false, DisplayPusher.QuickStopTimeout);
    }

    [Fact]
    public async Task Send_off_then_on_quickly_releases_before_it_opens_again()
    {
        // ToggleSend(false) then ToggleSend(true): the second open must wait for the first release, never overlap it.
        var opener = new FakeOpener { Make = () => new FakeDisplay { BlitDelayMs = 300 } };
        var slot = new PusherSlot(new DisplayPowerPolicy(), opener.Open);
        Assert.True(slot.TryOpen(out _));
        var first = slot.Current!;
        var work = new SerialWork();

        var release = work.Run(() => slot.Release(blank: true, DisplayPusher.StopTimeout));
        var open = work.Run(() => (slot.TryOpen(out var e), e));
        await release;
        Assert.True(first.IsClosed);   // the first display was closed before the second open ran
        var (ok, error) = await open;
        Assert.True(ok, error);
        Assert.Equal(2, opener.Opens);
        slot.Release(blank: false, DisplayPusher.QuickStopTimeout);
    }

    [Fact]
    public async Task Restarting_a_dead_display_through_SerialWork_keeps_the_UI_thread_free()
    {
        var (slot, opener, _) = FakeOpener.WithDeadPusher();
        opener.Make = () => { Thread.Sleep(800); return new FakeDisplay(); };   // reopening is slow
        using var ui = new TestUiThread();
        var work = new SerialWork();
        PusherSlot.RestartResult result = default;
        var (blocked, done) = ui.Blocked(async () => result = await work.Run(() => slot.RestartIfDead(out _, out _)));
        await done;
        Assert.True(blocked < Responsive, $"the UI thread was held {blocked.TotalMilliseconds:0} ms");
        Assert.Equal(PusherSlot.RestartResult.Reopened, result);
        slot.Release(blank: false, DisplayPusher.QuickStopTimeout);
    }

    // ---- BackgroundProbe: "is the tray app running?" every second in SlowTick ----

    [Fact]
    public async Task A_slow_probe_never_holds_the_UI_thread_and_runs_once_at_a_time()
    {
        int calls = 0;
        var probe = new BackgroundProbe<bool>(() => { Interlocked.Increment(ref calls); Thread.Sleep(500); return true; }, false);
        using var ui = new TestUiThread();
        var (blocked, _) = ui.Blocked(() =>
        {
            for (var i = 0; i < 5; i++) { probe.Poke(); _ = probe.Value; }   // five slow ticks in a row
            return Task.CompletedTask;
        });
        Assert.True(blocked < Responsive, $"the UI thread was held {blocked.TotalMilliseconds:0} ms");
        Assert.False(probe.Value);       // not known yet: the last value
        await probe.Last;
        Assert.True(probe.Value);
        Assert.Equal(1, calls);          // one check at a time, not one per tick
    }

    [Fact]
    public async Task A_fresh_report_wins_over_an_older_probe()
    {
        var release = new ManualResetEventSlim();
        var probe = new BackgroundProbe<bool>(() => { release.Wait(); return false; }, false);
        probe.Poke();                     // started before the tray app was launched: will say "not running"
        probe.Report(true);               // ToggleSend just checked: it runs
        release.Set();
        await probe.Last;
        Assert.True(probe.Value);
    }

    // ---- Save: Theme.Save copied pictures and videos on the UI thread ----

    [Fact]
    public async Task Saving_a_theme_with_a_big_video_freezes_a_direct_caller_but_not_through_ThemeSaveJob()
    {
        var root = Path.Combine(Path.GetTempPath(), "glance-ui-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            var video = Path.Combine(root, "source", "big.mp4");
            Directory.CreateDirectory(Path.GetDirectoryName(video)!);
            using (var f = File.Create(video)) { f.SetLength(300L * 1024 * 1024); f.WriteByte(1); }   // 300 MB

            Theme Make() { var t = Theme.CreateDefault(null); t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = video }); return t; }
            using var ui = new TestUiThread();

            var direct = Make();
            var (before, _) = ui.Blocked(() => { direct.Save(Path.Combine(root, "a", "theme.json")); return Task.CompletedTask; });

            var live = Make();
            ThemeSaveJob? job = null;
            var (after, done) = ui.Blocked(async () => job = await ThemeSaveJob.SaveAsync(live, Path.Combine(root, "b", "theme.json")));
            await done;

            _out.WriteLine($"direct save: UI held {before.TotalMilliseconds:0} ms; ThemeSaveJob: {after.TotalMilliseconds:0} ms");
            // copying 300 MB takes ~0.1-1 s depending on the disk; the point is that it lands on the UI thread
            Assert.True(before > TimeSpan.FromMilliseconds(50), $"direct {before.TotalMilliseconds:0} ms was not slow; the test proves nothing");
            // relative as well as absolute: on a fast disk the whole copy can fit in the 150 ms bound, so also require a small fraction of it
            Assert.True(after < Responsive && after * 4 < before, $"the UI thread was held {after.TotalMilliseconds:0} ms (direct: {before.TotalMilliseconds:0} ms)");
            // same result as the direct save: the video was adopted into the new folder
            Assert.Equal("assets/big.mp4", live.Widgets.Last().ImagePath);
            Assert.Equal(Path.Combine(root, "b"), live.BaseFolder);
            Assert.True(File.Exists(Path.Combine(root, "b", "assets", "big.mp4")));
            Assert.False(job!.ChangedWhileSaving);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void A_save_gives_the_live_theme_the_same_paths_as_Theme_Save()
    {
        var root = Path.Combine(Path.GetTempPath(), "glance-ui-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pic = Path.Combine(root, "src", "p.png"); var font = Path.Combine(root, "src", "f.ttf");
            Directory.CreateDirectory(Path.GetDirectoryName(pic)!);
            File.WriteAllText(pic, "png"); File.WriteAllText(font, "ttf");
            var original = Theme.CreateDefault(null);
            original.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = pic });
            original.Widgets.Add(new Widget { Kind = WidgetKind.Text, FontFile = font });
            var json = original.ToSnapshot();
            Theme Make() { var t = new Theme(); t.ApplySnapshot(json); return t; }   // two identical themes (same ids)
            var a = Make(); var movedA = a.Save(Path.Combine(root, "a", "theme.json"));
            var b = Make(); var job = ThemeSaveJob.Begin(b, Path.Combine(root, "b", "theme.json"));
            job.Run(); var movedB = job.Apply();

            Assert.Equal(movedA, movedB);
            Assert.Equal(a.ToSnapshot(), b.ToSnapshot());
            Assert.Equal(File.ReadAllText(Path.Combine(root, "a", "theme.json")), File.ReadAllText(Path.Combine(root, "b", "theme.json")));
            Assert.Equal(job.SavedSnapshot, b.ToSnapshot());
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void An_edit_made_while_saving_is_kept_and_reported()
    {
        var root = Path.Combine(Path.GetTempPath(), "glance-ui-save-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pic = Path.Combine(root, "src", "p.png"); var other = Path.Combine(root, "src", "q.png");
            Directory.CreateDirectory(Path.GetDirectoryName(pic)!);
            File.WriteAllText(pic, "p"); File.WriteAllText(other, "q");
            var live = Theme.CreateDefault(null);
            var kept = new Widget { Kind = WidgetKind.Image, ImagePath = pic };
            var changed = new Widget { Kind = WidgetKind.Image, ImagePath = pic, Name = "changed" };
            live.Widgets.Add(kept); live.Widgets.Add(changed);

            var job = ThemeSaveJob.Begin(live, Path.Combine(root, "t", "theme.json"));
            changed.ImagePath = other;     // the user picks another picture while the files are copied
            changed.X = 77;
            job.Run();
            job.Apply();

            Assert.Equal("assets/p.png", kept.ImagePath);   // untouched widget: the saved path
            Assert.Equal(other, changed.ImagePath);         // the user's choice is not overwritten
            Assert.Equal(77.0, changed.X);
            Assert.True(job.ChangedWhileSaving);
            Assert.DoesNotContain("q.png", File.ReadAllText(Path.Combine(root, "t", "theme.json")));   // the file has the state at Begin
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}

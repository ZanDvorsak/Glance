using System.Diagnostics;
using Glance.Core.Media;
using Glance.Core.Themes;
using SkiaSharp;

namespace Glance.Tests;

/// <summary>A test that needs a real FFmpeg; reported as skipped on a PC (or CI runner) without one.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (Ffmpeg.Find() is null) Skip = "FFmpeg is not installed on this PC";
    }
}

/// <summary>Follow-ups from docs/reviews/review-video-frames.md (L1 to L3 and the nits; L4 is in VideoFrameTests).</summary>
public class VideoReviewFixTests
{
    private static void WaitFor(Func<bool> condition, string what, int ms = 5000) =>
        Assert.True(SpinWait.SpinUntil(condition, ms), "timed out waiting for: " + what);

    /// <summary>A renderer whose video decoders are fakes, with its clock in the test's hands.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly List<FakeVideoProcess> Processes = new();
        public readonly List<VideoSource> Sources = new();
        public readonly ThemeRenderer Renderer;
        public readonly Theme Theme = Fake.Canvas(200, 200);
        public readonly Widget Widget;
        public readonly string Path = $@"C:\nowhere\clip-{Guid.NewGuid():N}.mp4";
        public Action<FakeVideoProcess>? OnLaunch;
        public long Now;

        public Rig()
        {
            Widget = new Widget { Kind = WidgetKind.Image, X = 0, Y = 0, Width = 100, Height = 100, ImagePath = Path };
            Theme.Widgets.Add(Widget);
            Renderer = new ThemeRenderer
            {
                Now = () => Now,
                VideoFactory = (p, w, h, fit) =>
                {
                    var v = new VideoSource(p, w, h, fit, 15, () => "ffmpeg.exe", () => null, Launch);
                    lock (Sources) Sources.Add(v);
                    return v;
                },
            };
        }

        private IVideoProcess Launch(string exe, IReadOnlyList<string> args, Action<string> onErrorLine)
        {
            var p = new FakeVideoProcess(args, onErrorLine);
            lock (Processes) Processes.Add(p);
            OnLaunch?.Invoke(p);
            return p;
        }

        public int Launches { get { lock (Processes) return Processes.Count; } }
        public FakeVideoProcess Process(int i) { lock (Processes) return Processes[i]; }
        public VideoSource Source(int i) { lock (Sources) return Sources[i]; }

        public SKColor Render(int x = 50, int y = 50)
        {
            using var bmp = Fake.Render(Theme, null, renderer: Renderer);
            return bmp.GetPixel(x, y);
        }

        public void Dispose() => Renderer.Dispose();
    }

    // ---- L1: a slow renderer still gets the sharp decoder after a resize ------------------------------------------

    [Fact]
    public void L1_a_renderer_drawing_every_1_2_seconds_still_restarts_the_decoder_at_the_new_size()
    {
        using var rig = new Rig();
        rig.Render();
        WaitFor(() => rig.Launches == 1, "ffmpeg started");
        rig.Process(0).Push(SKColors.Red);
        WaitFor(() => rig.Render() == SKColors.Red, "first frame drawn");

        rig.Widget.Width = 150;
        for (int i = 0; i < 4; i++) { rig.Now += 1200; rig.Render(); }   // the tray loop in USB back-off draws about once a second
        WaitFor(() => rig.Launches == 2, "a decoder at the new size");
        Assert.Equal(150, rig.Process(1).Width);
    }

    [Fact]
    public void L1_a_drag_still_starts_no_decoder_while_the_size_keeps_changing()
    {
        using var rig = new Rig();
        rig.Render();
        WaitFor(() => rig.Launches == 1, "ffmpeg started");
        for (int i = 1; i <= 30; i++) { rig.Widget.Width = 100 + i; rig.Now += 300; rig.Render(); }   // a slow drag: a new size every 300 ms
        Thread.Sleep(300);
        Assert.Equal(1, rig.Launches);
    }

    // ---- L2: Dispose does not wait for ffmpeg to die --------------------------------------------------------------

    [Fact]
    public void L2_disposing_a_source_returns_at_once_and_kills_ffmpeg_on_another_thread()
    {
        var processes = new List<FakeVideoProcess>();
        var v = new VideoSource("clip.mp4", 8, 8, ImageFit.Stretch, 15, () => "ffmpeg.exe", () => null, (_, a, e) =>
        {
            var p = new FakeVideoProcess(a, e) { KillDelayMs = 1500 };   // Kill(true) walks every process on the PC
            lock (processes) processes.Add(p);
            return p;
        });
        WaitFor(() => { lock (processes) return processes.Count == 1; }, "ffmpeg started");
        var sw = Stopwatch.StartNew();
        v.Dispose();
        Assert.True(sw.ElapsedMilliseconds < 500, $"Dispose took {sw.ElapsedMilliseconds} ms");
        WaitFor(() => processes[0].Killed && processes[0].Disposed, "ffmpeg killed and disposed");
        Assert.NotEqual(Environment.CurrentManagedThreadId, processes[0].KillThreadId);
    }

    // ---- L3: a file that keeps failing backs off, and is logged once ----------------------------------------------

    /// <summary>Asks for the frame at <c>Now</c>; returns how many decoders were started by then.</summary>
    private static int RenderAndCount(Rig rig, int expectedLaunches)
    {
        rig.Render();
        if (expectedLaunches > 0) SpinWait.SpinUntil(() => rig.Launches >= expectedLaunches, 3000);
        Thread.Sleep(50);
        return rig.Launches;
    }

    private static void WaitStopped(Rig rig, int i) => WaitFor(() => rig.Sources.Count > i && rig.Source(i).Stopped, $"decoder {i + 1} stopped");

    [Fact]
    public void L3_a_video_that_keeps_failing_waits_twice_as_long_each_time_up_to_a_minute()
    {
        using var rig = new Rig { OnLaunch = p => { p.WriteError("boom"); p.Exit(1); } };
        Assert.Equal(1, RenderAndCount(rig, 1));
        WaitStopped(rig, 0);

        long[] waits = { 5_000, 10_000, 20_000, 40_000, 60_000, 60_000 };
        for (int i = 0; i < waits.Length; i++)
        {
            rig.Now += waits[i] - 1;
            Assert.Equal(i + 1, RenderAndCount(rig, 0));   // not yet
            rig.Now += 1;
            Assert.Equal(i + 2, RenderAndCount(rig, i + 2));
            WaitStopped(rig, i + 1);
        }
    }

    [Fact]
    public void L3_the_wait_starts_again_after_a_run_that_played()
    {
        using var rig = new Rig { OnLaunch = p => { p.WriteError("boom"); p.Exit(1); } };
        RenderAndCount(rig, 1);
        WaitStopped(rig, 0);
        rig.Now += 5_000;
        Assert.Equal(2, RenderAndCount(rig, 2));
        WaitStopped(rig, 1);

        rig.OnLaunch = null;   // the third run plays a frame, then stops
        rig.Now += 10_000;
        Assert.Equal(3, RenderAndCount(rig, 3));
        rig.Process(2).Push(SKColors.Red);
        WaitFor(() => rig.Source(2).FramesRead == 1, "a frame played");
        rig.Process(2).Exit(1);
        WaitStopped(rig, 2);

        rig.OnLaunch = p => { p.WriteError("boom"); p.Exit(1); };
        rig.Now += 5_000;   // back to the first wait, not 20 s
        Assert.Equal(4, RenderAndCount(rig, 4));
    }

    [Fact]
    public void L3_a_failing_video_is_logged_once_per_error()
    {
        using var rig = new Rig { OnLaunch = p => { p.WriteError("boom"); p.Exit(1); } };
        var logged = new List<string>();
        void Handler(string m) { if (m.Contains(rig.Path)) lock (logged) logged.Add(m); }
        ThemeRenderer.VideoProblem += Handler;
        try
        {
            RenderAndCount(rig, 1);
            for (int i = 0; i < 4; i++)
            {
                WaitStopped(rig, i);
                rig.Render();               // notices the stop
                rig.Now += 60_000;
                RenderAndCount(rig, i + 2);
            }
            WaitStopped(rig, 4);
            rig.Render();
            lock (logged)
            {
                Assert.Single(logged);
                Assert.Contains("boom", logged[0]);
            }
        }
        finally { ThemeRenderer.VideoProblem -= Handler; }
    }

    // ---- nits -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Nit_unused_videos_are_stopped_after_3_s_of_the_renderers_own_clock()
    {
        using var rig = new Rig();
        rig.Render();
        WaitFor(() => rig.Launches == 1, "ffmpeg started");
        rig.Widget.Visible = false;
        rig.Now += 3_100;
        rig.Render();
        WaitFor(() => rig.Process(0).Killed, "the hidden video's ffmpeg stopped", 2000);
    }

    [Fact]
    public void Nit_while_resizing_the_widgets_own_old_decoder_is_shown_rather_than_another_widgets()
    {
        using var rig = new Rig();
        // another widget shows the same file with Cover; it is added first, so it is the older decoder
        rig.Theme.Widgets.Insert(0, new Widget { Kind = WidgetKind.Image, X = 150, Y = 150, Width = 40, Height = 40, Fit = ImageFit.Cover, ImagePath = rig.Path });
        rig.Render();
        WaitFor(() => rig.Launches == 1, "first decoder");
        for (int i = 0; i < 3; i++) { rig.Now += 300; rig.Render(); }
        WaitFor(() => rig.Launches == 2, "second decoder");
        var stretch = rig.Process(0).Width == 100 ? rig.Process(0) : rig.Process(1);
        var cover = stretch == rig.Process(0) ? rig.Process(1) : rig.Process(0);
        cover.Push(SKColors.Red);
        stretch.Push(SKColors.Blue);
        WaitFor(() => rig.Render() == SKColors.Blue && rig.Render(160, 160) == SKColors.Red, "both drawn");

        rig.Widget.Width = 120;   // resized: until it settles, show its own Stretch decoder, not the Cover one
        Assert.Equal(SKColors.Blue, rig.Render(110, 50));
    }
}

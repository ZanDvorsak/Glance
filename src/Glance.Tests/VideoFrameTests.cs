using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Glance.Core.Media;
using Glance.Core.Themes;
using SkiaSharp;

namespace Glance.Tests;

/// <summary>
/// A stand-in for the ffmpeg child process: the test pushes raw BGRA frames and decides when it exits.
/// The frame size is read from the "scale=W:H" filter the source asks for.
/// </summary>
internal sealed class FakeVideoProcess : IVideoProcess
{
    private readonly FrameFeed _feed = new();
    private readonly Action<string> _onErrorLine;

    public FakeVideoProcess(IReadOnlyList<string> args, Action<string> onErrorLine)
    {
        Args = args;
        _onErrorLine = onErrorLine;
        var m = Regex.Match(args[args.ToList().IndexOf("-vf") + 1], @"scale=(\d+):(\d+)");
        (Width, Height) = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        ThreadId = Environment.CurrentManagedThreadId;
    }

    public IReadOnlyList<string> Args { get; }
    public int Width { get; }
    public int Height { get; }
    public int ThreadId { get; }
    public volatile bool Killed;
    public volatile bool Disposed;
    private volatile bool _exited;

    public Stream Output => _feed;
    public int? ExitCode { get; private set; }

    public void Push(SKColor c)
    {
        var frame = new byte[Width * Height * 4];
        for (int i = 0; i < frame.Length; i += 4) { frame[i] = c.Blue; frame[i + 1] = c.Green; frame[i + 2] = c.Red; frame[i + 3] = c.Alpha; }
        _feed.Add(frame);
    }

    public void WriteError(string line) => _onErrorLine(line);

    public void Exit(int code) { ExitCode = code; _exited = true; _feed.End(); }

    public bool WaitForExit(int ms) => _exited;
    public void Kill() { Killed = true; Exit(-1); }
    public void Dispose() => Disposed = true;

    /// <summary>A read-only stream fed by the test; Read blocks until data comes or the feed ends.</summary>
    private sealed class FrameFeed : Stream
    {
        private readonly BlockingCollection<byte[]> _chunks = new();
        private byte[] _current = Array.Empty<byte>();
        private int _pos;

        public void Add(byte[] chunk) { try { _chunks.Add(chunk); } catch (InvalidOperationException) { } }
        public void End() { try { _chunks.CompleteAdding(); } catch (ObjectDisposedException) { } }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos == _current.Length)
            {
                if (!_chunks.TryTake(out var next, Timeout.Infinite)) return 0;
                (_current, _pos) = (next, 0);
            }
            int n = Math.Min(count, _current.Length - _pos);
            Buffer.BlockCopy(_current, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Review M9 and its nits: video frame lifetime, ffmpeg restarts while resizing, silent ffmpeg exits.</summary>
public class VideoFrameTests
{
    private static void WaitFor(Func<bool> condition, string what, int ms = 5000) =>
        Assert.True(SpinWait.SpinUntil(condition, ms), "timed out waiting for: " + what);

    private sealed class Launcher
    {
        public readonly List<FakeVideoProcess> Started = new();
        public int Count { get { lock (Started) return Started.Count; } }
        public FakeVideoProcess this[int i] { get { lock (Started) return Started[i]; } }

        public IVideoProcess Launch(string exe, IReadOnlyList<string> args, Action<string> onErrorLine)
        {
            var p = new FakeVideoProcess(args, onErrorLine);
            lock (Started) Started.Add(p);
            return p;
        }
    }

    private static VideoSource Source(Launcher l, int w = 8, int h = 8) =>
        new("clip.mp4", w, h, ImageFit.Stretch, 15, () => "ffmpeg.exe", () => null, l.Launch);

    private static SKImage FrameOf(VideoSource v)
    {
        using var lease = v.AcquireFrame();
        return lease.Image!;
    }

    // ---- M9a: frames are disposed --------------------------------------------------------------------------------

    [Fact]
    public void M9_a_replaced_frame_is_disposed_and_the_last_one_is_disposed_with_the_source()
    {
        var l = new Launcher();
        var v = Source(l);
        WaitFor(() => l.Count == 1, "ffmpeg started");

        l[0].Push(SKColors.Red);
        WaitFor(() => v.FramesRead == 1, "frame 1");
        var first = FrameOf(v);

        l[0].Push(SKColors.Green);
        WaitFor(() => v.FramesRead == 2, "frame 2");
        Assert.True(first.Handle == IntPtr.Zero, "frame 1 was replaced but not disposed");

        var second = FrameOf(v);
        v.Dispose();
        Assert.True(second.Handle == IntPtr.Zero, "the last frame was not disposed with the source");
    }

    [Fact]
    public void M9_a_frame_being_drawn_stays_valid_until_the_drawer_lets_go()
    {
        var l = new Launcher();
        using var v = Source(l);
        WaitFor(() => l.Count == 1, "ffmpeg started");
        l[0].Push(SKColors.Red);
        WaitFor(() => v.FramesRead == 1, "frame 1");

        var lease = v.AcquireFrame();
        var held = lease.Image!;
        l[0].Push(SKColors.Green);
        WaitFor(() => v.FramesRead == 2, "frame 2");
        Assert.True(held.Handle != IntPtr.Zero, "a frame still being drawn was disposed");
        using (var bmp = new SKBitmap(8, 8))
        using (var c = new SKCanvas(bmp))
        {
            c.DrawImage(held, 0, 0);
            Assert.Equal(SKColors.Red, bmp.GetPixel(4, 4));
        }
        lease.Dispose();
        Assert.True(held.Handle == IntPtr.Zero, "the replaced frame was not disposed once the drawer let go");
        lease.Dispose();   // a second dispose is harmless
    }

    [Fact]
    public void M9_frames_decoded_while_another_thread_draws_are_all_disposed()
    {
        var l = new Launcher();
        var v = Source(l, 16, 16);
        WaitFor(() => l.Count == 1, "ffmpeg started");
        const int frames = 300;
        var seen = new HashSet<SKImage>();
        var producer = Task.Run(() => { for (int i = 0; i < frames; i++) l[0].Push(i % 2 == 0 ? SKColors.Red : SKColors.Blue); });
        using (var bmp = new SKBitmap(16, 16))
        using (var c = new SKCanvas(bmp))
            while (v.FramesRead < frames)
            {
                using var lease = v.AcquireFrame();
                if (lease.Image is { } img) { seen.Add(img); c.DrawImage(img, 0, 0); }
            }
        producer.Wait();
        v.Dispose();
        Assert.NotEmpty(seen);
        Assert.All(seen, img => Assert.True(img.Handle == IntPtr.Zero, "a frame was never disposed"));
    }

    [Fact]
    public void M9_disposing_a_source_stops_ffmpeg()
    {
        var l = new Launcher();
        var v = Source(l);
        WaitFor(() => l.Count == 1, "ffmpeg started");
        v.Dispose();
        Assert.True(l[0].Killed);
        Assert.True(l[0].Disposed);
    }

    [Fact]
    public void M9_a_source_disposed_while_ffmpeg_is_still_starting_stops_it_once_it_has_started()
    {
        using var gate = new ManualResetEventSlim();
        var l = new Launcher();
        var v = new VideoSource("clip.mp4", 8, 8, ImageFit.Stretch, 15, () => "ffmpeg.exe", () => null,
            (exe, args, err) => { gate.Wait(3000); return l.Launch(exe, args, err); });
        v.Dispose();
        gate.Set();
        WaitFor(() => l.Count == 1 && l[0].Killed && l[0].Disposed, "the late process killed");
    }

    // ---- M9b: resizing does not start an ffmpeg per size, nor on the rendering thread --------------------------

    private static (ThemeRenderer r, Launcher l, Widget w, Theme t, Func<long> now, Action<long> advance) VideoRenderer()
    {
        var l = new Launcher();
        long now = 0;
        var r = new ThemeRenderer
        {
            Now = () => now,
            VideoFactory = (p, w, h, fit) => new VideoSource(p, w, h, fit, 15, () => "ffmpeg.exe", () => null, l.Launch),
        };
        var t = Fake.Canvas(200, 200);
        var w = new Widget { Kind = WidgetKind.Image, X = 0, Y = 0, Width = 100, Height = 100, ImagePath = @"C:\nowhere\clip.mp4" };
        t.Widgets.Add(w);
        return (r, l, w, t, () => now, ms => now += ms);
    }

    [Fact]
    public void M9_ffmpeg_is_never_started_on_the_rendering_thread()
    {
        var (r, l, _, t, _, _) = VideoRenderer();
        using (r)
        {
            Fake.Render(t, null, renderer: r).Dispose();
            WaitFor(() => l.Count == 1, "ffmpeg started");
            Assert.NotEqual(Environment.CurrentManagedThreadId, l[0].ThreadId);
        }
    }

    [Fact]
    public void M9_dragging_the_resize_handle_keeps_one_ffmpeg_and_scales_its_frames()
    {
        var (r, l, w, t, _, advance) = VideoRenderer();
        using (r)
        {
            Fake.Render(t, null, renderer: r).Dispose();
            WaitFor(() => l.Count == 1, "ffmpeg started");
            l[0].Push(SKColors.Red);
            WaitFor(() => FramesShown(r, t, 50, 50, SKColors.Red), "the first frame drawn");

            for (int i = 1; i <= 50; i++)   // a drag: one pixel wider per mouse move, about 60 moves a second
            {
                w.Width = 100 + i;
                advance(16);
                Fake.Render(t, null, renderer: r).Dispose();
            }
            Thread.Sleep(300);   // let any start that was queued happen
            Assert.Equal(1, l.Count);
            using (var bmp = Fake.Render(t, null, renderer: r))
                Assert.Equal(SKColors.Red, bmp.GetPixel(140, 50));   // the old frames, scaled to the new box

            advance(600);   // the size has been stable for a while: now the decoder restarts at the new size
            Fake.Render(t, null, renderer: r).Dispose();
            WaitFor(() => l.Count == 2, "ffmpeg restarted at the new size");
            Assert.Equal((150, 100), (l[1].Width, l[1].Height));
            Assert.NotEqual(Environment.CurrentManagedThreadId, l[1].ThreadId);

            Assert.Equal(SKColors.Red, RenderPixel(r, t, 140, 50));   // the old frames stay until the new decoder has one
            l[1].Push(SKColors.Blue);
            WaitFor(() => FramesShown(r, t, 140, 50, SKColors.Blue), "the new decoder's frame drawn");
        }
    }

    [Fact]
    public void M9_two_widgets_showing_one_video_at_different_sizes_each_get_their_own_decoder()
    {
        var (r, l, _, t, _, advance) = VideoRenderer();
        using (r)
        {
            t.Widgets.Add(new Widget { Kind = WidgetKind.Image, X = 100, Y = 100, Width = 50, Height = 50, ImagePath = @"C:\nowhere\clip.mp4" });
            for (int i = 0; i < 4; i++) { Fake.Render(t, null, renderer: r).Dispose(); advance(200); }
            WaitFor(() => l.Count == 2, "a decoder per size");
            Thread.Sleep(200);
            Assert.Equal(2, l.Count);
        }
    }

    private static SKColor RenderPixel(ThemeRenderer r, Theme t, int x, int y)
    {
        using var bmp = Fake.Render(t, null, renderer: r);
        return bmp.GetPixel(x, y);
    }

    private static bool FramesShown(ThemeRenderer r, Theme t, int x, int y, SKColor c) => RenderPixel(r, t, x, y) == c;

    // ---- nit: ffmpeg that exits without a word --------------------------------------------------------------------

    [Fact]
    public void M9_ffmpeg_exiting_without_a_message_shows_an_error()
    {
        var l = new Launcher();
        using var v = Source(l);
        WaitFor(() => l.Count == 1, "ffmpeg started");
        l[0].Exit(1);
        WaitFor(() => v.Error is not null, "an error after a silent exit", 3000);
        Assert.Contains("exit code 1", v.Error);
    }

    [Fact]
    public void M9_ffmpeg_exiting_with_a_message_shows_that_message()
    {
        var l = new Launcher();
        using var v = Source(l);
        WaitFor(() => l.Count == 1, "ffmpeg started");
        l[0].WriteError("clip.mp4: Invalid data found when processing input");
        l[0].Exit(1);
        WaitFor(() => v.Error is not null, "an error");
        Assert.Equal("clip.mp4: Invalid data found when processing input", v.Error);
    }

    [Fact]
    public void M9_a_video_whose_ffmpeg_stopped_is_started_again_later()
    {
        var (r, l, _, t, _, advance) = VideoRenderer();
        using (r)
        {
            Fake.Render(t, null, renderer: r).Dispose();
            WaitFor(() => l.Count == 1, "ffmpeg started");
            l[0].Exit(1);
            Thread.Sleep(300);
            advance(1000);
            Fake.Render(t, null, renderer: r).Dispose();
            Thread.Sleep(200);
            Assert.Equal(1, l.Count);   // not every frame

            advance(ThemeRenderer.VideoRetryMs);
            Fake.Render(t, null, renderer: r).Dispose();
            WaitFor(() => l.Count == 2, "ffmpeg started again", 2000);
        }
    }
}

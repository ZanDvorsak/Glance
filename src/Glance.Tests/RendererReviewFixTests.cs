using System.Diagnostics;
using Glance.Core.Media;
using Glance.Core.Sensors;
using Glance.Core.Themes;
using SkiaSharp;

namespace Glance.Tests;

/// <summary>
/// Findings from the independent review of the M10/M11 fix (`docs/reviews/review-renderer-memory.md`):
/// M1 budget fairness, M2 display-size decodes, L1-L5 and the nits.
/// </summary>
public sealed class RendererReviewFixTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"glance_rev_{Guid.NewGuid():N}");

    public RendererReviewFixTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Png(string name, int w, int h, SKColor color)
    {
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(color);
        return Save(name, bmp);
    }

    private string Save(string name, SKBitmap bmp)
    {
        var path = Path.Combine(_dir, name);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private string Gif(string name, int frames)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, GifBytes(frames));
        return path;
    }

    /// <summary>A 1x1 GIF of <paramref name="frames"/> frames, alternating red and green, 100 ms each, looping.</summary>
    internal static byte[] GifBytes(int frames)
    {
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange(new byte[] { 1, 0, 1, 0, 0x80, 0, 0 });
        gif.AddRange(new byte[] { 255, 0, 0, 0, 255, 0 });
        gif.AddRange(new byte[] { 0x21, 0xFF, 0x0B });
        gif.AddRange("NETSCAPE2.0"u8.ToArray());
        gif.AddRange(new byte[] { 3, 1, 0, 0, 0 });
        for (int i = 0; i < frames; i++)
        {
            gif.AddRange(new byte[] { 0x21, 0xF9, 4, 0, 10, 0, 0, 0 });
            gif.AddRange(new byte[] { 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0 });
            gif.AddRange(new byte[] { 2, 2, (byte)(i % 2 == 0 ? 0x44 : 0x4C), 0x01, 0 });
        }
        gif.Add(0x3B);
        return gif.ToArray();
    }

    private static void Touch(string path, int seconds) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(seconds));

    private static Widget Picture(string path, double x, double y, double w, double h, ImageFit fit = ImageFit.Stretch) =>
        new() { Kind = WidgetKind.Image, X = x, Y = y, Width = w, Height = h, ImagePath = path, Fit = fit };

    /// <summary>One Render call onto a bitmap the size of the theme.</summary>
    private static SKBitmap RenderOnce(Theme t, ThemeRenderer r, ISensorSource? sensors = null)
    {
        var bmp = new SKBitmap(new SKImageInfo(t.Width, t.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        r.Render(canvas, t, sensors);
        return bmp;
    }

    /// <summary>Renders the way <c>DisplayPusher</c> does: the theme scaled to fit a panel of the given size.</summary>
    private static SKBitmap RenderOnPanel(Theme t, ThemeRenderer r, int width, int height)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);
        float s = Math.Min((float)width / t.Width, (float)height / t.Height);
        canvas.Save();
        canvas.Translate((width - t.Width * s) / 2, (height - t.Height * s) / 2);
        canvas.Scale(s, s);
        canvas.ClipRect(new SKRect(0, 0, t.Width, t.Height));
        r.Render(canvas, t, null);
        canvas.Restore();
        return bmp;
    }

    // ---- M1: fair budget, no frozen or shortened animations -----------------------------------------------------

    [Fact]
    public void M1_two_animations_on_one_page_both_keep_animating()
    {
        var a = Gif("a.gif", 40); var b = Gif("b.gif", 40);
        var t = Fake.Canvas(100, 100);
        t.Widgets.Add(Picture(a, 0, 0, 20, 20));
        t.Widgets.Add(Picture(b, 30, 0, 20, 20));
        using var r = new ThemeRenderer { ImageBudgetBytes = 40 * 4 };   // room for 40 of the 80 frames (1x1 = 4 bytes each)
        using (Fake.Render(t, null, renderer: r)) { }
        int fa = r.CachedImage(a)!.Frames.Length, fb = r.CachedImage(b)!.Frames.Length;
        Assert.True(fa >= 10 && fb >= 10, $"a frames {fa}, b frames {fb}");
        Assert.True(r.CachedImageBytes <= r.ImageBudgetBytes, $"{r.CachedImageBytes} bytes for a {r.ImageBudgetBytes} byte budget");
    }

    [Fact]
    public void M1_a_shortened_animation_keeps_its_full_loop_time()
    {
        var a = Gif("a.gif", 40);
        var t = Fake.Canvas(100, 100);
        t.Widgets.Add(Picture(a, 0, 0, 20, 20));
        using var r = new ThemeRenderer { ImageBudgetBytes = 20 * 4 };   // half the frames fit
        using (Fake.Render(t, null, renderer: r)) { }
        var img = r.CachedImage(a)!;
        Assert.Equal(20, img.Frames.Length);
        Assert.Equal(40 * 100, img.TotalMs);   // frames are skipped evenly, the loop still lasts 4 s
    }

    [Fact]
    public void M1_an_animation_never_freezes_even_with_no_budget_left()
    {
        var a = Gif("a.gif", 40);
        var t = Fake.Canvas(100, 100);
        t.Widgets.Add(Picture(a, 0, 0, 20, 20));
        using var r = new ThemeRenderer { ImageBudgetBytes = 4 };
        using (Fake.Render(t, null, renderer: r)) { }
        Assert.True(r.CachedImage(a)!.Frames.Length >= 2, "a frozen animation shows one frame forever");
    }

    // ---- M2: decode for the display's pixels, not the theme's -----------------------------------------------------

    [Fact]
    public void M2_pictures_are_decoded_for_the_panel_when_the_theme_is_scaled_up()
    {
        var path = Png("photo.png", 1200, 900, SKColors.Red);
        var t = Fake.Canvas(320, 240);
        t.Widgets.Add(Picture(path, 0, 0, 320, 240));
        using var r = new ThemeRenderer();
        using (RenderOnPanel(t, r, 480, 320)) { }
        Assert.True(r.CachedImage(path)!.Width >= 426, $"decoded {r.CachedImage(path)!.Width} px wide for a 427 px box on the panel");
    }

    [Fact]
    public void M2_a_theme_larger_than_the_panel_decodes_no_more_than_the_panel_shows()
    {
        var path = Png("photo.png", 2000, 1500, SKColors.Red);
        var t = Fake.Canvas(800, 600);
        t.Widgets.Add(Picture(path, 0, 0, 800, 600));
        using var r = new ThemeRenderer();
        using (RenderOnPanel(t, r, 400, 300)) { }
        Assert.InRange(r.CachedImage(path)!.Width, 400, 420);
    }

    // ---- L1: keep the old picture while a changed file cannot be decoded ------------------------------------------

    [Fact]
    public void L1_a_half_written_file_keeps_showing_the_old_picture()
    {
        var path = Png("pic.png", 50, 50, SKColors.Red);
        Touch(path, -60);
        long now = 0;
        using var r = new ThemeRenderer { Now = () => now, StatWaitMs = 5000 };
        var t = Fake.Canvas(); t.Widgets.Add(Picture(path, 0, 0, 100, 100));
        using (var bmp = Fake.Render(t, null, renderer: r)) Assert.Equal(SKColors.Red, bmp.GetPixel(50, 50));

        var full = File.ReadAllBytes(Png("green.png", 50, 50, SKColors.Lime));
        File.WriteAllBytes(path, full.Take(20).ToArray());   // a save caught half way
        Touch(path, -30);
        now += 10_000;
        using (var bmp = Fake.Render(t, null, renderer: r)) Assert.Equal(SKColors.Red, bmp.GetPixel(50, 50));

        File.WriteAllBytes(path, full);                      // the save finishes
        Touch(path, 0);
        now += 10_000;
        using (var bmp = Fake.Render(t, null, renderer: r)) Assert.Equal(SKColors.Lime, bmp.GetPixel(50, 50));
    }

    [Fact]
    public void L1_a_file_that_stays_broken_finally_shows_the_placeholder()
    {
        var path = Png("pic.png", 50, 50, SKColors.Red);
        Touch(path, -60);
        long now = 0;
        using var r = new ThemeRenderer { Now = () => now, StatWaitMs = 5000 };
        var t = Fake.Canvas(); t.Widgets.Add(Picture(path, 0, 0, 100, 100));
        using (Fake.Render(t, null, renderer: r)) { }
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
        Touch(path, 0);
        for (int i = 0; i < 3; i++) { now += 10_000; using (Fake.Render(t, null, renderer: r)) { } }
        using var bmp = Fake.Render(t, null, renderer: r);
        Assert.NotEqual(SKColors.Red, bmp.GetPixel(50, 50));
    }

    // ---- L2 and N5: resizing decodes once the size has settled ----------------------------------------------------

    [Fact]
    public void L2_resizing_does_not_decode_at_every_step_and_decodes_once_settled()
    {
        var path = Png("big.png", 2000, 1500, SKColors.Red);
        var t = Fake.Canvas(800, 600);
        t.Widgets.Add(Picture(path, 0, 0, 40, 30));
        long now = 0;
        using var r = new ThemeRenderer { Now = () => now };
        using (RenderOnce(t, r)) { }
        for (int wpx = 44; wpx <= 800; wpx += 4)   // a drag, one frame every 16 ms
        {
            t.Widgets[0].Width = wpx; t.Widgets[0].Height = wpx * 0.75;
            now += 16;
            using (RenderOnce(t, r)) { }
        }
        Assert.Equal(1, r.ImageDecodes);

        now += 1000;   // the user let go
        using (RenderOnce(t, r)) { }
        using (RenderOnce(t, r)) { }
        Assert.Equal(2, r.ImageDecodes);
        Assert.True(r.CachedImage(path)!.Width >= 800);
    }

    [Fact]
    public void N5_a_much_smaller_widget_gets_a_smaller_decode_once_settled()
    {
        var path = Png("big.png", 2000, 1500, SKColors.Red);
        var t = Fake.Canvas(800, 600);
        t.Widgets.Add(Picture(path, 0, 0, 800, 600));
        long now = 0;
        using var r = new ThemeRenderer { Now = () => now };
        using (RenderOnce(t, r)) { }
        t.Widgets[0].Width = 100; t.Widgets[0].Height = 75;
        using (RenderOnce(t, r)) { }
        now += 1000;
        using (RenderOnce(t, r)) { }
        Assert.InRange(r.CachedImage(path)!.Width, 100, 200);
    }

    // ---- N6: Stretch decodes to the exact box -------------------------------------------------------------------

    [Fact]
    public void N6_stretch_decodes_to_the_exact_box()
    {
        var path = Png("sq.png", 1000, 1000, SKColors.Red);
        var t = Fake.Canvas(200, 100);
        t.Widgets.Add(Picture(path, 0, 0, 200, 50));
        using var r = new ThemeRenderer();
        using var bmp = Fake.Render(t, null, renderer: r);
        var img = r.CachedImage(path)!;
        Assert.Equal((200, 50), (img.Width, img.Height));
        Assert.Equal(SKColors.Red, bmp.GetPixel(100, 25));
    }

    [Fact]
    public void N6_contain_keeps_the_aspect_ratio_when_another_widget_stretches_the_same_file()
    {
        var path = Png("sq.png", 1000, 1000, SKColors.Red);
        var t = Fake.Canvas(300, 200);
        t.Widgets.Add(Picture(path, 0, 0, 300, 50));                       // stretched
        t.Widgets.Add(Picture(path, 0, 100, 200, 100, ImageFit.Contain));  // a 100x100 square in the middle
        long now = 0;
        using var r = new ThemeRenderer { Now = () => now };
        using (Fake.Render(t, null, renderer: r)) { }
        now += 1000;   // let both sizes settle into one decode
        using var bmp = Fake.Render(t, null, renderer: r);
        Assert.Equal(SKColors.Red, bmp.GetPixel(100, 150));
        Assert.NotEqual(SKColors.Red, bmp.GetPixel(20, 150));    // left bar stays empty
        Assert.NotEqual(SKColors.Red, bmp.GetPixel(180, 150));   // right bar stays empty
    }

    // ---- L4: a replaced video file restarts ---------------------------------------------------------------------

    [Fact]
    public void L4_a_video_file_replaced_on_disk_is_restarted()
    {
        var path = Path.Combine(_dir, "clip.mp4");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        Touch(path, -60);
        long now = 0; int created = 0;
        using var r = new ThemeRenderer
        {
            Now = () => now, StatWaitMs = 5000,
            // an ffmpeg path that does not exist: the source reports an error and starts nothing
            VideoFactory = (p, w, h, fit) => { created++; return new VideoSource(p, w, h, fit, 15, () => @"C:\glance-test\no-ffmpeg.exe", () => null); },
        };
        var t = Fake.Canvas(); t.Widgets.Add(Picture(path, 0, 0, 100, 100));
        using (Fake.Render(t, null, renderer: r)) { }
        now += 10_000;
        using (Fake.Render(t, null, renderer: r)) { }
        Assert.Equal(1, created);   // unchanged file: not restarted

        File.WriteAllBytes(path, new byte[] { 4, 5, 6, 7 });
        Touch(path, 0);
        now += 10_000;
        using (Fake.Render(t, null, renderer: r)) { }
        Assert.Equal(2, created);
    }

    // ---- L5 and N2: the file check -------------------------------------------------------------------------------

    [Fact]
    public void L5_a_slow_file_check_does_not_block_rendering()
    {
        var path = Png("pic.png", 50, 50, SKColors.Red);
        long now = 0;
        using var r = new ThemeRenderer { Now = () => now };
        var t = Fake.Canvas(); t.Widgets.Add(Picture(path, 0, 0, 100, 100));
        using (Fake.Render(t, null, renderer: r)) { }

        r.StatFile = p => { Thread.Sleep(3000); return default; };   // an unreachable network share
        now += 10_000;
        var sw = Stopwatch.StartNew();
        using (var bmp = RenderOnce(t, r)) Assert.Equal(SKColors.Red, bmp.GetPixel(50, 50));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"render took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void N2_a_changed_file_is_checked_once()
    {
        var path = Png("pic.png", 50, 50, SKColors.Red);
        Touch(path, -60);
        long now = 0; int stats = 0;
        using var r = new ThemeRenderer { Now = () => now, StatWaitMs = 5000 };
        r.StatFile = p => { Interlocked.Increment(ref stats); return ThemeRenderer.FileStamp.Of(p); };
        var t = Fake.Canvas(); t.Widgets.Add(Picture(path, 0, 0, 100, 100));
        using (RenderOnce(t, r)) { }
        Png("pic.png", 60, 60, SKColors.Lime);
        Touch(path, 0);
        now += 10_000;
        stats = 0;
        using (var bmp = RenderOnce(t, r)) Assert.Equal(SKColors.Lime, bmp.GetPixel(50, 50));
        Assert.Equal(1, Volatile.Read(ref stats));
    }

    // ---- N3: the working bitmap is freed when a load fails --------------------------------------------------------

    [Fact]
    public void N3_a_failed_animation_load_frees_its_working_bitmaps()
    {
        var path = Gif("a.gif", 4);
        var bitmaps = new List<SKBitmap>();
        AnimatedImage.WorkingBitmapCreated = bitmaps.Add;
        AnimatedImage.FrameCreated = (i, _) => { if (i == 1) throw new InvalidOperationException("simulated"); };
        try { Assert.Null(AnimatedImage.Load(path)); }
        finally { AnimatedImage.WorkingBitmapCreated = null; AnimatedImage.FrameCreated = null; }
        Assert.NotEmpty(bitmaps);
        Assert.All(bitmaps, b => Assert.Equal(IntPtr.Zero, b.Handle));
    }

    // ---- N4: drawing a graph does not copy its history ----------------------------------------------------------

    [Fact]
    public void N4_drawing_a_graph_does_not_copy_its_history_every_frame()
    {
        var t = Fake.Canvas(200, 100);
        t.Widgets.Add(new Widget { Kind = WidgetKind.Graph, X = 0, Y = 0, Width = 200, Height = 100, SensorId = "v", HistorySeconds = 900, Min = 0, Max = 100 });
        var src = new FakeSource(new[] { new SensorInfo("v", "t", "v", "Load", 40, "%") });
        using var full = new ThemeRenderer();
        full.SeedGraphHistory(t.ActivePage, src);   // 1800 samples
        using var bare = new ThemeRenderer();        // one sample

        long Alloc(ThemeRenderer r)
        {
            using var bmp = new SKBitmap(200, 100);
            using var canvas = new SKCanvas(bmp);
            for (int i = 0; i < 3; i++) r.Render(canvas, t, src);
            var before = GC.GetAllocatedBytesForCurrentThread();
            r.Render(canvas, t, src);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        long extra = Alloc(full) - Alloc(bare);
        Assert.True(extra < 4096, $"a full history costs {extra} more bytes per frame");
    }

    // ---- guard: downscale quality ---------------------------------------------------------------------------------

    [Fact]
    public void Downscaled_detail_averages_instead_of_aliasing()
    {
        using var tile = new SKBitmap(4, 4);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
                tile.SetPixel(x, y, ((x / 2 + y / 2) & 1) == 0 ? SKColors.White : SKColors.Black);
        using var check = new SKBitmap(2000, 1500);
        using (var canvas = new SKCanvas(check))
        using (var shader = SKShader.CreateBitmap(tile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat))
        using (var paint = new SKPaint { Shader = shader })
            canvas.DrawRect(0, 0, 2000, 1500, paint);   // a 2 px checkerboard
        var path = Save("check.png", check);
        var t = Fake.Canvas(); t.Widgets.Add(Picture(path, 0, 0, 100, 75));
        using var bmp = Fake.Render(t, null);
        for (int x = 10; x < 90; x += 20)
            Assert.InRange(bmp.GetPixel(x, 37).Red, (byte)100, (byte)155);   // a 2 px checkerboard shrunk 20x is grey
    }
}

/// <summary>Review finding L3: a longer graph is seeded for the part the existing history does not cover.</summary>
public class GraphHistoryReviewFixTests
{
    private static bool Sorted(IReadOnlyList<(long ms, float value)> s) => s.Zip(s.Skip(1), (a, b) => a.ms < b.ms).All(x => x);

    private static Widget Graph(string sensor, double seconds) =>
        new() { Kind = WidgetKind.Graph, X = 0, Y = 0, Width = 100, Height = 50, SensorId = sensor, HistorySeconds = seconds, Min = 0, Max = 100 };

    [Fact]
    public void L3_a_longer_graph_added_next_to_a_shorter_one_is_seeded_for_the_missing_part()
    {
        var t = Fake.Canvas(200, 100);
        t.Widgets.Add(Graph("v", 30));
        var src = new FakeSource(new[] { new SensorInfo("v", "t", "v", "Load", 40, "%") });
        using var r = new ThemeRenderer();
        r.SeedGraphHistory(t.ActivePage, src);
        t.Widgets.Add(Graph("v", 300));
        r.SeedGraphHistory(t.ActivePage, src);

        var w = r.History.Window("v", 0, 1e9);
        Assert.True(Sorted(w));
        long span = w[^1].ms - w[0].ms;
        Assert.True(span >= 299_000, $"history covers {span / 1000} s of a 300 s graph");
    }

    [Fact]
    public void L3_real_samples_stay_and_only_the_missing_past_is_seeded()
    {
        var t = Fake.Canvas(200, 100);
        t.Widgets.Add(Graph("v", 60));
        var src = new FakeSource(new[] { new SensorInfo("v", "t", "v", "Load", 40, "%") });
        using var r = new ThemeRenderer();
        using (Fake.Render(t, src, renderer: r)) { }   // records a real sample
        var real = r.History.Window("v", 0, 1e9).ToList();

        r.SeedGraphHistory(t.ActivePage, src);

        var w = r.History.Window("v", 0, 1e9);
        Assert.True(Sorted(w), "no zigzag back to the left edge");
        Assert.Equal(real, w.Skip(w.Count - real.Count).ToList());   // the real samples are the newest, unchanged
        Assert.True(w.Count > real.Count, "the empty past of the graph is filled");
    }
}

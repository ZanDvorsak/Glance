using Glance.Core.Media;
using Glance.Core.Sensors;
using Glance.Core.Themes;
using SkiaSharp;

namespace Glance.Tests;

/// <summary>
/// Review finding M10: the renderer's image and font caches (memory use, freeing typefaces, noticing files that
/// change on disk). Memory is measured through proxies: decoded pixel sizes, the cache's byte total and whether a
/// typeface's native handle was freed.
/// </summary>
public sealed class RendererMemoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"glance_mem_{Guid.NewGuid():N}");

    public RendererMemoryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Png(string name, int w, int h, SKColor color)
    {
        var path = Path.Combine(_dir, name);
        using var bmp = new SKBitmap(w, h);
        bmp.Erase(color);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>Makes sure a rewritten file looks changed even on file systems with coarse timestamps.</summary>
    private static void Touch(string path, int seconds) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(seconds));

    private static Theme ImageTheme(string path, int w, int h, ImageFit fit = ImageFit.Stretch, int themeW = 100, int themeH = 100)
    {
        var t = Fake.Canvas(themeW, themeH);
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, X = 0, Y = 0, Width = w, Height = h, ImagePath = path, Fit = fit });
        return t;
    }

    private static string? SystemFont(string file)
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", file);
        return File.Exists(p) ? p : null;
    }

    // ---- typefaces ----------------------------------------------------------------------------------------------

    [Fact]
    public void Clearing_the_cache_frees_font_file_typefaces()
    {
        var font = SystemFont("cour.ttf") ?? SystemFont("consola.ttf");
        if (font is null) return;   // not a Windows machine with the usual fonts
        var t = Fake.Canvas(200, 60);
        t.Widgets.Add(new Widget { Kind = WidgetKind.Text, Text = "Hello", FontSize = 30, FontFile = font });
        using var r = new ThemeRenderer();
        using (Fake.Render(t, null, renderer: r)) { }
        var face = r.CachedFileFace(font);
        Assert.NotNull(face);
        Assert.NotEqual(IntPtr.Zero, face!.Handle);

        r.ClearImageCache();

        Assert.Equal(IntPtr.Zero, face.Handle);   // the native typeface was freed, not just forgotten
    }

    [Fact]
    public void Disposing_the_renderer_frees_font_file_typefaces()
    {
        var font = SystemFont("cour.ttf") ?? SystemFont("consola.ttf");
        if (font is null) return;
        var t = Fake.Canvas(200, 60);
        t.Widgets.Add(new Widget { Kind = WidgetKind.Text, Text = "Hello", FontSize = 30, FontFile = font });
        var r = new ThemeRenderer();
        using (Fake.Render(t, null, renderer: r)) { }
        var face = r.CachedFileFace(font)!;
        r.Dispose();
        Assert.Equal(IntPtr.Zero, face.Handle);
    }

    [Fact]
    public void A_font_file_replaced_on_disk_is_read_again_and_the_old_typeface_freed()
    {
        var first = SystemFont("cour.ttf");
        var second = SystemFont("consola.ttf") ?? SystemFont("arial.ttf");
        if (first is null || second is null) return;
        var path = Path.Combine(_dir, "font.ttf");
        File.Copy(first, path);
        Touch(path, -60);
        var t = Fake.Canvas(200, 60);
        t.Widgets.Add(new Widget { Kind = WidgetKind.Text, Text = "Hello", FontSize = 30, FontFile = path });
        long now = 0;
        using var r = new ThemeRenderer { Now = () => now, StatWaitMs = 5000 };
        using (Fake.Render(t, null, renderer: r)) { }
        var old = r.CachedFileFace(path)!;

        File.Copy(second, path, overwrite: true);
        Touch(path, 0);
        now += 10_000;
        using (Fake.Render(t, null, renderer: r)) { }

        var fresh = r.CachedFileFace(path);
        Assert.NotNull(fresh);
        Assert.NotSame(old, fresh);
        Assert.Equal(IntPtr.Zero, old.Handle);
    }

    [Fact]
    public void Disposing_one_renderer_leaves_installed_fonts_working_for_another()
    {
        // SkiaSharp hands every caller the same object for an installed family and style (checked by hand), so a
        // renderer must never really free them: the editor preview, the display pusher and the virtual display share them.
        var t = Fake.Canvas(200, 60);
        t.Widgets.Add(new Widget { Kind = WidgetKind.Text, Text = "Hello", FontSize = 30, Bold = true, FontFamily = "Segoe UI", Color = "#FFFFFFFF" });
        using var keep = new ThemeRenderer();
        using var before = Fake.Render(t, null, renderer: keep);
        using (var gone = new ThemeRenderer()) { using (Fake.Render(t, null, renderer: gone)) { } }
        using var after = Fake.Render(t, null, renderer: keep);
        Assert.Equal(Ink(before), Ink(after));
        Assert.True(Ink(after) > 50);
    }

    private static int Ink(SKBitmap b)
    {
        int n = 0;
        for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) if (b.GetPixel(x, y).Red > 128) n++;
        return n;
    }

    // ---- decode size and budget ---------------------------------------------------------------------------------

    [Fact]
    public void A_large_image_is_decoded_near_the_size_it_is_drawn_at()
    {
        var path = Png("big.png", 2000, 1500, SKColors.Red);
        using var r = new ThemeRenderer();
        using var bmp = Fake.Render(ImageTheme(path, 100, 75), null, renderer: r);

        var img = r.CachedImage(path);
        Assert.NotNull(img);
        Assert.InRange(img!.Width, 100, 200);    // enough pixels for the box, far fewer than 2000
        Assert.InRange(img.Height, 75, 150);
        Assert.Equal(SKColors.Red, bmp.GetPixel(50, 37));
    }

    [Fact]
    public void A_cover_image_keeps_enough_pixels_to_fill_its_box()
    {
        var path = Png("wide.png", 2000, 1000, SKColors.Lime);
        using var r = new ThemeRenderer();
        using var bmp = Fake.Render(ImageTheme(path, 100, 100, ImageFit.Cover), null, renderer: r);
        var img = r.CachedImage(path)!;
        Assert.True(img.Height >= 100, $"height {img.Height} must cover the 100 px box");
        Assert.True(img.Width <= 400, $"width {img.Width} should be near the drawn 200 px, not 2000");
        Assert.Equal(SKColors.Lime, bmp.GetPixel(50, 50));
    }

    [Fact]
    public void A_small_image_is_not_scaled_up_when_decoded()
    {
        var path = Png("small.png", 20, 10, SKColors.Blue);
        using var r = new ThemeRenderer();
        using (Fake.Render(ImageTheme(path, 100, 100), null, renderer: r)) { }
        var img = r.CachedImage(path)!;
        Assert.Equal((20, 10), (img.Width, img.Height));
    }

    [Fact]
    public void Growing_an_image_widget_decodes_it_again_at_the_bigger_size()
    {
        var path = Png("big.png", 2000, 1500, SKColors.Red);
        var t = ImageTheme(path, 40, 30, themeW: 400, themeH: 300);
        long now = 0;
        using var r = new ThemeRenderer { Now = () => now, StatWaitMs = 5000 };
        using (Fake.Render(t, null, renderer: r)) { }
        t.Widgets[0].Width = 400; t.Widgets[0].Height = 300;
        using (Fake.Render(t, null, renderer: r)) { }
        now += 1000;   // the size has settled (review L2: no decode at every step of a drag)
        using (Fake.Render(t, null, renderer: r)) { }
        Assert.True(r.CachedImage(path)!.Width >= 400);
    }

    [Fact]
    public void Images_stay_within_the_renderer_memory_budget()
    {
        // three pages, each with its own 400x400 picture (640 KB decoded); the budget holds only one of them
        var t = Fake.Canvas(400, 400);
        t.Pages.Clear();
        var colors = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue };
        for (int i = 0; i < 3; i++)
        {
            var page = new ThemePage();
            page.Widgets.Add(new Widget { Kind = WidgetKind.Image, X = 0, Y = 0, Width = 400, Height = 400, ImagePath = Png($"p{i}.png", 400, 400, colors[i]) });
            t.Pages.Add(page);
        }
        using var r = new ThemeRenderer { ImageBudgetBytes = 1024 * 1024 };
        for (int i = 0; i < 3; i++)
        {
            using var bmp = Fake.Render(t, null, t.Pages[i], r);
            Assert.Equal(colors[i], bmp.GetPixel(200, 200));   // the page on screen is always drawn
            Assert.True(r.CachedImageBytes <= r.ImageBudgetBytes, $"page {i}: {r.CachedImageBytes} bytes cached");
        }
    }

    // ---- files that change on disk ------------------------------------------------------------------------------

    [Fact]
    public void An_image_replaced_on_disk_is_drawn_again_without_clearing_the_cache()
    {
        var path = Png("pic.png", 50, 50, SKColors.Red);
        Touch(path, -60);
        long now = 0;
        using var r = new ThemeRenderer { Now = () => now, StatWaitMs = 5000 };
        var t = ImageTheme(path, 100, 100);
        using (var bmp = Fake.Render(t, null, renderer: r)) Assert.Equal(SKColors.Red, bmp.GetPixel(50, 50));

        Png("pic.png", 60, 60, SKColors.Lime);
        Touch(path, 0);
        now += 10_000;
        using (var bmp = Fake.Render(t, null, renderer: r)) Assert.Equal(SKColors.Lime, bmp.GetPixel(50, 50));
    }

    [Fact]
    public void A_missing_image_is_tried_again_after_a_while()
    {
        var path = Path.Combine(_dir, "later.png");
        long now = 0;
        using var r = new ThemeRenderer { Now = () => now, StatWaitMs = 5000 };
        var t = ImageTheme(path, 100, 100);
        using (var bmp = Fake.Render(t, null, renderer: r)) Assert.NotEqual(SKColors.Red, bmp.GetPixel(50, 50));

        Png("later.png", 50, 50, SKColors.Red);
        now += 10_000;
        using (var bmp = Fake.Render(t, null, renderer: r)) Assert.Equal(SKColors.Red, bmp.GetPixel(50, 50));
    }

    [Fact]
    public void Unchanged_images_are_decoded_once_even_after_an_undo_style_snapshot_restore()
    {
        var path = Png("pic.png", 50, 50, SKColors.Red);
        long now = 0;
        using var r = new ThemeRenderer { Now = () => now, StatWaitMs = 5000 };
        var t = ImageTheme(path, 100, 100);
        using (Fake.Render(t, null, renderer: r)) { }
        for (int i = 0; i < 5; i++)
        {
            t.ApplySnapshot(t.ToSnapshot());   // what undo and redo do: brand-new widget objects, same files
            now += 10_000;
            using (Fake.Render(t, null, renderer: r)) { }
        }
        Assert.Equal(1, r.ImageDecodes);
    }

    // ---- AnimatedImage ------------------------------------------------------------------------------------------

    [Fact]
    public void A_gif_that_fails_partway_frees_the_frames_it_already_made()
    {
        var path = Path.Combine(_dir, "two.gif");
        File.WriteAllBytes(path, TwoFrameGif());
        var made = new List<SKImage>();
        AnimatedImage.FrameCreated = (i, img) =>
        {
            made.Add(img);
            if (i == 1) throw new InvalidOperationException("simulated decode failure");
        };
        try { Assert.Null(AnimatedImage.Load(path)); }
        finally { AnimatedImage.FrameCreated = null; }

        Assert.Equal(2, made.Count);
        Assert.All(made, img => Assert.Equal(IntPtr.Zero, img.Handle));
    }

    /// <summary>Hand-built 1x1 GIF: frame 1 red, frame 2 green, 100 ms each.</summary>
    internal static byte[] TwoFrameGif()
    {
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange(new byte[] { 1, 0, 1, 0, 0x80, 0, 0 });
        gif.AddRange(new byte[] { 255, 0, 0, 0, 255, 0 });
        gif.AddRange(new byte[] { 0x21, 0xFF, 0x0B });
        gif.AddRange("NETSCAPE2.0"u8.ToArray());
        gif.AddRange(new byte[] { 3, 1, 0, 0, 0 });
        foreach (var pixelData in new byte[] { 0x44, 0x4C })
        {
            gif.AddRange(new byte[] { 0x21, 0xF9, 4, 0, 10, 0, 0, 0 });
            gif.AddRange(new byte[] { 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0 });
            gif.AddRange(new byte[] { 2, 2, pixelData, 0x01, 0 });
        }
        gif.Add(0x3B);
        return gif.ToArray();
    }
}

/// <summary>Review finding M11: graph history seeding and time order.</summary>
public class GraphHistorySeedTests
{
    private static bool Sorted(IReadOnlyList<(long ms, float value)> s) => s.Zip(s.Skip(1), (a, b) => a.ms < b.ms).All(x => x);

    [Fact]
    public void Seeding_after_real_samples_keeps_time_order()
    {
        var h = new SensorHistory();
        for (int i = 0; i < 60; i++) h.Record("s", 50, i * 1000L);             // a minute of real samples
        for (long ms = 30_000; ms < 60_000; ms += SensorHistory.SampleMs) h.Seed("s", ms + 250, 10);
        Assert.True(Sorted(h.Window("s", 60_000, 120)), "the window must stay oldest first");
    }

    [Fact]
    public void Window_returns_a_copy_not_the_live_list()
    {
        var h = new SensorHistory();
        h.Record("s", 1, 0);
        var w = h.Window("s", 0, 60);
        h.Record("s", 2, 1000);
        Assert.Single(w);
    }

    private static Widget Graph(string sensor, double seconds) =>
        new() { Kind = WidgetKind.Graph, X = 0, Y = 0, Width = 100, Height = 50, SensorId = sensor, HistorySeconds = seconds, Min = 0, Max = 100 };

    [Fact]
    public void Seeding_a_page_again_does_not_duplicate_the_history()
    {
        var t = Fake.Canvas(200, 100);
        t.Widgets.Add(Graph("v", 60));
        var src = new FakeSource(new[] { new SensorInfo("v", "t", "v", "Load", 40, "%") });
        using var r = new ThemeRenderer();
        r.SeedGraphHistory(t.ActivePage, src);
        r.SeedGraphHistory(t.ActivePage, src);   // adding a preset seeds the whole page again
        var w = r.History.Window("v", 0, 1e9);
        Assert.True(Sorted(w));
        Assert.Equal(60 * 1000 / SensorHistory.SampleMs, w.Count);
    }

    [Fact]
    public void Two_graphs_sharing_a_sensor_are_seeded_once_for_the_longer_window()
    {
        var t = Fake.Canvas(200, 100);
        t.Widgets.Add(Graph("v", 30));
        t.Widgets.Add(Graph("v", 120));
        var src = new FakeSource(new[] { new SensorInfo("v", "t", "v", "Load", 40, "%") });
        using var r = new ThemeRenderer();
        r.SeedGraphHistory(t.ActivePage, src);
        var w = r.History.Window("v", 0, 1e9);
        Assert.True(Sorted(w));
        Assert.Equal(120 * 1000 / SensorHistory.SampleMs, w.Count);
    }

    // A_graph_that_already_has_real_samples_is_not_seeded_over became
    // GraphHistoryReviewFixTests.L3_real_samples_stay_and_only_the_missing_past_is_seeded (review L3: the empty past is now filled).
}

using Glance.Core.Display;
using Glance.Core.Editing;
using Glance.Core.Media;
using Glance.Core.Rendering;
using Glance.Core.Sensors;
using Glance.Core.Setup;
using Glance.Core.Themes;
using SkiaSharp;

namespace Glance.Tests;

public class PageRotationTests
{
    private static Theme ThreePages(bool auto = true)
    {
        var t = new Theme { AutoRotate = auto };
        t.Pages.Clear();
        t.Pages.Add(new ThemePage { Name = "A", DurationSeconds = 2 });
        t.Pages.Add(new ThemePage { Name = "B", DurationSeconds = 3 });
        t.Pages.Add(new ThemePage { Name = "C", DurationSeconds = 1 });
        return t;
    }

    private static int Next(Theme t, int current, ref long since, long now, int pinned = -1, int step = 0) =>
        DisplayPusher.NextPageIndex(t, current, ref since, now, pinned, step);

    [Fact]
    public void Stays_on_a_page_until_its_duration_has_passed()
    {
        var t = ThreePages(); long since = 0;
        Assert.Equal(0, Next(t, 0, ref since, 1900));
        Assert.Equal(1, Next(t, 0, ref since, 2000));
    }

    [Fact]
    public void Each_page_uses_its_own_duration_and_wraps_around()
    {
        var t = ThreePages(); long since = 0; int page = 0;
        page = Next(t, page, ref since, 2100); Assert.Equal(1, page);          // A (2 s) -> B
        page = Next(t, page, ref since, 4000); Assert.Equal(1, page);          // B needs 3 s: not yet
        page = Next(t, page, ref since, 5200); Assert.Equal(2, page);          // B -> C
        page = Next(t, page, ref since, 6300); Assert.Equal(0, page);          // C (1 s) -> back to A
    }

    [Theory]
    [InlineData(+1, 0, 1)]
    [InlineData(-1, 0, 2)]    // previous from the first page wraps to the last
    [InlineData(-2, 1, 2)]
    [InlineData(+5, 0, 2)]
    public void Manual_steps_move_and_wrap(int step, int from, int expected)
    {
        var t = ThreePages(); long since = 0;
        Assert.Equal(expected, Next(t, from, ref since, 100, step: step));
    }

    [Fact]
    public void A_manual_step_restarts_the_page_timer()
    {
        var t = ThreePages(); long since = 0;
        Next(t, 0, ref since, 1500, step: 1);
        Assert.Equal(1500, since);
    }

    [Fact]
    public void A_pinned_page_wins_over_rotation_and_is_clamped()
    {
        var t = ThreePages(); long since = 0;
        Assert.Equal(1, Next(t, 0, ref since, 999_999, pinned: 1));
        Assert.Equal(2, Next(t, 0, ref since, 999_999, pinned: 40));
    }

    [Fact]
    public void Turning_auto_rotate_off_keeps_the_current_page()
    {
        var t = ThreePages(auto: false); long since = 0;
        Assert.Equal(1, Next(t, 1, ref since, 500_000));
    }

    [Fact]
    public void A_single_page_theme_is_always_page_zero()
    {
        var t = new Theme(); long since = 0;
        Assert.Equal(0, Next(t, 0, ref since, 1_000_000, step: 3));
    }

    [Fact]
    public void A_stale_page_index_after_deleting_pages_falls_back_safely()
    {
        var t = ThreePages(); long since = 0;
        Assert.InRange(Next(t, 7, ref since, 10), 0, 2);
    }

    [Fact]
    public void Durations_below_one_second_are_treated_as_one_second()
    {
        var t = ThreePages(); t.Pages[0].DurationSeconds = 0; long since = 0;
        Assert.Equal(0, Next(t, 0, ref since, 900));
        Assert.Equal(1, Next(t, 0, ref since, 1000));
    }
}

public class SetupDiagnosisTests
{
    private static CheckResult Diagnose(bool dll = true, bool found = false, bool runner = false, string? openError = null, bool registered = false, string? service = null) =>
        SetupChecks.DiagnoseDisplay(dll, found, runner, openError, registered, service);

    [Fact]
    public void Missing_library_is_a_problem_with_a_Zadig_fix() =>
        Assert.Equal((CheckStatus.Problem, FixKind.WingetInstall, SetupChecks.ZadigId), Tuple(Diagnose(dll: false)));

    [Fact]
    public void Connected_and_free_is_ok() =>
        Assert.Equal(CheckStatus.Ok, Diagnose(found: true, registered: true, service: "libusb0").Status);

    [Fact]
    public void Connected_with_the_runner_using_it_is_ok_and_says_so()
    {
        var r = Diagnose(found: true, runner: true);
        Assert.Equal(CheckStatus.Ok, r.Status);
        Assert.Contains("service or the tray app", r.Detail);   // runner-service: the elevated runner is gone
    }

    [Fact]
    public void Connected_but_busy_is_a_warning_that_mentions_the_error_and_AIDA64()
    {
        var r = Diagnose(found: true, openError: "access denied");
        Assert.Equal(CheckStatus.Warning, r.Status);
        Assert.Contains("access denied", r.Detail);
        Assert.Contains("AIDA64", r.Detail);
    }

    [Fact]
    public void Right_driver_but_unplugged_says_plug_it_in_and_offers_no_installer()
    {
        var r = Diagnose(registered: true, service: "libusb0");
        Assert.Equal(CheckStatus.Problem, r.Status);
        Assert.Equal(FixKind.None, r.Fix);
        Assert.Contains("plug", r.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wrong_driver_names_the_driver_and_offers_Zadig()
    {
        var r = Diagnose(registered: true, service: "usbccgp");
        Assert.Equal(FixKind.WingetInstall, r.Fix);
        Assert.Contains("usbccgp", r.Detail);
    }

    [Fact]
    public void Known_device_without_a_driver_service_offers_Zadig() =>
        Assert.Equal(FixKind.WingetInstall, Diagnose(registered: true, service: null).Fix);

    [Fact]
    public void Never_connected_says_so_and_offers_Zadig()
    {
        var r = Diagnose();
        Assert.Equal(FixKind.WingetInstall, r.Fix);
        Assert.Contains("No display", r.Detail);
    }

    [Fact]
    public void Only_problems_need_attention()
    {
        var ok = new CheckResult("a", "a", CheckStatus.Ok, "");
        var warn = new CheckResult("b", "b", CheckStatus.Warning, "");
        var bad = new CheckResult("c", "c", CheckStatus.Problem, "");
        Assert.False(SetupChecks.NeedsAttention(new[] { ok, warn }));
        Assert.True(SetupChecks.NeedsAttention(new[] { ok, bad }));
    }

    private static (CheckStatus, FixKind, string?) Tuple(CheckResult r) => (r.Status, r.Fix, r.FixArg);
}

public class UndoManagerTests
{
    [Fact]
    public void Undo_and_redo_walk_through_history()
    {
        var u = new UndoManager("0");
        Assert.True(u.Commit("1")); Assert.True(u.Commit("2"));
        Assert.Equal("1", u.Undo()); Assert.Equal("0", u.Undo());
        Assert.Null(u.Undo());
        Assert.Equal("1", u.Redo()); Assert.Equal("2", u.Redo());
        Assert.Null(u.Redo());
    }

    [Fact]
    public void Identical_state_is_not_a_step()
    {
        var u = new UndoManager("a");
        Assert.False(u.Commit("a"));
        Assert.False(u.CanUndo);
    }

    [Fact]
    public void A_new_edit_clears_redo()
    {
        var u = new UndoManager("0");
        u.Commit("1"); u.Undo();
        Assert.True(u.CanRedo);
        u.Commit("x");
        Assert.False(u.CanRedo);
    }

    [Fact]
    public void History_is_capped()
    {
        var u = new UndoManager("0");
        for (int i = 1; i <= 300; i++) u.Commit(i.ToString());
        int steps = 0; while (u.Undo() is not null) steps++;
        Assert.InRange(steps, 150, 250);
    }

    [Fact]
    public void Reset_forgets_everything_and_Rebase_adds_no_step()
    {
        var u = new UndoManager("0");
        u.Commit("1");
        u.Rebase("1b");
        Assert.True(u.CanUndo);
        Assert.Equal("0", u.Undo());   // rebase changed the baseline, not the history
        u.Reset("fresh");
        Assert.False(u.CanUndo); Assert.False(u.CanRedo);
    }
}

public class SensorHistoryTests
{
    [Fact]
    public void Samples_are_throttled_to_the_sample_interval()
    {
        var h = new SensorHistory();
        h.Record("s", 1, 0); h.Record("s", 2, 100); h.Record("s", 3, SensorHistory.SampleMs);
        Assert.Equal(2, h.Window("s", SensorHistory.SampleMs, 60).Count);
    }

    [Fact]
    public void Window_returns_only_the_requested_seconds_oldest_first()
    {
        var h = new SensorHistory();
        for (int i = 0; i < 40; i++) h.Record("s", i, i * 1000L);   // one sample per second
        var w = h.Window("s", 39_000, 10);
        Assert.Equal(29f, w[0].value);   // the window includes its own start (39 s - 10 s = 29 s)
        Assert.Equal(39f, w[^1].value);
        Assert.True(w.Zip(w.Skip(1), (a, b) => a.ms < b.ms).All(x => x));
    }

    [Fact]
    public void Old_samples_are_dropped_after_fifteen_minutes()
    {
        var h = new SensorHistory();
        h.Record("s", 1, 0);
        h.Record("s", 2, 16 * 60 * 1000L);
        Assert.Single(h.Window("s", 16 * 60 * 1000L, 3600));
    }

    [Fact]
    public void Unknown_sensor_has_an_empty_window_and_seed_bypasses_the_throttle()
    {
        var h = new SensorHistory();
        Assert.Empty(h.Window("nope", 0, 60));
        for (int i = 0; i < 5; i++) h.Seed("s", 100 + i, 1);
        Assert.Equal(5, h.Window("s", 105, 60).Count);
    }
}

public class SensorCategoryTests
{
    [Theory]
    [InlineData("/amdcpu/0/temperature/2", "CPU")]
    [InlineData("/intelcpu/0/load/0", "CPU")]
    [InlineData("/gpu-nvidia/0/clock/0", "GPU")]
    [InlineData("/lpc/nct6798d/0/fan/1", "Motherboard")]
    [InlineData("/ram/load/0", "Memory")]
    [InlineData("/memory/dimm/2/temperature/0", "Memory")]
    [InlineData("/nvme/1/temperature/0", "Storage")]
    [InlineData("/hdd/0/temperature/0", "Storage")]
    [InlineData("/drive/c/load/0", "Drives")]
    [InlineData("/nic/{GUID}/throughput/7", "Network")]
    [InlineData("/something/else/0", "Other")]
    public void Sensors_are_grouped_by_hardware(string id, string expected) =>
        Assert.Equal(expected, SensorCategory.Of(new SensorInfo(id, "hw", "n", "Load", 1, "")));

    [Fact]
    public void Groups_and_types_have_a_stable_display_order()
    {
        Assert.True(SensorCategory.CategoryRank("CPU") < SensorCategory.CategoryRank("GPU"));
        Assert.True(SensorCategory.CategoryRank("Network") < SensorCategory.CategoryRank("Other"));
        Assert.True(SensorCategory.TypeRank("Temperature") < SensorCategory.TypeRank("Fan"));
        Assert.True(SensorCategory.TypeRank("Fan") < SensorCategory.TypeRank("Unknown type"));
    }
}

public class DriveSensorTests
{
    [Fact]
    public void Every_fixed_drive_reports_used_free_total_and_utilization()
    {
        var all = DriveSensors.Read();
        Assert.NotEmpty(all);
        foreach (var letter in all.Select(s => s.Id.Split('/')[2]).Distinct())
        {
            var mine = all.Where(s => s.Id.StartsWith($"/drive/{letter}/")).ToList();
            Assert.Equal(4, mine.Count);
            var util = Assert.Single(mine, s => s.Id.EndsWith("/load/0"));
            Assert.InRange(util.Value!.Value, 0, 100);
            Assert.True(mine.Single(s => s.Id.EndsWith("/data/2")).Value > 0);
        }
    }
}

public class FrameConverterTests
{
    private static byte[] Convert1(SKColor c, SKColorType type)
    {
        using var bmp = new SKBitmap(new SKImageInfo(1, 1, type, SKAlphaType.Premul));
        bmp.SetPixel(0, 0, c);
        return FrameConverter.ToRgb565Be(bmp);
    }

    [Theory]
    [InlineData(255, 0, 0, 0xF8, 0x00)]       // red   = 0xF800
    [InlineData(0, 255, 0, 0x07, 0xE0)]       // green = 0x07E0
    [InlineData(0, 0, 255, 0x00, 0x1F)]       // blue  = 0x001F
    [InlineData(255, 255, 255, 0xFF, 0xFF)]
    [InlineData(0, 0, 0, 0x00, 0x00)]
    public void Colours_become_big_endian_RGB565(byte r, byte g, byte b, byte hi, byte lo)
    {
        foreach (var type in new[] { SKColorType.Bgra8888, SKColorType.Rgba8888 })
        {
            var bytes = Convert1(new SKColor(r, g, b), type);
            Assert.Equal(new[] { hi, lo }, bytes);
        }
    }

    [Fact]
    public void Output_has_two_bytes_per_pixel_in_row_order()
    {
        using var bmp = new SKBitmap(new SKImageInfo(3, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        bmp.SetPixel(0, 0, SKColors.Red); bmp.SetPixel(2, 1, SKColors.Blue);
        var bytes = FrameConverter.ToRgb565Be(bmp);
        Assert.Equal(3 * 2 * 2, bytes.Length);
        Assert.Equal(0xF8, bytes[0]);                 // first pixel is red
        Assert.Equal(0x1F, bytes[^1]);                // last pixel is blue
    }

    [Fact]
    public void Unsupported_pixel_formats_are_rejected()
    {
        using var bmp = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Gray8, SKAlphaType.Opaque));
        Assert.Throws<ArgumentException>(() => FrameConverter.ToRgb565Be(bmp));
    }
}

public class MediaTests
{
    [Theory]
    [InlineData("clip.mp4", true)]
    [InlineData("CLIP.WEBM", true)]
    [InlineData("a/b/c.mkv", true)]
    [InlineData("picture.png", false)]
    [InlineData("anim.gif", false)]
    [InlineData("", false)]
    public void Video_files_are_recognised_by_extension(string path, bool expected) => Assert.Equal(expected, Ffmpeg.IsVideo(path));

    [Fact]
    public void A_still_image_loads_as_one_frame()
    {
        var path = Path.Combine(Path.GetTempPath(), $"glance_test_{Guid.NewGuid():N}.png");
        using (var bmp = new SKBitmap(4, 3)) { bmp.Erase(SKColors.Red); using var img = SKImage.FromBitmap(bmp); File.WriteAllBytes(path, img.Encode(SKEncodedImageFormat.Png, 100).ToArray()); }
        try
        {
            using var anim = AnimatedImage.Load(path);
            Assert.NotNull(anim);
            Assert.False(anim!.IsAnimated);
            Assert.Equal((4, 3), (anim.Width, anim.Height));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_two_frame_gif_loops_through_its_frames_by_time()
    {
        // hand-built 1x1 GIF: frame 1 red for 100 ms, frame 2 green for 100 ms, looping forever
        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange(new byte[] { 1, 0, 1, 0, 0x80, 0, 0 });              // 1x1, global colour table with 2 entries
        gif.AddRange(new byte[] { 255, 0, 0, 0, 255, 0 });                // red, green
        gif.AddRange(new byte[] { 0x21, 0xFF, 0x0B });
        gif.AddRange("NETSCAPE2.0"u8.ToArray());
        gif.AddRange(new byte[] { 3, 1, 0, 0, 0 });                       // loop forever
        foreach (var pixelData in new byte[] { 0x44, 0x4C })              // LZW data for "colour 0" and "colour 1"
        {
            gif.AddRange(new byte[] { 0x21, 0xF9, 4, 0, 10, 0, 0, 0 });   // graphic control: 10 centiseconds
            gif.AddRange(new byte[] { 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0 }); // image descriptor
            gif.AddRange(new byte[] { 2, 2, pixelData, 0x01, 0 });        // min code size, one sub-block, terminator
        }
        gif.Add(0x3B);
        var path = Path.Combine(Path.GetTempPath(), $"glance_test_{Guid.NewGuid():N}.gif");
        File.WriteAllBytes(path, gif.ToArray());
        try
        {
            using var anim = AnimatedImage.Load(path);
            Assert.NotNull(anim);
            Assert.True(anim!.IsAnimated);
            Assert.Equal(2, anim.Frames.Length);
            Assert.Same(anim.At(0), anim.At(50));          // still frame 1
            Assert.NotSame(anim.At(0), anim.At(150));      // frame 2
            Assert.Same(anim.At(0), anim.At(250));         // looped back to frame 1
        }
        finally { File.Delete(path); }
    }
}

public class HandleTests
{
    [Fact]
    public void Resizable_widgets_get_eight_handles_and_text_gets_four()
    {
        var bar = Widget.Create(WidgetKind.Bar); bar.LastBounds = new SKRect(10, 10, 110, 30);
        var text = Widget.Create(WidgetKind.Text); text.LastBounds = new SKRect(10, 10, 110, 30);
        Assert.Equal(8, Handles.Layout(bar, 6).Count());
        Assert.Equal(4, Handles.Layout(text, 6).Count());
        Assert.True(Handles.IsFree(Widget.Create(WidgetKind.Gauge)));
        Assert.False(Handles.IsFree(Widget.Create(WidgetKind.Clock)));
    }

    [Fact]
    public void Hit_testing_finds_the_handle_under_a_point_with_tolerance()
    {
        var w = Widget.Create(WidgetKind.Panel); w.LastBounds = new SKRect(10, 10, 110, 60);
        Assert.Equal(Handle.TopLeft, Handles.HitTest(w, 10, 10, 6, 3));
        Assert.Equal(Handle.BottomRight, Handles.HitTest(w, 112, 62, 6, 3));
        Assert.Equal(Handle.Right, Handles.HitTest(w, 110, 35, 6, 0));
        Assert.Equal(Handle.None, Handles.HitTest(w, 60, 35, 6, 3));
    }

    [Fact]
    public void A_widget_that_was_never_drawn_has_no_handles()
    {
        var w = Widget.Create(WidgetKind.Bar);   // LastBounds is empty
        Assert.Empty(Handles.Layout(w, 6));
    }
}

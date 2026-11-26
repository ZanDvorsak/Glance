using System.Text;
using Glance.Core.Import;
using Glance.Core.Sensors;
using Glance.Core.Themes;
using SkiaSharp;

namespace Glance.Tests;

public class AidaSensorMapTests
{
    private static readonly AidaSensorMap Map = new(Fake.Typical());

    [Theory]
    [InlineData("TCPU", "/amdcpu/0/temperature/2")]
    [InlineData("TGPU1", "/gpu-nvidia/0/temperature/0")]
    [InlineData("TGPU1HOT", "/gpu-nvidia/0/temperature/2")]
    [InlineData("TGPU1MEM", "/gpu-nvidia/0/temperature/3")]
    [InlineData("SCPUUTI", "/amdcpu/0/load/0")]
    [InlineData("SCPU3UTI", "/amdcpu/0/load/3")]
    [InlineData("SCPUCLK", "/amdcpu/0/clock/1")]
    [InlineData("SGPU1UTI", "/gpu-nvidia/0/load/0")]
    [InlineData("SGPU1CLK", "/gpu-nvidia/0/clock/0")]
    [InlineData("SGPU1MEMCLK", "/gpu-nvidia/0/clock/1")]
    [InlineData("SVMEMUSAGE", "/gpu-nvidia/0/load/3")]
    [InlineData("SMEMUTI", "/ram/load/0")]
    [InlineData("FCPU", "/lpc/nct6798d/0/fan/1")]
    [InlineData("SDRVCUTI", "/drive/c/load/0")]
    [InlineData("SDRVCUSEDSPC", "/drive/c/data/0")]
    [InlineData("SDRVCFREESPC", "/drive/c/data/1")]
    public void AIDA64_item_codes_map_to_the_right_sensor(string code, string expectedId) =>
        Assert.Equal(expectedId, Map.Resolve(code, "")?.Sensor?.Id);

    [Fact]
    public void Codes_are_case_insensitive_and_unknown_codes_are_not_mapped()
    {
        Assert.NotNull(Map.Resolve("tcpu", ""));
        Assert.Null(Map.Resolve("SNOTREAL", ""));
        Assert.Null(Map.Resolve("SDRVZUTI", ""));   // no drive Z
    }

    [Fact]
    public void Units_are_converted_between_what_AIDA_shows_and_what_the_sensor_reports()
    {
        Assert.Equal(1024.0, Map.Resolve("SUSEDMEM", " MB")!.Multiplier);          // GB -> MB
        Assert.Equal(1.0, Map.Resolve("SUSEDMEM", " GB")!.Multiplier);
        Assert.Equal(1.0 / 1024, Map.Resolve("SNIC1DLRATE", "KB/s")!.Multiplier, 6);   // B/s -> KB/s
        Assert.Equal(1.0, Map.Resolve("TCPU", "°C")!.Multiplier);
    }

    [Theory]
    [InlineData("SDATE", "d")]
    [InlineData("STIME", "HH:mm:ss")]
    [InlineData("SDAY", "dddd")]
    public void Date_and_time_items_become_clocks(string code, string format) => Assert.Equal(format, AidaSensorMap.ClockFormat(code));
}

public class AidaImporterTests
{
    private static string TinyPngHex()
    {
        using var bmp = new SKBitmap(2, 2); bmp.Erase(SKColors.Red);
        using var img = SKImage.FromBitmap(bmp);
        return Convert.ToHexString(img.Encode(SKEncodedImageFormat.Png, 100).ToArray());
    }

    /// <summary>A small but representative .axlcd: image, static label, simple temperature, boxed value, bar, date, an unknown item, and a second page.</summary>
    private static string SampleTheme() => string.Join("\r\n", new[]
    {
        "<LCDVER>200</LCDVER><SWVER>7.20.6802</SWVER>",
        "<LCDBGCOLOR>16777215</LCDBGCOLOR>",
        "<LCDPAGE1>",
        $" <ID>IMG</ID><URL></URL><ITMX>-10</ITMX><ITMY>5</ITMY><IMGFIL>bg.png</IMGFIL><IMGDAT>{TinyPngHex()}</IMGDAT>",
        " <ID>LBL</ID><TXTSIZ>12</TXTSIZ><FNTNAM>Arial</FNTNAM><LBL>Temperature:</LBL><LBLCOL>33023</LBLCOL><ITMX>11</ITMX><ITMY>90</ITMY>",
        " <ID>[SIMPLE]TCPU</ID><TXTSIZ>30</TXTSIZ><FNTNAM></FNTNAM><TXTBIR>100</TXTBIR><SHWLBL>0</SHWLBL><LBL>CPU</LBL><SHWUNT>1</SHWUNT><UNT>°C</UNT><TXTCOL>16777215</TXTCOL><ITMX>33</ITMX><ITMY>112</ITMY>",
        " <ID>SCPUUTI</ID><WID>160</WID><TXTSIZ>9</TXTSIZ><FNTNAM>Tahoma</FNTNAM><SHWLBL>0</SHWLBL><LBL>CPU Utilization</LBL><SHWVAL>1</SHWVAL><VALCOL>16777215</VALCOL><SHWUNT>1</SHWUNT><UNT>%</UNT><UNTWID>40</UNTWID><SHWBAR>0</SHWBAR><UNTCOL>207096</UNTCOL><ITMX>297</ITMX><ITMY>250</ITMY>",
        " <ID>SMEMUTI</ID><WID>160</WID><TXTSIZ>9</TXTSIZ><SHWLBL>0</SHWLBL><LBL>Memory Utilization</LBL><SHWVAL>0</SHWVAL><SHWUNT>0</SHWUNT><SHWBAR>1</SHWBAR><BARWID>165</BARWID><BARHEI>15</BARHEI><BARMIN>0</BARMIN><BARMAX>100</BARMAX><BARMINFGC>65280</BARMINFGC><BARMINBGC>12632256</BARMINBGC><ITMX>303</ITMX><ITMY>200</ITMY>",
        " <ID>SDATE</ID><TXTSIZ>12</TXTSIZ><SHWLBL>0</SHWLBL><TXTCOL>33023</TXTCOL><ITMX>376</ITMX><ITMY>5</ITMY>",
        " <ID>SNOTREAL</ID><WID>100</WID><TXTSIZ>9</TXTSIZ><SHWLBL>0</SHWLBL><LBL>Unknown thing</LBL><SHWVAL>1</SHWVAL><ITMX>1</ITMX><ITMY>1</ITMY>",
        "</LCDPAGE1>",
        "<LCDPAGE2>",
        " <ID>LBL</ID><TXTSIZ>20</TXTSIZ><FNTNAM>Arial</FNTNAM><LBL>Second page</LBL><LBLCOL>0</LBLCOL><ITMX>40</ITMX><ITMY>40</ITMY>",
        "</LCDPAGE2>",
    });

    private static (AidaImportResult result, TempDir dir) Import()
    {
        var dir = new TempDir();
        var file = dir.Combine("sample.axlcd");
        File.WriteAllText(file, SampleTheme(), Encoding.Latin1);   // AIDA files are ANSI, so the degree sign is one byte
        return (AidaImporter.Import(file, dir.Combine("out"), Fake.Typical()), dir);
    }

    [Fact]
    public void Pages_background_and_counts_come_through()
    {
        var (r, dir) = Import();
        using (dir)
        {
            Assert.Equal(2, r.Theme.Pages.Count);
            Assert.Equal("#FFFFFFFF", r.Theme.BackgroundColor);                     // 16777215 = white
            Assert.Equal(1, r.Theme.Pages[1].Widgets.Count);
            Assert.Equal(1, r.Unmapped);                                             // only SNOTREAL
            Assert.Contains(r.Notes, n => n.Contains("SNOTREAL"));
        }
    }

    [Fact]
    public void The_embedded_picture_is_unpacked_and_placed()
    {
        var (r, dir) = Import();
        using (dir)
        {
            var img = r.Theme.Pages[0].Widgets.First(w => w.Kind == WidgetKind.Image);
            Assert.Equal((-10.0, 5.0, 2.0, 2.0), (img.X, img.Y, img.Width, img.Height));
            Assert.Equal("assets/bg.png", img.ImagePath);
            Assert.True(File.Exists(dir.Combine("out", "assets", "bg.png")));
        }
    }

    [Fact]
    public void Colours_are_converted_from_BGR_and_point_sizes_to_pixels()
    {
        var (r, dir) = Import();
        using (dir)
        {
            var label = r.Theme.Pages[0].Widgets.First(w => w.Kind == WidgetKind.Text && w.Text == "Temperature:");
            Assert.Equal("#FFFF8000", label.Color);        // 33023 = 0x0080FF in BGR = orange
            Assert.Equal(16.0, label.FontSize);            // 12 pt at 96 dpi
            Assert.Equal("Arial", label.FontFamily);
        }
    }

    [Fact]
    public void A_simple_temperature_item_becomes_a_big_bold_sensor_widget_bound_to_the_real_sensor()
    {
        var (r, dir) = Import();
        using (dir)
        {
            var temp = r.Theme.Pages[0].Widgets.First(w => w.Kind == WidgetKind.Sensor && w.SensorId == "/amdcpu/0/temperature/2");
            Assert.Equal((40.0, true, "°C"), (temp.FontSize, temp.Bold, temp.Unit));
            Assert.Equal((33.0, 112.0), (temp.X, temp.Y));
        }
    }

    [Fact]
    public void A_boxed_value_is_right_aligned_with_its_unit_in_a_fixed_field()
    {
        var (r, dir) = Import();
        using (dir)
        {
            var value = r.Theme.Pages[0].Widgets.First(w => w.Kind == WidgetKind.Sensor && w.SensorId == "/amdcpu/0/load/0");
            Assert.Equal(TextAlign.Right, value.Align);
            Assert.Equal(297 + 160 - 40, value.X);         // item x + width - unit field
            var unit = r.Theme.Pages[0].Widgets.First(w => w.Kind == WidgetKind.Text && w.Name.EndsWith(" unit"));
            Assert.Equal((value.X, "%"), (unit.X, unit.Text.Trim()));
        }
    }

    [Fact]
    public void A_bar_item_keeps_its_size_range_and_colours()
    {
        var (r, dir) = Import();
        using (dir)
        {
            var bar = r.Theme.Pages[0].Widgets.First(w => w.Kind == WidgetKind.Bar);
            Assert.Equal((165.0, 15.0, 0.0, 100.0), (bar.Width, bar.Height, bar.Min, bar.Max));
            Assert.Equal("#FF00FF00", bar.Color);          // 65280 = green
            Assert.Equal("/ram/load/0", bar.SensorId);
        }
    }

    [Fact]
    public void Date_items_become_clock_widgets_and_unknown_items_stay_visible_but_unbound()
    {
        var (r, dir) = Import();
        using (dir)
        {
            Assert.Contains(r.Theme.Pages[0].Widgets, w => w.Kind == WidgetKind.Clock && w.Text == "d");
            var unknown = r.Theme.Pages[0].Widgets.First(w => w.Kind == WidgetKind.Sensor && w.Name == "Unknown thing");
            Assert.Equal("", unknown.SensorId);
        }
    }

    [Fact]
    public void Importing_with_no_sensors_still_produces_a_theme()
    {
        using var dir = new TempDir();
        var file = dir.Combine("s.axlcd"); File.WriteAllText(file, SampleTheme(), Encoding.Latin1);
        var r = AidaImporter.Import(file, dir.Combine("out"), new List<SensorInfo>());
        Assert.Equal(2, r.Theme.Pages.Count);
        Assert.True(r.Unmapped >= 4);
    }
}

public class RendererTests
{
    private static SensorInfo Value(string id, float v) => new(id, "t", id, "Load", v, "%");

    private static SKColor Pixel(Theme t, ISensorSource? s, int x, int y)
    {
        using var bmp = Fake.Render(t, s);
        return bmp.GetPixel(x, y);
    }

    [Fact]
    public void A_panel_fills_its_rectangle_and_the_background_shows_elsewhere()
    {
        var t = Fake.Canvas();
        t.Widgets.Add(new Widget { Kind = WidgetKind.Panel, X = 10, Y = 10, Width = 80, Height = 80, Color = "#FFFF0000", CornerRadius = 0 });
        using var bmp = Fake.Render(t, null);
        Assert.True(Fake.Near(bmp.GetPixel(50, 50), 255, 0, 0));
        Assert.True(Fake.Near(bmp.GetPixel(2, 2), 0, 0, 0));
    }

    [Fact]
    public void A_page_can_override_the_theme_background()
    {
        var t = Fake.Canvas();
        t.Pages[0].BackgroundColor = "#FF112233";
        Assert.True(Fake.Near(Pixel(t, null, 50, 50), 0x11, 0x22, 0x33));
    }

    [Fact]
    public void Hidden_widgets_are_not_drawn()
    {
        var t = Fake.Canvas();
        t.Widgets.Add(new Widget { Kind = WidgetKind.Panel, X = 0, Y = 0, Width = 100, Height = 100, Color = "#FFFF0000", Visible = false });
        Assert.True(Fake.Near(Pixel(t, null, 50, 50), 0, 0, 0));
    }

    private static Theme BarTheme(float value, Action<Widget>? tweak = null, bool vertical = false)
    {
        var t = Fake.Canvas();
        var bar = vertical
            ? new Widget { Kind = WidgetKind.Bar, X = 40, Y = 10, Width = 20, Height = 80, Vertical = true }
            : new Widget { Kind = WidgetKind.Bar, X = 10, Y = 40, Width = 80, Height = 20 };
        bar.SensorId = "v"; bar.Min = 0; bar.Max = 100; bar.CornerRadius = 0; bar.Color = "#FF00FF00"; bar.BackColor = "#FF404040";
        tweak?.Invoke(bar);
        t.Widgets.Add(bar);
        return t;
    }

    [Fact]
    public void A_horizontal_bar_fills_from_the_left_in_proportion_to_the_value()
    {
        var src = new FakeSource(new[] { Value("v", 50) });
        using var bmp = Fake.Render(BarTheme(50), src);
        Assert.True(Fake.Near(bmp.GetPixel(30, 50), 0, 255, 0), "left half is filled");
        Assert.True(Fake.Near(bmp.GetPixel(70, 50), 0x40, 0x40, 0x40), "right half is the track");
    }

    [Fact]
    public void A_vertical_bar_fills_from_the_bottom_up()
    {
        var src = new FakeSource(new[] { Value("v", 25) });
        using var bmp = Fake.Render(BarTheme(25, vertical: true), src);
        Assert.True(Fake.Near(bmp.GetPixel(50, 85), 0, 255, 0), "bottom is filled");
        Assert.True(Fake.Near(bmp.GetPixel(50, 15), 0x40, 0x40, 0x40), "top is the track");
    }

    [Fact]
    public void The_multiplier_scales_the_value_before_it_is_drawn()
    {
        var src = new FakeSource(new[] { Value("v", 50) });
        using var bmp = Fake.Render(BarTheme(50, b => b.Multiplier = 2), src);   // 50 x 2 = 100 %
        Assert.True(Fake.Near(bmp.GetPixel(85, 50), 0, 255, 0));
    }

    [Theory]
    [InlineData(40f, 0, 255, 0)]       // below both levels: normal colour
    [InlineData(80f, 255, 193, 7)]     // at or above the warning level
    [InlineData(95f, 255, 82, 82)]     // at or above the critical level
    public void Thresholds_switch_the_colour_of_a_bar(float value, byte r, byte g, byte b)
    {
        var src = new FakeSource(new[] { Value("v", value) });
        using var bmp = Fake.Render(BarTheme(value, w => { w.WarnAt = 75; w.CritAt = 90; w.Max = 100; w.Min = 0; w.Width = 80; w.CornerRadius = 0; }), src);
        Assert.True(Fake.Near(bmp.GetPixel(15, 50), r, g, b), $"expected ({r},{g},{b}) but got {bmp.GetPixel(15, 50)}");
    }

    [Fact]
    public void A_missing_sensor_draws_an_empty_track_not_a_crash()
    {
        using var bmp = Fake.Render(BarTheme(0), new FakeSource(Array.Empty<SensorInfo>()));
        Assert.True(Fake.Near(bmp.GetPixel(30, 50), 0x40, 0x40, 0x40));
    }

    [Fact]
    public void Text_widgets_report_bounds_for_hit_testing_and_draw_pixels()
    {
        var t = Fake.Canvas(200, 60);
        var w = new Widget { Kind = WidgetKind.Text, X = 10, Y = 10, Text = "Hello", FontSize = 24, Color = "#FFFFFFFF" };
        t.Widgets.Add(w);
        using var bmp = Fake.Render(t, null);
        Assert.True(w.LastBounds.Width > 20 && w.LastBounds.Height > 10);
        Assert.Contains(Enumerable.Range(0, 200).SelectMany(x => Enumerable.Range(0, 60).Select(y => bmp.GetPixel(x, y))), c => c.Red > 128);
    }

    [Theory]
    [InlineData(TextAlign.Left)]
    [InlineData(TextAlign.Center)]
    [InlineData(TextAlign.Right)]
    public void Text_alignment_anchors_the_bounds_on_the_chosen_side(TextAlign align)
    {
        var t = Fake.Canvas(300, 60);
        var w = new Widget { Kind = WidgetKind.Text, X = 150, Y = 10, Text = "Anchor", FontSize = 20, Align = align };
        t.Widgets.Add(w);
        using var bmp = Fake.Render(t, null);
        var b = w.LastBounds;
        switch (align)
        {
            case TextAlign.Left: Assert.Equal(150, b.Left, 1); break;
            case TextAlign.Right: Assert.Equal(150, b.Right, 1); break;
            default: Assert.Equal(150, b.MidX, 1); break;
        }
    }

    [Fact]
    public void Gauges_and_graphs_draw_something_and_a_gauge_arc_changes_with_the_value()
    {
        int Count(SKBitmap b) { int n = 0; for (int x = 0; x < b.Width; x++) for (int y = 0; y < b.Height; y++) if (b.GetPixel(x, y).Green > 100) n++; return n; }

        SKBitmap Gauge(float v)
        {
            var t = Fake.Canvas(120, 120);
            t.Widgets.Add(new Widget { Kind = WidgetKind.Gauge, X = 5, Y = 5, Width = 110, Height = 110, SensorId = "v", Min = 0, Max = 100, Color = "#FF00FF00", BackColor = "#00000000", TextColor = "#00000000", Thickness = 10, Text = "" });
            return Fake.Render(t, new FakeSource(new[] { Value("v", v) }));
        }
        using var low = Gauge(10); using var high = Gauge(90);
        Assert.True(Count(high) > Count(low) * 2, "a fuller gauge lights more pixels");

        var g = Fake.Canvas(200, 80);
        g.Widgets.Add(new Widget { Kind = WidgetKind.Graph, X = 5, Y = 5, Width = 190, Height = 70, SensorId = "v", Min = 0, Max = 100, Color = "#FF00FF00", ShowGrid = false });
        using var renderer = new ThemeRenderer();
        var src = new FakeSource(new[] { Value("v", 60) });
        renderer.SeedGraphHistory(g.ActivePage, src);
        using var bmp = Fake.Render(g, src, renderer: renderer);
        Assert.True(Count(bmp) > 100, "the graph draws a line from seeded history");
    }

    [Fact]
    public void Missing_images_and_broken_paths_draw_a_placeholder_instead_of_throwing()
    {
        var t = Fake.Canvas();
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, X = 0, Y = 0, Width = 100, Height = 100, ImagePath = "does/not/exist.png" });
        using var bmp = Fake.Render(t, null);
        Assert.NotEqual(new SKColor(0, 0, 0, 255), bmp.GetPixel(50, 50));
    }

    [Fact]
    public void A_whole_theme_of_every_widget_kind_renders_without_throwing()
    {
        var t = new Theme();
        foreach (var kind in Enum.GetValues<WidgetKind>()) { var w = Widget.Create(kind, 10, 10); w.SensorId = "/amdcpu/0/load/0"; t.Widgets.Add(w); }
        using var bmp = Fake.Render(t, new FakeSource(Fake.Typical()));
        Assert.Equal(480, bmp.Width);
    }
}

public class SensorPipeTests
{
    private static async Task<bool> WaitFor(Func<bool> condition, int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until) { if (condition()) return true; await Task.Delay(50); }
        return condition();
    }

    [Fact]
    public async Task A_client_receives_what_the_server_publishes_and_drops_it_when_the_server_stops()
    {
        var name = "glance-test-" + Guid.NewGuid().ToString("N");   // never the real pipe name: a runner may be running on this PC
        var server = new SensorPipeServer(name);
        server.Start();
        server.Publish(new[] { new SensorInfo("/a", "hw", "A", "Load", 12.5f, "%"), new SensorInfo("/b", "hw", "B", "Fan", 800, "RPM") });
        using var client = new PipeSensorClient(name, staleAfter: TimeSpan.FromSeconds(2), trustedOwners: new[] { System.Security.Principal.WindowsIdentity.GetCurrent().User! });   // the test server runs as this user, not as the service

        Assert.True(await WaitFor(() => client.HasData, 6000), "the client never received data");
        Assert.Equal(new[] { "/a", "/b" }, client.Snapshot.Select(s => s.Id).ToArray());
        Assert.Equal(12.5f, client.Snapshot[0].Value);

        server.Publish(new[] { new SensorInfo("/a", "hw", "A", "Load", 99f, "%") });
        Assert.True(await WaitFor(() => client.Snapshot.Count == 1 && client.Snapshot[0].Value == 99f, 6000), "updates did not arrive");

        server.Dispose();
        Assert.True(await WaitFor(() => !client.HasData, 8000), "stale data was still reported as live after the server stopped");
        Assert.Empty(client.Snapshot);
    }

    [Fact]
    public async Task A_connected_but_silent_sender_is_reported_as_stale()
    {
        // the connection stays open but no new snapshots arrive (a hung runner): old values must not look live
        var name = "glance-test-" + Guid.NewGuid().ToString("N");
        using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.Out);
        using var client = new PipeSensorClient(name, staleAfter: TimeSpan.FromMilliseconds(800), trustedOwners: new[] { System.Security.Principal.WindowsIdentity.GetCurrent().User! });
        await server.WaitForConnectionAsync(new CancellationTokenSource(6000).Token);
        using var writer = new StreamWriter(server) { AutoFlush = true };
        await writer.WriteLineAsync("[{\"Id\":\"/a\",\"Hardware\":\"hw\",\"Name\":\"A\",\"Type\":\"Load\",\"Value\":1,\"Unit\":\"%\"}]");

        Assert.True(await WaitFor(() => client.HasData, 4000), "the first snapshot never arrived");
        Assert.True(await WaitFor(() => !client.HasData, 4000), "values kept looking live although nothing new arrived");
        Assert.True(server.IsConnected, "the test is only meaningful while the connection is still open");
    }

    [Fact]
    public void A_client_with_no_server_simply_has_no_data()
    {
        using var client = new PipeSensorClient("glance-test-nobody-" + Guid.NewGuid().ToString("N"));
        Assert.False(client.HasData);
        Assert.Empty(client.Snapshot);
    }
}

using System.IO.Compression;
using System.Reflection;
using System.Text;
using Glance.Core.Presets;
using Glance.Core.Themes;

namespace Glance.Tests;

/// <summary>
/// M2: paths inside a shared theme's theme.json (UNC, device, absolute, "..", outside assets/) are removed on import,
/// and the renderer never opens network or device paths.
/// </summary>
public class ImportedThemePathTests
{
    private static string MakePackage(TempDir dir, Action<Theme> fill, params string[] assets)
    {
        var t = new Theme { Name = "shared" };
        fill(t);
        var path = dir.Combine("shared.glance");
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        Put(z, ThemePackage.ThemeEntry, t.ToSnapshot());
        foreach (var a in assets) Put(z, a, "x");
        return path;
    }

    private static void Put(ZipArchive z, string entry, string content)
    {
        using var w = new StreamWriter(z.CreateEntry(entry).Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    public static IEnumerable<object[]> BadPaths() => new[]
    {
        @"\\attacker\share\x.png",          // UNC: Windows would send the user's credentials to that machine
        "//attacker/share/x.png",           // UNC written with forward slashes
        @"\\?\UNC\attacker\share\x.png",    // UNC in long-path form
        @"\\.\PhysicalDrive0",              // device path
        @"\\?\C:\Windows\win.ini",          // long-path form of a local file
        @"C:\Windows\win.ini",              // absolute local path: re-saving or re-exporting would copy it
        @"\Windows\win.ini",                // rooted on the current drive
        @"C:secret.png",                    // drive-relative
        @"assets\..\..\secret.png",         // climbs out of the theme folder
        "../secret.png",
        "assets/../theme.json",             // ".." that climbs back to theme.json (outside assets/)
        "assets/sub/../a.png",              // ".." that still ends inside assets/: only the ".." rule removes it
        "theme.json",                       // relative, but not inside assets/
        "assets/a.png:hidden",              // alternate data stream
        "assets",                           // the folder itself
    }.Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(BadPaths))]
    public void A_bad_image_path_inside_theme_json_is_removed_on_import(string bad)
    {
        using var dir = new TempDir();
        var package = MakePackage(dir, t => t.Widgets.Add(new Widget { Kind = WidgetKind.Image, Name = "Logo", ImagePath = bad }));
        var themeFile = ThemePackage.Import(package, dir.Combine("imported"));

        Assert.Equal("", Theme.Load(themeFile).Widgets[0].ImagePath);
        Assert.DoesNotContain(bad.Replace(@"\", @"\\"), File.ReadAllText(themeFile));   // the cleaned theme was saved
    }

    [Theory]
    [MemberData(nameof(BadPaths))]
    public void A_bad_font_path_inside_theme_json_is_removed_on_import(string bad)
    {
        using var dir = new TempDir();
        var package = MakePackage(dir, t => { t.Pages.Add(new ThemePage { Name = "Two" }); t.Pages[1].Widgets.Add(new Widget { Kind = WidgetKind.Text, FontFile = bad }); });
        var themeFile = ThemePackage.Import(package, dir.Combine("imported"));

        Assert.Equal("", Theme.Load(themeFile).Pages[1].Widgets[0].FontFile);
    }

    [Fact]
    public void Good_paths_are_kept_and_the_theme_file_is_left_as_it_was()
    {
        using var dir = new TempDir();
        var package = MakePackage(dir, t =>
        {
            t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/bg.png" });
            t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = @"assets\sub\pic.png" });
            t.Widgets.Add(new Widget { Kind = WidgetKind.Text, FontFile = "assets/font.ttf" });
            t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/missing.png" });   // missing but harmless
        }, "assets/bg.png", "assets/sub/pic.png", "assets/font.ttf");
        var target = dir.Combine("imported");
        var themeFile = ThemePackage.Import(package, target);

        string original;
        using (var z = ZipFile.OpenRead(package)) using (var r = new StreamReader(z.GetEntry(ThemePackage.ThemeEntry)!.Open())) original = r.ReadToEnd();
        Assert.Equal(original, File.ReadAllText(themeFile));
        var back = Theme.Load(themeFile);
        Assert.Equal(new[] { "assets/bg.png", @"assets\sub\pic.png", "", "assets/missing.png" }, back.Widgets.Select(w => w.ImagePath));
        Assert.Equal("assets/font.ttf", back.Widgets[2].FontFile);
    }

    [Theory]
    [InlineData(@"\\attacker\share\x.png")]
    [InlineData("//attacker/share/x.png")]
    [InlineData(@"\\?\UNC\attacker\share\x.png")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\C:\Windows\win.ini")]
    [InlineData(@"\??\C:\Windows\win.ini")]
    public void The_renderer_never_resolves_network_or_device_paths(string path)
    {
        var t = new Theme { BaseFolder = @"C:\Themes\x" };
        Assert.Null(t.ResolveAsset(path));
    }

    [Fact]
    public void Local_absolute_and_relative_paths_still_resolve()
    {
        var t = new Theme { BaseFolder = @"C:\Themes\x" };
        Assert.Equal(@"D:\Pictures\bg.png", t.ResolveAsset(@"D:\Pictures\bg.png"));
        Assert.Equal(@"C:\Themes\x\assets/bg.png", t.ResolveAsset("assets/bg.png"));
    }
}

/// <summary>M3: Theme.Normalize makes any parseable theme file safe to edit and render.</summary>
public class ThemeNormalizeTests
{
    private static Theme LoadJson(TempDir dir, string json)
    {
        var p = dir.Combine("theme.json");
        File.WriteAllText(p, json);
        return Theme.Load(p);
    }

    private static void RenderAllPages(Theme t)
    {
        foreach (var page in t.Pages)
        {
            using var bmp = Fake.Render(t, new FakeSource(Fake.Typical()), page);
        }
    }

    private static IEnumerable<PropertyInfo> Props<T>(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(T) && p.CanWrite && p.GetIndexParameters().Length == 0);

    [Fact]
    public void Null_pages_list_gives_one_empty_page()
    {
        using var dir = new TempDir();
        var t = LoadJson(dir, """{ "Name": "x", "Pages": null }""");
        Assert.Single(t.Pages);
        Assert.Empty(t.Widgets);
    }

    [Fact]
    public void Null_pages_widget_lists_and_widgets_are_dropped_or_replaced()
    {
        using var dir = new TempDir();
        var t = LoadJson(dir, """
            { "Pages": [ null, { "Name": "A", "Widgets": null }, { "Name": "B", "Widgets": [ null, { "Kind": "Text", "Text": "hi" }, null ] } ],
              "Widgets": [ null ] }
            """);
        Assert.Equal(new[] { "A", "B" }, t.Pages.Select(p => p.Name));
        Assert.NotNull(t.Pages[0].Widgets);
        Assert.Empty(t.Pages[0].Widgets);
        Assert.Single(t.Pages[1].Widgets);
        Assert.Equal("hi", t.Pages[1].Widgets[0].Text);
        RenderAllPages(t);
    }

    [Fact]
    public void A_null_sensor_id_no_longer_breaks_rendering()
    {
        using var dir = new TempDir();
        var t = LoadJson(dir, """{ "Pages": [ { "Widgets": [ { "Kind": "Sensor", "SensorId": null, "Format": null, "Unit": null } ] } ] }""");
        RenderAllPages(t);
        Assert.Equal("", t.Widgets[0].SensorId);
    }

    [Fact]
    public void Null_strings_become_their_defaults_and_every_kind_still_renders()
    {
        using var dir = new TempDir();
        var nulls = string.Join(", ", Props<string>(typeof(Widget)).Select(p => $"\"{p.Name}\": null"));
        var widgets = string.Join(", ", Enum.GetNames<WidgetKind>().Select(k => $"{{ \"Kind\": \"{k}\", {nulls} }}"));
        var t = LoadJson(dir, $$"""{ "Name": null, "BackgroundColor": null, "Pages": [ { "Name": null, "Widgets": [ {{widgets}} ] } ] }""");

        Assert.Equal("Untitled", t.Name);
        Assert.Equal("#FF000000", t.BackgroundColor);
        Assert.False(string.IsNullOrEmpty(t.Pages[0].Name));
        var fresh = new Widget();
        foreach (var w in t.Widgets)
            foreach (var p in Props<string>(typeof(Widget)))
            {
                Assert.NotNull(p.GetValue(w));
                if (p.Name is not ("Id" or "Text")) Assert.Equal(p.GetValue(fresh), p.GetValue(w));
            }
        Assert.Equal("", t.Widgets[0].Text);                     // a null text means no text, not the word "Text"
        Assert.Equal(t.Widgets.Count, t.Widgets.Select(w => w.Id).Distinct().Count());   // each null id got its own
        RenderAllPages(t);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, -1)]
    [InlineData(100000, 320)]
    [InlineData(480, 2147483647)]
    public void Screen_size_is_clamped_to_1_to_4096(int w, int h)
    {
        using var dir = new TempDir();
        var t = LoadJson(dir, $$"""{ "Width": {{w}}, "Height": {{h}} }""");
        Assert.InRange(t.Width, 1, 4096);
        Assert.InRange(t.Height, 1, 4096);
        Assert.Equal(Math.Clamp(w, 1, 4096), t.Width);
        Assert.Equal(Math.Clamp(h, 1, 4096), t.Height);
        RenderAllPages(t);
    }

    [Fact]
    public void Extreme_numbers_are_clamped_so_the_property_panel_and_renderer_cope()
    {
        using var dir = new TempDir();
        var huge = string.Join(", ", Props<double>(typeof(Widget)).Select(p => $"\"{p.Name}\": 1e300"));
        var tiny = string.Join(", ", Props<double>(typeof(Widget)).Select(p => $"\"{p.Name}\": -1e300"));
        var t = LoadJson(dir, $$"""
            { "Pages": [ { "DurationSeconds": 1e300, "Widgets": [ { "Kind": "Gauge", {{huge}} }, { "Kind": "Graph", {{tiny}} },
                                                                 { "Kind": "Text", {{huge}} }, { "Kind": "Bar", {{tiny}} } ] },
                         { "DurationSeconds": -1e300, "Widgets": [ { "Kind": "Panel", {{huge}} }, { "Kind": "Sensor", {{tiny}} } ] } ] }
            """);

        foreach (var page in t.Pages)
        {
            _ = Convert.ToDecimal(page.DurationSeconds);   // MainWindow shows it in a NumericUpDown
            Assert.InRange(page.DurationSeconds, 1, 86400);
            foreach (var w in page.Widgets)
            {
                foreach (var p in Props<double>(typeof(Widget)))
                {
                    var v = (double)p.GetValue(w)!;
                    Assert.True(double.IsFinite(v), p.Name);
                    _ = Convert.ToDecimal(v);              // PropertiesPanel.Num: decimal overflow crashed the editor
                }
                Assert.InRange(w.FontSize, 1, 1000);
                Assert.InRange(w.Width, 0, 8192);
                Assert.InRange(w.Height, 0, 8192);
                Assert.InRange(w.Thickness, 0, 1000);
                Assert.InRange(w.CornerRadius, 0, 4096);
                Assert.InRange(w.Opacity, 0, 100);
                Assert.InRange(w.HistorySeconds, 1, 86400);
                Assert.InRange(w.Sweep, -360, 360);
            }
        }
        RenderAllPages(t);
    }

    [Fact]
    public void A_live_or_undo_snapshot_is_normalized_too()
    {
        // the runner's LIVE command and the editor's undo both go through ApplySnapshot
        var t = new Theme();
        t.ApplySnapshot("""{ "Width": 0, "Height": 99999, "Name": null, "Pages": null }""");
        Assert.Equal((1, 4096), (t.Width, t.Height));
        Assert.Equal("Untitled", t.Name);
        Assert.Single(t.Pages);
        t.ApplySnapshot("""{ "Pages": [ { "Widgets": [ null, { "Kind": "Sensor", "SensorId": null, "FontSize": 1e300 } ] } ] }""");
        Assert.Single(t.Widgets);
        Assert.Equal(1000, t.Widgets[0].FontSize);
        RenderAllPages(t);
    }

    [Fact]
    public void Unknown_enum_numbers_fall_back_to_the_defaults()
    {
        using var dir = new TempDir();
        var t = LoadJson(dir, """{ "Pages": [ { "Widgets": [ { "Kind": 99, "Align": 42, "Fit": -3 } ] } ] }""");
        var w = t.Widgets[0];
        Assert.Equal(WidgetKind.Text, w.Kind);
        Assert.Equal(TextAlign.Left, w.Align);
        Assert.Equal(Glance.Core.Media.ImageFit.Stretch, w.Fit);
    }

    public static IEnumerable<object[]> ValidThemes()
    {
        yield return new object[] { "default", Theme.CreateDefault(@"C:\pics\bg.png") };
        foreach (var tpl in ThemeTemplates.All) yield return new object[] { tpl.Id, tpl.Build(Fake.Typical(), new PresetOptions("#FF40C4FF", PanelStyle.Glass)) };

        // the edges of every range the editor allows (PropertiesPanel and the page duration box)
        var edge = new Theme { Name = "edges", Width = 4096, Height = 1, BackgroundColor = "#00112233", AutoRotate = false };
        edge.Pages[0].DurationSeconds = 3600; edge.Pages[0].BackgroundColor = "#FF102030";
        edge.Widgets.Add(new Widget { Kind = WidgetKind.Gauge, X = -2000, Y = 4000, Width = 4000, Height = 4, FontSize = 400, Thickness = 200,
            StartAngle = -360, Sweep = 360, Min = -100000, Max = 100000, Multiplier = -1000000, WarnAt = 1000000, CritAt = 0, Opacity = 0, CornerRadius = 200 });
        edge.Widgets.Add(new Widget { Kind = WidgetKind.Graph, Width = 1, Height = 1, FontSize = 4, Thickness = 0.5, HistorySeconds = 900, Sweep = 10, Opacity = 100 });
        edge.Widgets.Add(new Widget { Kind = WidgetKind.Text, Text = "", SensorId = "", FontFile = "", ImagePath = "", Width = 0, Height = 0, X = 0.25, Y = -0.75 });
        edge.Pages.Add(new ThemePage { Name = "short", DurationSeconds = 1 });
        yield return new object[] { "edges", edge };
    }

    [Theory]
    [MemberData(nameof(ValidThemes))]
    public void Valid_themes_load_unchanged(string name, Theme theme)
    {
        using var dir = new TempDir();
        var json = theme.ToSnapshot();
        var back = LoadJson(dir, json);
        Assert.True(json == back.ToSnapshot(), name + " changed on load");
    }
}

/// <summary>Tests that use the API added for M2/M3 (the import report and the internal Normalize).</summary>
public class ThemeInputApiTests
{
    [Fact]
    public void Import_reports_each_removed_path_with_page_widget_and_reason()
    {
        using var dir = new TempDir();
        var t = new Theme { Name = "shared" };
        t.Pages[0].Name = "Main";
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, Name = "Logo", ImagePath = @"\\attacker\share\x.png", FontFile = @"C:\Windows\Fonts\arial.ttf" });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, Name = "Fine", ImagePath = "assets/ok.png" });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Text, Name = "Up", FontFile = "../x.ttf" });
        var package = dir.Combine("p.glance");
        using (var z = ZipFile.Open(package, ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry(ThemePackage.ThemeEntry).Open())) w.Write(t.ToSnapshot());

        ThemePackage.Import(package, dir.Combine("imp"), out var removed);

        Assert.Equal(3, removed.Count);
        Assert.Equal(new RemovedAssetPath("Main", "Logo", RemovedAssetPath.Picture, @"\\attacker\share\x.png", "network or device path"), removed[0]);
        Assert.Equal(new RemovedAssetPath("Main", "Logo", RemovedAssetPath.Font, @"C:\Windows\Fonts\arial.ttf", "absolute path"), removed[1]);
        Assert.Equal(new RemovedAssetPath("Main", "Up", RemovedAssetPath.Font, "../x.ttf", "contains \"..\""), removed[2]);
        Assert.Contains(@"\\attacker\share\x.png", removed[0].ToString());
    }

    [Fact]
    public void Import_of_a_clean_package_reports_nothing()
    {
        using var dir = new TempDir();
        var t = new Theme();
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/a.png" });
        var package = dir.Combine("p.glance");
        using (var z = ZipFile.Open(package, ZipArchiveMode.Create))
            using (var w = new StreamWriter(z.CreateEntry(ThemePackage.ThemeEntry).Open())) w.Write(t.ToSnapshot());
        ThemePackage.Import(package, dir.Combine("imp"), out var removed);
        Assert.Empty(removed);
    }

    [Fact]
    public void NaN_and_infinity_become_the_default_or_the_nearest_limit()
    {
        // JSON cannot carry NaN/Infinity with Glance's options, but code (an importer, a computed layout) can
        var t = new Theme();
        t.Pages[0].DurationSeconds = double.NaN;
        var w = new Widget { X = double.NaN, Y = double.PositiveInfinity, Width = double.NegativeInfinity, FontSize = double.PositiveInfinity,
            Multiplier = double.NaN, Max = double.NegativeInfinity, Opacity = double.NaN, Thickness = double.NaN };
        t.Widgets.Add(w);
        t.Normalize();

        Assert.Equal(10, t.Pages[0].DurationSeconds);
        Assert.Equal(0, w.X);
        Assert.Equal(100_000, w.Y);
        Assert.Equal(0, w.Width);
        Assert.Equal(1000, w.FontSize);
        Assert.Equal(1, w.Multiplier);
        Assert.Equal(-1e12, w.Max);
        Assert.Equal(100, w.Opacity);
        Assert.Equal(8, w.Thickness);
        _ = t.ToSnapshot();   // NaN would make the JSON writer throw on save
    }

    [Theory]
    [InlineData(@"\\server\share\x.png", true)]
    [InlineData("//server/share/x.png", true)]
    [InlineData(@"\/server\x", true)]
    [InlineData(@"\\?\C:\x.png", true)]
    [InlineData(@"\\.\COM1", true)]
    [InlineData(@"\??\C:\x.png", true)]
    [InlineData(@"C:\x.png", false)]
    [InlineData(@"\x.png", false)]
    [InlineData("assets/x.png", false)]
    [InlineData("", false)]
    public void Network_and_device_paths_are_recognised(string path, bool expected) =>
        Assert.Equal(expected, Theme.IsNetworkOrDevicePath(path));
}

/// <summary>The editor's crash handler writes through CrashLog; it must never throw and must not grow without bound.</summary>
public class CrashLogTests
{
    [Fact]
    public void Writes_the_source_and_the_exception_with_its_stack()
    {
        using var dir = new TempDir();
        var file = dir.Combine("sub", "editor.log");
        Exception ex;
        try { throw new InvalidOperationException("boom"); } catch (Exception e) { ex = e; }
        Assert.True(Glance.Core.Diagnostics.CrashLog.Write(file, "Unhandled UI exception", ex, new DateTime(2026, 11, 26, 10, 0, 0)));
        var text = File.ReadAllText(file);
        Assert.StartsWith("2026-11-26 10:00:00  Unhandled UI exception: System.InvalidOperationException: boom", text);
        Assert.Contains(nameof(Writes_the_source_and_the_exception_with_its_stack), text);
    }

    [Fact]
    public void A_full_log_is_rotated_to_old()
    {
        using var dir = new TempDir();
        var file = dir.Combine("editor.log");
        File.WriteAllText(file, new string('x', (int)Glance.Core.Diagnostics.CrashLog.MaxBytes + 1));
        Glance.Core.Diagnostics.CrashLog.Write(file, "again", new Exception("e"), DateTime.Now);
        Assert.True(new FileInfo(file).Length < 1000);
        Assert.True(File.Exists(file + ".old"));
    }

    [Fact]
    public void Never_throws_when_the_log_cannot_be_written()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Combine("editor.log"));   // a folder where the file should be
        Assert.False(Glance.Core.Diagnostics.CrashLog.Write(dir.Combine("editor.log"), "x", null, DateTime.Now));
    }
}

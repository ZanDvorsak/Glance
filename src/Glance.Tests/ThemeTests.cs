using System.IO.Compression;
using System.Text;
using Glance.Core.Themes;

namespace Glance.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "glance_tests_" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string Combine(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
    public void Dispose() { try { Directory.Delete(Path, true); } catch { /* best effort */ } }
}

public class ThemeModelTests
{
    [Fact]
    public void A_new_theme_always_has_one_page_and_the_editor_widgets_alias_that_page()
    {
        var t = new Theme();
        Assert.Single(t.Pages);
        t.Widgets.Add(Widget.Create(WidgetKind.Text));
        Assert.Single(t.Pages[0].Widgets);
    }

    [Fact]
    public void Widgets_follows_the_active_page_and_clamps_a_bad_index()
    {
        var t = new Theme(); t.Pages.Add(new ThemePage { Name = "Two" });
        t.ActivePageIndex = 1; t.Widgets.Add(Widget.Create(WidgetKind.Clock));
        Assert.Empty(t.Pages[0].Widgets); Assert.Single(t.Pages[1].Widgets);
        t.ActivePageIndex = 99;
        Assert.Same(t.Pages[1], t.ActivePage);
    }

    [Fact]
    public void Snapshots_round_trip_everything_including_all_pages()
    {
        var t = new Theme { Name = "n", Width = 800, Height = 480, BackgroundColor = "#FF123456", AutoRotate = false };
        t.Widgets.Add(new Widget { Kind = WidgetKind.Gauge, Name = "g", X = 3, Y = 4, Width = 90, Height = 90, SensorId = "/x", Unit = "%", Min = 1, Max = 9, WarnAt = 5, CritAt = 8, Group = "grp", Vertical = true });
        t.Pages.Add(new ThemePage { Name = "Second", DurationSeconds = 7, BackgroundColor = "#FF000011" });
        t.Pages[1].Widgets.Add(Widget.Create(WidgetKind.Graph));

        var copy = new Theme();
        copy.ApplySnapshot(t.ToSnapshot());
        Assert.Equal(t.ToSnapshot(), copy.ToSnapshot());
        Assert.Equal((800, 480, false), (copy.Width, copy.Height, copy.AutoRotate));
        Assert.Equal(2, copy.Pages.Count);
        Assert.Equal("#FF000011", copy.Pages[1].BackgroundColor);
        var g = copy.Pages[0].Widgets[0];
        Assert.Equal((WidgetKind.Gauge, "grp", 5.0, 8.0), (g.Kind, g.Group, g.WarnAt, g.CritAt));
    }

    [Fact]
    public void ApplySnapshot_keeps_the_instance_and_clamps_the_active_page()
    {
        var t = new Theme(); t.Pages.Add(new ThemePage()); t.Pages.Add(new ThemePage()); t.ActivePageIndex = 2;
        var single = new Theme(); single.Widgets.Add(Widget.Create(WidgetKind.Text));
        t.ApplySnapshot(single.ToSnapshot());
        Assert.Single(t.Pages);
        Assert.Equal(0, t.ActivePageIndex);
        Assert.Single(t.Widgets);
    }

    [Fact]
    public void Enums_are_written_as_readable_strings()
    {
        var t = new Theme(); t.Widgets.Add(Widget.Create(WidgetKind.Sensor));
        var json = t.ToSnapshot();
        Assert.Contains("\"Kind\": \"Sensor\"", json);
        Assert.Contains("\"Align\": \"Left\"", json);
    }

    [Fact]
    public void Themes_saved_before_pages_existed_load_as_one_page()
    {
        using var dir = new TempDir();
        var file = dir.Combine("theme.json");
        File.WriteAllText(file, """
            { "Name": "old", "Width": 480, "Height": 320, "BackgroundColor": "#FF000000",
              "Widgets": [ { "Kind": "Text", "Name": "a", "Text": "hi" }, { "Kind": "Clock", "Name": "b" } ] }
            """);
        var t = Theme.Load(file);
        Assert.Single(t.Pages);
        Assert.Equal(new[] { "a", "b" }, t.Widgets.Select(w => w.Name));
        Assert.Null(t.LegacyWidgets);
        Assert.DoesNotContain("\"Widgets\"", t.ToSnapshot().Replace("\"Widgets\": [", "X").Split("\"Pages\"")[0]);   // the old top-level array is gone
    }

    [Fact]
    public void Missing_newer_properties_get_their_defaults()
    {
        using var dir = new TempDir();
        var file = dir.Combine("theme.json");
        File.WriteAllText(file, """{ "Pages": [ { "Name": "P", "Widgets": [ { "Kind": "Bar", "Name": "bar" } ] } ] }""");
        var bar = Theme.Load(file).Widgets[0];
        Assert.False(bar.Vertical);
        Assert.Equal(1.0, bar.Multiplier);
        Assert.Equal(TextAlign.Left, bar.Align);
        Assert.True(bar.Visible);
        Assert.Equal(100.0, bar.Opacity);
    }

    [Fact]
    public void Save_bundles_images_next_to_the_theme_and_uses_relative_paths()
    {
        using var dir = new TempDir();
        var picture = dir.Combine("elsewhere", "bg.png");
        Directory.CreateDirectory(Path.GetDirectoryName(picture)!);
        File.WriteAllBytes(picture, new byte[] { 1, 2, 3 });

        var t = new Theme { Name = "t" };
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = picture });
        var themeFile = dir.Combine("out", "theme.json");
        t.Save(themeFile);

        Assert.Equal("assets/bg.png", t.Widgets[0].ImagePath);
        Assert.True(File.Exists(dir.Combine("out", "assets", "bg.png")));
        var back = Theme.Load(themeFile);
        Assert.Equal(dir.Combine("out", "assets", "bg.png"), Path.GetFullPath(back.ResolveAsset("assets/bg.png")!));
    }

    [Fact]
    public void Clone_makes_an_independent_widget_with_a_new_id()
    {
        var w = new Widget { Name = "cpu", X = 5 };
        var c = w.Clone();
        Assert.NotEqual(w.Id, c.Id);
        Assert.Equal(5, c.X);
        c.X = 9;
        Assert.Equal(5, w.X);
    }

    [Fact]
    public void Every_widget_kind_can_be_created_with_sensible_defaults()
    {
        foreach (var kind in Enum.GetValues<WidgetKind>())
        {
            var w = Widget.Create(kind, 11, 22);
            Assert.Equal(kind, w.Kind);
            Assert.Equal((11.0, 22.0), (w.X, w.Y));
            Assert.False(string.IsNullOrWhiteSpace(w.Name));
        }
    }
}

public class ThemePackageTests
{
    private static string MakeZip(TempDir dir, string name, Action<ZipArchive> fill)
    {
        var path = dir.Combine(name);
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        fill(z);
        return path;
    }

    private static void Put(ZipArchive z, string entry, string content)
    {
        using var w = new StreamWriter(z.CreateEntry(entry).Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private static string ValidJson() => new Theme { Name = "ok" }.ToSnapshot();

    [Fact]
    public void Export_then_import_round_trips_theme_pages_and_images()
    {
        using var dir = new TempDir();
        var src = dir.Combine("src"); Directory.CreateDirectory(Path.Combine(src, "assets"));
        File.WriteAllBytes(Path.Combine(src, "assets", "bg.png"), new byte[] { 1, 2, 3, 4, 5 });
        var outside = dir.Combine("elsewhere", "photo.jpg"); Directory.CreateDirectory(Path.GetDirectoryName(outside)!); File.WriteAllBytes(outside, new byte[] { 9, 9 });

        var t = new Theme { Name = "share me", BaseFolder = src };
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/bg.png" });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/bg.png" });   // same file used twice
        t.Pages.Add(new ThemePage { Name = "Second" });
        t.Pages[1].Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = outside });

        var package = dir.Combine("share.glance");
        ThemePackage.Export(t, package);
        Assert.Equal(outside, t.Pages[1].Widgets[0].ImagePath);   // exporting must not change the open theme

        using (var z = ZipFile.OpenRead(package))
            Assert.Equal(new[] { "assets/bg.png", "assets/photo.jpg", "theme.json" }, z.Entries.Select(e => e.FullName).OrderBy(x => x).ToArray());

        var themeFile = ThemePackage.Import(package, dir.Combine("imported"));
        var back = Theme.Load(themeFile);
        Assert.Equal("share me", back.Name);
        Assert.Equal(2, back.Pages.Count);
        Assert.All(back.Pages[0].Widgets, w => Assert.Equal("assets/bg.png", w.ImagePath));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, File.ReadAllBytes(back.ResolveAsset("assets/bg.png")!));
        Assert.Equal(new byte[] { 9, 9 }, File.ReadAllBytes(back.ResolveAsset(back.Pages[1].Widgets[0].ImagePath)!));
    }

    [Fact]
    public void Two_different_files_with_the_same_name_do_not_overwrite_each_other()
    {
        using var dir = new TempDir();
        var a = dir.Combine("a", "pic.png"); var b = dir.Combine("b", "pic.png");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!); Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.WriteAllBytes(a, new byte[] { 1 }); File.WriteAllBytes(b, new byte[] { 2 });
        var t = new Theme();
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = a });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = b });
        var package = dir.Combine("x.glance"); ThemePackage.Export(t, package);
        var back = Theme.Load(ThemePackage.Import(package, dir.Combine("imp")));
        var paths = back.Widgets.Select(w => w.ImagePath).ToList();
        Assert.Equal(2, paths.Distinct().Count());
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(back.ResolveAsset(paths[0])!));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(back.ResolveAsset(paths[1])!));
    }

    [Theory]
    [InlineData("assets/../../evil.txt")]
    [InlineData("assets/..\\..\\evil.txt")]
    [InlineData("../evil.txt")]
    public void Path_traversal_is_refused_and_nothing_is_written(string entry)
    {
        using var dir = new TempDir();
        var package = MakeZip(dir, "bad.glance", z => { Put(z, "theme.json", ValidJson()); Put(z, entry, "x"); });
        var target = dir.Combine("sub", "target");
        Assert.Throws<InvalidDataException>(() => ThemePackage.Import(package, target));
        Assert.False(File.Exists(dir.Combine("evil.txt")));
        Assert.False(File.Exists(dir.Combine("sub", "evil.txt")));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void Drive_letter_paths_are_refused()
    {
        using var dir = new TempDir();
        var package = MakeZip(dir, "drive.glance", z => { Put(z, "theme.json", ValidJson()); Put(z, "assets/C:/evil.txt", "x"); });
        Assert.Throws<InvalidDataException>(() => ThemePackage.Import(package, dir.Combine("t")));
    }

    [Fact]
    public void Files_outside_assets_are_refused()
    {
        using var dir = new TempDir();
        var package = MakeZip(dir, "exe.glance", z => { Put(z, "theme.json", ValidJson()); Put(z, "payload.exe", "MZ"); });
        var target = dir.Combine("t");
        Assert.Throws<InvalidDataException>(() => ThemePackage.Import(package, target));
        Assert.False(File.Exists(Path.Combine(target, "payload.exe")));
    }

    [Fact]
    public void An_archive_without_theme_json_is_refused()
    {
        using var dir = new TempDir();
        Assert.Throws<InvalidDataException>(() => ThemePackage.Import(MakeZip(dir, "no.glance", z => Put(z, "assets/a.png", "x")), dir.Combine("t")));
    }

    [Fact]
    public void Too_many_files_are_refused()
    {
        using var dir = new TempDir();
        var package = MakeZip(dir, "many.glance", z => { Put(z, "theme.json", ValidJson()); for (int i = 0; i < 600; i++) Put(z, $"assets/f{i}.txt", "x"); });
        Assert.Throws<InvalidDataException>(() => ThemePackage.Import(package, dir.Combine("t")));
    }

    [Fact]
    public void A_theme_json_that_is_not_a_theme_is_refused()
    {
        using var dir = new TempDir();
        var package = MakeZip(dir, "junk.glance", z => Put(z, "theme.json", "this is not json"));
        Assert.ThrowsAny<Exception>(() => ThemePackage.Import(package, dir.Combine("t")));
    }
}

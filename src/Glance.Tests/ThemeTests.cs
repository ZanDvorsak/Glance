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

    private static string[] FilesIn(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).OrderBy(f => f).ToArray() : Array.Empty<string>();

    [Fact]
    public void Save_as_to_another_folder_takes_every_image_video_and_font_along()
    {
        using var dir = new TempDir();
        var a = dir.Combine("A");
        Directory.CreateDirectory(Path.Combine(a, "assets"));
        File.WriteAllBytes(Path.Combine(a, "assets", "bg.png"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(a, "assets", "clip.mp4"), new byte[] { 4, 5, 6, 7 });
        File.WriteAllBytes(Path.Combine(a, "assets", "digits.ttf"), new byte[] { 8, 9 });
        var outside = dir.Combine("elsewhere", "photo.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllBytes(outside, new byte[] { 10 });

        var t = new Theme { Name = "t", BaseFolder = a };
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/bg.png" });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Text, FontFile = "assets/digits.ttf" });
        t.Pages.Add(new ThemePage { Name = "Second" });
        t.Pages[1].Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/clip.mp4" });
        t.Pages[1].Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = outside });
        t.Pages[1].Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/bg.png" });   // used twice

        var themeFile = dir.Combine("B", "theme.json");
        t.Save(themeFile);

        Assert.Equal(new[] { "bg.png", "clip.mp4", "digits.ttf", "photo.jpg" }, FilesIn(dir.Combine("B", "assets")));
        var back = Theme.Load(themeFile);
        var used = back.Pages.SelectMany(p => p.Widgets)
            .SelectMany(w => new[] { w.ImagePath, w.FontFile }).Where(p => !string.IsNullOrEmpty(p)).ToList();
        Assert.Equal(5, used.Count);
        Assert.All(used, p =>
        {
            Assert.False(Path.IsPathRooted(p));
            var file = Path.GetFullPath(back.ResolveAsset(p)!);
            Assert.StartsWith(dir.Combine("B") + Path.DirectorySeparatorChar, file);   // in the new folder, not the old one
            Assert.True(File.Exists(file), file);
        });
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(back.ResolveAsset(back.Pages[0].Widgets[0].ImagePath)!));
        Assert.Equal(new byte[] { 8, 9 }, File.ReadAllBytes(back.ResolveAsset(back.Pages[0].Widgets[1].FontFile)!));
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, File.ReadAllBytes(back.ResolveAsset(back.Pages[1].Widgets[0].ImagePath)!));
        Assert.Equal(new byte[] { 10 }, File.ReadAllBytes(back.ResolveAsset(back.Pages[1].Widgets[1].ImagePath)!));
        Assert.Equal(new[] { "bg.png", "clip.mp4", "digits.ttf" }, FilesIn(Path.Combine(a, "assets")));   // the old folder is left alone
    }

    [Fact]
    public void Save_as_keeps_two_different_files_with_the_same_name_apart()
    {
        using var dir = new TempDir();
        var a = dir.Combine("A");
        Directory.CreateDirectory(Path.Combine(a, "assets"));
        File.WriteAllBytes(Path.Combine(a, "assets", "pic.png"), new byte[] { 1 });
        var other = dir.Combine("elsewhere", "pic.png");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllBytes(other, new byte[] { 2 });   // same name and same size, different content

        var t = new Theme { BaseFolder = a };
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/pic.png" });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = other });
        t.Save(dir.Combine("B", "theme.json"));

        Assert.NotEqual(t.Widgets[0].ImagePath, t.Widgets[1].ImagePath);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(t.ResolveAsset(t.Widgets[0].ImagePath)!));
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(t.ResolveAsset(t.Widgets[1].ImagePath)!));
    }

    [Fact]
    public void Save_as_does_not_reuse_a_different_file_of_the_same_name_and_size_in_the_target()
    {
        using var dir = new TempDir();
        var a = dir.Combine("A");
        Directory.CreateDirectory(Path.Combine(a, "assets"));
        File.WriteAllBytes(Path.Combine(a, "assets", "bg.bmp"), new byte[] { 1, 1, 1 });
        Directory.CreateDirectory(dir.Combine("B", "assets"));
        File.WriteAllBytes(dir.Combine("B", "assets", "bg.bmp"), new byte[] { 2, 2, 2 });   // another theme's picture

        var t = new Theme { BaseFolder = a };
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/bg.bmp" });
        t.Save(dir.Combine("B", "theme.json"));

        Assert.Equal(new byte[] { 1, 1, 1 }, File.ReadAllBytes(t.ResolveAsset(t.Widgets[0].ImagePath)!));
        Assert.Equal(new byte[] { 2, 2, 2 }, File.ReadAllBytes(dir.Combine("B", "assets", "bg.bmp")));   // the other theme keeps its picture
    }

    [Fact]
    public void Save_as_keeps_same_named_files_from_different_subfolders_apart()
    {
        using var dir = new TempDir();
        var a = dir.Combine("A");
        Directory.CreateDirectory(Path.Combine(a, "assets", "x"));
        Directory.CreateDirectory(Path.Combine(a, "assets", "y"));
        File.WriteAllBytes(Path.Combine(a, "assets", "x", "p.png"), new byte[] { 3, 3 });
        File.WriteAllBytes(Path.Combine(a, "assets", "y", "p.png"), new byte[] { 4, 4 });

        var t = new Theme { BaseFolder = a };
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/x/p.png" });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/y/p.png" });
        t.Save(dir.Combine("B", "theme.json"));

        Assert.Equal(new byte[] { 3, 3 }, File.ReadAllBytes(t.ResolveAsset(t.Widgets[0].ImagePath)!));
        Assert.Equal(new byte[] { 4, 4 }, File.ReadAllBytes(t.ResolveAsset(t.Widgets[1].ImagePath)!));
    }

    [Fact]
    public void Save_as_reuses_an_identical_file_already_in_the_target()
    {
        using var dir = new TempDir();
        var a = dir.Combine("A");
        Directory.CreateDirectory(Path.Combine(a, "assets"));
        File.WriteAllBytes(Path.Combine(a, "assets", "bg.bmp"), new byte[] { 7, 7, 7 });
        Directory.CreateDirectory(dir.Combine("B", "assets"));
        File.WriteAllBytes(dir.Combine("B", "assets", "bg.bmp"), new byte[] { 7, 7, 7 });

        var t = new Theme { BaseFolder = a };
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/bg.bmp" });
        t.Save(dir.Combine("B", "theme.json"));

        Assert.Equal("assets/bg.bmp", t.Widgets[0].ImagePath);
        Assert.Equal(new[] { "bg.bmp" }, FilesIn(dir.Combine("B", "assets")));
    }

    [Fact]
    public void A_save_that_fails_while_copying_leaves_the_open_theme_and_the_target_unchanged()
    {
        using var dir = new TempDir();
        var a = dir.Combine("A");
        Directory.CreateDirectory(Path.Combine(a, "assets"));
        File.WriteAllBytes(Path.Combine(a, "assets", "a.png"), new byte[] { 1, 2 });
        File.WriteAllBytes(Path.Combine(a, "assets", "locked.png"), new byte[] { 5 });
        Directory.CreateDirectory(dir.Combine("B", "assets"));
        File.WriteAllBytes(dir.Combine("B", "assets", "a.png"), new byte[] { 9 });   // forces a new name for A's a.png
        var target = dir.Combine("B", "theme.json");
        File.WriteAllText(target, "previous theme");

        var t = new Theme { BaseFolder = a };
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/a.png" });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/locked.png" });
        var before = t.ToSnapshot();

        using (new FileStream(Path.Combine(a, "assets", "locked.png"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => t.Save(target));

        Assert.Equal(before, t.ToSnapshot());   // every widget path is as it was
        Assert.Equal(a, t.BaseFolder);
        Assert.Equal("previous theme", File.ReadAllText(target));
        Assert.Equal(new[] { "a.png" }, FilesIn(dir.Combine("B", "assets")));   // the copies made before the failure are gone
        Assert.Equal(new[] { "theme.json" }, FilesIn(dir.Combine("B")));
    }

    [Fact]
    public void A_save_that_fails_while_writing_the_theme_file_leaves_the_open_theme_unchanged()
    {
        using var dir = new TempDir();
        var a = dir.Combine("A");
        Directory.CreateDirectory(Path.Combine(a, "assets"));
        File.WriteAllBytes(Path.Combine(a, "assets", "bg.png"), new byte[] { 1, 2 });
        Directory.CreateDirectory(dir.Combine("B"));
        var target = dir.Combine("B", "theme.json");
        File.WriteAllText(target, "previous theme");
        File.SetAttributes(target, FileAttributes.ReadOnly);
        try
        {
            var t = new Theme { BaseFolder = a };
            t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/bg.png" });
            var before = t.ToSnapshot();

            Assert.ThrowsAny<Exception>(() => t.Save(target));

            Assert.Equal(before, t.ToSnapshot());
            Assert.Equal(a, t.BaseFolder);
            Assert.Equal("previous theme", File.ReadAllText(target));
            Assert.Empty(FilesIn(dir.Combine("B", "assets")));
            Assert.Equal(new[] { "theme.json" }, FilesIn(dir.Combine("B")));   // no temporary file left behind
        }
        finally { File.SetAttributes(target, FileAttributes.Normal); }
    }

    [Fact]
    public void After_save_as_to_another_folder_undo_cannot_bring_back_files_the_new_folder_lacks()
    {
        using var dir = new TempDir();
        var a = dir.Combine("A");
        Directory.CreateDirectory(Path.Combine(a, "assets"));
        File.WriteAllBytes(Path.Combine(a, "assets", "bg.png"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(a, "assets", "old.png"), new byte[] { 2 });
        var t = new Theme { BaseFolder = a };
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/bg.png" });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/old.png" });
        var undo = new Glance.Core.Editing.UndoManager(t.ToSnapshot());
        t.Widgets.RemoveAt(1);                 // the user deletes a picture...
        undo.Commit(t.ToSnapshot());

        var moved = t.Save(dir.Combine("B", "theme.json"));   // ...then saves the theme somewhere else
        undo.AfterSave(t.ToSnapshot(), moved);

        Assert.True(moved);
        for (var s = undo.Undo(); s is not null; s = undo.Undo())
        {
            var back = new Theme { BaseFolder = t.BaseFolder };
            back.ApplySnapshot(s);
            Assert.All(back.Pages.SelectMany(p => p.Widgets).Select(w => w.ImagePath).Where(p => p != ""),
                p => Assert.True(File.Exists(back.ResolveAsset(p)), "undo brought back " + p + ", which is not in the new folder"));
        }
    }

    [Fact]
    public void Saving_to_the_same_folder_keeps_the_undo_history()
    {
        using var dir = new TempDir();
        var t = new Theme { BaseFolder = dir.Path };
        t.Widgets.Add(Widget.Create(WidgetKind.Text));
        var undo = new Glance.Core.Editing.UndoManager(t.ToSnapshot());
        t.Widgets.Add(Widget.Create(WidgetKind.Clock));
        undo.Commit(t.ToSnapshot());

        var moved = t.Save(dir.Combine("theme.json"));
        undo.AfterSave(t.ToSnapshot(), moved);

        Assert.False(moved);
        Assert.True(undo.CanUndo);
    }

    [Fact]
    public void Saving_again_to_the_same_folder_copies_nothing_extra()
    {
        using var dir = new TempDir();
        var a = dir.Combine("A");
        Directory.CreateDirectory(Path.Combine(a, "assets"));
        File.WriteAllBytes(Path.Combine(a, "assets", "bg.png"), new byte[] { 1, 2, 3 });
        Directory.CreateDirectory(Path.Combine(a, "assets", "fonts"));
        File.WriteAllBytes(Path.Combine(a, "assets", "fonts", "digits.ttf"), new byte[] { 8, 9 });

        var t = new Theme { BaseFolder = a + Path.DirectorySeparatorChar };   // a trailing slash is still the same folder
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/bg.png" });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Text, FontFile = "assets/fonts/digits.ttf" });   // the theme's own layout is kept
        var themeFile = Path.Combine(a, "theme.json");
        t.Save(themeFile);
        Theme.Load(themeFile).Save(themeFile);

        Assert.Equal(new[] { "bg.png" }, FilesIn(Path.Combine(a, "assets")));
        Assert.Equal(new[] { "digits.ttf" }, FilesIn(Path.Combine(a, "assets", "fonts")));
        Assert.Equal(new[] { "theme.json" }, FilesIn(a));
        Assert.Equal("assets/bg.png", t.Widgets[0].ImagePath);
        Assert.Equal("assets/fonts/digits.ttf", t.Widgets[1].FontFile);
        var reloaded = Theme.Load(themeFile);   // what the second save actually wrote
        Assert.Equal("assets/bg.png", reloaded.Widgets[0].ImagePath);
        Assert.Equal("assets/fonts/digits.ttf", reloaded.Widgets[1].FontFile);
    }

    [Fact]
    public void Save_as_leaves_missing_files_as_they_are()
    {
        using var dir = new TempDir();
        var gone = dir.Combine("elsewhere", "gone.png");
        var t = new Theme { BaseFolder = dir.Combine("A") };
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/missing.png" });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = gone });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Text, FontFile = "assets/missing.ttf" });

        t.Save(dir.Combine("B", "theme.json"));

        Assert.Equal(("assets/missing.png", gone, "assets/missing.ttf"), (t.Widgets[0].ImagePath, t.Widgets[1].ImagePath, t.Widgets[2].FontFile));
        Assert.False(Directory.Exists(dir.Combine("B", "assets")));
        Assert.True(File.Exists(dir.Combine("B", "theme.json")));
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

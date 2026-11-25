using System.IO.Compression;
using Glance.Core.Themes;
using SkiaSharp;

namespace Glance.Tests;

public class FontTests
{
    // any real font that ships with Windows; tests that need one return early elsewhere
    private static string? SystemFont(string file)
    {
        var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", file);
        return File.Exists(p) ? p : null;
    }

    private static int InkPixels(SKBitmap b)
    {
        int n = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                if (b.GetPixel(x, y).Red > 128) n++;
        return n;
    }

    private static Theme TextTheme(Widget w)
    {
        var t = Fake.Canvas(300, 100);
        t.Widgets.Add(w);
        return t;
    }

    [Fact]
    public void Old_theme_files_without_font_settings_still_load_with_defaults()
    {
        var t = new Theme();
        t.Widgets.Add(new Widget { Kind = WidgetKind.Text, Text = "hi" });
        var json = t.ToSnapshot().Replace("\"FontFile\":\"\",", "").Replace("\"Italic\":false,", "");
        t.ApplySnapshot(json);
        Assert.Equal("", t.Widgets[0].FontFile);
        Assert.False(t.Widgets[0].Italic);
    }

    [Fact]
    public void A_missing_font_file_falls_back_to_the_installed_font()
    {
        var baseline = TextTheme(new Widget { Kind = WidgetKind.Text, Text = "Hello", FontSize = 40, X = 5, Y = 5, Color = "#FFFFFFFF" });
        var broken = TextTheme(new Widget { Kind = WidgetKind.Text, Text = "Hello", FontSize = 40, X = 5, Y = 5, Color = "#FFFFFFFF", FontFile = @"C:\does\not\exist.ttf" });
        using var a = Fake.Render(baseline, null);
        using var b = Fake.Render(broken, null);
        Assert.True(InkPixels(a) > 100);
        Assert.Equal(InkPixels(a), InkPixels(b));
    }

    [Fact]
    public void A_font_file_changes_how_text_is_drawn()
    {
        var mono = SystemFont("cour.ttf") ?? SystemFont("consola.ttf");
        if (mono is null) return;   // not a Windows machine with the usual fonts
        var plain = TextTheme(new Widget { Kind = WidgetKind.Text, Text = "Wilmington", FontSize = 36, X = 5, Y = 5, Color = "#FFFFFFFF" });
        var custom = TextTheme(new Widget { Kind = WidgetKind.Text, Text = "Wilmington", FontSize = 36, X = 5, Y = 5, Color = "#FFFFFFFF", FontFile = mono });
        using var a = Fake.Render(plain, null);
        using var b = Fake.Render(custom, null);
        Assert.NotEqual(InkPixels(a), InkPixels(b));
    }

    [Fact]
    public void Italic_changes_the_drawing_for_installed_and_file_fonts()
    {
        var upright = TextTheme(new Widget { Kind = WidgetKind.Text, Text = "Hill", FontSize = 40, X = 5, Y = 5, Color = "#FFFFFFFF" });
        var slanted = TextTheme(new Widget { Kind = WidgetKind.Text, Text = "Hill", FontSize = 40, X = 5, Y = 5, Color = "#FFFFFFFF", Italic = true });
        using var a = Fake.Render(upright, null);
        using var b = Fake.Render(slanted, null);
        Assert.NotEqual(Hash(a), Hash(b));

        var file = SystemFont("arial.ttf") ?? SystemFont("segoeui.ttf");
        if (file is null) return;
        using var c = Fake.Render(TextTheme(new Widget { Kind = WidgetKind.Text, Text = "Hill", FontSize = 40, X = 5, Y = 5, Color = "#FFFFFFFF", FontFile = file }), null);
        using var d = Fake.Render(TextTheme(new Widget { Kind = WidgetKind.Text, Text = "Hill", FontSize = 40, X = 5, Y = 5, Color = "#FFFFFFFF", FontFile = file, Italic = true }), null);
        Assert.NotEqual(Hash(c), Hash(d));
    }

    private static string Hash(SKBitmap b) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(b.Bytes));

    [Fact]
    public void Saving_copies_an_absolute_font_file_into_the_theme_assets()
    {
        using var dir = new TempDir();
        var font = dir.Combine("elsewhere", "My Font.ttf"); Directory.CreateDirectory(Path.GetDirectoryName(font)!); File.WriteAllBytes(font, new byte[] { 7, 7, 7 });
        var t = new Theme { Name = "fonts" };
        t.Widgets.Add(new Widget { Kind = WidgetKind.Text, Text = "x", FontFile = font });
        t.Save(dir.Combine("out", "theme.json"));
        Assert.Equal("assets/My Font.ttf", t.Widgets[0].FontFile);
        Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(dir.Combine("out", "assets", "My Font.ttf")));
    }

    [Fact]
    public void Exported_themes_carry_their_font_files()
    {
        using var dir = new TempDir();
        var src = dir.Combine("src"); Directory.CreateDirectory(Path.Combine(src, "assets"));
        File.WriteAllBytes(Path.Combine(src, "assets", "f.otf"), new byte[] { 1, 2, 3 });
        var t = new Theme { Name = "share", BaseFolder = src };
        t.Widgets.Add(new Widget { Kind = WidgetKind.Text, Text = "x", FontFile = "assets/f.otf" });

        var package = dir.Combine("share.glance");
        ThemePackage.Export(t, package);
        using (var z = ZipFile.OpenRead(package))
            Assert.Contains("assets/f.otf", z.Entries.Select(e => e.FullName));

        var back = Theme.Load(ThemePackage.Import(package, dir.Combine("imported")));
        Assert.Equal("assets/f.otf", back.Widgets[0].FontFile);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(back.ResolveAsset(back.Widgets[0].FontFile)!));
    }
}

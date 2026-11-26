using System.IO.Compression;
using System.Text;
using Glance.Core.Diagnostics;
using Glance.Core.Import;
using Glance.Core.Media;
using Glance.Core.Sensors;
using Glance.Core.Themes;

namespace Glance.Tests;

/// <summary>Fixes for the findings of docs/reviews/review-theme-input.md (M1, L1-L5, N1-N5).</summary>
public class ThemeInputReviewFixTests
{
    private static string MakePackage(TempDir dir, Theme t)
    {
        var path = dir.Combine("p.glance");
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        using var w = new StreamWriter(z.CreateEntry(ThemePackage.ThemeEntry).Open(), new UTF8Encoding(false));
        w.Write(t.ToSnapshot());
        return path;
    }

    // ---- M1: Save must not open network or device paths ------------------------------------------------------------

    [Theory]
    [InlineData(@"\\localhost\C$\Windows\win.ini")]    // the reviewer's probe: the admin share of this PC
    [InlineData(@"\\127.0.0.1\C$\Windows\win.ini")]
    [InlineData(@"\\?\C:\Windows\win.ini")]            // device form of a local file: readable without any share
    public void Save_keeps_a_network_or_device_path_as_text_and_never_copies_it(string foreign)
    {
        using var dir = new TempDir();
        var t = new Theme();
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = foreign });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Text, FontFile = foreign });
        t.Save(dir.Combine("theme.json"));

        Assert.Equal(foreign, t.Widgets[0].ImagePath);
        Assert.Equal(foreign, t.Widgets[1].FontFile);
        Assert.False(File.Exists(dir.Combine("assets", "win.ini")), "Save opened " + foreign + " and copied it into the theme");
    }

    [Fact]
    public void Save_still_adopts_local_files()
    {
        using var dir = new TempDir();
        var pic = dir.Combine("pic.png"); File.WriteAllBytes(pic, new byte[] { 1, 2 });
        var t = new Theme();
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = pic });
        t.Save(dir.Combine("out", "theme.json"));
        Assert.Equal("assets/pic.png", t.Widgets[0].ImagePath);
        Assert.True(File.Exists(dir.Combine("out", "assets", "pic.png")));
    }

    // M1, the other half: a network file the user picks is copied in at once, so a NAS picture still works.
    // \\?\C:\... is refused like a share (device form) but is readable here without one.

    [Fact]
    public void A_picked_network_file_is_copied_into_a_saved_theme_at_once()
    {
        using var dir = new TempDir();
        var nas = dir.Combine("nas"); Directory.CreateDirectory(nas);
        File.WriteAllBytes(Path.Combine(nas, "bg.png"), new byte[] { 7, 7 });
        File.WriteAllBytes(dir.Combine("theme", "assets", "bg.png").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)), new byte[] { 1 });
        var t = new Theme { BaseFolder = dir.Combine("theme") };

        var stored = PickedFile.Localize(@"\\?\" + Path.Combine(nas, "bg.png"), t, dir.Combine("staging"));

        Assert.Equal("assets/bg_1.png", stored);   // never overwrites a file the theme already has
        Assert.Equal(new byte[] { 7, 7 }, File.ReadAllBytes(t.ResolveAsset(stored)!));
    }

    [Fact]
    public void A_picked_network_file_for_an_unsaved_theme_is_staged_locally_and_adopted_on_save()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.Combine("bg.png"), new byte[] { 5 });
        var t = new Theme();
        var stored = PickedFile.Localize(@"\\?\" + dir.Combine("bg.png"), t, dir.Combine("staging"));

        Assert.True(Path.IsPathFullyQualified(stored) && !Theme.IsNetworkOrDevicePath(stored), stored);
        Assert.StartsWith(dir.Combine("staging"), stored);
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = stored });
        t.Save(dir.Combine("out", "theme.json"));
        Assert.Equal("assets/bg.png", t.Widgets[0].ImagePath);
        Assert.Equal(new byte[] { 5 }, File.ReadAllBytes(dir.Combine("out", "assets", "bg.png")));
    }

    [Fact]
    public void A_picked_local_file_is_used_where_it_is()
    {
        var t = new Theme();
        Assert.Equal(@"C:\Pictures\bg.png", PickedFile.Localize(@"C:\Pictures\bg.png", t, @"C:\unused"));
    }

    // ---- L1: a path GetFullPath cannot handle removes that path, not the whole import ------------------------------

    [Theory]
    [InlineData("assets/a\0b.png")]
    [InlineData("long")]
    public void An_unreadable_path_is_removed_and_the_theme_still_imports(string bad)
    {
        if (bad == "long") bad = "assets/" + new string('a', 40_000) + ".png";
        using var dir = new TempDir();
        var t = new Theme();
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, Name = "Bad", ImagePath = bad });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, Name = "Good", ImagePath = "assets/ok.png" });

        var themeFile = ThemePackage.Import(MakePackage(dir, t), dir.Combine("imp"));

        var back = Theme.Load(themeFile);
        Assert.Equal("", back.Widgets[0].ImagePath);
        Assert.Equal("assets/ok.png", back.Widgets[1].ImagePath);
    }

    // ---- L2: ResolveAsset only resolves fully qualified local paths, and checks the combined path ------------------

    [Theory]
    [InlineData(@"\x.png")]          // rooted on the process's current drive (which may be a share)
    [InlineData("C:x.png")]          // relative to drive C:'s current folder
    [InlineData("/x.png")]
    public void Rooted_paths_that_are_not_fully_qualified_are_not_resolved(string path)
    {
        var t = new Theme { BaseFolder = @"C:\Themes\x" };
        Assert.Null(t.ResolveAsset(path));
    }

    [Theory]
    [InlineData(@"\\server\share\themes\x")]
    [InlineData("//server/share/themes/x")]
    [InlineData(@"\\?\UNC\server\share\x")]
    public void Relative_paths_under_a_network_base_folder_are_not_resolved(string baseFolder)
    {
        var t = new Theme { BaseFolder = baseFolder };
        Assert.Null(t.ResolveAsset("assets/bg.png"));
    }

    [Fact]
    public void Slash_backslash_unc_is_recognised() => Assert.True(Theme.IsNetworkOrDevicePath(@"/\server\x"));

    // ---- L5: the editor can say why a path shows nothing -----------------------------------------------------------

    [Theory]
    [InlineData(@"\\nas\photos\bg.png", "network or device")]
    [InlineData(@"\x.png", "current drive")]
    [InlineData("C:x.png", "current drive")]
    public void AssetProblem_explains_a_refused_path(string path, string expected)
    {
        var t = new Theme { BaseFolder = @"C:\Themes\x" };
        Assert.Contains(expected, t.AssetProblem(path));
    }

    [Fact]
    public void AssetProblem_explains_a_theme_stored_on_a_share()
    {
        var t = new Theme { BaseFolder = @"\\nas\themes\x" };
        Assert.Contains("network folder", t.AssetProblem("assets/bg.png"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("assets/bg.png")]
    [InlineData(@"C:\Pictures\bg.png")]
    public void AssetProblem_is_null_for_paths_that_resolve(string path)
    {
        var t = new Theme { BaseFolder = @"C:\Themes\x" };
        Assert.Null(t.AssetProblem(path));
        Assert.Null(new Theme().AssetProblem("assets/bg.png"));   // unsaved theme: nothing to resolve against yet, not an error
    }

    // ---- L4: a video widget cannot make VideoSource allocate huge frames -------------------------------------------

    [Theory]
    [InlineData(8192, 8192)]
    [InlineData(8192, 4096)]
    [InlineData(4000, 4000)]
    public void Huge_video_widgets_are_decoded_at_a_capped_size(int w, int h)
    {
        using var v = new VideoSource("x.mp4", w, h, ImageFit.Stretch, 15, () => null, () => null);
        Assert.True((long)v.Width * v.Height <= 1920 * 1080, $"{v.Width} x {v.Height} = {(long)v.Width * v.Height * 4 / 1048576} MiB per frame");
        Assert.InRange((double)v.Width / v.Height, (double)w / h * 0.98, (double)w / h * 1.02);   // aspect kept: Skia stretches it back
    }

    [Fact]
    public void Normal_video_widgets_keep_their_size()
    {
        using var v = new VideoSource("x.mp4", 480, 320, ImageFit.Cover, 15, () => null, () => null);
        Assert.Equal((480, 320), (v.Width, v.Height));
    }

    // ---- N1: duplicate widget ids get new ids --------------------------------------------------------------------

    [Fact]
    public void Duplicate_widget_ids_are_replaced_on_load()
    {
        using var dir = new TempDir();
        var p = dir.Combine("theme.json");
        File.WriteAllText(p, """
            { "Pages": [ { "Widgets": [ { "Id": "a", "Name": "one" }, { "Id": "a", "Name": "two" } ] },
                         { "Widgets": [ { "Id": "a", "Name": "three" }, { "Id": "b", "Name": "four" } ] } ] }
            """);
        var t = Theme.Load(p);
        var ids = t.Pages.SelectMany(pg => pg.Widgets).Select(w => w.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal("a", ids[0]);   // the first keeps its id
        Assert.Equal("b", ids[3]);
    }

    // ---- N3: AIDA64 imports go through Normalize -------------------------------------------------------------------

    [Fact]
    public void An_aida_import_with_absurd_numbers_is_normalized()
    {
        using var dir = new TempDir();
        var file = dir.Combine("x.axlcd");
        File.WriteAllText(file, string.Join("\n",
            "<LCDBGCOLOR>0</LCDBGCOLOR>",
            "<LCDPAGE1>",
            " <ID>LBL</ID><TXTSIZ>1e300</TXTSIZ><FNTNAM>Arial</FNTNAM><LBL>Huge</LBL><LBLCOL>0</LBLCOL><ITMX>1e300</ITMX><ITMY>-1e300</ITMY>",
            " <ID>LBL</ID><TXTSIZ>0</TXTSIZ><FNTNAM>Arial</FNTNAM><LBL>Zero</LBL><LBLCOL>0</LBLCOL><ITMX>10</ITMX><ITMY>10</ITMY>",
            "</LCDPAGE1>"), Encoding.Latin1);

        var r = AidaImporter.Import(file, dir.Combine("out"), new List<SensorInfo>());

        foreach (var w in r.Theme.Widgets)
        {
            Assert.InRange(w.FontSize, 1, 1000);
            _ = Convert.ToDecimal(w.X); _ = Convert.ToDecimal(w.Y);   // what the property panel does
        }
    }

    // ---- N4: CrashLog is safe to call from several threads ---------------------------------------------------------

    [Fact]
    public void Concurrent_crash_log_writes_all_land()
    {
        using var dir = new TempDir();
        var file = dir.Combine("editor.log");
        const int threads = 8, each = 150;
        var ok = 0;
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
        {
            for (int n = 0; n < each; n++)
                if (CrashLog.Write(file, $"t{i}", new Exception($"e{n}"), DateTime.Now)) Interlocked.Increment(ref ok);
        });
        Assert.Equal(threads * each, ok);
        var lines = File.ReadAllLines(file).Count(l => l.Contains("System.Exception: e"));
        Assert.Equal(threads * each, lines);
    }

    // ---- N5: the removed-path line reads like the dialog around it -------------------------------------------------

    [Fact]
    public void A_removed_path_reads_as_page_widget_what_path_and_why()
    {
        using var dir = new TempDir();
        var t = new Theme();
        t.Pages[0].Name = "Main";
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, Name = "Logo", ImagePath = @"\\attacker\s\x.png", FontFile = @"C:\f.ttf" });
        ThemePackage.Import(MakePackage(dir, t), dir.Combine("imp"), out var removed);

        Assert.Equal(@"Page ""Main"", widget ""Logo"": picture or video \\attacker\s\x.png (network or device path)", removed[0].ToString());
        Assert.Equal(@"Page ""Main"", widget ""Logo"": font C:\f.ttf (absolute path)", removed[1].ToString());
    }
}

/// <summary>L3: the editor's UI crash handler tells the user once and recognises an error that repeats every frame.</summary>
public class CrashGuardTests
{
    private static Exception Thrown(string message)
    {
        try { throw new InvalidOperationException(message); } catch (Exception e) { return e; }
    }

    [Fact]
    public void The_first_error_is_shown_then_repeats_are_logged_a_few_times_then_kept_quiet()
    {
        var g = new CrashGuard();
        var t0 = new DateTime(2026, 11, 26, 12, 0, 0);
        var verdicts = Enumerable.Range(0, 8).Select(i => g.Note(Thrown("boom"), t0.AddSeconds(i * 2))).ToList();   // slow repeats: no storm
        Assert.Equal(CrashVerdict.Notify, verdicts[0]);
        Assert.All(verdicts.Skip(1).Take(CrashGuard.MaxLoggedPerError - 1), v => Assert.Equal(CrashVerdict.Log, v));
        Assert.All(verdicts.Skip(CrashGuard.MaxLoggedPerError), v => Assert.Equal(CrashVerdict.Quiet, v));
    }

    [Fact]
    public void An_error_repeating_every_frame_becomes_a_storm_once()
    {
        var g = new CrashGuard();
        var t0 = new DateTime(2026, 11, 26, 12, 0, 0);
        var verdicts = Enumerable.Range(0, 100).Select(i => g.Note(Thrown("every frame"), t0.AddMilliseconds(i * 33))).ToList();
        Assert.Equal(CrashVerdict.Notify, verdicts[0]);
        Assert.Equal(CrashVerdict.Storm, verdicts[CrashGuard.StormCount - 1]);
        Assert.Single(verdicts, v => v == CrashVerdict.Storm);
        Assert.Equal(CrashGuard.MaxLoggedPerError, verdicts.Count(v => v is CrashVerdict.Notify or CrashVerdict.Log));   // the log is not flooded
    }

    [Fact]
    public void Different_errors_are_counted_apart()
    {
        var g = new CrashGuard();
        var now = DateTime.Now;
        Assert.Equal(CrashVerdict.Notify, g.Note(Thrown("a"), now));
        Assert.Equal(CrashVerdict.Notify, g.Note(Thrown("b"), now));
        Assert.Equal(CrashVerdict.Notify, g.Note(new ArgumentException("a"), now));
        Assert.Equal(CrashVerdict.Log, g.Note(Thrown("a"), now));
    }

    [Fact]
    public void Repeats_spread_out_over_time_never_storm()
    {
        var g = new CrashGuard();
        var t0 = DateTime.Now;
        var verdicts = Enumerable.Range(0, 200).Select(i => g.Note(Thrown("timer"), t0.AddSeconds(i))).ToList();   // 1 per second: 11 per window at most
        Assert.DoesNotContain(CrashVerdict.Storm, verdicts);
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> act) { act(value); return value; }
}

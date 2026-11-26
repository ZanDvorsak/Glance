using System.Text.Json;
using Glance.Core.Settings;
using Glance.Core.Storage;
using Glance.Core.Themes;
using Xunit;

namespace Glance.Tests;

/// <summary>
/// Review finding M4 (and L3's export part): saves of theme.json, settings.json and .glance packages must never leave
/// a half-written or missing file behind, readers must not act on a file that is being written, and a corrupt
/// settings file must be kept aside instead of being silently replaced by the defaults.
/// <para>A "crash mid-write" is simulated by a byte-range lock on the target file: a write in place truncates the
/// file and then fails on the lock, exactly what a crash or power cut after the truncation leaves.</para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]   // file locks and ACLs
public class AtomicSaveTests
{
    private static readonly string[] NoFiles = Array.Empty<string>();

    private static string[] FilesIn(string folder) =>
        Directory.Exists(folder) ? Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal).ToArray() : NoFiles;

    private static string SettingsJson(string lastTheme, int brightness) =>
        JsonSerializer.Serialize(new AppSettings { LastTheme = lastTheme, Brightness = brightness });

    /// <summary>Opens the file the way an indexer or virus scanner might (sharing read and write) and locks its first byte.</summary>
    private static FileStream LockFirstByte(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Lock(0, 1);
        return fs;
    }

    /// <summary>Opens the file with no sharing at all, as another program might; nothing else can open it until disposed.</summary>
    private static FileStream Exclusive(string path) => new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

    // ---- settings.json ------------------------------------------------------------------------------------------

    [Fact]
    public void A_settings_save_that_fails_mid_write_keeps_the_previous_file()
    {
        using var clock = AtomicFile.UseClock(new FakeRetryClock());   // the swap is retried in virtual time
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        var previous = SettingsJson(@"C:\themes\mine\theme.json", 3);
        File.WriteAllText(path, previous);

        using (LockFirstByte(path))
            Assert.ThrowsAny<Exception>(() => new AppSettings { LastTheme = @"C:\other\theme.json", Brightness = 5 }.Save(path));   // IOException or UnauthorizedAccessException

        Assert.Equal(previous, File.ReadAllText(path));
        Assert.Equal(new[] { "settings.json" }, FilesIn(dir.Path));   // no temporary file left behind
    }

    [Fact]
    public void A_settings_save_replaces_the_file_and_leaves_nothing_else()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        new AppSettings { LastTheme = "a", Brightness = 1 }.Save(path);
        new AppSettings { LastTheme = "b", Brightness = 2 }.Save(path);

        var back = AppSettings.Load(path);
        Assert.Equal("b", back.LastTheme);
        Assert.Equal(2, back.Brightness);
        Assert.Equal(new[] { "settings.json" }, FilesIn(dir.Path));
    }

    [Fact]
    public void A_settings_reader_during_writes_always_sees_a_whole_file_and_no_save_is_lost()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        new AppSettings { LastTheme = @"C:\t\start.json" }.Save(path);
        // real threads race here; a long budget (also for the reader task, which inherits it) means a busy machine can
        // only slow this test down, never fail it. The assertions are about content, not timing.
        using var clock = AtomicFile.UseClock(new LongBudgetClock());

        var stop = 0; var reads = 0; var defaults = 0;
        using var firstRead = new ManualResetEventSlim();
        var reader = Task.Run(() =>
        {
            try
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    if (!AppSettings.TryLoad(path, out var s) || s.LastTheme is null) defaults++;
                    reads++;
                    firstRead.Set();
                }
            }
            finally { firstRead.Set(); }
        });
        firstRead.Wait();   // the saves start only once the reader is running, however busy the machine is
        Exception? writerError = null;
        try
        {
            for (var i = 0; i < 300; i++) new AppSettings { LastTheme = $@"C:\t\{i}.json", Brightness = i % 8 }.Save(path);
        }
        catch (Exception ex) { writerError = ex; }
        Volatile.Write(ref stop, 1);
        reader.Wait();

        Assert.Null(writerError);
        Assert.True(reads > 0);
        Assert.Equal(0, defaults);
        Assert.Equal(@"C:\t\299.json", AppSettings.Load(path).LastTheme);
    }

    [Fact]
    public void Missing_settings_give_the_defaults_quietly()
    {
        using var dir = new TempDir();
        var log = new List<string>();

        Assert.True(AppSettings.TryLoad(dir.Combine("settings.json"), out var s, log.Add));

        Assert.Null(s.LastTheme);
        Assert.Equal(7, s.Brightness);
        Assert.Empty(log);
        Assert.Empty(FilesIn(dir.Path));
    }

    [Theory]
    [InlineData("{ \"LastTheme\": \"C:\\\\themes\\\\mine\\\\theme.json\", \"Brigh")]   // cut off mid-write
    [InlineData("")]                                                               // truncated to nothing
    [InlineData("\0\0\0\0\0\0")]                                                   // a power cut can leave zeroed blocks
    public void Corrupt_settings_are_logged_kept_aside_and_never_overwritten(string content)
    {
        using var clock = AtomicFile.UseClock(new FakeRetryClock());   // a syntax error is retried until the budget runs out
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, content);
        var log = new List<string>();

        Assert.True(AppSettings.TryLoad(path, out var s, log.Add));

        Assert.Null(s.LastTheme);   // the app starts with the defaults
        Assert.Equal(7, s.Brightness);
        var aside = Assert.Single(Directory.GetFiles(dir.Path, "settings.json.corrupt-*"));
        Assert.Equal(content, File.ReadAllText(aside));
        Assert.False(File.Exists(path));
        Assert.Contains(log, m => m.Contains("corrupt", StringComparison.OrdinalIgnoreCase) && m.Contains(Path.GetFileName(aside)));

        s.Save(path);   // the next save writes a fresh file and leaves the corrupt copy alone
        Assert.Equal(content, File.ReadAllText(aside));
    }

    [Fact]
    public void Settings_locked_for_a_moment_are_read_once_the_lock_is_gone()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, SettingsJson(@"C:\themes\mine\theme.json", 3));

        var holder = Exclusive(path);
        var fake = new FakeRetryClock { OnWait = n => { if (n == 2) holder.Dispose(); } };   // the other program lets go during the second wait
        AppSettings s;
        using (AtomicFile.UseClock(fake))
            Assert.True(AppSettings.TryLoad(path, out s));
        holder.Dispose();

        Assert.Equal(2, fake.Waits);   // it waited exactly until the file was free
        Assert.Equal(@"C:\themes\mine\theme.json", s.LastTheme);
        Assert.Equal(3, s.Brightness);
    }

    [Fact]
    public void Settings_that_stay_unreadable_are_reported_as_such_and_left_where_they_are()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        var content = SettingsJson(@"C:\themes\mine\theme.json", 3);
        File.WriteAllText(path, content);
        var log = new List<string>();

        var fake = new FakeRetryClock();
        bool ok;
        using (Exclusive(path))   // held for the whole read: the budget runs out (in virtual time)
        using (AtomicFile.UseClock(fake))
            ok = AppSettings.TryLoad(path, out _, log.Add);

        Assert.True(fake.Waits > 0);
        Assert.False(ok);   // the runner keeps the settings it already has instead of switching to the defaults
        Assert.Equal(content, File.ReadAllText(path));
        Assert.Equal(new[] { "settings.json" }, FilesIn(dir.Path));   // not mistaken for a corrupt file
        Assert.NotEmpty(log);
    }

    [Fact]
    public void Settings_being_rewritten_in_place_by_another_program_are_not_mistaken_for_corrupt()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, "{ \"LastTheme\": \"C:\\\\x");   // what a text editor's in-place save looks like halfway
        // the other program finishes its write while the reader waits for the first time
        var fake = new FakeRetryClock { OnWait = n => { if (n == 1) File.WriteAllText(path, SettingsJson(@"C:\themes\mine\theme.json", 4)); } };

        AppSettings s;
        using (AtomicFile.UseClock(fake))
            Assert.True(AppSettings.TryLoad(path, out s));

        Assert.Equal(1, fake.Waits);
        Assert.Equal(@"C:\themes\mine\theme.json", s.LastTheme);
        Assert.Equal(4, s.Brightness);
        Assert.Equal(new[] { "settings.json" }, FilesIn(dir.Path));
    }

    // ---- theme.json ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_theme_save_that_fails_mid_write_keeps_the_previous_file()
    {
        using var clock = AtomicFile.UseClock(new FakeRetryClock());   // the swap is retried in virtual time
        using var dir = new TempDir();
        var path = dir.Combine("theme.json");
        new Theme { Name = "previous" }.Save(path);
        var previous = File.ReadAllText(path);

        using (LockFirstByte(path))
            Assert.ThrowsAny<Exception>(() => new Theme { Name = "next" }.Save(path));   // IOException or UnauthorizedAccessException

        Assert.Equal(previous, File.ReadAllText(path));
        Assert.Equal("previous", Theme.Load(path).Name);
        Assert.DoesNotContain(FilesIn(dir.Path), n => n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".saving", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_theme_save_keeps_one_backup_of_the_previous_theme_file()
    {
        using var dir = new TempDir();
        var path = dir.Combine("theme.json");

        new Theme { Name = "one" }.Save(path);
        Assert.Equal(new[] { "theme.json" }, FilesIn(dir.Path));   // nothing to back up the first time

        new Theme { Name = "two" }.Save(path);
        Assert.Equal(new[] { "theme.json", "theme.json.bak" }, FilesIn(dir.Path));
        Assert.Equal("one", Theme.Load(path + ".bak").Name);

        new Theme { Name = "three" }.Save(path);
        Assert.Equal(new[] { "theme.json", "theme.json.bak" }, FilesIn(dir.Path));   // one copy, not a growing pile
        Assert.Equal("two", Theme.Load(path + ".bak").Name);
        Assert.Equal("three", Theme.Load(path).Name);
    }

    [Fact]
    public void A_theme_read_while_the_file_is_briefly_locked_succeeds()
    {
        using var dir = new TempDir();
        var path = dir.Combine("theme.json");
        new Theme { Name = "mine" }.Save(path);

        var holder = Exclusive(path);
        var fake = new FakeRetryClock { OnWait = n => { if (n == 2) holder.Dispose(); } };   // released during the second wait
        Theme t;
        using (AtomicFile.UseClock(fake))
            t = AtomicFile.ReadWithRetry(() => Theme.Load(path));
        holder.Dispose();

        Assert.Equal(2, fake.Waits);
        Assert.Equal("mine", t.Name);
    }

    [Fact]
    public void A_theme_read_of_a_half_written_file_waits_for_the_whole_file()
    {
        using var dir = new TempDir();
        var path = dir.Combine("theme.json");
        new Theme { Name = "mine" }.Save(path);
        var whole = File.ReadAllText(path);
        File.WriteAllText(path, whole[..(whole.Length / 2)]);   // another program is halfway through writing it in place
        // the gate: that program finishes its write while the reader waits for the first time, never earlier or later
        var fake = new FakeRetryClock { OnWait = n => { if (n == 1) File.WriteAllText(path, whole); } };

        Theme t;
        using (AtomicFile.UseClock(fake))
            t = AtomicFile.ReadWithRetry(() => Theme.Load(path));

        Assert.Equal(1, fake.Waits);   // the first read saw the half file, the second the whole one
        Assert.Equal("mine", t.Name);
    }

    [Fact]
    public void A_theme_read_of_a_missing_file_fails_at_once()
    {
        using var dir = new TempDir();
        var fake = new FakeRetryClock();
        using (AtomicFile.UseClock(fake))
            Assert.Throws<FileNotFoundException>(() => AtomicFile.ReadWithRetry(() => Theme.Load(dir.Combine("theme.json"))));
        Assert.Equal(0, fake.Waits);
    }

    // ---- .glance export -----------------------------------------------------------------------------------------

    [Fact]
    public void An_export_that_fails_midway_keeps_the_previous_package()
    {
        using var dir = new TempDir();
        var src = dir.Combine("src");
        Directory.CreateDirectory(Path.Combine(src, "assets"));
        File.WriteAllBytes(Path.Combine(src, "assets", "a.png"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(src, "assets", "locked.png"), new byte[] { 4 });
        var t = new Theme { Name = "pkg", BaseFolder = src };
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/a.png" });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Image, ImagePath = "assets/locked.png" });
        var outDir = dir.Combine("out");
        Directory.CreateDirectory(outDir);
        var package = Path.Combine(outDir, "mine.glance");
        File.WriteAllText(package, "previous package");

        using (new FileStream(Path.Combine(src, "assets", "locked.png"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => ThemePackage.Export(t, package));

        Assert.Equal("previous package", File.ReadAllText(package));
        Assert.Equal(new[] { "mine.glance" }, FilesIn(outDir));   // no temporary file left behind
    }

    [Fact]
    public void An_export_over_an_existing_package_replaces_it_completely()
    {
        using var dir = new TempDir();
        var package = dir.Combine("mine.glance");
        File.WriteAllText(package, new string('x', 100_000));   // longer than the new package, so leftovers would show

        ThemePackage.Export(new Theme { Name = "fresh" }, package);

        var themeFile = ThemePackage.Import(package, dir.Combine("in"));
        Assert.Equal("fresh", Theme.Load(themeFile).Name);
        Assert.Equal(new[] { "mine.glance" }, FilesIn(dir.Path));
    }
}

using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Glance.Core.Settings;
using Glance.Core.Storage;
using Glance.Core.Themes;
using Xunit;

namespace Glance.Tests;

/// <summary>
/// The findings of the independent review of the atomic-saves branch (docs/reviews/review-atomic-saves.md).
/// Each test names the finding it proves. Waits use <see cref="TestTiming"/> (the retry budget is 400 ms in tests).
/// </summary>
public class AtomicSaveReviewTests
{
    private static string[] FilesIn(string folder) =>
        Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static string SettingsJson(string lastTheme, int brightness) =>
        JsonSerializer.Serialize(new AppSettings { LastTheme = lastTheme, Brightness = brightness });

    /// <summary>Holds the file open on another thread for <paramref name="ms"/> milliseconds with the given sharing.</summary>
    private static Task Hold(string path, int ms, FileShare share, FileAccess access = FileAccess.Read)
    {
        var opened = new ManualResetEventSlim();
        var task = Task.Run(() =>
        {
            using var fs = new FileStream(path, FileMode.Open, access, share);
            opened.Set();
            Thread.Sleep(ms);
        });
        opened.Wait();
        return task;
    }

    private static string Stale(string path)
    {
        File.WriteAllText(path, "left over");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-2));
        return path;
    }

    // ---- M1: settings that could not be read are never saved over ------------------------------------------------

    [Fact]
    public void M1_settings_that_could_not_be_read_are_never_saved_over()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        var original = SettingsJson(@"C:\themes\mine\theme.json", 3);
        File.WriteAllText(path, original);
        var log = new List<string>();

        var holder = Hold(path, TestTiming.LongMs, FileShare.None, FileAccess.ReadWrite);
        var read = AppSettings.Read(path, log.Add);
        holder.Wait();

        Assert.Equal(SettingsFileState.Unreadable, read.State);

        read.Settings.SetupCheckShown = true;   // what the editor does on first run, then it saves
        var saved = read.Settings.TrySave(path, log.Add);

        Assert.Equal(original, File.ReadAllText(path));   // the user's settings survive
        Assert.False(saved);
        Assert.NotNull(read.Problem);   // something the editor can show
        Assert.Contains(log, m => m.Contains("not saved", StringComparison.OrdinalIgnoreCase));
    }

    // ---- L2: the backup is best effort ---------------------------------------------------------------------------

    [Fact]
    public void L2_a_theme_save_still_succeeds_when_the_backup_copy_cannot_be_made()
    {
        using var dir = new TempDir();
        var path = dir.Combine("theme.json");
        new Theme { Name = "one" }.Save(path);

        // the old file can be replaced (DELETE is allowed) but not read, so no backup copy can be made
        var me = WindowsIdentity.GetCurrent().User!;
        var deny = new FileSystemAccessRule(me, FileSystemRights.ReadData, AccessControlType.Deny);
        var info = new FileInfo(path);
        var acl = info.GetAccessControl(); acl.AddAccessRule(deny); info.SetAccessControl(acl);
        try
        {
            new Theme { Name = "two" }.Save(path);

            Assert.Equal("two", Theme.Load(path).Name);
            Assert.Equal(new[] { "theme.json" }, FilesIn(dir.Path));   // no backup, no temporary files
        }
        finally
        {
            if (File.Exists(path)) { var a = info.GetAccessControl(); a.RemoveAccessRule(deny); info.SetAccessControl(a); }
        }
    }

    [Fact]
    public void L2_a_theme_save_that_fails_mid_write_fails_at_the_swap_not_at_the_backup()
    {
        using var dir = new TempDir();
        var path = dir.Combine("theme.json");
        new Theme { Name = "previous" }.Save(path);
        var previous = File.ReadAllText(path);

        Exception ex;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            fs.Lock(0, 1);
            ex = Assert.ThrowsAny<Exception>(() => new Theme { Name = "next" }.Save(path));
            fs.Unlock(0, 1);
        }

        Assert.DoesNotContain(".bak.", ex.Message);   // the error is the swap's, not the backup copy's
        Assert.Contains("theme.json", ex.Message);
        Assert.Equal(previous, File.ReadAllText(path));
        Assert.Equal(new[] { "theme.json" }, FilesIn(dir.Path));
    }

    // ---- L3 / N6: no waiting on errors that cannot clear ---------------------------------------------------------

    [Fact]
    public void L3_a_save_over_a_read_only_file_fails_at_once()
    {
        using var dir = new TempDir();
        var path = dir.Combine("theme.json");
        new Theme { Name = "one" }.Save(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var watch = Stopwatch.StartNew();
            Assert.ThrowsAny<Exception>(() => new Theme { Name = "two" }.Save(path));
            Assert.True(watch.ElapsedMilliseconds < TestTiming.AtOnceMs, $"took {watch.ElapsedMilliseconds} ms");
            Assert.Equal("one", Theme.Load(path).Name);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public void N6_a_read_does_not_wait_on_errors_that_are_not_sharing_or_lock_violations()
    {
        const int diskFull = unchecked((int)0x80070070);
        var watch = Stopwatch.StartNew();
        Assert.Throws<IOException>(() => AtomicFile.ReadWithRetry<int>(() => throw new IOException("disk full", diskFull)));
        Assert.Throws<PathTooLongException>(() => AtomicFile.ReadWithRetry<int>(() => throw new PathTooLongException()));
        Assert.Throws<UnauthorizedAccessException>(() => AtomicFile.ReadWithRetry<int>(() => throw new UnauthorizedAccessException()));
        Assert.True(watch.ElapsedMilliseconds < TestTiming.AtOnceMs, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void N6_a_read_still_waits_out_a_sharing_violation()
    {
        var calls = 0;
        var value = AtomicFile.ReadWithRetry(() => ++calls < 3 ? throw new IOException("busy", unchecked((int)0x80070020)) : 42);
        Assert.Equal(42, value);
        Assert.Equal(3, calls);
    }

    // ---- L4: a handle that allows deleting no longer blocks the swap; a blocked settings save is reported ---------

    [Fact]
    public void L4_a_settings_save_succeeds_while_another_program_holds_the_file_with_full_sharing()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, SettingsJson("old", 1));

        var holder = Hold(path, TestTiming.LongMs, FileShare.ReadWrite | FileShare.Delete);
        var ok = new AppSettings { LastTheme = "new", Brightness = 2 }.TrySave(path);
        holder.Wait();

        Assert.True(ok);
        Assert.Equal("new", AppSettings.Load(path).LastTheme);
    }

    [Fact]
    public void L4_a_theme_save_succeeds_while_another_program_holds_the_file_with_full_sharing()
    {
        using var dir = new TempDir();
        var path = dir.Combine("theme.json");
        new Theme { Name = "one" }.Save(path);

        var holder = Hold(path, TestTiming.LongMs, FileShare.ReadWrite | FileShare.Delete);
        new Theme { Name = "two" }.Save(path);
        holder.Wait();

        Assert.Equal("two", Theme.Load(path).Name);
        Assert.Equal("one", Theme.Load(path + ".bak").Name);
    }

    [Fact]
    public void L4_a_settings_save_that_cannot_happen_is_reported_not_dropped()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        var original = SettingsJson("old", 1);
        File.WriteAllText(path, original);
        var log = new List<string>();

        var holder = Hold(path, TestTiming.LongMs, FileShare.Read);   // no FILE_SHARE_DELETE: nothing can replace it
        var ok = new AppSettings { LastTheme = "new" }.TrySave(path, log.Add);
        holder.Wait();

        Assert.False(ok);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Contains(log, m => m.Contains("not saved", StringComparison.OrdinalIgnoreCase) && m.Contains("settings.json"));
    }

    // ---- L5: leftovers from a crash are cleaned up ---------------------------------------------------------------

    [Fact]
    public void L5_a_save_removes_old_temporary_files_left_by_a_crash_and_nothing_else()
    {
        using var dir = new TempDir();
        var path = dir.Combine("theme.json");
        var g = () => Guid.NewGuid().ToString("N");
        Stale(dir.Combine($"theme.json.{g()}.tmp"));
        Stale(dir.Combine($"theme.json.bak.{g()}.tmp"));
        var fresh = dir.Combine($"theme.json.{g()}.tmp"); File.WriteAllText(fresh, "another save in progress");
        var unrelated = Stale(dir.Combine("theme.json.notes.tmp"));
        var otherFile = Stale(dir.Combine($"other.json.{g()}.tmp"));

        new Theme { Name = "one" }.Save(path);

        Assert.Equal(
            new[] { Path.GetFileName(otherFile), "theme.json", Path.GetFileName(fresh), Path.GetFileName(unrelated) }.OrderBy(n => n, StringComparer.Ordinal),
            FilesIn(dir.Path));
    }

    [Fact]
    public void L5_reading_settings_removes_old_temporary_files_left_by_a_crash()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, SettingsJson("x", 2));
        Stale(dir.Combine($"settings.json.{Guid.NewGuid():N}.tmp"));

        AppSettings.Read(path);

        Assert.Equal(new[] { "settings.json" }, FilesIn(dir.Path));
    }

    // ---- L6 / N2: the caller learns what happened, and wrong content is told apart -------------------------------

    [Fact]
    public void L6_a_corrupt_settings_file_is_reported_to_the_caller_with_where_it_was_kept()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, "{ \"LastTheme\": \"C:\\\\x");

        var read = AppSettings.Read(path);

        Assert.Equal(SettingsFileState.Corrupt, read.State);
        Assert.NotNull(read.KeptAs);
        Assert.True(File.Exists(read.KeptAs));
        Assert.Contains(Path.GetFileName(read.KeptAs!), read.Problem);
    }

    [Theory]
    [InlineData("{ \"Brightness\": \"high\" }")]
    [InlineData("null")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"just text\"")]
    public void N2_valid_json_that_is_not_settings_is_kept_aside_as_wrong_content_without_waiting(string content)
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, content);
        var log = new List<string>();

        var watch = Stopwatch.StartNew();
        var read = AppSettings.Read(path, log.Add);
        watch.Stop();

        Assert.Equal(SettingsFileState.WrongContent, read.State);
        Assert.Null(read.Settings.LastTheme);
        Assert.Equal(content, File.ReadAllText(read.KeptAs!));
        Assert.False(File.Exists(path));
        Assert.True(watch.ElapsedMilliseconds < TestTiming.AtOnceMs, $"took {watch.ElapsedMilliseconds} ms");   // nothing to wait for
        Assert.Contains(log, m => m.Contains("not Glance settings", StringComparison.OrdinalIgnoreCase));
    }

    // ---- N4: leftovers are capped --------------------------------------------------------------------------------

    [Fact]
    public void N4_only_the_newest_five_corrupt_settings_copies_are_kept()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        for (var i = 1; i <= 6; i++) File.WriteAllText($"{path}.corrupt-2020010{i}-000000", "old " + i);
        File.WriteAllText(path, "{ broken");

        var read = AppSettings.Read(path);

        var kept = Directory.GetFiles(dir.Path, "settings.json.corrupt-*").Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(5, kept.Length);
        Assert.Contains(Path.GetFileName(read.KeptAs!), kept);
        Assert.DoesNotContain("settings.json.corrupt-20200101-000000", kept);
        Assert.DoesNotContain("settings.json.corrupt-20200102-000000", kept);
    }

    [Fact]
    public void N4_the_log_is_started_afresh_when_it_gets_too_big()
    {
        using var dir = new TempDir();
        var log = dir.Combine("glance.log");
        File.WriteAllText(log, new string('x', 2000));

        AppSettings.AppendLog(log, "hello", maxBytes: 1000);

        Assert.True(new FileInfo(log).Length < 1000);
        Assert.Contains("hello", File.ReadAllText(log));
        Assert.Equal(2000, new FileInfo(log + ".old").Length);   // one older log is kept
    }

    // ---- L7: theme.json.bak is used ------------------------------------------------------------------------------

    [Fact]
    public void L7_a_damaged_theme_file_falls_back_to_the_backup_and_says_so()
    {
        using var dir = new TempDir();
        var path = dir.Combine("theme.json");
        new Theme { Name = "one" }.Save(path);
        new Theme { Name = "two" }.Save(path);
        File.WriteAllText(path, "{ \"Name\": \"tw");   // damaged, for example by an older version or a hand edit
        var log = new List<string>();

        var read = ThemeFile.Load(path, log.Add);

        Assert.True(read.FromBackup);
        Assert.Equal("one", read.Theme.Name);
        Assert.Equal(dir.Path, read.Theme.BaseFolder);   // relative image paths still resolve next to theme.json
        Assert.NotNull(read.Problem);
        Assert.NotEmpty(log);
    }

    [Fact]
    public void L7_a_damaged_theme_file_without_a_backup_still_reports_the_error()
    {
        using var dir = new TempDir();
        var path = dir.Combine("theme.json");
        File.WriteAllText(path, "{ \"Name\": \"tw");
        Assert.ThrowsAny<JsonException>(() => ThemeFile.Load(path));
    }

    [Fact]
    public void L7_a_good_theme_file_is_read_as_it_is()
    {
        using var dir = new TempDir();
        var path = dir.Combine("theme.json");
        new Theme { Name = "one" }.Save(path);
        new Theme { Name = "two" }.Save(path);

        var read = ThemeFile.Load(path);

        Assert.False(read.FromBackup);
        Assert.Equal("two", read.Theme.Name);
        Assert.Null(read.Problem);
    }

    // ---- test gap: a theme reader during many saves --------------------------------------------------------------

    [Fact]
    public void A_theme_reader_during_many_saves_never_sees_the_file_missing_or_partial()
    {
        using var dir = new TempDir();
        var path = dir.Combine("theme.json");
        new Theme { Name = "start" }.Save(path);

        var stop = 0; var reads = 0; Exception? readerError = null;
        var reader = Task.Run(() =>
        {
            try
            {
                // reads the way the runner does (a momentary sharing violation during the swap is waited out, not an error);
                // a short pause between reads, since a reader that never lets go starves the rename (the runner reads once per change)
                while (Volatile.Read(ref stop) == 0)
                {
                    var read = ThemeFile.Load(path);
                    Assert.False(read.FromBackup);                    // it never found theme.json damaged
                    Assert.StartsWith("s", read.Theme.Name);          // "start" or "save n": a whole file
                    reads++;
                    Thread.Sleep(2);
                }
            }
            catch (Exception ex) { readerError = ex; }
        });
        Exception? writerError = null;
        try
        {
            for (var i = 0; i < 200; i++) new Theme { Name = "save " + i }.Save(path);
        }
        catch (Exception ex) { writerError = ex; }
        Volatile.Write(ref stop, 1);
        reader.Wait();

        Assert.Null(writerError);
        Assert.Null(readerError);
        Assert.True(reads > 0);
        Assert.Equal("save 199", Theme.Load(path).Name);
        Assert.Equal(new[] { "theme.json", "theme.json.bak" }, FilesIn(dir.Path));
    }
}

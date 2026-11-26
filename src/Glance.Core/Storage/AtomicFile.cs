using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Glance.Core.Storage;

/// <summary>
/// Saves files so that the name always holds either the whole old content or the whole new content, never a
/// half-written file: the new content goes to a temporary file in the same folder, is flushed to the disk, and is then
/// renamed over the old one (a rename within one NTFS volume is all or nothing). A crash, a power cut or an error while
/// writing leaves the old file as it was.
/// <para>Temporary files are named <c>&lt;name&gt;.&lt;random&gt;.tmp</c>, so a watcher for <c>*.json</c> only sees the
/// final rename. Another process that has the file open (a reader, an indexer, a virus scanner) can make the rename or
/// a read fail for a moment; both are retried for about a second.</para>
/// </summary>
public static class AtomicFile
{
    /// <summary>How long a save or a read keeps trying while the file is busy.</summary>
    internal static readonly TimeSpan RetryBudget = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Where <see cref="Write"/> keeps the previous version when asked to.</summary>
    public static string BackupPath(string path) => path + ".bak";

    /// <summary>Writes <paramref name="text"/> as UTF-8 without a byte order mark (what <see cref="File.WriteAllText(string, string?)"/> writes).</summary>
    public static void WriteAllText(string path, string text, bool keepBackup = false) =>
        Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            writer.Write(text);
        }, keepBackup);

    /// <summary>
    /// Replaces <paramref name="path"/> with what <paramref name="write"/> writes, all or nothing. With
    /// <paramref name="keepBackup"/>, the file it replaces is kept as <see cref="BackupPath"/> (one copy, overwritten
    /// by the next save). If anything fails, the old file is untouched, no temporary file is left behind, and the
    /// error is thrown.
    /// </summary>
    public static void Write(string path, Action<Stream> write, bool keepBackup = false)
    {
        var target = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(target)!;
        var name = Path.GetFileName(target);
        var temp = Path.Combine(dir, $"{name}.{Guid.NewGuid():N}.tmp");
        string? backupTemp = null;
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(fs);
                fs.Flush(flushToDisk: true);   // the content is on the disk before the name points at it
            }

            if (keepBackup && File.Exists(target))
            {
                // a copy, not a rename: the target keeps its whole old content until the final rename below
                backupTemp = Path.Combine(dir, $"{name}.bak.{Guid.NewGuid():N}.tmp");
                var copyFrom = target; var copyTo = backupTemp;
                Retry(() => File.Copy(copyFrom, copyTo));
                File.SetAttributes(backupTemp, FileAttributes.Normal);   // a read-only original must not make the copy undeletable
            }

            Retry(() => File.Move(temp, target, overwrite: true));
        }
        catch
        {
            TryDelete(temp);
            if (backupTemp is not null) TryDelete(backupTemp);
            throw;
        }

        if (backupTemp is not null)
        {
            // the save itself has succeeded; failing to keep the backup is not a reason to report it as failed
            try { Retry(() => File.Move(backupTemp, BackupPath(target), overwrite: true)); }
            catch { TryDelete(backupTemp); }
        }
    }

    /// <summary>
    /// Runs <paramref name="read"/> and tries again for about a second while the file is busy (another process has
    /// it open) or does not parse yet (another program is halfway through writing it in place). A missing file
    /// fails at once. After the last try the error is thrown.
    /// </summary>
    public static T ReadWithRetry<T>(Func<T> read) => Retry(read, ex => IsBusy(ex) || ex is JsonException);

    private static void Retry(Action action) => Retry<object?>(() => { action(); return null; }, IsBusy);

    private static T Retry<T>(Func<T> operation, Func<Exception, bool> worthRetrying)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try { return operation(); }
            catch (Exception ex) when (worthRetrying(ex) && watch.Elapsed < RetryBudget) { Thread.Sleep(RetryDelay); }
        }
    }

    /// <summary>Sharing violations, locks and "access denied" (also what a file that is being replaced reports); not a missing file.</summary>
    private static bool IsBusy(Exception ex) =>
        ex is UnauthorizedAccessException || (ex is IOException && ex is not FileNotFoundException && ex is not DirectoryNotFoundException);

    private static void TryDelete(string file) { try { File.Delete(file); } catch { /* best effort */ } }
}

namespace Glance.Core.Diagnostics;

/// <summary>
/// Appends unhandled exceptions to a log file (the editor uses <c>%AppData%\Glance\editor.log</c>). Never throws: it runs
/// from crash handlers. The file is capped: when it grows past <see cref="MaxBytes"/> it is renamed to <c>*.old</c>
/// (replacing an older one), so an error that repeats every frame cannot fill the disk.
/// Writes are serialized: the UI thread and the finalizer thread (unobserved task exceptions) may log at the same time,
/// and without the lock one of them could lose its entry to a sharing violation or a half-done rotation.
/// </summary>
public static class CrashLog
{
    public const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();

    /// <summary>Writes one entry. Returns false if the log could not be written.</summary>
    public static bool Write(string file, string source, Exception? ex, DateTime now)
    {
        var entry = $"{now:yyyy-MM-dd HH:mm:ss}  {source}: {ex?.ToString() ?? "(no exception object)"}{Environment.NewLine}";
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                var info = new FileInfo(file);
                if (info.Exists && info.Length > MaxBytes) File.Move(file, file + ".old", overwrite: true);
                File.AppendAllText(file, entry);
                return true;
            }
            catch { return false; }
        }
    }
}

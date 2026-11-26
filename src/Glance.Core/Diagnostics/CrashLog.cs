namespace Glance.Core.Diagnostics;

/// <summary>
/// Appends unhandled exceptions to a log file (the editor uses <c>%AppData%\Glance\editor.log</c>). Never throws: it runs
/// from crash handlers. The file is capped: when it grows past <see cref="MaxBytes"/> it is renamed to <c>*.old</c>
/// (replacing an older one), so an error that repeats every frame cannot fill the disk.
/// </summary>
public static class CrashLog
{
    public const long MaxBytes = 1_000_000;

    /// <summary>Writes one entry. Returns false if the log could not be written.</summary>
    public static bool Write(string file, string source, Exception? ex, DateTime now)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            var info = new FileInfo(file);
            if (info.Exists && info.Length > MaxBytes) File.Move(file, file + ".old", overwrite: true);
            File.AppendAllText(file, $"{now:yyyy-MM-dd HH:mm:ss}  {source}: {ex?.ToString() ?? "(no exception object)"}{Environment.NewLine}");
            return true;
        }
        catch { return false; }
    }
}

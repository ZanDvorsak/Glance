using System.Runtime.ExceptionServices;
using System.Text.Json;
using Glance.Core.Storage;

namespace Glance.Core.Themes;

/// <param name="Theme">The theme that was read.</param>
/// <param name="FromBackup">True when theme.json could not be read and theme.json.bak was used instead.</param>
/// <param name="Problem">A sentence for the user when the backup was used, otherwise null.</param>
public sealed record ThemeRead(Theme Theme, bool FromBackup, string? Problem);

/// <summary>Reads a saved theme the way the editor and the runner do.</summary>
public static class ThemeFile
{
    /// <summary>
    /// Reads <paramref name="path"/>, waiting a moment if another program has it open or is still writing it
    /// (<see cref="AtomicFile.ReadWithRetry"/>). When the file is damaged (it does not parse) and
    /// <c>theme.json.bak</c> (the version before the last save, see <see cref="Theme.Save"/>) can be read, that is
    /// returned instead, with <see cref="ThemeRead.FromBackup"/> set and a message; nothing on disk is changed, so the
    /// next save writes a good <c>theme.json</c>. Otherwise the original error is thrown (a missing file is not
    /// "damaged" and does not fall back).
    /// </summary>
    public static ThemeRead Load(string path, Action<string>? log = null)
    {
        try { return new ThemeRead(AtomicFile.ReadWithRetry(() => Theme.Load(path)), false, null); }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            var backup = AtomicFile.BackupPath(path);
            if (!File.Exists(backup)) throw;
            Theme theme;
            try { theme = AtomicFile.ReadWithRetry(() => Theme.Load(backup)); }
            catch { ExceptionDispatchInfo.Capture(ex).Throw(); throw; }   // the backup is no better: report the theme file's own problem
            var problem = $"{Path.GetFileName(path)} is damaged ({ex.Message}), so the version before the last save " +
                          $"({Path.GetFileName(backup)}) is shown. Save the theme to repair {Path.GetFileName(path)}.";
            log?.Invoke($"{path}: {problem}");
            return new ThemeRead(theme, true, problem);
        }
    }
}

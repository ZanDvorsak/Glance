using Glance.Core.Storage;

namespace Glance.Core.Themes;

/// <param name="Theme">The theme that was read.</param>
/// <param name="FromBackup">True when theme.json could not be read and theme.json.bak was used instead.</param>
/// <param name="Problem">A sentence for the user when the backup was used, otherwise null.</param>
public sealed record ThemeRead(Theme Theme, bool FromBackup, string? Problem);

/// <summary>Reads a saved theme the way the editor and the runner do. Seam: same behaviour as before for now.</summary>
public static class ThemeFile
{
    public static ThemeRead Load(string path, Action<string>? log = null) =>
        new(AtomicFile.ReadWithRetry(() => Theme.Load(path)), false, null);
}

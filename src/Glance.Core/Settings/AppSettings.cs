using System.Text.Json;
using Glance.Core.Storage;

namespace Glance.Core.Settings;

public sealed class AppSettings
{
    public string? LastTheme { get; set; }
    public int Brightness { get; set; } = 7;
    /// <summary>While the editor sends to the display, keep it on the page being edited instead of rotating.</summary>
    public bool PinDisplayToPage { get; set; }
    /// <summary>The setup check opens by itself the first time something required is missing; this remembers that it did.</summary>
    public bool SetupCheckShown { get; set; }

    /// <summary>%AppData%\Glance. The first time it is needed, settings and themes from the app's earlier name (%AppData%\MiniLcd) are copied over.</summary>
    public static string Folder { get; } = ResolveFolder();
    public static string ThemesFolder => Path.Combine(Folder, "Themes");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    private static string ResolveFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var current = Path.Combine(appData, "Glance");
        var legacy = Path.Combine(appData, "MiniLcd");
        try
        {
            if (!Directory.Exists(current) && Directory.Exists(legacy))
            {
                // copy into a temporary folder and move it into place at the end, so the editor and the runner
                // starting together never see a half-copied folder. The old folder is left untouched as a backup.
                var staging = current + ".migrating-" + Environment.ProcessId;
                CopyDirectory(legacy, staging);

                var settings = Path.Combine(staging, "settings.json");
                if (File.Exists(settings))   // LastTheme is an absolute path into the old folder
                    File.WriteAllText(settings, File.ReadAllText(settings)
                        .Replace(@"\\MiniLcd\\", @"\\Glance\\").Replace("/MiniLcd/", "/Glance/"));

                try { Directory.Move(staging, current); }
                catch { try { Directory.Delete(staging, true); } catch { } }   // the other process got there first
            }
        }
        catch { /* migration is best effort; a fresh folder is created on first save */ }
        return current;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }


    // ---- reading ------------------------------------------------------------------------------------------------

    /// <summary>Corrupt copies kept next to settings.json; older ones are deleted.</summary>
    internal const int KeepCorruptCopies = 5;
    /// <summary>glance.log is started afresh (the old one kept as glance.log.old) once it is this big.</summary>
    internal const long MaxLogBytes = 512 * 1024;

    /// <summary>
    /// True when these settings stand in for a file that exists but could not be read. They must never be saved,
    /// or the user's real settings would be replaced by the defaults (review M1). Not serialized.
    /// </summary>
    private bool _standIn;

    /// <summary>Reads %AppData%\Glance\settings.json. Problems go to glance.log unless a log is given.</summary>
    public static SettingsRead Read(Action<string>? log = null) => Read(FilePath, log ?? DefaultLog);

    /// <summary>
    /// Reads a settings file and says what it found (see <see cref="SettingsFileState"/>):
    /// <list type="bullet">
    /// <item>missing: the defaults, nothing logged (first run);</item>
    /// <item>not valid JSON, even after waiting a moment in case another program is still writing it: <c>Corrupt</c>;</item>
    /// <item>valid JSON that is not settings (wrong types, <c>null</c>, an array): <c>WrongContent</c>, without waiting;</item>
    /// <item>both of those are logged and renamed to <c>settings.json.corrupt-&lt;time&gt;</c> (the newest
    /// <see cref="KeepCorruptCopies"/> are kept) so no save can overwrite them, and the defaults are used;</item>
    /// <item>still locked by another program after the retries: <c>Unreadable</c>. The defaults are returned, but they
    /// refuse to be saved (<see cref="TrySave"/>), so the file keeps the user's settings.</item>
    /// </list>
    /// Leftover temporary files from a crashed save are removed first.
    /// </summary>
    public static SettingsRead Read(string path, Action<string>? log = null)
    {
        AtomicFile.CleanUpStale(path);
        string text;
        try
        {
            // a syntax error is retried: another program may be halfway through writing the file in place
            text = AtomicFile.ReadWithRetry(() =>
            {
                var t = File.ReadAllText(path);
                using (JsonDocument.Parse(t)) { }
                return t;
            });
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new SettingsRead(new AppSettings(), SettingsFileState.Missing, null, null);
        }
        catch (JsonException ex)
        {
            return Damaged(path, SettingsFileState.Corrupt, "it is not valid JSON: " + ex.Message, log);
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not read {path}: {ex.Message} Settings will not be saved until they can be read.");
            return new SettingsRead(new AppSettings { _standIn = true }, SettingsFileState.Unreadable, null,
                "Your settings could not be read (another program has the file open), so default settings are in use " +
                "and changes to them will not be saved. Restart Glance to try again.");
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(text) ?? throw new JsonException("it holds null");
            return new SettingsRead(settings, SettingsFileState.Loaded, null, null);
        }
        catch (JsonException ex)
        {
            return Damaged(path, SettingsFileState.WrongContent, "it is valid JSON but not Glance settings: " + ex.Message, log);
        }
    }

    public static AppSettings Load(Action<string>? log = null) => Read(log).Settings;

    /// <summary>The settings to use, whatever was found; see <see cref="Read(string, Action{string}?)"/>.</summary>
    public static AppSettings Load(string path, Action<string>? log = null) => Read(path, log).Settings;

    public static bool TryLoad(out AppSettings settings, Action<string>? log = null) => TryLoad(FilePath, out settings, log ?? DefaultLog);

    /// <summary>Like <see cref="Read(string, Action{string}?)"/>; false only when the file exists but stayed locked (<c>Unreadable</c>).</summary>
    public static bool TryLoad(string path, out AppSettings settings, Action<string>? log = null)
    {
        var read = Read(path, log);
        settings = read.Settings;
        return read.State != SettingsFileState.Unreadable;
    }

    /// <summary>Keeps a damaged settings file aside (renamed, or copied if it cannot be renamed), logs it and returns the defaults.</summary>
    private static SettingsRead Damaged(string path, SettingsFileState state, string reason, Action<string>? log)
    {
        var defaults = new AppSettings();
        var stem = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        var aside = stem;
        for (var n = 2; File.Exists(aside); n++) aside = $"{stem}-{n}";
        try
        {
            try { File.Move(path, aside); }
            catch (FileNotFoundException) { return new SettingsRead(defaults, SettingsFileState.Missing, null, null); }   // another process moved it first
            catch { File.Copy(path, aside); }   // cannot rename it: at least keep a copy before the next save replaces it
        }
        catch (Exception ex)
        {
            log?.Invoke($"The settings file {path} cannot be used ({reason}) and could not be kept aside ({ex.Message}). The default settings are used; the next save replaces it.");
            return new SettingsRead(defaults, state, null, "Your settings file was damaged, so Glance started with default settings.");
        }
        log?.Invoke($"The settings file {path} cannot be used ({reason}). It was kept as {Path.GetFileName(aside)} and the default settings are used.");
        PruneCorruptCopies(path);
        return new SettingsRead(defaults, state, aside,
            $"Your settings file was damaged, so Glance started with default settings. The damaged file was kept as {Path.GetFileName(aside)} in {Path.GetDirectoryName(aside)}.");
    }

    /// <summary>Deletes all but the newest <see cref="KeepCorruptCopies"/> corrupt copies (their names sort by time).</summary>
    private static void PruneCorruptCopies(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var copies = Directory.GetFiles(dir, Path.GetFileName(path) + ".corrupt-*")
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(KeepCorruptCopies);
            foreach (var old in copies) { try { File.Delete(old); } catch { /* best effort */ } }
        }
        catch { /* best effort */ }
    }

    // ---- logging ------------------------------------------------------------------------------------------------

    /// <summary>Where problems go when the caller gives no log (the editor): %AppData%\Glance\glance.log.</summary>
    private static void DefaultLog(string message)
    {
        try { Directory.CreateDirectory(Folder); AppendLog(Path.Combine(Folder, "glance.log"), message, MaxLogBytes); }
        catch { /* logging must never break loading or saving */ }
    }

    /// <summary>Appends one time-stamped line; a log bigger than <paramref name="maxBytes"/> is first renamed to <c>.old</c> (one older log is kept).</summary>
    internal static void AppendLog(string file, string message, long maxBytes)
    {
        var info = new FileInfo(file);
        if (info.Exists && info.Length > maxBytes) File.Move(file, file + ".old", overwrite: true);
        File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
    }

    // ---- saving -------------------------------------------------------------------------------------------------

    /// <summary>Saves to %AppData%\Glance\settings.json. Returns false (and writes to glance.log) when it could not.</summary>
    public bool Save()
    {
        try { Directory.CreateDirectory(Folder); }
        catch (Exception ex) { DefaultLog($"Settings not saved: {ex.Message}"); return false; }
        return TrySave(FilePath, DefaultLog);
    }

    /// <summary>Saves all or nothing; returns false and logs why when it could not (the previous file is then unchanged).</summary>
    public bool TrySave(string path, Action<string>? log = null)
    {
        try { Save(path); return true; }
        catch (Exception ex)
        {
            log?.Invoke($"Settings not saved to {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Saves all or nothing (see <see cref="AtomicFile"/>): a failure leaves the previous file as it was, and throws.
    /// Throws <see cref="InvalidOperationException"/> for settings that stand in for a file that could not be read.
    /// </summary>
    public void Save(string path)
    {
        if (_standIn)
            throw new InvalidOperationException("the settings file could not be read when Glance started, so saving would replace your settings with the defaults.");
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this));
    }
}

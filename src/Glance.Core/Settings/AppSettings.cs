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

    /// <summary>The saved settings, or the defaults when there are none or they cannot be read. See <see cref="TryLoad(string, out AppSettings, Action{string}?)"/>.</summary>
    public static AppSettings Load(Action<string>? log = null) => Load(FilePath, log ?? DefaultLog);

    public static AppSettings Load(string path, Action<string>? log = null)
    {
        TryLoad(path, out var settings, log);
        return settings;
    }

    public static bool TryLoad(out AppSettings settings, Action<string>? log = null) => TryLoad(FilePath, out settings, log ?? DefaultLog);

    /// <summary>
    /// Reads the settings file. Returns true with the saved settings, or with the defaults when the file is missing
    /// or corrupt. A corrupt file (it does not parse, even after waiting a moment in case another program is still
    /// writing it) is logged and renamed to <c>settings.json.corrupt-&lt;time&gt;</c>, so the next save cannot
    /// overwrite it. Returns false, with the defaults, when the file exists but cannot be read (another process
    /// keeps it locked): callers that already have settings should keep them.
    /// </summary>
    public static bool TryLoad(string path, out AppSettings settings, Action<string>? log = null)
    {
        settings = new();
        string? text = null;
        try
        {
            settings = AtomicFile.ReadWithRetry(() =>
            {
                text = File.ReadAllText(path);
                return JsonSerializer.Deserialize<AppSettings>(text) ?? throw new JsonException("The file holds no settings.");
            });
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }   // first run
        catch (JsonException ex)
        {
            KeepAside(path, ex.Message, log);
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"Could not read {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Moves a corrupt settings file out of the way (or, failing that, copies it) and logs where it went.</summary>
    private static void KeepAside(string path, string reason, Action<string>? log)
    {
        var stem = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        var aside = stem;
        for (var n = 2; File.Exists(aside); n++) aside = $"{stem}-{n}";
        try
        {
            try { File.Move(path, aside); }
            catch (FileNotFoundException) { return; }   // another process (the editor or the runner) moved it first
            catch { File.Copy(path, aside); }           // cannot rename it: at least keep a copy before the next save replaces it
            log?.Invoke($"The settings file {path} is corrupt ({reason}). It was kept as {Path.GetFileName(aside)} and the default settings are used.");
        }
        catch (Exception ex)
        {
            log?.Invoke($"The settings file {path} is corrupt ({reason}) and could not be kept aside ({ex.Message}). The default settings are used; the next save replaces it.");
        }
    }

    /// <summary>Where <see cref="Load(Action{string}?)"/> reports problems when the caller gives no log (the editor).</summary>
    private static void DefaultLog(string message)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.AppendAllText(Path.Combine(Folder, "glance.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* logging must never break loading */ }
    }

    // ---- seam for the review fixes (same behaviour as above) ----

    public static SettingsRead Read(Action<string>? log = null) => Read(FilePath, log ?? DefaultLog);

    public static SettingsRead Read(string path, Action<string>? log = null)
    {
        var existed = File.Exists(path);
        var ok = TryLoad(path, out var s, log);
        return new SettingsRead(s, !ok ? SettingsFileState.Unreadable : existed ? SettingsFileState.Loaded : SettingsFileState.Missing, null, null);
    }

    public bool TrySave(string path, Action<string>? log = null)
    {
        try { Save(path); return true; }
        catch { return false; }
    }

    internal static void AppendLog(string file, string message, long maxBytes) =>
        File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");

    /// <summary>Saves to %AppData%\Glance\settings.json. Failures are not fatal and are ignored.</summary>
    public bool Save()
    {
        try { Directory.CreateDirectory(Folder); Save(FilePath); return true; }
        catch { return false; }
    }

    /// <summary>Saves all or nothing (see <see cref="AtomicFile"/>): a failure leaves the previous file as it was, and throws.</summary>
    public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this));
}

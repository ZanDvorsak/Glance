using System.Text.Json;

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

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try { Directory.CreateDirectory(Folder); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch { /* non-fatal */ }
    }
}

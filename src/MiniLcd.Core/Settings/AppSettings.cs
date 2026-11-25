using System.Text.Json;

namespace MiniLcd.Core.Settings;

public sealed class AppSettings
{
    public string? LastTheme { get; set; }
    public int Brightness { get; set; } = 7;
    /// <summary>While the editor sends to the display, keep it on the page being edited instead of rotating.</summary>
    public bool PinDisplayToPage { get; set; }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiniLcd");
    public static string ThemesFolder => Path.Combine(Folder, "Themes");
    private static string FilePath => Path.Combine(Folder, "settings.json");

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

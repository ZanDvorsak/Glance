using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Glance.Core.Themes;

public partial class Theme : ObservableObject
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    [ObservableProperty] private string _name = "Untitled";
    [ObservableProperty] private int _width = 480;
    [ObservableProperty] private int _height = 320;
    [ObservableProperty] private string _backgroundColor = "#FF000000";
    /// <summary>With more than one page, switch between them automatically using each page's duration.</summary>
    [ObservableProperty] private bool _autoRotate = true;

    /// <summary>A theme always has at least one page.</summary>
    public ObservableCollection<ThemePage> Pages { get; set; } = new() { new ThemePage { Name = "Page 1" } };

    /// <summary>Which page the editor is working on. Editor state only; not saved.</summary>
    [JsonIgnore] public int ActivePageIndex { get; set; }

    [JsonIgnore] public ThemePage ActivePage => Pages[Math.Clamp(ActivePageIndex, 0, Pages.Count - 1)];

    /// <summary>The widgets of the page being edited. Most editor and import code works on this.</summary>
    [JsonIgnore] public ObservableCollection<Widget> Widgets => ActivePage.Widgets;

    /// <summary>Only used to read theme files written before pages existed (their widgets sat at the top level).</summary>
    [JsonPropertyName("Widgets")] public List<Widget>? LegacyWidgets { get; set; }

    /// <summary>Folder the theme file lives in; relative image paths resolve against it. Not saved.</summary>
    [JsonIgnore] public string? BaseFolder { get; set; }

    /// <summary>Makes a freshly deserialized theme valid: migrates old single-page files and guarantees one page.</summary>
    private void Normalize()
    {
        if (LegacyWidgets is { Count: > 0 } legacy)
        {
            if (Pages.Count == 0) Pages.Add(new ThemePage { Name = "Page 1" });
            foreach (var w in legacy) Pages[0].Widgets.Add(w);
        }
        LegacyWidgets = null;
        if (Pages.Count == 0) Pages.Add(new ThemePage { Name = "Page 1" });
        ActivePageIndex = Math.Clamp(ActivePageIndex, 0, Pages.Count - 1);
    }

    public static Theme Load(string path)
    {
        var t = JsonSerializer.Deserialize<Theme>(File.ReadAllText(path), Json) ?? new Theme();
        t.Normalize();
        t.BaseFolder = Path.GetDirectoryName(Path.GetFullPath(path));
        return t;
    }

    /// <summary>
    /// Saves the theme and copies the files it uses (images, videos, fonts) into an assets folder next to it.
    /// Absolute paths are always copied; relative paths are copied too when the theme moves to another folder
    /// (Save As), so the new copy never points at files that are only in the old folder.
    /// </summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var assets = Path.Combine(dir, "assets");
        var sameFolder = BaseFolder is not null && SameFolder(BaseFolder, dir);

        // returns the relative path to store, or the original when the file cannot be found
        string Adopt(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var rooted = Path.IsPathRooted(path);
            if (!rooted && sameFolder) return path;          // already next to the theme
            var source = rooted ? path : ResolveAsset(path); // relative paths belong to the old folder
            if (source is null || !File.Exists(source)) return path;
            Directory.CreateDirectory(assets);
            var name = Path.GetFileName(source);
            var dest = Path.Combine(assets, name);
            var n = 1;
            while (File.Exists(dest) && new FileInfo(dest).Length != new FileInfo(source).Length)
                dest = Path.Combine(assets, $"{Path.GetFileNameWithoutExtension(name)}_{n++}{Path.GetExtension(name)}");
            if (!File.Exists(dest)) File.Copy(source, dest);
            return "assets/" + Path.GetFileName(dest);
        }
        foreach (var w in Pages.SelectMany(p => p.Widgets))
        {
            w.ImagePath = Adopt(w.ImagePath);
            w.FontFile = Adopt(w.FontFile);
        }
        BaseFolder = dir;
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    private static bool SameFolder(string a, string b)
    {
        static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);   // Windows paths are case-insensitive
    }

    /// <summary>The whole theme as JSON, used by undo/redo and live preview.</summary>
    public string ToSnapshot() => JsonSerializer.Serialize(this, Json);

    /// <summary>Replaces this theme's content with a snapshot, keeping this instance (and its base folder and active page).</summary>
    public void ApplySnapshot(string json)
    {
        var t = JsonSerializer.Deserialize<Theme>(json, Json) ?? throw new InvalidOperationException("Bad snapshot");
        t.Normalize();
        Name = t.Name; Width = t.Width; Height = t.Height; BackgroundColor = t.BackgroundColor; AutoRotate = t.AutoRotate;
        Pages.Clear();
        foreach (var p in t.Pages) Pages.Add(p);
        ActivePageIndex = Math.Clamp(ActivePageIndex, 0, Pages.Count - 1);
    }

    public string? ResolveAsset(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Path.IsPathRooted(path)) return path;
        return BaseFolder is null ? null : Path.Combine(BaseFolder, path);
    }

    /// <summary>Starter layout resembling the autumn theme: background picture, CPU and GPU panels, clock.</summary>
    public static Theme CreateDefault(string? backgroundImage)
    {
        var t = new Theme { Name = "Autumn starter" };
        if (backgroundImage is not null)
            t.Widgets.Add(new Widget { Kind = WidgetKind.Image, Name = "Background", X = -35, Y = 0, Width = 515, Height = 322, ImagePath = backgroundImage });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Panel, Name = "CPU panel", X = 10, Y = 10, Width = 230, Height = 140, Color = "#78000000", CornerRadius = 10 });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Text, Name = "CPU label", X = 24, Y = 20, Text = "CPU", FontSize = 16 });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Sensor, Name = "CPU temp", Text = "", X = 24, Y = 48, FontSize = 44, Bold = true, SensorId = "/amdcpu/0/temperature/2", Unit = "°C", WarnAt = 75, CritAt = 90 });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Bar, Name = "CPU load bar", X = 24, Y = 118, Width = 200, Height = 12, SensorId = "/amdcpu/0/load/0", Color = "#FFFFA040" });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Panel, Name = "GPU panel", X = 10, Y = 160, Width = 230, Height = 140, Color = "#78000000", CornerRadius = 10 });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Text, Name = "GPU label", X = 24, Y = 170, Text = "GPU", FontSize = 16 });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Sensor, Name = "GPU temp", Text = "", X = 24, Y = 198, FontSize = 44, Bold = true, SensorId = "/gpu-nvidia/0/temperature/0", Unit = "°C", WarnAt = 75, CritAt = 90 });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Bar, Name = "GPU load bar", X = 24, Y = 268, Width = 200, Height = 12, SensorId = "/gpu-nvidia/0/load/0", Color = "#FF40C4FF" });
        t.Widgets.Add(new Widget { Kind = WidgetKind.Clock, Name = "Clock", X = 380, Y = 12, Text = "HH:mm:ss", FontSize = 20 });
        return t;
    }
}

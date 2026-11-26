using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Glance.Core.Storage;

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

    /// <summary>Largest screen side a theme may have. Real displays are far smaller; this only stops absurd allocations.</summary>
    public const int MaxScreenSide = 4096;

    /// <summary>
    /// Makes a freshly deserialized theme valid, whatever the file said: migrates old single-page files, guarantees one page,
    /// replaces null lists, drops null pages and widgets, gives null strings their defaults, maps unknown enum numbers to
    /// the defaults, and clamps numbers that could crash or explode (sizes, font size, durations, NaN/Infinity, values too
    /// big for the property panel's <c>decimal</c>). Every limit is wider than what the editor lets you enter, so a theme
    /// the editor wrote loads unchanged. Runs on <see cref="Load"/> and <see cref="ApplySnapshot"/> (undo, live preview).
    /// </summary>
    internal void Normalize()
    {
        // JSON null is accepted for non-nullable members, so nothing below may assume the file was well formed
        Name ??= "Untitled";
        BackgroundColor ??= "#FF000000";
        Width = Math.Clamp(Width, 1, MaxScreenSide);
        Height = Math.Clamp(Height, 1, MaxScreenSide);
        Pages ??= new();
        // "Pages": [null] in a hand-edited or imported file would otherwise leave a theme with nothing to draw
        for (int i = Pages.Count - 1; i >= 0; i--) if (Pages[i] is null) Pages.RemoveAt(i);
        if (LegacyWidgets is { Count: > 0 } legacy)
        {
            if (Pages.Count == 0) Pages.Add(new ThemePage { Name = "Page 1" });
            Pages[0].Widgets ??= new();
            foreach (var w in legacy) if (w is not null) Pages[0].Widgets.Add(w);
        }
        LegacyWidgets = null;
        if (Pages.Count == 0) Pages.Add(new ThemePage { Name = "Page 1" });
        var ids = new HashSet<string>(StringComparer.Ordinal);   // widget ids are unique across the whole theme
        for (int i = 0; i < Pages.Count; i++) NormalizePage(Pages[i], i, ids);
        ActivePageIndex = Math.Clamp(ActivePageIndex, 0, Pages.Count - 1);
    }

    private static readonly Widget WidgetDefaults = new();
    private static readonly ThemePage PageDefaults = new();

    private static void NormalizePage(ThemePage page, int index, HashSet<string> ids)
    {
        page.Name ??= $"Page {index + 1}";
        page.DurationSeconds = Bound(page.DurationSeconds, 1, 86400, PageDefaults.DurationSeconds);   // the pusher waits at least 1 s anyway
        page.Widgets ??= new();
        for (int i = page.Widgets.Count - 1; i >= 0; i--) if (page.Widgets[i] is null) page.Widgets.RemoveAt(i);
        foreach (var w in page.Widgets) NormalizeWidget(w, ids);
    }

    private static void NormalizeWidget(Widget w, HashSet<string> ids)
    {
        var d = WidgetDefaults;
        // a missing or repeated id gets a new one (the editor finds the selection by id; the first holder keeps it)
        if (string.IsNullOrEmpty(w.Id) || !ids.Add(w.Id)) { w.Id = Guid.NewGuid().ToString("N"); ids.Add(w.Id); }
        w.Name ??= d.Name;
        w.Text ??= "";   // null means "no text", not the placeholder word
        w.SensorId ??= d.SensorId;
        w.Format ??= d.Format;
        w.Unit ??= d.Unit;
        w.FontFamily ??= d.FontFamily;
        w.FontFile ??= d.FontFile;
        w.Color ??= d.Color;
        w.BackColor ??= d.BackColor;
        w.TextColor ??= d.TextColor;
        w.WarnColor ??= d.WarnColor;
        w.CritColor ??= d.CritColor;
        w.ImagePath ??= d.ImagePath;
        w.Group ??= d.Group;

        // JsonStringEnumConverter also accepts plain numbers, so "Kind": 99 deserializes
        if (!Enum.IsDefined(w.Kind)) w.Kind = d.Kind;
        if (!Enum.IsDefined(w.Align)) w.Align = d.Align;
        if (!Enum.IsDefined(w.Fit)) w.Fit = d.Fit;

        // Limits sit well outside the editor's own ranges (PropertiesPanel), so they only touch values no editor wrote.
        const double Far = 100_000, Huge = 1e12;   // Huge: comfortably inside decimal (7.9e28), which the property panel converts to
        w.X = Bound(w.X, -Far, Far, d.X);
        w.Y = Bound(w.Y, -Far, Far, d.Y);
        w.Width = Bound(w.Width, 0, 2 * MaxScreenSide, d.Width);
        w.Height = Bound(w.Height, 0, 2 * MaxScreenSide, d.Height);
        w.FontSize = Bound(w.FontSize, 1, 1000, d.FontSize);
        w.Thickness = Bound(w.Thickness, 0, 1000, d.Thickness);
        w.CornerRadius = Bound(w.CornerRadius, 0, MaxScreenSide, d.CornerRadius);
        w.Opacity = Bound(w.Opacity, 0, 100, d.Opacity);
        w.HistorySeconds = Bound(w.HistorySeconds, 1, 86400, d.HistorySeconds);
        w.Sweep = Bound(w.Sweep, -360, 360, d.Sweep);
        w.StartAngle = Bound(w.StartAngle, -Far, Far, d.StartAngle);
        w.Multiplier = Bound(w.Multiplier, -Huge, Huge, d.Multiplier);
        w.Min = Bound(w.Min, -Huge, Huge, d.Min);
        w.Max = Bound(w.Max, -Huge, Huge, d.Max);
        w.WarnAt = Bound(w.WarnAt, -Huge, Huge, d.WarnAt);
        w.CritAt = Bound(w.CritAt, -Huge, Huge, d.CritAt);
    }

    /// <summary>Clamps to [lo, hi]; NaN becomes the default, +/-Infinity the nearest limit.</summary>
    private static double Bound(double v, double lo, double hi, double fallback) => double.IsNaN(v) ? fallback : Math.Clamp(v, lo, hi);

    public static Theme Load(string path)
    {
        var t = JsonSerializer.Deserialize<Theme>(File.ReadAllText(path), Json) ?? new Theme();
        t.Normalize();
        t.BaseFolder = Path.GetDirectoryName(Path.GetFullPath(path));
        return t;
    }

    /// <summary>
    /// Saves the theme and copies the files it uses (images, videos, fonts) into an assets folder next to it.
    /// Absolute paths are always copied. Relative paths are copied too when the theme moves to another folder
    /// (Save As), so the new copy never points at files that are only in the old folder. A theme that was never
    /// saved has no folder to resolve relative paths against, so those are kept as they are, like missing files.
    /// Save As flattens subfolders: every copied file lands directly in <c>assets/</c>.
    /// <para>All or nothing: the files are copied and the theme file is written first; only then are the widget
    /// paths and <see cref="BaseFolder"/> changed. If anything fails, the files this call copied are removed,
    /// the previous theme file is kept, and the theme object is unchanged.</para>
    /// </summary>
    /// <returns>True when the theme moved from one folder to another (Save As), so older relative paths no longer resolve.</returns>
    public bool Save(string path)
    {
        var target = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(dir);
        var assets = Path.Combine(dir, "assets");
        var assetsExisted = Directory.Exists(assets);
        // String comparison only: a junction or subst drive looks like another folder, which costs a redundant copy, nothing worse.
        var sameFolder = BaseFolder is not null && SameFolder(BaseFolder, dir);
        var created = new List<string>();
        var adopted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // source file -> stored path

        // returns the relative path to store, or the stored value as it is when the file cannot be found
        string Adopt(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored;
            var rooted = Path.IsPathRooted(stored);
            if (!rooted && sameFolder) return stored;            // already next to the theme
            // Relative paths belong to the old folder. Rooted paths go through the same gate as the renderer, so Save never
            // opens a network or device path (that would send the user's credentials to whatever host a theme names).
            // A network file the user picks is copied in at pick time (PickedFile.Localize), so it never needs to get here.
            var source = ResolveAsset(stored);
            if (source is null || !File.Exists(source)) return stored;
            source = Path.GetFullPath(source);
            if (adopted.TryGetValue(source, out var known)) return known;
            Directory.CreateDirectory(assets);
            var name = Path.GetFileName(source);
            var dest = Path.Combine(assets, name);
            // an existing file is reused only when it has the same content; otherwise pick the next free name
            for (var n = 1; File.Exists(dest) && !SameContent(source, dest); n++)
                dest = Path.Combine(assets, $"{Path.GetFileNameWithoutExtension(name)}_{n}{Path.GetExtension(name)}");
            if (!File.Exists(dest)) { File.Copy(source, dest); created.Add(dest); }
            return adopted[source] = "assets/" + Path.GetFileName(dest);
        }

        var widgets = Pages.SelectMany(p => p.Widgets).ToList();
        try
        {
            // 1) copy every file and work out the new paths, without touching the theme
            var updates = widgets.Select(w => (Image: Adopt(w.ImagePath), Font: Adopt(w.FontFile))).ToList();

            // 2) write the theme file from a copy that has the new paths, then swap it in
            var copy = JsonSerializer.Deserialize<Theme>(ToSnapshot(), Json)!;
            var copyWidgets = copy.Pages.SelectMany(p => p.Widgets).ToList();
            for (var i = 0; i < updates.Count; i++) { copyWidgets[i].ImagePath = updates[i].Image; copyWidgets[i].FontFile = updates[i].Font; }
            // all or nothing, flushed to disk, and the replaced file is kept as theme.json.bak (see AtomicFile)
            AtomicFile.WriteAllText(target, JsonSerializer.Serialize(copy, Json), keepBackup: true);

            // 3) everything is on disk: now change the open theme
            for (var i = 0; i < updates.Count; i++) { widgets[i].ImagePath = updates[i].Image; widgets[i].FontFile = updates[i].Font; }
            var moved = BaseFolder is not null && !sameFolder;
            BaseFolder = dir;
            return moved;
        }
        catch
        {
            foreach (var f in created) TryDelete(f);
            if (!assetsExisted) try { Directory.Delete(assets); } catch { /* not empty or gone: leave it */ }
            throw;
        }
    }

    private static void TryDelete(string file) { try { File.Delete(file); } catch { /* best effort while undoing a failed save */ } }

    private static bool SameFolder(string a, string b)
    {
        static string Norm(string p) => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);   // Windows paths are case-insensitive
    }

    /// <summary>True when both paths are the same file or hold exactly the same bytes.</summary>
    private static bool SameContent(string a, string b)
    {
        if (string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase)) return true;
        if (new FileInfo(a).Length != new FileInfo(b).Length) return false;
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        var ba = new byte[81920]; var bb = new byte[81920];
        while (true)
        {
            var na = fa.ReadAtLeast(ba, ba.Length, throwOnEndOfStream: false);
            var nb = fb.ReadAtLeast(bb, bb.Length, throwOnEndOfStream: false);
            if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            if (na == 0) return true;
        }
    }

    /// <summary>The whole theme as JSON, used by undo/redo and live preview.</summary>
    public string ToSnapshot() => JsonSerializer.Serialize(this, Json);

    /// <summary>Replaces this theme's content with a snapshot, keeping this instance (and its base folder and active page).</summary>
    public void ApplySnapshot(string json)
    {
        var t = JsonSerializer.Deserialize<Theme>(json, Json) ?? throw new InvalidOperationException("Bad snapshot");
        t.Normalize();
        Name = t.Name; Width = t.Width; Height = t.Height; BackgroundColor = t.BackgroundColor; AutoRotate = t.AutoRotate;
        // Replace page by page instead of Clear() + Add: a display pusher may be drawing this same theme on another thread,
        // and the list must never be empty while it looks (Normalize guarantees the snapshot has at least one page).
        // LIMIT: this is "never empty", not atomic or thread-safe. For a moment the list can mix old and new pages, and it
        // still shrinks while another thread may be reading it, so a background reader that indexes Pages directly can still
        // go out of range. DisplayPusher copies the list first (StablePages); any other background reader must do the same
        // until background renderers get an immutable snapshot.
        for (int i = 0; i < t.Pages.Count; i++)
        {
            if (i < Pages.Count) Pages[i] = t.Pages[i];
            else Pages.Add(t.Pages[i]);
        }
        while (Pages.Count > t.Pages.Count) Pages.RemoveAt(Pages.Count - 1);
        ActivePageIndex = Math.Clamp(ActivePageIndex, 0, Pages.Count - 1);
    }

    /// <summary>
    /// The file a widget path points at, or null when there is none to open. This is the one gate for every reader of
    /// widget paths: the renderer (images, videos, fonts), Export and Save.
    /// <list type="bullet">
    /// <item>Fully qualified local paths (<c>C:\...</c>, also mapped drives the user set up) are returned as they are,
    /// because the editor uses them for files picked but not yet saved into the theme.</item>
    /// <item>Rooted paths that are not fully qualified (<c>\x.png</c>, <c>C:x.png</c>) are refused: they depend on the
    /// process's current drive and folder, which may be on a share.</item>
    /// <item>Relative paths resolve against <see cref="BaseFolder"/>, and the result is checked again, so a theme whose
    /// folder is on a share (opened from there, or sent with <c>LIVE</c>) resolves nothing either.</item>
    /// <item>Network and device paths (<c>\\server\share</c>, <c>//server</c>, <c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>) are
    /// never resolved: opening one makes Windows sign in to that machine with the user's credentials (and the runner does it
    /// elevated), and a device path can read raw disks.</item>
    /// </list>
    /// Glance never stores such paths itself: a network file the user picks is copied into the theme at once
    /// (<see cref="PickedFile.Localize"/>). <see cref="AssetProblem"/> says why a path was refused.
    /// </summary>
    public string? ResolveAsset(string path) => Resolve(path, out _);

    /// <summary>Why <see cref="ResolveAsset"/> refuses <paramref name="path"/>, as a short sentence for the editor;
    /// null when the path is empty, resolves, or only lacks a saved theme folder.</summary>
    public string? AssetProblem(string path)
    {
        Resolve(path, out var problem);
        return problem;
    }

    private string? Resolve(string path, out string? problem)
    {
        problem = null;
        if (string.IsNullOrEmpty(path)) return null;
        if (IsNetworkOrDevicePath(path))
        {
            problem = "Glance does not open files on network or device paths. Use the browse button: a picked file is copied into the theme.";
            return null;
        }
        if (Path.IsPathRooted(path))
        {
            if (Path.IsPathFullyQualified(path)) return path;
            problem = @"This path depends on the current drive or folder. Use a full path such as C:\Pictures\bg.png, or the browse button.";
            return null;
        }
        if (BaseFolder is null) return null;
        var full = Path.Combine(BaseFolder, path);
        if (!IsNetworkOrDevicePath(full)) return full;
        problem = "This theme is stored in a network folder, and Glance does not open files from network folders. Keep themes in a local folder.";
        return null;
    }

    /// <summary>True for UNC and device paths: two leading slashes in either direction (<c>\\server</c>, <c>//server</c>,
    /// <c>\\?\</c>, <c>\\.\</c>) or the NT object prefix <c>\??\</c>.</summary>
    public static bool IsNetworkOrDevicePath(string path)
    {
        static bool Sep(char c) => c is '\\' or '/';
        var p = path.TrimStart();
        if (p.Length >= 2 && Sep(p[0]) && Sep(p[1])) return true;
        return p.Length >= 4 && Sep(p[0]) && p[1] == '?' && p[2] == '?' && Sep(p[3]);
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

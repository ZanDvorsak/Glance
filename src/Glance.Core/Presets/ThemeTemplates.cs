using Glance.Core.Sensors;
using Glance.Core.Themes;

namespace Glance.Core.Presets;

/// <summary>A complete starter theme, composed from the premade widgets so it binds to this PC's own sensors.</summary>
public sealed class ThemeTemplate
{
    private readonly Func<Composer, Theme> _builder;

    internal ThemeTemplate(string id, string name, string description, Func<Composer, Theme> builder)
    { Id = id; Name = name; Description = description; _builder = builder; }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }

    public Theme Build(IReadOnlyList<SensorInfo> sensors, PresetOptions options) => _builder(new Composer(sensors, options));
}

/// <summary>Places premade widgets onto a theme page at given positions, giving each a group so it moves as one.</summary>
internal sealed class Composer
{
    private readonly Dictionary<string, int> _counts = new();
    public IReadOnlyList<SensorInfo> Sensors { get; }
    public PresetOptions Options { get; }

    public Composer(IReadOnlyList<SensorInfo> sensors, PresetOptions options) { Sensors = sensors; Options = options; }

    public Theme NewTheme(string name, string background, string firstPageName = "Page 1")
    {
        var t = new Theme { Name = name, BackgroundColor = background };
        t.Pages[0].Name = firstPageName;
        return t;
    }

    public void Add(ThemePage page, string presetId, double x, double y)
    {
        var preset = PresetLibrary.All.First(p => p.Id == presetId);
        var result = preset.Build(Sensors, Options);
        _counts[preset.Name] = _counts.GetValueOrDefault(preset.Name) + 1;
        var group = $"{preset.Name} {_counts[preset.Name]}";
        foreach (var w in result.Widgets) { w.X += x; w.Y += y; w.Group = group; page.Widgets.Add(w); }
    }
}

public static class ThemeTemplates
{
    public static IReadOnlyList<ThemeTemplate> All { get; } = new List<ThemeTemplate>
    {
        new("minimal", "Minimal", "Big clock, CPU and GPU temperature, memory and video memory. Calm and readable.", c =>
        {
            var t = c.NewTheme("Minimal", "#FF0B0D12");
            var p = t.Pages[0];
            c.Add(p, "clock-card", 125, 8);
            c.Add(p, "cpu-temp-card", 20, 110); c.Add(p, "gpu-temp-card", 250, 110);
            c.Add(p, "ram-card", 20, 216); c.Add(p, "vram-card", 250, 216);
            return t;
        }),

        new("dashboard", "Dashboard", "Six gauges at a glance: temperatures, memory, and CPU, GPU and video memory load.", c =>
        {
            var t = c.NewTheme("Dashboard", "#FF0D0F14");
            var p = t.Pages[0];
            c.Add(p, "cpu-temp-gauge", 10, 10); c.Add(p, "gpu-temp-gauge", 165, 10); c.Add(p, "ram-ring", 320, 10);
            c.Add(p, "cpu-load-ring", 10, 165); c.Add(p, "gpu-load-ring", 165, 165); c.Add(p, "vram-ring", 320, 165);
            return t;
        }),

        new("gamer", "Gaming", "GPU first: a large GPU overview with a CPU overview and two temperature gauges.", c =>
        {
            var t = c.NewTheme("Gaming", "#FF10080C");
            var p = t.Pages[0];
            c.Add(p, "gpu-overview", 8, 8); c.Add(p, "cpu-overview", 8, 166);
            c.Add(p, "gpu-temp-gauge", 300, 8); c.Add(p, "cpu-temp-gauge", 300, 166);
            return t;
        }),

        new("pages", "Overview + history", "Two pages that rotate: an overview, then live graphs of CPU and GPU temperature.", c =>
        {
            var t = c.NewTheme("Overview and history", "#FF0C0E13", "Overview");
            var overview = t.Pages[0];
            c.Add(overview, "cpu-overview", 8, 8); c.Add(overview, "gpu-overview", 8, 148);
            c.Add(overview, "ram-ring", 300, 8); c.Add(overview, "vram-ring", 300, 162);

            var history = new ThemePage { Name = "History", DurationSeconds = 8 };
            t.Pages.Add(history);
            c.Add(history, "cpu-temp-graph", 110, 4); c.Add(history, "gpu-temp-graph", 110, 118);
            c.Add(history, "clock-compact", 165, 240);
            return t;
        }),

        new("desk-clock", "Desk clock", "A huge clock and date, with CPU and GPU temperature underneath.", c =>
        {
            var t = c.NewTheme("Desk clock", "#FF0A0C10");
            var p = t.Pages[0];
            p.Widgets.Add(new Widget { Kind = WidgetKind.Clock, Name = "Time", X = 240, Y = 14, Text = "HH:mm", FontSize = 120, Bold = true, Align = TextAlign.Center, Color = "#FFFFFFFF" });
            p.Widgets.Add(new Widget { Kind = WidgetKind.Clock, Name = "Date", X = 240, Y = 158, Text = "dddd, d MMMM", FontSize = 26, Align = TextAlign.Center, Color = "#FF9AA4B5" });
            c.Add(p, "temps-pair", 90, 214);
            return t;
        }),

        new("blank", "Blank", "An empty screen. Add widgets from the gallery or the palette.", c => c.NewTheme("New theme", "#FF000000")),
    };
}

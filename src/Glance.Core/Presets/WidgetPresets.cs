using Glance.Core.Import;
using Glance.Core.Sensors;
using Glance.Core.Themes;

namespace Glance.Core.Presets;

public enum PanelStyle { Glass, Solid, None }

public sealed record PresetOptions(string Accent = "#FF40C4FF", PanelStyle Panel = PanelStyle.Glass);

/// <summary>The widgets of a preset laid out from (0,0), their overall size, and any sensors that could not be found on this PC.</summary>
public sealed record PresetResult(List<Widget> Widgets, double Width, double Height, List<string> Missing);

public sealed class WidgetPreset
{
    private readonly Func<Ctx, PresetResult> _builder;

    internal WidgetPreset(string id, string name, string category, string description, Func<Ctx, PresetResult> builder)
    { Id = id; Name = name; Category = category; Description = description; _builder = builder; }

    public string Id { get; }
    public string Name { get; }
    public string Category { get; }
    public string Description { get; }

    /// <summary>Creates the widgets for this PC, laid out from (0,0), using the real sensors found in <paramref name="sensors"/>.</summary>
    public PresetResult Build(IReadOnlyList<SensorInfo> sensors, PresetOptions options) => _builder(new Ctx(sensors, options));
}

/// <summary>Builder context: finds real sensors on this PC and creates consistently styled widgets.</summary>
internal sealed class Ctx
{
    private readonly AidaSensorMap _map;
    public PresetOptions Options { get; }
    public IReadOnlyList<SensorInfo> Sensors { get; }
    public List<string> Missing { get; } = new();

    public const string Muted = "#FF9AA4B5";
    public const string White = "#FFFFFFFF";

    public Ctx(IReadOnlyList<SensorInfo> sensors, PresetOptions options) { Sensors = sensors; Options = options; _map = new AidaSensorMap(sensors); }

    public string Accent => Options.Accent;

    public static readonly string[] CpuIds = { "/amdcpu/", "/intelcpu/", "/cpu/" };
    public static readonly string[] GpuIds = { "/gpu-", "/gpu/" };
    public static readonly string[] StorageIds = { "/nvme/", "/hdd/", "/ssd/", "/storage/" };

    /// <summary>Looks a sensor up by hardware, type and name (exact name first, then partial), recording it as missing when absent.</summary>
    public (string id, double mult) Find(string what, string[] idHas, string type, params string[] names)
    {
        var candidates = Sensors.Where(s => s.Type == type && idHas.Any(h => s.Id.Contains(h, StringComparison.OrdinalIgnoreCase))).ToList();
        SensorInfo? hit = null;
        foreach (var n in names)
        {
            hit = candidates.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                  ?? candidates.FirstOrDefault(s => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) break;
        }
        hit ??= candidates.FirstOrDefault();
        if (hit is null) { Missing.Add(what); return ("", 1); }
        return (hit.Id, 1);
    }

    /// <summary>Per-core CPU load sensors ("CPU Core #1" ...), in core order, capped at 32.</summary>
    public List<SensorInfo> CpuCoreLoads() =>
        Sensors.Where(s => s.Type == "Load" && CpuIds.Any(h => s.Id.Contains(h, StringComparison.OrdinalIgnoreCase))
                           && System.Text.RegularExpressions.Regex.IsMatch(s.Name, @"^CPU Core #\d+$"))
               .OrderBy(s => int.Parse(System.Text.RegularExpressions.Regex.Match(s.Name, @"\d+").Value)).Take(32).ToList();

    /// <summary>Finds a sensor by its AIDA64-style code (TCPU, SGPU1UTI, ...). <paramref name="aidaUnit"/> controls unit conversion.</summary>
    public (string id, double mult) Sensor(string code, string what, string aidaUnit = "")
    {
        var m = _map.Resolve(code, aidaUnit);
        if (m?.Sensor is null) { Missing.Add(what); return ("", 1); }
        return (m.Sensor.Id, m.Multiplier);
    }

    public Widget? Panel(double w, double h)
    {
        var color = Options.Panel switch { PanelStyle.Solid => "#FF161B26", PanelStyle.Glass => "#A00B0E14", _ => null };
        return color is null ? null : new Widget { Kind = WidgetKind.Panel, Name = "Card", X = 0, Y = 0, Width = w, Height = h, Color = color, CornerRadius = 14 };
    }

    public Widget Label(string text, double x, double y, double size = 13, string color = Muted, TextAlign align = TextAlign.Left, bool bold = false) =>
        new() { Kind = WidgetKind.Text, Name = text.Length > 18 ? text[..18] : text, X = x, Y = y, Text = text, FontSize = size, Color = color, Align = align, Bold = bold };

    public Widget Value(string name, (string id, double mult) s, double x, double y, double size, string unit, string format = "0",
        TextAlign align = TextAlign.Left, double warn = 0, double crit = 0, string? color = null, bool bold = true) =>
        new()
        {
            Kind = WidgetKind.Sensor, Name = name, X = x, Y = y, Text = "", SensorId = s.id, Multiplier = s.mult, Unit = unit, Format = format,
            FontSize = size, Bold = bold, Align = align, Color = color ?? White, WarnAt = warn, CritAt = crit,
        };

    public Widget Bar(string name, (string id, double mult) s, double x, double y, double w, double h, double min = 0, double max = 100, double warn = 0, double crit = 0) =>
        new() { Kind = WidgetKind.Bar, Name = name, X = x, Y = y, Width = w, Height = h, SensorId = s.id, Multiplier = s.mult, Min = min, Max = max, Color = Accent, BackColor = "#33FFFFFF", CornerRadius = h / 2, WarnAt = warn, CritAt = crit };

    public Widget Gauge(string name, (string id, double mult) s, double x, double y, double size, string unit, string caption, double min, double max,
        double warn = 0, double crit = 0, bool ring = false) =>
        new()
        {
            Kind = WidgetKind.Gauge, Name = name, X = x, Y = y, Width = size, Height = size, SensorId = s.id, Multiplier = s.mult, Unit = unit, Text = caption,
            Min = min, Max = max, FontSize = size * 0.22, Bold = true, Thickness = Math.Max(6, size * 0.09), Color = Accent, BackColor = "#33FFFFFF",
            StartAngle = ring ? -90 : 135, Sweep = ring ? 360 : 270, WarnAt = warn, CritAt = crit,
        };

    public Widget Graph(string name, (string id, double mult) s, double x, double y, double w, double h, double min = 0, double max = 0, double warn = 0, double crit = 0, double seconds = 60) =>
        new()
        {
            Kind = WidgetKind.Graph, Name = name, X = x, Y = y, Width = w, Height = h, SensorId = s.id, Multiplier = s.mult, Min = min, Max = max,
            Thickness = 2, Color = Accent, BackColor = "#22FFFFFF", HistorySeconds = seconds, WarnAt = warn, CritAt = crit,
        };

    public PresetResult Done(double w, double h, params Widget?[] widgets) =>
        new(widgets.Where(x => x is not null).Select(x => x!).ToList(), w, h, Missing.Distinct().ToList());
}

public static class PresetLibrary
{
    public static readonly string[] Categories = { "Temperature", "Utilization", "Overview", "System" };

    public static IReadOnlyList<WidgetPreset> All { get; } = Build();

    private static WidgetPreset P(string id, string name, string category, string description, Func<Ctx, PresetResult> build) =>
        new(id, name, category, description, build);

    private static List<WidgetPreset> Build() => new()
    {
        // ---- temperature ---------------------------------------------------------------------------------------
        P("cpu-temp-card", "CPU temperature", "Temperature", "Big value with a level bar. Turns amber at 75 and red at 90.", c =>
            TempCard(c, "CPU", "TCPU", "CPU temperature")),
        P("gpu-temp-card", "GPU temperature", "Temperature", "Big value with a level bar. Turns amber at 75 and red at 90.", c =>
            TempCard(c, "GPU", "TGPU1", "GPU temperature")),
        P("cpu-temp-gauge", "CPU temperature gauge", "Temperature", "Arc gauge with the value in the middle.", c =>
            GaugeCard(c, "CPU", "TCPU", "CPU temperature", "°C", 0, 100, 75, 90, ring: false)),
        P("gpu-temp-gauge", "GPU temperature gauge", "Temperature", "Arc gauge with the value in the middle.", c =>
            GaugeCard(c, "GPU", "TGPU1", "GPU temperature", "°C", 0, 100, 75, 90, ring: false)),
        P("cpu-temp-graph", "CPU temperature graph", "Temperature", "Last minute of CPU temperature.", c =>
            GraphCard(c, "CPU temperature", "TCPU", "CPU temperature", "°C", 0, 0, 75, 90)),
        P("gpu-temp-graph", "GPU temperature graph", "Temperature", "Last minute of GPU temperature.", c =>
            GraphCard(c, "GPU temperature", "TGPU1", "GPU temperature", "°C", 0, 0, 75, 90)),

        // ---- utilization ---------------------------------------------------------------------------------------
        P("cpu-load-card", "CPU utilization", "Utilization", "Percentage with a level bar.", c =>
            LoadCard(c, "CPU", "SCPUUTI", "CPU utilization")),
        P("gpu-load-card", "GPU utilization", "Utilization", "Percentage with a level bar.", c =>
            LoadCard(c, "GPU", "SGPU1UTI", "GPU utilization")),
        P("cpu-load-ring", "CPU utilization ring", "Utilization", "Full ring gauge.", c =>
            GaugeCard(c, "CPU", "SCPUUTI", "CPU utilization", "%", 0, 100, 80, 95, ring: true)),
        P("gpu-load-ring", "GPU utilization ring", "Utilization", "Full ring gauge.", c =>
            GaugeCard(c, "GPU", "SGPU1UTI", "GPU utilization", "%", 0, 100, 80, 95, ring: true)),
        P("cpu-load-graph", "CPU utilization graph", "Utilization", "Last minute of CPU load.", c =>
            GraphCard(c, "CPU utilization", "SCPUUTI", "CPU utilization", "%", 0, 100, 0, 0)),
        P("gpu-load-graph", "GPU utilization graph", "Utilization", "Last minute of GPU load.", c =>
            GraphCard(c, "GPU utilization", "SGPU1UTI", "GPU utilization", "%", 0, 100, 0, 0)),
        P("ram-card", "Memory usage", "Utilization", "RAM percentage, level bar and GB in use.", c =>
        {
            var load = c.Sensor("SMEMUTI", "Memory utilization");
            var used = c.Sensor("SUSEDMEM", "Memory used", " GB");
            return c.Done(210, 96, c.Panel(210, 96), c.Label("MEMORY", 14, 10), c.Value("Memory %", load, 14, 28, 40, "%", warn: 80, crit: 95),
                c.Value("Memory used", used, 196, 30, 15, " GB used", "0.0", TextAlign.Right, color: Ctx.Muted, bold: false),
                c.Bar("Memory bar", load, 14, 78, 182, 8, warn: 80, crit: 95));
        }),
        P("vram-card", "Video memory usage", "Utilization", "GPU memory percentage with a level bar.", c =>
        {
            var load = c.Sensor("SVMEMUSAGE", "Video memory utilization");
            return c.Done(210, 96, c.Panel(210, 96), c.Label("VRAM", 14, 10), c.Value("VRAM %", load, 14, 28, 40, "%", warn: 85, crit: 95),
                c.Bar("VRAM bar", load, 14, 78, 182, 8, warn: 85, crit: 95));
        }),

        // ---- overview ------------------------------------------------------------------------------------------
        P("cpu-overview", "CPU overview", "Overview", "Temperature, utilization, clock and a load bar in one card.", c =>
        {
            var temp = c.Sensor("TCPU", "CPU temperature");
            var load = c.Sensor("SCPUUTI", "CPU utilization");
            var clock = c.Sensor("SCPUCLK", "CPU clock");
            return c.Done(262, 130, c.Panel(262, 130), c.Label("CPU", 14, 10),
                c.Value("CPU temp", temp, 14, 30, 42, "°C", warn: 75, crit: 90),
                c.Label("LOAD", 128, 34, 11), c.Value("CPU load", load, 248, 30, 20, "%", align: TextAlign.Right),
                c.Label("CLOCK", 128, 62, 11), c.Value("CPU clock", clock, 248, 58, 16, " MHz", align: TextAlign.Right, color: Ctx.Muted, bold: false),
                c.Bar("CPU load bar", load, 14, 108, 234, 8));
        }),
        P("gpu-overview", "GPU overview", "Overview", "Temperature, utilization, video memory, clock and a load bar.", c =>
        {
            var temp = c.Sensor("TGPU1", "GPU temperature");
            var load = c.Sensor("SGPU1UTI", "GPU utilization");
            var vram = c.Sensor("SVMEMUSAGE", "Video memory utilization");
            var clock = c.Sensor("SGPU1CLK", "GPU clock");
            return c.Done(262, 150, c.Panel(262, 150), c.Label("GPU", 14, 10),
                c.Value("GPU temp", temp, 14, 30, 42, "°C", warn: 75, crit: 90),
                c.Label("LOAD", 128, 34, 11), c.Value("GPU load", load, 248, 30, 20, "%", align: TextAlign.Right),
                c.Label("VRAM", 128, 60, 11), c.Value("VRAM", vram, 248, 56, 16, "%", align: TextAlign.Right, color: Ctx.Muted, bold: false),
                c.Label("CLOCK", 128, 82, 11), c.Value("GPU clock", clock, 248, 78, 16, " MHz", align: TextAlign.Right, color: Ctx.Muted, bold: false),
                c.Bar("GPU load bar", load, 14, 128, 234, 8));
        }),

        // ---- system --------------------------------------------------------------------------------------------
        P("clock-card", "Clock and date", "System", "Large time with the date underneath.", c =>
            c.Done(230, 92, c.Panel(230, 92),
                new Widget { Kind = WidgetKind.Clock, Name = "Time", X = 14, Y = 8, Text = "HH:mm:ss", FontSize = 44, Bold = true, Color = Ctx.White },
                new Widget { Kind = WidgetKind.Clock, Name = "Date", X = 16, Y = 62, Text = "dddd, d MMMM", FontSize = 15, Color = Ctx.Muted })),
        P("drive-card", "Drive usage", "System", "Used and free space with a fill bar (first fixed drive).", c =>
        {
            var drive = c.Sensors.Where(s => s.Id.StartsWith("/drive/") && s.Id.EndsWith("/load/0")).OrderBy(s => s.Id).FirstOrDefault();
            string letter = drive is null ? "c" : drive.Id.Split('/')[2];
            (string, double) Find(string suffix, string what) =>
                c.Sensors.FirstOrDefault(s => s.Id == $"/drive/{letter}/{suffix}") is { } x ? (x.Id, 1.0) : Miss(c, what);
            var used = Find("data/0", "Drive used space"); var free = Find("data/1", "Drive free space"); var pct = Find("load/0", "Drive utilization");
            return c.Done(210, 96, c.Panel(210, 96), c.Label($"DRIVE {letter.ToUpperInvariant()}:", 14, 10),
                c.Value("Used", used, 14, 28, 24, " GB", "0"), c.Value("Free", free, 196, 36, 13, " GB free", "0", TextAlign.Right, color: Ctx.Muted, bold: false),
                c.Bar("Drive bar", pct, 14, 78, 182, 8, warn: 85, crit: 95));
        }),
        P("network-card", "Network speed", "System", "Download and upload speed.", c =>
        {
            var down = c.Sensor("SNIC1DLRATE", "Download speed", "KB/s");
            var up = c.Sensor("SNIC1ULRATE", "Upload speed", "KB/s");
            return c.Done(220, 92, c.Panel(220, 92), c.Label("NETWORK", 14, 10),
                c.Label("↓", 14, 30, 20, c.Accent, bold: true), c.Value("Download", down, 206, 32, 20, " KB/s", "0", TextAlign.Right),
                c.Label("↑", 14, 56, 20, c.Accent, bold: true), c.Value("Upload", up, 206, 58, 20, " KB/s", "0", TextAlign.Right));
        }),
        P("fan-card", "CPU fan speed", "System", "Fan speed in RPM.", c =>
        {
            var fan = c.Sensor("FCPU", "CPU fan");
            return c.Done(200, 84, c.Panel(200, 84), c.Label("CPU FAN", 14, 10), c.Value("CPU fan", fan, 14, 32, 30, " RPM"));
        }),
        P("gpu-fan-card", "GPU fan speed", "System", "Fan speed in RPM.", c =>
        {
            var fan = c.Sensor("FGPU1", "GPU fan");
            return c.Done(200, 84, c.Panel(200, 84), c.Label("GPU FAN", 14, 10), c.Value("GPU fan", fan, 14, 32, 30, " RPM"));
        }),
        P("cpu-power-card", "CPU power", "System", "Package power in watts.", c =>
        {
            var p = c.Find("CPU power", Ctx.CpuIds, "Power", "Package");
            return c.Done(180, 84, c.Panel(180, 84), c.Label("CPU POWER", 14, 10), c.Value("CPU power", p, 14, 32, 30, " W"));
        }),
        P("gpu-power-card", "GPU power", "System", "Board power in watts.", c =>
        {
            var p = c.Find("GPU power", Ctx.GpuIds, "Power", "GPU Package", "Package", "Power");
            return c.Done(180, 84, c.Panel(180, 84), c.Label("GPU POWER", 14, 10), c.Value("GPU power", p, 14, 32, 30, " W"));
        }),
        P("cpu-clock-card", "CPU clock", "System", "Average core clock in MHz.", c =>
        {
            var clk = c.Sensor("SCPUCLK", "CPU clock");
            return c.Done(210, 84, c.Panel(210, 84), c.Label("CPU CLOCK", 14, 10), c.Value("CPU clock", clk, 14, 32, 30, " MHz"));
        }),
        P("disk-activity-card", "Disk activity", "System", "How busy the first drive is, with a level bar.", c =>
        {
            var a = c.Find("Disk activity", Ctx.StorageIds, "Load", "Total Activity");
            return c.Done(210, 96, c.Panel(210, 96), c.Label("DISK ACTIVITY", 14, 10), c.Value("Disk activity", a, 14, 28, 40, "%", warn: 80, crit: 95),
                c.Bar("Disk bar", a, 14, 78, 182, 8, warn: 80, crit: 95));
        }),
        P("cpu-cores", "CPU cores", "Utilization", "One vertical bar per core, so you can see the load spread.", c =>
        {
            var cores = c.CpuCoreLoads();
            if (cores.Count == 0) { c.Missing.Add("CPU core load"); cores = new List<SensorInfo>(); }
            const double barW = 10, gap = 4, top = 36, barH = 70;
            double width = Math.Max(160, 28 + cores.Count * (barW + gap) - gap), height = 120;
            var items = new List<Widget?> { c.Panel(width, height), c.Label("CPU CORES", 14, 10) };
            for (int i = 0; i < cores.Count; i++)
            {
                var b = c.Bar($"Core {i + 1}", (cores[i].Id, 1.0), 14 + i * (barW + gap), top, barW, barH, warn: 80, crit: 95);
                b.Vertical = true; b.CornerRadius = 3;
                items.Add(b);
            }
            return c.Done(width, height, items.ToArray());
        }),
        P("ram-ring", "Memory ring", "Utilization", "Full ring with the memory percentage.", c =>
            GaugeCard(c, "RAM", "SMEMUTI", "Memory utilization", "%", 0, 100, 80, 95, ring: true)),
        P("vram-ring", "Video memory ring", "Utilization", "Full ring with the video memory percentage.", c =>
            GaugeCard(c, "VRAM", "SVMEMUSAGE", "Video memory utilization", "%", 0, 100, 85, 95, ring: true)),
        P("drive-ring", "Drive usage ring", "System", "Full ring showing how full the first drive is.", c =>
        {
            var drive = c.Sensors.Where(s => s.Id.StartsWith("/drive/") && s.Id.EndsWith("/load/0")).OrderBy(s => s.Id).FirstOrDefault();
            if (drive is null) c.Missing.Add("Drive utilization");
            string letter = drive is null ? "C" : drive.Id.Split('/')[2].ToUpperInvariant();
            var s = (drive?.Id ?? "", 1.0);
            return c.Done(150, 150, c.Panel(150, 150), c.Gauge($"Drive {letter} ring", s, 10, 10, 130, "%", $"DRIVE {letter}:", 0, 100, 85, 95, ring: true));
        }),
        P("network-graph", "Download speed graph", "System", "Last minute of download speed.", c =>
            GraphCard(c, "Download speed", "SNIC1DLRATE", "Download speed", " KB/s", 0, 0, 0, 0, "KB/s")),
        P("temps-pair", "CPU and GPU temperature", "Temperature", "Both temperatures side by side with level bars.", c =>
        {
            var cpu = c.Sensor("TCPU", "CPU temperature"); var gpu = c.Sensor("TGPU1", "GPU temperature");
            return c.Done(300, 96, c.Panel(300, 96),
                c.Label("CPU", 14, 10), c.Value("CPU temp", cpu, 14, 28, 40, "°C", warn: 75, crit: 90), c.Bar("CPU bar", cpu, 14, 78, 122, 8, warn: 75, crit: 90),
                c.Label("GPU", 164, 10), c.Value("GPU temp", gpu, 164, 28, 40, "°C", warn: 75, crit: 90), c.Bar("GPU bar", gpu, 164, 78, 122, 8, warn: 75, crit: 90));
        }),
        P("clock-compact", "Compact clock", "System", "Just the time, small.", c =>
            c.Done(150, 56, c.Panel(150, 56),
                new Widget { Kind = WidgetKind.Clock, Name = "Time", X = 14, Y = 8, Text = "HH:mm", FontSize = 34, Bold = true, Color = Ctx.White })),
    };

    private static (string, double) Miss(Ctx c, string what) { c.Missing.Add(what); return ("", 1.0); }

    // ---- shared card layouts ------------------------------------------------------------------------------------

    private static PresetResult TempCard(Ctx c, string title, string code, string what)
    {
        var s = c.Sensor(code, what);
        return c.Done(210, 96, c.Panel(210, 96), c.Label(title, 14, 10), c.Value(title + " temp", s, 14, 28, 40, "°C", warn: 75, crit: 90),
            c.Bar(title + " temp bar", s, 14, 78, 182, 8, warn: 75, crit: 90));
    }

    private static PresetResult LoadCard(Ctx c, string title, string code, string what)
    {
        var s = c.Sensor(code, what);
        return c.Done(210, 96, c.Panel(210, 96), c.Label(title, 14, 10), c.Value(title + " load", s, 14, 28, 40, "%", warn: 80, crit: 95),
            c.Bar(title + " load bar", s, 14, 78, 182, 8, warn: 80, crit: 95));
    }

    private static PresetResult GaugeCard(Ctx c, string title, string code, string what, string unit, double min, double max, double warn, double crit, bool ring)
    {
        var s = c.Sensor(code, what);
        return c.Done(150, 150, c.Panel(150, 150), c.Gauge(title + " gauge", s, 10, 10, 130, unit, title, min, max, warn, crit, ring));
    }

    private static PresetResult GraphCard(Ctx c, string title, string code, string what, string unit, double min, double max, double warn, double crit, string aidaUnit = "")
    {
        var s = c.Sensor(code, what, aidaUnit);
        const double w = 260, h = 110;
        return c.Done(w, h, c.Panel(w, h), c.Label(title.ToUpperInvariant(), 14, 10, 12), c.Value(title + " value", s, w - 14, 6, 20, unit, "0", TextAlign.Right, warn, crit),
            c.Graph(title + " graph", s, 12, 36, w - 24, h - 46, min, max, warn, crit));
    }
}

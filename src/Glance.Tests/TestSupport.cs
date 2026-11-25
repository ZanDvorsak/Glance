using Glance.Core.Sensors;
using Glance.Core.Themes;
using SkiaSharp;

namespace Glance.Tests;

/// <summary>A sensor source over a fixed list, for tests.</summary>
internal sealed class FakeSource : ISensorSource
{
    public FakeSource(IReadOnlyList<SensorInfo> sensors) => Snapshot = sensors;
    public IReadOnlyList<SensorInfo> Snapshot { get; set; }
    public void Update() { }
}

internal static class Fake
{
    private static SensorInfo S(string id, string hw, string name, string type, float? value, string unit) => new(id, hw, name, type, value, unit);

    /// <summary>
    /// A PC shaped like a real LibreHardwareMonitor feed: Ryzen CPU with 8 cores, NVIDIA GPU, RAM, motherboard chip,
    /// NVMe drive, a network adapter and drive-space sensors. Ids follow LibreHardwareMonitor's format.
    /// </summary>
    public static List<SensorInfo> Typical()
    {
        const string cpu = "AMD Ryzen 7 5800X", gpu = "NVIDIA GeForce RTX 4070 Ti";
        var list = new List<SensorInfo>
        {
            S("/amdcpu/0/temperature/2", cpu, "Core (Tctl/Tdie)", "Temperature", 65, "°C"),
            S("/amdcpu/0/temperature/3", cpu, "CCD1 (Tdie)", "Temperature", 60, "°C"),
            S("/amdcpu/0/load/0", cpu, "CPU Total", "Load", 40, "%"),
            S("/amdcpu/0/clock/1", cpu, "Core #1", "Clock", 4500, "MHz"),
            S("/amdcpu/0/power/0", cpu, "Package", "Power", 90, "W"),

            S("/gpu-nvidia/0/temperature/0", gpu, "GPU Core", "Temperature", 55, "°C"),
            S("/gpu-nvidia/0/temperature/2", gpu, "GPU Hot Spot", "Temperature", 63, "°C"),
            S("/gpu-nvidia/0/temperature/3", gpu, "GPU Memory Junction", "Temperature", 60, "°C"),
            S("/gpu-nvidia/0/load/0", gpu, "GPU Core", "Load", 30, "%"),
            S("/gpu-nvidia/0/load/3", gpu, "GPU Memory", "Load", 45, "%"),
            S("/gpu-nvidia/0/clock/0", gpu, "GPU Core", "Clock", 2000, "MHz"),
            S("/gpu-nvidia/0/clock/1", gpu, "GPU Memory", "Clock", 10500, "MHz"),
            S("/gpu-nvidia/0/power/0", gpu, "GPU Package", "Power", 150, "W"),
            S("/gpu-nvidia/0/fan/0", gpu, "GPU Fan", "Fan", 1200, "RPM"),

            S("/ram/load/0", "Total Memory", "Memory", "Load", 60, "%"),
            S("/ram/data/0", "Total Memory", "Memory Used", "Data", 20, "GB"),
            S("/ram/data/1", "Total Memory", "Memory Available", "Data", 12, "GB"),

            S("/lpc/nct6798d/0/fan/1", "Nuvoton NCT6798D", "Fan #1", "Fan", 900, "RPM"),
            S("/lpc/nct6798d/0/temperature/0", "Nuvoton NCT6798D", "System", "Temperature", 35, "°C"),

            S("/nvme/0/load/0", "Samsung SSD 980 PRO", "Used Space", "Load", 52, "%"),
            S("/nvme/0/load/1", "Samsung SSD 980 PRO", "Total Activity", "Load", 5, "%"),

            S("/nic/{GUID}/throughput/7", "Ethernet", "Download Speed", "Throughput", 204800, "B/s"),
            S("/nic/{GUID}/throughput/6", "Ethernet", "Upload Speed", "Throughput", 10240, "B/s"),

            S("/drive/c/data/0", "Drive C:", "C: Used Space", "Data", 475, "GB"),
            S("/drive/c/data/1", "Drive C:", "C: Free Space", "Data", 455, "GB"),
            S("/drive/c/data/2", "Drive C:", "C: Total Size", "Data", 930, "GB"),
            S("/drive/c/load/0", "Drive C:", "C: Utilization", "Load", 51, "%"),
        };
        for (int i = 1; i <= 8; i++)
            list.Add(S($"/amdcpu/0/load/{i}", cpu, $"CPU Core #{i}", "Load", 10 + i * 8, "%"));
        return list;
    }

    /// <summary>Renders one page of a theme into a bitmap and returns it (caller disposes).</summary>
    public static SKBitmap Render(Theme theme, ISensorSource? sensors, ThemePage? page = null, ThemeRenderer? renderer = null)
    {
        var own = renderer is null;
        renderer ??= new ThemeRenderer();
        try
        {
            var bmp = new SKBitmap(new SKImageInfo(theme.Width, theme.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bmp);
            for (int i = 0; i < 2; i++) renderer.Render(canvas, theme, sensors, null, page);   // twice: text bounds settle after a first pass
            return bmp;
        }
        finally { if (own) renderer.Dispose(); }
    }

    public static bool Near(SKColor c, byte r, byte g, byte b, int tolerance = 12) =>
        Math.Abs(c.Red - r) <= tolerance && Math.Abs(c.Green - g) <= tolerance && Math.Abs(c.Blue - b) <= tolerance;

    /// <summary>A small theme with a black background for pixel tests.</summary>
    public static Theme Canvas(int w = 100, int h = 100) => new() { Width = w, Height = h, BackgroundColor = "#FF000000" };
}

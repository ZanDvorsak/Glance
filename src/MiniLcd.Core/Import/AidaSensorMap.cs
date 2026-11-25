using System.Text.RegularExpressions;
using MiniLcd.Core.Sensors;

namespace MiniLcd.Core.Import;

/// <summary>Maps AIDA64 item codes (TCPU, SGPU1UTI, ...) to LibreHardwareMonitor sensors on this machine.</summary>
public sealed class AidaSensorMap
{
    private static readonly string[] Cpu = { "/amdcpu/", "/intelcpu/", "/cpu/" };
    private static readonly string[] Gpu = { "/gpu-", "/gpu/" };
    private static readonly string[] Ram = { "/ram/" };
    private static readonly string[] Storage = { "/nvme/", "/hdd/", "/ssd/", "/storage/" };
    private static readonly string[] Nic = { "/nic/" };
    private static readonly string[] Lpc = { "/lpc/", "/embeddedcontroller/" };

    private readonly IReadOnlyList<SensorInfo> _sensors;
    public AidaSensorMap(IReadOnlyList<SensorInfo> sensors) => _sensors = sensors;

    public record Mapping(SensorInfo? Sensor, double Multiplier = 1);

    private SensorInfo? Find(string[] idHas, string type, params string[] names)
    {
        var c = _sensors.Where(s => s.Type == type && idHas.Any(h => s.Id.Contains(h, StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var n in names)
        {
            var m = c.FirstOrDefault(s => s.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                    ?? c.FirstOrDefault(s => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
            if (m is not null) return m;
        }
        return names.Length == 0 ? c.FirstOrDefault() : null;
    }

    private SensorInfo? FindOrFirst(string[] idHas, string type, params string[] names)
        => Find(idHas, type, names) ?? Find(idHas, type);

    /// <summary>Returns null when the code is not a sensor value we know how to map.</summary>
    public Mapping? Resolve(string code, string aidaUnit)
    {
        code = code.ToUpperInvariant();
        SensorInfo? s = code switch
        {
            "TCPU" or "TCPUPKG" or "TCPUDIO" => FindOrFirst(Cpu, "Temperature", "Package", "Tctl", "Tdie", "Core"),
            "TGPU1" => FindOrFirst(Gpu, "Temperature", "GPU Core"),
            "TGPU1HOT" => Find(Gpu, "Temperature", "Hot Spot"),
            "TGPU1MEM" => Find(Gpu, "Temperature", "Memory Junction", "Memory"),
            "TMOBO" => FindOrFirst(Lpc, "Temperature", "Motherboard", "System", "Temperature #1"),
            "SCPUUTI" => Find(Cpu, "Load", "CPU Total"),
            "SCPUCLK" => Find(Cpu, "Clock", "Cores (Average)", "Core #1"),
            "SMEMUTI" => Find(Ram, "Load", "Memory"),
            "SUSEDMEM" => Find(Ram, "Data", "Memory Used"),
            "SFREEMEM" => Find(Ram, "Data", "Memory Available"),
            "SGPU1UTI" => Find(Gpu, "Load", "GPU Core"),
            "SGPU1CLK" => Find(Gpu, "Clock", "GPU Core"),
            "SGPU1MEMCLK" => Find(Gpu, "Clock", "GPU Memory"),
            "SVMEMUSAGE" => Find(Gpu, "Load", "GPU Memory", "Memory Controller"),
            "SNIC1DLRATE" or "SNIC1DLSPEED" => Find(Nic, "Throughput", "Download"),
            "SNIC1ULRATE" or "SNIC1ULSPEED" => Find(Nic, "Throughput", "Upload"),
            "FCPU" => Find(Lpc, "Fan", "CPU", "Fan #1"),
            "FGPU1" => Find(Gpu, "Fan"),
            _ => null,
        };

        if (s is null)
        {
            var drv = Regex.Match(code, @"^SDRV([A-Z])(UTI|USEDSPC|FREESPC)$");
            if (drv.Success)
            {
                var l = drv.Groups[1].Value.ToLowerInvariant();
                var wanted = drv.Groups[2].Value switch { "UTI" => "load/0", "USEDSPC" => "data/0", _ => "data/1" };
                s = _sensors.FirstOrDefault(x => x.Id == $"/drive/{l}/{wanted}");
            }
        }
        if (s is null)
        {
            var core = Regex.Match(code, @"^SCPU(\d+)(UTI|CLK)$");
            if (core.Success)
                s = core.Groups[2].Value == "UTI"
                    ? Find(Cpu, "Load", $"CPU Core #{core.Groups[1].Value}")
                    : Find(Cpu, "Clock", $"Core #{core.Groups[1].Value}");
        }
        return s is null ? null : new Mapping(s, UnitMultiplier(s.Unit, aidaUnit));
    }

    /// <summary>AIDA shows MB / KB/s where LibreHardwareMonitor reports GB / B/s.</summary>
    private static double UnitMultiplier(string lhmUnit, string aidaUnit)
    {
        var a = aidaUnit.Trim().ToUpperInvariant();
        return (lhmUnit, a) switch
        {
            ("GB", "MB") => 1024,
            ("MB", "GB") => 1.0 / 1024,
            ("B/s", "KB/S") => 1.0 / 1024,
            ("B/s", "MB/S") => 1.0 / (1024 * 1024),
            _ => 1,
        };
    }

    /// <summary>Codes that are clocks rather than sensors, with a .NET date format.</summary>
    public static string? ClockFormat(string code) => code.ToUpperInvariant() switch
    {
        "SDATE" => "d",
        "STIME" => "HH:mm:ss",
        "SDAY" => "dddd",
        _ => null,
    };
}

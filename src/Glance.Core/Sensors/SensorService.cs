using LibreHardwareMonitor.Hardware;

namespace Glance.Core.Sensors;

public sealed record SensorInfo(string Id, string Hardware, string Name, string Type, float? Value, string Unit);

/// <summary>Reads every sensor LibreHardwareMonitor can find (CPU, GPU, motherboard, fans, drives, ...).</summary>
public sealed class SensorService : ISensorSource, IDisposable
{
    private readonly Computer _computer;
    private readonly object _lock = new();
    private List<SensorInfo> _snapshot = new();

    public SensorService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsStorageEnabled = true,
            IsNetworkEnabled = true,
            IsBatteryEnabled = true,
            IsPsuEnabled = true,
        };
        _computer.Open();
    }

    public IReadOnlyList<SensorInfo> Snapshot { get { lock (_lock) return _snapshot; } }

    /// <summary>Polls all hardware and refreshes <see cref="Snapshot"/>.</summary>
    public void Update()
    {
        var list = new List<SensorInfo>();
        foreach (var hw in _computer.Hardware) Collect(hw, list);
        lock (_lock) _snapshot = list;
    }

    private static void Collect(IHardware hw, List<SensorInfo> list)
    {
        hw.Update();
        foreach (var s in hw.Sensors)
            list.Add(new SensorInfo(s.Identifier.ToString(), hw.Name, s.Name, s.SensorType.ToString(), s.Value, UnitFor(s.SensorType)));
        foreach (var sub in hw.SubHardware) Collect(sub, list);
    }

    public SensorInfo? Find(string id) => Snapshot.FirstOrDefault(s => s.Id == id);

    private static string UnitFor(SensorType t) => t switch
    {
        SensorType.Temperature => "°C",
        SensorType.Load or SensorType.Level or SensorType.Control or SensorType.Humidity => "%",
        SensorType.Clock => "MHz",
        SensorType.Fan => "RPM",
        SensorType.Voltage => "V",
        SensorType.Power => "W",
        SensorType.Current => "A",
        SensorType.Frequency => "Hz",
        SensorType.Flow => "L/h",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.Energy => "mWh",
        _ => "",
    };

    public void Dispose() => _computer.Close();
}

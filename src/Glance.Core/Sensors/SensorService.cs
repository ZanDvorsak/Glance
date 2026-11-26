using LibreHardwareMonitor.Hardware;

namespace Glance.Core.Sensors;

public sealed record SensorInfo(string Id, string Hardware, string Name, string Type, float? Value, string Unit);

/// <summary>One sensor of a <see cref="IHardwareNode"/>, as read after <see cref="IHardwareNode.Update"/>.</summary>
public sealed record SensorReading(string Id, string Name, SensorType Type, float? Value);

/// <summary>
/// One piece of hardware as <see cref="SensorService"/> reads it: LibreHardwareMonitor's <see cref="IHardware"/> on a real PC,
/// fakes in tests (so tests never read real sensors).
/// </summary>
public interface IHardwareNode
{
    string Name { get; }
    void Update();
    IEnumerable<SensorReading> Readings { get; }
    IEnumerable<IHardwareNode> Children { get; }
}

/// <summary>Reads every sensor LibreHardwareMonitor can find (CPU, GPU, motherboard, fans, drives, ...).</summary>
public sealed class SensorService : ISensorSource, IDisposable
{
    private readonly Func<IEnumerable<IHardwareNode>> _roots;
    private readonly Action _close;
    private readonly object _lock = new();
    private List<SensorInfo> _snapshot = new();

    public SensorService()
    {
        var computer = new Computer
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
        computer.Open();
        _roots = () => computer.Hardware.Select(h => (IHardwareNode)new LhmNode(h));
        _close = computer.Close;
    }

    /// <summary>Tests: reads the given hardware instead of this PC's; <paramref name="close"/> stands for closing LibreHardwareMonitor.</summary>
    internal SensorService(Func<IEnumerable<IHardwareNode>> roots, Action? close = null, Action<string>? log = null, Func<long>? nowMs = null)
    {
        _roots = roots;
        _close = close ?? (() => { });
    }

    public IReadOnlyList<SensorInfo> Snapshot { get { lock (_lock) return _snapshot; } }

    /// <summary>Polls all hardware and refreshes <see cref="Snapshot"/>.</summary>
    public void Update()
    {
        var list = new List<SensorInfo>();
        foreach (var hw in _roots()) Collect(hw, list);
        lock (_lock) _snapshot = list;
    }

    private static void Collect(IHardwareNode hw, List<SensorInfo> list)
    {
        hw.Update();
        foreach (var s in hw.Readings)
            list.Add(new SensorInfo(s.Id, hw.Name, s.Name, s.Type.ToString(), s.Value, UnitFor(s.Type)));
        foreach (var sub in hw.Children) Collect(sub, list);
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

    public void Dispose() => _close();

    /// <summary>LibreHardwareMonitor's hardware as an <see cref="IHardwareNode"/>.</summary>
    private sealed class LhmNode : IHardwareNode
    {
        private readonly IHardware _hw;
        public LhmNode(IHardware hw) => _hw = hw;
        public string Name => _hw.Name;
        public void Update() => _hw.Update();
        public IEnumerable<SensorReading> Readings => _hw.Sensors.Select(s => new SensorReading(s.Identifier.ToString(), s.Name, s.SensorType, s.Value));
        public IEnumerable<IHardwareNode> Children => _hw.SubHardware.Select(h => (IHardwareNode)new LhmNode(h));
    }
}

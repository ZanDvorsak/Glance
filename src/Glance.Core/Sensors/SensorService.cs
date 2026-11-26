using Glance.Core.Service;
using LibreHardwareMonitor.Hardware;

namespace Glance.Core.Sensors;

public sealed record SensorInfo(string Id, string Hardware, string Name, string Type, float? Value, string Unit)
{
    /// <summary>NaN and infinities are "no value" (review M8): JSON cannot carry them, and widgets cannot draw them.</summary>
    public static float? Finite(float? value) => value is { } v && float.IsFinite(v) ? v : null;
}

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

/// <summary>
/// Reads every sensor LibreHardwareMonitor can find (CPU, GPU, motherboard, fans, drives, ...).
/// Each device is read on its own: one that fails keeps its sensors in the list with no value, and the others keep updating
/// (review M8). <see cref="Dispose"/> never closes LibreHardwareMonitor while an update runs.
/// </summary>
public sealed class SensorService : ISensorSource, IDisposable
{
    /// <summary>How long <see cref="Dispose"/> waits for an update in progress; after that, the update closes when it ends.</summary>
    public static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(3);

    private readonly Func<IEnumerable<IHardwareNode>> _roots;
    private readonly Action _close;
    private readonly Action<string>? _log;
    private readonly LogLimiter _limiter;
    private readonly object _lock = new();     // the snapshot
    private readonly object _gate = new();     // an update, or the close
    private volatile bool _disposed;
    private bool _closed;
    private List<SensorInfo> _snapshot = new();

    /// <param name="log">Where a failing device is reported (the service log); the same line is written once a minute.</param>
    public SensorService(Action<string>? log = null)
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
        _roots = () => computer.Hardware.Select(h => (IHardwareNode)new LhmNode(h)).ToList();
        _close = computer.Close;
        _log = log;
        _limiter = new LogLimiter(TimeSpan.FromMinutes(1));
    }

    /// <summary>Tests: reads the given hardware instead of this PC's; <paramref name="close"/> stands for closing LibreHardwareMonitor.</summary>
    internal SensorService(Func<IEnumerable<IHardwareNode>> roots, Action? close = null, Action<string>? log = null, Func<long>? nowMs = null)
    {
        _roots = roots;
        _close = close ?? (() => { });
        _log = log;
        _limiter = new LogLimiter(TimeSpan.FromMinutes(1), nowMs);
    }

    public IReadOnlyList<SensorInfo> Snapshot { get { lock (_lock) return _snapshot; } }

    /// <summary>
    /// Polls all hardware and refreshes <see cref="Snapshot"/>. A failing device does not stop the others; only a failure to
    /// list the hardware at all throws (the snapshot then stays as it was, and the caller must not report it as new).
    /// After <see cref="Dispose"/> it does nothing.
    /// </summary>
    public void Update()
    {
        lock (_gate)
        {
            try
            {
                if (_disposed) return;
                var list = new List<SensorInfo>();
                foreach (var hw in _roots()) Collect(hw, list);
                lock (_lock) _snapshot = list;
            }
            finally
            {
                if (_disposed) CloseOnce();   // Dispose gave up waiting for this update: it closes now that it is over
            }
        }
    }

    private void Collect(IHardwareNode hw, List<SensorInfo> list)
    {
        string name = "?";
        bool failed = false;
        try { name = hw.Name; hw.Update(); }
        catch (Exception ex) { failed = true; Log($"Sensors: reading \"{name}\" failed: {ex.GetType().Name}: {ex.Message}"); }
        try
        {
            foreach (var s in hw.Readings)   // a device that failed keeps its sensors, with no value: widgets show "no value", not old numbers
                list.Add(new SensorInfo(s.Id, name, s.Name, s.Type.ToString(), failed ? null : SensorInfo.Finite(s.Value), UnitFor(s.Type)));
            foreach (var sub in hw.Children) Collect(sub, list);
        }
        catch (Exception ex) { Log($"Sensors: listing \"{name}\" failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void Log(string message)
    {
        try { if (_limiter.Filter(message) is { } line) _log?.Invoke(line); } catch { /* never */ }
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

    /// <summary>
    /// Closes LibreHardwareMonitor, but never in the middle of an update: waits up to <see cref="DisposeWait"/> for one in
    /// progress, and if it is still running then, that update closes it when it ends.
    /// </summary>
    public void Dispose() => Dispose(DisposeWait);

    /// <summary>As <see cref="Dispose()"/>, waiting at most <paramref name="wait"/>. True if it is closed now, false if the running update will close it.</summary>
    public bool Dispose(TimeSpan wait)
    {
        _disposed = true;
        if (!Monitor.TryEnter(_gate, wait)) return false;   // the running update closes when it is over
        try { CloseOnce(); return true; }
        finally { Monitor.Exit(_gate); }
    }

    private void CloseOnce()   // under _gate
    {
        if (_closed) return;
        _closed = true;
        try { _close(); }
        catch (Exception ex) { Log("Sensors: closing failed: " + ex.Message); }
    }

    /// <summary>LibreHardwareMonitor's hardware as an <see cref="IHardwareNode"/>.</summary>
    private sealed class LhmNode : IHardwareNode
    {
        private readonly IHardware _hw;
        public LhmNode(IHardware hw) => _hw = hw;
        public string Name => _hw.Name;
        public void Update() => _hw.Update();
        public IEnumerable<SensorReading> Readings => _hw.Sensors.Select(s => new SensorReading(s.Identifier.ToString(), s.Name, s.SensorType, s.Value)).ToList();
        public IEnumerable<IHardwareNode> Children => _hw.SubHardware.Select(h => (IHardwareNode)new LhmNode(h)).ToList();
    }
}

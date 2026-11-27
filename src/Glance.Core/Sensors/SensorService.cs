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

    /// <summary>How many failed updates in a row a device's sensors keep their last good value (review L1).</summary>
    public const int FailedUpdatesKept = 3;

    // per sensor id: the last good value and how many updates in a row its device has failed since (under _gate)
    private Dictionary<string, (float? Value, int Failures)> _held = new(), _nextHeld = new();

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
                _nextHeld = new Dictionary<string, (float?, int)>();
                foreach (var hw in _roots()) Collect(hw, list);
                _held = _nextHeld;   // sensors that are gone are forgotten
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
        catch (Exception ex) { failed = true; Log($"reading {name} {ex.GetType().Name}", $"Sensors: reading \"{name}\" failed: {ex.GetType().Name}: {ex.Message}"); }
        try
        {
            foreach (var s in hw.Readings)
                list.Add(new SensorInfo(s.Id, name, s.Name, s.Type.ToString(), failed ? Held(s.Id) : Fresh(s.Id, SensorInfo.Finite(s.Value)), UnitFor(s.Type)));
            foreach (var sub in hw.Children) Collect(sub, list);
        }
        catch (Exception ex) { Log($"listing {name} {ex.GetType().Name}", $"Sensors: listing \"{name}\" failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>A sensor read now: remember its value in case its device fails next time.</summary>
    private float? Fresh(string id, float? value)
    {
        _nextHeld[id] = (value, 0);
        return value;
    }

    /// <summary>
    /// A sensor whose device failed this update: its last good value for up to <see cref="FailedUpdatesKept"/> failed updates in
    /// a row (devices such as the EC, SMBus or NVMe fail now and then, and the display should not blink), then no value, so a
    /// device that has really stopped never shows old numbers as live (review L1).
    /// </summary>
    private float? Held(string id)
    {
        var (value, failures) = _held.TryGetValue(id, out var h) ? h : (null, FailedUpdatesKept);
        failures++;
        var shown = failures <= FailedUpdatesKept ? value : null;
        _nextHeld[id] = (value, failures);
        return shown;
    }

    /// <param name="key">What counts as "the same line" for the once-a-minute limit: without the error text, which can change every time.</param>
    private void Log(string key, string message)
    {
        try { if (_limiter.Filter(key, message) is { } line) _log?.Invoke(line); } catch { /* never */ }
    }

    private void Log(string message) => Log(message, message);

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

namespace Glance.Core.Sensors;

/// <summary>
/// Reads all hardware sensors in this process (needs administrator rights for CPU/motherboard data)
/// plus drive-space sensors, refreshing in the background once per second.
/// </summary>
public sealed class LocalSensorSource : ISensorSource, IDisposable
{
    private readonly SensorService _service = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile IReadOnlyList<SensorInfo> _merged = Array.Empty<SensorInfo>();
    private List<SensorInfo> _drives = new();
    private long _drivesAt;

    /// <summary>Hardware sensors only (what the editor's pipe client expects; it adds drives itself).</summary>
    public IReadOnlyList<SensorInfo> HardwareSnapshot => _service.Snapshot;

    public IReadOnlyList<SensorInfo> Snapshot => _merged;
    public void Update() { }

    public event Action? Updated;

    public LocalSensorSource()
    {
        _service.Update();
        Merge();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { return; }
                try { _service.Update(); Merge(); Updated?.Invoke(); } catch { /* keep going */ }
            }
        });
    }

    private void Merge()
    {
        if (Environment.TickCount64 - _drivesAt > 5000) { _drives = DriveSensors.Read(); _drivesAt = Environment.TickCount64; }
        _merged = _service.Snapshot.Concat(_drives).ToList();
    }

    public void Dispose() { _cts.Cancel(); _service.Dispose(); }
}

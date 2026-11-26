using Glance.Core.Service;

namespace Glance.Core.Sensors;

/// <summary>
/// Reads all hardware sensors in this process (needs administrator rights for CPU/motherboard data)
/// plus drive-space sensors, refreshing in the background once per second.
/// <see cref="Updated"/> is raised only after an update that read the hardware, so a stalled or failing loop publishes
/// nothing new and the service's pipe clients see the feed age (review M8).
/// </summary>
public sealed class LocalSensorSource : ISensorSource, IDisposable
{
    private readonly SensorService _service;
    private readonly Func<List<SensorInfo>> _readDrives;
    private readonly Action<string>? _log;
    private readonly LogLimiter _limiter = new(TimeSpan.FromMinutes(1));
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private volatile IReadOnlyList<SensorInfo> _merged = Array.Empty<SensorInfo>();
    private List<SensorInfo> _drives = new();
    private long _drivesAt;
    private int _disposed;

    /// <summary>Hardware sensors only (what the editor's pipe client expects; it adds drives itself).</summary>
    public IReadOnlyList<SensorInfo> HardwareSnapshot => _service.Snapshot;

    public IReadOnlyList<SensorInfo> Snapshot => _merged;
    public void Update() { }

    public event Action? Updated;

    /// <param name="log">Where failing devices and a failing update loop are reported (the service log), once a minute per line.</param>
    public LocalSensorSource(Action<string>? log = null) : this(new SensorService(log), DriveSensors.Read, log) { }

    /// <summary>Tests: a fake <paramref name="service"/> and drive list, so no real sensor is read.</summary>
    internal LocalSensorSource(SensorService service, Func<List<SensorInfo>> readDrives, Action<string>? log = null)
    {
        _service = service;
        _readDrives = readDrives;
        _log = log;
        try { _service.Update(); }
        catch (Exception ex) { Log("Sensors: the first update failed: " + ex.Message); }   // the loop tries again every second
        Merge();
        _loop = Task.Run(Loop);
    }

    private async Task Loop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { return; }
                bool fresh = false;
                try { _service.Update(); fresh = true; }
                catch (Exception ex) { Log("Sensors: update failed: " + ex.Message); }
                if (_cts.IsCancellationRequested) return;
                try { Merge(); } catch (Exception ex) { Log("Sensors: reading drive space failed: " + ex.Message); }
                if (fresh)
                {
                    try { Updated?.Invoke(); } catch (Exception ex) { Log("Sensors: publishing failed: " + ex.Message); }
                }
            }
        }
        finally { _service.Dispose(); }   // the loop owns LibreHardwareMonitor: it is closed only once no update can run
    }

    private void Merge()
    {
        if (Environment.TickCount64 - _drivesAt > 5000) { _drives = _readDrives(); _drivesAt = Environment.TickCount64; }
        _merged = _service.Snapshot.Concat(_drives).ToList();
    }

    private void Log(string message)
    {
        try { if (_limiter.Filter(message) is { } line) _log?.Invoke(line); } catch { /* never */ }
    }

    /// <summary>Cancels the loop and waits for it (bounded); the loop closes LibreHardwareMonitor on its way out.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        bool ended = false;
        try { ended = _loop.Wait(SensorService.DisposeWait); } catch { /* the loop never throws; best effort */ }
        // if the loop is still inside an update after that, mark it closed without waiting again: the update closes when it ends
        if (!ended) _service.Dispose(TimeSpan.Zero);
    }
}

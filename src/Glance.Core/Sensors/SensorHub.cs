using System.Diagnostics;

namespace Glance.Core.Sensors;

/// <summary>
/// The one sensor source of a user-side process (the editor and the tray app). It uses the Glance service's feed when it
/// arrives (full CPU and motherboard data, read by the service as SYSTEM) and otherwise reads the basic sensors itself,
/// without administrator rights. Drive-space sensors come from Windows in both cases. A feed whose snapshots stop changing
/// counts as gone (<see cref="PipeSensorClient.HasData"/>), so a stalled service falls back to the local sensors (review M8).
/// </summary>
public sealed class SensorHub : ISensorSource, IDisposable
{
    private readonly PipeSensorClient _pipe;
    private readonly CancellationTokenSource _cts = new();
    private volatile SensorService? _local;
    private volatile IReadOnlyList<SensorInfo> _merged = Array.Empty<SensorInfo>();
    private List<SensorInfo> _drives = new();
    private long _drivesAt;
    private readonly Func<SensorService> _openLocal;
    private readonly Func<List<SensorInfo>> _readDrives;
    private readonly TimeSpan _firstWait;
    private readonly Task _localLoop, _mergeLoop;
    private int _disposed;

    /// <param name="pipe">Tests pass their own client; the default reads <see cref="PipeSensorClient.PipeName"/> from the service.</param>
    public SensorHub(PipeSensorClient? pipe = null) : this(pipe, () => new SensorService(), DriveSensors.Read, TimeSpan.FromMilliseconds(1500)) { }

    /// <summary>Tests: fake local sensors and drives (no real sensor is read), and a shorter wait for the service's feed.</summary>
    internal SensorHub(PipeSensorClient? pipe, Func<SensorService> openLocal, Func<List<SensorInfo>> readDrives, TimeSpan firstWait)
    {
        _pipe = pipe ?? new PipeSensorClient();
        _openLocal = openLocal;
        _readDrives = readDrives;
        _firstWait = firstWait;
        _mergeLoop = Task.Run(MergeLoop);
        _localLoop = Task.Run(LocalLoop);
    }

    /// <summary>True while the Glance service's feed arrives (all sensors).</summary>
    public bool FromService => _pipe.HasData;

    /// <summary>Hardware sensors (from the service or local reading) plus drive-space sensors from Windows.</summary>
    public IReadOnlyList<SensorInfo> Snapshot => _merged;
    public void Update() { }

    public string Status => FromService
        ? $"Sensors: Glance service ({Snapshot.Count})"
        : _local is null ? "Sensors: starting..." : $"Sensors: limited, service not running ({Snapshot.Count})";

    private void Merge()
    {
        var baseList = _pipe.HasData ? _pipe.Snapshot : _local?.Snapshot ?? Array.Empty<SensorInfo>();
        if (Environment.TickCount64 - _drivesAt > 5000) { _drives = _readDrives(); _drivesAt = Environment.TickCount64; }
        _merged = baseList.Concat(_drives).ToList();
    }

    private async Task MergeLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { Merge(); } catch (Exception ex) { Debug.WriteLine("Sensor merge failed: " + ex.Message); }
            try { await Task.Delay(500, _cts.Token); } catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Opens, reads and closes the local sensors. It owns them: they are closed here, on the way out, so they are never closed
    /// while an update runs, and sensors opened while the hub is disposed are closed too. One failure only skips a second.
    /// </summary>
    private async Task LocalLoop()
    {
        try
        {
            try { await Task.Delay(_firstWait, _cts.Token); } catch (OperationCanceledException) { return; }   // give the service's feed a moment to arrive first
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (_pipe.HasData)
                    {
                        if (_local is { } l) { _local = null; l.Dispose(); }
                    }
                    else
                    {
                        _local ??= _openLocal();
                        if (_cts.IsCancellationRequested) return;
                        _local.Update();
                    }
                }
                catch (Exception ex) { Debug.WriteLine("Local sensors failed: " + ex.Message); }   // try again in a second
                try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { return; }
            }
        }
        finally
        {
            if (_local is { } l) { _local = null; l.Dispose(); }
        }
    }

    /// <summary>
    /// How long <see cref="Dispose"/> waits for the loops. Short: the editor and the tray app dispose the hub on their UI threads
    /// (review M7), and safety does not depend on it (the loop closes its own sensors); it only lets the close happen before exit.
    /// </summary>
    public static readonly TimeSpan DisposeWait = TimeSpan.FromMilliseconds(500);

    /// <summary>Cancels the loops and waits for them (briefly); the local loop closes its sensors on its way out.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _pipe.Dispose();
        bool ended = false;
        try { ended = Task.WaitAll(new[] { _localLoop, _mergeLoop }, DisposeWait); } catch { /* best effort */ }
        // still inside an update or an open: mark it closed without waiting; the update closes it when it ends,
        // and the loop's finally closes sensors that are still being opened
        if (!ended) _local?.Dispose(TimeSpan.Zero);
    }
}

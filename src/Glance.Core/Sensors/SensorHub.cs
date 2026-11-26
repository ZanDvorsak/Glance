using System.Diagnostics;

namespace Glance.Core.Sensors;

/// <summary>
/// The one sensor source of a user-side process (the editor and the tray app). It uses the Glance service's feed when it
/// arrives (full CPU and motherboard data, read by the service as SYSTEM) and otherwise reads the basic sensors itself,
/// without administrator rights. Drive-space sensors come from Windows in both cases.
/// </summary>
public sealed class SensorHub : ISensorSource, IDisposable
{
    private readonly PipeSensorClient _pipe;
    private readonly CancellationTokenSource _cts = new();
    private volatile SensorService? _local;
    private volatile IReadOnlyList<SensorInfo> _merged = Array.Empty<SensorInfo>();
    private List<SensorInfo> _drives = new();
    private long _drivesAt;

    /// <param name="pipe">Tests pass their own client; the default reads <see cref="PipeSensorClient.PipeName"/> from the service.</param>
    public SensorHub(PipeSensorClient? pipe = null)
    {
        _pipe = pipe ?? new PipeSensorClient();
        Task.Run(LocalLoop);
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
        if (Environment.TickCount64 - _drivesAt > 5000) { _drives = DriveSensors.Read(); _drivesAt = Environment.TickCount64; }
        _merged = baseList.Concat(_drives).ToList();
    }

    private async Task LocalLoop()
    {
        try
        {
            _ = Task.Run(async () => { while (!_cts.IsCancellationRequested) { Merge(); try { await Task.Delay(500, _cts.Token); } catch { return; } } });
            await Task.Delay(1500, _cts.Token); // give the service's feed a moment to arrive first
            while (!_cts.IsCancellationRequested)
            {
                if (_pipe.HasData)
                {
                    if (_local is { } l) { _local = null; l.Dispose(); }
                }
                else
                {
                    _local ??= new SensorService();
                    _local.Update();
                }
                await Task.Delay(1000, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine("Local sensors failed: " + ex.Message); }
    }

    public void Dispose() { _cts.Cancel(); _pipe.Dispose(); _local?.Dispose(); }
}

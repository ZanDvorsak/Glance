using System.IO.Pipes;
using System.Text.Json;

namespace MiniLcd.Core.Sensors;

/// <summary>Receives sensor snapshots from the elevated MiniLcd.SensorHost over a named pipe.</summary>
public sealed class PipeSensorClient : ISensorSource, IDisposable
{
    public const string PipeName = "MiniLcd.Sensors";

    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private IReadOnlyList<SensorInfo> _snapshot = Array.Empty<SensorInfo>();

    public PipeSensorClient() => Task.Run(Loop);

    public IReadOnlyList<SensorInfo> Snapshot { get { lock (_lock) return _snapshot; } }
    public bool HasData => Snapshot.Count > 0;
    public void Update() { }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.In);
                await pipe.ConnectAsync(1000, _cts.Token);
                using var reader = new StreamReader(pipe);
                while (!_cts.IsCancellationRequested && await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    var list = JsonSerializer.Deserialize<List<SensorInfo>>(line);
                    if (list is not null) lock (_lock) _snapshot = list;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* host not running or disconnected: retry */ }
            try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose() => _cts.Cancel();
}

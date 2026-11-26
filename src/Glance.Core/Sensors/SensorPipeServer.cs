using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Glance.Core.Service;

namespace Glance.Core.Sensors;

/// <summary>
/// Publishes sensor snapshots, read only, over a named pipe. Each client is served on its own task and dropped if it stops
/// reading, so one stuck client can never block the others or the accept loop.
/// The first instance is created with the first-instance flag, so a name another program took is refused and reported, and
/// the server keeps trying until it is free (review M1). Start it before the sensors are ready: it serves "[]" until then (L10).
/// </summary>
public sealed class SensorPipeServer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private volatile string _latest = "[]";
    private readonly string _pipeName;
    private readonly PipeSecurity? _security;
    private readonly Action<string>? _log;
    private readonly LogLimiter _limiter = new(TimeSpan.FromMinutes(1));
    private int _instances;   // our live instances; 0 means the name has to be claimed again

    /// <param name="pipeName">Defaults to the name the editor looks for. Tests pass their own so they never collide with a running service.</param>
    /// <param name="security">The pipe's rules; null allows only the current user (the Glance service passes <c>ServicePipeSecurity.Build</c>).</param>
    /// <param name="log">Where pipe problems are reported (the service log); repeated lines are written once a minute.</param>
    public SensorPipeServer(string? pipeName = null, PipeSecurity? security = null, Action<string>? log = null)
    {
        _pipeName = pipeName ?? PipeSensorClient.PipeName;
        _security = security;
        _log = log;
    }

    public void Publish(IReadOnlyList<SensorInfo> snapshot) => _latest = JsonSerializer.Serialize(snapshot);

    public void Start()
    {
        var security = _security ?? ServicePipeSecurity.CurrentUserOnly();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = ServicePipeFactory.Create(_pipeName, PipeDirection.Out, 16, security, first: Volatile.Read(ref _instances) == 0);
                    Interlocked.Increment(ref _instances);
                    await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                    _ = Serve(server);
                    server = null;
                }
                catch (OperationCanceledException) { Release(server); return; }
                catch (Exception ex)
                {
                    Release(server);
                    Log("Sensor pipe error: " + ex.Message);
                    try { await Task.Delay(2000, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                }
            }
        });
    }

    private void Release(NamedPipeServerStream? pipe)
    {
        if (pipe is null) return;
        try { pipe.Dispose(); } catch { /* best effort */ }
        // only instances that were counted get here: Create throws before the increment
        Interlocked.Decrement(ref _instances);
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        try
        {
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true);
            while (!_cts.IsCancellationRequested && pipe.IsConnected)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5)); // a client that does not read for 5 s is dropped
                await writer.WriteLineAsync(_latest.AsMemory(), timeout.Token).ConfigureAwait(false);
                await writer.FlushAsync(timeout.Token).ConfigureAwait(false);
                await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
            }
        }
        catch { /* client left or stalled */ }
        finally { Release(pipe); }
    }

    private void Log(string message)
    {
        try { if (_limiter.Filter(message) is { } line) _log?.Invoke(line); } catch { /* never */ }
    }

    public void Dispose() => _cts.Cancel();
}

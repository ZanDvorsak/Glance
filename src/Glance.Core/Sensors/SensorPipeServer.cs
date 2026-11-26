using System.Globalization;
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
/// <para>
/// Each client gets one line, <c>{"Seq":n,"AgeMs":ms,"Sensors":[...]}</c>, as soon as a snapshot is published and at least once a
/// second: the last snapshot, its number, and how long ago it was published. The line is re-sent while nothing new is published
/// (so a client that stops reading is found), but its age grows, so clients see a stalled sensor loop as stale even while the
/// pipe stays connected (review M8).
/// </para>
/// </summary>
public sealed class SensorPipeServer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private sealed record Published(long Seq, long AtMs, string Json);
    private volatile Published _latest = new(0, Environment.TickCount64, "[]");
    private TaskCompletionSource _changed = NewSignal();   // completed (and replaced) by each Publish, so clients get it at once
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
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

    /// <summary>
    /// Makes <paramref name="snapshot"/> the one clients get, as a new snapshot. Never throws: NaN and infinities are sent as
    /// "no value" (JSON cannot carry them, review M8); if it still cannot be written, the reason is logged and the old snapshot
    /// stays, ageing, so clients see the feed go stale.
    /// </summary>
    public void Publish(IReadOnlyList<SensorInfo> snapshot)
    {
        try
        {
            var clean = snapshot.Select(s => s with { Value = SensorInfo.Finite(s.Value) }).ToList();
            var json = JsonSerializer.Serialize(clean);
            var previous = _latest;
            _latest = new Published(previous.Seq + 1, Environment.TickCount64, json);   // one publisher (the service's sensor loop)
            Interlocked.Exchange(ref _changed, NewSignal()).TrySetResult();
        }
        catch (Exception ex) { Log("Sensor snapshot could not be published: " + ex.Message); }
    }

    /// <summary>The line a client gets now: the last snapshot with its number and age.</summary>
    internal string Line()
    {
        var p = _latest;
        long age = Math.Max(0, Environment.TickCount64 - p.AtMs);
        return string.Create(CultureInfo.InvariantCulture, $"{{\"Seq\":{p.Seq},\"AgeMs\":{age},\"Sensors\":{p.Json}}}");
    }

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
                    await server.WaitForConnectionAsync(_cts.Token);
                    _ = Serve(server);
                    server = null;
                }
                catch (OperationCanceledException) { Release(server); return; }
                catch (Exception ex)
                {
                    Release(server);
                    Log("Sensor pipe error: " + ex.Message);
                    try { await Task.Delay(2000, _cts.Token); } catch (OperationCanceledException) { return; }
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
                var changed = Volatile.Read(ref _changed).Task;   // before the line: a snapshot published after it is sent at once
                await writer.WriteLineAsync(Line().AsMemory(), timeout.Token);
                await writer.FlushAsync(timeout.Token);
                // a new snapshot goes out at once (so the age a client sees stays about one update interval); without one, the
                // same snapshot is re-sent after a second, older, which also finds a client that stopped reading
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    await Task.WhenAny(changed, Task.Delay(1000, wait.Token));
                    wait.Cancel();
                }
                _cts.Token.ThrowIfCancellationRequested();
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

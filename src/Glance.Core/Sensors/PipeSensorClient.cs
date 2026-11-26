using System.IO.Pipes;
using System.Text.Json;

namespace Glance.Core.Sensors;

/// <summary>Receives sensor snapshots from the Glance service over a named pipe, after checking that SYSTEM or Administrators serve it.</summary>
public sealed class PipeSensorClient : ISensorSource, IDisposable
{
    public const string PipeName = "Glance.Sensors";

    /// <summary>The runner sends a snapshot every second; this long without one means it has stopped.</summary>
    private readonly long _staleAfterMs;
    private readonly string _pipeName;

    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private IReadOnlyList<SensorInfo> _snapshot = Array.Empty<SensorInfo>();
    private long _lastUpdateMs;

    private readonly IReadOnlyList<System.Security.Principal.SecurityIdentifier> _trustedOwners;

    /// <param name="trustedOwners">Who may serve the pipe: default SYSTEM or Administrators (the Glance service). Tests pass the current user.</param>
    public PipeSensorClient(string? pipeName = null, TimeSpan? staleAfter = null, IEnumerable<System.Security.Principal.SecurityIdentifier>? trustedOwners = null)
    {
        _pipeName = pipeName ?? PipeName;
        _trustedOwners = (trustedOwners ?? Glance.Core.Service.PipeServerCheck.ServiceOwners).ToArray();
        _staleAfterMs = (long)(staleAfter ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
        Task.Run(Loop);
    }

    public IReadOnlyList<SensorInfo> Snapshot { get { lock (_lock) return _snapshot; } }

    /// <summary>True only while snapshots are arriving. After the sender stops, the last values are stale and must not be shown as live.</summary>
    public bool HasData
    {
        get { lock (_lock) return _snapshot.Count > 0 && Environment.TickCount64 - _lastUpdateMs < _staleAfterMs; }
    }

    public void Update() { }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.In);
                await pipe.ConnectAsync(1000, _cts.Token);
                Glance.Core.Service.PipeServerCheck.Verify(pipe, _trustedOwners);   // fake values from a squatter are never shown
                using var reader = new StreamReader(pipe);
                while (!_cts.IsCancellationRequested && await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    var list = JsonSerializer.Deserialize<List<SensorInfo>>(line);
                    if (list is not null) lock (_lock) { _snapshot = list; _lastUpdateMs = Environment.TickCount64; }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* sender not running or disconnected: retry */ }

            lock (_lock) _snapshot = Array.Empty<SensorInfo>();   // the connection is gone: drop the old values
            try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose() => _cts.Cancel();
}

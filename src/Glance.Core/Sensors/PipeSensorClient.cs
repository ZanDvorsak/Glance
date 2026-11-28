using System.IO.Pipes;
using System.Text.Json;

namespace Glance.Core.Sensors;

/// <summary>Receives sensor snapshots from the Glance service over a named pipe, after checking that SYSTEM or Administrators serve it.</summary>
public sealed class PipeSensorClient : ISensorSource, IDisposable
{
    public const string PipeName = "Glance.Sensors";

    /// <summary>
    /// The service publishes a new snapshot every second: a snapshot older than this means its sensor loop has stopped (or the
    /// pipe has), even while the pipe stays connected and keeps re-sending it (review M8).
    /// </summary>
    private readonly long _staleAfterMs;
    private readonly string _pipeName;

    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private IReadOnlyList<SensorInfo> _snapshot = Array.Empty<SensorInfo>();
    private long _lastUpdateMs;   // when the service read the current snapshot, on this PC's tick clock
    private long _sequence;

    private readonly IReadOnlyList<System.Security.Principal.SecurityIdentifier> _trustedOwners;

    /// <param name="trustedOwners">Who may serve the pipe: default SYSTEM or Administrators (the Glance service). Tests pass the owner of their own test pipes (TestPipes.Own).</param>
    public PipeSensorClient(string? pipeName = null, TimeSpan? staleAfter = null, IEnumerable<System.Security.Principal.SecurityIdentifier>? trustedOwners = null)
    {
        _pipeName = pipeName ?? PipeName;
        _trustedOwners = (trustedOwners ?? Glance.Core.Service.PipeServerCheck.ServiceOwners).ToArray();
        _staleAfterMs = (long)(staleAfter ?? TimeSpan.FromSeconds(5)).TotalMilliseconds;
        Task.Run(Loop);
    }

    public IReadOnlyList<SensorInfo> Snapshot { get { lock (_lock) return _snapshot; } }

    /// <summary>
    /// True only while new snapshots are arriving. After the sender stops, or stops publishing new snapshots, the last values
    /// are stale and must not be shown as live.
    /// </summary>
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
                using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.In, PipeOptions.None, System.Security.Principal.TokenImpersonationLevel.Anonymous);   // the server can never act as this user
                await pipe.ConnectAsync(1000, _cts.Token).ConfigureAwait(false);
                Glance.Core.Service.PipeServerCheck.Verify(pipe, _trustedOwners);   // fake values from a squatter are never shown
                using var reader = new StreamReader(pipe);
                while (!_cts.IsCancellationRequested && await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false) is { } line)
                {
                    if (Parse(line) is not { } feed) continue;
                    lock (_lock)
                    {
                        _snapshot = feed.Sensors;
                        _sequence = feed.Seq;
                        _lastUpdateMs = Environment.TickCount64 - feed.AgeMs;   // when the service read it, not when it was re-sent
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* sender not running or disconnected: retry */ }

            lock (_lock) _snapshot = Array.Empty<SensorInfo>();   // the connection is gone: drop the old values
            try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    private sealed record FeedLine(long Seq, long AgeMs, List<SensorInfo>? Sensors);

    /// <summary>
    /// One line from <see cref="SensorPipeServer"/>: <c>{"Seq":n,"AgeMs":ms,"Sensors":[...]}</c>. A bare array (the format before
    /// review M8) counts as just read. Null for a line that is not either.
    /// </summary>
    internal static (long Seq, long AgeMs, IReadOnlyList<SensorInfo> Sensors)? Parse(string line)
    {
        if (line.TrimStart().StartsWith('['))
            return JsonSerializer.Deserialize<List<SensorInfo>>(line) is { } legacy ? (0, 0, legacy) : null;
        if (JsonSerializer.Deserialize<FeedLine>(line) is not { Sensors: { } sensors } feed) return null;
        return (feed.Seq, Math.Clamp(feed.AgeMs, 0, TimeSpan.FromDays(1).Ticks / TimeSpan.TicksPerMillisecond), sensors);
    }

    /// <summary>The number of the last snapshot received (it grows by one with each new snapshot the service publishes).</summary>
    public long Sequence { get { lock (_lock) return _sequence; } }

    public void Dispose() => _cts.Cancel();
}

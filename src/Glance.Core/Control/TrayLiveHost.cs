namespace Glance.Core.Control;

/// <summary>
/// The tray app's wiring around <see cref="TrayLiveState"/>, in Core so tests drive the same code as the tray (review nit):
/// every control line goes through <see cref="ExecuteLine"/> (live commands here, the rest to the tray), connection ends
/// through <see cref="ConnectionClosed"/>, and an own timer ends a live theme whose lease ran out, so this does not depend on
/// the tray's supervisor tick. <c>changed</c> runs under the tray's lock whenever the live theme or the pin may have changed.
/// </summary>
public sealed class TrayLiveHost : IDisposable
{
    private readonly object _gate;
    private readonly Action _changed;
    private readonly Action<string> _log;
    private readonly Timer _timer;

    /// <param name="gate">The tray's own lock; <see cref="State"/> is read and changed only under it.</param>
    /// <param name="changed">Called under <paramref name="gate"/> after anything may have changed (the tray hands the pin to its display loop).</param>
    /// <param name="tick">How often the lease is checked (1 s by default).</param>
    public TrayLiveHost(object gate, Action changed, Action<string> log, TimeSpan? lease = null, Func<TimeSpan>? clock = null, TimeSpan? tick = null)
    {
        _gate = gate;
        _changed = changed;
        _log = log;
        State = new TrayLiveState(lease, clock);
        var period = tick ?? TimeSpan.FromSeconds(1);
        _timer = new Timer(_ => Expire(), null, period, period);
    }

    /// <summary>Read under the tray's lock.</summary>
    public TrayLiveState State { get; }

    /// <summary>
    /// Runs one control line. <c>LIVE</c>, <c>LIVE_KEEP</c>, <c>LIVE_CLEAR</c> and <c>PAGE</c> are handled here; any other
    /// command goes to <paramref name="other"/>, after it renewed the lease of its connection (so <c>STATUS</c> keeps it alive too).
    /// </summary>
    public string ExecuteLine(string line, long connection, Func<ControlCommand, string> other)
    {
        // parsing and the wire format live in ControlCommand, so the editor and tests share them
        if (!ControlCommand.TryParse(line, out var cmd, out var error)) return "ERR " + error;
        string? reply;
        lock (_gate)
        {
            reply = State.Handle(cmd, connection);
            if (reply is not null) _changed();
        }
        return reply ?? other(cmd);
    }

    /// <summary>A control connection ended (the editor exited, crashed or was killed).</summary>
    public void ConnectionClosed(long connection)
    {
        bool ended;
        lock (_gate)
        {
            ended = State.ConnectionClosed(connection);
            if (ended) _changed();
        }
        if (ended) _log("The editor's live preview or page pin ended (its connection closed); showing the saved theme");
    }

    /// <summary>Ends a live theme nobody kept alive. Runs on the host's timer; callable directly.</summary>
    public void Expire()
    {
        bool ended;
        try
        {
            lock (_gate)
            {
                if (Interlocked.Exchange(ref _renewPending, 0) == 1) State.Renew();   // a wake noted by Resumed
                ended = State.Expire();
                if (ended) _changed();
            }
        }
        catch (Exception ex) { _log("Live preview check failed: " + ex.Message); return; }
        if (ended) _log("The editor's live preview ended (no word from the editor); showing the saved theme");
    }

    private int _renewPending;

    /// <summary>
    /// Windows woke up: restart the lease, so the saved theme does not flash before the editor's next heartbeat. Never waits:
    /// it runs on the power watcher's thread while the tray's lock may be held for seconds; the next <see cref="Expire"/>
    /// renews the lease first, under the lock, before it checks it.
    /// </summary>
    public void Resumed() => Interlocked.Exchange(ref _renewPending, 1);

    public void Dispose() => _timer.Dispose();
}

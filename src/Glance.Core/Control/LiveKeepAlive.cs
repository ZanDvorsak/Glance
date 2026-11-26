namespace Glance.Core.Control;

/// <summary>
/// When the editor sends <c>LIVE_KEEP</c> while it shows its edits through the tray app (review M5). The tray leases the live
/// theme for <see cref="TrayLiveState.DefaultLease"/> (5 s); a heartbeat every <see cref="Interval"/> keeps it well inside that,
/// and the answer tells the editor when the tray no longer holds its theme (the tray restarted, or the connection was replaced),
/// so the editor sends it again at once instead of waiting for the next edit.
/// Not thread safe: call it from one thread (the editor's UI thread).
/// </summary>
public sealed class LiveKeepAlive
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.5);

    private bool _inFlight;
    private DateTime _last = DateTime.MinValue;

    /// <summary>Forgets everything; call when live sending starts.</summary>
    public void Reset() { _inFlight = false; _last = DateTime.MinValue; }

    /// <summary>True when a heartbeat should go out now (and records it as on its way).</summary>
    public bool TryBegin(DateTime now)
    {
        if (_inFlight || now - _last < Interval) return false;
        _inFlight = true;
        _last = now;
        return true;
    }

    /// <summary>Records the tray's reply. Returns true when the tray no longer holds the editor's theme: send it again.</summary>
    public bool Complete(string? reply)
    {
        _inFlight = false;
        // only this exact reply: an older tray answers "ERR unknown command", and resending would not help it
        return reply == TrayLiveState.NotHeldReply;
    }
}

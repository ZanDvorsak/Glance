using Glance.Core.Themes;

namespace Glance.Core.Control;

/// <summary>
/// The tray app's share of the editor's live preview: the unsaved theme (<c>LIVE</c>) and the pinned page (<c>PAGE</c>).
/// <para>
/// Review M5: the live theme is <b>leased</b> to the control connection that sent it. It ends, and the page pin with it, on
/// <c>LIVE_CLEAR</c> from that connection, when that connection closes (the editor exited, crashed or was killed: Windows
/// closes its pipe), or after <see cref="DefaultLease"/> without any command on that connection (a hung editor). The tray then
/// shows the saved theme again. The editor keeps the lease alive with <c>LIVE_KEEP</c> (any command on the connection counts).
/// </para>
/// <para>
/// After review of that fix: a <c>LIVE</c> from another connection (a second editor) takes the theme over, and the first editor
/// learns it from <see cref="TakenReply"/> instead of sending its theme back (M1); its <c>LIVE_CLEAR</c> leaves the new owner
/// alone. The page pin belongs to the connection that set it and ends when that one closes (M2). Time comes from a monotonic
/// clock, and <see cref="Renew"/> restarts the lease after a wake from sleep (L1).
/// </para>
/// Not thread safe: callers hold their own lock (<see cref="TrayLiveHost"/> does).
/// </summary>
public sealed class TrayLiveState
{
    /// <summary>How long a live theme lasts without any command from the connection that sent it.</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(5);

    /// <summary>The reply to <c>LIVE_KEEP</c> when no live theme is shown: send it again.</summary>
    public const string NotHeldReply = "ERR no live theme";

    /// <summary>The reply to <c>LIVE_KEEP</c>/<c>LIVE_CLEAR</c> when another connection (another editor) shows its theme: stop, do not fight.</summary>
    public const string TakenReply = "ERR live taken";

    private readonly TimeSpan _lease;
    private readonly Func<TimeSpan> _now;
    private long? _owner;          // the connection whose live theme is shown
    private long? _pinOwner;       // the connection that pinned the page
    private TimeSpan _lastHeard;   // the owner's last command

    /// <param name="clock">Monotonic time (only differences count); <see cref="MonotonicClock.Now"/> by default.</param>
    public TrayLiveState(TimeSpan? lease = null, Func<TimeSpan>? clock = null)
    {
        _lease = lease ?? DefaultLease;
        _now = clock ?? (() => MonotonicClock.Now);
    }

    /// <summary>The editor's unsaved theme, or null to show the saved one.</summary>
    public Theme? Live { get; private set; }

    /// <summary>The pinned page (0-based), or null to rotate.</summary>
    public int? PinnedPage { get; private set; }

    /// <summary>
    /// Handles <c>LIVE</c>, <c>LIVE_KEEP</c>, <c>LIVE_CLEAR</c> and <c>PAGE</c>; returns the reply, or null for any other
    /// command. Every command (also the others) from the connection that holds the live theme renews its lease.
    /// </summary>
    public string? Handle(ControlCommand command, long connection)
    {
        if (Live is not null && _owner == connection) _lastHeard = _now();
        switch (command.Verb)
        {
            case ControlVerb.Live:
            {
                var t = new Theme { BaseFolder = command.Folder };
                t.ApplySnapshot(command.ThemeJson!);   // a bad theme throws before anything changes
                Live = t;
                _owner = connection;
                _lastHeard = _now();
                return "OK";
            }
            case ControlVerb.LiveKeep:
                return Live is null ? NotHeldReply : _owner == connection ? "OK" : TakenReply;
            case ControlVerb.LiveClear:
                if (Live is not null && _owner != connection) return TakenReply;   // another editor's theme: not ours to clear
                End();
                return "OK";
            case ControlVerb.Page:
                PinnedPage = command.Number > 0 ? command.Number - 1 : null;
                _pinOwner = PinnedPage is null ? null : connection;
                return "OK";
            default:
                return null;
        }
    }

    /// <summary>A control connection ended. Returns true when it held the live theme or the page pin, which have now ended.</summary>
    public bool ConnectionClosed(long connection)
    {
        if (Live is not null && _owner == connection) { End(); return true; }
        if (PinnedPage is not null && _pinOwner == connection) { PinnedPage = null; _pinOwner = null; return true; }
        return false;
    }

    /// <summary>Supervisor tick. Returns true when the live theme's lease ran out, so it has now ended.</summary>
    public bool Expire()
    {
        if (Live is null || _now() - _lastHeard < _lease) return false;
        End();
        return true;
    }

    /// <summary>Windows woke up: the editor slept too, so give it a whole lease to speak again (no flash of the saved theme).</summary>
    public void Renew()
    {
        if (Live is not null) _lastHeard = _now();
    }

    /// <summary>Back to the saved theme, and the editor's page pin goes with it.</summary>
    private void End()
    {
        Live = null;
        PinnedPage = null;
        _owner = null;
        _pinOwner = null;
    }
}

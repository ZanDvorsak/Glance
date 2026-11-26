using Glance.Core.Themes;

namespace Glance.Core.Control;

/// <summary>
/// The tray app's share of the editor's live preview: the unsaved theme (<c>LIVE</c>) and the pinned page (<c>PAGE</c>).
/// <para>
/// Review M5: the live theme is <b>leased</b> to the control connection that sent it. It ends, and the page pin with it, on
/// <c>LIVE_CLEAR</c>, when that connection closes (the editor exited, crashed or was killed: Windows closes its pipe), or after
/// <see cref="DefaultLease"/> without any command on that connection (a hung editor). The tray then shows the saved theme again,
/// so a lost <c>LIVE_CLEAR</c> can no longer leave an unsaved theme on the display until the tray restarts.
/// The editor keeps the lease alive with <c>LIVE_KEEP</c> (any other command on the same connection counts too).
/// </para>
/// Not thread safe: the tray calls it under its own lock.
/// </summary>
public sealed class TrayLiveState
{
    /// <summary>How long a live theme lasts without any command from the connection that sent it.</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(5);

    /// <summary>The reply to <c>LIVE_KEEP</c> when the asking connection holds no live theme.</summary>
    public const string NotHeldReply = "ERR no live theme";

    private readonly TimeSpan _lease;
    private readonly Func<DateTime> _now;
    private long? _owner;          // the connection whose live theme is shown
    private DateTime _lastHeard;   // the owner's last command

    public TrayLiveState(TimeSpan? lease = null, Func<DateTime>? clock = null)
    {
        _lease = lease ?? DefaultLease;
        _now = clock ?? (() => DateTime.UtcNow);
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
                return Live is not null && _owner == connection ? "OK" : NotHeldReply;
            case ControlVerb.LiveClear:
                End();
                return "OK";
            case ControlVerb.Page:
                PinnedPage = command.Number > 0 ? command.Number - 1 : null;
                return "OK";
            default:
                return null;
        }
    }

    /// <summary>A control connection ended. Returns true when it held the live theme, which has now ended.</summary>
    public bool ConnectionClosed(long connection)
    {
        if (Live is null || _owner != connection) return false;
        End();
        return true;
    }

    /// <summary>Supervisor tick. Returns true when the live theme's lease ran out, so it has now ended.</summary>
    public bool Expire()
    {
        if (Live is null || _now() - _lastHeard < _lease) return false;
        End();
        return true;
    }

    /// <summary>Like <c>LIVE_CLEAR</c>: back to the saved theme, and the editor's page pin goes with it.</summary>
    private void End()
    {
        Live = null;
        PinnedPage = null;
        _owner = null;
    }
}

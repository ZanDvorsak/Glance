using Glance.Core.Themes;

namespace Glance.Core.Control;

/// <summary>
/// The tray app's share of the editor's live preview: the unsaved theme (<c>LIVE</c>) and the pinned page (<c>PAGE</c>).
/// Not thread safe: the tray calls it under its own lock.
/// </summary>
public sealed class TrayLiveState
{
    /// <summary>How long a live theme lasts without any command from the connection that sent it.</summary>
    public static readonly TimeSpan DefaultLease = TimeSpan.FromSeconds(5);

    /// <summary>The reply to <c>LIVE_KEEP</c> when the asking connection holds no live theme.</summary>
    public const string NotHeldReply = "ERR no live theme";

    public TrayLiveState(TimeSpan? lease = null, Func<DateTime>? clock = null) { }

    /// <summary>The editor's unsaved theme, or null to show the saved one.</summary>
    public Theme? Live { get; private set; }

    /// <summary>The pinned page (0-based), or null to rotate.</summary>
    public int? PinnedPage { get; private set; }

    /// <summary>Handles <c>LIVE</c>, <c>LIVE_KEEP</c>, <c>LIVE_CLEAR</c> and <c>PAGE</c>; returns the reply, or null for any other command.</summary>
    public string? Handle(ControlCommand command, long connection)
    {
        switch (command.Verb)
        {
            case ControlVerb.Live:
                var t = new Theme { BaseFolder = command.Folder };
                t.ApplySnapshot(command.ThemeJson!);
                Live = t;
                return "OK";
            case ControlVerb.LiveClear:
                Live = null; PinnedPage = null;
                return "OK";
            case ControlVerb.Page:
                PinnedPage = command.Number > 0 ? command.Number - 1 : null;
                return "OK";
            case ControlVerb.LiveKeep:
                return "ERR unknown command";
            default:
                return null;
        }
    }

    /// <summary>A control connection ended. Returns true when that changed what the tray shows.</summary>
    public bool ConnectionClosed(long connection) => false;

    /// <summary>Supervisor tick. Returns true when that changed what the tray shows.</summary>
    public bool Expire() => false;
}

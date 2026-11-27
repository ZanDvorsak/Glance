namespace Glance.Core.Control;

/// <summary>What the tray's answer to <c>LIVE_KEEP</c> means for the editor.</summary>
public enum KeepResult
{
    /// <summary>The tray still shows the editor's theme.</summary>
    Kept,
    /// <summary>No answer in time: the link reconnects and the next heartbeat asks again.</summary>
    NoAnswer,
    /// <summary>The tray no longer shows it (restarted, or the lease ran out): send pin, brightness and theme again.</summary>
    Resend,
    /// <summary>Another editor took the display over: stop live sending here, do not take it back (review M1).</summary>
    Taken,
    /// <summary>Anything else (an older tray answers "ERR unknown command"): resending would not help.</summary>
    Other,
}

/// <summary>
/// When the editor sends <c>LIVE_KEEP</c> while it shows its edits through the tray app (review M5). The tray leases the live
/// theme for <see cref="TrayLiveState.DefaultLease"/> (5 s); a heartbeat every <see cref="Interval"/> keeps it well inside that.
/// Time is monotonic (<see cref="MonotonicClock"/>, review L1). Not thread safe: call it from one thread (the editor's UI thread).
/// </summary>
public sealed class LiveKeepAlive
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.5);

    private bool _inFlight;
    private TimeSpan? _last;

    /// <summary>Forgets everything; call when live sending starts.</summary>
    public void Reset() { _inFlight = false; _last = null; }

    /// <summary>True when a heartbeat should go out now (and records it as on its way).</summary>
    /// <param name="now">Monotonic time, <see cref="MonotonicClock.Now"/>.</param>
    public bool TryBegin(TimeSpan now)
    {
        if (_inFlight || (_last is { } last && now - last < Interval)) return false;
        _inFlight = true;
        _last = now;
        return true;
    }

    /// <summary>Records the tray's reply and says what to do about it.</summary>
    public KeepResult Complete(string? reply)
    {
        _inFlight = false;
        return reply switch
        {
            null => KeepResult.NoAnswer,
            TrayLiveState.NotHeldReply => KeepResult.Resend,
            TrayLiveState.TakenReply => KeepResult.Taken,
            _ when reply.StartsWith("OK", StringComparison.Ordinal) => KeepResult.Kept,
            _ => KeepResult.Other,
        };
    }

    /// <summary>
    /// What goes to the tray before the live theme, at the start and again after <see cref="KeepResult.Resend"/> (review M2: the
    /// tray drops the pin with the theme): the brightness and the page pin (<c>PAGE 0</c> releases it).
    /// </summary>
    /// <param name="pinnedPage">0-based page to pin, or null to rotate.</param>
    public static IReadOnlyList<ControlCommand> RestoreCommands(int brightness, int? pinnedPage) =>
        new[] { ControlCommand.Brightness(brightness), ControlCommand.Page(pinnedPage is { } p ? p + 1 : 0) };
}

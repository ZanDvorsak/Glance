namespace Glance.Core.Display;

/// <summary>
/// Holds the editor's <see cref="DisplayPusher"/> (the runner keeps its own under its lock). One pusher at a time:
/// <list type="bullet">
/// <item>it is opened only while the <see cref="DisplayPowerPolicy"/> allows it, checked before and right after opening;</item>
/// <item>it is released by exactly one caller (a Windows power event, the UI or a dead-loop restart can race for it);</item>
/// <item>a pusher whose stop timed out still holds the USB device, so nothing is opened until it lets go.</item>
/// </list>
/// Thread-safe: power events arrive on a watcher thread, everything else on the UI thread.
/// </summary>
public sealed class PusherSlot
{
    private readonly object _gate = new();
    private readonly Func<DisplayPusher> _open;
    private DisplayPusher? _current;
    private DisplayPusher? _closing;   // stopped, but a stuck transfer still holds the device

    public PusherSlot(DisplayPowerPolicy power, Func<DisplayPusher> open) { Power = power; _open = open; }

    public DisplayPowerPolicy Power { get; }

    /// <summary>The running pusher, or null.</summary>
    public DisplayPusher? Current => Volatile.Read(ref _current);

    /// <summary>Why the display cannot be opened because of Windows or the app closing, or null.</summary>
    public string? PowerReason => Power.MayDrive ? null : Power.Ended ? "The editor is closing or Windows is ending the session." : "Windows is going to sleep.";

    /// <summary>Null when the device is free; otherwise why not (a stopped pusher is still finishing its last transfer).</summary>
    public string? StillClosing()
    {
        var c = Volatile.Read(ref _closing);
        if (c is { IsClosed: false }) return "The display is still finishing its last transfer; try again in a few seconds.";
        Interlocked.CompareExchange(ref _closing, null, c);
        return null;
    }

    /// <summary>Opens the display unless Windows is asleep or ending the session, or the device is still held by a stopped pusher.</summary>
    public bool TryOpen(out string? error)
    {
        lock (_gate)
        {
            if (Current is not null) { error = null; return true; }
            if (PowerReason is { } notNow) { error = notNow; return false; }
            if (StillClosing() is { } busy) { error = busy; return false; }
            DisplayPusher p;
            try { p = _open(); }
            catch (Exception ex) { error = ex.Message; return false; }
            Volatile.Write(ref _current, p);
            // a power event does not wait behind this lock for long; if one came while the display opened, close it again
            if (PowerReason is { } late) { Release(blank: true, DisplayPusher.QuickStopTimeout); error = late; return false; }
            error = null;
            return true;
        }
    }

    /// <summary>Stops and detaches the pusher. Any thread; only one caller gets it. A stop that times out is remembered (see <see cref="StillClosing"/>).</summary>
    /// <returns>True if there was a pusher to stop.</returns>
    public bool Release(bool blank, TimeSpan timeout)
    {
        var p = Interlocked.Exchange(ref _current, null);
        if (p is null) return false;
        if (!p.Stop(blank, timeout)) Volatile.Write(ref _closing, p);
        return true;
    }

    public enum RestartResult { NotNeeded, Reopened, NotNow, Failed }

    /// <summary>
    /// If the loop ended on its own (it should not, but must never freeze the display silently), replace it, through the same
    /// checks as <see cref="TryOpen"/>: never while asleep or after the session ended, and never when someone else released it first.
    /// </summary>
    public RestartResult RestartIfDead(out string? why, out string? error)
    {
        why = null; error = null;
        var dead = Current;
        if (dead is null || dead.IsAlive) return RestartResult.NotNeeded;
        // only the caller that detaches it may stop it; a power event (or the user) may have released it a moment ago
        if (Interlocked.CompareExchange(ref _current, null, dead) != dead) return RestartResult.NotNeeded;
        why = dead.LastError ?? "unknown error";
        // the loop has ended, so this returns at once; asleep or ending, the screen should be black, otherwise it is reopened anyway
        if (!dead.Stop(blank: !Power.MayDrive, DisplayPusher.QuickStopTimeout)) Volatile.Write(ref _closing, dead);
        if (TryOpen(out error)) return RestartResult.Reopened;
        return Power.MayDrive ? RestartResult.Failed : RestartResult.NotNow;
    }

    /// <summary>
    /// For a Windows power event: blank and release the display within <see cref="PowerBlank.OwnerBudgetFor"/>.
    /// Call after updating <see cref="Power"/>, so nothing reopens it meanwhile.
    /// </summary>
    public PowerBlank.Result BlankForPowerEvent(PowerEvent e, out bool hadOne)
    {
        bool had = false;
        var result = PowerBlank.Run(_gate, () =>
        {
            var p = Interlocked.Exchange(ref _current, null);
            had = p is not null;
            return p;
        }, PowerBlank.OwnerBudgetFor(e), stuck => Volatile.Write(ref _closing, stuck));
        hadOne = had;
        return result;
    }
}

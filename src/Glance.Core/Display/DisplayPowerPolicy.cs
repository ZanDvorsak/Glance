namespace Glance.Core.Display;

/// <summary>Something Windows (or the user) did that affects whether the display may stay on.</summary>
public enum PowerEvent
{
    /// <summary>Windows is shutting down, restarting or signing out (WM_ENDSESSION with TRUE). The process can be killed right after.</summary>
    SessionEnding,
    /// <summary>Windows is going to sleep or hibernate.</summary>
    Suspend,
    /// <summary>Windows woke up from sleep or hibernate.</summary>
    Resume,
    /// <summary>The program itself is exiting (tray Exit, the EXIT command, the editor closing).</summary>
    Quit,
}

/// <summary>What the owner of the display should do in response to a <see cref="PowerEvent"/>.</summary>
public enum PowerAction
{
    /// <summary>Nothing to do (already handled, or nothing changed).</summary>
    None,
    /// <summary>Blank the display (black frame, backlight 0), release it and never open it again in this process.</summary>
    BlankAndStop,
    /// <summary>Blank the display and release it, and do not reopen it until <see cref="PowerAction.Reopen"/>.</summary>
    BlankAndHold,
    /// <summary>The display may be opened again; continue showing the theme.</summary>
    Reopen,
}

/// <summary>
/// Decides what to do with the display on shutdown, sign-out, sleep, wake and exit. Pure state, no Windows calls, so it can be tested.
/// It guarantees the final blank happens once (a session end and a normal quit can race) and that nothing reopens the display
/// after the session ended or while Windows sleeps: owners check <see cref="MayDrive"/> before opening it.
/// Thread-safe: Windows events arrive on their own thread.
/// </summary>
public sealed class DisplayPowerPolicy
{
    private readonly object _lock = new();
    private bool _ended, _suspended;
    private int _awakeTicks;

    /// <summary>True once the session ended or the program quit. The display must not be opened again.</summary>
    public bool Ended { get { lock (_lock) return _ended; } }

    /// <summary>True between a suspend and the following resume.</summary>
    public bool Suspended { get { lock (_lock) return _suspended; } }

    /// <summary>Whether the display may be opened (and drawn to) right now.</summary>
    public bool MayDrive { get { lock (_lock) return !_ended && !_suspended; } }

    /// <summary>
    /// Called by the owner's periodic timer (the runner's 2 s supervisor). Timers do not run while the PC sleeps, so ticks only
    /// count time spent awake. If Windows never sends the wake-up notice (a vetoed or failed suspend, a missed broadcast),
    /// this clears the "asleep" state after <paramref name="ticksToHeal"/> awake ticks so the display does not stay dark for good.
    /// </summary>
    /// <returns><see cref="PowerAction.Reopen"/> when it healed a stuck sleep state, otherwise <see cref="PowerAction.None"/>.</returns>
    public PowerAction NoteAwakeTick(int ticksToHeal = 15)
    {
        lock (_lock)
        {
            if (_ended || !_suspended) { _awakeTicks = 0; return PowerAction.None; }
            if (++_awakeTicks < ticksToHeal) return PowerAction.None;
            _awakeTicks = 0;
            _suspended = false;
            return PowerAction.Reopen;
        }
    }

    /// <remarks>
    /// The first session end or quit always returns <see cref="PowerAction.BlankAndStop"/>, even while asleep (then there is
    /// nothing open to blank, but the owner must still stop, for example when an installer closes the app).
    /// Owners also treat a user's "resume" or "reload" as <see cref="PowerEvent.Resume"/>, which clears a stale sleep state.
    /// </remarks>
    public PowerAction Handle(PowerEvent e)
    {
        lock (_lock)
        {
            switch (e)
            {
                case PowerEvent.SessionEnding:
                case PowerEvent.Quit:
                    if (_ended) return PowerAction.None;                       // the final blank already happened
                    _ended = true;
                    return PowerAction.BlankAndStop;

                case PowerEvent.Suspend:
                    if (_ended || _suspended) return PowerAction.None;
                    _suspended = true;
                    _awakeTicks = 0;
                    return PowerAction.BlankAndHold;

                case PowerEvent.Resume:
                    // Windows sends two resume notifications (automatic, then "user present"); only the first one counts.
                    if (_ended || !_suspended) return PowerAction.None;
                    _suspended = false;
                    return PowerAction.Reopen;

                default:
                    return PowerAction.None;
            }
        }
    }
}

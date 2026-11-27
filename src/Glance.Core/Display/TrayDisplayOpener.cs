using Glance.Core.Service;

namespace Glance.Core.Display;

/// <summary>What <see cref="TrayDisplayOpener"/> needs from the tray app. Members marked "under Gate" are called holding <see cref="Gate"/>.</summary>
public interface ITrayDisplayOwner
{
    /// <summary>The owner's lock over its pusher.</summary>
    object Gate { get; }
    DisplayPowerPolicy Power { get; }
    bool Paused { get; }
    /// <summary>Under Gate: a pusher is already running.</summary>
    bool HasPusher { get; }
    /// <summary>Under Gate: take the new pusher (apply the current page pin and brightness).</summary>
    void Adopt(DisplayPusher pusher);
    /// <summary>Under Gate: a stop timed out; the pusher still holds the device until its transfer gives up.</summary>
    void RememberClosing(DisplayPusher pusher);
    /// <summary>Under Gate: the open failed (shown in the tray menu).</summary>
    void OpenFailed(string message);
    void Log(string line);
    /// <summary>Not under Gate: something changed; the tray refreshes its status (and may start the next open).</summary>
    void Changed();
}

/// <summary>
/// Opens the tray app's display off its UI thread, one attempt at a time, and decides what to do with the result: keep it, or
/// stop it because Windows started a sleep or shutdown, the user paused, another pusher runs, or the app is quitting.
/// Glance 0.2.0 opened the display on the tray's UI thread and deadlocked there (docs/design/fix-tray-deadlock.md).
/// <list type="bullet">
/// <item>An unwanted pusher is stopped outside the owner's lock: a stalled transfer can take seconds, and the tray's
/// Supervise tick and the editor's commands wait on that lock (review L1).</item>
/// <item><see cref="Close"/> (quit) waits, bounded, for an open in flight; what it opened is then blanked and closed, and the
/// owner is not touched any more (review L2).</item>
/// <item>A failed open asks for the next attempt after <see cref="RetryDelay"/>, not on the owner's next 2 s tick.</item>
/// </list>
/// </summary>
public sealed class TrayDisplayOpener
{
    /// <summary>How soon a failed open is tried again (a starting service answers within a second or two).</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private readonly ITrayDisplayOwner _owner;
    private readonly Func<int, int?, DisplayPusher> _open;
    private readonly LogLimiter _failLog = new(TimeSpan.FromMinutes(1));   // a missing display is retried often
    private readonly ManualResetEventSlim _idle = new(true);               // set when no open (or its stop) runs
    private bool _opening, _closed;   // under the owner's Gate
    private int _active;              // under the owner's Gate: attempts not finished yet, including their stop
    private long _retryAt;            // under the owner's Gate: Environment.TickCount64 before which no new attempt starts

    /// <param name="open">Opens the display with this brightness and page pin. Runs on the thread pool, never on the caller's thread.</param>
    public TrayDisplayOpener(ITrayDisplayOwner owner, Func<int, int?, DisplayPusher> open) { _owner = owner; _open = open; }

    /// <summary>True from <see cref="Start"/> until the result has been handed to the owner.</summary>
    public bool IsOpening { get { lock (_owner.Gate) return _opening; } }

    /// <summary>Starts an open on the thread pool and returns at once. False when one runs, a failed one is waiting out
    /// <see cref="RetryDelay"/>, or the app is quitting.</summary>
    public bool Start(int brightness, int? pinnedPage)
    {
        lock (_owner.Gate)
        {
            if (_opening || _closed || Environment.TickCount64 < _retryAt) return false;
            _opening = true;
            if (_active++ == 0) _idle.Reset();
        }
        _ = Task.Run(() =>
        {
            DisplayPusher? pusher = null;
            Exception? error = null;
            try { pusher = _open(brightness, pinnedPage); }
            catch (Exception ex) { error = ex; }
            Opened(pusher, error);
        });
        return true;
    }

    /// <summary>
    /// The app is quitting: no new opens, and waits at most <paramref name="timeout"/> for one in flight, which then blanks and
    /// closes what it opened. Call before disposing what the pusher uses (the sensors). Not on a thread that holds the owner's Gate.
    /// </summary>
    /// <returns>True when nothing is left running.</returns>
    public bool Close(TimeSpan timeout)
    {
        lock (_owner.Gate) _closed = true;
        return _idle.Wait(timeout);
    }

    private void Opened(DisplayPusher? opened, Exception? error)
    {
        DisplayPusher? stop = null;
        bool blank = false, closed;
        TimeSpan stopTimeout = DisplayPusher.StopTimeout;
        string? retryLog = null;
        lock (_owner.Gate)
        {
            _opening = false;
            closed = _closed;
            if (opened is null)
            {
                if (!closed)
                {
                    var message = error?.Message ?? "unknown error";
                    _owner.OpenFailed(message);
                    retryLog = _failLog.Filter("Could not open the display: " + message);
                    _retryAt = Environment.TickCount64 + (long)RetryDelay.TotalMilliseconds;
                }
            }
            else if (closed) { stop = opened; blank = true; stopTimeout = DisplayPusher.QuickStopTimeout; }   // quitting: never leave it lit
            else if (!_owner.Power.MayDrive)
            {
                _owner.Log("Windows is ending the session or sleeping; closing the display just opened");
                stop = opened; blank = true; stopTimeout = DisplayPusher.QuickStopTimeout;
            }
            else if (_owner.Paused || _owner.HasPusher) { stop = opened; blank = _owner.Paused; }
            else
            {
                _owner.Adopt(opened);
                _owner.Log(opened.ViaService ? "Display opened through the Glance service" : "Display opened over USB (no Glance service)");
            }
        }
        if (retryLog is not null) _owner.Log(retryLog);

        // outside the lock: a stalled transfer can hold this stop for seconds (L1)
        if (stop is not null && !stop.Stop(blank, stopTimeout) && !closed)
            lock (_owner.Gate) _owner.RememberClosing(stop);

        lock (_owner.Gate) if (--_active == 0) _idle.Set();
        if (closed) return;   // quitting: the owner and its UI are going away (L2)
        _owner.Changed();
        if (opened is null) _ = Task.Delay(RetryDelay + TimeSpan.FromMilliseconds(50)).ContinueWith(_ => { if (!Volatile.Read(ref _closed)) _owner.Changed(); }, TaskScheduler.Default);
    }
}

namespace Glance.Core.Display;

/// <summary>
/// Opens a <see cref="DisplayPusher"/> on the thread pool, one attempt at a time, for an owner whose supervisor runs on a UI
/// thread (the tray app). Opening talks to the Glance service pipe or to USB and can take seconds; the UI thread must never
/// wait for that (Glance 0.2.0 deadlocked doing it in the tray app's constructor, docs/design/fix-tray-deadlock.md).
/// </summary>
public sealed class BackgroundOpener
{
    private int _running;

    /// <summary>True while an attempt runs, until its callback has returned.</summary>
    public bool IsOpening => Volatile.Read(ref _running) != 0;

    /// <summary>
    /// Starts <paramref name="open"/> on the thread pool unless an attempt is already running, and returns at once.
    /// <paramref name="done"/> then gets the new pusher or the error, on the pool thread; it takes ownership of the pusher
    /// (if it throws, the pusher is stopped without blanking).
    /// </summary>
    /// <returns>False when an attempt was already running (nothing was started).</returns>
    public bool TryStart(Func<DisplayPusher> open, Action<DisplayPusher?, Exception?> done)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        _ = Task.Run(() =>
        {
            DisplayPusher? pusher = null;
            Exception? error = null;
            try { pusher = open(); }
            catch (Exception ex) { error = ex; }
            try { done(pusher, error); }
            catch { pusher?.Stop(blank: false, DisplayPusher.QuickStopTimeout); }   // nobody took it: never leave the device claimed
            finally { Volatile.Write(ref _running, 0); }
        });
        return true;
    }
}

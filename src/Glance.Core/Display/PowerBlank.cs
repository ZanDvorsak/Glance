using System.Diagnostics;

namespace Glance.Core.Display;

/// <summary>
/// Blanks the display for a Windows shutdown, sign-out or sleep within a time limit. Windows gives about 5 s for WM_ENDSESSION
/// and about 2 s for PBT_APMSUSPEND; a stalled USB transfer (3 s timeouts) or an owner that holds its lock while it opens the
/// display must not hold Windows past that. The stop itself is the display loop's own bounded
/// <see cref="DisplayPusher.Stop(bool, TimeSpan)"/>: if it times out, the loop still owns the device and closes it when the stuck
/// transfer gives up, so the owner is told (<c>stillClosing</c>) and must not reopen the display before <see cref="DisplayPusher.IsClosed"/>.
/// </summary>
public static class PowerBlank
{
    /// <summary>How long the watcher lets a session-end handler run before it answers Windows.</summary>
    public static readonly TimeSpan SessionEndBudget = TimeSpan.FromSeconds(4);
    /// <summary>How long the watcher lets a sleep (or wake) handler run before it answers Windows.</summary>
    public static readonly TimeSpan SleepBudget = TimeSpan.FromMilliseconds(1500);

    public static TimeSpan BudgetFor(PowerEvent e) => e == PowerEvent.SessionEnding ? SessionEndBudget : SleepBudget;

    /// <summary>What an owner's blank may take: a little less than the watcher's budget, so it can still log and answer.</summary>
    public static TimeSpan OwnerBudgetFor(PowerEvent e) => BudgetFor(e) - TimeSpan.FromMilliseconds(300);

    public enum Result
    {
        /// <summary>Nothing was open, so there was nothing to blank.</summary>
        NothingOpen,
        Blanked,
        /// <summary>A transfer was still stuck when the time ran out. The loop blanks (if it can) and closes the display when it gives up.</summary>
        TimedOut,
        /// <summary>The owner held its lock for too long (for example while opening the display). It sees the policy and blanks itself.</summary>
        Busy,
    }

    /// <param name="gate">The owner's lock around its display field.</param>
    /// <param name="take">Runs under <paramref name="gate"/>: detaches the pusher from the owner and returns it, or null.</param>
    /// <param name="budget">Total time allowed, waiting for the lock included.</param>
    /// <param name="stillClosing">Called when the stop timed out: the pusher still holds the device; do not reopen before it is closed.</param>
    public static Result Run(object gate, Func<DisplayPusher?> take, TimeSpan budget, Action<DisplayPusher>? stillClosing = null)
    {
        var sw = Stopwatch.StartNew();
        DisplayPusher? p;
        if (!Monitor.TryEnter(gate, budget / 3)) return Result.Busy;
        try { p = take(); }
        finally { Monitor.Exit(gate); }
        if (p is null) return Result.NothingOpen;
        var left = budget - sw.Elapsed;
        if (p.Stop(blank: true, left > TimeSpan.Zero ? left : TimeSpan.Zero)) return Result.Blanked;
        stillClosing?.Invoke(p);
        return Result.TimedOut;
    }
}

/// <summary>How long the editor keeps trying to reopen the display after the PC wakes (USB can take a while after hibernate).</summary>
public static class ResumeRetry
{
    /// <summary>1.5 s, 3 s, then every 5 s, about 60 s in total.</summary>
    public static IEnumerable<TimeSpan> Delays()
    {
        yield return TimeSpan.FromSeconds(1.5);
        yield return TimeSpan.FromSeconds(3);
        for (int i = 0; i < 11; i++) yield return TimeSpan.FromSeconds(5);
    }
}

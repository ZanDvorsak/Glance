using System.Diagnostics;

namespace Glance.Core.Display;

/// <summary>
/// Blanks the display for a Windows shutdown, sign-out or sleep within a time limit. Windows gives about 5 s for WM_ENDSESSION
/// and about 2 s for PBT_APMSUSPEND; a stalled USB transfer (3 s timeouts, several in a row) or an owner that holds its lock
/// while it opens the display must not hold Windows past that. A blank that does not finish in time is abandoned on its own
/// background thread (it may still complete), and the owner is told so it can log it.
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
        /// <summary>The blank was started but did not finish in time (it keeps going in the background).</summary>
        TimedOut,
        /// <summary>The owner held its lock for too long (for example while opening the display). It sees the policy and blanks itself.</summary>
        Busy,
    }

    /// <param name="gate">The owner's lock around its display field.</param>
    /// <param name="take">Runs under <paramref name="gate"/>: detaches the display from the owner and returns how to blank and close it, or null.</param>
    /// <param name="budget">Total time allowed, waiting for the lock included.</param>
    public static Result Run(object gate, Func<Action?> take, TimeSpan budget)
    {
        var sw = Stopwatch.StartNew();
        Action? stop;
        if (!Monitor.TryEnter(gate, budget / 3)) return Result.Busy;
        try { stop = take(); }
        finally { Monitor.Exit(gate); }
        if (stop is null) return Result.NothingOpen;
        var left = budget - sw.Elapsed;
        return RunWithDeadline(stop, left > TimeSpan.Zero ? left : TimeSpan.Zero) ? Result.Blanked : Result.TimedOut;
    }

    /// <summary>Runs <paramref name="work"/> on its own background thread and waits at most <paramref name="deadline"/>. True if it finished.</summary>
    public static bool RunWithDeadline(Action work, TimeSpan deadline)
    {
        var t = new Thread(() => { try { work(); } catch { /* best effort; the caller only needs to know whether it finished */ } })
        { IsBackground = true, Name = "Glance power blank" };
        t.Start();
        return t.Join(deadline);
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

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Glance.Core.Control;

/// <summary>
/// Time for leases and heartbeats (review L1 of the live-lease fix). Never the wall clock: setting the clock back would stop
/// heartbeats and expiry, a jump forward would end a lease at once. On Windows it is the "unbiased" interrupt time, which also
/// does not count time asleep, so waking the PC does not make a live theme look silent for the whole night.
/// </summary>
public static class MonotonicClock
{
    private static readonly Stopwatch Fallback = Stopwatch.StartNew();

    /// <summary>Time since an arbitrary start; only differences mean anything.</summary>
    public static TimeSpan Now
    {
        get
        {
            try { if (OperatingSystem.IsWindows() && QueryUnbiasedInterruptTime(out var t)) return TimeSpan.FromTicks((long)t); }   // 100 ns units
            catch { /* fall back */ }
            return Fallback.Elapsed;
        }
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}

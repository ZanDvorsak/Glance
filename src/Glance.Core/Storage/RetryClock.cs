using System.Diagnostics;

namespace Glance.Core.Storage;

/// <summary>
/// How <see cref="AtomicFile"/>'s retry loop measures its budget and waits between tries. The real one uses the
/// system clock, <see cref="AtomicFile.RetryBudget"/> and <see cref="Thread.Sleep(TimeSpan)"/>. Tests swap in their own
/// with <see cref="AtomicFile.UseClock"/> so that "the file is released while the reader waits" happens at a fixed
/// point instead of racing real time.
/// </summary>
internal class RetryClock
{
    /// <summary>How long to keep trying.</summary>
    public virtual TimeSpan Budget => AtomicFile.RetryBudget;

    /// <summary>A mark taken before the first try.</summary>
    public virtual long Start() => Stopwatch.GetTimestamp();

    /// <summary>True once <see cref="Budget"/> has passed since <paramref name="start"/>; no more tries then.</summary>
    public virtual bool Expired(long start) => Stopwatch.GetElapsedTime(start) >= Budget;

    /// <summary>Waits before the next try.</summary>
    public virtual void Wait(TimeSpan delay) => Thread.Sleep(delay);
}

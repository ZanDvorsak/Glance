using System.Runtime.CompilerServices;
using Glance.Core.Storage;

namespace Glance.Tests;

/// <summary>
/// Retry timing for tests. Tests about busy or half-written files do not race real time: they install a
/// <see cref="FakeRetryClock"/> (virtual time; a gate runs at each wait) or, where real threads must race,
/// a <see cref="LongBudgetClock"/>. The shorter default budget below only speeds up incidental retries elsewhere.
/// </summary>
internal static class TestTiming
{
    /// <summary>The real clock's budget in tests (production: one second).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(400);

#pragma warning disable CA2255 // a module initializer is the simplest way to set this before any test runs
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init() => AtomicFile.RetryBudget = Budget;
}

/// <summary>
/// Virtual time for <see cref="AtomicFile"/>'s retry loop: <see cref="Wait"/> moves the clock forward instead of
/// sleeping and then calls <see cref="OnWait"/> with the number of the wait (1, 2, ...). A test uses that as the gate
/// where "the other program" finishes its write or lets go of the file, so the outcome never depends on scheduling.
/// The budget is one second of virtual time (20 waits of 50 ms), and running it out takes no real time.
/// </summary>
internal sealed class FakeRetryClock : RetryClock
{
    private TimeSpan _now;
    public int Waits { get; private set; }
    public Action<int>? OnWait { get; init; }
    public override TimeSpan Budget => TimeSpan.FromSeconds(1);
    public override long Start() => _now.Ticks;
    public override bool Expired(long start) => _now - TimeSpan.FromTicks(start) >= Budget;
    public override void Wait(TimeSpan delay)
    {
        _now += delay;
        Waits++;
        OnWait?.Invoke(Waits);
    }
}

/// <summary>
/// The real clock with a budget far beyond any scheduling delay, for tests where real threads race (a reader during
/// many saves). Waiting only happens while the other thread actually holds the file, so passing runs stay fast; what
/// those tests assert (never a missing or partial file, no lost save) does not depend on timing.
/// </summary>
internal sealed class LongBudgetClock : RetryClock
{
    public override TimeSpan Budget => TimeSpan.FromSeconds(30);
}

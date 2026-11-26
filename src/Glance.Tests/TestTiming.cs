using System.Runtime.CompilerServices;
using Glance.Core.Storage;

namespace Glance.Tests;

/// <summary>
/// Shorter waits for the whole test assembly, so tests about busy files stay fast (review N9). Only the tests in
/// <see cref="AtomicSaveTests"/> and <see cref="AtomicSaveReviewTests"/> depend on these numbers.
/// </summary>
internal static class TestTiming
{
    /// <summary>How long AtomicFile keeps retrying in tests (production: one second).</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(400);
    /// <summary>A lock that clears well inside the budget.</summary>
    public const int BriefMs = 100;
    /// <summary>A lock that outlasts the budget.</summary>
    public const int LongMs = 1500;
    /// <summary>"At once": well under the budget.</summary>
    public const int AtOnceMs = 200;

#pragma warning disable CA2255 // a module initializer is the simplest way to set this before any test runs
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init() => AtomicFile.RetryBudget = Budget;
}

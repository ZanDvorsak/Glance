using System.Diagnostics;
using Glance.Core.Display;
using Glance.Core.Sensors;
using Xunit.Abstractions;

namespace Glance.Tests;

/// <summary>Opens pushers over fake displays for a <see cref="PusherSlot"/> and counts the opens.</summary>
internal sealed class FakeOpener
{
    private static readonly ISensorSource NoSensors = new FakeSource(Array.Empty<SensorInfo>());
    private static readonly PusherTiming Quick = new(ErrorBackoffMs: 50);
    private int _opens;

    public Func<FakeDisplay> Make { get; set; } = () => new FakeDisplay();
    public List<FakeDisplay> Displays { get; } = new();
    public int Opens => Volatile.Read(ref _opens);

    public DisplayPusher Open()
    {
        Interlocked.Increment(ref _opens);
        var d = Make();
        lock (Displays) Displays.Add(d);
        return new DisplayPusher(DisplayPusherTests.Ticking, NoSensors, 7, d, Quick);
    }

    /// <summary>A slot whose first pusher's loop dies at once (its display throws on start), like a loop that failed for good.</summary>
    public static (PusherSlot slot, FakeOpener opener, DisplayPowerPolicy power) WithDeadPusher()
    {
        var power = new DisplayPowerPolicy();
        var opener = new FakeOpener { Make = () => new FakeDisplay { SizeThrows = true } };
        var slot = new PusherSlot(power, opener.Open);
        Assert.True(slot.TryOpen(out _));
        Assert.True(DisplayPusherTests.WaitUntil(() => slot.Current is { IsAlive: false }), "the loop did not die");
        opener.Make = () => new FakeDisplay();
        return (slot, opener, power);
    }
}

/// <summary>Review finding M2: a dead pusher must not be restarted behind the power policy's back.</summary>
public class DeadPusherRestartTests
{
    [Theory]
    [InlineData(PowerEvent.Suspend)]
    [InlineData(PowerEvent.SessionEnding)]
    public void A_dead_pusher_is_not_restarted_while_Windows_sleeps_or_after_the_session_ended(PowerEvent e)
    {
        var (slot, opener, power) = FakeOpener.WithDeadPusher();
        power.Handle(e);   // the watcher updated the policy; its blank has not taken the pusher yet (the race in M2)

        var result = slot.RestartIfDead(out _, out _);

        Assert.Equal(PusherSlot.RestartResult.NotNow, result);
        Assert.Null(slot.Current);
        Assert.Equal(1, opener.Opens);   // never reopened
    }

    [Fact]
    public void A_dead_pusher_is_restarted_while_awake()
    {
        var (slot, opener, _) = FakeOpener.WithDeadPusher();
        Assert.Equal(PusherSlot.RestartResult.Reopened, slot.RestartIfDead(out var why, out _));
        Assert.NotNull(why);
        Assert.Equal(2, opener.Opens);
        Assert.True(slot.Current is { IsAlive: true });
        slot.Release(blank: false, DisplayPusher.QuickStopTimeout);
    }

    [Fact]
    public void A_dead_pusher_already_released_by_a_power_event_is_left_alone()
    {
        var (slot, opener, power) = FakeOpener.WithDeadPusher();
        power.Handle(PowerEvent.Suspend);
        slot.BlankForPowerEvent(PowerEvent.Suspend, out var hadOne);
        Assert.True(hadOne);
        power.Handle(PowerEvent.Resume);
        Assert.Equal(PusherSlot.RestartResult.NotNeeded, slot.RestartIfDead(out _, out _));
        Assert.Equal(1, opener.Opens);
    }
}

/// <summary>The power-event blank with the display loop's own bounded Stop, timed against fake displays.</summary>
public class PowerBlankTimingTests
{
    private readonly ITestOutputHelper _out;
    public PowerBlankTimingTests(ITestOutputHelper output) => _out = output;

    private static (PusherSlot slot, DisplayPowerPolicy power, FakeDisplay lcd, FakeOpener opener) Running(int blitDelayMs)
    {
        var power = new DisplayPowerPolicy();
        var opener = new FakeOpener { Make = () => new FakeDisplay { BlitDelayMs = blitDelayMs } };
        var slot = new PusherSlot(power, opener.Open);
        Assert.True(slot.TryOpen(out var err), err);
        var lcd = opener.Displays[0];
        Assert.True(lcd.BlitStarted.Wait(5000), "no frame was sent");
        return (slot, power, lcd, opener);
    }

    [Theory]
    [InlineData(PowerEvent.SessionEnding)]
    [InlineData(PowerEvent.Suspend)]
    public void A_stalled_display_is_released_within_the_budget_and_not_reopened_while_it_is_still_held(PowerEvent e)
    {
        var (slot, power, _, _) = Running(blitDelayMs: 10_000);   // a transfer that hangs far longer than Windows waits
        power.Handle(e);
        var sw = Stopwatch.StartNew();
        var result = slot.BlankForPowerEvent(e, out var hadOne);
        sw.Stop();
        _out.WriteLine($"{e}: stalled display -> {result} after {sw.ElapsedMilliseconds} ms (budget {PowerBlank.BudgetFor(e).TotalMilliseconds} ms)");

        Assert.True(hadOne);
        Assert.Equal(PowerBlank.Result.TimedOut, result);
        Assert.True(sw.Elapsed < PowerBlank.BudgetFor(e), $"took {sw.ElapsedMilliseconds} ms");
        Assert.Null(slot.Current);
        Assert.NotNull(slot.StillClosing());   // the stuck loop still holds the device: it must not be opened again yet
    }

    [Fact]
    public void After_a_stuck_sleep_blank_the_display_is_reopened_only_once_the_old_loop_lets_go()
    {
        var (slot, power, _, opener) = Running(blitDelayMs: 2500);   // stuck past the sleep budget, then gives up
        power.Handle(PowerEvent.Suspend);
        Assert.Equal(PowerBlank.Result.TimedOut, slot.BlankForPowerEvent(PowerEvent.Suspend, out _));
        power.Handle(PowerEvent.Resume);

        Assert.False(slot.TryOpen(out var error));          // straight after wake: still held
        Assert.Contains("finishing its last transfer", error);
        Assert.Equal(1, opener.Opens);

        Assert.True(DisplayPusherTests.WaitUntil(() => slot.StillClosing() is null, 6000), "the old loop never let go");
        Assert.True(slot.TryOpen(out error), error);
        Assert.Equal(2, opener.Opens);
        slot.Release(blank: false, DisplayPusher.QuickStopTimeout);
    }

    [Theory]
    [InlineData(PowerEvent.SessionEnding)]
    [InlineData(PowerEvent.Suspend)]
    public void A_working_display_is_blanked_well_within_the_budget(PowerEvent e)
    {
        var (slot, power, lcd, _) = Running(blitDelayMs: 400);   // about the real display's full-frame time
        power.Handle(e);
        var sw = Stopwatch.StartNew();
        var result = slot.BlankForPowerEvent(e, out _);
        sw.Stop();
        _out.WriteLine($"{e}: working display -> {result} after {sw.ElapsedMilliseconds} ms (budget {PowerBlank.BudgetFor(e).TotalMilliseconds} ms)");

        Assert.Equal(PowerBlank.Result.Blanked, result);
        Assert.Equal(1, lcd.Blanks);
        Assert.Equal(1, lcd.Disposes);
        Assert.True(sw.Elapsed < PowerBlank.BudgetFor(e), $"took {sw.ElapsedMilliseconds} ms");
        Assert.Null(slot.StillClosing());
    }
}

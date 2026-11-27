using System.Diagnostics;
using System.Text.RegularExpressions;
using Glance.Core.Control;

namespace Glance.Tests;

/// <summary>Findings of docs/reviews/review-live-lease-pipes.md. Written before the fixes; each failed on the branch at 13ec427.</summary>
public partial class LiveLeaseTests
{
    private const string Taken = "ERR live taken";

    // ---- M1: two live editors must not fight ---------------------------------------------------------------------------

    [Fact]
    public async Task M1_a_second_editor_taking_the_live_theme_tells_the_first_instead_of_fighting()
    {
        using var tray = new FakeTray(TestPipe());
        using var first = new TrayLink(tray.Pipe);
        using var second = new TrayLink(tray.Pipe);
        Assert.Equal("OK", await first.SendAsync(ControlCommand.Live(LiveJson("First"), null)));
        Assert.Equal("OK", await second.SendAsync(ControlCommand.Live(LiveJson("Second"), null)));
        // before: "ERR no live theme", so the first editor sent its theme again and took it back, every 1.5 s
        Assert.Equal(Taken, await first.SendAsync(ControlCommand.LiveKeep));
        Assert.Equal("Second", tray.Read(t => t.State.Live?.Name));
    }

    [Fact]
    public void M1_the_first_editors_live_clear_does_not_clear_the_second_editors_theme()
    {
        var state = new TrayLiveState();
        state.Handle(ControlCommand.Live(LiveJson("First"), null), connection: 1);
        state.Handle(ControlCommand.Live(LiveJson("Second"), null), connection: 2);
        Assert.Equal(Taken, state.Handle(ControlCommand.LiveClear, connection: 1));   // the first editor stops live
        Assert.Equal("Second", state.Live?.Name);
    }

    // ---- M2: the page pin belongs to a connection ------------------------------------------------------------------------

    [Fact]
    public void M2_a_page_pin_ends_when_the_connection_that_set_it_closes()
    {
        var state = new TrayLiveState();
        state.Handle(ControlCommand.Page(3), connection: 4);   // PAGE stuck, but its LIVE never came (editor gone, or LIVE rejected)
        Assert.Equal(2, state.PinnedPage);
        Assert.True(state.ConnectionClosed(4), "closing the pin's connection changed nothing");
        Assert.Null(state.PinnedPage);
    }

    [Fact]
    public void M2_the_editor_sends_its_pin_and_brightness_again_when_the_tray_lost_its_theme()
    {
        // The editor's code for it lives in MainWindow (KeepLive): after "ERR no live theme" it must queue BRIGHTNESS and PAGE
        // before the LIVE that the gate sends again, as StartLive does.
        var keepLive = MethodBody(MainWindowSource(), "KeepLive");
        Assert.Contains("RestoreCommands", keepLive);
    }

    // ---- L1: a monotonic clock for the lease and the heartbeat -------------------------------------------------------------

    [Fact]
    public void L1_the_lease_and_the_heartbeat_never_read_the_wall_clock()
    {
        var core = Path.Combine(SourceRoot(), "Glance.Core", "Control");
        var offenders = new[] { "TrayLiveState.cs", "LiveKeepAlive.cs" }
            .Where(f => Regex.IsMatch(File.ReadAllText(Path.Combine(core, f)), @"DateTime\.(UtcNow|Now)\b"))
            .ToList();
        if (Regex.IsMatch(MethodBody(MainWindowSource(), "KeepLive"), @"DateTime\.(UtcNow|Now)\b")) offenders.Add("MainWindow.KeepLive");
        Assert.True(offenders.Count == 0, "wall clock used in: " + string.Join(", ", offenders));
    }

    // ---- L2: ticking Send asks the tray now ----------------------------------------------------------------------------

    [Fact]
    public void L2_ticking_send_probes_the_tray_fresh_instead_of_trusting_the_cached_answer()
    {
        var toggle = MethodBody(MainWindowSource(), "ToggleSend");
        Assert.DoesNotContain("RunnerClient.Presence", toggle);
        Assert.Contains("RunnerClient.Probe", toggle);
    }

    // ---- L3: a reply given up on frees the connection at once ----------------------------------------------------------

    [Fact]
    public async Task L3_a_reply_given_up_on_closes_the_connection_at_once()
    {
        // A tray that reads the command and never answers (stuck behind its lock). Once the link gives up, the tray's end must
        // see the editor hang up, so the connection (one of the tray's 4) is not held until the tray finally answers.
        var pipe = TestPipe();
        using var server = new System.IO.Pipes.NamedPipeServerStream(pipe, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        using var link = new TrayLink(pipe, replyTimeout: TimeSpan.FromMilliseconds(300));
        var reply = link.SendAsync(ControlCommand.Status);
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var buf = new byte[64];
        await server.ReadAsync(buf).AsTask().WaitAsync(TimeSpan.FromSeconds(5));   // "STATUS\n", never answered
        Assert.Null(await reply.WaitAsync(TimeSpan.FromSeconds(5)));
        var hungUp = server.ReadAsync(buf).AsTask();                              // 0 bytes once the editor's handle is closed
        var done = await Task.WhenAny(hungUp, Task.Delay(2000));
        Assert.True(done == hungUp && hungUp.Result == 0, "the editor's end of the pipe stayed open after the link gave up");
    }


    // ---- M3: Send is bounded -------------------------------------------------------------------------------------------

    [Fact]
    public void M3_send_gives_up_in_time_even_behind_a_slow_queue()
    {
        using var tray = new FakeTray(TestPipe(), beforeApply: v => { if (v == ControlVerb.Page) Thread.Sleep(1500); });
        using var link = new TrayLink(tray.Pipe, replyTimeout: TimeSpan.FromSeconds(2), connectTimeoutMs: 1000);
        for (int i = 0; i < 4; i++) link.Post(ControlCommand.Page(1));   // about 6 s of work ahead
        var watch = Stopwatch.StartNew();
        var reply = link.Send(ControlCommand.Status);
        Assert.True(watch.ElapsedMilliseconds < 4500, $"Send waited {watch.ElapsedMilliseconds} ms (reply {reply ?? "null"})");
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------

    private static string MainWindowSource() => File.ReadAllText(Path.Combine(SourceRoot(), "Glance.App", "MainWindow.cs"));

    /// <summary>The text of a method of the editor, from its declaration to the closing brace at the same indentation.</summary>
    private static string MethodBody(string source, string name)
    {
        var m = Regex.Match(source, @"\n(?<indent>[ ]+)(private|internal|public)[^\n]*\b" + name + @"\(");
        Assert.True(m.Success, name + " not found");
        var end = source.IndexOf("\n" + m.Groups["indent"].Value + "}", m.Index, StringComparison.Ordinal);
        return source[m.Index..end];
    }

    // ---- added with the fixes (new behaviour and the review nit; no "before") -------------------------------------------

    [Fact]
    public void M1_the_editor_stops_instead_of_resending_when_another_editor_took_over()
    {
        Assert.Equal(KeepResult.Taken, new LiveKeepAlive().Complete(TrayLiveState.TakenReply));
    }

    [Fact]
    public void M2_the_restore_commands_carry_brightness_and_pin_in_order()
    {
        Assert.Equal(new[] { "BRIGHTNESS 4", "PAGE 3" }, LiveKeepAlive.RestoreCommands(4, pinnedPage: 2).Select(c => c.Encode()));
        Assert.Equal(new[] { "BRIGHTNESS 7", "PAGE 0" }, LiveKeepAlive.RestoreCommands(7, pinnedPage: null).Select(c => c.Encode()));
    }

    [Fact]
    public void M2_the_pin_of_a_connection_that_stays_open_survives_another_closing()
    {
        var state = new TrayLiveState();
        state.Handle(ControlCommand.Page(2), connection: 1);
        state.Handle(ControlCommand.Live(LiveJson(), null), connection: 1);
        Assert.False(state.ConnectionClosed(2));
        Assert.Equal(1, state.PinnedPage);
        Assert.Equal("OK", state.Handle(ControlCommand.Page(0), connection: 1));   // PAGE 0 releases it
        Assert.Null(state.PinnedPage);
    }

    [Fact]
    public void L1_waking_up_restarts_the_lease()
    {
        var clock = new Clock();
        var state = new TrayLiveState(TimeSpan.FromSeconds(5), () => clock.Now);
        state.Handle(ControlCommand.Live(LiveJson(), null), connection: 1);
        clock.Advance(4.5);
        state.Renew();             // Windows resumed
        clock.Advance(4.5);
        Assert.False(state.Expire(), "the saved theme flashed right after waking");
        clock.Advance(1);
        Assert.True(state.Expire());
    }

    [Fact]
    public void L1_the_monotonic_clock_moves_forward()
    {
        var a = MonotonicClock.Now;
        Thread.Sleep(30);
        Assert.True(MonotonicClock.Now > a);
    }

    [Fact]
    public async Task Nit_the_trays_own_timer_ends_a_live_theme_nobody_keeps_alive()
    {
        // through TrayLiveHost, the class RunnerContext uses: nobody calls Expire by hand here
        using var tray = new FakeTray(TestPipe(), lease: TimeSpan.FromMilliseconds(400), tick: TimeSpan.FromMilliseconds(50));
        using var link = new TrayLink(tray.Pipe);
        Assert.Equal("OK", await link.SendAsync(ControlCommand.Live(LiveJson(), null)));
        Assert.True(await Eventually(() => tray.Read(t => t.State.Live is null), 3000), "the lease never ran out");
        Assert.True(await Eventually(() => { lock (tray.Log) return tray.Log.Any(l => l.Contains("no word from the editor")); }, 2000));
    }

    [Fact]
    public async Task Nit_status_from_the_editor_keeps_its_live_theme()
    {
        using var tray = new FakeTray(TestPipe(), lease: TimeSpan.FromMilliseconds(600), tick: TimeSpan.FromMilliseconds(50));
        using var link = new TrayLink(tray.Pipe);
        Assert.Equal("OK", await link.SendAsync(ControlCommand.Live(LiveJson(), null)));
        for (int i = 0; i < 10; i++)
        {
            await Task.Delay(150);
            Assert.StartsWith("OK", await link.SendAsync(ControlCommand.Status));   // the probe's STATUS on the same connection
        }
        Assert.NotNull(tray.Read(t => t.State.Live));   // 1.5 s, much longer than the lease
    }
}

using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Glance.Core.Control;
using Glance.Core.Themes;

namespace Glance.Tests;

/// <summary>
/// Review M5 (a stale live theme stays on the display after the editor is gone; commands can overtake each other) and
/// M13 ("tray running" judged by a process name). Pipe tests use random names, never the real ones.
/// </summary>
public partial class LiveLeaseTests
{
    private static string TestPipe() => "glance-test-tray-" + Guid.NewGuid().ToString("N");

    private static string LiveJson(string name = "Unsaved") => new Theme { Name = name }.ToSnapshot();

    private static async Task<bool> Eventually(Func<bool> condition, int timeoutMs)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    /// <summary>A monotonic clock the test moves by hand.</summary>
    private sealed class Clock
    {
        public TimeSpan Now = TimeSpan.FromHours(5);
        public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// The tray app's control side with the tray's own wiring: <see cref="TrayControlServer"/> on a test pipe and
    /// <see cref="TrayLiveHost"/> (the class <c>RunnerContext</c> uses) under one lock. Every command is recorded with its
    /// connection; <paramref name="beforeApply"/> runs before a command is applied (outside the lock), so a test can slow one down.
    /// </summary>
    private sealed class FakeTray : IDisposable
    {
        private readonly object _gate = new();
        private readonly Action<ControlVerb>? _beforeApply;
        public readonly TrayLiveHost Host;
        public readonly List<(ControlVerb Verb, long Connection)> Seen = new();
        public readonly List<string> Log = new();
        public readonly TrayControlServer Server;
        public readonly string Pipe;
        public int Changes;   // how often the host said the live theme or pin may have changed

        public FakeTray(string pipe, Action<ControlVerb>? beforeApply = null, TimeSpan? lease = null, TimeSpan? tick = null)
        {
            Pipe = pipe;
            _beforeApply = beforeApply;
            Host = new TrayLiveHost(_gate, () => Changes++, AddLog, lease, tick: tick);
            Server = new TrayControlServer(pipe, Execute, Host.ConnectionClosed, AddLog);
        }

        public TrayLiveState State => Host.State;

        /// <summary>Set after each command was applied (outside the lock).</summary>
        public Action<ControlVerb>? AfterApply;

        private void AddLog(string line) { lock (Log) Log.Add(line); }

        private string Execute(string line, long connection)
        {
            if (!ControlCommand.TryParse(line, out var cmd, out var error)) return "ERR " + error;
            _beforeApply?.Invoke(cmd.Verb);
            lock (_gate) Seen.Add((cmd.Verb, connection));
            var reply = Host.ExecuteLine(line, connection, _ => "OK fake tray");   // the tray's own entry point
            AfterApply?.Invoke(cmd.Verb);
            return reply;
        }

        public T Read<T>(Func<FakeTray, T> read) { lock (_gate) return read(this); }

        public void Dispose() { Server.Dispose(); Host.Dispose(); }
    }


    /// <summary>An editor that sends LIVE on its own connection and then disappears (crash, kill, Task Manager) without LIVE_CLEAR.</summary>
    private static string SendLiveAndVanish(string pipe)
    {
        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, ControlPipeClient.Options, TokenImpersonationLevel.Anonymous);
        client.Connect(3000);
        client.Write(new UTF8Encoding(false).GetBytes(ControlCommand.Live(LiveJson(), null).Encode() + "\n"));
        using var reader = new StreamReader(client, Encoding.UTF8, false, 1024, leaveOpen: true);
        return reader.ReadLine() ?? "";
    }   // disposed here: Windows closes the pipe of a process that ends the same way

    // ---- M5: the live theme is leased -----------------------------------------------------------------------------------

    [Fact]
    public async Task M5_the_live_theme_ends_when_the_editor_that_sent_it_goes_away()
    {
        using var tray = new FakeTray(TestPipe());
        Assert.Equal("OK", await Task.Run(() => SendLiveAndVanish(tray.Pipe)));
        Assert.True(await Eventually(() => tray.Read(t => t.State.Live is null), 3000),
            "the editor's unsaved theme stayed on the display after the editor was gone");
    }

    [Fact]
    public void M5_the_live_theme_ends_after_the_lease_without_a_word_from_the_editor()
    {
        var clock = new Clock();
        var state = new TrayLiveState(TimeSpan.FromSeconds(5), () => clock.Now);
        Assert.Equal("OK", state.Handle(ControlCommand.Live(LiveJson(), null), connection: 1));
        Assert.Equal("OK", state.Handle(ControlCommand.Page(2), connection: 1));
        clock.Advance(4);
        Assert.False(state.Expire());
        Assert.NotNull(state.Live);
        clock.Advance(2);   // 6 s without a word: the editor hangs, or its connection is stuck
        Assert.True(state.Expire(), "a live theme nobody keeps alive did not end");
        Assert.Null(state.Live);
        Assert.Null(state.PinnedPage);
    }

    [Fact]
    public void M5_a_heartbeat_keeps_the_live_theme()
    {
        var clock = new Clock();
        var state = new TrayLiveState(TimeSpan.FromSeconds(5), () => clock.Now);
        state.Handle(ControlCommand.Live(LiveJson(), null), connection: 1);
        for (int i = 0; i < 5; i++)
        {
            clock.Advance(3);
            Assert.Equal("OK", state.Handle(ControlCommand.LiveKeep, connection: 1));
            Assert.False(state.Expire());
        }
        Assert.Equal("Unsaved", state.Live?.Name);
    }

    [Fact]
    public void M5_a_heartbeat_without_a_live_theme_asks_the_editor_to_send_it_again()
    {
        var state = new TrayLiveState();
        Assert.Equal(TrayLiveState.NotHeldReply, state.Handle(ControlCommand.LiveKeep, connection: 1));   // e.g. the tray restarted
        state.Handle(ControlCommand.Live(LiveJson(), null), connection: 1);
        Assert.Equal(TrayLiveState.TakenReply, state.Handle(ControlCommand.LiveKeep, connection: 2));    // another editor shows its theme (review M1)
        Assert.Equal("OK", state.Handle(ControlCommand.LiveKeep, connection: 1));
    }

    [Fact]
    public void M5_the_editors_page_pin_ends_with_its_live_theme()
    {
        var state = new TrayLiveState();
        state.Handle(ControlCommand.Page(2), connection: 7);
        state.Handle(ControlCommand.Live(LiveJson(), null), connection: 7);
        Assert.Equal(1, state.PinnedPage);
        Assert.True(state.ConnectionClosed(7));
        Assert.Null(state.Live);
        Assert.Null(state.PinnedPage);
    }

    [Fact]
    public void M5_another_connection_closing_leaves_the_live_theme_alone()   // guard: STATUS from the setup check, a script, ...
    {
        var state = new TrayLiveState();
        state.Handle(ControlCommand.Live(LiveJson(), null), connection: 1);
        Assert.False(state.ConnectionClosed(2));
        Assert.NotNull(state.Live);
        // changed after review M1: only the connection that shows its theme may clear it (another editor's LIVE_CLEAR must not)
        Assert.Equal(TrayLiveState.TakenReply, state.Handle(ControlCommand.LiveClear, connection: 2));
        Assert.NotNull(state.Live);
        Assert.Equal("OK", state.Handle(ControlCommand.LiveClear, connection: 1));
        Assert.Null(state.Live);
    }

    // ---- M5: one ordered connection -----------------------------------------------------------------------------------

    [Fact]
    public async Task M5_live_clear_never_overtakes_the_live_before_it()
    {
        // The tray needs a moment for LIVE (it waits for its lock while the display reopens). LIVE_CLEAR sent right after it,
        // for example when the editor closes, must still be applied after it, or the unsaved theme stays.
        // The fake tray holds LIVE until a LIVE_CLEAR has been applied, or 1.5 s: the window in which a LIVE_CLEAR on another
        // connection gets in first.
        using var clearApplied = new ManualResetEventSlim();
        using var tray = new FakeTray(TestPipe(), beforeApply: verb => { if (verb == ControlVerb.Live) clearApplied.Wait(1500); });
        tray.AfterApply = verb => { if (verb == ControlVerb.LiveClear) clearApplied.Set(); };
        using var link = new TrayLink(tray.Pipe);
        link.Post(ControlCommand.Live(LiveJson(), null));
        link.Post(ControlCommand.LiveClear);
        Assert.True(await Eventually(() => tray.Read(t => t.Seen.Count) == 2, 8000), "the tray did not get both commands");
        var order = tray.Read(t => t.Seen.Select(s => s.Verb).ToArray());
        Assert.Equal(new[] { ControlVerb.Live, ControlVerb.LiveClear }, order);
        Assert.Null(tray.Read(t => t.State.Live));
    }

    [Fact]
    public async Task M5_the_editors_commands_share_one_connection()
    {
        // one connection: the tray sees the editor go away when it closes, and the commands arrive in the order sent
        using var tray = new FakeTray(TestPipe());
        using var link = new TrayLink(tray.Pipe);
        for (int i = 0; i < 3; i++) Assert.StartsWith("OK", await link.SendAsync(ControlCommand.Status));
        var connections = tray.Read(t => t.Seen.Select(s => s.Connection).Distinct().Count());
        Assert.Equal(1, connections);
    }

    [Fact]
    public void M5_closing_the_link_delivers_what_is_still_queued()
    {
        using var tray = new FakeTray(TestPipe(), beforeApply: _ => Thread.Sleep(300));
        var link = new TrayLink(tray.Pipe);
        link.Post(ControlCommand.Live(LiveJson(), null));
        link.Post(ControlCommand.LiveClear);
        Assert.True(link.Close(TimeSpan.FromSeconds(5)), "closing did not wait for the queued commands");
        Assert.Contains(tray.Read(t => t.Seen.Select(s => s.Verb).ToList()), v => v == ControlVerb.LiveClear);
        Assert.Null(tray.Read(t => t.State.Live));
    }

    [Fact]
    public async Task M5_the_link_reaches_a_tray_that_was_restarted()   // guard: a dead connection is replaced, not reused for ever
    {
        var pipe = TestPipe();
        using var link = new TrayLink(pipe);
        using (var first = new FakeTray(pipe)) Assert.StartsWith("OK", await link.SendAsync(ControlCommand.Status));
        await Task.Delay(300);   // the old tray's connection is gone now
        using var second = new FakeTray(pipe);
        Assert.StartsWith("OK", await link.SendAsync(ControlCommand.Status));
    }

    // ---- M13: "is the tray there" by a round trip ------------------------------------------------------------------------

    [Fact]
    public void M13_a_tray_process_that_does_not_answer_is_not_used()
    {
        // Process.GetProcessesByName("Glance.Runner") found one in this session, but its pipe does not answer us
        Assert.Equal(TrayState.NotAnswering, TrayPresence.Decide(statusReply: null, new bool?[] { true }));
    }

    [Fact]
    public void M13_a_tray_of_another_account_in_this_session_is_told_apart()
    {
        Assert.Equal(TrayState.OtherAccount, TrayPresence.Decide(null, new bool?[] { false }));
        Assert.Equal(TrayState.OtherAccount, TrayPresence.Decide(null, new bool?[] { null }));   // owner unknown: not ours to use
        Assert.Contains("another account", TrayPresence.Explain(TrayState.OtherAccount));
        Assert.Contains("does not answer", TrayPresence.Explain(TrayState.NotAnswering));
    }

    [Fact]
    public void M13_a_tray_that_answers_is_used_whatever_its_process_is_called()
    {
        Assert.Equal(TrayState.Answering, TrayPresence.Decide("OK Showing \"Mine\" | page 1/1", Array.Empty<bool?>()));
        Assert.Equal(TrayState.Answering, TrayPresence.Decide("OK", new bool?[] { false }));   // ours answered; a stray one does not matter
    }

    [Fact]
    public void M13_no_answer_and_no_tray_process_means_none()   // guard: the editor then draws the display itself
    {
        Assert.Equal(TrayState.None, TrayPresence.Decide(null, Array.Empty<bool?>()));
        Assert.Equal(TrayState.None, TrayPresence.Decide("ERR unknown command", Array.Empty<bool?>()));
    }

    [Fact]
    public async Task M13_the_probe_asks_the_tray_over_its_pipe()
    {
        using var tray = new FakeTray(TestPipe());
        var pipe = tray.Pipe;
        var found = await Task.Run(() => TrayPresence.Probe(() => ControlPipeClient.Send(pipe, ControlCommand.Status), () => Array.Empty<bool?>()));
        Assert.Equal(TrayState.Answering, found);
        var silent = await Task.Run(() => TrayPresence.Probe(() => ControlPipeClient.Send(TestPipe(), ControlCommand.Status), () => new bool?[] { true }));
        Assert.Equal(TrayState.NotAnswering, silent);
    }

    // ---- the move into Core kept the tray pipe's protection ------------------------------------------------------------

    [Fact]
    public async Task The_tray_control_server_does_not_join_a_squatted_name()   // review L8, kept by the move
    {
        var pipe = TestPipe();
        using var squatter = new NamedPipeServerStream(pipe, PipeDirection.InOut, 254, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var tray = new FakeTray(pipe);
        Assert.True(await Eventually(() => { lock (tray.Log) return tray.Log.Any(l => l.Contains("taken")); }, 4000), "the squatted name was not refused");
    }

    [Fact]
    public void The_tray_control_server_keeps_the_pipe_rules()   // review L1, L8, T1: the rules moved with the code
    {
        var src = File.ReadAllText(Path.Combine(SourceRoot(), "Glance.Core", "Control", "TrayControlServer.cs"));
        Assert.Contains("ServicePipeSecurity.CurrentUserOnly()", src);
        Assert.Contains("first: Volatile.Read(ref _instances) == 0", src);
        Assert.Contains("NativeSessions.IsRemoteClient(pipe)", src);
        var runner = File.ReadAllText(Path.Combine(SourceRoot(), "Glance.Runner", "RunnerContext.cs"));
        Assert.Contains("new(ControlPipeName, Execute, _live.ConnectionClosed, Log)", runner);   // the default (current user only) rules
    }

    private static string SourceRoot()
    {
        for (var d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
            if (File.Exists(Path.Combine(d, "Glance.sln"))) return d;
        throw new DirectoryNotFoundException("Glance.sln not found above " + AppContext.BaseDirectory);
    }

    // ---- added with the fix (new behaviour, no "before") ------------------------------------------------------------

    [Fact]
    public void M13_this_users_own_process_counts_as_its_own()
    {
        using var me = Process.GetCurrentProcess();
        Assert.True(TrayPresence.IsMine(me));
    }

    [Fact]
    public void M13_a_tray_state_the_editor_cannot_use_is_explained()
    {
        foreach (var s in new[] { TrayState.NotAnswering, TrayState.OtherAccount, TrayState.Unknown }) Assert.False(string.IsNullOrWhiteSpace(TrayPresence.Explain(s)));
        Assert.Null(TrayPresence.Explain(TrayState.Answering));
        Assert.Null(TrayPresence.Explain(TrayState.None));
    }

    [Fact]
    public void M5_the_heartbeat_goes_out_well_inside_the_lease_and_one_at_a_time()
    {
        Assert.True(LiveKeepAlive.Interval * 3 <= TrayLiveState.DefaultLease, "a heartbeat or two may be late without losing the lease");
        var keep = new LiveKeepAlive();
        var t = TimeSpan.FromHours(5);
        Assert.True(keep.TryBegin(t));
        Assert.False(keep.TryBegin(t + TimeSpan.FromSeconds(3)));          // still on its way
        Assert.Equal(KeepResult.Kept, keep.Complete("OK"));
        Assert.False(keep.TryBegin(t + TimeSpan.FromSeconds(1)));          // too soon after the last one
        Assert.True(keep.TryBegin(t + TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void M5_only_the_trays_not_held_reply_makes_the_editor_send_its_theme_again()
    {
        var keep = new LiveKeepAlive();
        Assert.Equal(KeepResult.Resend, keep.Complete(TrayLiveState.NotHeldReply));
        Assert.Equal(KeepResult.NoAnswer, keep.Complete(null));                      // no answer: the link reconnects and the next heartbeat asks again
        Assert.Equal(KeepResult.Other, keep.Complete("ERR unknown command"));     // an older tray: resending every 1.5 s would not help it
        Assert.Equal(KeepResult.Kept, keep.Complete("OK"));

        var gate = new LiveSendGate();
        var send = gate.TryBegin("snap")!;
        gate.Complete(send, "OK");
        Assert.Null(gate.TryBegin("snap"));                     // already on the display
        gate.Lost();
        Assert.NotNull(gate.TryBegin("snap"));                  // the tray lost it: the same snapshot goes again
    }

    [Fact]
    public async Task M5_a_closed_link_answers_at_once_with_no_reply()
    {
        var link = new TrayLink(TestPipe());
        Assert.True(link.Close(TimeSpan.FromSeconds(1)));
        var watch = Stopwatch.StartNew();
        Assert.Null(await link.SendAsync(ControlCommand.Status));
        Assert.True(watch.ElapsedMilliseconds < 500, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task M5_a_tray_that_does_not_answer_in_time_costs_one_timeout_and_the_link_recovers()
    {
        int slow = 1;
        using var tray = new FakeTray(TestPipe(), beforeApply: _ => { if (Interlocked.Exchange(ref slow, 0) == 1) Thread.Sleep(1500); });
        using var link = new TrayLink(tray.Pipe, replyTimeout: TimeSpan.FromMilliseconds(400));
        Assert.Null(await link.SendAsync(ControlCommand.Status));                   // given up after 400 ms, connection dropped
        Assert.StartsWith("OK", await link.SendAsync(ControlCommand.Status));       // a new connection; the late reply is not mixed in
    }

    [Fact]
    public async Task M5_a_live_theme_the_tray_lost_is_asked_for_again_over_the_link()
    {
        var pipe = TestPipe();
        using var link = new TrayLink(pipe);
        using (var first = new FakeTray(pipe))
        {
            Assert.Equal("OK", await link.SendAsync(ControlCommand.Live(LiveJson(), null)));
            Assert.Equal("OK", await link.SendAsync(ControlCommand.LiveKeep));
        }
        await Task.Delay(300);
        using var restarted = new FakeTray(pipe);
        Assert.Equal(TrayLiveState.NotHeldReply, await link.SendAsync(ControlCommand.LiveKeep));
    }

    [Fact]
    public async Task M13_a_squatter_on_the_tray_pipe_cannot_act_as_the_editor_through_the_link()   // T1, kept by the new client
    {
        var pipe = TestPipe();
        using var squatter = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var link = new TrayLink(pipe, replyTimeout: TimeSpan.FromSeconds(1));
        var reply = link.SendAsync(ControlCommand.Status);
        await squatter.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await squatter.ReadAsync(new byte[3]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        string? actedAs = null;
        try { squatter.RunAsClient(() => { using var id = WindowsIdentity.GetCurrent(); actedAs = id.User?.Value; }); }
        catch { actedAs = null; }   // impersonation refused: what we want
        using var me = WindowsIdentity.GetCurrent();
        Assert.NotEqual(me.User?.Value, actedAs);
        squatter.Disconnect();
        Assert.Null(await reply);
    }
}

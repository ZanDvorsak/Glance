using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Glance.Core.Control;

namespace Glance.Tests;

/// <summary>
/// How the editor streams its live theme to the runner (review of H2: findings M2, L1, L2, L3).
/// The gate is used on the editor's UI thread; these tests drive it step by step like the 500 ms live timer does.
/// </summary>
public class LiveSendGateTests
{
    [Fact]
    public void A_failed_live_send_is_retried_on_the_next_tick()   // M2
    {
        var gate = new LiveSendGate();
        var first = gate.TryBegin("theme A");
        Assert.NotNull(first);
        gate.Complete(first!, null);                       // the runner did not answer
        Assert.NotNull(gate.TryBegin("theme A"));          // same snapshot, but it never arrived: send it again
    }

    [Fact]
    public void A_successful_send_is_not_repeated()
    {
        var gate = new LiveSendGate();
        gate.Complete(gate.TryBegin("theme A")!, "OK");
        Assert.Null(gate.TryBegin("theme A"));
    }

    [Fact]
    public void Only_one_send_is_in_flight_and_the_newest_snapshot_goes_next()   // M2 and L3
    {
        var gate = new LiveSendGate();
        var a = gate.TryBegin("edit 1");
        Assert.NotNull(a);
        Assert.Null(gate.TryBegin("edit 2"));             // edit 1 is still on its way
        Assert.Null(gate.TryBegin("edit 3"));
        gate.Complete(a!, "OK");
        var next = gate.TryBegin("edit 3");
        Assert.NotNull(next);
        Assert.Equal("edit 3", next!.Snapshot);           // the newest edit, so an older one can never arrive last
    }

    [Fact]
    public void The_warning_gives_way_to_the_normal_status_after_a_later_send_succeeds()   // M2
    {
        var gate = new LiveSendGate();
        var failed = gate.Complete(gate.TryBegin("theme A")!, null);
        Assert.False(string.IsNullOrEmpty(failed));
        Assert.NotEqual(LiveSendGate.ShowingText, failed);
        var retry = gate.TryBegin("theme A");
        Assert.NotNull(retry);
        Assert.Equal(LiveSendGate.ShowingText, gate.Complete(retry!, "OK"));
    }

    [Fact]
    public void Success_without_an_earlier_warning_leaves_the_status_alone()
    {
        var gate = new LiveSendGate();
        Assert.Null(gate.Complete(gate.TryBegin("theme A")!, "OK"));
    }

    [Fact]
    public void No_answer_and_a_rejection_have_different_messages()   // L1
    {
        var silent = new LiveSendGate();
        var noAnswer = silent.Complete(silent.TryBegin("theme A")!, null);
        var refusing = new LiveSendGate();
        var rejected = refusing.Complete(refusing.TryBegin("theme A")!, "ERR LIVE folder is not valid base64");
        Assert.NotNull(noAnswer);
        Assert.NotNull(rejected);
        Assert.NotEqual(noAnswer, rejected);
        Assert.Contains("did not answer", noAnswer);
        Assert.Contains("LIVE folder is not valid base64", rejected);   // the runner's reason is shown
    }

    [Fact]
    public void A_rejection_suggests_restarting_the_runner_after_an_update()   // L2
    {
        var gate = new LiveSendGate();
        var text = gate.Complete(gate.TryBegin("theme A")!, "ERR The input is not a valid Base-64 string");
        Assert.Contains("restart the Glance tray app", text);   // runner-service: the runner is now the (non-elevated) tray app
    }

    [Fact]
    public void The_same_failure_again_does_not_rewrite_the_status()
    {
        var gate = new LiveSendGate();
        Assert.NotNull(gate.Complete(gate.TryBegin("theme A")!, null));
        Assert.Null(gate.Complete(gate.TryBegin("theme A")!, null));   // retried and failed the same way: nothing new to say
    }

    [Fact]
    public void A_send_from_before_a_restart_is_ignored()
    {
        var gate = new LiveSendGate();
        var old = gate.TryBegin("theme A")!;
        gate.Reset();                                      // live sending was stopped and started again
        var fresh = gate.TryBegin("theme A");
        Assert.NotNull(fresh);                             // not blocked by the old send
        Assert.Null(gate.Complete(old, null));             // its late failure says nothing about the new session
        Assert.Null(gate.TryBegin("theme B"));             // and does not end the new send early
        Assert.Null(gate.Complete(fresh!, "OK"));
    }
}

public class ControlPipeClientTests
{
    private static string TestPipe() => "glance-test-control-" + Guid.NewGuid().ToString("N");   // never the real pipe name

    /// <summary>A fake runner that answers one command after <paramref name="delayMs"/>. The pipe already exists when this is called.</summary>
    private static async Task<string?> SlowRunner(NamedPipeServerStream server, int delayMs, string reply)
    {
        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(10));
        string? line = null;
        try
        {
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            line = await reader.ReadLineAsync();
            await Task.Delay(delayMs);
            await writer.WriteLineAsync(reply);
            // like the runner's Handle loop: keep the pipe open until the client hangs up (closing early can discard the unread reply)
            await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (IOException) { /* the client gave up */ }
        return line;
    }

    [Fact]
    public async Task A_live_reply_that_takes_longer_than_connecting_is_still_received()   // L1
    {
        // the runner can hold its lock for about 2 s while it reopens the display; it still applies the edits afterwards
        var name = TestPipe();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var runner = SlowRunner(server, 2600, "OK");
        var command = ControlCommand.Live("{}", null);
        var watch = Stopwatch.StartNew();
        var reply = await Task.Run(() => ControlPipeClient.Send(name, command));
        Assert.True(reply == "OK", $"reply {(reply is null ? "null" : $"'{reply}'")} after {watch.ElapsedMilliseconds} ms");
        Assert.Equal(command.Encode(), await runner);
    }

    [Theory]
    [InlineData("STATUS")]
    [InlineData("LIVE")]
    public async Task The_runners_reply_reaches_the_caller(string verb)   // found while fixing L1: every reply used to come back as null
    {
        var name = TestPipe();
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var runner = SlowRunner(server, 0, "OK page 1/1");
        var command = verb == "LIVE" ? ControlCommand.Live("{}", null) : ControlCommand.Status;
        var reply = await Task.Run(() => ControlPipeClient.Send(name, command));
        Assert.Equal("OK page 1/1", reply);
        Assert.Equal(command.Encode(), await runner);
    }

    [Fact]
    public void No_runner_means_no_reply_and_no_long_wait()
    {
        var watch = Stopwatch.StartNew();
        Assert.Null(ControlPipeClient.Send(TestPipe(), ControlCommand.Status));
        Assert.True(watch.ElapsedMilliseconds < 3000, $"took {watch.ElapsedMilliseconds} ms");
    }
}

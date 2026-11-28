using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using Glance.Core.Control;
using Glance.Core.Display;
using Glance.Core.Sensors;
using Glance.Core.Service;
using Glance.Core.Themes;

namespace Glance.Tests;

/// <summary>
/// Like the UI thread of WinForms (the tray app) and Avalonia (the editor): work posted to this context runs only when its
/// own thread pumps messages, so a thread that blocks on a task whose continuation was posted here never wakes up.
/// xUnit runs tests without any such context, which is how the tray app's deadlock with Glance 0.2.0 got past the tests.
/// </summary>
internal sealed class UiLikeContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Work, object? State)> _queue = new();
    private int _posted;

    /// <summary>How many callbacks library code posted back to this "UI thread". Glance.Core should never post any.</summary>
    public int Posted => Volatile.Read(ref _posted);

    public override void Post(SendOrPostCallback d, object? state) { Interlocked.Increment(ref _posted); _queue.Add((d, state)); }
    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("A UI context cannot Send from its own blocked thread.");
    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Runs what was posted, on the calling thread: frees a "UI thread" that a test found deadlocked, so it can end.</summary>
    public void Drain() { while (_queue.TryTake(out var w)) w.Work(w.State); }

    public sealed record Outcome(bool Finished, Exception? Error, UiLikeContext Context, TimeSpan Took);

    /// <summary>
    /// Runs <paramref name="body"/> on a new thread whose current context is a <see cref="UiLikeContext"/> that is never pumped
    /// (the thread is busy running <paramref name="body"/>, like a UI thread stuck in a constructor). Waits at most
    /// <paramref name="watchdog"/>; a thread that is still blocked then is freed by draining its queue, so no test leaves it hanging.
    /// </summary>
    public static Outcome Run(Action body, TimeSpan watchdog)
    {
        var ctx = new UiLikeContext();
        Exception? error = null;
        var sw = Stopwatch.StartNew();
        var t = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(ctx);
            try { body(); } catch (Exception ex) { error = ex; }
        }) { IsBackground = true, Name = "UI-like thread" };
        t.SetApartmentState(ApartmentState.STA);   // like the tray app's and the editor's UI thread
        t.Start();
        bool finished = t.Join(watchdog);
        var took = sw.Elapsed;
        if (!finished)
        {
            // deadlocked: run the posted continuations elsewhere until the thread gets out, so it does not outlive the test
            var until = Stopwatch.StartNew();
            while (!t.Join(50) && until.Elapsed < TimeSpan.FromSeconds(20)) ctx.Drain();
        }
        return new Outcome(finished, error, ctx, took);
    }
}

/// <summary>
/// Bug: Glance 0.2.0's tray app never showed anything. Its constructor opened the display on the WinForms UI thread, and
/// <c>RemoteFrameDisplay.Request</c> blocked on async pipe I/O whose continuations (no <c>ConfigureAwait(false)</c>) were posted
/// back to that same blocked thread: a permanent deadlock, which the reply timeout could not break either. The editor's
/// "Send to display" opens the display on Avalonia's UI thread the same way. These tests run the real client code on a
/// UI-like thread against servers on random pipe names (never the real ones).
/// </summary>
public sealed class UiThreadDeadlockTests : IDisposable
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly int ThisSession = Process.GetCurrentProcess().SessionId;
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(8);

    private readonly string _pipe = "glance-test-deadlock-" + Guid.NewGuid().ToString("N");
    private readonly List<IDisposable> _cleanup = new();
    private readonly List<FakeDisplay> _devices = new();

    public void Dispose() { foreach (var d in Enumerable.Reverse(_cleanup)) { try { d.Dispose(); } catch { } } }

    private DisplayServer StartServer()
    {
        var server = new DisplayServer(() => { var d = new FakeDisplay(480, 320); lock (_devices) _devices.Add(d); return d; }, new DisplayServerOptions
        {
            PipeName = _pipe,
            Security = ServicePipeSecurity.Build(Me),
            ClientSession = _ => ThisSession,
            ConsoleSession = () => ThisSession,
        });
        server.Start();
        _cleanup.Add(server);
        return server;
    }

    /// <summary>A pipe server that accepts and reads requests but never answers (a hung service).</summary>
    private void StartSilentServer(PipeOptions options = PipeOptions.Asynchronous)
    {
        var started = new ManualResetEventSlim();
        var stop = new CancellationTokenSource();
        _cleanup.Add(stop);
        _ = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, options | PipeOptions.Asynchronous);
            started.Set();
            try
            {
                await server.WaitForConnectionAsync(stop.Token);
                var buf = new byte[64 * 1024];
                while (await server.ReadAsync(buf, stop.Token) > 0) { /* swallow, never reply */ }
            }
            catch { /* the test is over */ }
        });
        started.Wait(5000);
    }

    private static void AssertNoDeadlock(UiLikeContext.Outcome o, string what)
    {
        Assert.True(o.Finished, $"{what} deadlocked on a UI-like thread: still blocked after {Watchdog.TotalSeconds:0} s ({o.Context.Posted} callbacks were posted back to the blocked UI thread)");
    }

    [Fact]
    public void Connecting_to_the_service_on_a_UI_thread_returns_instead_of_deadlocking()
    {
        StartServer();
        RemoteFrameDisplay? display = null;
        var o = UiLikeContext.Run(() => display = RemoteFrameDisplay.Connect(_pipe, TestPipes.Own, connectTimeoutMs: 3000), Watchdog);
        if (display is not null) _cleanup.Add(display);

        AssertNoDeadlock(o, "RemoteFrameDisplay.Connect (Hello)");
        Assert.Null(o.Error);
        Assert.NotNull(display);
        Assert.Equal(0, o.Context.Posted);   // nothing in Glance.Core needs the caller's UI thread
    }

    [Fact]
    public void The_tray_apps_first_open_on_its_UI_thread_reaches_the_display()
    {
        // what RunnerContext's constructor did: new DisplayPusher(...) -> DisplayConnector.Open() -> Connect + SetBrightness
        StartServer();
        DisplayPusher? pusher = null;
        var o = UiLikeContext.Run(() => pusher = new DisplayPusher(() => Theme.CreateDefault(null), new FakeSource(Array.Empty<SensorInfo>()), 4,
            DisplayConnector.Open(_pipe, TestPipes.Own, () => throw new InvalidOperationException("must not fall back to USB"))), Watchdog);

        AssertNoDeadlock(o, "Opening the display through the service");
        Assert.Null(o.Error);
        Assert.NotNull(pusher);
        try
        {
            Assert.True(SpinWait.SpinUntil(() => { lock (_devices) return _devices.Count == 1 && _devices[0].Blits > 0; }, 10_000), "no frame reached the display");
            Assert.True(pusher!.ViaService);
        }
        finally { pusher?.Stop(blank: true, TimeSpan.FromSeconds(5)); }
        Assert.Equal(0, o.Context.Posted);
    }

    [Fact]
    public void Requests_made_on_a_UI_thread_after_connecting_also_return()
    {
        // the editor's brightness slider and "turn the display off" run on its UI thread with a connection opened earlier
        StartServer();
        var display = RemoteFrameDisplay.Connect(_pipe, TestPipes.Own, connectTimeoutMs: 3000);
        _cleanup.Add(display);
        var o = UiLikeContext.Run(() =>
        {
            display.SetBrightness(3);
            display.Blit(0, 0, 480, 320, new byte[DisplayWire.FrameBytes]);
            display.BlankOrThrow();
        }, Watchdog);

        AssertNoDeadlock(o, "SetBrightness/Blit/Blank");
        Assert.Null(o.Error);
        Assert.Equal(0, o.Context.Posted);
    }

    [Fact]
    public void The_service_status_query_returns_on_a_UI_thread()
    {
        StartServer();
        string? status = null;
        var o = UiLikeContext.Run(() => status = RemoteFrameDisplay.QueryStatus(_pipe, TestPipes.Own, 3000), Watchdog);
        AssertNoDeadlock(o, "RemoteFrameDisplay.QueryStatus");
        Assert.NotNull(status);
    }

    [Fact]
    public void A_service_that_never_answers_times_out_on_a_UI_thread_instead_of_hanging_for_ever()
    {
        StartSilentServer();
        var o = UiLikeContext.Run(() => RemoteFrameDisplay.Connect(_pipe, TestPipes.Own, connectTimeoutMs: 3000, replyTimeout: TimeSpan.FromSeconds(1)), Watchdog);

        AssertNoDeadlock(o, "The reply timeout");
        var io = Assert.IsAssignableFrom<IOException>(o.Error);
        Assert.Contains("did not answer in time", io.Message);
        Assert.True(o.Took < TimeSpan.FromSeconds(5), $"took {o.Took.TotalSeconds:0.0} s for a 1 s reply timeout");
    }

    [Fact]
    public void A_service_that_never_answers_times_out_off_the_UI_thread_too()
    {
        StartSilentServer();
        var sw = Stopwatch.StartNew();
        var ex = Assert.ThrowsAny<IOException>(() => RemoteFrameDisplay.Connect(_pipe, TestPipes.Own, connectTimeoutMs: 3000, replyTimeout: TimeSpan.FromSeconds(1)));
        Assert.Contains("did not answer in time", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed.TotalSeconds:0.0} s for a 1 s reply timeout");
    }

    [Fact]
    public void The_editors_tray_command_returns_on_its_UI_thread_when_the_tray_app_never_answers()
    {
        // ControlPipeClient.Send waits for the reply with a timeout; the editor calls some commands from its UI thread
        StartSilentServer(PipeOptions.CurrentUserOnly);
        string? reply = "not called";
        var o = UiLikeContext.Run(() => reply = ControlPipeClient.Send(_pipe, ControlCommand.Status), Watchdog);

        AssertNoDeadlock(o, "ControlPipeClient.Send");
        Assert.Null(o.Error);
        Assert.Null(reply);
        Assert.True(o.Took < TimeSpan.FromSeconds(4), $"took {o.Took.TotalSeconds:0.0} s for a {ControlPipeClient.ReplyTimeoutMs} ms reply timeout");
    }

    [Fact]
    public void The_editors_tray_command_gets_its_reply_on_its_UI_thread()
    {
        var stop = new CancellationTokenSource();
        _cleanup.Add(stop);
        var started = new ManualResetEventSlim();
        _ = Task.Run(async () =>
        {
            using var server = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            started.Set();
            await server.WaitForConnectionAsync(stop.Token);
            using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
            var line = await reader.ReadLineAsync(stop.Token);
            await Task.Delay(200, stop.Token);   // the reply arrives after the client started waiting
            var bytes = new UTF8Encoding(false).GetBytes("OK " + line + "\n");
            await server.WriteAsync(bytes, stop.Token);
            await server.FlushAsync(stop.Token);
            try { await Task.Delay(2000, stop.Token); } catch { }
        });
        started.Wait(5000);

        string? reply = null;
        var o = UiLikeContext.Run(() => reply = ControlPipeClient.Send(_pipe, ControlCommand.Status), Watchdog);
        AssertNoDeadlock(o, "ControlPipeClient.Send");
        Assert.Equal("OK STATUS", reply);
    }
}

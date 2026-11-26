using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Glance.Core.Display;
using Glance.Core.Sensors;
using Glance.Core.Service;
using Glance.Core.Themes;

namespace Glance.Tests;

/// <summary>
/// The service's display server and the user-side client, over a real named pipe with a fake device. The server runs as the
/// test user here, so the tests trust that user as the pipe's owner where the product trusts SYSTEM or Administrators.
/// Pipe names are random, never the real ones, so a Glance service on this PC is never touched.
/// </summary>
public sealed class DisplayServiceTests : IDisposable
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly int ThisSession = Process.GetCurrentProcess().SessionId;
    private static readonly PusherTiming Quick = new(FrameIntervalMs: 20, ErrorBackoffMs: 20, NoPagesReportMs: 100);

    private readonly string _pipe = "glance-test-display-" + Guid.NewGuid().ToString("N");
    private readonly List<FakeDisplay> _devices = new();
    private readonly List<string> _log = new();
    private readonly List<IDisposable> _cleanup = new();
    private int? _clientSession = ThisSession, _console = ThisSession;
    private long _now;

    private Func<FakeDisplay>? NextDevice;

    private FakeDisplay OpenFake()
    {
        var d = NextDevice?.Invoke() ?? new FakeDisplay(480, 320);
        lock (_devices) _devices.Add(d);
        return d;
    }

    private int Opens { get { lock (_devices) return _devices.Count; } }
    private FakeDisplay Device(int i) { lock (_devices) return _devices[i]; }

    private DisplayServer StartServer(TimeSpan? idle = null)
    {
        var server = new DisplayServer(OpenFake, new DisplayServerOptions
        {
            PipeName = _pipe,
            Security = ServicePipeSecurity.Build(Me),
            ClientSession = _ => _clientSession,
            ConsoleSession = () => _console,
            NowMs = () => Interlocked.Read(ref _now),
            IdleBlankAfter = idle ?? TimeSpan.FromSeconds(20),
            Log = m => { lock (_log) _log.Add(m); },
        });
        server.Start();
        _cleanup.Add(server);
        return server;
    }

    private RemoteFrameDisplay Connect()
    {
        var c = RemoteFrameDisplay.Connect(_pipe, new[] { Me }, connectTimeoutMs: 3000);
        _cleanup.Add(c);
        return c;
    }

    private static byte[] Frame(byte v) => Enumerable.Repeat(v, DisplayWire.FrameBytes).ToArray();

    /// <summary>One raw request on a fresh connection, for requests the client class never sends.</summary>
    private async Task<(ReplyStatus Status, string Message, bool ClosedAfter)> Raw(byte[] request)
    {
        using var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000);
        await pipe.WriteAsync(request);
        using var cts = new CancellationTokenSource(5000);
        var reply = await DisplayWire.ReadReplyAsync(pipe, cts.Token);
        // closed after the reply? A connection that stays open just has nothing more to say within a second.
        var buf = new byte[1];
        using var probe = new CancellationTokenSource(1000);
        bool closed;
        try { closed = await pipe.ReadAsync(buf, probe.Token) == 0; }
        catch (IOException) { closed = true; }
        catch (OperationCanceledException) { closed = false; }
        return (reply.Status, reply.Message, closed);
    }

    public void Dispose() { foreach (var d in Enumerable.Reverse(_cleanup)) { try { d.Dispose(); } catch { } } }

    // ---- frames and access ----------------------------------------------------------------------------------------

    [Fact]
    public void Frames_from_the_console_session_reach_the_device_with_the_requested_brightness()
    {
        var server = StartServer();
        var c = Connect();
        c.SetBrightness(5);
        Assert.Equal(0, Opens);   // brightness alone does not claim the device
        c.Blit(0, 0, 480, 320, Frame(1));

        Assert.Equal(1, Opens);
        Assert.Equal(1, Device(0).Blits);
        Assert.Equal(new[] { "brightness 5", "blit" }, Device(0).Calls);
        Assert.True(server.DeviceOpen);
        Assert.Equal(ThisSession, server.DrivingSession);

        c.SetBrightness(2);
        Assert.Equal("brightness 2", Device(0).Calls.Last());
    }

    [Fact]
    public void Another_session_is_refused_with_a_reason_and_never_opens_the_device()
    {
        StartServer();
        _clientSession = ThisSession + 1;
        var ex = Assert.Throws<DisplayServiceException>(() => Connect());
        Assert.True(ex.Denied);
        Assert.Contains("console", ex.Message);
        Assert.Equal(0, Opens);
    }

    [Fact]
    public async Task The_status_is_answered_for_any_session()
    {
        StartServer();
        _clientSession = ThisSession + 1;
        var (status, message, _) = await Raw(DisplayWire.EncodeRequest(DisplayOp.Status, ReadOnlySpan<byte>.Empty));
        Assert.Equal(ReplyStatus.Ok, status);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }

    [Fact]
    public async Task A_frame_of_the_wrong_size_is_refused_and_the_connection_closed()
    {
        StartServer();
        var (status, _, closed) = await Raw(new byte[] { (byte)DisplayOp.Frame, 0x80, 0x96, 0x98, 0x00 });   // 10 MB announced
        Assert.Equal(ReplyStatus.Error, status);
        Assert.True(closed);
        Assert.Equal(0, Opens);
    }

    [Fact]
    public void A_frame_that_is_not_the_whole_screen_is_refused_by_the_client()
    {
        StartServer();
        var c = Connect();
        Assert.Throws<ArgumentException>(() => c.Blit(0, 0, 96, 64, new byte[96 * 64 * 2]));
    }

    [Fact]
    public void A_squatted_display_pipe_is_not_used()
    {
        StartServer();   // served by the test user, not by SYSTEM or Administrators
        Assert.Throws<UnauthorizedAccessException>(() => RemoteFrameDisplay.Connect(_pipe, trustedOwners: null, connectTimeoutMs: 3000));
    }

    [Fact]
    public void A_service_that_does_not_answer_is_given_up_on()
    {
        using var silent = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _ = silent.WaitForConnectionAsync();
        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<IOException>(() => RemoteFrameDisplay.Connect(_pipe, new[] { Me }, 3000, TimeSpan.FromMilliseconds(500)));
        Assert.True(sw.ElapsedMilliseconds < 4000, $"took {sw.ElapsedMilliseconds} ms");
    }

    // ---- releasing the device ---------------------------------------------------------------------------------------

    [Fact]
    public void Blank_turns_the_display_black_and_releases_it_and_the_next_frame_reopens_it()
    {
        var server = StartServer();
        var c = Connect();
        c.Blit(0, 0, 480, 320, Frame(1));
        c.Blank();
        Assert.Equal(1, Device(0).Blanks);
        Assert.Equal(1, Device(0).Disposes);
        Assert.False(server.DeviceOpen);

        c.Blit(0, 0, 480, 320, Frame(2));
        Assert.Equal(2, Opens);
        Assert.Equal(1, Device(1).Blits);
    }

    [Fact]
    public void An_idle_client_is_blanked_after_the_idle_time()
    {
        var server = StartServer(TimeSpan.FromSeconds(20));
        Connect().Blit(0, 0, 480, 320, Frame(1));
        Interlocked.Exchange(ref _now, 19_000);
        server.Tick();
        Assert.True(server.DeviceOpen);
        Interlocked.Exchange(ref _now, 21_000);
        server.Tick();
        Assert.False(server.DeviceOpen);
        Assert.Equal(1, Device(0).Blanks);
        Assert.Equal(1, Device(0).Disposes);
        Assert.Null(server.DrivingSession);
    }

    [Fact]
    public void A_usb_failure_is_reported_to_the_client_and_the_next_frame_reopens_the_device()
    {
        StartServer();
        var c = Connect();
        NextDevice = () => new FakeDisplay(480, 320) { BlitError = new IOException("usb broke") };
        var ex = Assert.ThrowsAny<IOException>(() => c.Blit(0, 0, 480, 320, Frame(1)));
        Assert.Contains("usb broke", ex.Message);
        Assert.Equal(1, Device(0).Disposes);

        NextDevice = null;
        c.Blit(0, 0, 480, 320, Frame(2));
        Assert.Equal(2, Opens);
        Assert.Equal(1, Device(1).Blits);
    }

    // ---- power and sessions -----------------------------------------------------------------------------------------

    [Fact]
    public void Sleep_blanks_and_refuses_frames_until_wake()
    {
        var server = StartServer();
        var c = Connect();
        c.Blit(0, 0, 480, 320, Frame(1));

        Assert.Equal(PowerBlank.Result.Blanked, server.HandlePower(PowerEvent.Suspend));
        Assert.Equal(1, Device(0).Blanks);
        Assert.False(server.DeviceOpen);

        var refused = Assert.Throws<DisplayServiceException>(() => c.Blit(0, 0, 480, 320, Frame(2)));
        Assert.False(refused.Denied);
        Assert.Contains("sleep", refused.Message);
        Assert.Equal(1, Opens);

        server.HandlePower(PowerEvent.Resume);
        c.Blit(0, 0, 480, 320, Frame(3));
        Assert.Equal(2, Opens);
    }

    [Fact]
    public void Shutdown_blanks_once_and_the_display_is_never_reopened()
    {
        var server = StartServer();
        var c = Connect();
        c.Blit(0, 0, 480, 320, Frame(1));

        Assert.Equal(PowerBlank.Result.Blanked, server.HandlePower(PowerEvent.SessionEnding));
        Assert.Throws<DisplayServiceException>(() => c.Blit(0, 0, 480, 320, Frame(2)));
        Assert.NotEqual(PowerBlank.Result.Blanked, server.HandlePower(PowerEvent.Quit));
        server.Dispose();
        Assert.Equal(1, Opens);
        Assert.Equal(1, Device(0).Blanks);
    }

    [Fact]
    public void Stopping_the_service_blanks_the_display()
    {
        var server = StartServer();
        Connect().Blit(0, 0, 480, 320, Frame(1));
        server.Dispose();
        Assert.Equal(1, Device(0).Blanks);
        Assert.Equal(1, Device(0).Disposes);
    }

    [Fact]
    public void Signing_out_of_the_driving_session_blanks_at_once_and_other_sessions_do_not()
    {
        var server = StartServer();
        Connect().Blit(0, 0, 480, 320, Frame(1));
        server.SessionLeft(ThisSession + 5);
        Assert.True(server.DeviceOpen);
        server.SessionLeft(ThisSession);
        Assert.False(server.DeviceOpen);
        Assert.Equal(1, Device(0).Blanks);
    }

    [Fact]
    public async Task A_power_event_that_finds_the_device_busy_is_paid_by_the_next_tick()
    {
        var server = StartServer();
        var c = Connect();
        c.Blit(0, 0, 480, 320, Frame(1));
        Device(0).BlitDelayMs = 2500;   // a stalled transfer holds the device lock
        Device(0).BlitStarted.Reset();
        var stuck = Task.Run(() => { try { c.Blit(0, 0, 480, 320, Frame(2)); } catch { } });
        Assert.True(Device(0).BlitStarted.Wait(3000));

        Assert.Equal(PowerBlank.Result.Busy, server.HandlePower(PowerEvent.Suspend));
        await stuck;
        server.Tick();
        Assert.False(server.DeviceOpen);
        Assert.Equal(1, Device(0).Blanks);
    }

    // ---- the user side: DisplayPusher through the service, and the connector ----------------------------------------

    [Fact]
    public async Task The_pusher_draws_through_the_service_and_its_stop_blanks_the_display()
    {
        var server = StartServer();
        var theme = Theme.CreateDefault(null);
        var p = new DisplayPusher(() => theme, new FakeSource(Fake.Typical()), 6, Connect(), Quick);
        var until = Environment.TickCount64 + 8000;
        while (Opens == 0 || Device(0).Blits == 0) { Assert.True(Environment.TickCount64 < until, "no frame reached the device"); await Task.Delay(20); }
        Assert.Null(p.LastError);

        Assert.True(p.Stop(blank: true, TimeSpan.FromSeconds(5)));
        Assert.Equal(1, Device(0).Blanks);
        Assert.False(server.DeviceOpen);
        Assert.Contains("brightness 6", Device(0).Calls);
    }

    [Fact]
    public void The_connector_uses_the_service_when_it_answers()
    {
        StartServer();
        int usb = 0;
        using var d = DisplayConnector.Open(_pipe, new[] { Me }, () => { usb++; return new FakeDisplay(480, 320); });
        Assert.IsType<RemoteFrameDisplay>(d);
        Assert.Equal(0, usb);
    }

    [Fact]
    public void The_connector_opens_usb_directly_without_a_service_or_with_a_squatter()
    {
        int usb = 0;
        using (var d = DisplayConnector.Open(_pipe, new[] { Me }, () => { usb++; return new FakeDisplay(480, 320); }))
            Assert.IsType<FakeDisplay>(d);
        Assert.Equal(1, usb);

        StartServer();   // served by the test user: with the product's trust rule it is a squatter
        using (var d = DisplayConnector.Open(_pipe, null, () => { usb++; return new FakeDisplay(480, 320); }))
            Assert.IsType<FakeDisplay>(d);
        Assert.Equal(2, usb);
    }

    [Fact]
    public void The_connector_does_not_go_around_a_service_that_refuses()
    {
        StartServer();
        _clientSession = ThisSession + 1;
        int usb = 0;
        var ex = Assert.Throws<DisplayServiceException>(() => DisplayConnector.Open(_pipe, new[] { Me }, () => { usb++; return new FakeDisplay(480, 320); }));
        Assert.True(ex.Denied);
        Assert.Equal(0, usb);
    }

    [Fact]
    public async Task Windows_reports_the_session_of_a_local_pipe_client()
    {
        using var server = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var wait = server.WaitForConnectionAsync();
        using var client = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut);
        await client.ConnectAsync(3000);
        await wait;
        Assert.Equal(ThisSession, NativeSessions.ClientSessionOf(server));
    }

    // ---- the sensor feed --------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_sensor_client_ignores_a_feed_that_is_not_served_by_the_service()
    {
        var name = "glance-test-" + Guid.NewGuid().ToString("N");
        using var server = new SensorPipeServer(name, ServicePipeSecurity.Build(Me));
        server.Start();
        server.Publish(new[] { new SensorInfo("/a", "hw", "A", "Load", 1, "%") });

        using var squatted = new PipeSensorClient(name, staleAfter: TimeSpan.FromSeconds(2));   // trusts only SYSTEM/Administrators
        using var trusting = new PipeSensorClient(name, staleAfter: TimeSpan.FromSeconds(2), trustedOwners: new[] { Me });
        var until = Environment.TickCount64 + 6000;
        while (!trusting.HasData && Environment.TickCount64 < until) await Task.Delay(50);
        Assert.True(trusting.HasData, "the trusting client never got data, so the test proves nothing");
        await Task.Delay(1500);
        Assert.False(squatted.HasData);
        Assert.Empty(squatted.Snapshot);
    }
}

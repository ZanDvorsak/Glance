using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using Glance.Core.Display;
using Glance.Core.Sensors;
using Glance.Core.Service;

namespace Glance.Tests;

/// <summary>
/// Fixes after the security review of the service (docs/reviews/review-runner-service.md). Each test is named after its finding.
/// Same rules as the rest of this class: random pipe names, fake device, fake sessions, the test user trusted as the owner.
/// </summary>
public sealed partial class DisplayServiceTests
{
    private static async Task<bool> Eventually(Func<bool> condition, int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until) { if (condition()) return true; await Task.Delay(50); }
        return condition();
    }

    // ---- M1: a squatter's pipe is refused, not joined ----------------------------------------------------------------

    [Fact]
    public void M1_the_display_server_does_not_start_on_a_squatted_name()
    {
        // a normal program with .NET defaults: its DACL gives SYSTEM (and here the same user) full control, so a server without the
        // first-instance flag would quietly join it and serve under the squatter's rules
        using var squatter = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 254, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var server = new DisplayServer(OpenFake, new DisplayServerOptions { PipeName = _pipe, Security = ServicePipeSecurity.Build(Me), ClientSession = _ => ThisSession, ConsoleSession = () => ThisSession });
        _cleanup.Add(server);
        Assert.ThrowsAny<IOException>(() => server.Start());
    }

    [Fact]
    public async Task M1_the_sensor_server_reports_a_squatted_name_and_serves_once_it_is_gone()
    {
        var name = "glance-test-" + Guid.NewGuid().ToString("N");
        var log = new List<string>();
        var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 254, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var server = new SensorPipeServer(name, ServicePipeSecurity.Build(Me), m => { lock (log) log.Add(m); });
        server.Start();
        server.Publish(new[] { new SensorInfo("/a", "hw", "A", "Load", 1, "%") });

        Assert.True(await Eventually(() => { lock (log) return log.Any(l => l.Contains("taken")); }, 4000), "the squatted sensor pipe was not reported");
        squatter.Dispose();
        using var client = new PipeSensorClient(name, staleAfter: TimeSpan.FromSeconds(3), trustedOwners: new[] { Me });
        Assert.True(await Eventually(() => client.HasData, 8000), "the sensor pipe was not served after the squatter left");
    }

    [Fact]
    public void M1_L8_a_pipe_name_someone_else_serves_is_refused_by_every_Glance_server()
    {
        using var squatter = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 254, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        // the tray's control pipe uses the same factory with its current-user rules
        var ex = Record.Exception(() => ServicePipeFactory.Create(_pipe, PipeDirection.InOut, 4, ServicePipeSecurity.CurrentUserOnly(), first: true).Dispose());
        Assert.IsType<PipeNameTakenException>(ex);
    }

    // ---- M2: idle connections and log growth -------------------------------------------------------------------------

    [Fact]
    public async Task M2_idle_connections_from_another_session_do_not_lock_out_the_console_user()
    {
        StartServer();
        _clientSession = ThisSession + 1;   // another signed-in user opens 8 connections and sends nothing
        var idle = new List<NamedPipeClientStream>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var c = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await c.ConnectAsync(500); } catch (TimeoutException) { c.Dispose(); break; }
                idle.Add(c);
                await Task.Delay(30);   // let the server see who it is before the next one
            }
            _clientSession = ThisSession;
            var sw = Stopwatch.StartNew();
            var display = RemoteFrameDisplay.Connect(_pipe, new[] { Me }, connectTimeoutMs: 2000);
            _cleanup.Add(display);
            display.Blit(0, 0, 480, 320, Frame(1));
            Assert.True(sw.ElapsedMilliseconds < 2500, $"the console user waited {sw.ElapsedMilliseconds} ms");
        }
        finally { foreach (var c in idle) c.Dispose(); }
    }

    [Fact]
    public async Task M2_one_session_cannot_hold_more_than_its_share_of_connections()
    {
        StartServer();
        _clientSession = ThisSession + 1;
        var held = new List<NamedPipeClientStream>();
        try
        {
            for (int i = 0; i < 5; i++)
            {
                var c = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                await c.ConnectAsync(3000);
                held.Add(c);
                await Task.Delay(50);
            }
            async Task<bool> ClosedSoon(NamedPipeClientStream c)
            {
                using var cts = new CancellationTokenSource(1500);
                try { return await c.ReadAsync(new byte[1], cts.Token) == 0; }
                catch (IOException) { return true; }
                catch (OperationCanceledException) { return false; }
            }
            Assert.True(await ClosedSoon(held[4]), "the fifth connection from one session was kept");
            Assert.False(await ClosedSoon(held[0]), "the first connection was closed too");
        }
        finally { foreach (var c in held) c.Dispose(); }
    }

    [Fact]
    public async Task M2_a_connection_that_sends_nothing_is_closed()
    {
        StartServer(idleTimeout: TimeSpan.FromMilliseconds(300));
        using var c = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await c.ConnectAsync(3000);
        using var cts = new CancellationTokenSource(3000);
        int n;
        try { n = await c.ReadAsync(new byte[1], cts.Token); } catch (IOException) { n = 0; } catch (OperationCanceledException) { n = -1; }
        Assert.Equal(0, n);   // closed by the server; -1 would mean it was still open after 3 s
    }

    [Fact]
    public async Task M2_the_display_server_does_not_log_every_busy_second()
    {
        StartServer();
        // the server's accept loop failing over and over must not write a line each time
        var limiter = new LogLimiter(TimeSpan.FromMinutes(1), () => 0);
        int written = 0;
        for (int i = 0; i < 100; i++) if (limiter.Filter("Display pipe error: All pipe instances are busy.") is not null) written++;
        Assert.Equal(1, written);
        await Task.CompletedTask;
    }

    // ---- L1: clients over the network path ---------------------------------------------------------------------------

    [Fact]
    public async Task L1_a_client_over_the_network_path_is_refused()
    {
        StartServer();   // the fake session lookup would even call it the console session
        using var c = new NamedPipeClientStream("localhost", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        try { await c.ConnectAsync(3000); }
        catch (Exception) { return; }   // no SMB loopback on this machine (for example CI): nothing to prove here
        try { await c.WriteAsync(DisplayWire.EncodeRequest(DisplayOp.Hello, BitConverter.GetBytes(DisplayWire.Version))); }
        catch (IOException) { Assert.Equal(0, Opens); return; }   // closed before it could even ask: refused
        using var cts = new CancellationTokenSource(3000);
        ReplyStatus status;
        try { status = (await DisplayWire.ReadReplyAsync(c, cts.Token)).Status; }
        catch (InvalidDataException) { status = ReplyStatus.Denied; }   // closed without an answer is a refusal too
        catch (IOException) { status = ReplyStatus.Denied; }
        Assert.Equal(ReplyStatus.Denied, status);
        Assert.Equal(0, Opens);
    }

    // ---- L2: suspend while a frame is being sent --------------------------------------------------------------------

    [Fact]
    public async Task L2_a_suspend_during_a_transfer_blanks_as_soon_as_the_transfer_ends_without_a_tick()
    {
        var server = StartServer();
        var c = Connect();
        c.Blit(0, 0, 480, 320, Frame(1));
        Device(0).BlitDelayMs = 1500;
        Device(0).BlitStarted.Reset();
        var stuck = Task.Run(() => { try { c.Blit(0, 0, 480, 320, Frame(2)); } catch { } });
        Assert.True(Device(0).BlitStarted.Wait(3000));
        Assert.Equal(PowerBlank.Result.Busy, server.HandlePower(PowerEvent.Suspend));
        await stuck;
        // no Tick and no further request: Windows would be asleep before either
        Assert.Equal(1, Device(0).Blanks);
        Assert.False(server.DeviceOpen);
    }

    // ---- L3: the connector and a service that is busy or still starting ---------------------------------------------

    [Fact]
    public void L3_an_installed_service_that_does_not_answer_yet_is_waited_for_not_bypassed()
    {
        int usb = 0;
        var ex = Assert.ThrowsAny<IOException>(() => DisplayConnector.Open(_pipe, new[] { Me }, () => { usb++; return new FakeDisplay(480, 320); }, () => ServiceState.Running));
        Assert.Equal(0, usb);
        Assert.Contains("service", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ServiceState.NotInstalled)]
    [InlineData(ServiceState.Stopped)]
    public void L3_without_a_running_service_the_connector_opens_usb(ServiceState state)
    {
        int usb = 0;
        using var d = DisplayConnector.Open(_pipe, new[] { Me }, () => { usb++; return new FakeDisplay(480, 320); }, () => state);
        Assert.Equal(1, usb);
    }

    // ---- N5: what STATUS tells other users ---------------------------------------------------------------------------

    [Fact]
    public async Task N5_other_sessions_get_a_status_without_the_session_or_error_text()
    {
        StartServer();
        Connect().Blit(0, 0, 480, 320, Frame(1));
        _clientSession = ThisSession + 1;
        var (status, message, _) = await Raw(DisplayWire.EncodeRequest(DisplayOp.Status, ReadOnlySpan<byte>.Empty));
        Assert.Equal(ReplyStatus.Ok, status);
        Assert.DoesNotContain("session", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ThisSession.ToString(), message);
    }

    // ---- test gaps from the review's mutation check ------------------------------------------------------------------

    [Fact]
    public async Task T4_a_refused_or_wrong_hello_closes_the_connection()
    {
        StartServer();
        var wrongVersion = DisplayWire.EncodeRequest(DisplayOp.Hello, BitConverter.GetBytes(99));
        var (status, _, closed) = await Raw(wrongVersion);
        Assert.Equal(ReplyStatus.Error, status);
        Assert.True(closed, "a wrong HELLO left the connection open");

        _clientSession = ThisSession + 1;
        (status, _, closed) = await Raw(DisplayWire.EncodeRequest(DisplayOp.Hello, BitConverter.GetBytes(DisplayWire.Version)));
        Assert.Equal(ReplyStatus.Denied, status);
        Assert.True(closed, "a refused HELLO left the connection open");
    }

    [Fact]
    public async Task R3_brightness_out_of_range_is_clamped_by_the_service()
    {
        StartServer();
        Connect().Blit(0, 0, 480, 320, Frame(1));
        await Raw(DisplayWire.EncodeRequest(DisplayOp.Brightness, new byte[] { 200 }));
        Assert.Equal("brightness 7", Device(0).Calls.Last());
    }
}

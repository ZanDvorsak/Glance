using System.Diagnostics;
using System.Security.Principal;
using Glance.Core.Display;
using Glance.Core.Service;
using Glance.Core.Sensors;
using Glance.Core.Themes;
using SkiaSharp;

namespace Glance.Tests;

/// <summary>
/// The audit after the Glance 0.2.0 deadlock (docs/design/fix-tray-deadlock.md): every other place where the editor or the
/// tray app waits synchronously on async or background work from its UI thread, run on a UI-like thread that is never pumped.
/// Also the service's logging of refusals and failed opens (the tray app's background open: TrayDisplayOpenerTests).
/// </summary>
public sealed class UiThreadAuditTests : IDisposable
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;
    private static readonly int ThisSession = Process.GetCurrentProcess().SessionId;
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(8);
    private static readonly PusherTiming Quick = new(FrameIntervalMs: 20, ErrorBackoffMs: 20, NoPagesReportMs: 100);

    private readonly string _pipe = "glance-test-audit-" + Guid.NewGuid().ToString("N");
    private readonly List<IDisposable> _cleanup = new();
    private readonly List<FakeDisplay> _devices = new();
    private readonly List<string> _log = new();
    private int? _clientSession = ThisSession;
    private Func<FakeDisplay> _device = () => new FakeDisplay(480, 320);

    public void Dispose() { foreach (var d in Enumerable.Reverse(_cleanup)) { try { d.Dispose(); } catch { } } }

    private int Opens { get { lock (_devices) return _devices.Count; } }
    private FakeDisplay Device(int i) { lock (_devices) return _devices[i]; }
    private string[] LogLines { get { lock (_log) return _log.ToArray(); } }

    private void StartServer()
    {
        var server = new DisplayServer(() => { var d = _device(); lock (_devices) _devices.Add(d); return d; }, new DisplayServerOptions
        {
            PipeName = _pipe,
            Security = ServicePipeSecurity.Build(Me),
            ClientSession = _ => _clientSession,
            ConsoleSession = () => ThisSession,
            Log = m => { lock (_log) _log.Add(m); },
        });
        server.Start();
        _cleanup.Add(server);
    }

    private DisplayPusher OpenThroughService() => new(() => Theme.CreateDefault(null), new FakeSource(Array.Empty<SensorInfo>()), 4,
        DisplayConnector.Open(_pipe, new[] { Me }, () => throw new InvalidOperationException("must not fall back to USB")), Quick);

    private static void AssertReturned(UiLikeContext.Outcome o, string what)
    {
        Assert.True(o.Finished, $"{what} deadlocked on a UI-like thread ({o.Context.Posted} callbacks posted back to it)");
        Assert.Null(o.Error);
        Assert.Equal(0, o.Context.Posted);
    }

    // ---- the editor: Send to display through the service (PusherSlot on Avalonia's UI thread) --------------------------

    [Fact]
    public void The_editors_Send_to_display_through_the_service_works_on_its_UI_thread()
    {
        // MainWindow.StartSending -> PusherSlot.TryOpen -> OpenPusher -> new DisplayPusher -> DisplayConnector.Open, all on the UI thread
        StartServer();
        var slot = new PusherSlot(new DisplayPowerPolicy(), OpenThroughService);
        bool opened = false; string? error = null;
        var o = UiLikeContext.Run(() => opened = slot.TryOpen(out error), Watchdog);
        try
        {
            AssertReturned(o, "PusherSlot.TryOpen through the service");
            Assert.True(opened, error);
            Assert.True(slot.Current!.ViaService);
            Assert.True(SpinWait.SpinUntil(() => Opens == 1 && Device(0).Blits > 0, 10_000), "no frame reached the display");
        }
        finally
        {
            // "Send to display" off: the editor releases (and blanks) on its UI thread too
            var off = UiLikeContext.Run(() => slot.Release(blank: true, DisplayPusher.StopTimeout), Watchdog);
            AssertReturned(off, "PusherSlot.Release (DisplayPusher.Stop)");
        }
        Assert.True(Device(0).Blanks >= 1, "the service never blanked the display");
    }

    [Fact]
    public void The_editors_restart_of_a_dead_loop_through_the_service_returns_on_its_UI_thread()
    {
        StartServer();
        var slot = new PusherSlot(new DisplayPowerPolicy(), OpenThroughService);
        Assert.True(slot.TryOpen(out _));
        var dead = slot.Current!;
        dead.Stop(blank: false, TimeSpan.FromSeconds(5));   // a loop that ended on its own
        PusherSlot.RestartResult result = default;
        var o = UiLikeContext.Run(() => result = slot.RestartIfDead(out _, out _), Watchdog);
        try
        {
            AssertReturned(o, "PusherSlot.RestartIfDead");
            Assert.Equal(PusherSlot.RestartResult.Reopened, result);
        }
        finally { slot.Release(blank: false, TimeSpan.FromSeconds(5)); }
    }

    // ---- the other synchronous waits ---------------------------------------------------------------------------------

    [Fact]
    public void Starting_the_session_power_watcher_on_a_UI_thread_returns()
    {
        // SessionPowerWatcher's constructor waits (ready.Wait) for its own thread to create the hidden window
        SessionPowerWatcher? w = null;
        var o = UiLikeContext.Run(() => w = new SessionPowerWatcher(_ => { }), Watchdog);
        try { AssertReturned(o, "new SessionPowerWatcher"); Assert.NotEqual(IntPtr.Zero, w!.WindowHandle); }
        finally { w?.Dispose(); }
    }

    [Fact]
    public void The_renderers_file_checks_return_on_a_UI_thread()
    {
        // ThemeRenderer's FileCheck.Poll waits briefly (_pending.Wait(ms)) for a file stat it started on the thread pool;
        // the editor's preview renders on its UI thread
        var dir = Directory.CreateTempSubdirectory("glance-audit-");
        try
        {
            using (var bmp = new SKBitmap(8, 8))
            using (var data = bmp.Encode(SKEncodedImageFormat.Png, 100))
            using (var f = File.Create(Path.Combine(dir.FullName, "a.png"))) data.SaveTo(f);
            var theme = new Theme { Width = 64, Height = 64, BaseFolder = dir.FullName };
            theme.Widgets.Add(new Widget { Kind = WidgetKind.Image, X = 0, Y = 0, Width = 32, Height = 32, ImagePath = "a.png" });
            var o = UiLikeContext.Run(() =>
            {
                using var r = new ThemeRenderer();
                for (int i = 0; i < 3; i++) using (Fake.Render(theme, null, renderer: r)) { }
            }, Watchdog);
            AssertReturned(o, "ThemeRenderer.Render with an image");
        }
        finally { try { dir.Delete(true); } catch { } }
    }

    [Fact]
    public void Stopping_a_pusher_from_a_UI_thread_returns_and_blanks_through_the_service()
    {
        // DisplayPusher.Stop waits (_task.Wait(timeout)) for the loop, which blanks through the service from its own thread
        StartServer();
        var p = OpenThroughService();
        Assert.True(SpinWait.SpinUntil(() => Opens == 1 && Device(0).Blits > 0, 10_000));
        bool stopped = false;
        var o = UiLikeContext.Run(() => stopped = p.Stop(blank: true, DisplayPusher.QuickStopTimeout), Watchdog);
        AssertReturned(o, "DisplayPusher.Stop");
        Assert.True(stopped);
        Assert.True(Device(0).Blanks >= 1);
    }

    // ---- the service logs why a client gets nothing ------------------------------------------------------------------------

    [Fact]
    public void The_service_logs_a_refused_client_once_a_minute_not_on_every_request()
    {
        StartServer();
        _clientSession = ThisSession + 1;   // not the console session
        for (int i = 0; i < 3; i++) Assert.Throws<DisplayServiceException>(() => RemoteFrameDisplay.Connect(_pipe, new[] { Me }, 3000));
        var refused = LogLines.Where(l => l.StartsWith("Refused Hello from session " + (ThisSession + 1))).ToList();
        Assert.Single(refused);
        Assert.Contains("console", refused[0]);
    }

    [Fact]
    public void The_service_logs_when_it_cannot_open_the_USB_display()
    {
        _device = () => throw new IOException("No display found (USB 1908:0102).");
        StartServer();
        using var c = RemoteFrameDisplay.Connect(_pipe, new[] { Me }, 3000);
        for (int i = 0; i < 3; i++)
            Assert.Throws<DisplayServiceException>(() => c.Blit(0, 0, 480, 320, new byte[DisplayWire.FrameBytes]));
        var lines = LogLines.Where(l => l.StartsWith($"Could not open the display for session {ThisSession}")).ToList();
        Assert.Single(lines);
        Assert.Contains("No display found", lines[0]);
    }
}

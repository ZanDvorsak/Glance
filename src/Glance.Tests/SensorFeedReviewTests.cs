using System.Diagnostics;
using System.Security.Principal;
using Glance.Core.Sensors;
using Glance.Core.Service;
using LibreHardwareMonitor.Hardware;

namespace Glance.Tests;

/// <summary>
/// Findings of the independent review of the sensor-feed branch (docs/reviews/review-sensor-feed.md, "Fixes after review").
/// No real sensor is read (fake devices), and every pipe has a random name.
/// </summary>
public class SensorFeedReviewTests
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;

    private static string PipeName() => "glance-test-" + Guid.NewGuid().ToString("N");

    private static async Task<bool> Eventually(Func<bool> condition, int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until) { if (condition()) return true; await Task.Delay(25); }
        return condition();
    }

    private static List<SensorInfo> NoDrives() => new();

    private sealed class Hw : IHardwareNode
    {
        public string Name { get; init; } = "hw";
        public List<SensorReading> List { get; } = new();
        public Action? OnUpdate { get; set; }
        public int Updates;
        public volatile bool Inside;
        public void Update()
        {
            Inside = true;
            try { Interlocked.Increment(ref Updates); OnUpdate?.Invoke(); }
            finally { Inside = false; }
        }
        public IEnumerable<SensorReading> Readings => List;
        public IEnumerable<IHardwareNode> Children => Array.Empty<IHardwareNode>();
    }

    private static Hw Device(string name, string id, float? value)
    {
        var hw = new Hw { Name = name };
        hw.List.Add(new SensorReading(id, id, SensorType.Temperature, value));
        return hw;
    }

    private static float? ValueOf(SensorService s, string id) => s.Snapshot.Single(x => x.Id == id).Value;

    // ---- L1: a device that fails now and then ---------------------------------------------------------------------------

    [Fact]
    public void L1_a_device_that_fails_now_and_then_keeps_its_last_value_for_a_few_updates()
    {
        var nvme = Device("nvme", "/nvme/temp", 41f);
        bool failing = false;
        nvme.OnUpdate = () => { if (failing) throw new IOException("SMART read timed out"); };
        using var service = new SensorService(() => new[] { nvme });
        service.Update();
        Assert.Equal(41f, ValueOf(service, "/nvme/temp"));

        failing = true;
        for (int i = 1; i <= 3; i++)   // the grace period: 3 failed updates (SensorService.FailedUpdatesKept)
        {
            service.Update();
            Assert.True(ValueOf(service, "/nvme/temp") == 41f, $"failed update {i}: the value blinked to \"no value\"");
        }
        service.Update();   // one more: it is really gone now
        Assert.Null(ValueOf(service, "/nvme/temp"));

        failing = false;
        nvme.List[0] = nvme.List[0] with { Value = 43f };
        service.Update();
        Assert.Equal(43f, ValueOf(service, "/nvme/temp"));

        failing = true;   // the count starts again after a good update
        service.Update();
        Assert.Equal(43f, ValueOf(service, "/nvme/temp"));
    }

    // ---- L3: the service's stop budget ------------------------------------------------------------------------------------

    [Fact]
    public async Task L3_the_service_sensors_stop_within_about_a_second_when_an_update_hangs()
    {
        var release = new ManualResetEventSlim(false);
        var hw = Device("cpu", "/cpu", 1f);
        hw.OnUpdate = () => { if (hw.Updates > 1) release.Wait(15000); };   // the second update hangs (a stuck driver call)
        bool closedDuring = false; int closes = 0;
        var source = new LocalSensorSource(new SensorService(() => new[] { hw }, () => { if (hw.Inside) closedDuring = true; Interlocked.Increment(ref closes); }), NoDrives);
        Assert.True(await Eventually(() => hw.Inside, 4000), "the loop never started its second update");

        var sw = Stopwatch.StartNew();
        source.Dispose();   // ServiceRuntime.Stop, after the blank
        sw.Stop();
        release.Set();

        Assert.True(sw.ElapsedMilliseconds < 1500, $"Dispose took {sw.ElapsedMilliseconds} ms of the service's stop budget");
        Assert.True(await Eventually(() => Volatile.Read(ref closes) == 1, 4000), "the sensors were never closed");
        Assert.False(closedDuring, "closed while the update was still running");
    }

    // ---- L4: no flip-flop between the service's feed and the local sensors ------------------------------------------------

    [Fact]
    public async Task L4_a_short_gap_in_the_service_feed_does_not_open_the_local_sensors()
    {
        var name = PipeName();
        using var server = new SensorPipeServer(name, ServicePipeSecurity.Build(Me));
        server.Start();
        using var stop = new CancellationTokenSource();
        var gap = new ManualResetEventSlim(true);   // set = publishing
        var publisher = Task.Run(async () =>
        {
            int i = 0;
            while (!stop.IsCancellationRequested)
            {
                if (gap.IsSet) server.Publish(new[] { new SensorInfo("/service", "hw", "S", "Load", i++, "%") });
                try { await Task.Delay(200, stop.Token); } catch (OperationCanceledException) { return; }
            }
        });
        var pipe = new PipeSensorClient(name, staleAfter: TimeSpan.FromMilliseconds(1500), trustedOwners: new[] { Me });
        Assert.True(await Eventually(() => pipe.HasData, 6000), "the service's feed never arrived");
        int opens = 0;
        using var hub = new SensorHub(pipe, () => { Interlocked.Increment(ref opens); return new SensorService(() => new[] { Device("local", "/local", 2f) }); },
            NoDrives, TimeSpan.FromMilliseconds(100));
        await Task.Delay(1500);

        for (int g = 0; g < 3; g++)   // three gaps, each stale for about 0.6 s: less than the hub's 1 s between checks (after a resume, a slow update)
        {
            gap.Reset();
            await Task.Delay(2100);
            gap.Set();
            await Task.Delay(1200);
        }
        stop.Cancel();
        await publisher;

        Assert.Equal(0, Volatile.Read(ref opens));   // before: LibreHardwareMonitor was opened in the user's process, then closed again
        Assert.True(hub.FromService);
    }

    // ---- nits -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Nit_snapshot_numbers_are_not_lost_when_two_threads_publish()
    {
        using var server = new SensorPipeServer(PipeName(), ServicePipeSecurity.Build(Me));
        var one = new[] { new SensorInfo("/a", "hw", "A", "Load", 1f, "%") };
        const int threads = 8, each = 5000;
        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, _ => { for (int i = 0; i < each; i++) server.Publish(one); });
        Assert.Equal(threads * each, PipeSensorClient.Parse(server.Line())!.Value.Seq);
    }

    [Fact]
    public void Nit_a_device_whose_error_text_changes_is_still_logged_once_a_minute()
    {
        var log = new List<string>();
        long now = 0;
        int n = 0;
        var ec = Device("ec", "/ec/fan", 900f);
        ec.OnUpdate = () => throw new InvalidOperationException($"EC read failed at port 0x{++n:x}");   // a different text every time
        using var service = new SensorService(() => new[] { ec }, log: log.Add, nowMs: () => now);

        for (int i = 0; i < 30; i++) { service.Update(); now += 1000; }
        Assert.Single(log);
        Assert.Contains("EC read failed", log[0]);
    }

    [Fact]
    public async Task Nit_disposing_the_hub_never_waits_for_its_local_sensors()
    {
        var release = new ManualResetEventSlim(false);
        var hw = Device("cpu", "/cpu", 1f);
        hw.OnUpdate = () => release.Wait(15000);   // a slow local update while the user closes the editor
        bool closedDuring = false; int closes = 0;
        var pipe = new PipeSensorClient(PipeName(), trustedOwners: new[] { Me });   // no service
        var hub = new SensorHub(pipe, () => new SensorService(() => new[] { hw }, () => { if (hw.Inside) closedDuring = true; Interlocked.Increment(ref closes); }),
            NoDrives, TimeSpan.FromMilliseconds(50));
        Assert.True(await Eventually(() => hw.Inside, 4000), "the hub never started reading its local sensors");

        var sw = Stopwatch.StartNew();
        hub.Dispose();   // on the editor's or the tray app's UI thread
        sw.Stop();
        release.Set();

        Assert.True(sw.ElapsedMilliseconds < 150, $"Dispose blocked the UI thread for {sw.ElapsedMilliseconds} ms");
        Assert.True(await Eventually(() => Volatile.Read(ref closes) == 1, 4000), "the local sensors were never closed");
        Assert.False(closedDuring, "closed while the update was still running");
    }
}

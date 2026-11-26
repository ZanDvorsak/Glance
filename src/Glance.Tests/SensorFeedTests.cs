using System.Security.Principal;
using Glance.Core.Sensors;
using Glance.Core.Service;
using LibreHardwareMonitor.Hardware;

namespace Glance.Tests;

/// <summary>
/// Review M8 and the sensor dispose nits (docs/design/fix-sensor-feed.md): non-finite values, a stalled sensor loop behind a
/// connected pipe, one failing device, and closing LibreHardwareMonitor while an update runs. No real sensor is read: the
/// hardware is <see cref="FakeHw"/>, and every pipe has a random name.
/// </summary>
public class SensorFeedTests
{
    private static readonly SecurityIdentifier Me = WindowsIdentity.GetCurrent().User!;

    private static string PipeName() => "glance-test-" + Guid.NewGuid().ToString("N");   // never the real pipe name

    private static async Task<bool> Eventually(Func<bool> condition, int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < until) { if (condition()) return true; await Task.Delay(25); }
        return condition();
    }

    /// <summary>A fake device: fixed readings, optional work (or a throw, or a block) inside <see cref="Update"/>.</summary>
    private sealed class FakeHw : IHardwareNode
    {
        public string Name { get; init; } = "hw";
        public List<SensorReading> List { get; } = new();
        public List<FakeHw> Kids { get; } = new();
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
        public IEnumerable<IHardwareNode> Children => Kids;
    }

    private static FakeHw Device(string name, params (string Id, float? Value)[] readings)
    {
        var hw = new FakeHw { Name = name };
        foreach (var (id, v) in readings) hw.List.Add(new SensorReading(id, id, SensorType.Load, v));
        return hw;
    }

    private static List<SensorInfo> NoDrives() => new();

    // ---- non-finite values ----------------------------------------------------------------------------------------

    [Fact]
    public void M8_a_NaN_or_infinite_reading_becomes_no_value()
    {
        var hw = Device("cpu", ("/nan", float.NaN), ("/inf", float.PositiveInfinity), ("/ninf", float.NegativeInfinity), ("/ok", 5f), ("/none", null));
        using var service = new SensorService(() => new[] { hw });
        service.Update();

        var byId = service.Snapshot.ToDictionary(s => s.Id, s => s.Value);
        Assert.Null(byId["/nan"]);
        Assert.Null(byId["/inf"]);
        Assert.Null(byId["/ninf"]);
        Assert.Equal(5f, byId["/ok"]);
        Assert.Null(byId["/none"]);
    }

    [Fact]
    public async Task M8_publishing_a_NaN_never_throws_and_the_client_gets_no_value_for_it()
    {
        var name = PipeName();
        using var server = new SensorPipeServer(name, ServicePipeSecurity.Build(Me));
        server.Start();
        var ex = Record.Exception(() => server.Publish(new[]
        {
            new SensorInfo("/nan", "hw", "A", "Load", float.NaN, "%"),
            new SensorInfo("/inf", "hw", "B", "Load", float.PositiveInfinity, "%"),
            new SensorInfo("/ok", "hw", "C", "Load", 7f, "%"),
        }));
        Assert.Null(ex);   // before: System.ArgumentException (.NET number values must be finite)

        using var client = new PipeSensorClient(name, staleAfter: TimeSpan.FromSeconds(3), trustedOwners: new[] { Me });
        Assert.True(await Eventually(() => client.HasData, 6000), "the snapshot with a NaN in it never arrived");
        var byId = client.Snapshot.ToDictionary(s => s.Id, s => s.Value);
        Assert.Null(byId["/nan"]);
        Assert.Null(byId["/inf"]);
        Assert.Equal(7f, byId["/ok"]);
    }

    [Fact]
    public async Task M8_one_sensor_that_stays_NaN_does_not_freeze_the_others()
    {
        var name = PipeName();
        using var server = new SensorPipeServer(name, ServicePipeSecurity.Build(Me));
        server.Start();
        server.Publish(new[] { new SensorInfo("/a", "hw", "A", "Load", 1f, "%"), new SensorInfo("/b", "hw", "B", "Load", 1f, "%") });
        using var client = new PipeSensorClient(name, staleAfter: TimeSpan.FromSeconds(3), trustedOwners: new[] { Me });
        Assert.True(await Eventually(() => client.HasData, 6000), "the first snapshot never arrived");

        for (int i = 2; i <= 3; i++)   // the service's update loop: /a has no reading any more, /b keeps changing
        {
            try { server.Publish(new[] { new SensorInfo("/a", "hw", "A", "Load", float.NaN, "%"), new SensorInfo("/b", "hw", "B", "Load", i, "%") }); }
            catch { /* before: LocalSensorSource's loop swallowed this, so nothing new was ever published */ }
        }
        Assert.True(await Eventually(() => client.Snapshot.Any(s => s.Id == "/b" && s.Value == 3f), 4000), "every sensor froze because one was NaN");
    }

    // ---- a stalled sensor loop behind a connected pipe ---------------------------------------------------------------

    [Fact]
    public async Task M8_a_feed_that_stops_changing_is_stale_while_the_pipe_stays_connected()
    {
        var name = PipeName();
        using var server = new SensorPipeServer(name, ServicePipeSecurity.Build(Me));
        server.Start();
        server.Publish(new[] { new SensorInfo("/a", "hw", "A", "Load", 1f, "%") });   // then the sensor loop hangs: nothing new
        using var client = new PipeSensorClient(name, staleAfter: TimeSpan.FromMilliseconds(1500), trustedOwners: new[] { Me });
        Assert.True(await Eventually(() => client.HasData, 6000), "the first snapshot never arrived");

        await Task.Delay(3500);   // more than twice the stale time; the server keeps re-sending the old snapshot every second
        Assert.NotEmpty(client.Snapshot);   // still connected: the snapshot is only dropped when the connection goes
        Assert.False(client.HasData, "the old snapshot kept looking live although the sensors stopped");
    }

    [Fact]
    public async Task M8_a_client_that_connects_after_the_feed_stalled_never_sees_it_as_live()
    {
        var name = PipeName();
        using var server = new SensorPipeServer(name, ServicePipeSecurity.Build(Me));
        server.Start();
        server.Publish(new[] { new SensorInfo("/a", "hw", "A", "Load", 1f, "%") });
        await Task.Delay(2500);   // the sensor loop has been silent for longer than the client's stale time

        using var client = new PipeSensorClient(name, staleAfter: TimeSpan.FromMilliseconds(1500), trustedOwners: new[] { Me });
        bool everLive = false;
        Assert.True(await Eventually(() => { everLive |= client.HasData; return client.Snapshot.Count > 0; }, 6000), "nothing arrived");
        var until = Environment.TickCount64 + 1500;
        while (Environment.TickCount64 < until) { everLive |= client.HasData; await Task.Delay(25); }
        Assert.False(everLive, "a new client took an old snapshot for live values");
    }

    [Fact]
    public async Task M8_new_snapshots_keep_the_feed_live()
    {
        var name = PipeName();
        using var server = new SensorPipeServer(name, ServicePipeSecurity.Build(Me));
        server.Start();
        using var client = new PipeSensorClient(name, staleAfter: TimeSpan.FromMilliseconds(1500), trustedOwners: new[] { Me });
        bool everStale = false, seen = false;
        var until = Environment.TickCount64 + 4500;
        int i = 0;
        while (Environment.TickCount64 < until)
        {
            server.Publish(new[] { new SensorInfo("/a", "hw", "A", "Load", i++, "%") });   // the service's 1 s update loop
            for (int k = 0; k < 20; k++) { await Task.Delay(25); if (client.HasData) seen = true; else if (seen) everStale = true; }
        }
        Assert.True(seen, "nothing arrived");
        Assert.False(everStale, "a feed that keeps changing was reported stale");
    }

    [Fact]
    public async Task M8_each_new_snapshot_has_the_next_number_and_reaches_the_client_at_once()
    {
        var name = PipeName();
        using var server = new SensorPipeServer(name, ServicePipeSecurity.Build(Me));
        server.Start();
        server.Publish(new[] { new SensorInfo("/a", "hw", "A", "Load", 1f, "%") });
        using var client = new PipeSensorClient(name, trustedOwners: new[] { Me });
        Assert.True(await Eventually(() => client.HasData, 6000), "the first snapshot never arrived");
        Assert.Equal(1, client.Sequence);
        await Task.Delay(300);   // the server is now waiting for its once-a-second re-send

        server.Publish(new[] { new SensorInfo("/a", "hw", "A", "Load", 2f, "%") });
        Assert.True(await Eventually(() => client.Sequence == 2, 500), "a new snapshot waited for the next re-send");
        Assert.Equal(2f, client.Snapshot[0].Value);
    }

    [Fact]
    public void M8_the_client_reads_the_numbered_line_and_the_older_bare_list()
    {
        var line = PipeSensorClient.Parse("{\"Seq\":7,\"AgeMs\":1200,\"Sensors\":[{\"Id\":\"/a\",\"Hardware\":\"hw\",\"Name\":\"A\",\"Type\":\"Load\",\"Value\":null,\"Unit\":\"%\"}]}");
        Assert.NotNull(line);
        Assert.Equal(7, line!.Value.Seq);
        Assert.Equal(1200, line.Value.AgeMs);
        Assert.Null(Assert.Single(line.Value.Sensors).Value);

        var legacy = PipeSensorClient.Parse("[{\"Id\":\"/a\",\"Hardware\":\"hw\",\"Name\":\"A\",\"Type\":\"Load\",\"Value\":3,\"Unit\":\"%\"}]");
        Assert.Equal(0, legacy!.Value.AgeMs);
        Assert.Equal(3f, Assert.Single(legacy.Value.Sensors).Value);

        Assert.Equal(0, PipeSensorClient.Parse("{\"Seq\":1,\"AgeMs\":-5,\"Sensors\":[]}")!.Value.AgeMs);   // never newer than now
        Assert.Null(PipeSensorClient.Parse("{\"Seq\":1}"));   // no sensors: not a snapshot
    }

    [Fact]
    public async Task M8_the_hub_falls_back_to_its_own_sensors_when_the_service_feed_stalls()
    {
        var name = PipeName();
        using var server = new SensorPipeServer(name, ServicePipeSecurity.Build(Me));
        server.Start();
        server.Publish(new[] { new SensorInfo("/service", "hw", "S", "Load", 1f, "%") });   // and then nothing new
        var local = Device("local", ("/local", 2f));
        var pipe = new PipeSensorClient(name, staleAfter: TimeSpan.FromMilliseconds(1500), trustedOwners: new[] { Me });
        using var hub = new SensorHub(pipe, () => new SensorService(() => new[] { local }), NoDrives, TimeSpan.FromMilliseconds(200));

        Assert.True(await Eventually(() => hub.Snapshot.Any(s => s.Id == "/service"), 6000), "the service's feed never arrived");
        Assert.True(await Eventually(() => hub.Snapshot.Any(s => s.Id == "/local"), 6000), "the hub kept showing the stalled service feed as live");
        Assert.False(hub.FromService);
    }

    // ---- one failing device ------------------------------------------------------------------------------------------

    [Fact]
    public void M8_one_failing_device_does_not_freeze_the_others()
    {
        var good = Device("gpu", ("/gpu/load", 10f));
        var bad = Device("ec", ("/ec/fan", 900f));
        bad.OnUpdate = () => throw new InvalidOperationException("EC read failed");
        var badChild = Device("sub", ("/mb/sub/temp", 40f));
        badChild.OnUpdate = () => throw new IOException("SMBus busy");
        var board = Device("mb", ("/mb/temp", 30f));
        board.Kids.Add(badChild);
        using var service = new SensorService(() => new[] { bad, good, board });

        var ex = Record.Exception(service.Update);
        Assert.Null(ex);   // before: the whole update threw and the old list stayed (every sensor frozen)
        good.List[0] = good.List[0] with { Value = 20f };
        service.Update();

        var byId = service.Snapshot.ToDictionary(s => s.Id, s => s.Value);
        Assert.Equal(20f, byId["/gpu/load"]);       // the others keep updating
        Assert.Equal(30f, byId["/mb/temp"]);
        Assert.True(byId.ContainsKey("/ec/fan"));   // the failing device's sensors stay known, with no value
        Assert.Null(byId["/ec/fan"]);
        Assert.Null(byId["/mb/sub/temp"]);
    }

    [Fact]
    public void M8_a_device_that_fails_at_start_does_not_stop_the_service_sensors()
    {
        var good = Device("gpu", ("/gpu/load", 10f));
        var bad = Device("ec", ("/ec/fan", 900f));
        bad.OnUpdate = () => throw new InvalidOperationException("EC read failed");
        var service = new SensorService(() => new[] { bad, good });

        LocalSensorSource? source = null;
        var ex = Record.Exception(() => source = new LocalSensorSource(service, NoDrives));   // before: the constructor threw, so the service had no sensors at all
        using var _ = source;
        Assert.Null(ex);
        Assert.Contains(source!.HardwareSnapshot, s => s.Id == "/gpu/load" && s.Value == 10f);
    }

    [Fact]
    public void M8_a_failing_device_is_logged_once_a_minute_not_every_second()
    {
        var log = new List<string>();
        long now = 0;
        var bad = Device("ec", ("/ec/fan", 900f));
        bad.OnUpdate = () => throw new InvalidOperationException("EC read failed");
        using var service = new SensorService(() => new[] { bad }, log: log.Add, nowMs: () => now);

        for (int i = 0; i < 30; i++) { service.Update(); now += 1000; }   // 30 s of failures
        Assert.Single(log);
        Assert.Contains("ec", log[0]);
        Assert.Contains("EC read failed", log[0]);
        for (int i = 0; i < 40; i++) { service.Update(); now += 1000; }   // past the minute: one more line, with the count
        Assert.Equal(2, log.Count);
        Assert.Contains("repeated", log[1]);
    }

    // ---- shutdown order: cancel, wait for the loop, then close ----------------------------------------------------------

    /// <summary>A device whose second and later updates block until <c>release</c> is set; closing records whether an update was running.</summary>
    private static (FakeHw Hw, ManualResetEventSlim Release, Func<bool> ClosedDuringUpdate, Func<int> Closes, Action Close) Blocking()
    {
        var release = new ManualResetEventSlim(false);
        var hw = Device("cpu", ("/cpu", 1f));
        hw.OnUpdate = () => { if (hw.Updates > 1) release.Wait(10000); };
        bool during = false; int closes = 0;
        void Close() { if (hw.Inside) during = true; Interlocked.Increment(ref closes); }
        return (hw, release, () => during, () => closes, Close);
    }

    [Fact]
    public async Task Nit_the_local_source_never_closes_the_sensors_while_its_loop_is_updating()
    {
        var (hw, release, closedDuringUpdate, closes, close) = Blocking();
        var source = new LocalSensorSource(new SensorService(() => new[] { hw }, close), NoDrives);
        Assert.True(await Eventually(() => hw.Inside, 4000), "the loop never started its second update");

        var dispose = Task.Run(source.Dispose);
        await Task.Delay(400);
        release.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await Eventually(() => closes() > 0, 4000), "the sensors were never closed");
        Assert.False(closedDuringUpdate(), "LibreHardwareMonitor was closed while the loop was inside Update()");
        Assert.Equal(1, closes());
    }

    [Fact]
    public async Task Nit_the_hub_never_closes_its_local_sensors_while_its_loop_is_updating()
    {
        var (hw, release, closedDuringUpdate, closes, close) = Blocking();
        hw.OnUpdate = () => release.Wait(10000);   // the hub's first update already blocks
        var pipe = new PipeSensorClient(PipeName(), trustedOwners: new[] { Me });   // nobody serves it: local sensors
        var hub = new SensorHub(pipe, () => new SensorService(() => new[] { hw }, close), NoDrives, TimeSpan.FromMilliseconds(50));
        Assert.True(await Eventually(() => hw.Inside, 4000), "the hub never started reading its local sensors");

        var dispose = Task.Run(hub.Dispose);
        await Task.Delay(400);
        release.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await Eventually(() => closes() > 0, 4000), "the local sensors were never closed");
        Assert.False(closedDuringUpdate(), "LibreHardwareMonitor was closed while the hub's loop was inside Update()");
    }

    [Fact]
    public async Task Nit_local_sensors_the_hub_opens_while_it_is_disposed_are_closed()
    {
        var opening = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        int closes = 0;
        var pipe = new PipeSensorClient(PipeName(), trustedOwners: new[] { Me });
        var hub = new SensorHub(pipe, () =>
        {
            opening.Set();
            release.Wait(10000);   // LibreHardwareMonitor takes seconds to open
            return new SensorService(() => new[] { Device("cpu", ("/cpu", 1f)) }, () => Interlocked.Increment(ref closes));
        }, NoDrives, TimeSpan.FromMilliseconds(50));
        Assert.True(opening.Wait(4000), "the hub never opened its local sensors");

        var dispose = Task.Run(hub.Dispose);
        await Task.Delay(200);
        release.Set();
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await Eventually(() => closes > 0, 4000), "sensors opened during Dispose were never closed (the driver stays open)");
    }

    [Fact]
    public async Task The_hub_tries_its_local_sensors_again_after_they_fail_to_open()
    {
        int opens = 0;
        var pipe = new PipeSensorClient(PipeName(), trustedOwners: new[] { Me });   // no service
        using var hub = new SensorHub(pipe, () =>
        {
            if (Interlocked.Increment(ref opens) == 1) throw new InvalidOperationException("driver busy");
            return new SensorService(() => new[] { Device("cpu", ("/cpu", 1f)) });
        }, NoDrives, TimeSpan.FromMilliseconds(50));

        Assert.True(await Eventually(() => hub.Snapshot.Any(s => s.Id == "/cpu"), 5000),
            "one failure to open the local sensors stopped them for good");
    }
}

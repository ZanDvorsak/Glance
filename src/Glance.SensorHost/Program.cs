using Glance.Core.Sensors;

// Standalone elevated helper (the Glance.Runner tray app does the same job and more).
// Reads all hardware sensors (needs admin) and publishes snapshots, read-only, over a named pipe.
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var sensors = new SensorService();
using var server = new SensorPipeServer();
server.Start();

Console.WriteLine("Sensor host running. Ctrl+C to stop.");
while (!cts.IsCancellationRequested)
{
    sensors.Update();
    server.Publish(sensors.Snapshot);
    try { await Task.Delay(1000, cts.Token); } catch (OperationCanceledException) { }
}

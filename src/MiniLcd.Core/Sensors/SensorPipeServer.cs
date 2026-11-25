using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MiniLcd.Core.Sensors;

/// <summary>
/// Publishes sensor snapshots, read-only, over a named pipe that only the current Windows user may open.
/// Each client is served on its own task and dropped if it stops reading, so one stuck client
/// can never block the others or the accept loop.
/// </summary>
public sealed class SensorPipeServer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private volatile string _latest = "[]";

    public void Publish(IReadOnlyList<SensorInfo> snapshot) => _latest = JsonSerializer.Serialize(snapshot);

    public void Start()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var server = NamedPipeServerStreamAcl.Create(PipeSensorClient.PipeName, PipeDirection.Out, 16,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                    await server.WaitForConnectionAsync(_cts.Token);
                    _ = Serve(server);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception) { try { await Task.Delay(1000, _cts.Token); } catch (OperationCanceledException) { return; } }
            }
        });
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        try
        {
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
            while (!_cts.IsCancellationRequested && pipe.IsConnected)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5)); // a client that does not read for 5 s is dropped
                await writer.WriteLineAsync(_latest.AsMemory(), timeout.Token);
                await writer.FlushAsync(timeout.Token);
                await Task.Delay(1000, _cts.Token);
            }
        }
        catch { /* client left or stalled */ }
        finally { pipe.Dispose(); }
    }

    public void Dispose() => _cts.Cancel();
}

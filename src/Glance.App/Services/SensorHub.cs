using System.Diagnostics;
using Glance.Core.Sensors;

namespace Glance.App.Services;

/// <summary>
/// Single sensor source for the app. Uses the elevated SensorHost when it is running (full CPU/motherboard data),
/// otherwise falls back to limited local, non-admin reading in the background.
/// </summary>
public sealed class SensorHub : ISensorSource, IDisposable
{
    private readonly PipeSensorClient _pipe = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile SensorService? _local;

    public SensorHub() => Task.Run(LocalLoop);

    private volatile IReadOnlyList<SensorInfo> _merged = Array.Empty<SensorInfo>();
    private List<SensorInfo> _drives = new();
    private long _drivesAt;

    public bool IsElevated => _pipe.HasData;
    /// <summary>Hardware sensors (from the helper or local reading) plus drive-space sensors from Windows.</summary>
    public IReadOnlyList<SensorInfo> Snapshot => _merged;
    public void Update() { }

    private void Merge()
    {
        var baseList = _pipe.HasData ? _pipe.Snapshot : _local?.Snapshot ?? Array.Empty<SensorInfo>();
        if (Environment.TickCount64 - _drivesAt > 5000) { _drives = DriveSensors.Read(); _drivesAt = Environment.TickCount64; }
        _merged = baseList.Concat(_drives).ToList();
    }

    public string Status => IsElevated
        ? $"Sensors: elevated helper ({Snapshot.Count})"
        : _local is null ? "Sensors: starting..." : $"Sensors: limited, no helper ({Snapshot.Count})";

    private async Task LocalLoop()
    {
        try
        {
            _ = Task.Run(async () => { while (!_cts.IsCancellationRequested) { Merge(); try { await Task.Delay(500, _cts.Token); } catch { return; } } });
            await Task.Delay(1500, _cts.Token); // give the helper a moment to deliver first
            while (!_cts.IsCancellationRequested)
            {
                if (_pipe.HasData)
                {
                    if (_local is { } l) { _local = null; l.Dispose(); }
                }
                else
                {
                    _local ??= new SensorService();
                    _local.Update();
                }
                await Task.Delay(1000, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Debug.WriteLine("Local sensors failed: " + ex.Message); }
    }

    /// <summary>Looks for an executable next to the app or in a sibling project's dev build output.</summary>
    public static string? FindExe(string exe, string projectFolder)
    {
        var dir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe);
        for (var d = dir; d is not null; d = Path.GetDirectoryName(d))
        {
            var proj = Path.Combine(d, projectFolder, "bin");
            if (!Directory.Exists(proj)) continue;
            return Directory.EnumerateFiles(proj, exe, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        return null;
    }

    public static string? FindRunnerExe() => FindExe("Glance.Runner.exe", "Glance.Runner");

    /// <summary>The elevated process that provides sensors: the runner if available, otherwise the standalone helper.</summary>
    public static string? FindHostExe() => FindRunnerExe() ?? FindExe("Glance.SensorHost.exe", "Glance.SensorHost");

    /// <summary>Starts the helper; Windows shows a UAC prompt because it needs administrator rights.</summary>
    public static bool LaunchHost()
    {
        var path = FindHostExe();
        if (path is null) return false;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(path)! }); return true; }
        catch { return false; } // user declined UAC
    }

    public void Dispose() { _cts.Cancel(); _pipe.Dispose(); _local?.Dispose(); }
}

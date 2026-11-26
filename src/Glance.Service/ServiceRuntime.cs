using System.Diagnostics;
using System.Security.Principal;
using Glance.Core.Display;
using Glance.Core.Sensors;
using Glance.Core.Service;

namespace Glance.Service;

/// <summary>
/// Wires the service together: the display server (USB), the sensors (LibreHardwareMonitor) and their pipes, and a 2 s tick.
/// All decisions live in <c>Glance.Core</c> (<see cref="DisplayServer"/>, <see cref="DisplayAccess"/>, <see cref="DisplayPowerPolicy"/>,
/// <see cref="ServicePipeSecurity"/>), where they are tested; this class only connects them.
/// </summary>
internal sealed class ServiceRuntime : IServiceBody
{
    private readonly Action<string> _log;
    private readonly string? _logProblem;
    private DisplayServer? _display;
    private bool _displayServing;
    private LocalSensorSource? _sensors;
    private SensorPipeServer? _sensorPipe;
    private Timer? _tick;
    private int _ticks, _stopped;

    /// <param name="logProblem">Why the log file is not written, if it is not (shown in the display status instead).</param>
    public ServiceRuntime(Action<string> log, string? logProblem) { _log = log; _logProblem = logProblem; }

    public void Log(string message) { try { _log(message); } catch { /* never */ } }

    public void Start()
    {
        using (var me = WindowsIdentity.GetCurrent()) Log($"Glance service {typeof(ServiceRuntime).Assembly.GetName().Version} starting as {me.Name}");

        // L5: the pre-shutdown timeout through the service API (a registry write is only read at the next boot); log what Windows uses
        if (ServiceControl.SetPreshutdownTimeout(ServiceNames.PreshutdownTimeoutMs) is { } err) Log("Could not set the pre-shutdown timeout: " + err);
        Log($"Pre-shutdown timeout: {ServiceControl.QueryPreshutdownTimeout()?.ToString() ?? "unknown"} ms");

        var system = WindowsIdentity.GetCurrent().User!;

        // the pipes first: they are quick, and should exist before anyone can sign in (a squatter has less time, L10)
        _display = DisplayServer.ForUsb(new DisplayServerOptions { Log = Log, StatusNote = _logProblem });
        TryServeDisplay();
        _sensorPipe = new SensorPipeServer(ServiceNames.SensorPipe, ServicePipeSecurity.Build(system), Log);
        _sensorPipe.Start();   // serves "[]" until the sensors are ready

        // M3: Glance 0.1's logon task would start the tray app elevated; remove it if the installer could not
        _ = Task.Run(RemoveLegacyTask);

        try
        {
            _sensors = new LocalSensorSource();
            _sensorPipe.Publish(_sensors.HardwareSnapshot);
            _sensors.Updated += () => _sensorPipe.Publish(_sensors.HardwareSnapshot);
            Log($"Sensors ready ({_sensors.HardwareSnapshot.Count})");
        }
        catch (Exception ex) { Log("Sensors could not be read: " + ex.Message); }   // the display still works

        _tick = new Timer(_ => OnTick(), null, 2000, 2000);
    }

    private void RemoveLegacyTask()
    {
        try
        {
            if (RunHidden(LegacyTask.QueryStartInfo()) != 0) return;   // not there
            int code = RunHidden(LegacyTask.ServiceDeleteStartInfo());
            Log(code == 0 ? $"Removed Glance 0.1's \"{LegacyTask.Name}\" sign-in task (it would start the tray app with administrator rights)"
                          : $"Could not remove Glance 0.1's \"{LegacyTask.Name}\" sign-in task (schtasks exit code {code})");
        }
        catch (Exception ex) { Log("Checking for Glance 0.1's sign-in task failed: " + ex.Message); }
    }

    private static int RunHidden(ProcessStartInfo psi)
    {
        using var p = Process.Start(psi)!;
        var drain = Task.WhenAll(p.StandardOutput.ReadToEndAsync(), p.StandardError.ReadToEndAsync());
        if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return -1; }
        drain.Wait(2000);
        return p.ExitCode;
    }

    /// <summary>Creates the display pipe. If another program holds the name (squatting while the service was stopped), retry later.</summary>
    private void TryServeDisplay()
    {
        if (_displayServing || _display is null) return;
        try { _display.Start(); _displayServing = true; Log("Display pipe ready"); }
        catch (Exception ex) { Log($"The display pipe {ServiceNames.DisplayPipe} could not be created: {ex.Message}"); }
    }

    private void OnTick()
    {
        try
        {
            _display?.Tick();
            if (!_displayServing && ++_ticks % 5 == 0) TryServeDisplay();
        }
        catch (Exception ex) { Log("Tick failed: " + ex.Message); }
    }

    public void OnSignal(ServiceSignal signal)
    {
        if (_display is null) return;
        switch (signal.Kind)
        {
            case ServiceSignalKind.Power:
                Log($"Windows {signal.Power}: display {_display.HandlePower(signal.Power)}");
                break;
            case ServiceSignalKind.SessionLeft:
                _display.SessionLeft(signal.SessionId);
                break;
        }
    }

    public void Stop(ServiceSignal reason)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        var e = reason.Kind == ServiceSignalKind.Power ? reason.Power : PowerEvent.Quit;
        Log($"Glance service stopping ({e})");
        if (_display is { } display)
        {
            Log("Display: " + display.HandlePower(e));   // the blank first: Windows may end the process soon after
            display.Dispose();
        }
        _tick?.Dispose();
        _sensorPipe?.Dispose();
        try { _sensors?.Dispose(); } catch { /* best effort */ }
        Log("Glance service stopped");
    }
}

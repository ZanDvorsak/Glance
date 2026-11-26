using System.Runtime.InteropServices;
using System.Security.Principal;
using Glance.Core.Security;
using Glance.Core.Service;

namespace Glance.Service;

/// <summary>What the service does; <see cref="NativeService"/> calls it from the Service Control Manager's threads.</summary>
internal interface IServiceBody
{
    void Start();
    /// <summary>A control request mapped by <see cref="ServiceControlMap"/>. Power events run here, within their budget.</summary>
    void OnSignal(ServiceSignal signal);
    /// <summary>The service stops (STOP or PRESHUTDOWN). Blanks the display once.</summary>
    void Stop(ServiceSignal reason);
    void Log(string message);
}

/// <summary>
/// The Windows service entry point, written against the service API directly: .NET's <c>ServiceBase</c> cannot accept
/// <c>SERVICE_CONTROL_PRESHUTDOWN</c>, which comes before shutdown and has its own timeout (the service sets it at start).
/// The mapping of control codes to display events lives in <see cref="ServiceControlMap"/> (Core, tested).
/// </summary>
internal static class NativeService
{
    private const int ServiceWin32OwnProcess = 0x10;
    private const int Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;
    private const int NoError = 0, ErrorAccessDenied = 5, ErrorCallNotImplemented = 120, ErrorExceptionInService = 1064;

    private static IServiceBody? _body;
    private static Func<Action<string>, string?, IServiceBody>? _create;
    private static IntPtr _handle;
    private static int _checkPoint, _state;
    private static readonly object _statusLock = new();

    // kept in static fields so the garbage collector never frees a callback Windows still holds
    private static readonly ServiceMainProc MainProc = ServiceMain;
    private static readonly HandlerEx Handler = HandleControl;

    /// <summary>
    /// Runs the service until it is stopped. Returns false when this process was not started by the Service Control Manager.
    /// <paramref name="create"/> gets the log and, if the log file cannot be written, why.
    /// </summary>
    public static bool Run(Func<Action<string>, string?, IServiceBody> create)
    {
        _create = create;
        var table = new[]
        {
            new ServiceTableEntry { Name = ServiceNames.ServiceName, Proc = Marshal.GetFunctionPointerForDelegate(MainProc) },
            new ServiceTableEntry { Name = null, Proc = IntPtr.Zero },
        };
        return StartServiceCtrlDispatcherW(table);   // returns when the service has stopped
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _handle = RegisterServiceCtrlHandlerExW(ServiceNames.ServiceName, Handler, IntPtr.Zero);
        if (_handle == IntPtr.Zero) return;
        Report(StartPending, 30000);   // the folder check and the sensor library can take a while

        // L6: only now is it certain that Windows started this process as the service, so only now is the log opened
        ServiceLog? log = null;
        using (var me = WindowsIdentity.GetCurrent())
            if (ServiceLog.ShouldOpen(me.IsSystem, elevatedConsole: false)) log = ServiceLog.OpenDefault();
        Action<string> write = m => log?.Write(m);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => write("Unhandled error: " + e.ExceptionObject);
        if (log?.DisabledReason is { } logProblem) EventLogWriter.Write(logProblem, error: false);

        // H1: never run as SYSTEM from a folder that someone without administrator rights could change
        if (ServiceStartGuard.Refusal(Environment.ProcessPath, exe => FolderTrust.CheckExecutable(exe, TrustScope.FolderTree)) is { } refusal)
        {
            write(refusal);
            EventLogWriter.Write(refusal, error: true);
            Report(Stopped, 0, ErrorAccessDenied);
            return;
        }

        try
        {
            _body = _create!(write, log?.DisabledReason);
            _body.Start();
        }
        catch (Exception ex)
        {
            write("The service could not start: " + ex);
            Report(Stopped, 0, ErrorExceptionInService);
            return;
        }
        Report(Running, 0);
    }

    private static int HandleControl(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        var body = _body;
        if (control == ServiceControlMap.Interrogate) { lock (_statusLock) Report(_state, 0); return NoError; }
        if (body is null) return ErrorCallNotImplemented;

        int session = 0;
        if (control == ServiceControlMap.SessionChange && eventData != IntPtr.Zero)
            session = Marshal.ReadInt32(eventData, 4);   // WTSSESSION_NOTIFICATION { DWORD cbSize; DWORD dwSessionId; }
        var signal = ServiceControlMap.Map(control, eventType, session);

        switch (control)
        {
            case ServiceControlMap.Stop:
            case ServiceControlMap.Preshutdown:
            case ServiceControlMap.Shutdown:
                lock (_statusLock) { if (_state is StopPending or Stopped) return NoError; Report(StopPending, 5000); }
                // answer the control manager at once; the blank runs on another thread and reports "stopped" when done
                Task.Run(() =>
                {
                    try { body.Stop(signal); }
                    catch (Exception ex) { try { body.Log("Stopping failed: " + ex.Message); } catch { } }
                    Report(Stopped, 0);
                });
                return NoError;

            case ServiceControlMap.PowerEventControl:
            case ServiceControlMap.SessionChange:
                // Windows waits for this answer before it sleeps, so the blank happens here (bounded inside)
                try { body.OnSignal(signal); }
                catch (Exception ex) { try { body.Log("Handling a Windows event failed: " + ex.Message); } catch { } }
                return NoError;

            default:
                return ErrorCallNotImplemented;
        }
    }

    private static void Report(int state, int waitHintMs, int exitCode = NoError)
    {
        lock (_statusLock)
        {
            _state = state;
            var status = new ServiceStatus
            {
                ServiceType = ServiceWin32OwnProcess,
                CurrentState = state,
                ControlsAccepted = state == Running ? ServiceControlMap.AcceptedControls : 0,
                Win32ExitCode = exitCode,
                CheckPoint = state is StartPending or StopPending ? ++_checkPoint : 0,
                WaitHint = waitHintMs,
            };
            SetServiceStatus(_handle, ref status);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceTableEntry
    {
        public string? Name;
        public IntPtr Proc;
    }

    private delegate void ServiceMainProc(int argc, IntPtr argv);
    private delegate int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool StartServiceCtrlDispatcherW([In] ServiceTableEntry[] table);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(string name, HandlerEx handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool SetServiceStatus(IntPtr handle, ref ServiceStatus status);
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Glance.Core.Service;

public enum ServiceState { NotInstalled, Stopped, Starting, Stopping, Running, Unknown }

/// <summary>
/// Queries and starts the Glance service from a normal user's process (the editor). Starting needs no prompt: the installer
/// gives interactive users the "start" right (<see cref="ServiceNames.ServiceSddl"/>). Stopping needs an administrator, so it
/// runs Windows' own <c>sc.exe</c> through one UAC prompt; nothing of Glance's runs elevated.
/// </summary>
public static class ServiceControl
{
    /// <summary>Maps a <c>SERVICE_STATUS.dwCurrentState</c> value. Pure.</summary>
    public static ServiceState StateFromCode(int code) => code switch
    {
        1 or 7 => ServiceState.Stopped,     // SERVICE_STOPPED, SERVICE_PAUSED (a paused service does not drive the display)
        2 or 5 => ServiceState.Starting,    // START_PENDING, CONTINUE_PENDING
        3 or 6 => ServiceState.Stopping,    // STOP_PENDING, PAUSE_PENDING
        4 => ServiceState.Running,
        _ => ServiceState.Unknown,
    };

    /// <summary>The service's state, without elevation.</summary>
    public static ServiceState Query(string name = ServiceNames.ServiceName)
    {
        if (!OperatingSystem.IsWindows()) return ServiceState.NotInstalled;
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return ServiceState.Unknown;
        try
        {
            var svc = OpenServiceW(scm, name, ServiceNames.ServiceQueryStatus);
            if (svc == IntPtr.Zero) return Marshal.GetLastWin32Error() == ErrorServiceDoesNotExist ? ServiceState.NotInstalled : ServiceState.Unknown;
            try { return QueryServiceStatus(svc, out var status) ? StateFromCode(status.CurrentState) : ServiceState.Unknown; }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>Starts the service without elevation. Returns an error message, or null when it is starting or running.</summary>
    public static string? Start(string name = ServiceNames.ServiceName)
    {
        if (!OperatingSystem.IsWindows()) return "The Glance service only exists on Windows.";
        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return "Windows' service manager could not be opened.";
        try
        {
            var svc = OpenServiceW(scm, name, ServiceNames.ServiceStart | ServiceNames.ServiceQueryStatus);
            if (svc == IntPtr.Zero)
                return Marshal.GetLastWin32Error() switch
                {
                    ErrorServiceDoesNotExist => "The Glance service is not installed. Install Glance with the installer to use it.",
                    ErrorAccessDenied => "Windows did not allow this account to start the Glance service. Reinstalling Glance restores the permission.",
                    var e => $"The Glance service could not be opened (error {e}).",
                };
            try
            {
                if (StartServiceW(svc, 0, IntPtr.Zero)) return null;
                var err = Marshal.GetLastWin32Error();
                return err == ErrorServiceAlreadyRunning ? null : $"The Glance service could not be started (error {err}). Its log is %ProgramData%\\Glance\\service.log.";
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    /// <summary>"sc.exe stop GlanceService" from System32, through the UAC prompt.</summary>
    public static ProcessStartInfo StopElevatedStartInfo(string name = ServiceNames.ServiceName) =>
        new(Path.Combine(Environment.SystemDirectory, "sc.exe"), "stop " + name)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.SystemDirectory,
        };

    private const int ScManagerConnect = 0x1;
    private const int ErrorAccessDenied = 5, ErrorServiceAlreadyRunning = 1056, ErrorServiceDoesNotExist = 1060;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus { public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, int access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, int access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool StartServiceW(IntPtr service, int argc, IntPtr argv);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}

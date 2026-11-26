using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Glance.Core.Service;

/// <summary>Which Windows session a pipe client runs in, and which session owns the physical console.</summary>
public static class NativeSessions
{
    /// <summary>The client's session, or null if Windows cannot say (for example a remote client).</summary>
    public static int? ClientSessionOf(NamedPipeServerStream pipe)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return GetNamedPipeClientSessionId(pipe.SafePipeHandle.DangerousGetHandle(), out var id) ? (int)id : null; }
        catch { return null; }
    }

    /// <summary>The session attached to the physical console, or null while there is none (it is being switched).</summary>
    public static int? ActiveConsoleSession()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var id = WTSGetActiveConsoleSessionId();
        return id == 0xFFFFFFFF ? null : (int)id;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetNamedPipeClientSessionId(IntPtr pipe, out uint sessionId);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WTSGetActiveConsoleSessionId();
}

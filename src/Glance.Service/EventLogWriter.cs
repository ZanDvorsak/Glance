using System.Runtime.InteropServices;

namespace Glance.Service;

/// <summary>
/// Writes to the Windows Application event log (source "Glance") for the cases where the service log file cannot be used:
/// the service refuses to run (H1), or its log folder is not admin-only (L6). Best effort, never throws.
/// </summary>
internal static class EventLogWriter
{
    public static void Write(string message, bool error)
    {
        try
        {
            var source = RegisterEventSourceW(null, "Glance");
            if (source == IntPtr.Zero) return;
            try { ReportEventW(source, error ? (ushort)1 : (ushort)2, 0, 0, IntPtr.Zero, 1, 0, new[] { message }, IntPtr.Zero); }
            finally { DeregisterEventSource(source); }
        }
        catch { /* best effort */ }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr RegisterEventSourceW(string? server, string source);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool ReportEventW(IntPtr log, ushort type, ushort category, uint eventId, IntPtr userSid, ushort strings, uint dataSize, string[] texts, IntPtr data);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DeregisterEventSource(IntPtr log);
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Glance.Core.Control;

/// <summary>What the editor knows about the tray app of its own session.</summary>
public enum TrayState
{
    /// <summary>Not asked yet.</summary>
    Unknown,
    /// <summary>No tray app answers, and none runs in this session: the editor draws the display itself.</summary>
    None,
    /// <summary>This user's tray app answered: the editor hands it the live theme.</summary>
    Answering,
    /// <summary>A tray app of this user runs in this session but did not answer (starting, busy or stuck).</summary>
    NotAnswering,
    /// <summary>A tray app runs in this session but belongs to another account (or runs as administrator).</summary>
    OtherAccount,
}

/// <summary>
/// Decides whether the tray app is there for the editor (review M13). The answer to a <c>STATUS</c> round trip on this
/// user's own control pipe decides; process names only explain why a tray that runs cannot be used. Before, any
/// <c>Glance.Runner</c> process in the session counted as "the tray runs", even one that never answered the editor
/// (another account's, or one that was still starting), and the editor then sent its edits into nothing.
/// </summary>
public static class TrayPresence
{
    public const string ProcessName = "Glance.Runner";

    /// <param name="statusReply">The tray's reply to <c>STATUS</c>, or null when it did not answer.</param>
    /// <param name="trayProcesses">One entry per tray process in this session: true when it runs as this user, false when as
    /// another account, null when that could not be found out.</param>
    public static TrayState Decide(string? statusReply, IReadOnlyCollection<bool?> trayProcesses)
    {
        if (statusReply is { } r && r.StartsWith("OK", StringComparison.Ordinal)) return TrayState.Answering;
        if (trayProcesses.Any(mine => mine == true)) return TrayState.NotAnswering;
        if (trayProcesses.Count > 0) return TrayState.OtherAccount;
        return TrayState.None;
    }

    /// <summary>Asks the tray, then looks at the processes only if it did not answer. Blocks: never on the UI thread.</summary>
    public static TrayState Probe(Func<string?> askStatus, Func<IReadOnlyCollection<bool?>> trayProcesses)
    {
        string? reply;
        try { reply = askStatus(); } catch { reply = null; }
        if (reply is { } r && r.StartsWith("OK", StringComparison.Ordinal)) return TrayState.Answering;
        IReadOnlyCollection<bool?> found;
        try { found = trayProcesses(); } catch { found = Array.Empty<bool?>(); }
        return Decide(reply, found);
    }

    /// <summary>The tray processes in this Windows session (another user's session does not count), and whether each is this user's.</summary>
    public static IReadOnlyCollection<bool?> TrayProcessesInThisSession()
    {
        int session = Process.GetCurrentProcess().SessionId;
        var found = Process.GetProcessesByName(ProcessName);
        try
        {
            return found.Where(p => { try { return p.SessionId == session; } catch { return false; } })
                        .Select(IsMine).ToList();
        }
        finally { foreach (var p in found) p.Dispose(); }
    }

    /// <summary>
    /// True when the process runs as this user, false when as another account, null when Windows would not say (typically
    /// another account's process, or one that runs as administrator).
    /// </summary>
    public static bool? IsMine(Process process)
    {
        try
        {
            using var handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
            if (handle.IsInvalid) return null;
            if (!OpenProcessToken(handle, TokenQuery, out var token)) return null;
            using (token)
            {
                using var owner = new WindowsIdentity(token.DangerousGetHandle());
                using var me = WindowsIdentity.GetCurrent();
                return owner.User == me.User;
            }
        }
        catch { return null; }
    }

    /// <summary>The status-bar text for a tray app the editor cannot use (or has not found yet), or null.</summary>
    public static string? Explain(TrayState state) => state switch
    {
        TrayState.NotAnswering => "Your Glance tray app is running but does not answer (it may still be starting). Try again in a moment, or restart it from Background service.",
        TrayState.OtherAccount => "A Glance tray app of another account (or one running as administrator) is running in this Windows session, and the editor cannot talk to it. Exit it, then try again.",
        TrayState.Unknown => "Still looking for the Glance tray app; try again in a moment.",
        _ => null,
    };

    private const uint ProcessQueryLimitedInformation = 0x1000, TokenQuery = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}

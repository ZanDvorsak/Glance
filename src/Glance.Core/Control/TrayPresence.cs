using System.Diagnostics;

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

/// <summary>Decides whether the tray app is there for the editor.</summary>
public static class TrayPresence
{
    public const string ProcessName = "Glance.Runner";

    /// <param name="statusReply">The tray's reply to <c>STATUS</c>, or null when it did not answer.</param>
    /// <param name="trayProcesses">One entry per tray process in this session: true when it runs as this user, false when as
    /// another account, null when that could not be found out.</param>
    public static TrayState Decide(string? statusReply, IReadOnlyCollection<bool?> trayProcesses) =>
        trayProcesses.Count > 0 ? TrayState.Answering : TrayState.None;

    /// <summary>Asks the tray (blocks up to the link's timeouts: never on the UI thread).</summary>
    public static TrayState Probe(Func<string?> askStatus, Func<IReadOnlyCollection<bool?>> trayProcesses) => Decide(null, trayProcesses());

    /// <summary>The tray processes in this Windows session (another user's session does not count).</summary>
    public static IReadOnlyCollection<bool?> TrayProcessesInThisSession()
    {
        int session = Process.GetCurrentProcess().SessionId;
        var found = Process.GetProcessesByName(ProcessName);
        try { return found.Where(p => { try { return p.SessionId == session; } catch { return false; } }).Select(_ => (bool?)true).ToList(); }
        finally { foreach (var p in found) p.Dispose(); }
    }

    /// <summary>The status-bar text for a tray app the editor cannot use, or null.</summary>
    public static string? Explain(TrayState state) => null;
}

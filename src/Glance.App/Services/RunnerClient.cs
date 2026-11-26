// Every runner command is built with ControlCommand, so make it available to the whole editor.
global using Glance.Core.Control;
using System.Diagnostics;
using Glance.Core.Service;

namespace Glance.App.Services;

/// <summary>
/// Talks to this session's tray app (Glance.Runner, normal user rights) over its control pipe. The tray app draws the theme
/// and sends it to the Glance service, so while it runs the editor hands it the live theme instead of opening the display.
/// <para>
/// Review M5: every command goes through one <see cref="TrayLink"/>, one connection in order, so a <c>LIVE_CLEAR</c> can never
/// overtake the <c>LIVE</c> before it, and the tray sees the editor go away when that connection closes.
/// Review M13: whether the tray app is there is decided by a <c>STATUS</c> round trip (<see cref="TrayPresence"/>), not by
/// a process name.
/// </para>
/// </summary>
public static class RunnerClient
{
    /// <summary>Per Windows session and user, so two signed-in users never reach each other's tray app.</summary>
    public static string PipeName => ServiceNames.TrayControlPipeForThisSession();

    private static readonly Lazy<TrayLink> LinkLazy = new(() => new TrayLink(PipeName));
    private static TrayLink Link => LinkLazy.Value;

    private static int _presence = (int)TrayState.Unknown;
    private static int _probing;
    private static long _lastProbe;   // Environment.TickCount64 of the last probe start

    /// <summary>What the last probe found (<see cref="TrayState.Unknown"/> before the first one). Never blocks.</summary>
    public static TrayState Presence => (TrayState)Volatile.Read(ref _presence);

    /// <summary>
    /// Asks the tray app now and remembers the answer. Blocks for up to the link's timeouts, so never call it on the UI thread.
    /// </summary>
    public static TrayState Probe()
    {
        var state = TrayPresence.Probe(() => Link.Send(ControlCommand.Status), TrayPresence.TrayProcessesInThisSession);
        Volatile.Write(ref _presence, (int)state);
        return state;
    }

    /// <summary>For a UI timer: starts a probe in the background when the last one is 2 s old or more. Never blocks.</summary>
    public static void RefreshPresence()
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _lastProbe) < 2000) return;
        if (Interlocked.Exchange(ref _probing, 1) != 0) return;
        Interlocked.Exchange(ref _lastProbe, Environment.TickCount64);
        Task.Run(() =>
        {
            try { Probe(); }
            catch { /* the next tick tries again */ }
            finally { Volatile.Write(ref _probing, 0); }
        });
    }

    /// <summary>
    /// Sends one command and waits for the reply (null if the tray app could not be reached or did not answer in time).
    /// Blocks: call it off the UI thread. There is deliberately no overload that takes a raw string: the wire format lives
    /// only in <see cref="ControlCommand"/>.
    /// </summary>
    public static string? Send(ControlCommand command) => Link.Send(command);

    /// <summary>Sends one command in order behind the ones before it; the task gives the reply.</summary>
    public static Task<string?> SendAsync(ControlCommand command) => Link.SendAsync(command);

    /// <summary>Sends one command in order behind the ones before it, without waiting for the reply.</summary>
    public static void Post(ControlCommand command) => Link.Post(command);

    /// <summary>
    /// Shows the editor's current (possibly unsaved) theme on the display via the tray app. A null folder is fine (a new theme).
    /// The task gives the tray app's reply (null when it did not answer); <see cref="LiveSendGate"/> turns it into a status message.
    /// </summary>
    public static Task<string?> SendLiveAsync(string snapshotJson, string? baseFolder) => SendAsync(ControlCommand.Live(snapshotJson, baseFolder));

    /// <summary>When the editor exits: delivers what is still queued (bounded) and hangs up. Blocks: call it off the UI thread.</summary>
    public static bool CloseLink(TimeSpan timeout) => !LinkLazy.IsValueCreated || Link.Close(timeout);

    /// <summary>Starts the tray app with the editor's own (normal) rights.</summary>
    public static bool Launch()
    {
        var path = AppPaths.FindTrayExe();
        if (path is null) return false;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! }); return true; }
        catch { return false; }
    }
}

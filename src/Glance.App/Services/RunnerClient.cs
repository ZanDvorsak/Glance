// Every runner command is built with ControlCommand, so make it available to the whole editor.
global using Glance.Core.Control;
using System.Diagnostics;
using Glance.Core.Service;
using Glance.Core.Threading;

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

    /// <summary>
    /// Asks the tray app now: a <c>STATUS</c> round trip, and only without an answer a look at the tray processes in this
    /// session (M13). Blocks for up to the link's timeouts. The editor keeps the last answer in its own BackgroundProbe.
    /// </summary>
    [WorkerThread]   // a pipe round trip, and a process list when the tray does not answer
    public static TrayState Probe() => TrayPresence.Probe(() => Link.Send(ControlCommand.Status), TrayPresence.TrayProcessesInThisSession);

    /// <summary>
    /// Sends one command and waits for the reply (null if the tray app could not be reached or did not answer in time).
    /// Blocks: call it off the UI thread. There is deliberately no overload that takes a raw string: the wire format lives
    /// only in <see cref="ControlCommand"/>.
    /// </summary>
    [WorkerThread]   // waits for the queue and one round trip (bounded by TrayLink.SendTimeout)
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
    [WorkerThread]   // waits up to the timeout for the queue to drain
    public static bool CloseLink(TimeSpan timeout) => !LinkLazy.IsValueCreated || Link.Close(timeout);

    /// <summary>Starts the tray app with the editor's own (normal) rights.</summary>
    [WorkerThread]   // looks for the exe (in a development build also the build output) and starts it through ShellExecute
    public static bool Launch()
    {
        var path = AppPaths.FindTrayExe();
        if (path is null) return false;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! }); return true; }
        catch { return false; }
    }
}

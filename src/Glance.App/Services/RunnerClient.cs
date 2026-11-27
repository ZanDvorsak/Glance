// Every runner command is built with ControlCommand, so make it available to the whole editor.
global using Glance.Core.Control;
using System.Diagnostics;
using Glance.Core.Service;
using Glance.Core.Threading;

namespace Glance.App.Services;

/// <summary>
/// Talks to this session's tray app (Glance.Runner, normal user rights) over its control pipe. The tray app draws the theme
/// and sends it to the Glance service, so while it runs the editor hands it the live theme instead of opening the display.
/// </summary>
public static class RunnerClient
{
    /// <summary>Per Windows session, so two signed-in users never reach each other's tray app.</summary>
    public static string PipeName => ServiceNames.TrayControlPipeForThisSession();

    /// <summary>A tray app in this Windows session (another user's tray app does not count).</summary>
    public static bool IsRunning
    {
        get
        {
            int session = Process.GetCurrentProcess().SessionId;
            var found = Process.GetProcessesByName("Glance.Runner");
            try { return found.Any(p => { try { return p.SessionId == session; } catch { return false; } }); }
            finally { foreach (var p in found) p.Dispose(); }
        }
    }

    /// <summary>
    /// Sends one command and returns the reply, or null if the tray app could not be reached or did not answer in time.
    /// There is deliberately no overload that takes a raw string: the wire format lives only in <see cref="ControlCommand"/>.
    /// </summary>
    public static string? Send(ControlCommand command) => ControlPipeClient.Send(PipeName, command);

    /// <summary>
    /// Shows the editor's current (possibly unsaved) theme on the display via the tray app. A null folder is fine (a new theme).
    /// Returns the tray app's reply (null when it did not answer); <see cref="LiveSendGate"/> turns it into a status message.
    /// </summary>
    public static string? SendLive(string snapshotJson, string? baseFolder) => Send(ControlCommand.Live(snapshotJson, baseFolder));

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

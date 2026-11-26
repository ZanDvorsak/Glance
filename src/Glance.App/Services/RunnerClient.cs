// Every runner command is built with ControlCommand, so make it available to the whole editor.
global using Glance.Core.Control;
using System.Diagnostics;

namespace Glance.App.Services;

/// <summary>Talks to the elevated Glance.Runner background app over its control pipe.</summary>
public static class RunnerClient
{
    public const string PipeName = "Glance.Runner.Control";

    public static bool IsRunning => Process.GetProcessesByName("Glance.Runner").Length > 0;

    /// <summary>
    /// Sends one command and returns the reply, or null if the runner could not be reached or did not answer in time.
    /// There is deliberately no overload that takes a raw string: the wire format lives only in <see cref="ControlCommand"/>.
    /// </summary>
    public static string? Send(ControlCommand command) => ControlPipeClient.Send(PipeName, command);

    /// <summary>
    /// Shows the editor's current (possibly unsaved) theme on the display via the runner. A null folder is fine (a new theme).
    /// Returns the runner's reply (null when it did not answer); <see cref="LiveSendGate"/> turns it into a status message.
    /// </summary>
    public static string? SendLive(string snapshotJson, string? baseFolder) => Send(ControlCommand.Live(snapshotJson, baseFolder));

    /// <summary>Starts the runner; Windows shows a UAC prompt because it needs administrator rights.</summary>
    public static bool Launch()
    {
        var path = SensorHub.FindRunnerExe();
        if (path is null) return false;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(path)! }); return true; }
        catch { return false; } // UAC declined
    }
}

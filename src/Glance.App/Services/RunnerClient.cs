using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Glance.Core.Control;

namespace Glance.App.Services;

/// <summary>Talks to the elevated Glance.Runner background app over its control pipe.</summary>
public static class RunnerClient
{
    public const string PipeName = "Glance.Runner.Control";

    public static bool IsRunning => Process.GetProcessesByName("Glance.Runner").Length > 0;

    /// <summary>Sends one command and returns the reply, or null if the runner could not be reached.</summary>
    public static string? Send(string command, int timeoutMs = 800)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
            pipe.Connect(timeoutMs);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            writer.WriteLine(command);
            var task = reader.ReadLineAsync();
            return task.Wait(timeoutMs) ? task.Result : null;
        }
        catch { return null; }
    }

    /// <summary>Sends one command built with <see cref="ControlCommand"/>; returns the reply, or null if the runner could not be reached.</summary>
    public static string? Send(ControlCommand command, int timeoutMs = 800) => Send(command.Encode(), timeoutMs);

    /// <summary>Shows the editor's current (possibly unsaved) theme on the display via the runner. A null folder is fine (a new theme).</summary>
    public static bool SendLive(string snapshotJson, string? baseFolder) =>
        Send(ControlCommand.Live(snapshotJson, baseFolder), 2000) is { } r && r.StartsWith("OK");

    /// <summary>Starts the runner; Windows shows a UAC prompt because it needs administrator rights.</summary>
    public static bool Launch()
    {
        var path = SensorHub.FindRunnerExe();
        if (path is null) return false;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(path)! }); return true; }
        catch { return false; } // UAC declined
    }
}

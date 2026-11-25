using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using MiniLcd.Core.Themes;

namespace MiniLcd.App.Services;

/// <summary>Talks to the elevated MiniLcd.Runner background app over its control pipe.</summary>
public static class RunnerClient
{
    public const string PipeName = "MiniLcd.Runner.Control";

    public static bool IsRunning => Process.GetProcessesByName("MiniLcd.Runner").Length > 0;

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

    /// <summary>Shows the editor's current (possibly unsaved) theme on the display via the runner.</summary>
    public static bool SendLive(string snapshotJson, string? baseFolder)
    {
        var folder = Convert.ToBase64String(Encoding.UTF8.GetBytes(baseFolder ?? ""));
        var json = Convert.ToBase64String(Encoding.UTF8.GetBytes(snapshotJson));
        return Send($"LIVE {folder} {json}", 2000) is { } r && r.StartsWith("OK");
    }

    /// <summary>Starts the runner; Windows shows a UAC prompt because it needs administrator rights.</summary>
    public static bool Launch()
    {
        var path = SensorHub.FindRunnerExe();
        if (path is null) return false;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(path)! }); return true; }
        catch { return false; } // UAC declined
    }
}

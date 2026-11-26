using Glance.Core.Settings;

namespace Glance.Runner;

/// <summary>Appends to %AppData%\Glance\runner.log (the same file and format as the tray runner's log).</summary>
internal static class RunnerLog
{
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "runner.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* logging must never break the runner */ }
    }
}

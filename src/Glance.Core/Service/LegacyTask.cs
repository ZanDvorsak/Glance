using System.Diagnostics;
using Glance.Core.Setup;

namespace Glance.Core.Service;

/// <summary>
/// Glance 0.1's "Glance Runner" logon task. It starts Glance.Runner.exe with highest privileges and the user's environment, so
/// a leftover one would run the new tray app elevated (review M3). The installer deletes it; the service deletes it again at
/// every start, as SYSTEM, with Windows' own schtasks.exe.
/// </summary>
public static class LegacyTask
{
    public const string Name = AutostartTask.TaskName;

    private static ProcessStartInfo Schtasks(string args) =>
        new(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory,
        };

    /// <summary>How the service (already SYSTEM) deletes it: no prompt, no shell.</summary>
    public static ProcessStartInfo ServiceDeleteStartInfo() => Schtasks(AutostartTask.SchtasksDeleteArguments);

    /// <summary>Whether it exists (exit code 0).</summary>
    public static ProcessStartInfo QueryStartInfo() => Schtasks($"/Query /TN \"{Name}\"");
}

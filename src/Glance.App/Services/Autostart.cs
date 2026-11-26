using System.Diagnostics;
using Glance.Core.Setup;

namespace Glance.App.Services;

/// <summary>
/// The scheduled task "Glance Runner" that Glance 0.1.x used to start its elevated runner at sign-in. Glance now has a
/// Windows service instead (and the installer removes the task); this only finds a leftover task, for example from a
/// portable copy, and removes it with Windows' own schtasks.exe (one UAC prompt). Nothing of Glance's is ever elevated.
/// </summary>
public static class Autostart
{
    public const string TaskName = AutostartTask.TaskName;

    /// <summary>Querying a task does not need elevation. Starts schtasks.exe: call it off the UI thread.</summary>
    public static bool IsInstalled() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    /// <summary>Runs schtasks.exe from System32 without elevation and returns its exit code (-1 on failure).</summary>
    private static int RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            // Drain both pipes at once: reading one to the end while the other fills up can deadlock.
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return -1; }
            Task.WaitAll(new Task[] { o, e }, 2000);
            return p.ExitCode;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Removes the old logon task with Windows' own schtasks.exe through the UAC prompt.
    /// Returns an error message, or null when the task is gone afterwards (also when it was already gone).
    /// </summary>
    public static string? Remove()
    {
        var result = RunElevated(AutostartTask.RemoveStartInfo(), code => code == 0 ? null : $"schtasks exited with code {code}.");
        if (!IsInstalled()) return null;            // gone, whatever schtasks said
        return result ?? "The task is still present.";
    }

    /// <summary>Starts a Windows program through the UAC prompt, waits for it and maps its exit code to a message.</summary>
    public static string? RunElevated(ProcessStartInfo psi, Func<int, string?> describe)
    {
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return "The program could not be started.";
            if (!p.WaitForExit(60000)) return "Windows did not finish in time.";
            return describe(p.ExitCode);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "The Windows permission prompt was declined."; } // ERROR_CANCELLED
        catch (Exception ex) { return ex.Message; }
    }
}

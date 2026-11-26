using System.Diagnostics;
using System.Security.Principal;
using Glance.Core.Security;
using Glance.Core.Setup;

namespace Glance.App.Services;

/// <summary>
/// Starts the runner at login through a Windows scheduled task set to "run with highest privileges",
/// which is how an administrator-level program can start automatically without a UAC prompt each time.
/// The elevated runner registers and removes the task itself (<c>--install-autostart</c> / <c>--remove-autostart</c>),
/// building the definition in memory, so the editor never hands an elevated process a file it could have swapped.
/// Creating or removing the task needs administrator rights once (one UAC prompt).
/// </summary>
public static class Autostart
{
    public const string TaskName = AutostartTask.TaskName;

    /// <summary>Querying a task does not need elevation.</summary>
    public static bool IsInstalled()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            // Drain both pipes at once: reading one to the end while the other fills up can deadlock.
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return false; }
            Task.WaitAll(new Task[] { stdout, stderr }, 2000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Whether the runner's folder is safe to run elevated at every sign-in (only administrators can change it).
    /// Reads file permissions, so call it off the UI thread.
    /// </summary>
    public static TrustVerdict CheckRunnerFolder(string runnerExe) =>
        OperatingSystem.IsWindows() ? FolderTrust.Check(Path.GetDirectoryName(Path.GetFullPath(runnerExe))!)
                                    : TrustVerdict.No("Auto-start is only available on Windows.");

    /// <summary>The message shown when auto-start is refused because of the folder's permissions.</summary>
    public static string UntrustedMessage(TrustVerdict verdict) =>
        "Auto-start is not available for this copy of Glance because its folder can be changed without administrator rights: " +
        $"{verdict.Reason} Auto-start runs the runner as administrator at every sign-in, so any program could take over the PC " +
        "by replacing its files. Install Glance with the installer (into Program Files) to use auto-start.";

    /// <summary>Creates the logon task for the current user. Returns an error message, or null on success.</summary>
    public static string? Install(string runnerExe)
    {
        if (!OperatingSystem.IsWindows()) return "Auto-start is only available on Windows.";
        try
        {
            var trust = CheckRunnerFolder(runnerExe);
            if (!trust.Trusted) return UntrustedMessage(trust);

            // The interactive user (this editor's user), which may differ from the admin account that approves the prompt.
            var user = WindowsIdentity.GetCurrent().Name;
            if (AutostartTask.ValidateUserName(user) is { } err) return $"Cannot set up auto-start for '{user}': {err}";

            var result = RunRunnerElevated(runnerExe, AutostartTask.InstallArguments(user));
            return result ?? (IsInstalled() ? null : "The task was not created.");
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Removes the logon task. Returns an error message, or null on success.</summary>
    public static string? Remove(string runnerExe)
    {
        var result = RunRunnerElevated(runnerExe, AutostartTask.RemoveArguments());
        return result ?? (IsInstalled() ? "The task is still present." : null);
    }

    /// <summary>Starts the runner in its auto-start mode through the UAC prompt and maps its exit code to a message.</summary>
    private static string? RunRunnerElevated(string runnerExe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(runnerExe, args)
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(runnerExe)!,
            });
            if (p is null) return "The runner could not be started.";
            // The prompt has been answered by the time Start returns; the work itself takes a moment.
            if (!p.WaitForExit(60000)) return "The runner did not finish changing auto-start in time.";
            return AutostartTask.DescribeExit(p.ExitCode);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "The Windows permission prompt was declined."; } // ERROR_CANCELLED
        catch (Exception ex) { return ex.Message; }
    }
}

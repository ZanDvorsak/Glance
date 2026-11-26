using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Glance.Core.Security;
using Glance.Core.Setup;

namespace Glance.App.Services;

/// <summary>
/// Starts the runner at login through a Windows scheduled task set to "run with highest privileges",
/// which is how an administrator-level program can start automatically without a UAC prompt each time.
/// The elevated runner registers the task itself (<c>--install-autostart</c>), building the definition in memory, so
/// the editor never hands an elevated process a file it could have swapped. Removing uses Windows' own schtasks.exe
/// with a fixed command line, so it never elevates a runner copy. Each change asks for permission once (one UAC prompt).
/// </summary>
public static class Autostart
{
    public const string TaskName = AutostartTask.TaskName;

    /// <summary>Querying a task does not need elevation.</summary>
    public static bool IsInstalled() => RunSchtasks($"/Query /TN \"{TaskName}\"", out _) == 0;

    /// <summary>The registered task's XML (readable without elevation), or null when there is none or it cannot be read.</summary>
    public static string? ReadTaskXml() => RunSchtasks($"/Query /TN \"{TaskName}\" /XML ONE", out var xml) == 0 ? xml : null;

    /// <summary>Runs schtasks.exe from System32 without elevation and returns its exit code (-1 on failure).</summary>
    private static int RunSchtasks(string args, out string stdout)
    {
        stdout = "";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            // Drain both pipes at once: reading one to the end while the other fills up can deadlock.
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return -1; }
            Task.WaitAll(new Task[] { o, e }, 2000);
            stdout = o.IsCompletedSuccessfully ? o.Result : "";
            return p.ExitCode;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Whether a runner exe is safe to run elevated at every sign-in (only administrators can change it and everything
    /// in its folder). Reads file permissions, so call it off the UI thread.
    /// </summary>
    public static TrustVerdict CheckRunner(string runnerExe) =>
        OperatingSystem.IsWindows() ? FolderTrust.CheckExecutable(runnerExe, TrustScope.FolderTree)
                                    : TrustVerdict.No("Auto-start is only available on Windows.");

    /// <summary>What the existing task does, judged against this copy (M1). Reads the task and file permissions: off the UI thread.</summary>
    public static AutostartStatus Status(string? currentRunnerExe)
    {
        var xml = ReadTaskXml();
        bool exists = xml is not null || IsInstalled();
        return AutostartTask.Assess(exists, xml, currentRunnerExe, CheckRunner);
    }

    /// <summary>The message shown when auto-start is refused because of the folder's permissions.</summary>
    public static string UntrustedMessage(TrustVerdict verdict) =>
        "Auto-start is not available for this copy of Glance because its folder can be changed without administrator rights: " +
        $"{verdict.Reason} Auto-start runs the runner as administrator at every sign-in, so any program could take over the PC " +
        "by replacing its files. Install Glance with the installer (into Program Files) to use auto-start.";

    /// <summary>Whether this editor's user is an administrator, so Windows can start the runner for them at sign-in (M2).</summary>
    public static bool UserCanAutostart()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var me = WindowsIdentity.GetCurrent();
        var elevation = TokenElevation.Default;
        if (GetTokenInformation(me.Token, 18 /* TokenElevationType */, out int type, sizeof(int), out _)) elevation = (TokenElevation)type;
        return AutostartTask.UserCanAutostart(elevation, new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator));
    }

    /// <summary>Creates the logon task for the current user. Returns an error message, or null on success.</summary>
    public static string? Install(string runnerExe)
    {
        if (!OperatingSystem.IsWindows()) return "Auto-start is only available on Windows.";
        try
        {
            var trust = CheckRunner(runnerExe);
            if (!trust.Trusted) return UntrustedMessage(trust);
            if (!UserCanAutostart()) return AutostartTask.UserNotAdminMessage;

            // The interactive user (this editor's user), which may differ from the admin account that approves the prompt.
            string user;
            using (var me = WindowsIdentity.GetCurrent()) user = me.Name;
            if (AutostartTask.ValidateUserName(user) is { } err) return $"Cannot set up auto-start for '{user}': {err}";

            var result = RunElevated(new ProcessStartInfo(runnerExe, AutostartTask.InstallArguments(user))
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(runnerExe)!,
            }, AutostartTask.DescribeExit);
            return result ?? (IsInstalled() ? null : "The task was not created.");
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>
    /// Removes the logon task with Windows' own schtasks.exe (never the runner, which might be an untrusted copy).
    /// Returns an error message, or null when the task is gone afterwards (also when it was already gone).
    /// </summary>
    public static string? Remove()
    {
        var result = RunElevated(AutostartTask.RemoveStartInfo(), code => code == 0 ? null : $"schtasks exited with code {code}.");
        if (!IsInstalled()) return null;            // gone, whatever schtasks said
        return result ?? "The task is still present.";
    }

    /// <summary>Starts a program through the UAC prompt, waits for it and maps its exit code to a message.</summary>
    private static string? RunElevated(ProcessStartInfo psi, Func<int, string?> describe)
    {
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return "The program could not be started.";
            // The prompt has been answered by the time Start returns; the work itself takes a moment.
            if (!p.WaitForExit(60000)) return "Changing auto-start did not finish in time.";
            return describe(p.ExitCode);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "The Windows permission prompt was declined."; } // ERROR_CANCELLED
        catch (Exception ex) { return ex.Message; }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
}

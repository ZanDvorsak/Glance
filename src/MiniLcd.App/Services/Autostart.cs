using System.Diagnostics;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace MiniLcd.App.Services;

/// <summary>
/// Starts the runner at login through a Windows scheduled task set to "run with highest privileges",
/// which is how an administrator-level program can start automatically without a UAC prompt each time.
/// Creating or removing the task needs administrator rights once (one UAC prompt).
/// </summary>
public static class Autostart
{
    public const string TaskName = "MiniLcd Runner";

    /// <summary>Querying a task does not need elevation.</summary>
    public static bool IsInstalled()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Creates the logon task for the current user. Returns an error message, or null on success.</summary>
    public static string? Install(string runnerExe)
    {
        var xmlPath = Path.Combine(Path.GetTempPath(), "MiniLcdRunnerTask.xml");
        try
        {
            File.WriteAllText(xmlPath, BuildXml(runnerExe), Encoding.Unicode); // schtasks expects UTF-16
            var err = RunElevated($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            return err ?? (IsInstalled() ? null : "The task was not created.");
        }
        catch (Exception ex) { return ex.Message; }
        finally { try { File.Delete(xmlPath); } catch { } }
    }

    public static string? Remove()
    {
        var err = RunElevated($"/Delete /TN \"{TaskName}\" /F");
        return err ?? (IsInstalled() ? "The task is still present." : null);
    }

    private static string? RunElevated(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })!;
            p.WaitForExit(30000);
            return p.ExitCode == 0 ? null : $"schtasks exited with code {p.ExitCode}.";
        }
        catch (System.ComponentModel.Win32Exception) { return "The Windows permission prompt was declined."; }
        catch (Exception ex) { return ex.Message; }
    }

    private static string BuildXml(string exe)
    {
        var user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        var path = SecurityElement.Escape(exe);
        var dir = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "");
        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>Shows your Mini LCD theme on the USB display and provides hardware sensors.</Description></RegistrationInfo>
          <Triggers>
            <LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author"><Exec><Command>{path}</Command><WorkingDirectory>{dir}</WorkingDirectory></Exec></Actions>
        </Task>
        """;
    }
}

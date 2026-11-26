using System.Runtime.InteropServices;
using System.Security.Principal;
using Glance.Core.Security;
using Glance.Core.Setup;

namespace Glance.Runner;

/// <summary>
/// <c>Glance.Runner.exe --install-autostart --user DOMAIN\name</c> and <c>--remove-autostart</c>.
/// The runner is already elevated (its manifest requires it), so it registers the logon task itself through the
/// Task Scheduler COM API. The task definition is built in memory from this process's own path and never touches a
/// file, so nothing a normal process can write is read after the UAC prompt. No tray icon, display or mutex in this mode.
/// </summary>
internal static class AutostartInstaller
{
    private const int TaskCreateOrUpdate = 6;          // TASK_CREATE_OR_UPDATE
    private const int TaskLogonInteractiveToken = 3;   // TASK_LOGON_INTERACTIVE_TOKEN
    private const int ErrorFileNotFound = unchecked((int)0x80070002);

    public static int Run(AutostartRequest request)
    {
        try
        {
            if (!IsElevated()) return AutostartTask.Exit.NotElevated;
            return request.Mode switch
            {
                AutostartMode.Install => Install(request.User!),
                AutostartMode.Remove => Remove(),
                _ => AutostartTask.Exit.BadArguments,
            };
        }
        catch { return AutostartTask.Exit.Unexpected; }
    }

    private static int Install(string user)
    {
        // The task must start for the person at this screen (the editor's user), not for the admin account that
        // approved the UAC prompt, so the name has to resolve to this session's signed-in user.
        if (AutostartTask.ValidateUserName(user) is not null) return AutostartTask.Exit.InvalidUser;
        SecurityIdentifier requested;
        try { requested = (SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier)); }
        catch { return AutostartTask.Exit.InvalidUser; }
        var sessionUser = SessionUserSid();
        if (sessionUser is null || !sessionUser.Equals(requested)) return AutostartTask.Exit.InvalidUser;
        string account;
        try { account = requested.Translate(typeof(NTAccount)).Value; } // canonical DOMAIN\name
        catch { return AutostartTask.Exit.InvalidUser; }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !Path.IsPathFullyQualified(exe)) return AutostartTask.Exit.RegisterFailed;
        if (!FolderTrust.Check(Path.GetDirectoryName(exe)!).Trusted) return AutostartTask.Exit.FolderNotTrusted;

        var xml = AutostartTask.BuildXml(exe, account);
        dynamic root;
        try
        {
            root = RootFolder();
            root.RegisterTask(AutostartTask.TaskName, xml, TaskCreateOrUpdate, account, null, TaskLogonInteractiveToken, null);
        }
        catch { return AutostartTask.Exit.RegisterFailed; }

        // Read it back and make sure it runs exactly this exe; anything else is removed again.
        string? registered = null;
        try { registered = (string)root.GetTask(AutostartTask.TaskName).Xml; } catch { }
        if (AutostartTask.VerifyRegistered(registered, exe) is null) return AutostartTask.Exit.Ok;
        try { root.DeleteTask(AutostartTask.TaskName, 0); } catch { }
        return AutostartTask.Exit.Mismatch;
    }

    private static int Remove()
    {
        try { RootFolder().DeleteTask(AutostartTask.TaskName, 0); return AutostartTask.Exit.Ok; }
        catch (COMException ex) when (ex.HResult == ErrorFileNotFound) { return AutostartTask.Exit.Ok; } // already gone
        catch { return AutostartTask.Exit.RemoveFailed; }
    }

    private static dynamic RootFolder()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service.GetFolder("\\");
    }

    private static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>The user signed in to this process's Windows session (who sees the screen), or null.</summary>
    private static SecurityIdentifier? SessionUserSid()
    {
        var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        var name = SessionString(session, WtsUserName);
        var domain = SessionString(session, WtsDomainName);
        if (string.IsNullOrEmpty(name)) return null;
        try
        {
            var account = string.IsNullOrEmpty(domain) ? new NTAccount(name) : new NTAccount(domain, name);
            return (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
        }
        catch { return null; }
    }

    private const int WtsUserName = 5, WtsDomainName = 7;

    private static string? SessionString(int session, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}

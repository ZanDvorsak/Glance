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
/// Every outcome is written to runner.log. The decisions live in <see cref="AutostartTask"/> so they can be tested.
/// </summary>
internal static class AutostartInstaller
{
    private const int TaskCreateOrUpdate = 6;          // TASK_CREATE_OR_UPDATE
    private const int TaskLogonInteractiveToken = 3;   // TASK_LOGON_INTERACTIVE_TOKEN

    public static int Run(AutostartRequest request)
    {
        int code;
        try
        {
            code = !IsElevated() ? AutostartTask.Exit.NotElevated : request.Mode switch
            {
                AutostartMode.Install => Install(request.User!),
                AutostartMode.Remove => Remove(),
                _ => AutostartTask.Exit.BadArguments,
            };
        }
        catch (Exception ex)
        {
            RunnerLog.Write($"Auto-start: unexpected {ex.GetType().Name} (HRESULT 0x{ex.HResult:X8}): {ex.Message}");
            code = AutostartTask.Exit.Unexpected;
        }
        RunnerLog.Write($"Auto-start {(request.Mode == AutostartMode.Install ? "install" : "remove")}: exit {code}" +
                        (AutostartTask.DescribeExit(code) is { } msg ? $" ({msg})" : " (done)"));
        return code;
    }

    private static int Install(string user)
    {
        // The task must start for the person at this screen (the editor's user), not for the admin account that
        // approved the UAC prompt, so the name has to resolve to this session's signed-in user.
        if (AutostartTask.ValidateUserName(user) is not null) return AutostartTask.Exit.InvalidUser;
        SecurityIdentifier requested;
        string account;
        try
        {
            requested = (SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier));
            account = requested.Translate(typeof(NTAccount)).Value; // canonical DOMAIN\name
        }
        catch (Exception ex)
        {
            RunnerLog.Write($"Auto-start: could not look up '{user}': {ex.GetType().Name} (HRESULT 0x{ex.HResult:X8}).");
            return AutostartTask.Exit.UserLookupFailed;
        }

        string elevated;
        using (var me = WindowsIdentity.GetCurrent()) elevated = me.User!.Value;
        // Only needed when someone else approved the prompt; then the signed-in user must be an admin (M2).
        bool? isAdmin = string.Equals(elevated, requested.Value, StringComparison.OrdinalIgnoreCase) ? true : IsLocalAdministrator(account);
        var userCheck = AutostartTask.CheckInstallUser(requested.Value, SessionUserSid()?.Value, elevated, isAdmin);
        if (userCheck != AutostartTask.Exit.Ok) return userCheck;

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !Path.IsPathFullyQualified(exe)) return AutostartTask.Exit.RegisterFailed;
        var trust = FolderTrust.CheckExecutable(exe, TrustScope.FolderTree);
        if (!trust.Trusted)
        {
            RunnerLog.Write($"Auto-start: refused, {trust.Reason}");
            return AutostartTask.Exit.FolderNotTrusted;
        }

        var xml = AutostartTask.BuildXml(exe, account);
        dynamic root;
        try
        {
            root = RootFolder();
            root.RegisterTask(AutostartTask.TaskName, xml, TaskCreateOrUpdate, account, null, TaskLogonInteractiveToken, null);
        }
        catch (Exception ex)
        {
            RunnerLog.Write($"Auto-start: RegisterTask failed: {ex.GetType().Name} (HRESULT 0x{ex.HResult:X8}): {ex.Message}");
            return AutostartTask.Exit.RegisterFailed;
        }

        // Read it back and make sure it is exactly what was meant (L5); anything else is removed again.
        string? registered = null;
        try { registered = (string)root.GetTask(AutostartTask.TaskName).Xml; } catch { }
        var problem = AutostartTask.VerifyRegistered(registered, exe, requested.Value, ToSid);
        if (problem is null) return AutostartTask.Exit.Ok;
        RunnerLog.Write($"Auto-start: the registered task did not match ({problem}); removing it.");
        try { root.DeleteTask(AutostartTask.TaskName, 0); } catch { }
        return AutostartTask.Exit.Mismatch;
    }

    private static int Remove()
    {
        try { RootFolder().DeleteTask(AutostartTask.TaskName, 0); return AutostartTask.Exit.Ok; }
        catch (Exception ex)
        {
            var code = AutostartTask.ClassifyRemoveError(ex); // "not found" means it is already gone (L1)
            if (code != AutostartTask.Exit.Ok) RunnerLog.Write($"Auto-start: DeleteTask failed: {ex.GetType().Name} (HRESULT 0x{ex.HResult:X8}).");
            return code;
        }
    }

    private static string? ToSid(string name)
    {
        try { return name.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) ? new SecurityIdentifier(name).Value : new NTAccount(name).Translate(typeof(SecurityIdentifier)).Value; }
        catch { return null; }
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
        int session;
        using (var self = System.Diagnostics.Process.GetCurrentProcess()) session = self.SessionId;
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

    /// <summary>
    /// Whether <paramref name="account"/> is in the local Administrators group, directly or through other groups
    /// (NetUserGetLocalGroups with LG_INCLUDE_INDIRECT). Null when it cannot be determined.
    /// </summary>
    private static bool? IsLocalAdministrator(string account)
    {
        const int LgIncludeIndirect = 1, MaxPreferredLength = -1;
        int status = NetUserGetLocalGroups(null, account, 0, LgIncludeIndirect, out var buffer, MaxPreferredLength, out var read, out _);
        if (status != 0) { RunnerLog.Write($"Auto-start: NetUserGetLocalGroups failed for '{account}' ({status})."); return null; }
        try
        {
            var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            for (int i = 0; i < read; i++)
            {
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, i * IntPtr.Size));
                if (name is null) continue;
                try { if (new NTAccount(name).Translate(typeof(SecurityIdentifier)).Equals(admins)) return true; } catch { }
            }
            return false;
        }
        finally { NetApiBufferFree(buffer); }
    }

    private const int WtsUserName = 5, WtsDomainName = 7;

    private static string? SessionString(int session, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out var buffer, out _)) return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { WTSFreeMemory(buffer); }
    }

    // System DLLs only: never search the application folder or PATH from an elevated process.
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NetUserGetLocalGroups(string? server, string user, int level, int flags, out IntPtr buffer, int maxLength, out int entriesRead, out int totalEntries);

    [DllImport("netapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NetApiBufferFree(IntPtr buffer);
}

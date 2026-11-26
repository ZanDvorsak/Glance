using System.Security.AccessControl;
using System.Security.Principal;

namespace Glance.Core.Service;

/// <summary>
/// Names and fixed settings shared by the Glance service, the tray, the editor, the installer and the tests.
/// The installer (<c>installer/Glance.iss</c>) repeats some of them as text; a test checks that both say the same.
/// </summary>
public static class ServiceNames
{
    /// <summary>The Windows service's short name (<c>sc.exe</c>, the registry key).</summary>
    public const string ServiceName = "GlanceService";

    public const string DisplayName = "Glance display service";

    public const string Description =
        "Drives the Glance USB display and reads the hardware sensors. Themes are drawn by the signed-in user's Glance apps.";

    /// <summary>Frames and display commands from the user's tray or editor (binary, see <see cref="DisplayWire"/>).</summary>
    public const string DisplayPipe = "Glance.Service.Display";

    /// <summary>The read-only sensor feed, one JSON line per second. The same name the editor has always read.</summary>
    public const string SensorPipe = Sensors.PipeSensorClient.PipeName;

    /// <summary>The control pipe of Glance 0.1.1's elevated runner. Only the installer still talks to it (to ask it to exit).</summary>
    public const string LegacyRunnerPipe = "Glance.Runner.Control";

    /// <summary>
    /// The tray's control pipe for the editor of the same user in the same Windows session. Per session and per user, so two
    /// signed-in users never collide and a reused session id never reaches an older user's pipe. The server claims it with the
    /// first-instance flag and clients connect with <c>PipeOptions.CurrentUserOnly</c>, so a squatter is refused on both sides (L8).
    /// </summary>
    public static string TrayControlPipe(int sessionId, string userSid) =>
        LegacyRunnerPipe + "." + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + userSid;

    /// <summary>The tray control pipe of this process's session and user.</summary>
    public static string TrayControlPipeForThisSession()
    {
        using var me = WindowsIdentity.GetCurrent();
        return TrayControlPipe(System.Diagnostics.Process.GetCurrentProcess().SessionId, me.User!.Value);
    }

    /// <summary>
    /// The only privileges the service keeps (<c>sc.exe privs</c>, separated by slashes; review L7). LocalSystem otherwise holds
    /// every privilege, which would turn any bug in the service into a takeover of the PC. Change notification is needed by
    /// every process; global objects by the pipes and the sensor library. If CPU or motherboard sensors stop working after
    /// install, the sensor library needs one more (see the manual test plan); add it here and in the installer.
    /// </summary>
    public const string RequiredPrivileges = "SeChangeNotifyPrivilege/SeCreateGlobalPrivilege";

    /// <summary>How long Windows waits for the service at shutdown before it moves on (the service sets it at start, L5).</summary>
    public const int PreshutdownTimeoutMs = 5000;

    /// <summary>
    /// The service's own access rules (<c>sc.exe sdset</c>). It is Windows' default for a new service, plus "start" (<c>RP</c>)
    /// for interactive users, so the editor can start the service without a prompt. Stopping, changing and deleting it stay
    /// with SYSTEM and Administrators.
    /// </summary>
    public const string ServiceSddl =
        "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)";

    // service access rights (winsvc.h)
    public const int ServiceQueryConfig = 0x1, ServiceChangeConfig = 0x2, ServiceQueryStatus = 0x4, ServiceEnumerateDependents = 0x8,
        ServiceStart = 0x10, ServiceStop = 0x20, ServicePauseContinue = 0x40, ServiceInterrogate = 0x80, ServiceUserDefinedControl = 0x100,
        Delete = 0x10000, ReadControl = 0x20000, WriteDac = 0x40000, WriteOwner = 0x80000;

    /// <summary>
    /// Checks a service SDDL against the rules above. Returns the problems, or an empty list. Pure, for the tests and as a guard
    /// for anyone who edits <see cref="ServiceSddl"/>.
    /// </summary>
    public static IReadOnlyList<string> CheckServiceSddl(string sddl)
    {
        var problems = new List<string>();
        RawSecurityDescriptor sd;
        try { sd = new RawSecurityDescriptor(sddl); }
        catch (Exception ex) { return new[] { "not a valid security descriptor: " + ex.Message }; }
        if (sd.DiscretionaryAcl is null) return new[] { "no DACL: everyone could do everything" };

        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var interactive = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);
        const int readOnly = ServiceQueryConfig | ServiceQueryStatus | ServiceEnumerateDependents | ServiceInterrogate | ServiceUserDefinedControl | ReadControl;
        bool interactiveCanStart = false;

        foreach (var ace in sd.DiscretionaryAcl)
        {
            if (ace is not CommonAce common) { problems.Add("an unexpected entry type: " + ace.AceType); continue; }
            if (common.AceQualifier != AceQualifier.AccessAllowed) continue;   // deny entries only take rights away
            var sid = common.SecurityIdentifier;
            if (sid == system || sid == admins) continue;
            int mask = Expand(common.AccessMask);
            int allowed = sid == interactive ? readOnly | ServiceStart : readOnly;
            if (sid == interactive && (mask & ServiceStart) != 0) interactiveCanStart = true;
            if ((mask & ServiceStart) != 0 && sid != interactive) problems.Add($"{sid.Value} may start the service");
            if ((mask & ServiceStop) != 0) problems.Add($"{sid.Value} may stop the service");
            if ((mask & ServicePauseContinue) != 0) problems.Add($"{sid.Value} may pause the service");
            if ((mask & ServiceChangeConfig) != 0) problems.Add($"{sid.Value} may change the service's program or account");
            if ((mask & Delete) != 0) problems.Add($"{sid.Value} may delete the service");
            if ((mask & (WriteDac | WriteOwner)) != 0) problems.Add($"{sid.Value} may change the service's permissions or owner");
            if ((mask & ~allowed & ~(ServiceStop | ServicePauseContinue | ServiceChangeConfig | Delete | WriteDac | WriteOwner | ServiceStart)) != 0)
                problems.Add($"{sid.Value} has rights beyond reading the service's state");
        }
        if (!interactiveCanStart) problems.Add("interactive users cannot start the service, so the editor would need a prompt to start it");
        return problems;

        static int Expand(int mask)
        {
            const int genericAll = 0x10000000, genericExecute = 0x20000000, genericWrite = 0x40000000, genericRead = unchecked((int)0x80000000);
            const int all = 0x1FF | Delete | ReadControl | WriteDac | WriteOwner;
            if ((mask & genericAll) != 0) mask |= all;
            if ((mask & genericRead) != 0) mask |= ReadControl | ServiceQueryConfig | ServiceQueryStatus | ServiceInterrogate | ServiceEnumerateDependents;
            if ((mask & genericWrite) != 0) mask |= ReadControl | ServiceChangeConfig;
            if ((mask & genericExecute) != 0) mask |= ReadControl | ServiceStart | ServiceStop | ServicePauseContinue | ServiceUserDefinedControl;
            return mask & 0x00FFFFFF;
        }
    }
}

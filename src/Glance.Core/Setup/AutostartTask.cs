using System.Security;
using System.Xml.Linq;

namespace Glance.Core.Setup;

public enum AutostartMode { None, Install, Remove, Invalid }

/// <summary>What the runner was asked to do on its command line.</summary>
public sealed record AutostartRequest(AutostartMode Mode, string? User = null, string? Error = null);

/// <summary>
/// The "Glance Runner" logon task, shared by the editor (which asks for it) and the elevated runner (which registers it).
/// The runner builds the task definition in memory from its own path, so nothing a normal process can write is ever
/// read by the elevated side. Everything here is pure so it can be tested.
/// </summary>
public static class AutostartTask
{
    public const string TaskName = "Glance Runner";
    public const string InstallSwitch = "--install-autostart";
    public const string RemoveSwitch = "--remove-autostart";
    public const string UserSwitch = "--user";

    /// <summary>Exit codes of <c>Glance.Runner.exe --install-autostart / --remove-autostart</c>. Kept away from 1 (generic failure).</summary>
    public static class Exit
    {
        public const int Ok = 0;
        public const int BadArguments = 10;
        public const int NotElevated = 11;
        public const int InvalidUser = 12;
        public const int FolderNotTrusted = 13;
        public const int RegisterFailed = 14;
        public const int Mismatch = 15;
        public const int RemoveFailed = 16;
        public const int Unexpected = 17;
        public const int UserLookupFailed = 18;
        public const int NotSessionUser = 19;
        public const int UserNotAdmin = 20;
    }

    /// <summary>A readable message for a runner exit code, or null for success.</summary>
    public static string? DescribeExit(int code) => code switch
    {
        Exit.Ok => null,
        Exit.BadArguments => "The runner did not understand the auto-start request (is it an older version?).",
        Exit.NotElevated => "The runner did not get administrator rights.",
        Exit.InvalidUser => "The Windows user name given to the runner is not valid.",
        Exit.UserLookupFailed => "The runner could not look up your Windows account.",
        Exit.NotSessionUser => "Auto-start can only be set up for the user signed in on this screen (is the editor running as a different user?).",
        Exit.UserNotAdmin => UserNotAdminMessage,
        Exit.FolderNotTrusted => "Auto-start was refused: the runner's folder can be changed without administrator rights. Install Glance with the installer (into Program Files) to use auto-start.",
        Exit.RegisterFailed => "Windows Task Scheduler did not accept the auto-start task.",
        Exit.Mismatch => "The registered task did not point at this runner, so it was removed again. Please try again.",
        Exit.RemoveFailed => "Windows Task Scheduler could not remove the auto-start task.",
        Exit.Unexpected => "The runner hit an unexpected error while changing auto-start.",
        _ => $"The runner exited with code {code}.",
    };

    // Characters Windows does not allow in user or domain names (plus the quote, which would break the command line).
    private const string ForbiddenNameChars = "\"/[]:;|=,+*?<>";

    /// <summary>Checks the shape of a <c>DOMAIN\user</c> (or bare <c>user</c>) name. Returns an error, or null when it looks valid.</summary>
    public static string? ValidateUserName(string? user)
    {
        if (string.IsNullOrWhiteSpace(user)) return "No user name was given.";
        if (user.Length > 256) return "The user name is too long.";
        if (user != user.Trim()) return "The user name has leading or trailing spaces.";
        foreach (var c in user)
        {
            if (char.IsControl(c)) return "The user name contains a control character.";
            if (ForbiddenNameChars.Contains(c)) return $"The user name contains '{c}'.";
        }
        var parts = user.Split('\\');
        if (parts.Length > 2) return "The user name has more than one backslash.";
        if (parts.Any(p => p.Length == 0 || p != p.Trim())) return "The user name has an empty domain or account part.";
        return null;
    }

    /// <summary>The runner's arguments for installing. The name is validated, so it cannot contain a quote or end in a backslash.</summary>
    public static string InstallArguments(string user)
    {
        if (ValidateUserName(user) is { } err) throw new ArgumentException(err, nameof(user));
        return $"{InstallSwitch} {UserSwitch} \"{user}\"";
    }

    public static string RemoveArguments() => RemoveSwitch;

    /// <summary>
    /// Parses the runner's command line. Without either autostart switch the runner starts normally (<see cref="AutostartMode.None"/>);
    /// with one, the arguments must be exactly right or the request is <see cref="AutostartMode.Invalid"/>.
    /// </summary>
    public static AutostartRequest ParseArgs(IReadOnlyList<string> args)
    {
        static bool Is(string a, string s) => string.Equals(a, s, StringComparison.OrdinalIgnoreCase);
        bool install = args.Any(a => Is(a, InstallSwitch)), remove = args.Any(a => Is(a, RemoveSwitch));
        if (!install && !remove) return new AutostartRequest(AutostartMode.None);
        if (install && remove) return new AutostartRequest(AutostartMode.Invalid, Error: "Use either --install-autostart or --remove-autostart, not both.");

        if (remove)
            return args.Count == 1 ? new AutostartRequest(AutostartMode.Remove)
                                   : new AutostartRequest(AutostartMode.Invalid, Error: "--remove-autostart takes no other arguments.");

        string? user = null;
        for (int i = 0; i < args.Count; i++)
        {
            if (Is(args[i], InstallSwitch))
            {
                if (args.Take(i).Any(a => Is(a, InstallSwitch))) return new AutostartRequest(AutostartMode.Invalid, Error: "--install-autostart was given twice.");
            }
            else if (Is(args[i], UserSwitch))
            {
                if (user is not null) return new AutostartRequest(AutostartMode.Invalid, Error: "--user was given twice.");
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    return new AutostartRequest(AutostartMode.Invalid, Error: "--user needs a value.");
                user = args[++i];
            }
            else return new AutostartRequest(AutostartMode.Invalid, Error: $"Unknown argument '{args[i]}'.");
        }
        if (user is null) return new AutostartRequest(AutostartMode.Invalid, Error: "--install-autostart needs --user DOMAIN\\name.");
        if (ValidateUserName(user) is { } err) return new AutostartRequest(AutostartMode.Invalid, user, err);
        return new AutostartRequest(AutostartMode.Install, user);
    }

    /// <summary>
    /// The task definition: logon trigger for <paramref name="user"/>, highest privileges, no time limit, not stopped on
    /// battery. Same settings as earlier versions, so existing installs keep working.
    /// </summary>
    public static string BuildXml(string exe, string user)
    {
        if (!Path.IsPathFullyQualified(exe)) throw new ArgumentException("The runner path must be a full path.", nameof(exe));
        if (ValidateUserName(user) is { } err) throw new ArgumentException(err, nameof(user));
        var u = SecurityElement.Escape(user);
        var path = SecurityElement.Escape(exe);
        var dir = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "");
        return $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>Shows your Glance theme on the USB display and provides hardware sensors.</Description></RegistrationInfo>
          <Triggers>
            <LogonTrigger><Enabled>true</Enabled><UserId>{u}</UserId></LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author"><UserId>{u}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
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

    private static readonly StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    private static XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);

    /// <summary>The single Exec action of a task: its command, arguments and working folder, or a problem.</summary>
    private static (string? Command, string? Arguments, string? WorkingDirectory, string? Problem, XDocument? Doc) ReadAction(string? taskXml)
    {
        if (string.IsNullOrWhiteSpace(taskXml)) return (null, null, null, "The task could not be read.", null);
        XDocument doc;
        try { doc = XDocument.Parse(taskXml); }
        catch (Exception ex) { return (null, null, null, $"The task could not be read: {ex.Message}", null); }

        var all = Child(doc.Root, "Actions")?.Elements().ToList() ?? new List<XElement>();
        if (all.Count != 1) return (null, null, null, $"The task has {all.Count} actions instead of 1.", doc);
        var exec = all[0];
        if (exec.Name.LocalName != "Exec") return (null, null, null, $"The task's action is {exec.Name.LocalName}, not a program.", doc);
        return (Child(exec, "Command")?.Value, Child(exec, "Arguments")?.Value, Child(exec, "WorkingDirectory")?.Value, null, doc);
    }

    /// <summary>
    /// Checks only the task's action: exactly one Exec of <paramref name="expectedExe"/> with no arguments.
    /// Returns a problem, or null. The four-argument overload checks everything.
    /// </summary>
    public static string? VerifyRegistered(string? taskXml, string expectedExe)
    {
        var a = ReadAction(taskXml);
        if (a.Problem is not null) return a.Problem;
        if (!SameExecutable(a.Command, expectedExe)) return $"The task runs '{a.Command}' instead of '{expectedExe}'.";
        if (!string.IsNullOrWhiteSpace(a.Arguments)) return "The task passes unexpected arguments.";
        return null;
    }

    /// <summary>
    /// The read-back check after registering (L5), against the XML Task Scheduler returns (it stores the principal as a
    /// SID and the trigger's user as a name, and reorders settings): one Exec of <paramref name="expectedExe"/>, no
    /// arguments, the exe's folder as working folder, exactly one enabled logon trigger for the user, and one
    /// InteractiveToken / HighestAvailable principal for the user. Users are compared as SIDs through
    /// <paramref name="toSid"/>; pass a null <paramref name="expectedUserSid"/> to skip that. Returns a problem, or null.
    /// </summary>
    public static string? VerifyRegistered(string? taskXml, string expectedExe, string? expectedUserSid, Func<string, string?> toSid)
    {
        if (VerifyRegistered(taskXml, expectedExe) is { } actionProblem) return actionProblem;
        var a = ReadAction(taskXml);
        var root = a.Doc!.Root;

        var dir = Path.GetDirectoryName(expectedExe);
        if (!SameExecutable(a.WorkingDirectory, dir)) return $"The task starts in '{a.WorkingDirectory}' instead of '{dir}'.";

        bool SameUser(string? name) =>
            expectedUserSid is null || (name is not null && toSid(name.Trim()) is { } sid && string.Equals(sid, expectedUserSid, Ci));

        var triggers = Child(root, "Triggers")?.Elements().ToList() ?? new List<XElement>();
        if (triggers.Count != 1 || triggers[0].Name.LocalName != "LogonTrigger")
            return $"The task has {triggers.Count} trigger(s) ({string.Join(", ", triggers.Select(t => t.Name.LocalName))}) instead of one logon trigger.";
        if (string.Equals(Child(triggers[0], "Enabled")?.Value.Trim(), "false", Ci)) return "The task's logon trigger is turned off.";
        var triggerUser = Child(triggers[0], "UserId")?.Value;
        if (triggerUser is null) return "The task's logon trigger is not limited to one user.";
        if (!SameUser(triggerUser)) return $"The task's logon trigger is for '{triggerUser}', not for the signed-in user.";

        var principals = Child(root, "Principals")?.Elements().ToList() ?? new List<XElement>();
        if (principals.Count != 1) return $"The task has {principals.Count} principals instead of 1.";
        var p = principals[0];
        if (!string.Equals(Child(p, "LogonType")?.Value.Trim(), "InteractiveToken", Ci)) return "The task does not run in the signed-in user's session.";
        if (!string.Equals(Child(p, "RunLevel")?.Value.Trim(), "HighestAvailable", Ci)) return "The task does not run with highest privileges.";
        var principalUser = Child(p, "UserId")?.Value;
        if (!SameUser(principalUser)) return $"The task runs as '{principalUser}', not as the signed-in user.";
        return null;
    }

    /// <summary>True when both paths name the same file or folder (case-insensitive, normalised, optional surrounding quotes).</summary>
    public static bool SameExecutable(string? a, string? b)
    {
        static string? Norm(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return null;
            p = p.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(p)) return null;
            try { return Path.GetFullPath(p).TrimEnd('\\', '/'); } catch { return null; }
        }
        var na = Norm(a); var nb = Norm(b);
        return na is not null && nb is not null && string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
    }

    public const string RunnerFileName = "Glance.Runner.exe";

    /// <summary>
    /// Judges an existing "Glance Runner" task (M1): it must be a plain start of a Glance runner, that runner must be
    /// trusted, and it should be this copy. <paramref name="trustOfExe"/> is the trust check for the task's command.
    /// </summary>
    public static AutostartStatus Assess(bool exists, string? taskXml, string? currentExe, Func<string, Glance.Core.Security.TrustVerdict> trustOfExe)
    {
        if (!exists) return new AutostartStatus(AutostartState.NotInstalled, null, null);
        const string fix = " Turn auto-start off, or turn it on again from an installed copy to replace it.";

        var a = ReadAction(taskXml);
        if (a.Problem is not null)
            return new AutostartStatus(AutostartState.NotGlance, a.Command, $"The \"{TaskName}\" task could not be checked: {a.Problem}{fix}");
        var command = a.Command?.Trim().Trim('"');
        if (string.IsNullOrEmpty(command) || !string.Equals(Path.GetFileName(command), RunnerFileName, Ci) || !string.IsNullOrWhiteSpace(a.Arguments))
            return new AutostartStatus(AutostartState.NotGlance, command,
                $"The \"{TaskName}\" task does not simply start a Glance runner (it runs '{a.Command}{(string.IsNullOrWhiteSpace(a.Arguments) ? "" : " " + a.Arguments)}').{fix}");

        var trust = trustOfExe(command);
        if (!trust.Trusted)
            return new AutostartStatus(AutostartState.Untrusted, command,
                $"Auto-start runs {command} as administrator at every sign-in, but that copy is not safe: {trust.Reason}{fix}");
        if (currentExe is not null && !SameExecutable(command, currentExe))
            return new AutostartStatus(AutostartState.OtherCopy, command, $"Auto-start points at another copy of Glance: {command}.{fix}");
        return new AutostartStatus(AutostartState.Ok, command, null);
    }

    /// <summary>
    /// The runner's user checks (M2): the requested user must be the one signed in to this session, and when a different
    /// account approved the prompt (a standard user typing an admin's password), the requested user must be an
    /// administrator, or Windows can never start the requireAdministrator runner for them (error 740).
    /// <paramref name="requestedIsAdmin"/> is null when it could not be determined, which counts as "no".
    /// </summary>
    public static int CheckInstallUser(string requestedSid, string? sessionSid, string elevatedSid, bool? requestedIsAdmin)
    {
        if (sessionSid is null || !string.Equals(sessionSid, requestedSid, Ci)) return Exit.NotSessionUser;
        if (!string.Equals(elevatedSid, requestedSid, Ci) && requestedIsAdmin != true) return Exit.UserNotAdmin;
        return Exit.Ok;
    }

    /// <summary>
    /// The editor's own check before showing the prompt (M2): auto-start can only start the runner for a user who is an
    /// administrator. A UAC split token (Limited) or an elevated one (Full) belongs to an administrator; with UAC turned
    /// off (Default) the Administrators membership tells.
    /// </summary>
    public static bool UserCanAutostart(TokenElevation elevation, bool inAdministratorsRole) => elevation switch
    {
        TokenElevation.Limited => true,
        TokenElevation.Full => true,
        _ => inAdministratorsRole,
    };

    public const string UserNotAdminMessage =
        "Auto-start needs your Windows account to be an administrator. The runner needs administrator rights, and Windows cannot " +
        "give them to a standard account when it signs in, so the task would never start. You can still start the runner by hand.";

    private const int ErrorFileNotFound = unchecked((int)0x80070002), ErrorPathNotFound = unchecked((int)0x80070003);

    /// <summary>
    /// What a failed DeleteTask means (L1). Task Scheduler reports a missing task as HRESULT 0x80070002, which late-bound
    /// COM turns into <see cref="FileNotFoundException"/>, not a COMException, so only the HRESULT is looked at.
    /// A missing task counts as removed.
    /// </summary>
    public static int ClassifyRemoveError(Exception ex) =>
        ex.HResult is ErrorFileNotFound or ErrorPathNotFound ? Exit.Ok : Exit.RemoveFailed;

    /// <summary>The fixed arguments for removing the task with Windows' own schtasks.exe.</summary>
    public const string SchtasksDeleteArguments = "/Delete /TN \"" + TaskName + "\" /F";

    /// <summary>
    /// How the editor removes the task (L2): Windows' own <c>schtasks.exe</c> from System32 with a fixed command line,
    /// elevated through the prompt. It reads no file, so removing never elevates a runner copy that might have been
    /// swapped. A task that is already gone makes schtasks fail, so callers judge success by querying afterwards.
    /// </summary>
    public static System.Diagnostics.ProcessStartInfo RemoveStartInfo() =>
        new(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), SchtasksDeleteArguments)
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            WorkingDirectory = Environment.SystemDirectory,
        };

    /// <summary>Whether the auto-start box can be clicked: an untrusted copy can turn it off but not on.</summary>
    public static bool CheckboxEnabled(bool runnerFound, bool busy, bool installed, bool? copyTrusted) =>
        !busy && runnerFound && (installed || copyTrusted != false);
}

/// <summary>The token types Windows reports through TokenElevationType.</summary>
public enum TokenElevation { Default = 1, Full = 2, Limited = 3 }

public enum AutostartState { NotInstalled, Ok, NotGlance, Untrusted, OtherCopy }

/// <summary>What the existing "Glance Runner" task does, as judged by <see cref="AutostartTask.Assess"/>.</summary>
public sealed record AutostartStatus(AutostartState State, string? Command, string? Message);

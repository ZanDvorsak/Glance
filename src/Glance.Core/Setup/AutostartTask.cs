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
    }

    /// <summary>A readable message for a runner exit code, or null for success.</summary>
    public static string? DescribeExit(int code) => code switch
    {
        Exit.Ok => null,
        Exit.BadArguments => "The runner did not understand the auto-start request (is it an older version?).",
        Exit.NotElevated => "The runner did not get administrator rights.",
        Exit.InvalidUser => "The runner could not confirm which Windows user to start for (it must be the user signed in on this screen).",
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
                if (i + 1 >= args.Count) return new AutostartRequest(AutostartMode.Invalid, Error: "--user needs a value.");
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

    /// <summary>
    /// Checks a registered task's XML (as Task Scheduler returns it) against what was meant to be registered:
    /// exactly one action, an Exec of <paramref name="expectedExe"/> with no arguments. Returns a problem, or null.
    /// </summary>
    public static string? VerifyRegistered(string? taskXml, string expectedExe)
    {
        if (string.IsNullOrWhiteSpace(taskXml)) return "The task could not be read back.";
        XDocument doc;
        try { doc = XDocument.Parse(taskXml); }
        catch (Exception ex) { return $"The task could not be read back: {ex.Message}"; }

        var actions = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Actions");
        var all = actions?.Elements().ToList() ?? new List<XElement>();
        if (all.Count != 1) return $"The task has {all.Count} actions instead of 1.";
        var exec = all[0];
        if (exec.Name.LocalName != "Exec") return $"The task's action is {exec.Name.LocalName}, not a program.";
        string? Child(string n) => exec.Elements().FirstOrDefault(e => e.Name.LocalName == n)?.Value;
        var command = Child("Command");
        if (!SameExecutable(command, expectedExe)) return $"The task runs '{command}' instead of '{expectedExe}'.";
        if (!string.IsNullOrWhiteSpace(Child("Arguments"))) return "The task passes unexpected arguments.";
        return null;
    }

    /// <summary>True when both paths name the same file (case-insensitive, normalised, optional surrounding quotes).</summary>
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
}

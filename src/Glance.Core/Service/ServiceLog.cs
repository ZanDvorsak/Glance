using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Glance.Core.Security;

namespace Glance.Core.Service;

/// <summary>
/// The service's log file, <c>%ProgramData%\Glance\service.log</c>. A normal user can create folders in %ProgramData%, so a
/// folder (or junction) planted there could make SYSTEM write into a file of the user's choice. The log is therefore only
/// written when <see cref="FolderTrust"/> says only administrators can change the folder.
/// </summary>
public sealed class ServiceLog
{
    private readonly string _file;
    private readonly object _lock = new();

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Glance");

    /// <param name="checkFolder">The trust check; <see cref="FolderTrust.CheckFolder"/> in production.</param>
    public ServiceLog(string folder, Func<string, TrustVerdict> checkFolder)
    {
        _file = Path.Combine(folder, "service.log");
        TrustVerdict verdict;
        try { verdict = checkFolder(folder); }
        catch (Exception ex) { verdict = TrustVerdict.No("its permissions could not be read: " + ex.Message); }
        if (!verdict.Trusted) DisabledReason = $"No log file is written, because {folder} can be changed without administrator rights: {verdict.Reason}";
    }

    /// <summary>
    /// For the service: creates the folder with admin-only rules if it is missing (SYSTEM and Administrators may change it,
    /// users may read the log), then checks it, whoever created it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static ServiceLog OpenDefault()
    {
        var folder = DefaultFolder;
        try
        {
            if (!Directory.Exists(folder))
            {
                var rules = new DirectorySecurity();
                rules.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                rules.AddAccessRule(new FileSystemAccessRule(ServicePipeSecurity.LocalSystem, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
                rules.AddAccessRule(new FileSystemAccessRule(ServicePipeSecurity.Administrators, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
                rules.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, both, PropagationFlags.None, AccessControlType.Allow));
                rules.CreateDirectory(folder);
            }
        }
        catch { /* the check below decides */ }
        return new ServiceLog(folder, FolderTrust.CheckFolder);
    }

    /// <summary>Why nothing is written, or null when the log works.</summary>
    public string? DisabledReason { get; }

    public void Write(string message)
    {
        if (DisabledReason is not null) return;
        try { lock (_lock) File.AppendAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}"); }
        catch { /* logging must never break the service */ }
    }
}

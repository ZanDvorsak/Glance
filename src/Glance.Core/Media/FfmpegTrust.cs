using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Glance.Core.Media;

/// <summary>One access-control entry, reduced to what the trust rule needs.</summary>
/// <param name="Sid">The principal's SID string (for example S-1-5-32-545 for Users).</param>
/// <param name="Allow">Allow entry (true) or deny entry (false). Deny entries are ignored, which only makes the rule stricter.</param>
/// <param name="Rights">The raw access mask, generic bits included.</param>
/// <param name="InheritOnly">Applies only to children, not to the object itself.</param>
public sealed record AceFact(string Sid, bool Allow, int Rights, bool InheritOnly = false);

/// <summary>Security facts for one path: the owner, the access entries and whether it is a link or junction.</summary>
public sealed record PathFact(string Path, string? OwnerSid, IReadOnlyList<AceFact> Aces, bool IsReparsePoint = false);

public readonly record struct TrustVerdict(bool Trusted, string Reason);

/// <summary>
/// Decides whether an executable can be started by an elevated process without giving a normal (non-admin) process a
/// way in. The executable is trusted only if no principal other than SYSTEM, Administrators or TrustedInstaller can
/// replace it, change it, or plant files next to it, and nothing on the way to it is a link that could be redirected.
/// </summary>
/// <remarks>
/// This is an allow-list of admin principals rather than a deny-list of known user groups (Users, Authenticated Users,
/// Everyone, INTERACTIVE, the user's own SID): any write right held by anyone else, including groups we did not think of,
/// rejects the file. Rights are judged by where they sit:
/// <list type="bullet">
/// <item>the file: no write, append, delete, change-permissions or take-ownership;</item>
/// <item>its folder: additionally no creating files or folders (DLL planting) and no deleting children;</item>
/// <item>folders further up: no delete, delete-children, change-permissions or take-ownership. Creating new entries there
/// is harmless (it cannot replace an existing child) and must be allowed, because <c>C:\</c> lets Authenticated Users
/// create folders.</item>
/// </list>
/// Every object's owner must be an admin principal too, since an owner can always rewrite the permissions.
/// </remarks>
public static class FfmpegTrust
{
    // file / directory access bits (winnt.h)
    private const int WriteData = 0x2;            // FILE_WRITE_DATA / FILE_ADD_FILE
    private const int AppendData = 0x4;           // FILE_APPEND_DATA / FILE_ADD_SUBDIRECTORY
    private const int DeleteChild = 0x40;         // FILE_DELETE_CHILD
    private const int Delete = 0x10000;           // DELETE
    private const int WriteDac = 0x40000;         // WRITE_DAC
    private const int WriteOwner = 0x80000;       // WRITE_OWNER
    private const int GenericAll = 0x10000000;    // GENERIC_ALL
    private const int GenericWrite = 0x40000000;  // GENERIC_WRITE (maps to write data + append data)

    internal const int FileMask = WriteData | AppendData | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite;
    internal const int ParentMask = FileMask | DeleteChild;
    internal const int AncestorMask = DeleteChild | Delete | WriteDac | WriteOwner | GenericAll;

    public const string SystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private const string CreatorOwnerSid = "S-1-3-0"; // a template for children; has no effect on the object itself
    private const string OwnerRightsSid = "S-1-3-4";  // means "the owner", and the owner is checked separately

    private static readonly HashSet<string> AdminPrincipals = new(StringComparer.OrdinalIgnoreCase)
    {
        SystemSid, AdministratorsSid, TrustedInstallerSid, CreatorOwnerSid, OwnerRightsSid,
    };

    /// <summary>
    /// The trust rule. <paramref name="chain"/> is the file followed by every folder above it, up to and including the
    /// drive root (for example <c>C:\a\ffmpeg.exe</c>, <c>C:\a</c>, <c>C:\</c>).
    /// </summary>
    public static TrustVerdict Evaluate(string exePath, IReadOnlyList<PathFact> chain)
    {
        if (!IsLocalDrivePath(exePath))
            return new(false, "it is not on a local drive (network and relative paths are not trusted)");
        if (chain.Count < 2 || !string.Equals(chain[0].Path, exePath, StringComparison.OrdinalIgnoreCase) || !IsDriveRoot(chain[^1].Path))
            return new(false, "its folder permissions could not be read completely");

        for (int i = 0; i < chain.Count; i++)
        {
            var fact = chain[i];
            string what = i == 0 ? "the file" : $"the folder {fact.Path}";

            if (fact.IsReparsePoint)
                return new(false, $"{what} is a link or junction, which could be pointed somewhere else");
            if (fact.OwnerSid is null || !AdminPrincipals.Contains(fact.OwnerSid))
                return new(false, $"{what} is owned by {Describe(fact.OwnerSid)}, not by an administrator");

            int mask = i == 0 ? FileMask : i == 1 ? ParentMask : AncestorMask;
            foreach (var ace in fact.Aces)
            {
                if (!ace.Allow || ace.InheritOnly || AdminPrincipals.Contains(ace.Sid)) continue;
                if ((ace.Rights & mask) != 0)
                    return new(false, $"{Describe(ace.Sid)} can change {what}");
            }
        }
        return new(true, "only administrators can change it");
    }

    internal static bool IsLocalDrivePath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\' && !path.Contains("\\..", StringComparison.Ordinal);

    private static bool IsDriveRoot(string path) => path.Length == 3 && IsLocalDrivePath(path);

    private static string Describe(string? sid) => sid switch
    {
        null => "nobody known",
        "S-1-1-0" => "Everyone",
        "S-1-5-11" => "Authenticated Users",
        "S-1-5-32-545" => "Users",
        "S-1-5-4" => "INTERACTIVE",
        _ => TryName(sid),
    };

    private static string TryName(string sid)
    {
        if (!OperatingSystem.IsWindows()) return sid;
        try { return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
        catch { return sid; }
    }

    // ---- reading the facts from disk ----------------------------------------------------------------------------

    /// <summary>Reads the facts for a file and every folder above it, then applies <see cref="Evaluate"/>.</summary>
    public static TrustVerdict Check(string path)
    {
        if (!OperatingSystem.IsWindows()) return new(false, "folder permissions can only be checked on Windows");
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) { return new(false, "the path is invalid (" + ex.Message + ")"); }
        if (!IsLocalDrivePath(full)) return Evaluate(full, Array.Empty<PathFact>());

        var chain = new List<PathFact>();
        try
        {
            // a mapped network drive has a letter too, but its "Administrators" are the server's, not this PC's
            if (new DriveInfo(full[..3]).DriveType != DriveType.Fixed)
                return new(false, "it is not on a fixed local drive (network and removable drives are not trusted)");
            chain.Add(ReadFact(full, directory: false));
            for (var dir = Path.GetDirectoryName(full); dir is not null; dir = Path.GetDirectoryName(dir))
                chain.Add(ReadFact(dir, directory: true));
        }
        catch (Exception ex) { return new(false, "its permissions could not be read (" + ex.Message + ")"); }
        return Evaluate(full, chain);
    }

    [SupportedOSPlatform("windows")]
    private static PathFact ReadFact(string path, bool directory)
    {
        const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Access;
        FileSystemSecurity sec = directory ? new DirectoryInfo(path).GetAccessControl(sections) : new FileInfo(path).GetAccessControl(sections);
        var owner = sec.GetOwner(typeof(SecurityIdentifier))?.Value;
        var aces = new List<AceFact>();
        foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            aces.Add(new AceFact(rule.IdentityReference.Value, rule.AccessControlType == AccessControlType.Allow,
                (int)rule.FileSystemRights, rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)));
        bool reparse = (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        return new PathFact(path, owner, aces, reparse);
    }
}

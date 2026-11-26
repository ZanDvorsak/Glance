using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Glance.Core.Security;

/// <summary>What an object is to the program folder being judged; each role cares about different rights.</summary>
public enum TrustRole
{
    /// <summary>The program folder itself, or a folder inside it: creating files there is enough to plant a DLL.</summary>
    Folder,
    /// <summary>A file inside the program folder: changing or replacing it changes what runs.</summary>
    File,
    /// <summary>A folder above the program folder: deleting or renaming its children swaps the whole program folder.</summary>
    Ancestor,
}

/// <summary>One access control entry, reduced to what the trust decision needs.</summary>
/// <param name="Sid">The principal's SID string (for example <c>S-1-5-32-545</c>).</param>
/// <param name="Allow">True for an allow entry, false for a deny entry.</param>
/// <param name="Rights">The raw access mask.</param>
/// <param name="InheritOnly">True when the entry only applies to children, not to the object itself.</param>
/// <param name="Name">A readable name for messages (optional).</param>
public sealed record AclEntry(string Sid, bool Allow, int Rights, bool InheritOnly = false, string? Name = null);

/// <summary>The security facts of one file or folder.</summary>
/// <param name="Entries">The DACL entries, or null for a NULL DACL (everyone has full access).</param>
public sealed record ObjectSecurity(string Path, TrustRole Role, string? OwnerSid, IReadOnlyList<AclEntry>? Entries, string? OwnerName = null);

/// <summary>The result of a trust check. <see cref="Reason"/> says who can change what when it is not trusted.</summary>
public sealed record TrustVerdict(bool Trusted, string? Reason, string? PrincipalSid = null)
{
    public static readonly TrustVerdict Yes = new(true, null);
    public static TrustVerdict No(string reason, string? principalSid = null) => new(false, reason, principalSid);
}

/// <summary>
/// Decides whether a program folder can only be changed by administrators. Anything an elevated process starts
/// automatically (the autostart task, and for example ffmpeg) must come from such a folder, otherwise a normal process
/// could swap the files and get administrator rights without a prompt.
/// The decision (<see cref="Decide"/>) is a pure function of ACL facts so it can be tested; <see cref="Check"/> reads
/// the real ACLs on Windows.
/// </summary>
/// <remarks>
/// The check is deliberately conservative: deny entries are ignored (they can only make a folder look less safe than it
/// is), an unknown entry type or a NULL DACL counts as writable, and an owner that is not an administrator counts as
/// writable because an owner can always rewrite the permissions.
/// </remarks>
public static class FolderTrust
{
    public const string LocalSystem = "S-1-5-18";
    public const string Administrators = "S-1-5-32-544";
    public const string TrustedInstaller = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    /// <summary>CREATOR OWNER: only meaningful as an inheritable placeholder, never on the object itself.</summary>
    public const string CreatorOwner = "S-1-3-0";
    /// <summary>OWNER RIGHTS: the entry applies to whoever owns the object.</summary>
    public const string OwnerRights = "S-1-3-4";

    private static readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase) { LocalSystem, Administrators, TrustedInstaller };

    // Access mask bits (winnt.h). File and directory bits share values: WriteData = AddFile, AppendData = AddSubdirectory.
    public const int WriteData = 0x2, AppendData = 0x4, WriteExtendedAttributes = 0x10, DeleteChild = 0x40,
        Delete = 0x10000, WriteDac = 0x40000, WriteOwner = 0x80000, GenericAll = 0x10000000, GenericWrite = 0x40000000;

    /// <summary>The rights that let a holder change what an elevated process would load, per role.</summary>
    public static int DangerousRights(TrustRole role) => role switch
    {
        TrustRole.Folder => WriteData | AppendData | WriteExtendedAttributes | DeleteChild | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite,
        TrustRole.File => WriteData | AppendData | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite,
        // Creating new entries in C:\ or Program Files is harmless; removing or renaming ours is not.
        TrustRole.Ancestor => DeleteChild | Delete | WriteDac | WriteOwner | GenericAll,
        _ => -1,
    };

    public static bool IsTrustedSid(string? sid) => sid is not null && Trusted.Contains(sid);

    /// <summary>
    /// Pure decision: trusted only if, for every object, no principal other than SYSTEM, Administrators and
    /// TrustedInstaller holds a right that matters for its role, and every owner is one of those.
    /// Stops at the first problem, so the input can be produced lazily.
    /// </summary>
    public static TrustVerdict Decide(IEnumerable<ObjectSecurity> objects)
    {
        bool any = false;
        foreach (var o in objects)
        {
            any = true;
            if (Problem(o) is { } problem) return problem;
        }
        return any ? TrustVerdict.Yes : TrustVerdict.No("No files to check.");
    }

    private static TrustVerdict? Problem(ObjectSecurity o)
    {
        if (!IsTrustedSid(o.OwnerSid))
            return TrustVerdict.No($"{o.OwnerName ?? o.OwnerSid ?? "an unknown account"} owns {o.Path}, so it can change its permissions.", o.OwnerSid);
        if (o.Entries is null)
            return TrustVerdict.No($"{o.Path} has no access list, so everyone can change it.");

        var dangerous = DangerousRights(o.Role);
        foreach (var e in o.Entries)
        {
            if (!e.Allow || e.InheritOnly) continue;
            if ((e.Rights & dangerous) == 0) continue;
            if (string.Equals(e.Sid, CreatorOwner, StringComparison.OrdinalIgnoreCase)) continue;
            // OWNER RIGHTS grants to the owner, who was already checked above.
            if (string.Equals(e.Sid, OwnerRights, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsTrustedSid(e.Sid)) continue;
            return TrustVerdict.No($"{e.Name ?? e.Sid} can {Describe(o.Role)} {o.Path}.", e.Sid);
        }
        return null;
    }

    private static string Describe(TrustRole role) => role switch
    {
        TrustRole.Folder => "add or change files in",
        TrustRole.File => "change",
        _ => "move or replace folders in",
    };

    /// <summary>
    /// Reads the real permissions of <paramref name="folder"/>, everything inside it, and every folder above it,
    /// and decides. Any error reading them counts as not trusted.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static TrustVerdict Check(string folder)
    {
        try { return WithName(Decide(Gather(Path.GetFullPath(folder)))); }
        catch (Exception ex) { return TrustVerdict.No($"Could not read the permissions of {folder}: {ex.Message}"); }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<ObjectSecurity> Gather(string folder)
    {
        var root = new DirectoryInfo(folder);
        if (!root.Exists) throw new DirectoryNotFoundException($"{folder} does not exist.");
        yield return Read(root.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), folder, TrustRole.Folder);

        for (var d = root.Parent; d is not null; d = d.Parent)
            yield return Read(d.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), d.FullName, TrustRole.Ancestor);

        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 };
        foreach (var d in root.EnumerateDirectories("*", opts))
            yield return Read(d.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), d.FullName, TrustRole.Folder);
        foreach (var f in root.EnumerateFiles("*", opts))
            yield return Read(f.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), f.FullName, TrustRole.File);
    }

    [SupportedOSPlatform("windows")]
    private static ObjectSecurity Read(FileSystemSecurity security, string path, TrustRole role)
    {
        var sd = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        var owner = sd.Owner?.Value;
        if (sd.DiscretionaryAcl is null) return new ObjectSecurity(path, role, owner, null);

        var entries = new List<AclEntry>();
        foreach (var ace in sd.DiscretionaryAcl)
        {
            var inheritOnly = (ace.AceFlags & AceFlags.InheritOnly) != 0;
            if (ace is QualifiedAce q)
            {
                bool allow = q.AceQualifier == AceQualifier.AccessAllowed;
                bool deny = q.AceQualifier == AceQualifier.AccessDenied;
                if (!allow && !deny) continue; // audit and alarm entries do not grant anything
                entries.Add(new AclEntry(q.SecurityIdentifier.Value, allow, q.AccessMask, inheritOnly));
            }
            else
            {
                // An entry type we do not understand: assume the worst.
                entries.Add(new AclEntry("unknown", true, -1, inheritOnly, $"an unrecognised permission entry ({ace.AceType})"));
            }
        }
        return new ObjectSecurity(path, role, owner, entries);
    }

    /// <summary>Names are only looked up for the one account a message mentions; looking up every entry is slow.</summary>
    [SupportedOSPlatform("windows")]
    private static TrustVerdict WithName(TrustVerdict v)
    {
        if (v.Trusted || v.PrincipalSid is null || v.Reason is null) return v;
        try
        {
            var name = new SecurityIdentifier(v.PrincipalSid).Translate(typeof(NTAccount)).Value;
            return v with { Reason = v.Reason.Replace(v.PrincipalSid, name) };
        }
        catch { return v; }
    }
}

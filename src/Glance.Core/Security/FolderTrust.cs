using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Glance.Core.Security;

/// <summary>What a path is to the program being judged; each role cares about different rights.</summary>
public enum TrustRole
{
    /// <summary>The program's folder, or a folder inside it: creating files there is enough to plant a DLL.</summary>
    Folder,
    /// <summary>The program itself or a file next to it: changing or replacing it changes what runs.</summary>
    File,
    /// <summary>A folder above the program's folder: deleting or renaming its children swaps the whole program folder.</summary>
    Ancestor,
}

/// <summary>How much of an executable's folder <see cref="FolderTrust.CheckExecutable"/> reads.</summary>
public enum TrustScope
{
    /// <summary>The file, its folder (no new files may be planted) and every folder above it. Cheap; fine for System32.</summary>
    ExecutableOnly,
    /// <summary>Also every file directly in the exe's folder (DLLs loaded next to the exe).</summary>
    FolderFiles,
    /// <summary>Also everything below the folder (a .NET app also loads from subfolders). Used for the runner.</summary>
    FolderTree,
}

/// <summary>One access control entry, reduced to what the trust decision needs.</summary>
/// <param name="Sid">The principal's SID string (for example <c>S-1-5-32-545</c>).</param>
/// <param name="Allow">True for an allow entry, false for a deny entry (deny entries are ignored: stricter, never looser).</param>
/// <param name="Rights">The raw access mask, generic bits included.</param>
/// <param name="InheritOnly">True when the entry only applies to children, not to the object itself.</param>
/// <param name="Name">A readable name for messages (optional).</param>
public sealed record AclEntry(string Sid, bool Allow, int Rights, bool InheritOnly = false, string? Name = null);

/// <summary>The security facts of one file or folder.</summary>
/// <param name="Entries">The DACL entries, or null for a NULL DACL (everyone has full access).</param>
/// <param name="IsLink">A symbolic link, junction or other name-surrogate reparse point. Its own owner and ACL are read (never the target's), and it is refused.</param>
public sealed record PathSecurity(string Path, TrustRole Role, string? OwnerSid, IReadOnlyList<AclEntry>? Entries, string? OwnerName = null, bool IsLink = false);

/// <summary>The result of a trust check. <see cref="Reason"/> says who can change what when it is not trusted.</summary>
public sealed record TrustVerdict(bool Trusted, string? Reason, string? PrincipalSid = null)
{
    public static readonly TrustVerdict Yes = new(true, null);
    public static TrustVerdict No(string reason, string? principalSid = null) => new(false, reason, principalSid);
}

/// <summary>
/// The one trust engine of the code base: decides whether a program (and everything it loads) can only be changed by
/// administrators. Anything an elevated process starts without a prompt (the autostart task, ffmpeg in the runner) must
/// pass it, otherwise a normal process could swap files and get administrator rights silently.
/// </summary>
/// <remarks>
/// <para>The decision is pure (<see cref="Evaluate"/>, <see cref="Decide"/>, <see cref="PathProblem"/>) so it can be
/// tested; <see cref="CheckFolder"/> and <see cref="CheckExecutable"/> read the real file system on Windows.</para>
/// <para>Rules:</para>
/// <list type="bullet">
/// <item>Only fully qualified paths on a local fixed drive (<c>X:\...</c>) with no <c>.</c> or <c>..</c> segment: not UNC,
/// not <c>\\?\</c>, not relative, not a network, removable or optical drive (their "Administrators" are someone else's).</item>
/// <item>Every object's owner must be SYSTEM, Administrators or TrustedInstaller (an owner can always rewrite permissions).</item>
/// <item>No other principal may hold a dangerous right for the object's role. It is an allow-list, so the user's SID,
/// Users, Authenticated Users, Everyone, INTERACTIVE and any group we did not think of are all covered. Deny entries are
/// ignored and unknown entry types or a NULL DACL count as writable, so mistakes go towards "not trusted".</item>
/// <item>No link, junction or other name-surrogate reparse point anywhere: a link could be pointed somewhere else.</item>
/// <item>The chain is complete: the target's own facts and every folder up to the drive root were read.</item>
/// <item>A drive root cannot be deleted or renamed, so DELETE on the root itself is ignored (Windows gives Authenticated
/// Users Modify on data drive roots). Delete-children, change-permissions and take-ownership on the root still count.</item>
/// </list>
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

    /// <summary>The dangerous rights for one object: as <see cref="DangerousRights(TrustRole)"/>, without DELETE on a drive root.</summary>
    public static int DangerousRights(PathSecurity o) =>
        o.Role == TrustRole.Ancestor && IsDriveRoot(o.Path) ? DangerousRights(o.Role) & ~Delete : DangerousRights(o.Role);

    public static bool IsTrustedSid(string? sid) => sid is not null && Trusted.Contains(sid);

    /// <summary>True for <c>X:\</c>.</summary>
    public static bool IsDriveRoot(string path) => path.Length == 3 && PathProblem(path) is null;

    /// <summary>
    /// Pure path rule: null for a fully qualified local drive path (<c>X:\...</c>) without <c>.</c> or <c>..</c> segments,
    /// otherwise the reason it is refused.
    /// </summary>
    public static string? PathProblem(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "No path was given.";
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
            return $"{path} is not a full path on a local drive (network, device and relative paths are not trusted).";
        if (path.IndexOf('/') >= 0) return $"{path} mixes path separators.";
        foreach (var part in path[3..].Split('\\'))
            if (part is "." or "..") return $"{path} contains '{part}'.";
        return null;
    }

    /// <summary>
    /// Pure per-object rule: trusted only if, for every object, no principal other than SYSTEM, Administrators and
    /// TrustedInstaller holds a right that matters for its role, every owner is one of those, and nothing is a link.
    /// Stops at the first problem, so the input can be produced lazily. It does not check that the chain is complete;
    /// <see cref="Evaluate"/> does.
    /// </summary>
    public static TrustVerdict Decide(IEnumerable<PathSecurity> objects)
    {
        bool any = false;
        foreach (var o in objects)
        {
            any = true;
            if (Problem(o) is { } problem) return problem;
        }
        return any ? TrustVerdict.Yes : TrustVerdict.No("No files to check.");
    }

    /// <summary>
    /// The full pure decision for a program file (<paramref name="targetIsFile"/>) or a program folder: the path rule,
    /// the drive rule, <see cref="Decide"/> for every object, and a complete chain (the target's own facts, the file's
    /// folder, and every folder up to the drive root).
    /// </summary>
    public static TrustVerdict Evaluate(string target, bool targetIsFile, bool onFixedDrive, IEnumerable<PathSecurity> objects)
    {
        if (PathProblem(target) is { } bad) return TrustVerdict.No(bad);
        if (target.Length > 3) target = target.TrimEnd(Sep);
        if (!onFixedDrive) return TrustVerdict.No($"{target} is not on a fixed local drive (network and removable drives are not trusted).");
        if (targetIsFile && IsDriveRoot(target)) return TrustVerdict.No($"{target} is not a file.");

        // What must have been read: the target, the file's folder, and every folder above.
        var folder = targetIsFile ? Path.GetDirectoryName(target)! : target;
        var required = new List<(string Path, TrustRole Role)>();
        if (targetIsFile) required.Add((target, TrustRole.File));
        required.Add((folder, TrustRole.Folder));
        for (var d = Path.GetDirectoryName(folder); d is not null; d = Path.GetDirectoryName(d)) required.Add((d, TrustRole.Ancestor));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var o in objects)
        {
            if (Problem(o) is { } problem) return problem;
            seen.Add(Key(o.Path, o.Role));
        }
        foreach (var r in required)
            if (!seen.Contains(Key(r.Path, r.Role))) return TrustVerdict.No($"The permissions of {r.Path} were not checked.");
        return TrustVerdict.Yes;
    }

    private const char Sep = (char)92; // backslash
    private static string Key(string path, TrustRole role) => (path.Length > 3 ? path.TrimEnd(Sep) : path) + "|" + role;

    private static TrustVerdict? Problem(PathSecurity o)
    {
        if (!IsTrustedSid(o.OwnerSid))
            return TrustVerdict.No($"{o.OwnerName ?? o.OwnerSid ?? "an unknown account"} owns {o.Path}, so it can change its permissions.", o.OwnerSid);
        if (o.IsLink)
            return TrustVerdict.No($"{o.Path} is a link or junction, which could be pointed somewhere else.");
        if (o.Entries is null)
            return TrustVerdict.No($"{o.Path} has no access list, so everyone can change it.");

        var dangerous = DangerousRights(o);
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

    // ---- reading the real file system ------------------------------------------------------------------------------

    /// <summary>
    /// Reads the permissions of <paramref name="folder"/>, everything inside it, and every folder above it, and decides.
    /// Any error counts as not trusted.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static TrustVerdict CheckFolder(string folder) => Check(folder, targetIsFile: false, TrustScope.FolderTree);

    /// <summary>
    /// Reads the permissions of an executable, its folder (as much as <paramref name="scope"/> says) and every folder
    /// above it, and decides. Any error counts as not trusted.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static TrustVerdict CheckExecutable(string exe, TrustScope scope = TrustScope.FolderTree) => Check(exe, targetIsFile: true, scope);

    [SupportedOSPlatform("windows")]
    private static TrustVerdict Check(string path, bool targetIsFile, TrustScope scope)
    {
        try
        {
            if (PathProblem(path) is { } bad) return TrustVerdict.No(bad);
            if (path.Length > 3) path = path.TrimEnd(Sep);
            if (targetIsFile ? !File.Exists(path) : !Directory.Exists(path)) return TrustVerdict.No($"{path} does not exist.");
            bool fixedDrive = new DriveInfo(path[..3]).DriveType == DriveType.Fixed;
            return WithName(Evaluate(path, targetIsFile, fixedDrive, fixedDrive ? Gather(path, targetIsFile, scope) : Array.Empty<PathSecurity>()));
        }
        catch (Exception ex) { return TrustVerdict.No($"Could not read the permissions of {path}: {ex.Message}"); }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<PathSecurity> Gather(string path, bool targetIsFile, TrustScope scope)
    {
        var folder = targetIsFile ? Path.GetDirectoryName(path)! : path;
        if (targetIsFile) yield return ReadObject(path, TrustRole.File);
        yield return ReadObject(folder, TrustRole.Folder);
        for (var d = Path.GetDirectoryName(folder); d is not null; d = Path.GetDirectoryName(d))
            yield return ReadObject(d, TrustRole.Ancestor);

        if (targetIsFile && scope == TrustScope.ExecutableOnly) yield break;
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = !targetIsFile || scope == TrustScope.FolderTree,
            IgnoreInaccessible = false, AttributesToSkip = 0,
        };
        // A linked subfolder is read as a link and refused before anything below it matters. FolderFiles only reads the
        // files next to the exe (where Windows looks for its DLLs), not the subfolders.
        if (opts.RecurseSubdirectories)
            foreach (var d in Directory.EnumerateDirectories(folder, "*", opts)) yield return ReadObject(d, TrustRole.Folder);
        foreach (var f in Directory.EnumerateFiles(folder, "*", opts)) yield return ReadObject(f, TrustRole.File);
    }

    /// <summary>Reads one file or folder's owner, access list and link flag. A link is read itself, never its target.</summary>
    [SupportedOSPlatform("windows")]
    public static PathSecurity ReadObject(string path, TrustRole role)
    {
        bool isLink = IsNameSurrogate(path);
        const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Access;
        FileSystemSecurity security = role == TrustRole.File ? new FileInfo(path).GetAccessControl(sections) : new DirectoryInfo(path).GetAccessControl(sections);
        return FromDescriptor(path, role, new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0), isLink);
    }

    /// <summary>Turns a security descriptor into the facts <see cref="Decide"/> needs. Pure, so it can be tested with SDDL.</summary>
    [SupportedOSPlatform("windows")]
    public static PathSecurity FromDescriptor(string path, TrustRole role, RawSecurityDescriptor sd, bool isLink = false)
    {
        var owner = sd.Owner?.Value;
        if (sd.DiscretionaryAcl is null) return new PathSecurity(path, role, owner, null, IsLink: isLink);

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
        return new PathSecurity(path, role, owner, entries, IsLink: isLink);
    }

    /// <summary>
    /// True for symbolic links, junctions and other name-surrogate reparse points (they redirect to another path).
    /// Other reparse points, such as files compressed by Windows (WOF) or deduplicated, hold data and are not links.
    /// If the tag cannot be read, a reparse point counts as a link.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool IsNameSurrogate(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) return false;
        var h = FindFirstFileExW(path, 1 /* FindExInfoBasic */, out var data, 0, IntPtr.Zero, 0);
        if (h == new IntPtr(-1)) return true;
        FindClose(h);
        const uint NameSurrogate = 0x20000000; // IsReparseTagNameSurrogate
        return (data.dwFileAttributes & 0x400) == 0 || (data.dwReserved0 & NameSurrogate) != 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr FindFirstFileExW(string fileName, int infoLevel, out Win32FindData data, int searchOp, IntPtr filter, int flags);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool FindClose(IntPtr handle);

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

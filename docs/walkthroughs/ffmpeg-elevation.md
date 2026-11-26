# Starting FFmpeg safely from an elevated process: a walkthrough of the H3 fix

This walkthrough is for a mid-level developer who wants to understand one security fix in Glance: how the elevated background
runner decides whether it may start `ffmpeg.exe`. It covers the commit `e5cd903` on branch `claude/fix-ffmpeg-elevation-a3dfcc`,
which fixes finding **H3** in `docs/reviews/review-full-codebase.md`.

It assumes you know C# and .NET and have started a child process with `Process.Start`. You do not need to know Windows security
internals: ACLs, SIDs, UAC and winget scopes are explained where they first appear.

**Contents**

1. [The problem](#1-the-problem)
2. [Technologies at a glance](#2-technologies-at-a-glance)
3. [High-level walkthrough](#3-high-level-walkthrough)
4. [Detailed code review](#4-detailed-code-review)
   - 4.1 [Facts in, verdict out: the pure trust rule](#41-facts-in-verdict-out-the-pure-trust-rule)
   - 4.2 [Why three different masks](#42-why-three-different-masks)
   - 4.3 [Owners, inherit-only entries and deny entries](#43-owners-inherit-only-entries-and-deny-entries)
   - 4.4 [Reading the facts from disk](#44-reading-the-facts-from-disk)
   - 4.5 [The search: candidates, caching and logging](#45-the-search-candidates-caching-and-logging)
   - 4.6 [The runner and the video widget](#46-the-runner-and-the-video-widget)
   - 4.7 [The setup check and the all-users install](#47-the-setup-check-and-the-all-users-install)
   - 4.8 [The tests](#48-the-tests)
   - 4.9 [What the fix does not cover](#49-what-the-fix-does-not-cover)
5. [Self-review: five ways to improve the change](#5-self-review-five-ways-to-improve-the-change)

---

## 1. The problem

Glance has two processes. The editor runs as the normal user. The runner runs **as administrator**, because LibreHardwareMonitor
needs admin rights to read CPU and motherboard sensors. The runner can be started at logon by a scheduled task, with no UAC prompt.

The runner draws themes on the LCD. When a theme has a video background, `VideoSource` starts `ffmpeg.exe` and reads raw frames
from its stdout. Before this fix, `Ffmpeg.Find()` looked for `ffmpeg.exe` in this order:

1. next to the app (`<app>\ffmpeg\ffmpeg.exe`, `<app>\ffmpeg.exe`);
2. every folder on `PATH`, *including the user's own PATH entries*;
3. `%LOCALAPPDATA%\Microsoft\WinGet\Links\ffmpeg.exe` and a recursive search of `%LOCALAPPDATA%\Microsoft\WinGet\Packages\*FFmpeg*`.

The editor's own "Install FFmpeg" button ran a *user-scope* winget install, which puts FFmpeg exactly into places 2 and 3. All
of those folders can be written by any program the user runs, without elevation.

So the attack was simple. Malware running as the user replaces `ffmpeg.exe` in the user's winget folder, then writes a theme
with a video background (the runner watches `%AppData%\Glance` and reloads it). The runner starts "ffmpeg" as administrator.
That is a **silent elevation of privilege**: admin rights without a UAC prompt.

The brief for the fix asked for:

- when elevated, only start an ffmpeg that a non-admin cannot replace;
- keep video working in the normal case;
- say clearly in the setup check when FFmpeg is only in a user-writable place;
- log rejected candidates;
- make the trust decision a pure function that can be tested.

---

## 2. Technologies at a glance

| Area | What you need to know |
|---|---|
| **UAC and split tokens** | An administrator on Windows normally runs with a *filtered* token, which behaves like a normal user's. "Run as administrator" starts a process with the *full* token, after a UAC prompt. Both tokens belong to the **same user account**, so the elevated process sees the same profile, the same `%LOCALAPPDATA%` and the same user `PATH`. That is why an elevated process must not trust per-user folders. |
| **Detecting elevation in .NET 8** | `Environment.IsPrivilegedProcess` is true when the current process runs elevated (or as SYSTEM). |
| **Security descriptor** | Every NTFS file and folder has an **owner** (a SID) and a **DACL** (discretionary access control list). The DACL is a list of **ACEs** (access control entries): allow or deny, a SID, and an access mask. |
| **SID** | A security identifier. The well-known ones used here: `S-1-5-18` SYSTEM, `S-1-5-32-544` Administrators, `S-1-5-80-956008885-...` TrustedInstaller, `S-1-5-32-545` Users, `S-1-5-11` Authenticated Users, `S-1-1-0` Everyone, `S-1-5-4` INTERACTIVE, `S-1-3-0` CREATOR OWNER, `S-1-3-4` OWNER RIGHTS. A normal user's own SID looks like `S-1-5-21-...-1001`. |
| **Access mask** | 32 bits of rights. For files: `FILE_WRITE_DATA` 0x2, `FILE_APPEND_DATA` 0x4. On folders the same bits mean "add file" and "add subfolder". Also `FILE_DELETE_CHILD` 0x40 (folders), `DELETE` 0x10000, `WRITE_DAC` 0x40000 (change the ACL), `WRITE_OWNER` 0x80000 (take ownership), and the *generic* bits `GENERIC_ALL` 0x10000000 and `GENERIC_WRITE` 0x40000000, which Windows maps to specific rights. |
| **Inheritance** | ACEs can flow down from a folder to its children. An **inherit-only** ACE applies only to children, never to the folder itself. Inherited ACEs show up on the child as ordinary entries marked "inherited". |
| **Owner rights** | The owner of an object can always read and rewrite its DACL, whatever the DACL says (unless an OWNER RIGHTS ACE limits it). So "who owns it" matters as much as "who is in the DACL". |
| **Reparse points** | Symlinks, junctions and App Execution Aliases (such as `winget.exe` in `WindowsApps`) are reparse points: a name that redirects somewhere else. `File.GetAttributes` reports `FileAttributes.ReparsePoint` on the link itself, without following it. |
| **winget scopes** | winget can install a package for the current user (`--scope user`, the default for portable packages) or for all users (`--scope machine`, which needs admin). `Gyan.FFmpeg` is a *portable* package (a zip with exes). User scope lands in `%LOCALAPPDATA%\Microsoft\WinGet\Packages\...`. Machine scope is expected under `%ProgramFiles%\WinGet\Packages\...`, with symlinks in a `Links` folder. |
| **`runas`** | `ProcessStartInfo { Verb = "runas", UseShellExecute = true }` asks the shell to start the program elevated, which shows a UAC prompt. If the user says No, `Process.Start` throws a `Win32Exception` with `NativeErrorCode` 1223 (`ERROR_CANCELLED`). Output cannot be redirected through the shell. |
| **.NET ACL APIs** | `FileInfo.GetAccessControl()` and `DirectoryInfo.GetAccessControl()` (from `System.IO.FileSystem.AccessControl`) return a `FileSystemSecurity`. `GetOwner(typeof(SecurityIdentifier))` and `GetAccessRules(includeExplicit, includeInherited, typeof(SecurityIdentifier))` give the owner and the ACEs as SIDs. |

---

## 3. High-level walkthrough

### 3.1 The idea

Instead of guessing which folders are "safe" by name, the fix **reads the real permissions** of every candidate `ffmpeg.exe` and of
every folder above it. A candidate is trusted only if **no one except SYSTEM, Administrators or TrustedInstaller** can do any
of these:

- change or replace the file;
- add files next to it (a planted DLL would be loaded by ffmpeg);
- rename, delete or re-permission any folder on the way to it;
- own any object on the way (an owner can always rewrite the permissions);

and only if nothing on the path is a link or junction, and the file is on a fixed local drive.

This is an **allow-list**: it lists who *may* write, not who may not. A deny-list ("reject Users, Everyone, Authenticated Users...")
always misses some group: a domain group, the logon SID, a capability SID. With an allow-list, any group nobody thought of
counts as untrusted.

### 3.2 The pieces

```
                         elevated?  ── no ──>  first ffmpeg.exe that exists (unchanged behaviour, the editor)
Ffmpeg.Find()  ──>  Search(...)  │
                         └─ yes ─>  for each candidate that exists:
                                      FfmpegTrust.Check(path)
                                        ├─ ReadFact(file), ReadFact(each folder up to C:\)   (Windows ACLs)
                                        └─ Evaluate(path, chain)                             (pure rule)
                                      trusted?  return it
                                      else      Report(path, reason)  ──>  Ffmpeg.Rejected  ──>  runner.log
```

| File | Role in the fix |
|---|---|
| `Glance.Core/Media/FfmpegTrust.cs` (new) | The trust rule (`Evaluate`, pure) and the ACL reader (`Check`). |
| `Glance.Core/Media/Ffmpeg.cs` | The candidate list, the elevated-only filter, caching, the `Rejected` event and `LastRejection`. |
| `Glance.Core/Media/VideoSource.cs` | Shows a clearer error on the display when FFmpeg was rejected. |
| `Glance.Core/Setup/SetupChecks.cs` | The FFmpeg row now says whether the *runner* can use the copy found, and installs for all users. |
| `Glance.App/Views/SetupWindow.cs`, `Glance.App/MainWindow.cs` | Run the all-users install (UAC prompt) and handle "No". |
| `Glance.Runner/RunnerContext.cs` | Writes rejections to `runner.log`. |
| `Glance.Tests/FfmpegTrustTests.cs` (new) | Table tests of the rule, two tests of the real reader, and tests of the setup row. |

### 3.3 The user's journey after the fix

- **FFmpeg installed for all users** (or bundled next to an app installed in Program Files): the setup row is green, and video
  plays in the editor and in the runner.
- **FFmpeg only in the user's winget folder** (what the old button installed): the editor preview still plays video, because
  the editor is not elevated. The runner refuses it and logs why. The LCD shows "FFmpeg is only in a folder normal users can
  change (see runner.log)". The setup row turns into a **warning** with an "Install for all users" button.
- **No FFmpeg:** the setup row is informational, and its button now installs for all users. Windows asks for permission.

---

## 4. Detailed code review

### 4.1 Facts in, verdict out: the pure trust rule

**File:** `src/Glance.Core/Media/FfmpegTrust.cs`

The rule does not touch the disk. It works on plain records, so the tests can describe any ACL they like:

```csharp
public sealed record AceFact(string Sid, bool Allow, int Rights, bool InheritOnly = false);

public sealed record PathFact(string Path, string? OwnerSid, IReadOnlyList<AceFact> Aces, bool IsReparsePoint = false);

public readonly record struct TrustVerdict(bool Trusted, string Reason);
```

`Evaluate` takes the exe path and a **chain**: the file first, then each folder above it, ending at the drive root.

```csharp
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
```

Points to notice:

- **The input is checked before it is trusted.** The chain must start at the exe and end at a drive root. A caller that
  forgets a folder, or passes the chain of a different file, gets "could not be read completely" and no accidental "trusted".
- **The first problem wins**, and the reason names the principal and the object ("Users can change the folder C:\x\bin"). That
  text ends up in `runner.log`, so it is written for a person.
- `IsLocalDrivePath` accepts only `X:\...` with no `\..`. That rejects UNC paths (`\\server\share`), device paths (`\\?\C:\`)
  and relative paths in one line.

### 4.2 Why three different masks

```csharp
internal const int FileMask = WriteData | AppendData | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite;
internal const int ParentMask = FileMask | DeleteChild;
internal const int AncestorMask = DeleteChild | Delete | WriteDac | WriteOwner | GenericAll;
```

Which rights are dangerous depends on where they sit:

- **On the file**, anything that changes its bytes (write, append), removes it (delete), or rewrites its permissions or owner.
- **On the folder holding it**, the same, plus `FILE_DELETE_CHILD`, which lets you delete or rename `ffmpeg.exe` even without
  rights on the file itself. Here `WriteData`/`AppendData` mean "add file" and "add subfolder", and *adding a file* next to an
  exe is the classic DLL-planting attack.
- **On folders further up**, only the rights that remove or rename an existing child (`Delete` on the folder, `DeleteChild` on its
  parent) or that change permissions. To rename a folder you need `DELETE` on it or `FILE_DELETE_CHILD` on its parent, and the
  loop checks both for every link of the chain.

Creating *new* entries higher up is deliberately allowed. It cannot replace `C:\Program Files\...` with something else, and it
has to be allowed, because a stock `C:\` grants Authenticated Users "create folders":

```csharp
private static PathFact Root() => new(@"C:\", TrustedInstaller, new List<AceFact>
{
    Allow(AuthUsers, AppendData),                                        // "create folders", this folder only
    Allow(AuthUsers, GenericReadWriteExecuteDelete, inheritOnly: true),
    Allow(System, FullControl), Allow(Admins, FullControl), Allow(Users, ReadAndExecute),
});
```

That fixture in the tests matches what Windows 11 really has on `C:\`. I checked the real ACL during the review:
`S-1-5-11 Allow 0x4` and `S-1-5-11 Allow 0xE0010000 inherit-only`.

The generic bits `GENERIC_ALL` and `GENERIC_WRITE` are in the masks because they appear unmapped on inherit-only entries and
sometimes on entries written by tools. Leaving them out would let `GENERIC_ALL` for Users slip through.

### 4.3 Owners, inherit-only entries and deny entries

```csharp
private static readonly HashSet<string> AdminPrincipals = new(StringComparer.OrdinalIgnoreCase)
{
    SystemSid, AdministratorsSid, TrustedInstallerSid, CreatorOwnerSid, OwnerRightsSid,
};
```

- **Owners.** Every object on the chain must be owned by an admin principal. A user-scope winget install is owned by the user's
  own SID, so it fails here first. On the reviewer's PC the real verdict was "the file is owned by `<user>`, not by an
  administrator".
- **CREATOR OWNER** (`S-1-3-0`) on an object is only a template for future children. It never matches a real token, so it grants
  nothing on the object itself. **OWNER RIGHTS** (`S-1-3-4`) means "whoever owns this", and the owner was already checked.
  So both are safe to skip *as ACE principals*. (They are in the same set used for the owner check, which is harmless but
  untidy; see section 5.)
- **Inherit-only ACEs** are skipped, because they do not apply to the object they sit on. Their effect on a child is already
  visible in the child's own ACL, which is read separately.
- **Deny ACEs are ignored.** That looks wrong at first, but think about which way it fails. Ignoring a deny can only make an
  object look *more* writable than it is, so the worst case is rejecting a file that is in fact safe. The test
  `Deny_entries_never_make_a_writable_file_trusted` pins this down.

### 4.4 Reading the facts from disk

`Check` turns a path into a chain and calls `Evaluate`:

```csharp
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
```

```csharp
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
```

Points to notice:

- **Every error means "not trusted".** If a folder cannot be read, the file is rejected. A security check must fail closed.
- **Network drives.** A mapped drive letter passes `IsLocalDrivePath`, so `DriveInfo.DriveType` catches it. A remote server's
  "Administrators" group is not this PC's.
- **`typeof(SecurityIdentifier)`** keeps SIDs as SIDs. Translating to names (`NTAccount`) is slow and can fail for deleted accounts,
  so names are looked up only for the one SID that goes into an error message (`Describe`/`TryName`).
- **`(int)rule.FileSystemRights`** is the raw mask, generic bits included.
- **What .NET does with odd ACLs.** During the review, the compiled code was run on .NET 8 against two crafted files. A **NULL
  DACL** (no ACL at all, meaning everyone has full access) comes back as one `Everyone / -1` rule, and is rejected. A
  **conditional ACE** (`XA;;FA;;;BU;(...)`) comes back as a plain allow for Users, and is rejected. Both fail closed.

### 4.5 The search: candidates, caching and logging

**File:** `src/Glance.Core/Media/Ffmpeg.cs`

The candidate list is now a public iterator, and it adds the **machine-scope** winget folders before the per-user ones:

```csharp
// winget: "--scope machine" puts portable packages under Program Files (only admins can change them),
// the default user scope under %LOCALAPPDATA% (the user can). The Links entries are symlinks, so the real
// files under Packages are listed as well.
foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetEnvironmentVariable("LOCALAPPDATA") is { } l ? Path.Combine(l, "Microsoft") : null })
{
    if (string.IsNullOrEmpty(root)) continue;
    yield return Path.Combine(root, "WinGet", "Links", "ffmpeg.exe");
    var pkgs = Path.Combine(root, "WinGet", "Packages");
    ...
    foreach (var f in found) yield return f;
}
```

Enumerating `Packages` directly matters for two reasons. The `Links` entry is a symlink, which the rule rejects. And the runner's
`PATH` is a snapshot from when it started, so a fresh machine install would not be on it until the runner restarts.

The filter is small:

```csharp
internal static string? Search(bool requireAdminOnly, Action<string, string>? onRejected)
{
    foreach (var candidate in Candidates().Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (!File.Exists(candidate)) continue;
        if (!requireAdminOnly) return candidate;
        var verdict = FfmpegTrust.Check(candidate);
        if (verdict.Trusted) return candidate;
        onRejected?.Invoke(candidate, verdict.Reason);
    }
    return null;
}
```

And `Find` decides whether to filter based on the current process:

```csharp
public static string? Find()
{
    lock (Gate)
    {
        if (_cached is not null && File.Exists(_cached)) return _cached;
        if (_cached is null && DateTime.UtcNow - _checked < TimeSpan.FromSeconds(5)) return null; // don't hammer the disk
        _checked = DateTime.UtcNow;
        LastRejection = null;
        return _cached = Search(Environment.IsPrivilegedProcess, Report);
    }
}
```

Points to notice:

- **The editor is unchanged.** It is not elevated, so it takes the first existing candidate, as before. The only difference is
  that it now prefers an all-users copy if one exists.
- **The lock is new.** Several renderers (the editor preview, the editor's own pusher, the virtual display) can call `Find`
  on different threads. Before this change, `_cached` and `_checked` were read and written without one.
- **Is the cache safe? (TOCTOU, "time of check to time of use").** A trusted path is cached and started later without
  re-checking. That is fine *because of what "trusted" means*: nothing on that path can be changed by a non-admin, so the gap
  between the check and `Process.Start` gives an attacker nothing to race.
- **`FindForElevated()`** runs the strict search from the *non-elevated* editor, uncached, so the setup check can predict what the
  runner will do.
- **Logging.** `Report` builds a sentence, stores it in `LastRejection`, and raises `Rejected` once per path and reason:

```csharp
private static void Report(string path, string reason)
{
    var message = $"FFmpeg at {path} is not used: {reason}. The runner runs as administrator, so it only starts FFmpeg " +
                  "from a folder that only administrators can change (install it for all users).";
    LastRejection = message;
    if (Reported.Add(path + "|" + reason))
        try { Rejected?.Invoke(message); } catch { /* logging must never break the search */ }
}
```

### 4.6 The runner and the video widget

The runner subscribes once, at startup (`src/Glance.Runner/RunnerContext.cs`):

```csharp
Glance.Core.Media.Ffmpeg.Rejected += Log; // say why a user-writable ffmpeg.exe is not started as administrator
```

`VideoSource` turns a rejection into a message the user can act on. `ThemeRenderer.DrawImage` draws `v.Error` inside the
widget, so it appears on the LCD itself:

```csharp
var ffmpeg = Ffmpeg.Find();
if (ffmpeg is null)
{
    Error = Ffmpeg.LastRejection is not null ? "FFmpeg is only in a folder normal users can change (see runner.log)" : "FFmpeg not found";
    return;
}
```

The process start itself did not change. It still uses `ArgumentList`, so the video path cannot inject arguments.

### 4.7 The setup check and the all-users install

**File:** `src/Glance.Core/Setup/SetupChecks.cs`

The FFmpeg row is now a pure function of two answers: what this process found, and what the runner would accept. That makes it
easy to test:

```csharp
public static CheckResult DiagnoseFfmpeg(string? found, string? usableByRunner)
{
    const string id = "ffmpeg", title = "FFmpeg (video backgrounds)";
    if (usableByRunner is not null)
        return new CheckResult(id, title, CheckStatus.Ok, "Found. Video backgrounds are ready, in the editor and in the background runner.");
    if (found is not null)
        return new CheckResult(id, title, CheckStatus.Warning,
            $"Found at {found}, but that folder can be changed without administrator rights. ...",
            "Install for all users", FixKind.WingetInstallMachine, FfmpegId);
    return new CheckResult(id, title, CheckStatus.Info, "Optional. ...", "Install FFmpeg", FixKind.WingetInstallMachine, FfmpegId);
}
```

It is a **Warning**, not a **Problem**: video is optional, so it must not make `NeedsAttention` true. A test checks this.

The all-users install is a new `FixKind` and one helper shared by the setup window and the editor:

```csharp
public static ProcessStartInfo WingetInstallStartInfo(string packageId, bool machineScope)
{
    var args = $"install --id {packageId} --exact --accept-package-agreements --accept-source-agreements --disable-interactivity";
    if (!machineScope)
        return new ProcessStartInfo("winget", args)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    // runas needs the shell, which rules out redirecting output; the exit code still comes back
    return new ProcessStartInfo("winget", args + " --scope machine")
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
}
```

And the UI handles "No" on the UAC prompt separately from real failures:

```csharp
catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == SetupChecks.UacCancelled) { Say("Install cancelled: Windows did not get permission."); }
```

One trap is handled with words rather than code. If FFmpeg is already installed for the user, winget may decline to add a
machine copy. `FfmpegMachineInstallHelp` tells the user to remove the user copy first, or to run the machine install from an
admin terminal.

### 4.8 The tests

**File:** `src/Glance.Tests/FfmpegTrustTests.cs` (45 new test cases; the suite goes from 253 to 298)

The rule tests start from one realistic, trusted chain (a machine-scope winget layout under `C:\Program Files`, with the real
`C:\` entries) and change **one fact** per test:

```csharp
private static List<PathFact> With(int index, Func<PathFact, PathFact> change)
{
    var chain = GoodChain();
    chain[index] = change(chain[index]);
    return chain;
}

[Theory]
[InlineData(WriteData)]    // create files: plant a DLL next to ffmpeg.exe
[InlineData(AppendData)]   // create folders
[InlineData(DeleteChild)]  // delete or rename ffmpeg.exe
[InlineData(Delete)]
[InlineData(WriteDac)]
[InlineData(GenericWrite)]
public void Writes_in_the_folder_holding_ffmpeg_reject_it(int rights)
{
    var v = Eval(With(1, f => AddAce(f, Allow(Users, rights))));
    Assert.False(v.Trusted);
    Assert.Contains(@"\bin", v.Reason);
}
```

This "golden fixture plus one mutation" pattern is worth copying. Each test documents one attack, and the positive test
(`Admin_only_install_under_Program_Files_is_trusted`) proves the fixture really is valid, so the negative tests are failing
for the reason they name.

There are also tests for the other two positions (the file, the folders further up), for the harmless "create folders" right
higher up, and for inherit-only, deny, owners, links, network paths and incomplete chains. Two tests run the **real reader**:
`cmd.exe` must be trusted, and a fresh file in `%TEMP%` must be rejected.

A mutation check during the review showed how strong this is. Removing `DeleteChild` from `ParentMask` or disabling the link
rule makes three tests fail. It also showed the gap: turning the elevated filter in `Search` off entirely **passes every test**,
because `Search` is internal and only the pure rule is tested. See section 5.

### 4.9 What the fix does not cover

- **The trusted ffmpeg still runs as administrator**, and it still decodes files that a normal process can write (themes in
  `%AppData%\Glance`, or `LIVE` over the pipe). A crafted video aimed at an FFmpeg parser bug is still a way up. The original
  finding called de-elevating ffmpeg the "better" fix. This change does the "only start a trusted binary" half.
- **Nobody has run the machine-scope install in a test.** The design assumes winget puts portable packages for all users under
  `%ProgramFiles%\WinGet\Packages` with the ACL of Program Files. That should be verified once on a clean machine (see the review,
  M1).
- **Existing user-scope installs stop working in the runner** until FFmpeg is installed for all users. That is the intended
  trade-off, but users need to be told.

---

## 5. Self-review: five ways to improve the change

### 1. Merge the two trust checkers into one

The autostart fix (`claude/fix-autostart-task-0011d2`) adds `Glance.Core.Security.FolderTrust`, which answers the same question
for the runner's own folder. Each has something the other lacks:

- `FfmpegTrust` rejects links and junctions and non-fixed drives, and checks that the chain is complete.
- `FolderTrust` reads the raw security descriptor: it handles NULL DACLs and unknown ACE types explicitly, and keeps a stricter
  owner set.

There are also two `TrustVerdict` types in two namespaces. Keep one module (`FolderTrust`) with a `CheckExecutable(exe)` entry
(file, parent folder, ancestors), port the reparse-point, drive and chain-completeness rules into it, and delete `FfmpegTrust`.
Two copies of a security rule will drift apart.

### 2. Test the wiring, not only the rule

The security property depends on two lines: `Search(Environment.IsPrivilegedProcess, Report)` and
`if (!requireAdminOnly) return candidate;`. Make the search take its inputs (candidates, an `exists` function, the elevated flag,
a `check` function) and test it with fakes:

- elevated: an untrusted first candidate is skipped and reported, and a trusted later one is returned;
- elevated, nothing trusted: `null`;
- not elevated: the first existing candidate, without calling `check`.

Then do the mutation check again.

### 3. Run ffmpeg de-elevated, or at least narrow what it can do

Start ffmpeg with a normal-user token (`SaferComputeTokenFromLevel(SAFER_LEVELID_NORMALUSER)` + `CreateProcessAsUser`, or the
linked filtered token), keeping the stdout pipe. Then the binary's location matters much less, and a malicious video gets user
rights at most. Until then:

- pass `-protocol_whitelist file,pipe` before `-i`, so a disguised playlist cannot reach other files or the network;
- set `WorkingDirectory` to the ffmpeg folder;
- give the child a `PATH` with only the system folders, instead of the user's.

### 4. Log only what the user needs to act on

In the recommended setup (an all-users install), the `Links\ffmpeg.exe` symlink is checked first, rejected as a link, and
logged with "install it for all users". Only then is the real file under `Packages` accepted. Collect rejections during the search
and log them only when *nothing* was found. Or resolve the link with `FileSystemInfo.ResolveLinkTarget(true)` and judge both
the link's chain and the target's. Also clear `LastRejection` after a successful search, and raise `Rejected` outside the lock.

### 5. Close the loop on the install

- Verify the all-users winget install once, and write down where the files land and what ACL they get.
- In `MainWindow`, decide "FFmpeg installed, video is ready" with `FindForElevated()`, not `Find()`. Today the editor can say
  "ready" while the runner still refuses the copy.
- Let a `VideoSource` that failed for lack of FFmpeg retry after a few seconds. Today the LCD keeps the error until the theme
  reloads.
- Consider a deterministic option: the installer already puts Glance in Program Files, so an optional "include FFmpeg" step that
  drops it into `{app}\ffmpeg\` (the first candidate) would be trusted by construction, with no dependence on winget's layout.

# Auto-start without a hijack: a walkthrough of the H6 fix

This walkthrough is for a mid-level developer who wants to understand one change in Glance: how the "start the runner when I sign in"
feature was rebuilt so that a normal program cannot use it to get administrator rights. It covers the problem, the Windows features
involved, the new code piece by piece, and where the change could still be better.

It assumes you know C# and .NET. You do not need to know Task Scheduler, UAC or Windows ACLs: each is explained where it first
appears. The change is the commit "Fix H6: let the elevated runner register the autostart task itself" on branch
`claude/fix-autostart-task-0011d2`. The finding it fixes is H6 in `docs/reviews/review-full-codebase.md`.

**Contents**

1. [The problem](#1-the-problem)
2. [Technologies at a glance](#2-technologies-at-a-glance)
3. [High-level walkthrough](#3-high-level-walkthrough)
4. [Detailed code review](#4-detailed-code-review)
   - 4.1 [Routing in the runner's `Main`](#41-routing-in-the-runners-main)
   - 4.2 [The command line: building, parsing and validating](#42-the-command-line-building-parsing-and-validating)
   - 4.3 [The task definition and XML escaping](#43-the-task-definition-and-xml-escaping)
   - 4.4 [The elevated installer: who, what and a read-back](#44-the-elevated-installer-who-what-and-a-read-back)
   - 4.5 [Folder trust: reading ACLs and deciding](#45-folder-trust-reading-acls-and-deciding)
   - 4.6 [The editor side](#46-the-editor-side)
   - 4.7 [Tests](#47-tests)
5. [Self-review: five ways to improve the change](#5-self-review-five-ways-to-improve-the-change)

---

## 1. The problem

The Glance runner reads CPU and motherboard sensors, and Windows only allows that for administrators. So `Glance.Runner.exe` has a
manifest that says `requireAdministrator`, and starting it shows a UAC prompt. Nobody wants that prompt at every sign-in. The usual
Windows answer is a **scheduled task** that runs the program "with highest privileges" when the user signs in: you approve one
prompt while creating the task, and Windows starts the program elevated from then on, silently.

That silence is the danger. A task that silently starts an admin program is worth attacking. If a normal (non-admin) process can
change *what* the task starts, it gets admin rights at the next sign-in without any prompt. The full review found two ways to do it.

**Problem A: a file swapped during the prompt.** The old editor wrote the task definition to a fixed file and asked an elevated
`schtasks.exe` to read it:

```csharp
var xmlPath = Path.Combine(Path.GetTempPath(), "GlanceRunnerTask.xml");
File.WriteAllText(xmlPath, BuildXml(runnerExe), Encoding.Unicode); // schtasks expects UTF-16
var err = RunElevated($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
```

`%TEMP%` belongs to the user, so any program running as the user can rewrite that file while the UAC prompt is open. The user
approves a Microsoft-signed `schtasks.exe` and gets a task that runs whatever the attacker wrote.

**Problem B: a task pointing at a writable folder.** The task ran whichever `Glance.Runner.exe` the editor found: an installed copy,
the zip extracted under `Downloads`, or a dev `bin` folder. In the last two, the user can replace `Glance.Runner.exe`, or any DLL
next to it. After that, every sign-in runs the replacement as admin.

The brief for the fix asked for these things:

- The elevated runner registers and removes the task itself (`--install-autostart`, `--remove-autostart`), without starting the
  tray app, opening the display or taking the single-instance mutex.
- The interactive user is passed explicitly and validated.
- The task name and settings stay the same.
- Auto-start is refused with a clear message when the runner's folder is writable by non-admins.
- The registered task is read back and checked.
- A small deadlock in `IsInstalled` is fixed.
- The pure logic (`BuildXml`, the trust decision, argument parsing) lives in Core with tests.

---

## 2. Technologies at a glance

| Area | Technology | What you need to know here |
|---|---|---|
| Starting at sign-in | **Windows Task Scheduler** | A task has *triggers* (here: at logon of one user), a *principal* (who it runs as, and `RunLevel`), *settings*, and *actions* (here: one `Exec` with a `Command` and a `WorkingDirectory`). `RunLevel HighestAvailable` means "the user's full admin token if they have one", with no prompt. A task can be described as XML in the `http://schemas.microsoft.com/windows/2004/02/mit/task` namespace. |
| Talking to Task Scheduler | **Task Scheduler 2.0 COM API** (`Schedule.Service`) | `Connect()`, `GetFolder("\\")`, then `RegisterTask(name, xml, flags, user, password, logonType, sddl)`, `GetTask(name).Xml` and `DeleteTask(name, 0)`. The code calls it through C# `dynamic` (late-bound IDispatch), so no interop assembly is needed. Failures arrive as .NET exceptions mapped from the HRESULT: `0x80070002` becomes `FileNotFoundException`, not `COMException`. |
| Elevation | **UAC**, the `requireAdministrator` manifest, `ShellExecute` with `Verb = "runas"` | `Process.Start` with `UseShellExecute = true, Verb = "runas"` shows the prompt. If the user says no, it throws `Win32Exception` with `NativeErrorCode == 1223` (`ERROR_CANCELLED`). If they say yes, you get a `Process` whose exit code you can read. A *standard* user sees a credentials prompt instead ("over-the-shoulder" elevation): the elevated process then runs as the admin who typed the password, in the standard user's session. |
| Who is at the screen | **Remote Desktop Services API** (`WTSQuerySessionInformationW`) | Every process belongs to a session (`Process.SessionId`). `WTSUserName` and `WTSDomainName` give the user signed in to that session. That user can differ from the account an elevated process runs as. |
| Accounts | `NTAccount`, `SecurityIdentifier`, `WindowsIdentity` | `new NTAccount("PC\\ana").Translate(typeof(SecurityIdentifier))` resolves a name (also a UPN like `ana@corp.com`) to a SID, which is the real identity. Translating back gives the canonical `DOMAIN\name`. |
| File permissions | **Security descriptors and ACLs** | Every file and folder has an **owner** and a **DACL**: a list of ACEs (access control entries). Each ACE is *allow* or *deny*, names a SID and has an access mask (bits such as `FILE_WRITE_DATA 0x2`, `DELETE 0x10000`, `WRITE_DAC 0x40000`). ACEs can be *inherit-only*: they apply to children, not to the object itself. The owner can always rewrite the DACL. `RawSecurityDescriptor` in .NET gives you the raw entries. |
| Well-known SIDs | `S-1-5-18` SYSTEM, `S-1-5-32-544` Administrators, `S-1-5-80-9560...` TrustedInstaller, `S-1-5-32-545` Users, `S-1-5-11` Authenticated Users, `S-1-1-0` Everyone, `S-1-3-0` CREATOR OWNER, `S-1-3-4` OWNER RIGHTS | Only the first three are trusted by the new check. |
| Command lines | Windows argument splitting (`CommandLineToArgvW` rules) | A program receives one string and the runtime splits it. Quotes group words. A backslash is literal *unless* it comes before a quote, so a value ending in `\` can "eat" its closing quote. |
| XML | `System.Security.SecurityElement.Escape`, `XDocument` | `Escape` replaces `& < > " '` with entities. `XDocument.Parse` is used to read the task back. |
| Tests | **xUnit**, plus mutation checks | Pure functions are tested directly. A "mutation check" breaks the code on purpose to prove a test notices. |

---

## 3. High-level walkthrough

### 3.1 Before

```
Glance.App (normal user)                                  elevated (after UAC)
  BuildXml(runnerExe) -> %TEMP%\GlanceRunnerTask.xml  ->   schtasks.exe /Create /XML %TEMP%\GlanceRunnerTask.xml
                         ^ any user process can rewrite it while the prompt is open
  runnerExe = whatever FindRunnerExe() found (zip folder, dev bin, Program Files)
```

### 3.2 After

```
Glance.App (normal user)                                  Glance.Runner.exe (elevated, after UAC)
  1. FolderTrust.Check(runner folder)  -- refuse early         Program.Main: ParseArgs(args)
  2. user = WindowsIdentity.GetCurrent().Name                     -> AutostartInstaller.Run (no tray, no mutex, no display)
  3. runas Glance.Runner.exe --install-autostart --user "PC\ana"     a. am I elevated?
                                                                     b. does "PC\ana" resolve to the session's signed-in user?
                                                                     c. FolderTrust.Check(my own folder)
                                                                     d. BuildXml(Environment.ProcessPath, canonical user) in memory
                                                                     e. RegisterTask via COM (no file)
                                                                     f. read the task back; delete it if it is not exactly me
  4. map the exit code (0, 10-17) to a message  <---------------  exit code
```

Three ideas carry the design:

1. **The elevated side decides what to register.** The task's command is the elevated process's *own* path
   (`Environment.ProcessPath`), and the definition never leaves memory. A normal process can at most ask the trusted runner to
   register *itself*.
2. **Only admin-only folders qualify.** Auto-start is refused unless SYSTEM, Administrators and TrustedInstaller are the only ones who
   can change the program folder, anything inside it, or the folders above it. Both processes check: the editor so the user gets a
   readable reason before any prompt, and the runner because it is the one that acts.
3. **Pure logic in Core.** Argument building and parsing, user-name validation, the XML, the read-back check, the exit-code messages
   and the trust decision are pure functions in `Glance.Core`, so `Glance.Tests` can cover them. The Windows-specific parts (COM,
   WTS, reading ACLs) are thin.

### 3.3 Where the code lives

| File | Role |
|---|---|
| `src/Glance.Core/Setup/AutostartTask.cs` | Task name, switches, exit codes and messages, `ValidateUserName`, `InstallArguments`, `ParseArgs`, `BuildXml`, `VerifyRegistered`, `SameExecutable`. All pure. |
| `src/Glance.Core/Security/FolderTrust.cs` | `Decide` (pure trust rule over ACL facts) and `Check` (reads real ACLs on Windows). |
| `src/Glance.Runner/AutostartInstaller.cs` | The elevated install and remove modes: WTS session user, COM registration, read-back. |
| `src/Glance.Runner/Program.cs` | Sends the two switches to `AutostartInstaller` before anything else runs. |
| `src/Glance.App/Services/Autostart.cs` | Editor side: folder check, `runas` the runner, map exit codes, `IsInstalled`. |
| `src/Glance.App/Views/ServiceWindow.cs` | The Background service window: shows the reason and disables "on" for untrusted copies. |
| `src/Glance.Tests/AutostartTests.cs` | 62 tests for the two Core classes. |

---

## 4. Detailed code review

### 4.1 Routing in the runner's `Main`

**File:** `src/Glance.Runner/Program.cs`

```csharp
[STAThread]
private static int Main(string[] args)
{
    // Auto-start registration mode: started by the editor through a UAC prompt, does its job and exits.
    // It must not take the single-instance mutex, show a tray icon or touch the display.
    var request = AutostartTask.ParseArgs(args);
    if (request.Mode == AutostartMode.Invalid) return AutostartTask.Exit.BadArguments;
    if (request.Mode != AutostartMode.None) return AutostartInstaller.Run(request);

    // one runner per login session
    using var mutex = new Mutex(true, @"Local\Glance.Runner", out var first);
    if (!first) return 0;

    ApplicationConfiguration.Initialize();
    Application.Run(new RunnerContext());
    return 0;
}
```

Points worth noticing:

- **Order matters.** The switch check comes *before* the mutex. A user who already has the tray runner open can still turn auto-start
  on: the second, short-lived runner does not compete for `Local\Glance.Runner`, and it does not try to open the USB display, which
  the first runner owns.
- **`Main` now returns `int`.** The exit code is the only channel back to the editor, because a process started through `runas`
  cannot have its output redirected.
- **Unknown arguments still mean a normal start** (`AutostartMode.None`). The scheduled task passes no arguments, and older
  shortcuts keep working. Only when one of the two switches is present must the whole command line be exactly right.

### 4.2 The command line: building, parsing and validating

**File:** `src/Glance.Core/Setup/AutostartTask.cs`

The editor passes the user's name on a command line that crosses a privilege boundary, so the name is validated on both sides.

```csharp
// Characters Windows does not allow in user or domain names (plus the quote, which would break the command line).
private const string ForbiddenNameChars = "\"/[]:;|=,+*?<>";

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

public static string InstallArguments(string user)
{
    if (ValidateUserName(user) is { } err) throw new ArgumentException(err, nameof(user));
    return $"{InstallSwitch} {UserSwitch} \"{user}\"";
}
```

Why this is enough to make the quoting safe:

- **No `"` is allowed**, so the value cannot close its own quotes and add a second switch. The test case
  `PC\Ana" --remove-autostart "x` is rejected.
- **No empty part is allowed**, so the name cannot end in `\`. Under Windows' splitting rules, `"PC\"` would turn the backslash
  and quote into a literal quote and swallow the rest of the line.
- **Spaces are fine** because the value is quoted: `CORP\Ana Marija` arrives as one argument.

Names with `&`, `'`, `@` or non-ASCII letters are allowed. `ShellExecute` does not run a shell, so `&` has no special meaning.

`ParseArgs` is strict once a switch is present. It is case-insensitive, accepts `--user` before or after the switch, and refuses
duplicates, unknown words, a missing value and "both modes":

```csharp
if (remove)
    return args.Count == 1 ? new AutostartRequest(AutostartMode.Remove)
                           : new AutostartRequest(AutostartMode.Invalid, Error: "--remove-autostart takes no other arguments.");
...
else return new AutostartRequest(AutostartMode.Invalid, Error: $"Unknown argument '{args[i]}'.");
```

Validation here is about **shape**. The runner does not trust the name just because it is well formed. Section 4.4 shows that it
must also resolve to the person at the screen.

### 4.3 The task definition and XML escaping

**File:** `src/Glance.Core/Setup/AutostartTask.cs`

```csharp
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
      ...
        <LogonTrigger><Enabled>true</Enabled><UserId>{u}</UserId></LogonTrigger>
      ...
        <Principal id="Author"><UserId>{u}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
      ...
      <Actions Context="Author"><Exec><Command>{path}</Command><WorkingDirectory>{dir}</WorkingDirectory></Exec></Actions>
    </Task>
    """;
}
```

- **The XML is the same as the old one**, moved from the editor into Core. The same settings (`IgnoreNew`, no battery limits,
  `ExecutionTimeLimit PT0S`, `StartWhenAvailable`) mean a task created by the old version and one created by the new version behave
  the same. `TASK_CREATE_OR_UPDATE` replaces an old definition in place.
- **Every value is escaped.** A folder like `C:\Program Files\R&D` would otherwise make the XML invalid. A value containing
  `</Command><Arguments>...` would be worse: it could add elements. `SecurityElement.Escape` turns `& < > " '` into entities, and the
  test parses the result and checks that the values come back unchanged.
- **No `<Arguments>` element.** The task starts the runner in its normal tray mode. The read-back relies on this: any arguments at
  all are treated as tampering.
- **Building XML by string interpolation** is fine here because every interpolated value is escaped, and there are only three of
  them. `XDocument` would be safer by construction. Suggestion 5 comes back to this.

### 4.4 The elevated installer: who, what and a read-back

**File:** `src/Glance.Runner/AutostartInstaller.cs`

**Who.** This is the subtle part. The elevated process is not always the user at the screen. When a standard user approves a prompt
with an administrator's password, the elevated runner runs *as that administrator*. The task must start for the person who uses the
PC, so the runner checks the requested name against the session, not against itself:

```csharp
if (AutostartTask.ValidateUserName(user) is not null) return AutostartTask.Exit.InvalidUser;
SecurityIdentifier requested;
try { requested = (SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier)); }
catch { return AutostartTask.Exit.InvalidUser; }
var sessionUser = SessionUserSid();
if (sessionUser is null || !sessionUser.Equals(requested)) return AutostartTask.Exit.InvalidUser;
string account;
try { account = requested.Translate(typeof(NTAccount)).Value; } // canonical DOMAIN\name
catch { return AutostartTask.Exit.InvalidUser; }
```

`SessionUserSid` asks the Remote Desktop Services API who is signed in to this process's session:

```csharp
var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
var name = SessionString(session, WtsUserName);
var domain = SessionString(session, WtsDomainName);
...
var account = string.IsNullOrEmpty(domain) ? new NTAccount(name) : new NTAccount(domain, name);
return (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
```

Comparing **SIDs**, not strings, is the right move. `PC\Ana`, `pc\ana` and `ana@corp.com` can all name the same account, and only
the SID is unambiguous. The task is then registered with the canonical name translated back from the SID. So whatever spelling the
editor sends, a caller can never set up a task for a user who is not at the screen.

**What.** The command comes from the process itself, and its folder is checked again:

```csharp
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
```

Be clear about what the runner-side check can and cannot do. If someone has *already* replaced `Glance.Runner.exe`, their code runs
instead of this check. So the check is not a defence against a tampered runner. Its job is to make sure an **honest** runner never
registers a path that someone *could* tamper with later. The editor-side check (section 4.6) makes sure an untrusted copy is never
elevated in the first place just to install.

**Read-back.** After registering, the runner reads the task back and deletes it if it is not exactly what was meant:

```csharp
string? registered = null;
try { registered = (string)root.GetTask(AutostartTask.TaskName).Xml; } catch { }
if (AutostartTask.VerifyRegistered(registered, exe) is null) return AutostartTask.Exit.Ok;
try { root.DeleteTask(AutostartTask.TaskName, 0); } catch { }
return AutostartTask.Exit.Mismatch;
```

`VerifyRegistered` (in Core) parses the XML with `XDocument`. It matches on local names, so namespaces do not matter, and it insists
on exactly one action, an `Exec`, the same executable, and no arguments:

```csharp
var actions = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Actions");
var all = actions?.Elements().ToList() ?? new List<XElement>();
if (all.Count != 1) return $"The task has {all.Count} actions instead of 1.";
...
if (!SameExecutable(command, expectedExe)) return $"The task runs '{command}' instead of '{expectedExe}'.";
if (!string.IsNullOrWhiteSpace(Child("Arguments"))) return "The task passes unexpected arguments.";
```

This is a "fail closed" design: if the task cannot be read back at all, it is removed. Because the definition was built and
registered in the same process a moment earlier, this rarely fires. It is cheap insurance against a surprise.

**Remove** is a single `DeleteTask`. It tries to treat "already gone" as success:

```csharp
try { RootFolder().DeleteTask(AutostartTask.TaskName, 0); return AutostartTask.Exit.Ok; }
catch (COMException ex) when (ex.HResult == ErrorFileNotFound) { return AutostartTask.Exit.Ok; } // already gone
catch { return AutostartTask.Exit.RemoveFailed; }
```

Watch out here. Through `dynamic`, the runtime turns HRESULT `0x80070002` into `System.IO.FileNotFoundException`, so the first
`catch` never matches. A read-only probe with `GetTask` on a missing name confirmed the exception type. Filtering on `ex.HResult`
for any `Exception` would work. This is finding L1 in the review.

**Exit codes** are the contract with the editor. They avoid 1, the generic failure code, and each has a sentence the user can act
on:

```csharp
public const int Ok = 0;
public const int BadArguments = 10;
public const int NotElevated = 11;
public const int InvalidUser = 12;
public const int FolderNotTrusted = 13;
public const int RegisterFailed = 14;
public const int Mismatch = 15;
public const int RemoveFailed = 16;
public const int Unexpected = 17;
```

### 4.5 Folder trust: reading ACLs and deciding

**File:** `src/Glance.Core/Security/FolderTrust.cs`

The question is: *can anyone other than SYSTEM, Administrators or TrustedInstaller change what this elevated program loads?* The
code splits it into a pure decision and a Windows reader.

**Roles.** Different objects need different rights to be dangerous:

```csharp
public static int DangerousRights(TrustRole role) => role switch
{
    TrustRole.Folder => WriteData | AppendData | WriteExtendedAttributes | DeleteChild | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite,
    TrustRole.File => WriteData | AppendData | Delete | WriteDac | WriteOwner | GenericAll | GenericWrite,
    // Creating new entries in C:\ or Program Files is harmless; removing or renaming ours is not.
    TrustRole.Ancestor => DeleteChild | Delete | WriteDac | WriteOwner | GenericAll,
    _ => -1,
};
```

- In the **program folder** (and its subfolders), even *creating* a file is dangerous. A planted DLL with the right name is loaded
  before the system one. This is DLL search-order hijacking.
- For a **file**, writing, deleting (and then recreating), or changing permissions or owner are dangerous.
- For a **folder above** the program folder, creating new entries is harmless. `C:\` lets Authenticated Users create folders, and
  that cannot touch `C:\Program Files\Glance`. Deleting or renaming a child would let someone swap the whole program folder, so
  those rights are what matter.

**The decision** is an allow-list:

```csharp
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
```

Why each rule is there:

- **The owner first.** Whoever owns an object can always rewrite its DACL, whatever the DACL says. A zip extracted under
  `Downloads` is owned by the user, which is usually the first reason it is refused.
- **A NULL DACL means "everyone has full access"**, so it is not trusted.
- **Deny ACEs are skipped.** That sounds backwards, but skipping a deny can only make an object look *more* writable than it is. The
  check may be too strict, never too lenient. Exact Windows access evaluation (ordering, group membership) is avoided on purpose.
- **Inherit-only ACEs are skipped** because they do not apply to the object itself. `C:\` has an inherit-only "Authenticated Users:
  Modify" for new subfolders, and that must not make `C:\` look writable.
- **CREATOR OWNER** is a placeholder that only matters when ACEs are inherited. **OWNER RIGHTS** means "the owner", and the owner has
  already been judged.
- **An allow-list, not a block-list.** The code does not try to name Users, Authenticated Users, Everyone, INTERACTIVE, the user's
  own SID, logon-session SIDs or app-container SIDs. Anything that is not SYSTEM, Administrators or TrustedInstaller is refused if
  it holds a dangerous right.

**The reader** walks the program folder, every folder above it, and everything inside it. It yields lazily, so `Decide` stops
reading at the first problem:

```csharp
yield return Read(root.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), folder, TrustRole.Folder);

for (var d = root.Parent; d is not null; d = d.Parent)
    yield return Read(d.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), d.FullName, TrustRole.Ancestor);

var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = 0 };
foreach (var d in root.EnumerateDirectories("*", opts)) yield return Read(..., TrustRole.Folder);
foreach (var f in root.EnumerateFiles("*", opts)) yield return Read(..., TrustRole.File);
```

`Read` works from the raw security descriptor, not from the friendlier `GetAccessRules`. That way it sees every ACE type and can
treat any type it does not understand as "writable":

```csharp
var sd = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
...
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
```

Two practical details:

- **Any exception means "not trusted".** `Check` wraps everything in a `try`, and `IgnoreInaccessible = false` makes an unreadable
  file an error, not a skipped file.
- **Names are looked up once.** Translating every SID to a name would be slow, so only the one SID in the final message is
  translated (`WithName`). That is how the message reads "CDZAN\Zan owns ...", not a raw SID.

In a read-only run on the development PC, the installed `C:\Program Files\Glance` (515 files) came out trusted in about 12 ms. The
dev `bin` folder and folders on the data drives were refused because the user owns them. A junction created by the user and
pointing into Program Files was also refused: `GetAccessControl` reads the junction object itself, and the user owns it.

### 4.6 The editor side

**File:** `src/Glance.App/Services/Autostart.cs`

Install checks the folder first, so an untrusted copy is refused with a reason and never elevated. It then sends the editor's own
user name (the person at the screen) to the runner:

```csharp
var trust = CheckRunnerFolder(runnerExe);
if (!trust.Trusted) return UntrustedMessage(trust);

// The interactive user (this editor's user), which may differ from the admin account that approves the prompt.
var user = WindowsIdentity.GetCurrent().Name;
if (AutostartTask.ValidateUserName(user) is { } err) return $"Cannot set up auto-start for '{user}': {err}";

var result = RunRunnerElevated(runnerExe, AutostartTask.InstallArguments(user));
return result ?? (IsInstalled() ? null : "The task was not created.");
```

Starting the runner elevated and turning its exit code into a message:

```csharp
using var p = Process.Start(new ProcessStartInfo(runnerExe, args)
{
    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
    WorkingDirectory = Path.GetDirectoryName(runnerExe)!,
});
if (p is null) return "The runner could not be started.";
if (!p.WaitForExit(60000)) return "The runner did not finish changing auto-start in time.";
return AutostartTask.DescribeExit(p.ExitCode);
...
catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "The Windows permission prompt was declined."; }
```

The old code treated *every* `Win32Exception` as "declined". The new filter on 1223 lets other errors, such as a missing file,
show their real message.

**The deadlock fix in `IsInstalled`.** The old code read standard output to the end, then standard error. If a child process fills
the stderr pipe buffer while the parent is still blocked reading stdout, both wait for each other forever. The fix drains both at
once and gives up on a hung child:

```csharp
var stdout = p.StandardOutput.ReadToEndAsync();
var stderr = p.StandardError.ReadToEndAsync();
if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return false; }
Task.WaitAll(new Task[] { stdout, stderr }, 2000);
return p.ExitCode == 0;
```

**File:** `src/Glance.App/Views/ServiceWindow.cs`

The window checks the folder in the background when it opens. It explains the problem, and if an old task exists from before, it
recommends turning it off:

```csharp
var verdict = await Task.Run(() => Autostart.CheckRunnerFolder(exe));
_trust = verdict;
if (!verdict.Trusted)
    Say(Autostart.IsInstalled()
        ? Autostart.UntrustedMessage(verdict) + " Auto-start is still turned on from before; turning it off is recommended."
        : Autostart.UntrustedMessage(verdict));
```

The checkbox rule is one line:

```csharp
// An untrusted folder can still turn auto-start off, but not on.
_auto.IsEnabled = !_busy && SensorHub.FindRunnerExe() is not null && (installed || _trust?.Trusted != false);
```

While the check is still running, `_trust` is `null` and the box stays enabled. That is safe, because `Install` checks the folder
again before doing anything.

### 4.7 Tests

**File:** `src/Glance.Tests/AutostartTests.cs` (62 tests, part of the 315 in the suite)

The tests follow the project's habit of testing pure Core logic. A few are worth reading as examples.

**Escaping, by round-tripping.** It is not enough to check that an entity appears. The test parses the XML and compares the values:

```csharp
var exe = @"C:\Program Files\R&D ""Tools"" 'x'\Glance.Runner.exe";
var raw = AutostartTask.BuildXml(exe, @"PC\Ana");
Assert.Contains("R&amp;D &quot;Tools&quot; &apos;x&apos;", raw);
var doc = XDocument.Parse(raw); // well-formed
Assert.Equal(exe, doc.Descendants(Ns + "Command").Single().Value);
```

**Trust rules as tables.** Each dangerous right, per role, for an ordinary group:

```csharp
[InlineData(TrustRole.Folder, 0x2)]       // add a file (plant a DLL)
[InlineData(TrustRole.File, 0x40000)]     // rewrite its permissions
[InlineData(TrustRole.Ancestor, 0x40)]    // delete or rename the program folder
public void A_dangerous_right_for_ordinary_users_is_not_trusted(TrustRole role, int right) { ... }
```

**The `C:\` case,** where "create folders" is allowed on an ancestor but not on the program folder:

```csharp
root.Add(Allow(AuthenticatedUsers, 0x4 | ReadExecute));
root.Add(Allow(AuthenticatedUsers, Modify, inheritOnly: true));
Assert.True(Decide(Obj(TrustRole.Ancestor, root, FolderTrust.TrustedInstaller, @"C:\")).Trusted);
Assert.False(Decide(Obj(TrustRole.Folder, root)).Trusted);
```

**Mutation checks.** In review, removing the inherit-only skip, the `Arguments` check and the "one backslash" rule made four tests
fail. That is good. But making the Windows *reader* skip every ancestor and every file went unnoticed, because the only reader tests
are negative and fail early on the owner check. See suggestion 2.

---

## 5. Self-review: five ways to improve the change

These are the changes I would make next, roughly in order of value. The full list, with severities, is in
`docs/reviews/review-autostart-task.md`.

### 1. Close the environment route: the task still inherits the user's environment

The task uses `LogonType InteractiveToken`, so Windows starts the runner with the user's environment, including `HKCU\Environment`,
which any normal process can write. The .NET runtime reads several variables before `Main` runs: `DOTNET_STARTUP_HOOKS` loads an
assembly, and `CORECLR_ENABLE_PROFILING` / `CORECLR_PROFILER_PATH` load a native DLL. A program that sets them gets code into the
elevated runner at the next sign-in, with no prompt and no file in Program Files touched. That is the same outcome H6 was about. It
is older than this change, but the change does not close it.

**Suggestion:** add `<StartupHookSupport>false</StartupHookSupport>` to `Glance.Runner.csproj` now, and log a warning when profiler
or runtime variables are present. The lasting fix is architectural: move sensors and USB into a Windows service running as
LocalSystem (its environment comes from the service manager), and keep a non-elevated tray app that talks to it over the existing
pipes. That also removes the scheduled task and the UAC prompt altogether.

### 2. Test the ACL reader, not just the decision

`FolderTrust.Decide` is well covered. `Gather` and `Read`, which turn real files into facts, are covered only by tests that fail on
the very first rule (owner). A reader bug, such as reading the inherit-only flag wrong or skipping ancestors, would ship unnoticed.

**Suggestion:** make the descriptor-to-facts step a public pure function and test it with SDDL strings:

```csharp
var sd = new RawSecurityDescriptor("O:BAD:(A;;FA;;;SY)(A;OICIIO;GA;;;CO)(A;;0x1301bf;;;BU)");
var facts = FolderTrust.ToObjectSecurity(sd, @"C:\x", TrustRole.Folder);
Assert.Contains(facts.Entries!, e => e.Sid == "S-1-5-32-545" && !e.InheritOnly);
```

Also add one Windows-only positive test on a folder that is admin-only on every CI machine, such as `Program Files\dotnet\host`.

### 3. Check the task that is already there, not only the copy you are running from

The window only warns when the *current* copy is untrusted. A task created by version 0.1.1 from `Downloads\Glance` stays in place,
ticked and silent, once the user installs into Program Files. The task XML is readable without elevation
(`schtasks /Query /TN "Glance Runner" /XML ONE`), and `VerifyRegistered` already exists.

**Suggestion:** read the existing task in the editor. Run `VerifyRegistered` against the current runner, and `FolderTrust.Check` on
the task's own command folder. Offer "repair" (re-register) or "turn off". Keep the decision in a pure Core function, tested with
the real read-back format, where the principal comes back as a SID.

### 4. One trust engine for the runner and for FFmpeg

The sibling branch for FFmpeg adds `FfmpegTrust`, a second engine with its own `TrustVerdict` type, the same SIDs and masks, and
different strengths:

- `FfmpegTrust` rejects junctions and network or removable drives.
- `FolderTrust` checks every file in the folder and treats unknown ACE types as writable.

Two engines will drift, and two types with the same name will clash in the first file that imports both namespaces.

**Suggestion:** keep `Glance.Core.Security.FolderTrust` as the single engine. Add an `IsReparsePoint` fact, a fixed-local-drive
pre-check and a `CheckExecutable(exe)` entry point, then make `FfmpegTrust` a thin wrapper or remove it. Also drop `Delete` from
the mask for a *drive root*: Windows-formatted data drives give Authenticated Users Modify on the root, and you cannot delete a
root anyway.

### 5. Make the installer's decisions testable, and the XML safe by construction

`AutostartInstaller` has no tests, and its one bug (the "already gone" `catch` that never matches) is the kind a unit test catches
at once. Several decisions in it are pure:

- "does the requested SID equal the session SID";
- "is that user an administrator", which is needed: a standard user gets a task that Windows can never start, because the runner
  requires admin;
- "which HRESULTs mean already removed".

**Suggestion:** move those decisions into `AutostartTask` with tests, and give "not an administrator" its own exit code and
message. While there, build the XML with `XDocument` instead of string interpolation. Escaping then no longer depends on
remembering `SecurityElement.Escape` for each new field. Extend `VerifyRegistered` to check the working directory, the single logon
trigger and the principal's SID too.

# Review: the elevated runner registers the autostart task (fix for H6)

- **Branch:** `claude/fix-autostart-task-0011d2`, one commit (`3056a18`) on top of `main` (`0a9f10b`).
- **Change request:** finding **H6** in `docs/reviews/review-full-codebase.md` (the task XML in a fixed `%TEMP%` file read by an
  elevated `schtasks.exe`, and a task that could point at a runner in a user-writable folder).
- **Files:** `src/Glance.Core/Security/FolderTrust.cs` (new), `src/Glance.Core/Setup/AutostartTask.cs` (new),
  `src/Glance.Runner/AutostartInstaller.cs` (new), `src/Glance.Runner/Program.cs`, `src/Glance.App/Services/Autostart.cs`,
  `src/Glance.App/Views/ServiceWindow.cs`, `src/Glance.Tests/AutostartTests.cs` (new).
- **Reviewer:** independent review. I did not write the change. No scheduled task was created, changed or deleted, nothing was run
  elevated, and the editor and runner were not started.

## Verdict: merge after fixes

The change does what H6 asked for. Both problems in the finding are closed:

- **Problem A (file swap during the UAC prompt) is gone.** No file is involved any more. The elevated runner builds the XML in
  memory from `Environment.ProcessPath` and registers it through the Task Scheduler COM API.
- **Problem B (task pointing at a user-writable runner) is closed for honest copies.** Both the editor and the elevated runner
  refuse when the runner's folder, anything inside it, or any folder above it can be changed by a non-admin.

The design is sound and the pure parts are well tested. Before merging, fix these:

- **M1:** existing tasks are not checked.
- **M2:** for a standard user the task is created but can never start.
- **L1:** "already removed" is reported as a failure.

**H1** (the user's environment reaches the elevated runner at every sign-in) is older than this change and is not caused by it. It
does mean H6's "silent admin at every logon" can still be reached without touching a file. Add the one-line mitigation in this
change if you can, and plan the proper fix separately. Do not mark H6 as fully closed until then.

## Build and test results

| What | Result |
|---|---|
| `dotnet build src/Glance.sln -c Release -p:SkipHost=true` (worktree) | **Succeeded**, 0 errors, 28 warnings. All warnings are pre-existing: CA1416 in `SetupChecks.cs`, xUnit analyzers, `CS9057` analyzer version, one `CS8602` in `MainWindow.cs`. None are in the changed files. |
| `dotnet test src/Glance.Tests -c Release` (worktree) | **Passed: 315 / 315** (62 new), about 2 s. |
| Merge check, `git merge-tree --write-tree HEAD <branch>` against all five sibling branches | **No textual conflicts with any of them.** None of the siblings touch `Program.cs`, `Autostart.cs`, `ServiceWindow.cs` or the new files. |
| Build and test of the merged tree with `claude/fix-ffmpeg-elevation-a3dfcc` (exported to a scratch folder) | **Builds, 360 / 360 tests pass.** The two `TrustVerdict` types do not clash yet (see M3). |
| Mutation check 1: drop the inherit-only skip, the `Arguments` read-back check and the "more than one backslash" rule | **Caught.** 4 tests failed: `Inherit_only_entries...`, `Creating_folders_in_an_ancestor...`, `Read_back_rejects...` and `Invalid_user_names...("A\B\C")`. Code restored, `git status` clean. |
| Mutation check 2: make the Windows reader skip all ancestors, skip all files and treat every ACE as inherit-only | **Not caught.** All 62 tests still pass (see Test gaps). Code restored, `git status` clean. |
| Read-only probe: `FolderTrust.Check` on real folders, run from a scratch console app | `C:\Program Files\Glance` (515 files): **trusted**, 12 ms. `C:\Program Files\dotnet\host`: trusted. The worktree's `bin\Release`, `D:\Emulators`, `E:\...` and `G:\My Drive`: not trusted (owned by the user). |
| Read-only probe: a junction in the scratch folder pointing at `C:\Program Files\Glance` | **Not trusted.** The reader judges the junction object itself ("CDZAN\Zan owns ...\jtest"), not its target. |
| Read-only probe: Task Scheduler COM `GetTask` on a name that does not exist | Through `dynamic` it throws **`System.IO.FileNotFoundException`** (HResult `0x80070002`), **not `COMException`**. This is the cause of L1. |
| Read-only: `schtasks /Query /TN "Glance Runner" /XML ONE`, not elevated | Works. The existing task, created by the old code, is readable by the normal user. Its `Principal/UserId` comes back as a **SID**. |

---

## Findings

### High

#### H1. The user's environment still reaches the elevated runner at every sign-in (pre-existing, not caused by this change)
- **Where:** the task definition, `AutostartTask.BuildXml` (`src/Glance.Core/Setup/AutostartTask.cs:120-149`). It uses
  `LogonType InteractiveToken` with `RunLevel HighestAvailable`. Also `src/Glance.Runner/Glance.Runner.csproj`, which does not
  turn off startup hooks.
- **Evidence:** a logon task with `InteractiveToken` starts the program with the user's own environment block, and that block
  includes `HKCU\Environment`. Any process running as the user can write to that key without a prompt. The .NET runtime honours
  several environment variables before `Main` runs, with no check for elevation:
  - `DOTNET_STARTUP_HOOKS` loads a managed assembly.
  - `CORECLR_ENABLE_PROFILING`, `CORECLR_PROFILER` and `CORECLR_PROFILER_PATH` load a native profiler DLL.
  - In a `-FrameworkDependent` build, `DOTNET_ROOT` decides which `hostfxr` and runtime get loaded.

  None of this is blocked in `Glance.Runner.csproj`.
- **Scenario:** a program the user runs sets
  `CORECLR_ENABLE_PROFILING=1` and `CORECLR_PROFILER_PATH=C:\Users\Ana\AppData\Local\x.dll` in `HKCU\Environment`. At the next
  sign-in the task starts `C:\Program Files\Glance\Glance.Runner.exe` with highest privileges, and `x.dll` runs as administrator.
  There is no prompt and no file in the program folder is touched. This is the same outcome H6 described.
- **Status:** not verified on this PC. Testing it would need a task and environment changes, which this review was not allowed to
  make. The finding is based on how Task Scheduler builds environments and on the documented behaviour of the .NET runtime. The
  old code had the same exposure, so this is not a regression.
- **Remedial action:**
  1. Now: add `<StartupHookSupport>false</StartupHookSupport>` to `Glance.Runner.csproj` (and `Glance.SensorHost.csproj`). This
     writes `System.StartupHookProvider.IsSupported: false` into `runtimeconfig.json`, and that file is in the admin-only folder.
  2. Now: at runner start-up, log a warning to `runner.log` if any `CORECLR_*`, `COR_*`, `DOTNET_STARTUP_HOOKS`, `DOTNET_ROOT` or
     `DOTNET_ADDITIONAL_DEPS` variable is set. This cannot block a profiler, which loads before `Main`, but it makes an attack
     visible.
  3. Next: move the privileged work out of a process that inherits the user's environment. Use a Windows service running as
     LocalSystem (it gets its environment from the service control manager, not from HKCU) for sensors and USB, plus a
     non-elevated tray app that talks to it over the existing pipes. This also removes the UAC prompt and the scheduled task.
  4. Until then, say in `docs/reviews/review-full-codebase.md` that H6 is fixed for file swaps and writable folders, and that the
     environment route remains.

### Medium

#### M1. Existing tasks are not checked, so an old task pointing at a writable copy stays silently
- **Where:** `src/Glance.App/Services/Autostart.cs:20-34` (`IsInstalled` only checks that a task with that name exists) and
  `src/Glance.App/Views/ServiceWindow.cs:68-78, 90-93`.
- **Evidence:** the editor only warns about an existing task when *the copy it is running from* is untrusted. It never looks at
  what the existing task runs. The task is readable without elevation (`schtasks /Query /TN "Glance Runner" /XML ONE` worked here),
  so the information is available.
- **Scenario:** with version 0.1.1 the user turned on auto-start from the zip folder in `Downloads\Glance`. Later they install into
  Program Files and open the Background service window from the installed editor. The folder check passes, the checkbox shows
  ticked, and no warning appears. The task still starts `Downloads\Glance\Glance.Runner.exe` with highest privileges, which is
  exactly H6 problem B. The same happens if any other program registered a task under the name "Glance Runner".
- **Remedial action:**
  - In the editor, read the task XML without elevation (`schtasks /Query /TN "Glance Runner" /XML ONE`, with both pipes drained as
    in `IsInstalled`).
  - Pass it to `AutostartTask.VerifyRegistered(xml, currentRunnerExe)`, and run `FolderTrust.Check` on the folder of the task's
    `Command`.
  - If either check fails, show "Auto-start points at `<path>`, which `<reason>`. Turn it off, or turn it on again to repair it."
    Re-enabling already repairs it, because `TASK_CREATE_OR_UPDATE` replaces the old definition.
  - Put the decision in a pure Core function (`AutostartTask.Assess(taskXml, currentExe, trustOfTaskFolder)`) and test it with the
    real XML format, where the principal is stored as a SID.

#### M2. A standard user gets a task that can never start, and the editor reports success
- **Where:** `src/Glance.Runner/AutostartInstaller.cs:35-58`. The comment and commit message describe the case where "the admin
  account that approved the prompt" differs from the user.
- **Evidence:** when a standard user (Ana) turns on auto-start, an administrator (Bob) types his password at the UAC prompt.
  The runner then correctly registers the task for Ana's session (the WTS check passes). But `HighestAvailable` for a standard user
  is a standard token, and `Glance.Runner.exe` has a `requireAdministrator` manifest. Task Scheduler cannot start it
  (`ERROR_ELEVATION_REQUIRED`, 740) at any sign-in. The editor still says nothing went wrong and shows the box ticked. The old code
  had the same gap, but this change explicitly designs for this case and presents it as working.
- **Remedial action:** in `Install`, after resolving `requested`, check whether that account is a member of
  `BUILTIN\Administrators`, directly or through a group. For local accounts, `NetUserGetLocalGroups` with `LG_INCLUDE_INDIRECT`
  works. For domain or Azure AD accounts, use a `WindowsIdentity` for the UPN and check its groups. If the account is not an admin,
  return a new exit code (for example `18 UserNotAdmin`) with the message: "Auto-start needs the signed-in user to be an
  administrator. Without it Windows cannot start the runner at sign-in." Alternatively, the editor can read its own token's
  `TokenElevationType`. `Limited` means a split-token admin, which is the only case where auto-start can work.

#### M3. Two parallel trust engines with the same type name; the autostart one has no reparse-point or drive-type rule
- **Where:** `src/Glance.Core/Security/FolderTrust.cs` (this branch) and `src/Glance.Core/Media/FfmpegTrust.cs` (branch
  `claude/fix-ffmpeg-elevation-a3dfcc`).
- **Evidence:**
  - Both declare a type called `TrustVerdict`: `Glance.Core.Security.TrustVerdict` is a `record` class with `Yes`/`No`, and
    `Glance.Core.Media.TrustVerdict` is a `readonly record struct`. The merged tree builds today only because no file imports both
    namespaces. The first file that does will fail with CS0104 (ambiguous reference).
  - Both duplicate the SIDs and the access masks.
  - Each engine has a rule the other is missing:

    | Rule | FolderTrust (autostart) | FfmpegTrust |
    |---|---|---|
    | Owner must be SYSTEM, Administrators or TrustedInstaller | yes | yes |
    | Allow-list of principals, deny ACEs ignored | yes | yes |
    | Every file and subfolder in the program folder | **yes** (recursive) | no: only the exe and its parent folder. A writable sibling DLL of a "shared" FFmpeg build is missed. |
    | Unknown or callback ACE types count as writable | **yes** (raw descriptor) | no: `GetAccessRules` only returns the common allow and deny rules |
    | Rejects junctions and symlinks in the chain | no | **yes** |
    | Rejects UNC, mapped, removable and non-fixed drives | no | **yes** |
    | Rejects `..` in the path | no (it calls `GetFullPath`) | **yes** |
- **Scenario:** a runner on `\\server\share\Glance` is judged by the *server's* ACLs, where `BUILTIN\Administrators` means the
  server's administrators. The folder check can pass, and an administrator of the file server then gets admin on this PC at every
  sign-in.

  Junctions are less of a problem. A junction the user creates is refused, because its own owner is the user (verified with a
  scratch junction). A junction an *admin* created inside the trusted folder is judged by its own ACL, and the recursive scan
  does not walk into its target.
- **Remedial action:** unify on one engine, right after both branches merge:
  1. Keep `Glance.Core.Security.FolderTrust` (pure `Decide`, raw-descriptor reader, recursive scan) as the only engine. Keep its
     `TrustVerdict` and delete the struct in `Glance.Core.Media`.
  2. Port FfmpegTrust's three rules into the reader:
     - an `IsReparsePoint` flag on `ObjectSecurity`, refused in `Decide` for every role;
     - a "fixed local drive" pre-check (`DriveInfo.DriveType == Fixed`, no UNC);
     - the `..` check.
  3. Add `FolderTrust.CheckExecutable(string exe)`. It checks the file, then its folder recursively, then the ancestors. Turn
     `FfmpegTrust.Check` into a one-line wrapper or delete it, and update `Ffmpeg.Search` to call the new method.
  4. Keep both test suites. Run the FfmpegTrust cases against `FolderTrust.Decide`, adapting the `PathFact`/`AceFact` builders to
     `ObjectSecurity`/`AclEntry`.

### Low

#### L1. "Remove" reports a failure when the task is already gone
- **Where:** `src/Glance.Runner/AutostartInstaller.cs:72-74`.
- **Evidence:** `catch (COMException ex) when (ex.HResult == ErrorFileNotFound)` never matches. Through `dynamic` late binding, the
  runtime turns HRESULT `0x80070002` into `System.IO.FileNotFoundException`, not `COMException`. I confirmed this read-only with
  `GetTask` on a missing name; `DeleteTask` returns the same HRESULT. The general `catch` therefore returns `RemoveFailed` (16), and
  the editor shows "Windows Task Scheduler could not remove the auto-start task."
- **Scenario:** the task was already removed by the uninstaller, by another user, or by hand in Task Scheduler while the window
  showed it ticked. The user unticks the box and gets a misleading error.
- **Remedial action:** filter on the HRESULT, not the type: `catch (Exception ex) when (ex.HResult == ErrorFileNotFound)`. Better,
  move the mapping into a pure `AutostartTask.ClassifyRemoveError(int hresult)` and test it.

#### L2. Turning auto-start off from an untrusted copy elevates that untrusted copy
- **Where:** `src/Glance.App/Services/Autostart.cs:70-74`, `ServiceWindow.cs:73-76` ("turning it off is recommended").
- **Evidence:** `Remove` runs `FindRunnerExe()` through `runas` with no trust check. In an untrusted folder that exe could have been
  replaced. Before this change, removal elevated Microsoft's `schtasks.exe`, which was safe because no file was involved.
- **Scenario:** a dev build or zip copy whose `Glance.Runner.exe` was swapped by malware. The window recommends turning auto-start
  off, and the UAC prompt elevates the swapped file. The user approves a prompt, so this is not silent, but the window steered them
  to it.
- **Remedial action:** when the folder is not trusted, remove the task with
  `schtasks.exe /Delete /TN "Glance Runner" /F` through `runas`. It reads no files, so it is safe from a writable folder. Use the
  runner's `--remove-autostart` only for trusted copies, or always use `schtasks` for removal.

#### L3. A Windows-formatted data drive is always refused, even for a locked-down folder
- **Where:** `src/Glance.Core/Security/FolderTrust.cs:71` (the ancestor mask includes `Delete`).
- **Evidence:** `icacls D:\` on this PC shows `NT AUTHORITY\Authenticated Users:(M)` on the root itself, and Modify includes
  DELETE. A drive root cannot be deleted, so DELETE on the root is harmless, but the check refuses anything under `D:\`. FfmpegTrust
  has the same rule.
- **Scenario:** an admin installs to `D:\Program Files\Glance` and locks it down. Auto-start is refused with the message
  "Authenticated Users can move or replace folders in D:\".
- **Remedial action:** when an ancestor is a volume root (`Path.GetPathRoot(p) == p`), leave `Delete` out of its mask. Add a
  `Decide` test with the real `D:\` ACL above.

#### L4. One task name for every user on the PC
- **Where:** `AutostartTask.TaskName` and `TaskCreateOrUpdate` (`AutostartInstaller.cs:16, 58`). This is pre-existing.
- **Evidence:** the task lives at `\Glance Runner` with no per-user part. `IsInstalled` cannot tell whose task it is.
- **Scenario:** on a shared PC, Bob turns on auto-start and silently replaces Ana's task. Ana's window then shows the box ticked for
  a task that runs only for Bob. Unticking it removes Bob's task.
- **Remedial action:** in the short term, read the task's `Principal/UserId` (a SID) in the editor and say "Auto-start is set up for
  another user (`<name>`)" instead of showing it as ticked. In the longer term, use a per-user task (for example
  `\Glance\Runner <SID>`) and migrate the old name on the next install.

#### L5. The read-back only checks the action
- **Where:** `src/Glance.Core/Setup/AutostartTask.cs:155-172`.
- **Evidence:** `VerifyRegistered` checks the single `Exec`, its `Command` and the absence of `Arguments`. It does not check
  `WorkingDirectory`, the principal, `RunLevel`, `LogonType` or the trigger. The definition is built and registered in the same
  elevated process, so the risk is small. Another admin process would have to change the task in the few milliseconds between
  the two calls.
- **Remedial action:** also check that `WorkingDirectory` equals the exe's folder, that there is exactly one `LogonTrigger`, that
  `LogonType` is `InteractiveToken`, and that the `Principal/UserId` resolves to the requested SID. Task Scheduler stores the
  principal as a SID (observed on this PC), so compare SIDs, not names.

#### L6. The Background service window starts `schtasks.exe` on the UI thread every second
- **Where:** `src/Glance.App/Views/ServiceWindow.cs:56-58, 90` and `:74`. This is pre-existing, and the change adds one more call.
- **Evidence:** `Sync()` runs on a 1 s `DispatcherTimer` and calls `Autostart.IsInstalled()`, which starts a process and waits up to
  5 + 2 s. `CheckFolderTrust` also calls it on the UI thread after its `await`. AGENTS.md says not to block the UI thread with
  process work.
- **Remedial action:** cache the installed state. Refresh it on a background task every few seconds and right after a toggle, and
  have `Sync()` only read the cached value.

### Nits

- **N1.** `AutostartInstaller.cs:115-119`: `wtsapi32.dll` is imported without
  `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`, so the app folder is searched first. This is harmless in a trusted
  folder, but it is good practice for an elevated process.
- **N2.** Exit code 12 (`InvalidUser`) covers three different causes: bad shape, lookup failure, and "not the signed-in user".
  Separate codes would make support easier. The install mode also writes nothing to `runner.log`, and the general
  `catch { return Unexpected; }` (`:32`) loses the exception. Log one line with the exception type and HRESULT.
- **N3.** `ParseArgs` accepts `--user --install-autostart` and takes the switch as the user name. It fails safely later (account
  lookup), but `--user` could refuse a value that starts with `--`.
- **N4.** If the editor finds a runner from before this change (dev setups only), the old runner ignores the arguments. With no
  runner running it starts the tray app elevated and the editor waits 60 s. With one running it exits 0, and the editor reports
  "The task was not created." This does not happen in packaged builds.
- **N5.** `WindowsIdentity.GetCurrent()` (`Autostart.cs:60`) and `Process.GetCurrentProcess()` (`AutostartInstaller.cs:94`) are
  not disposed.
- **N6.** `AGENTS.md` does not mention the runner's new command-line modes or exit codes. Add a line under "Architecture", next to
  the control-pipe commands. Two sibling branches also edit `AGENTS.md`, so expect a trivial merge there.

---

## Merge notes for the sibling branches

- **`Program.cs`:** no conflict. No sibling touches `src/Glance.Runner/Program.cs`. The new branch at the top of `Main` returns
  before the mutex and before `RunnerContext` is created. The `RunnerContext` changes in
  `fix-live-empty-folder` (command parsing), `feature-shutdown-blank` (session and power watcher), `fix-display-loop` and
  `fix-ffmpeg-elevation` (`Ffmpeg.Rejected += Log`) therefore do not run in install mode, which is what we want.
- **`fix-ffmpeg-elevation`:** no textual conflict. The merged tree builds and its 360 tests pass. Unify the trust code as in M3.
  The simplest order is to merge this branch first, then rebase the ffmpeg branch so that `FfmpegTrust` becomes a wrapper over
  `FolderTrust`.
- **`fix-saveas-assets`, `fix-display-loop`, `feature-shutdown-blank`, `fix-live-empty-folder`:** no overlap with this branch's
  files. `git merge-tree` was clean for each one.

## Test gaps

1. **The Windows ACL reader is not tested for the positive case.** `Gather` and `Read` in `FolderTrust.cs:133-174` are only
   exercised by two negative tests (a missing folder, and a temp folder owned by the user). Both fail on the owner check before
   ancestors or files matter. Mutation check 2 confirmed it: skipping every ancestor, skipping every file, and treating every ACE
   as inherit-only went unnoticed by all 62 tests.
   - **Add:** a Windows-only test that runs `FolderTrust.Check` on a known admin-only folder (for example
     `Path.Combine(Environment.GetFolderPath(SpecialFolder.ProgramFiles), "dotnet", "host")`, which exists on CI) and expects
     trusted.
   - **Add:** a reader test with a temp folder whose *file* gets an explicit `Users: Modify` ACE. Use
     `FileSecurity.AddAccessRule` on a file the test owns; owner problems come first, so assert the reason names the file. Better,
     make `Read` a public pure function taking a `RawSecurityDescriptor` and test it with SDDL strings, for example
     `O:BAD:(A;;FA;;;SY)(A;;0x1301bf;;;BU)`.
2. **`AutostartInstaller` has no tests:** the session-user comparison, the delete-on-mismatch path, and the remove error mapping
   (L1 would have been caught). Move the decisions into Core, for example
   `AutostartTask.CheckInstallUser(requestedSid, sessionSid)` and `ClassifyRemoveError(hresult)`, and test them there.
3. **The command line is never really round-tripped.** `Install_arguments_round_trip_through_the_parser` splits the string by hand.
   Add a Windows-only test that sends `InstallArguments(user)` through `CommandLineToArgvW` (P/Invoke) for names with spaces,
   `&`, `'`, `%`, `@` and non-ASCII letters, then into `ParseArgs`.
4. **`VerifyRegistered` is only tested against XML this code built.** Add a fixture with Task Scheduler's real read-back format (as
   observed here: principal as a SID, reordered settings, an extra `IdleSettings`, no `Enabled` or `AllowHardTerminate`), so the
   check is known to accept what Windows returns.
5. **The routing in `Program.Main` is untested.** Install and remove modes must never reach the mutex or `RunnerContext`. A pure
   `RunnerStartup.Decide(args)` returning `Normal | Autostart(request) | ExitBadArgs` would make this testable.
6. **The window's enable rule** ("an untrusted folder can turn it off but not on") lives in `ServiceWindow.Sync`. Extract it to a
   pure function and test the four combinations.

## Manual test plan

Steps marked **(UAC)** need the user at the screen. Run on the real PC, or better on a VM for steps 8 to 10.

1. **Installed copy, turn on (UAC).** Install with the installer into `C:\Program Files\Glance`. Open the editor, then Background
   service. The box is enabled and no warning is shown. Tick it and approve the prompt.
   - Expect no tray icon from this launch and no change on the display.
   - Afterwards, `schtasks /Query /TN "Glance Runner" /XML ONE` shows a single `Exec` with
     `C:\Program Files\Glance\Glance.Runner.exe`, `WorkingDirectory C:\Program Files\Glance`, the user's SID as principal,
     `HighestAvailable` and the same settings as before.
2. **Declined prompt (UAC).** Tick the box and click "No". Expect "The Windows permission prompt was declined." and the box
   unticked.
3. **Sign-in.** Sign out and in, or reboot. The runner starts with no prompt and the display shows the theme.
4. **Turn off (UAC).** Untick and approve. The task is gone. Then delete the task by hand in Task Scheduler while the window shows it
   ticked, untick it, and check the message. With the current code expect the L1 error.
5. **Upgrade (M1).** On a PC that has the task from version 0.1.1, open the new editor. The box shows ticked, and with the current
   code no check of where the task points is done. Re-tick it and check the read-back XML is unchanged apart from the description.
6. **Zip copy.** Extract the zip under `Downloads` and run its editor. The window shows the "not available for this copy" message
   naming the owner, and the box is disabled. If a task already exists, the message adds "turning it off is recommended" (see L2).
7. **Dev build.** Run `src\Glance.App\bin\Release\net8.0\Glance.App.exe`. Same refusal as step 6.
8. **Standard user (UAC with an admin password, M2).** Sign in as a standard account, tick the box, and enter an admin's password.
   Today the editor reports success. Sign out and in: the runner does not start. Task Scheduler history shows error 740.
9. **Run as different user.** Start the editor with "Run as different user" in someone else's session, then tick the box. Expect
   the `InvalidUser` message (12).
10. **Runner already running.** With the tray runner running, tick and untick the box. Install mode must not stop or disturb the
    running runner (no mutex is taken).
11. **(Optional, VM only, H1)** Set `DOTNET_STARTUP_HOOKS` in `HKCU\Environment` to a harmless hook that writes a file, then sign in.
    If the file appears, H1 is confirmed. Repeat after adding `<StartupHookSupport>false</StartupHookSupport>`.

## Checked and found sound

- **No file on the elevated path.** The XML is built in memory (`AutostartTask.BuildXml`) and passed to `RegisterTask` as a string.
  `%TEMP%` is no longer used. H6 problem A is closed.
- **The registered command is the elevated process's own image** (`Environment.ProcessPath`), not something the editor supplies.
  A non-admin who can start the trusted runner with any arguments can only register *that* runner, and only for the user signed in
  to the session, or remove the task. Both need a UAC prompt.
- **The user name cannot be used to target another account.** Whatever the editor passes must resolve, through
  `NTAccount.Translate`, to the same SID as the session's WTS user. The task is then registered with the canonical `DOMAIN\name`
  from the SID. UPNs (`ana@corp.com`), Azure AD (`AzureAD\Name`), local accounts linked to a Microsoft account (`PC\name`) and
  names with spaces all go through `LookupAccountName`. Quotes, control characters and a trailing backslash are refused by
  `ValidateUserName`, so the quoted `--user "..."` argument cannot break out (`\"` cannot occur).
- **XML escaping:** `SecurityElement.Escape` covers `& < > " '` in the path, working directory and user. The test parses the result
  and compares the round-tripped values.
- **The editor's `WorkingDirectory` and environment do not affect the registered task.** The task's working directory is computed
  from the exe path by the elevated process, and no arguments are registered.
- **Trust decision:**
  - It is an allow-list (only SYSTEM, Administrators and TrustedInstaller may hold rights that matter), so the user's own SID,
    Users, Authenticated Users, Everyone, INTERACTIVE, logon-session and app-container SIDs are all covered without listing them.
  - Deny ACEs are ignored, which can only make the result stricter.
  - Inherit-only ACEs are correctly skipped for the object itself.
  - `CREATOR OWNER` is correctly ignored on the object, and `OWNER RIGHTS` is folded into the owner check.
  - The owner must be an admin principal.
  - A NULL DACL or an unknown ACE type means writable.
  - Ancestors allow "create folder" (needed for `C:\`), but not delete-child, delete, change-permissions or take-ownership.
  - Every file and subfolder in the program folder is checked.
  - Errors count as not trusted.
  - On this PC it gives the right answer for the installed copy and for user-owned folders. A user-made junction is refused
    because its own owner is read (verified).
- **The check runs on both sides.** The editor checks before showing the UAC prompt, so an untrusted copy is never elevated just
  to install. The elevated runner checks again on its own path. The runner-side check cannot protect against a runner that was
  *already* tampered with (that code runs before the check). The protection that matters is that an honest runner never registers
  a writable path, and the editor never elevates an untrusted copy to install.
- **Install mode has no side effects:** no `ApplicationConfiguration`, no `RunnerContext`, no mutex, no display access, no module
  initialisers in the solution. The failure exit codes (10 to 17) stay clear of 1 (the generic failure code), and every code has
  a readable message (tested).
- **The `IsInstalled` deadlock is fixed.** Both pipes are drained at the same time with `ReadToEndAsync`, a hung `schtasks` is
  killed after 5 s, and a cancelled UAC prompt is recognised by `NativeErrorCode == 1223` instead of every `Win32Exception`.
- **Compatible with the old tasks.** The same task name and the same settings (`IgnoreNew`, battery flags, `PT0S`,
  `StartWhenAvailable`, `InteractiveToken`, `HighestAvailable`). `TASK_CREATE_OR_UPDATE` replaces an old definition in place.
  `installer/Glance.iss` still removes the task by name on uninstall.
- **Tests:** the 62 new tests are readable and mostly behaviour-focused, and mutation check 1 shows the pure rules are pinned. All
  of them test code that is new in this change, so they would not compile against `main`. They are not regression tests of the
  old bug, which lived in untestable UI code.

---

## Proof: the bug existed and is fixed

**Problem A: the task file can be swapped. Shown on unfixed `main` (`0a9f10b`).**
- `main`'s `Autostart.Install` writes `Path.Combine(Path.GetTempPath(), "GlanceRunnerTask.xml")`. After the UAC prompt, an elevated `schtasks /Create ... /XML "<that path>"` reads it.
- From a **non-elevated** shell (`IsInRole(Administrator) = False`), overwriting `C:\Users\<user>\AppData\Local\Temp\GlanceRunnerTask.xml` succeeded. The test file was removed afterwards.
- So any normal process can replace the definition that the elevated `schtasks` registers.
- On the branch (`3056a18`), `git grep "GetTempPath|/XML"` over the editor, runner and `Security` code finds only the AIDA64 import preview folder (`MainWindow.cs:820`), which is unrelated and also on `main`. The XML is built in memory by the elevated runner and registered through the Task Scheduler COM API, so there is no file to swap.

**Problem B: the task can point at a user-writable runner.**
- On `main`, there is no trust or ACL check at all in `Autostart.cs` or `ServiceWindow.cs` (the search finds no matches).
- On the branch, a throwaway test against the **real file system** gave these results:

| Test | Result |
|---|---|
| A runner folder created by the user under `%TEMP%` | Refused: `CDZAN\Zan owns C:\Users\Zan\AppData\Local\Temp\glance-proof-h6-..., so it can change its permissions.` (Passed) |
| `C:\Program Files\Glance`, the installed copy on this PC | Trusted (Passed) |

**Not provable here:** actually registering the task through the UAC prompt, and the task starting at the next sign-in. To check by hand:
1. Install with the installer.
2. In **Background service**, tick **Start automatically**.
3. Approve the prompt.
4. Sign out and back in, and confirm the runner starts without a prompt.
5. Run the same steps from an extracted zip in Downloads, and confirm the box is refused with a clear message.

Also see finding **H1** above. It is older than this change, but it means H6 should not be called fully closed until it is addressed.
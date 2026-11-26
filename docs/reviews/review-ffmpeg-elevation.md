# Code review: only start an admin-only ffmpeg.exe from the elevated runner (H3)

**Branch:** `claude/fix-ffmpeg-elevation-a3dfcc` (one commit, `e5cd903`, on top of `main` at `0a9f10b`)
**Date:** 2026-11-26
**Scope:** the whole diff (8 files, +585/-35) and the code around it: `Ffmpeg`, `FfmpegTrust`, `VideoSource`, `ThemeRenderer`,
`SetupChecks`, `SetupWindow`, `MainWindow` (FFmpeg install), `RunnerContext`, the new tests, and the five sibling branches that
will be merged next to it.
**Method:** read every changed line and its callers; built and tested; ran two mutation checks; ran the compiled trust checker
against real files on this PC through a throw-away F# script in the session scratchpad (no repo files changed); simulated the
merges with `git merge-tree`. Read-only `winget show Gyan.FFmpeg` to see the package's installer type. Nothing was installed,
the editor and runner were not started, and nothing was tested on the physical display.

---

## Verdict: merge after fixes

The trust rule itself is careful and fails closed. It uses an allow-list of admin principals, judges rights by where they sit
(file, parent folder, ancestors), checks owners, rejects links, junctions and non-fixed drives, and is a pure function with good
table tests. I found no way for a non-admin process to get an untrusted `ffmpeg.exe` started by the elevated runner through this
code.

Two things should be settled before merging:

1. **M1:** nobody has checked that the new "Install for all users" path really produces a copy the runner accepts. That is the
   brief's "keep video working in the normal case". Until it is verified, every existing user-scope install stops playing video
   in the runner, the owner's PC included. This PC has FFmpeg only in user scope, and the compiled checker rejects it.
2. **M2:** the line that switches the check on (`Search(Environment.IsPrivilegedProcess, ...)`) has no test. A mutation that
   turns the check off still passes all 298 tests.

M3 (unify with the autostart branch's `FolderTrust`) can happen in the merge or right after it. M4 is a follow-up that the original
finding already called "better". None of the findings are high.

## Build and test

| Step | Result |
|---|---|
| `dotnet build src/Glance.sln -c Release -p:SkipHost=true` | Succeeded, 0 errors, 30 warnings. None of the warnings is in a file this branch adds. The CA1416 warnings in `SetupChecks.cs:165-169` are older code that moved down. |
| `dotnet test src/Glance.Tests -c Release` | **298/298 passed** (about 2 s). 45 of them are new (`main` has 253): 16 `FfmpegTrustTests` methods (40 cases with theories) and 5 `FfmpegSetupCheckTests`. |
| Mutation 1: `Ffmpeg.Search` always returns the first existing candidate (trust check off) | **All 298 still pass.** See M2. |
| Mutation 2: drop `DeleteChild` from `ParentMask` and disable the reparse-point rule | 3 tests fail, as they should (`Writes_in_the_folder_holding_ffmpeg_reject_it(64)`, `Links_and_junctions_on_the_path_reject_it(0)` and `(2)`). |
| After the mutations | Both files restored with `git checkout --`, the build and tests were rerun (298/298), and `git status` was clean. |

The compiled `FfmpegTrust.Check`, run against real files on this PC:

| Path | Verdict |
|---|---|
| `C:\Windows\System32\cmd.exe` | trusted |
| `C:\Program Files\dotnet\dotnet.exe`, `C:\Program Files\Git\cmd\git.exe` | trusted. This is the same ACL shape a machine-scope winget package should get. |
| The user-scope `...\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_...\ffmpeg-9.1-full_build\bin\ffmpeg.exe` | rejected: "the file is owned by `<the signed-in user>`, not by an administrator" |
| `%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe` (an App Execution Alias) | rejected: link or junction |
| `Ffmpeg.FindForElevated()` | `null`, so on this PC the runner would refuse FFmpeg after the merge |

---

## Findings

### Medium

#### M1. The "install for all users" path has not been checked, and existing installs stop working in the runner
- **Where:** `src/Glance.Core/Setup/SetupChecks.cs:86-95` (`WingetInstallStartInfo`), `src/Glance.Core/Media/Ffmpeg.cs:67-84`
  (the Program Files winget candidates), `src/Glance.Core/Setup/SetupChecks.cs:98-101` (`FfmpegMachineInstallHelp`).
- **Evidence:**
  - `winget show Gyan.FFmpeg` reports `Installer Type: portable (zip)` with no scope restriction. So `--scope machine` is
    plausible and should put the files under `%ProgramFiles%\WinGet\Packages\...`, with a symlink in `%ProgramFiles%\WinGet\Links`.
    The code assumes exactly that. Nobody has run the install and read the resulting ACLs. The commit message and the code do not
    claim it was verified.
  - winget first extracts the zip into the elevated user's `%TEMP%` (which is the same user's profile). If the files are then
    *moved* into Program Files instead of copied, NTFS keeps the security descriptor they had in `%TEMP%`, and that descriptor gives
    the user Full Control. In that case `FfmpegTrust` is right to reject the copy, but video in the runner still does not work.
    This failure mode is plausible and not confirmed. It is exactly what a test install would show.
  - Every user who already has the user-scope package, which is what the old "Install FFmpeg" button installed, now gets
    "FFmpeg is only in a folder normal users can change (see runner.log)" on the display. With a user-scope copy present,
    winget may answer "already installed" to the machine-scope install. The help text covers this, but only as text.
- **Failure scenario:** the owner merges, the runner restarts, and the video background on the LCD turns into an error label. They
  click "Install for all users", approve UAC, and either winget refuses because of the existing user install, or it installs a
  copy whose ACL the runner rejects. Video in the runner keeps failing, and the setup row keeps saying "Found at ..., but that
  folder can be changed".
- **Remedial action:**
  1. On a VM or a spare account, run `winget install --id Gyan.FFmpeg --exact --scope machine` from an elevated terminal. Then run
     `icacls` on the package folder and on `ffmpeg.exe`, and run `FfmpegTrust.Check` on the result (a 10-line F# script against
     `Glance.Core.dll` is enough). Write the outcome in the PR.
  2. If the files carry user ACEs, add an elevated repair step after the install (`icacls "<package dir>" /reset /T` run through
     `runas`). Or offer a supported layout that does not depend on winget's internals: the Inno installer already puts the app in
     `{autopf}` with `PrivilegesRequired=admin`, so an optional "put FFmpeg next to Glance" step (`{app}\ffmpeg\ffmpeg.exe`,
     which is already the first candidate) would be trusted by construction.
  3. Mention the behaviour change in the release notes and the README ("video in the background runner needs FFmpeg installed
     for all users").

#### M2. The code that turns the check on is not tested; a mutation that removes the protection passes every test
- **Where:** `src/Glance.Core/Media/Ffmpeg.cs:37` and `:87-98`.
- **Evidence:** `Search` is `internal`, and `Glance.Core` has no `InternalsVisibleTo`, so tests cannot call it. Replacing
  `if (!requireAdminOnly) return candidate;` with `return candidate;` left 298/298 passing. Replacing
  `Environment.IsPrivilegedProcess` with `false` on line 37 would pass the same way. The pure rule is well covered (mutation 2
  was caught), but the security property of H3 depends on these two lines.
- **Failure scenario:** a later refactor, say a merge that moves candidate search into a shared helper, drops the
  `requireAdminOnly` branch. CI stays green, and the elevated runner goes back to starting `ffmpeg.exe` from the user PATH.
- **Remedial action:** make the search injectable and test it:
  `internal static string? Search(IEnumerable<string> candidates, Func<string,bool> exists, bool requireAdminOnly, Func<string,TrustVerdict> check, Action<string,string>? onRejected)`.
  Have the current overload pass the real `Candidates()`, `File.Exists` and `FfmpegTrust.Check`, and add
  `[assembly: InternalsVisibleTo("Glance.Tests")]` (or make it public). Test that:
  - when elevated, an untrusted first candidate is skipped and reported, and a later trusted one is returned;
  - when elevated and nothing is trusted, the result is `null` and every rejection is reported;
  - when not elevated, the first existing candidate is returned without calling `check`.

  Also test that `Candidates()` lists `%ProgramFiles%\WinGet\Packages\*FFmpeg*\...\ffmpeg.exe`. A temp folder with
  `LOCALAPPDATA` redirected covers the user-scope half.

#### M3. Two ACL trust implementations with different rules (merge with `claude/fix-autostart-task-0011d2`)
- **Where:** this branch's `src/Glance.Core/Media/FfmpegTrust.cs`, and the sibling's `src/Glance.Core/Security/FolderTrust.cs`.
- **Evidence:** both answer the same question ("can only administrators change what this elevated process will run?") with the
  same allow-list and nearly the same masks, but they differ in ways that matter:

  | Aspect | `FfmpegTrust` (this branch) | `FolderTrust` (autostart branch) |
  |---|---|---|
  | Scope | one file, its parent, its ancestors | a folder, everything inside it (recursive), its ancestors |
  | Reading ACLs | `GetAccessRules(true, true, ...)` | raw `RawSecurityDescriptor`. NULL DACL means "everyone"; an unknown ACE type is treated as allow-everything. |
  | Links and junctions | rejected anywhere on the path | **not checked** |
  | Network, removable, `\\?\`, relative paths | rejected (`DriveType.Fixed`, `IsLocalDrivePath`) | not checked |
  | Chain must start at the file and end at a drive root | yes (`Evaluate` line 72) | no (`Decide` trusts whatever it is given) |
  | Owner allow-list | also accepts `S-1-3-0` / `S-1-3-4` as owners (harmless, see N2) | SYSTEM, Administrators, TrustedInstaller only |
  | Folder mask | no `FILE_WRITE_EA` | includes `FILE_WRITE_EA` |
  | Result type | `Glance.Core.Media.TrustVerdict` (`readonly record struct`, `Reason` not null) | `Glance.Core.Security.TrustVerdict` (`record`, `Reason` nullable, `PrincipalSid`) |

  `git merge-tree` shows no textual conflict, because the files and namespaces are different. Any file that imports both
  `Glance.Core.Media` and `Glance.Core.Security` gets `CS0104: 'TrustVerdict' is an ambiguous reference`. Two copies of a
  security rule will drift apart, and each has a protection the other lacks.
- **Failure scenario:** someone fixes a bypass in one class, for example adds the reparse-point rule to `FolderTrust` because an
  installer put a symlink in the app folder, and does not know the other copy exists.
- **Remedial action:** keep one module, `Glance.Core.Security.FolderTrust`, because it has the stricter raw reader, and fold in
  what `FfmpegTrust` adds:
  1. `IsReparsePoint` on `ObjectSecurity`, rejected for every object.
  2. The fixed-local-drive and path-shape checks.
  3. The "chain starts at the target and ends at a drive root" completeness check.
  4. A `CheckExecutable(string exe)` entry that yields the file (`TrustRole.File`), its parent (`TrustRole.Folder`, not
     recursive) and the ancestors (`TrustRole.Ancestor`).

  Then `Ffmpeg.Search` calls `FolderTrust.CheckExecutable`, `FfmpegTrust.cs` is deleted, and its chain tests are ported to
  `FolderTrust.Decide` with roles. There is one `TrustVerdict`. Merge the autostart branch first, then rebase this one onto it.
  This branch needs M1 and M2 anyway.

#### M4. The trusted ffmpeg still runs as administrator on input a normal process controls
- **Where:** `src/Glance.Core/Media/VideoSource.cs:37-44`. Inputs come from `%AppData%\Glance\Themes\...` (user-writable, reloaded
  by the runner) and from `LIVE` on the control pipe.
- **Evidence:** H3's own remedial list says decoding in a non-elevated process is "better", and the second half of the H3
  scenario ("the elevated ffmpeg parses media files from themes shared by other people") is not addressed. In addition:
  - FFmpeg probes the content, not the file extension. A `.mp4` that is really an HLS or concat playlist makes the elevated
    process open other files or network URLs. There is no `-protocol_whitelist`.
  - The child inherits the runner's environment, including the user PATH (on this PC that is about ten user-writable folders),
    and the runner's current directory. FFmpeg's own `LoadLibrary` wrapper restricts itself to the app folder and System32 (from
    the FFmpeg sources; not re-verified here), and the Gyan full build is static (only `ffmpeg.exe`, `ffplay.exe` and `ffprobe.exe`
    in `bin`, checked on this PC). System DLLs that it pulls in can still use the default search order.
- **Failure scenario:** a non-admin process writes a theme whose video is a crafted file aimed at an FFmpeg parser bug (FFmpeg
  ships several of these fixes a year). The runner reloads, and the exploit runs as administrator with no prompt. The binary is
  trusted, but its input is not.
- **Remedial action:** as a follow-up issue, not a blocker for this branch:
  1. Start ffmpeg de-elevated. For example, use `SaferCreateLevel(SAFER_LEVELID_NORMALUSER)` + `SaferComputeTokenFromLevel` +
     `CreateProcessAsUser`, or the linked (filtered) token from `GetTokenInformation(TokenLinkedToken)`. Keep the stdout pipe.
  2. Until then, add `-protocol_whitelist file,pipe` before `-i`, set `psi.WorkingDirectory` to the ffmpeg folder, and replace
     `PATH` in `psi.Environment` with the system folders only.

### Low

#### L1. In the recommended setup, runner.log gets a misleading "not used" line
- **Where:** `src/Glance.Core/Media/Ffmpeg.cs:47-54`, `:73`, `:92-95`; `src/Glance.Core/Media/FfmpegTrust.cs:80-81`.
- **Evidence:** a machine-scope portable install creates the symlink `%ProgramFiles%\WinGet\Links\ffmpeg.exe`, which is a
  candidate (line 73) and is often on the machine PATH too. The search rejects it as a link and calls `Report`, which logs
  "... is not used: the file is a link or junction ... (install it for all users)". Only then does it accept the real file under
  `Packages`. So a user who did exactly what was asked sees advice to do it again. `LastRejection` is also left set after a
  successful search, which contradicts its doc comment ("why the last search ... found nothing usable").
- **Remedial action:** collect rejections during the search and report them only when the result is `null`. Or, for a reparse
  point, resolve it with `FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true)` and judge the target's chain *and* the link's
  own chain, instead of rejecting outright. Clear `LastRejection` when a trusted copy is found.

#### L2. The setup warning says when video plays, and gets it wrong
- **Where:** `src/Glance.Core/Setup/SetupChecks.cs:71-75`.
- **Evidence:** "video backgrounds only play while the editor is sending". When the runner is running, the editor's Send goes
  through the runner (`MainWindow.cs:1313-1347`: `if (RunnerClient.IsRunning) { StartLive(); return; }`), so the runner renders the
  video and refuses FFmpeg. Video plays on the LCD only when the editor owns the display (runner not running), and always in the
  editor preview.
- **Remedial action:** reword it as "...so video backgrounds play in the editor's preview, and on the display only while the editor
  drives it without the background runner." Add an assertion on the reworded text to
  `Ffmpeg_only_in_a_user_folder_warns_that_the_runner_will_not_use_it`.

#### L3. After an install, the editor reports success using the non-elevated search
- **Where:** `src/Glance.App/MainWindow.cs:1274`.
- **Evidence:** `Ffmpeg.Find() is not null ? "FFmpeg installed. Video backgrounds are ready." ...`. In the editor `Find` accepts
  any copy, so if the machine install produced an untrusted copy (M1), or winget did nothing and some user copy exists, the
  status says "ready" while the runner will refuse it. `SetupWindow` gets this right because it re-runs `DiagnoseFfmpeg`.
- **Remedial action:** decide the message with `Ffmpeg.FindForElevated()` (off the UI thread). When only the user copy is found,
  show `FfmpegMachineInstallHelp`.

#### L4. A video that failed for lack of a trusted FFmpeg is never retried
- **Where:** `src/Glance.Core/Themes/ThemeRenderer.cs:352`, `src/Glance.Core/Media/VideoSource.cs:24-29`.
- **Evidence:** `GetVideo` caches the `VideoSource` by path and size, and keeps it alive as long as it is drawn. A source built
  while FFmpeg was missing or untrusted keeps its `Error` for good. The flow this change introduces is: see the error, then install
  for all users. After the install, the display still shows the error until the theme is reloaded or the runner restarts. This
  is older behaviour, but the change makes the path common.
- **Remedial action:** in `GetVideo`, when the cached source has `Error` set and was created without an FFmpeg, dispose and
  recreate it at most every 5-10 s. Or have the runner call `ClearImageCache()` when the setup check or the editor reports a new
  install.

#### L5. Relative PATH entries: the full path is checked, but the relative string is started
- **Where:** `src/Glance.Core/Media/Ffmpeg.cs:61-66`, `:91-94`; `FfmpegTrust.cs:124-127`.
- **Evidence:** a PATH entry such as `.`, `tools` or `C:tools` gives a non-rooted candidate. `Check` runs `Path.GetFullPath` and
  judges the resolved file, but `Search` returns the original `candidate` string. `Process.Start` then resolves it again against
  the current directory at start time. The runner does not change its directory today, so this is not exploitable now, but the
  path that is checked and the path that is started should be the same string.
- **Remedial action:** when elevated, skip PATH entries that are not fully qualified (`Path.IsPathFullyQualified`). Return the
  normalized full path that was checked (for example, add `Path` to `TrustVerdict` or return the full path from `Check`).

#### L6. The "real reader" tests depend on how the build machine's C:\ is set up
- **Where:** `src/Glance.Tests/FfmpegTrustTests.cs:208-232`.
- **Evidence:** `A_system_exe_is_trusted_on_this_pc` needs `C:\`, `C:\Windows` and `C:\Windows\System32` to be admin-owned with
  no non-admin delete or ACL rights. That holds on a stock Windows 11 (checked here), but CI runs on `windows-latest`
  hosted images, whose `C:\` ACLs nobody has looked at. This is good coverage, but it could fail for environmental reasons.
- **Remedial action:** keep the test. If it ever fails in CI, print `v.Reason` (it already does) and decide then. Consider
  `Skip.If` when `C:\` itself grants Authenticated Users more than "create folders".

#### L7. The docs still describe the old search
- **Where:** `AGENTS.md` ("FFmpeg is optional and found automatically (PATH, winget, app folder)"), `README.md:26`,
  `docs/TUTORIAL.md:24`, `docs/WALKTHROUGH.md` section 4.14.
- **Remedial action:** add one line to each: in the elevated runner, FFmpeg is used only from a folder that only administrators
  can change. Install it with "Install for all users", or `winget install --id Gyan.FFmpeg --scope machine` from an admin terminal.
  Rejections are written to `runner.log`.

### Nits

- **N1** `VideoSource.cs:27`: the message says "(see runner.log)" whenever `LastRejection` is set, which includes an editor that
  was started elevated. In that case nothing is logged, because only the runner subscribes to `Rejected`.
- **N2** `FfmpegTrust.cs:59-62,82`: the same `AdminPrincipals` set is used for owners. CREATOR OWNER (`S-1-3-0`) and OWNER
  RIGHTS (`S-1-3-4`) are placeholders and never real owners. Only an admin can set such an owner, so this is harmless, but keep a
  separate owner set (SYSTEM, Administrators, TrustedInstaller) the way `FolderTrust` does.
- **N3** `FfmpegTrust.cs:150`: `GetAccessRules` hides ACE types it cannot represent. I checked on .NET 8 that a NULL DACL comes
  back as `Everyone / -1` (rejected) and a conditional (callback) ACE comes back as a plain allow (rejected), so the common cases
  fail closed. Object ACEs and other non-qualified ACEs would be skipped silently. `FolderTrust`'s raw reader treats unknown ACE
  types as allow-everything, and M3 would inherit that.
- **N4** `MainWindow.cs:1270`, `SetupWindow.cs:143`: `Process.Start(...)!` with `UseShellExecute = true` can return `null` (the shell
  can hand the request to an existing process). The resulting `NullReferenceException` would show up as "FFmpeg install failed:
  Object reference not set...". Check for null and say "winget did not start".
- **N5** `Ffmpeg.cs:53`: the `Rejected` handlers run while `Gate` is held. The runner's `Log` only appends to a file, so this is
  fine today. A handler that marshals onto a UI thread that is itself waiting in `Ffmpeg.Find()` would deadlock. Raise the
  events after leaving the lock.

---

## Test gaps

- The wiring is untested (M2): which candidates are searched, whether they are skipped when elevated, `LastRejection`, and
  whether `Rejected` is raised once per path.
- `DiagnoseFfmpeg` is tested for its three outcomes, but `SetupChecks.Run` is not tested for the case where `found` is user-scope
  and `FindForElevated` is null. That needs the injectable search from M2.
- There is no test that the parent-folder rule rejects `AppendData` *alone* for a non-well-known SID (the theory uses `Users`
  only), and no test that an allow ACE for `S-1-3-0` that is *not* inherit-only is ignored. Both are low value.
- There is no test for a deny-only ACL (no allow entries for non-admins). It should be trusted.
- Nothing covers `WingetInstallStartInfo` with a package id that contains spaces. Only constants are passed today.

## What was checked and found sound

- **Allow-list, not deny-list.** Any allow ACE for a principal other than SYSTEM, Administrators or TrustedInstaller that holds a
  dangerous right rejects the file. That covers the current user's own SID, Users, Authenticated Users, Everyone, INTERACTIVE,
  the logon SID (`S-1-5-5-x-y`), domain groups, capability SIDs and anything else nobody thought of. There is a test with an
  arbitrary domain SID.
- **Inheritance.** `GetAccessRules(true, true, ...)` includes inherited ACEs, which is what applies to the object now.
  Inherit-only ACEs are skipped correctly: they do not apply to the object itself, and each child's effective ACL is read on its
  own. C:\'s inherit-only `Authenticated Users: Modify` entry is handled; a test covers it.
- **Deny ACEs are ignored.** That can only reject a file that is in fact safe, never accept an unsafe one.
- **Owner rights.** An owner implicitly has `READ_CONTROL` and `WRITE_DAC`, so every object on the chain must be owned by an
  admin principal. A file owned by the user's own SID (any user-scope install) is rejected; this was checked on this PC.
- **Rights by position.** To replace or rename an object you need `DELETE` on it or `FILE_DELETE_CHILD` on its parent. Both are
  checked for every link of the chain: the file `Delete`, the parent `DeleteChild`, every ancestor `Delete` and `DeleteChild`.
  `WRITE_DAC` and `WRITE_OWNER` (and `GENERIC_ALL`) are checked everywhere. Creating new entries in ancestors is correctly
  allowed: it cannot shadow an absolute path, and C:\ needs it.
- **Generic bits.** `GENERIC_ALL` and `GENERIC_WRITE` are in the masks. They do show up in effective ACEs set by some tools.
- **Planting DLLs next to the exe.** `WriteData` and `AppendData` on the folder that holds ffmpeg are rejected.
- **Links, junctions and hard links.** Every object's attributes are read without following links (`File.GetAttributes`), so a
  junction anywhere on the path rejects the file. Hard links do not help an attacker: the file's ACL is shared by all its names,
  and making a new name inside the trusted folder needs write access to that folder.
- **The gap between check and start (TOCTOU).** Once a path is trusted, nothing on it can be changed by a non-admin, so the time
  between the check and `CreateProcess`, and the cached path that is reused later, give a non-admin process no opening. Changes
  made by an admin are not a security boundary.
- **Path shapes.** UNC, `\\?\`, relative and `..` paths are rejected. Mapped network drives and removable drives are rejected by
  `DriveType.Fixed`. The DACL must be readable; if it is not, the file is rejected.
- **Detecting elevation.** `Environment.IsPrivilegedProcess` (.NET 8) is true for the elevated runner and false for the
  de-elevated editor.
- **The editor (not elevated).** `Search(false, ...)` returns the first existing candidate, in the same order as before. The
  new Program Files winget candidates come before the `%LOCALAPPDATA%` ones, so the editor's preview behaves as before and
  prefers an all-users copy.
- **Thread safety.** `Find`/`Reset` now take a lock. Before, `_cached` and `_checked` were raced between renderers.
- **UAC.** A cancelled prompt is reported as `Win32Exception` with `NativeErrorCode == 1223` (`ERROR_CANCELLED`) and handled in
  both places. `runas` correctly needs `UseShellExecute = true`, and the exit code still comes back.
- **Logging.** One line per path and reason (`Reported` set) goes into `runner.log`, the same file the runner already uses.
- **Merges.** `git merge-tree --write-tree` between this branch and each sibling
  (`fix-saveas-assets`, `fix-live-empty-folder`, `fix-display-loop`, `fix-autostart-task`, `feature-shutdown-blank`) is clean.
  The only `RunnerContext.cs` change here is one line in the constructor (line 50). The sibling changes to `RunnerContext` are in
  `Execute`, shutdown and supervision. The only semantic issue is M3 (the duplicate trust module and `TrustVerdict`).

---

## Proof: the bug existed and is fixed

The scenario was run on both versions with the same throwaway test:
1. Without admin rights, create `%TEMP%\glance-proof-h3-<guid>\ffmpeg.exe`. Any normal program can do this.
2. Put that folder first on `PATH`.
3. Ask the lookup that the elevated runner uses which ffmpeg it would start.

| Run | Lookup | Result |
|---|---|---|
| `main` (unfixed, `0a9f10b`) | `Ffmpeg.Find()`, which the runner calls on `main` | **Failed**: `The elevated runner would start the planted, user-writable file: C:\Users\...\Temp\glance-proof-h3-...\ffmpeg.exe` |
| Branch (`e5cd903`) | `Ffmpeg.FindForElevated()` (`Search(requireAdminOnly: true)`) | Passed: the planted file is refused. `Ffmpeg.Find()` in the non-elevated editor still returns it, so the editor's behaviour is unchanged. |

**Limit:** M2 above still applies. This proof calls `FindForElevated()` directly. It does not exercise the line that chooses elevated or non-elevated mode at run time (`Environment.IsPrivilegedProcess`). A mutation of that line went unnoticed, so add a test for the switch before merging.
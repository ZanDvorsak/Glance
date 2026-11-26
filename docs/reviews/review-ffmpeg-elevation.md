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

---

## Fixes after review

Phase 1, written by the session that owns the branch. The findings above are unchanged. M3 (one trust engine, through
the autostart branch's `FolderTrust`) and M4 (decoding without admin rights) are deferred by agreement, so they are not
covered here.

**Method.** Commit `2fe3a8d` only adds test seams and changes no behaviour (298/298 still passed): `FfmpegFinder` takes
the elevation flag, candidates, file check and trust check; `ThemeRenderer` takes a video factory and clock; the editor's
install message moved to `SetupChecks.FfmpegInstallOutcome`; `InternalsVisibleTo("Glance.Tests")`. The new tests were
written against that commit and run once (**18 tests: 12 failed, 6 passed**). Then the fixes went in and the same tests
were run again (**18/18 passed**). The 6 that passed before are the M2 coverage tests plus two baselines
(`Install_outcome_is_ready_when_the_runner_can_use_the_copy`, `A_video_without_ffmpeg_is_not_retried_every_frame`); for
M2 the proof is a mutation run instead (below). Tests are in `src/Glance.Tests/FfmpegReviewFixTests.cs` and
`FfmpegTrustTests.cs`.

**Final result:** `dotnet build src/Glance.sln -c Release` succeeded with 0 errors, and `dotnet test src/Glance.Tests -c Release`
gave **319/319 passed, 0 skipped** on this PC (298 before plus 21 new).

### M1. The "install for all users" path

**What was checked, without installing anything.**
- `winget show --id Gyan.FFmpeg`: version 9.1, `Installer Type: portable (zip)`.
- Manifest `manifests/g/Gyan/FFmpeg/9.1/Gyan.FFmpeg.installer.yaml` in winget-pkgs: `InstallerType: zip`,
  `NestedInstallerType: portable`, `NestedInstallerFiles` = `ffmpeg-9.1-full_build\bin\{ffmpeg,ffplay,ffprobe}.exe`
  with aliases, no `Scope` and no `ArchiveBinariesDependOnPath`, x64 only.
- winget-cli source (master):
  - `Runtime.cpp`: the machine root is `%ProgramFiles%\WinGet\Packages` and the machine links folder is `%ProgramFiles%\WinGet\Links`.
    `PortableInstaller.cpp` adds the links folder to PATH. The package folder is `<PackageId>_<SourceId>`.
  - `InstallFlow.cpp` (`EnsureRunningAsAdminForMachineScopeInstall`): a machine-scope portable install **requires an
    elevated winget**. It fails otherwise; it does not elevate itself. So starting it with `runas` is necessary.
  - `DownloadFlow.cpp` / `ArchiveFlow.cpp`: the zip is downloaded and extracted under `%TEMP%\WinGet\...\extracted`.
    `Runtime.cpp` gives that temp tree a **protected DACL: owner = the current user, Full Control for the current
    user, SYSTEM and Administrators**.
  - `PortableInstaller.cpp` (`InstallFile`): on the same volume the extracted folder is **moved with
    `std::filesystem::rename`**; it is copied only across volumes. No code sets ACLs on the portable install folder.
- Inference (code reading plus how NTFS works; not observed): a move keeps the security descriptor, so in the normal
  case (`%TEMP%` and Program Files on C:) the installed `ffmpeg.exe` keeps **Full Control for the installing user**. The
  trust check would reject it, correctly, because that user's non-elevated programs could replace it. The plain
  `--scope machine` install was therefore not enough, and in fact it is itself a weak spot for any elevated consumer.
- Already installed for the user: the code that was read never looks across scopes, so a machine install most likely
  goes in side by side (a second folder and a second links folder). Not observed.

**Fix.** "Install for all users" now starts an elevated script (`SetupChecks.MachineInstallScript`, run by
`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe` with `runas`). The script:
1. runs the packaged App Installer `winget.exe`, not `winget` from PATH (checked here: running it directly works for
   `--version` and `show`);
2. runs `winget install --id Gyan.FFmpeg --exact --scope machine ...`;
3. whatever winget returned, runs `System32\icacls.exe <folder> /reset /T` and `/setowner *S-1-5-32-544 /T` on each
   `%ProgramFiles%\WinGet\Packages\Gyan.FFmpeg_*` folder. The files then inherit Program Files' permissions and are owned
   by Administrators. Clicking again repairs an earlier all-users install;
4. returns winget's exit code.

The script limits module loading to the system folder before it runs any cmdlet, and the package id is checked against
`[A-Za-z0-9._-]`. Clarity for per-user installs:
- The setup row says "installed for your user only", that the runner will not play video until FFmpeg is installed for
  all users, and where video still plays.
- The runner's log line says that video will not play on the display until FFmpeg is installed for all users, and how
  to do that.

| Test | Before (`2fe3a8d`) | After |
|---|---|---|
| `All_users_install_resets_the_package_folder_to_admin_only_permissions` | **Failed**: `Assert.Equal() Failure: Strings differ` / `Expected: "C:\\WINDOWS\\system32\\WindowsPowerShell\\v1."···` / `Actual: "winget"` | Passed |
| same test, extra assertion "no cmdlet before the module path is reset" (found while checking the script; the first draft used `Join-Path` there) | **Failed** with that line put back: `a cmdlet runs before PSModulePath is reset` | Passed |

The script was also parsed with PowerShell's parser (0 errors). **It was not run.**

**Conclusion and open question.** By code reading, a plain machine-scope install would be rejected, and the
install-plus-reset path should produce a copy the runner accepts: it has the same ACL shape as `C:\Program Files\dotnet`,
which the trust check accepts on this PC. **Only a real install can confirm this.** That means one click on "Install for all
users" (UAC), then the setup row should turn green and `FindForElevated()` should return
`C:\Program Files\WinGet\Packages\Gyan.FFmpeg_...\bin\ffmpeg.exe`. The user decides whether to do it. Until then, on this
PC (per-user install only) video backgrounds do not play in the runner.

### M2. The switch that turns the check on is now tested

`Ffmpeg.Find()` is `FfmpegFinder.Find()`, and its line `Search(_isElevated())` is what the tests drive (`FfmpegLookupTests`):
`Elevated_lookup_skips_a_user_writable_copy_and_uses_the_admin_only_one`,
`Elevated_lookup_finds_nothing_when_no_copy_is_admin_only_and_reports_each`,
`Normal_lookup_returns_the_first_copy_without_reading_permissions`, `Winget_package_folders_are_searched_for_ffmpeg`;
and `RealAclSkipTests.The_default_finder_follows_this_process_elevation`.

| Mutation | Result |
|---|---|
| `Search(_isElevated())` → `Search(false)` (the reviewer's case) | **8 tests fail**, e.g. `Elevated_lookup_skips_a_user_writable_copy_and_uses_the_admin_only_one` |
| `if (!requireAdminOnly)` → `if (true)` (always return the first existing copy) | **8 tests fail** |
| real finder's flag `() => Environment.IsPrivilegedProcess` → `() => true` | **1 test fails** (`The_default_finder_follows_this_process_elevation`) |
| real finder's flag → `() => false` | **not caught**: the tests run without elevation, so `false` is the right answer there. Only an elevated test run could tell. |
| No mutation | 319/319 |

### L1. No "not used" line when a usable copy is found

The finder collects rejections and reports them only when the search finds nothing. `LastRejection` is cleared when a
copy is found.

| Test | Before | After |
|---|---|---|
| `A_rejected_Links_symlink_before_a_usable_copy_logs_nothing_and_leaves_no_rejection` | **Failed**: `Assert.Empty() Failure: Collection was not empty` | Passed |

### L2. Setup warning wording

The warning now reads: "...Until then, videos play in the editor's preview, and on the display only while the editor
drives it without the background runner."

| Test | Before | After |
|---|---|---|
| `User_folder_warning_says_video_plays_in_the_preview_and_only_without_the_runner` | **Failed**: `Assert.DoesNotContain() Failure: Sub-string found` ("while the editor is sending") | Passed |

### L3. Install result based on what the runner can use

`MainWindow` now calls `Ffmpeg.FindForElevated()` off the UI thread and passes it to `SetupChecks.FfmpegInstallOutcome`.
That says "ready" only when the runner can use the copy, and otherwise explains what to do (also when winget is not found).

| Test | Before | After |
|---|---|---|
| `Install_outcome_is_not_ready_when_only_a_user_writable_copy_exists` | **Failed**: `Assert.DoesNotContain() Failure: Sub-string found` ("FFmpeg installed. Video backgrounds are ready.") | Passed |
| `Install_outcome_is_ready_when_the_runner_can_use_the_copy` (baseline) | Passed | Passed |

### L4. A video without FFmpeg is tried again

`ThemeRenderer.GetVideo` throws away and recreates a `VideoSource` that had no usable FFmpeg (`FfmpegMissing`) once it is
5 s old (`VideoRetryMs`). `Ffmpeg.Find` also paces its own disk search to once every 5 s.

| Test | Before | After |
|---|---|---|
| `A_video_without_ffmpeg_is_tried_again_after_a_few_seconds` | **Failed**: `Assert.Equal() Failure: Values differ` / `Expected: 2` / `Actual: 1` | Passed |
| `A_video_without_ffmpeg_is_not_retried_every_frame` (baseline) | Passed | Passed |

### L5. The path that is checked is the path that is started

When elevated, candidates that are not fully qualified are refused ("it comes from a relative PATH entry"). The others
are normalised with `Path.GetFullPath`, and that exact string is checked and returned.

| Test | Before | After |
|---|---|---|
| `Elevated_lookup_never_returns_a_relative_PATH_entry` (`tools\ffmpeg.exe`, `.\ffmpeg.exe`, `C:tools\ffmpeg.exe`) | **Failed** ×3: `Assert.Null() Failure: Value is not null` / `Actual: "tools\\ffmpeg.exe"` (and the other two) | Passed |
| `Elevated_lookup_returns_the_same_full_path_it_checked` | **Failed**: `Expected: "C:\\Program Files\\WinGet\\ffmpeg.exe"` / `Actual: "C:\\Program Files\\x\\..\\WinGet\\ffmpeg.exe"` | Passed |

### L6. Real-ACL tests on unusual machines

`A_system_exe_is_trusted_on_this_pc` is now a `[StockSystemDriveFact]`. It is skipped, with the reason, when the system
drive's root lets non-admins replace what is under it (the root is judged by the same rule as any folder further up the
path). The temp-folder test stays a normal fact: a user's temp folder fails the rule on any machine.

| Test | Result |
|---|---|
| `A_stock_drive_root_runs_the_real_acl_tests` | Passed: the stock C:\ ACL gives no skip reason |
| `A_drive_root_users_can_change_skips_them_with_a_reason` | Passed: Authenticated Users with Modify gives a reason containing "unusual" |
| On this PC | not skipped (0 skipped in the full run) |

There is no "before" here: this is a robustness change, not a behaviour bug. Without it, a CI image with a writable
C:\ would make the test fail for reasons unrelated to Glance.

### L7. Docs

`AGENTS.md`, `README.md` (requirements), `docs/TUTORIAL.md` (setup check table) and `docs/WALKTHROUGH.md` 4.14 now say
that the runner only uses an FFmpeg that only administrators can change, and how to install one. The rejection reason
goes to `runner.log`. `docs/walkthroughs/ffmpeg-elevation.md` was left alone; it describes the first commit.

### Nits

| Finding | Test | Before | After |
|---|---|---|---|
| N2: CREATOR OWNER / OWNER RIGHTS as owner | `Placeholder_principals_are_not_accepted_as_owners` (`S-1-3-0`, `S-1-3-4`) | **Failed** ×2: `Assert.False() Failure` / `Expected: False` / `Actual: True` | Passed (separate owner set: SYSTEM, Administrators, TrustedInstaller) |
| N5: `Rejected` raised under the lock | `Rejected_handlers_run_outside_the_lookup_lock` | **Failed** after a 2 s wait: `Reset() on another thread was blocked while Rejected was being raised` | Passed (events are raised after the lock is released) |

N1, N3 and N4 were not in scope for this phase and are unchanged.

### Still open
- **M1** needs one real "Install for all users" click to confirm. The user decides.
- **M3** (one trust engine) is phase 2, after the autostart branch lands. **M4** (ffmpeg without admin rights) is a later follow-up.
- The real finder's elevation flag can only be fully mutation-tested by an elevated test run (see M2).
- Not tested here: anything with the physical display, the runner process, UAC, and the PowerShell script running.


---

## Phase 2: switched to FolderTrust

This resolves M3. The branch was rebased onto `claude/fix-autostart-task-0011d2` at `1279d5e`, which holds
`Glance.Core.Security.FolderTrust` as the single trust engine, and the FFmpeg lookup now uses it.

**Rebase.** All 4 commits of this branch replayed cleanly, with **no conflicts**: `AGENTS.md` and `RunnerContext.cs` are
changed on both branches, but in different places. After the rebase, both engines built side by side and 435/435 tests
passed.

**Switch.**
- `Ffmpeg.Trust(exe)` = `FolderTrust.CheckExecutable(exe, TrustScope.FolderFiles)`. This reads the exe, every file next to
  it (DLLs Windows would load from there), its folder and every folder up to the drive root. `FfmpegFinder` takes
  `Glance.Core.Security.TrustVerdict`. If a verdict has no reason, the log says "its permissions are not admin-only".
- **Removed:** `src/Glance.Core/Media/FfmpegTrust.cs`, with its types `AceFact`, `PathFact` and
  `Glance.Core.Media.TrustVerdict`. There is one `TrustVerdict` now, so the `CS0104` ambiguity from M3 cannot happen.
- Unchanged: the finder, its seams, `LastRejection`, `Rejected` (raised outside the lock, only when nothing usable is
  found), the relative-PATH refusal and the full-path rule, the setup texts and the install script.
- Behaviour differences, all from FolderTrust's rules:
  - It is stricter about files next to the exe, `FILE_WRITE_EA` on the folder, and unknown ACE types (counted as writable).
  - It ignores DELETE on a drive root itself, which cannot be deleted.
  - It treats only name-surrogate reparse points (links, junctions) as links. A file compressed by Windows (WOF) is no
    longer refused.
  - Reasons are FolderTrust's sentences, for example "`<user>` owns `<path>`, so it can change its permissions."
- `AGENTS.md` and `docs/WALKTHROUGH.md` 4.14 now name FolderTrust instead of FfmpegTrust.

**Tests moved to FolderTrust.** These old FfmpegTrust cases were not already covered by FolderTrust's own tests
(`FolderTrustTests`, `AutostartReviewFixTests` M3). They are now in `FfmpegFolderTrustTests`
(`src/Glance.Tests/FfmpegTrustTests.cs`) and call `FolderTrust.Evaluate` with roles:
- a machine-scope winget chain under the real C:\ root ACL is trusted; a user-scope chain in `%LOCALAPPDATA%` is refused;
- on ffmpeg.exe: INTERACTIVE write, GENERIC_ALL, Modify, and an arbitrary domain group are refused;
- on the bin folder: WRITE_DAC, WRITE_OWNER and GENERIC_WRITE are refused;
- further up: DELETE, WRITE_DAC and WRITE_OWNER are refused, while WriteData, AppendData and GENERIC_WRITE are harmless;
- a non-admin owner at the file, its folder or Program Files is refused, and so are CREATOR OWNER and OWNER RIGHTS as owners (N2);
- the winget `Links` symlink is refused while the real package file passes;
- the real reader through `Ffmpeg.Trust`: `C:\Program Files\dotnet\dotnet.exe` is trusted (`[StockSystemDriveFact]`,
  the L6 skip, now judging the root with `FolderTrust.Decide` as an ancestor), and an exe in the user's temp folder is refused.

Cases that FolderTrust already covered (inherit-only, deny, NULL DACL, junctions, path shapes, incomplete chain,
drive type, system exe on this PC) were dropped as duplicates. No rule was missing from FolderTrust, so FolderTrust was
not changed.

**H3 re-proved on the rebased branch.** I reran the review's scenario as a throwaway test: create
`%TEMP%\glance-proof-h3-<guid>\ffmpeg.exe` without admin rights, put that folder first on `PATH`, then call
`Ffmpeg.FindForElevated()` and `Ffmpeg.Find()`. Output:

```
elevated lookup=null
editor lookup=C:\Users\<user>\AppData\Local\Temp\glance-proof-h3-1f6b…\ffmpeg.exe
FolderTrust on planted: <PC>\<user> owns C:\Users\<user>\AppData\Local\Temp\glance-proof-h3-1f6b…\ffmpeg.exe, so it can change its permissions.
```

Both assertions passed: the elevated lookup does not return the planted file, and the editor still does. The elevated
result is `null` because this PC's per-user FFmpeg is refused too (see M1). The scenario is now also a permanent test,
`FfmpegH3RegressionTests.A_planted_ffmpeg_ahead_of_everything_is_refused_when_elevated_and_still_used_by_the_editor`. It uses
the real candidate list and the real FolderTrust check, but it injects the planted path instead of changing the process
`PATH` (which other tests running in parallel would see). It passes.

**M2 still holds.**
- `Search(_isElevated())` changed to `Search(false)`: **9 tests fail** (the 8 from phase 1 plus the H3 regression test).
- The real flag changed to `() => true`: **1 test fails**.
- Both files were restored afterwards.

**Result:** `dotnet build src/Glance.sln -c Release` succeeded with 0 errors, and `dotnet test src/Glance.Tests -c Release`
gave **417/417 passed, 0 skipped**.

**Still open** (unchanged from phase 1):
- M1 needs one real "Install for all users" click to confirm.
- M4 (ffmpeg without admin rights) is a later follow-up.
- `docs/walkthroughs/ffmpeg-elevation.md` describes the first commit and still mentions FfmpegTrust.

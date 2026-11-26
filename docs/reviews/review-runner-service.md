# Security review: move the elevated work into a Windows service (`claude/feature-runner-service-8fa395`)

**Date:** 2026-11-26
**Change:** 6 commits on `claude/feature-runner-service-8fa395` (`2f8164e`..`5261090`), base `main` at `9bf03b0`, 59 files, +3300 / -739 lines.
**Problem it fixes:** review H6/H1 (`docs/reviews/review-autostart-task.md`, "Fixes after review", H1). The elevated tray runner, started at
sign-in by a "highest privileges" scheduled task, inherited the user's environment. `CORECLR_ENABLE_PROFILING`/`CORECLR_PROFILER_PATH`,
`DOTNET_DiagnosticPorts` and other `DOTNET_`/`COMPlus_` variables that any normal program can write to `HKCU\Environment` then loaded code
into an administrator process: a silent UAC bypass.
**Design note:** `docs/design/runner-service.md` (decisions, proof, manual test plan).
**Reviewer:** an independent reviewer who did not write the change. No code was changed. Nothing was installed, started, stopped, registered
or deleted (no service, no scheduled task), nothing ran elevated, the installer was not run, and the real display and the running runner
were not touched.

**How to read a finding:**
- **Proven**: reproduced here with a test or a probe program (the output is quoted).
- **Confirmed**: the code path exists exactly as described; not executed.
- **Plausible**: depends on Windows behaviour that could not be observed without installing the service.

---

## Verdict: merge after fixes

The architecture is right, and it does remove H1 for the service. The privileged process (`Glance.Service`, LocalSystem) gets its
environment from the Service Control Manager, never parses a file a user chose, never starts a process, and accepts only fixed-size
binary requests. Rendering, images, fonts, FFmpeg and the `LIVE` folder all stay in the user's own processes. The pipe DACLs, the
owner check on the client side, the anonymous impersonation level, the per-request console-session rule and the service DACL are
each in one tested place, and the mutation checks the author lists reproduce exactly (see Proof verification). The tray app is now
`asInvoker`, and no Glance manifest asks for elevation (proven failing on `main`, passing here).

One high finding must be fixed before this ships, because the change itself creates it: **the installer lets the administrator put the
service binary in a user-writable folder** (H1). Glance 0.1.1 refused exactly that for its elevated task (`FolderTrust`); the service
has no such guard, and SYSTEM is a bigger prize than the old elevated runner.

Four medium findings should be fixed in the same round. Each has a small, specific fix:
- **M1** The service pipes are created without `FILE_FLAG_FIRST_PIPE_INSTANCE`, so a squatter's pipe is *joined*, not refused. That
  contradicts the design note; proven with two failing tests.
- **M2** Any interactive user can hold all 8 display-pipe instances with idle connections. The console user then loses the service,
  and SYSTEM appends one log line per second, without limit. Proven.
- **M3** A leftover 0.1 "Glance Runner" task now starts the *new* tray elevated with the user's environment: H1 again, and nothing
  detects it.
- **M4** SYSTEM loads `libusb0.dll` (and LibreHardwareMonitor its vendor DLLs) with the default DLL search order, which ends in
  `PATH`. Not exploitable on this PC (checked); one call fixes it.

None of these needs a design change. After the fixes, run the manual test plan below on a PC with the display: the service entry
point, session 0, pre-shutdown and the installer can only be verified there.

---

## Build and test results

Run in the branch worktree on 2026-11-26:

| Step | Result |
|---|---|
| `dotnet build src/Glance.sln -c Release -p:SkipHost=true` | **Succeeded**, 0 errors, **138 warnings**. The design note (§13) says the only warnings are the existing `SKPath` obsolete ones. That is not accurate. The build also reports `CA1416` warnings in the new `RunnerServiceTests.cs` (lines 141-252), analyzer-version warnings, and an existing `CS8602` at `MainWindow.cs:1232` (not in this diff). None of them matters for the change. |
| `dotnet test src/Glance.Tests -c Release` | **Passed 624/624** in about 10 s. |
| Baseline on `main` (`9bf03b0`, scratch worktree) | **542/542** passed, as the design note says. |

---

## Proof verification

The user's rule is that every bug must be proven to exist and then proven fixed. The author's proof (§13) was written against stubs
("test first against today's behaviour, keep the run, then write the code"). Most new tests use types that do not exist on `main`, so
they cannot be re-run there as they are. I re-ran what can be re-run, ported two key tests to `main`, and repeated part of the
mutation check. Scratch worktrees were used (`main` and branch HEAD, under the session scratchpad) and removed afterwards; `git worktree list`
no longer shows them, and the branch worktree is clean apart from the two new documents.

### 1. Before/after on `main` vs the branch (proven)

I wrote two tests that compile on both `main` and the branch (they only use `ControlPipeClient`, which exists on both, and file I/O):

| Test | `main` (9bf03b0) | Branch |
|---|---|---|
| `Review_no_Glance_program_asks_for_administrator_rights` (same rule as `ServiceLayoutTests.No_Glance_program_asks_for_administrator_rights`) | **Failed**: `Assert.Empty() Failure: Collection was not empty  Collection: ["Glance.Runner\\app.manifest", "Glance.SensorHost\\app.manifest"]` | **Passed** |
| `Review_a_squatter_on_the_tray_control_pipe_cannot_act_as_the_user` (same as the author's test of that name) | **Failed**: `Assert.NotEqual() Failure: Strings are equal  Expected: Not "S-1-5-21-...-1001"  Actual: "S-1-5-21-...-1001"` | **Passed** |

Full suites with these two added: `main` 542 passed / 2 failed; branch 626 passed / 0 failed. This matches the author's "before" for those
two rules.

**About the H1 root cause itself.** The vulnerable path on `main` was "requireAdministrator runner + highest-privilege logon task with
the interactive token". The branch removes both: the manifest rule above is proven, `AutostartInstaller.cs` and the `--install-autostart`
mode are deleted (checked by `ServiceLayoutTests.The_elevated_helpers_are_gone_and_the_service_is_built`), and the installer deletes the
old task. The service's own environment comes from the SCM. That is Windows behaviour, and it is correct: a service process gets the
system environment plus its own `Environment` registry value, never `HKCU\Environment`. It cannot be tested without installing the
service; the manual test plan below has an end-to-end check (test 3).

### 2. Mutation check (proven, reproduced)

I repeated the author's mutations M1-M4 and M9 in a scratch copy of the branch. I also added four of my own (R1-R4). I ran the service
test classes (84 tests) after each one:

| Mutation | Author's result | My result |
|---|---|---|
| M1 another session may draw (`DisplayAccess.cs:34`) | 6 | **6 failed** (same tests) |
| M2 no NETWORK deny (`ServicePipeSecurity.cs:28`) | 2 | **2 failed** |
| M3 any pipe owner trusted (`PipeServerCheck.cs:18`) | 4 | **4 failed** |
| M4 display client allows impersonation (`RemoteFrameDisplay.cs:66`) | 1 | **1 failed** |
| M9 frames drawn while asleep (`DisplayServer.cs:175`) | 2 | **2 failed** |
| R1 tray control client without `PipeOptions.CurrentUserOnly` (`ControlPipeClient.cs:33`) | not tried | **0 failed: survives** (test gap T1) |
| R2 a refused HELLO keeps the connection open (`DisplayServer.cs:135`) | not tried | **0 failed: survives** (test gap T4) |
| R3 brightness not clamped (`DisplayServer.cs:159`) | not tried | **0 failed: survives** (harmless, nit) |

After restoring, all 84 pass again and `git status` of the scratch copy showed only my test files.

### 3. Does anything still let a normal user's environment, files or registry reach SYSTEM or admin?

I checked every path into an elevated or SYSTEM process:

| Path | Result |
|---|---|
| Service environment | From the SCM (system environment). Not `HKCU`. **Sound.** `StartupHookSupport=false` is also set. |
| Service input | Only `DisplayWire` requests: an opcode plus an exact length checked before the payload is read, one brightness byte that is clamped, and frames passed to USB as opaque bytes. No paths, no JSON, no Skia, no processes. **Sound.** |
| Files SYSTEM writes | Only `%ProgramData%\Glance\service.log`, written only after `FolderTrust.CheckFolder` (owner and ACL, ancestors, no links). **Sound** (but see L6 and M2). |
| Files SYSTEM *executes* | `{app}\Glance.Service.exe` and every DLL next to it. **Not guarded: H1.** |
| DLLs SYSTEM loads by name | `libusb0.dll` and LibreHardwareMonitor's vendor DLLs (it imports `LoadLibraryW`), with the default search order, which ends in the machine `PATH`. **Conditional: M4.** |
| Elevated processes started by Glance | Only Windows tools: `sc.exe`, `schtasks.exe` through UAC from the editor, and `sc`/`net`/`schtasks`/`taskkill`/`tasklist`/`find`/`cmd /d` from the installer, all with `{sys}` full paths. They are native, so `CORECLR_`/`DOTNET_`/`COR_` variables do nothing. The installer's legacy `EXIT` uses `CreateFileW` with `SECURITY_SQOS_PRESENT` + anonymous. **Sound.** |
| A Glance .NET program running elevated with the user's environment | Not started by Glance any more. **But** a leftover 0.1 task starts `Glance.Runner.exe` with "highest privileges" whatever its manifest says, and the tray does not notice: **M3.** `Glance.Service.exe --console` run from an elevated prompt is the same by design (N2). |

---

## Findings

| ID | Severity | Summary |
|---|---|---|
| H1 | High | The service binary can be installed into a folder normal users can write; SYSTEM then runs their code (no folder check, unlike 0.1.1's task) |
| M1 | Medium | Service pipes lack `FILE_FLAG_FIRST_PIPE_INSTANCE`: the service joins a squatter's pipe under the squatter's rules, for good (proven) |
| M2 | Medium | 8 idle connections from any interactive user lock the console user out of the display pipe; SYSTEM logs one line per second without limit (proven) |
| M3 | Medium | A leftover 0.1 "Glance Runner" task starts the new tray elevated with the user's environment (H1 again); nothing verifies or detects it |
| M4 | Medium | SYSTEM loads `libusb0.dll` and vendor DLLs with the default search order (ends in `PATH`); no `SetDefaultDllDirectories` |
| L1 | Low | Network-path clients are not rejected by the pipe and get a non-null session id (proven: 9 and 13); the "unknown session" second line does not exist |
| L2 | Low | On suspend the service waits only ~400 ms for the device; an owed blank is then paid by nobody before Windows sleeps |
| L3 | Low | `DisplayConnector` treats "service busy or still starting" as "no service" and opens USB directly as the user, then never moves back |
| L4 | Low | Installer ignores every `sc.exe`/`net.exe` result; `taskkill` after a failed stop triggers the recovery restart mid-upgrade |
| L5 | Low | `PreshutdownTimeout` is written straight to the registry; the SCM probably applies it only after a reboot (plausible) |
| L6 | Low | `ServiceLog` is opened before the process knows it is the service: a user who runs `Glance.Service.exe` first disables the service log for good |
| L7 | Low | The service runs as full LocalSystem with every privilege; no `sc privs` / service SID hardening |
| L8 | Low | The tray control pipe has no NETWORK deny and no first-instance flag: another user can squat `Glance.Runner.Control.<n>` (cross-user DoS, 1 log line/s) |
| L9 | Low | `AppPaths.FindExe` / `FindEditor` dev fallback walks up to the drive root and can start a planted `C:\Glance.Runner\bin\...\Glance.Runner.exe` (pre-existing) |
| L10 | Low | The sensor pipe is created only after LibreHardwareMonitor has opened (seconds), and its errors are never logged |
| N1-N6 | Nit | Doc accuracy, `--console` in release builds, old helpers still in Core, leftovers from 0.1.1, status text, design-note wording |

### H1. The service binary can be installed into a user-writable folder; SYSTEM then runs a user's code (confirmed)

- **Where:**
  - `installer/Glance.iss:23`: `DefaultDirName={autopf}\{#AppName}`, with no `DisableDirPage=yes`, so the directory page is shown on a
    fresh install. On an upgrade, Inno's default `UsePreviousAppDir=yes` keeps the previous folder.
  - `installer/Glance.iss:157-175` (`InstallService`): registers `{app}\Glance.Service.exe` as a LocalSystem auto-start service. It
    checks nothing about `{app}`.
  - `src/Glance.Service/Program.cs:16-33`: no self-check either.
- **Evidence:**
  - This PC's `C:\` ACL (`icacls C:\`) contains `NT AUTHORITY\Authenticated Users:(OI)(CI)(IO)(M)`: every folder an administrator
    creates under `C:\` inherits **Modify for all authenticated users**. Secondary drives formatted by Windows usually have the same rule.
  - So an administrator who types `C:\Glance` or `D:\Apps\Glance` on the directory page gets a service folder that any user can write.
  - Glance 0.1.1 refused exactly this for its elevated logon task: `AutostartInstaller.Install` called
    `FolderTrust.CheckExecutable(exe, TrustScope.FolderTree)` and returned `FolderNotTrusted` (deleted in this change). The design note's
    sections 8-9 only apply `FolderTrust` to the log folder.
- **Failure scenario:**
  1. An administrator installs Glance to `D:\Apps\Glance`.
  2. Malware running as any normal user (or the same user, not elevated) replaces `Glance.Service.exe`. It could equally replace
     `hostfxr.dll`, `coreclr.dll` or any managed DLL in the self-contained package.
  3. At the next boot, or after a crash restart, the SCM starts it as SYSTEM.
  4. That is a silent elevation to SYSTEM. It is worse than the H1 it replaces, and it needs no environment trick.
- **Remedial action:**
  1. In `Glance.iss` add `DisableDirPage=yes`, so the service always lives in `{autopf}\Glance`. Alternatively keep the page and, in
     `NextButtonClick(wpSelectDir)`, refuse any folder outside `{autopf}`. Also refuse at `ssPostInstall` when an upgrade reuses an unsafe
     previous folder.
  2. Before `sc create`, reset the folder's ACL explicitly. Inherited Program Files rules can be changed by other software, and
     `[Dirs] Permissions:` can only add grants. Run `icacls "{app}" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F
     *S-1-5-32-545:(OI)(CI)RX *S-1-15-2-1:(OI)(CI)RX` (from `{sys}`, hidden, and check the exit code).
  3. Defence in depth: at the top of `ServiceMain`, run `FolderTrust.CheckExecutable(Environment.ProcessPath, TrustScope.FolderTree)`.
     If it fails, report `SERVICE_STOPPED` with an error and write to the Windows event log. This catches a folder whose rules were
     loosened later. It does not replace step 1, because a replaced exe never runs the check.
  4. Add a `ServiceLayoutTests` rule that `Glance.iss` contains `DisableDirPage=yes` (or the `NextButtonClick` guard) and the ACL reset.

### M1. The service pipes are created without `FILE_FLAG_FIRST_PIPE_INSTANCE`: the service joins a squatter's pipe (proven)

- **Where:**
  - `src/Glance.Core/Service/DisplayServer.cs:86-91` (`CreateInstance`, used by `Start` at :80-84).
  - `src/Glance.Core/Sensors/SensorPipeServer.cs:38-39`. Both pass `PipeOptions.Asynchronous` only.
  - .NET sets `FILE_FLAG_FIRST_PIPE_INSTANCE` by itself only when `maxNumberOfServerInstances == 1`. These servers use 8 and 16.
- **Design note claim (§4, "Squatting"):** "A squatter can only make the display unavailable (the service fails to create its pipe and
  logs it)." `ServiceRuntime.TryServeDisplay` (`ServiceRuntime.cs:52-57`) is written for that case.
- **Evidence (probe program and tests, run as the normal user):**
  - A pipe a normal user creates with .NET defaults has the DACL `D:(A;;FR;;;WD)(A;;FR;;;AN)(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;<user>)`.
    **SYSTEM gets full control**, which includes `FILE_CREATE_PIPE_INSTANCE`.
  - Against a squatter pipe with unlimited instances, `DisplayServer.ForUsb(...).Start()` **succeeded**, with no exception. A client
    then saw the **squatter's** security descriptor on the server's own instance (`O:<squatter>D:(A;;0x1f019f;;;WD)(A;;FR;;;BG)`): the
    server's `ServicePipeSecurity` rules were ignored.
  - `SensorPipeServer` behaves the same way.
  - Only when the squatter set max instances to 1 did `Start()` throw (`IOException: All pipe instances are busy`).
  - Two tests written to the design's rule fail on the branch:
    - `Review_F2_the_display_server_does_not_start_on_a_squatted_name`: `Assert.ThrowsAny() Failure: No exception was thrown`.
    - `Review_F2_the_sensor_server_never_serves_under_a_squatters_rules`: `Assert.Contains() Failure: Sub-string not found` (no `NU` deny).
  - `PipeOptions.FirstPipeInstance` exists in .NET 8 (`PipeOptions` names: `None, FirstPipeInstance, CurrentUserOnly, Asynchronous, WriteThrough`).
- **Failure scenario:**
  1. The service is down for a few seconds: the upgrade (`net stop` ... `sc start`), the recovery restart 5 s after a crash, an
     administrator's Stop/Start, or the boot window before the sensor pipe exists (L10).
  2. A normal program in any interactive session creates `Glance.Sensors` and/or `Glance.Service.Display`.
  3. The service starts and silently joins those pipes. Its instances are served under the squatter's rules.
  4. Every client's owner check fails, correctly. The full sensors are gone, and the tray and editor fall back to USB directly.
  5. This lasts **until the service restarts, even after the squatter exits**, because the service's own instances keep the pipe
     object alive. Nothing is logged for the sensor pipe.
  6. The squatter also keeps `WRITE_DAC` on a pipe that SYSTEM serves. It could drop the NETWORK deny, and remote clients would then
     reach the SYSTEM display server (combine with L1).
  7. The owner check means clients never trust the squatter. That part of the design holds. So this is a denial of service, not
     an elevation.
- **Remedial action:**
  1. Create the **first** instance with `PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance`, in `DisplayServer.Start` and
     `SensorPipeServer.Start`. Later instances must not pass the flag.
  2. Right after creating any instance, read `server.GetAccessControl()` and check that the owner is this process's user (or
     Administrators) and the DACL is the one built by `ServicePipeSecurity`. If not, dispose it, log "pipe name taken by <owner>" and retry.
  3. Give `SensorPipeServer` a `Log` callback, so a squatted or failing sensor pipe shows up in `service.log`.
  4. Keep my two tests (above) as regression tests.
  5. Correct §4 of the design note.

### M2. Idle connections lock the console user out, and SYSTEM logs one line per second (proven)

- **Where:**
  - `DisplayServer.cs:22` (`MaxConnections = 8`).
  - `DisplayServer.cs:118-140` (`Serve`: no read timeout, no per-session limit; the session is only used for authorization).
  - `DisplayServer.cs:108-112` (`AcceptLoop` logs every failure, then waits 1 s).
  - `ServiceLog.cs:59-64` (no size limit).
  - The same pattern is in `SensorPipeServer` (16 instances; a client that keeps reading keeps its instance).
- **Evidence (probe program):**
  - Eight clients connect and send nothing. Over 5 s the server logged **5 lines** of `Display pipe error: All pipe instances are busy.`
  - A real client (`RemoteFrameDisplay.Connect`, 500 ms) then got `TimeoutException`. `DisplayConnector.Open` treats that as "no
    service" and opens USB directly.
- **Failure scenario:**
  1. Another account on the PC holds 8 connections. That can be a second user via fast user switching or RDP, or any program
     running as them: the pipe DACL admits all INTERACTIVE users, and the authorization check happens per request, not per connection.
  2. The console user's tray loses the service. It falls back to USB if the device is free; otherwise it has no display.
  3. SYSTEM appends about 100 bytes per second to `%ProgramData%\Glance\service.log` until the service restarts: about 8.6 MB a day,
     and the log is never rotated.
  4. A console-session client can also grow the log by alternating Frame and Blank ("Display opened" / "turned the display off").
- **Remedial action:**
  1. Add a read timeout to `Serve`. For example, drop a connection that sends no complete request within 30 s; a status-only
     connection is answered and closed at once. Also limit the time to read one request (5 s for the 300 KiB payload).
  2. Allow at most 2 connections per client session (count them in `Serve` from `ClientSession`), and keep 1 instance for the console session.
  3. Rate-limit repeated log lines in `AcceptLoop` (log the first, then "repeated N times"). Cap `service.log` (for example 1 MiB,
     then roll to `service.old.log`).
  4. Test it: 8 idle clients, then a console client must still connect within a short time, and the log must not grow per second.

### M3. A leftover 0.1 "Glance Runner" task starts the new tray elevated with the user's environment (confirmed)

- **Where:**
  - `installer/Glance.iss:146`: deletes the task, but the exit code is only logged and the deletion is never checked.
  - `src/Glance.Runner/Program.cs:5-12`: no check of its own elevation. The H1 warning (`RunnerEnvironment.Warning`) was removed
    from `Main`, and `RunnerEnvironment` is now unused.
  - `src/Glance.App/Views/ServiceWindow.cs` (`CheckOldTask`): only offers removal when the editor's Background service window is opened.
- **Evidence:**
  - The 0.1 task runs `<folder>\Glance.Runner.exe` with `RunLevel=HighestAvailable` and the interactive token. The task's run level,
    not the exe's manifest, decides elevation.
  - After this change the same file name is the `asInvoker` tray. A leftover task therefore starts the new tray **elevated with the
    user's environment** at every sign-in. That is H1 again.
  - Worse, the tray now parses the user's theme, images, fonts and video (FFmpeg) while elevated. The old runner did not do that
    with user files.
- **When it happens:**
  - The installer's `schtasks /Delete` failed (it is ignored).
  - Or the task belongs to a 0.1 copy in another admin-only folder that is later overwritten with the 0.2 zip.
  - Rare, but silent and exactly the class of bug this change exists to remove.
- **Remedial action:**
  1. In the tray's `Main`, read `TokenElevationType`. If it is `Full` (elevated), log `RunnerEnvironment.Warning(...)`, show a balloon
     ("Glance was started with administrator rights by the old sign-in task; remove it in Background service"), and exit without
     rendering. Do the same in the editor, or relaunch de-elevated through `explorer.exe`.
  2. Let the **service** delete the 0.1 task at start (SYSTEM may, through the Task Scheduler COM API or `{sys}\schtasks.exe /Delete /TN
     "Glance Runner" /F`). Log it. Then a leftover task does not survive the first boot.
  3. In the installer, run `schtasks /Query /TN "Glance Runner"` after the delete and show a message box if it is still there.
  4. Add a test that `Glance.Runner` refuses to run elevated (pure decision in Core, like `AutostartTask.UserCanAutostart`).

### M4. SYSTEM loads DLLs with the default search order (confirmed; not exploitable on this PC)

- **Where:**
  - `src/Glance.Core/Usb/Libusb0.cs:8-10`: `[DllImport("libusb0.dll")]` with no `DefaultDllImportSearchPaths`, and no assembly-level attribute.
  - LibreHardwareMonitorLib 0.9.6 imports `LoadLibraryW` and loads vendor DLLs by name (for example `atiadlxx.dll`, `nvapi64.dll`).
  - `src/Glance.Service/Program.cs:16` never calls `SetDefaultDllDirectories`.
- **Evidence:**
  - Without a restriction, .NET probes the app folder and then the Windows default order. That order ends with the current
    directory and the **machine `PATH`** (the SYSTEM process's PATH).
  - `libusb0.dll` is loaded on the first `FRAME` from the console session, so any console-session program can trigger the load on demand.
  - On a PC without the libusb-win32 driver, or for a GPU vendor whose DLL is absent, the search reaches `PATH`.
  - A machine `PATH` folder that users can write, or one that is listed but missing (users can then create it, for example under
    `C:\`), gives code execution as SYSTEM.
  - I checked this PC's machine `PATH`: all 10 folders exist and none is user-writable, so **not exploitable here**.
- **History:** the 0.1.1 runner had the same search order, but only while elevated and only after the user started it. Now it is
  SYSTEM, at every boot, and any console-session process can trigger it.
- **Remedial action:**
  1. First line of `Glance.Service` `Main`: `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_APPLICATION_DIR)`
     (kernel32, `DefaultDllImportSearchPaths(System32)`). It removes `PATH` and the current directory for every later
     `LoadLibrary`, including LibreHardwareMonitor's.
  2. Mark `Libusb0` with `[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`; libusb-win32 installs `libusb0.dll` into System32.
  3. Add a layout test that `Glance.Service/Program.cs` calls `SetDefaultDllDirectories`.

### L1. Network-path clients reach the pipe and get a session id (proven)

- **Where:**
  - `NativeSessions.cs:10-15` and `DisplayAccess.cs:31`.
  - Design note §4: "A client whose session cannot be determined (for example a remote one that got past the ACL) is refused".
  - `ServicePipeSecurity.CurrentUserOnly()` (`ServicePipeSecurity.cs:43-48`) has no NETWORK deny.
- **Evidence (probe and test):**
  - A client that connects through `\\localhost\pipe\<name>` goes through the SMB redirector. It **connected** to a pipe with the
    tray-control rules, and also to one with the service rules.
  - So .NET does not set `PIPE_REJECT_REMOTE_CLIENTS`. The loopback SMB token is the interactive one, which is why the NETWORK deny did
    not apply.
  - `GetNamedPipeClientSessionId` returned **9** in one run and **13** in another. This process was in session 1, and those sessions
    do not exist.
  - Test `Review_F5_a_client_that_comes_over_the_network_path_has_no_session`: `Assert.Null() Failure ... Actual: 13`.
- **Impact today:**
  - The NETWORK deny ACE does stop genuine remote logons, so the service pipes are safe as long as their DACL is intact.
  - But the "second line" the design relies on does not exist: a network client gets an arbitrary session number. If that number
    equals the console session, the client counts as the person at the screen.
  - With M1 (a squatter can rewrite the DACL of the joined pipe) the two combine.
  - The tray's control pipe has no NETWORK deny at all. The same user, from another PC with their password, can send `LIVE` and the
    rest. That is low impact, since they already have the password.
- **Remedial action:**
  1. Create the service pipes with `PIPE_REJECT_REMOTE_CLIENTS`. Call `CreateNamedPipeW` through P/Invoke (with `FILE_FLAG_FIRST_PIPE_INSTANCE`
     for the first instance, see M1) and wrap the handle with `new NamedPipeServerStream(PipeDirection.InOut, isAsync: true,
     isConnected: false, safePipeHandle)`.
  2. Add the NETWORK deny to `CurrentUserOnly()`.
  3. Correct the design note: Windows does not return "unknown" for remote clients.

### L2. A suspend that finds the device busy leaves the blank to nobody (confirmed)

- **Where:** `DisplayServer.cs:241`: `Monitor.TryEnter(_gate, PowerBlank.OwnerBudgetFor(e) / 3)`. For a suspend that is (1.5 s - 0.3 s) / 3 =
  **400 ms**, about the length of one frame transfer.
- **What happens:**
  1. If the lock is busy, `_blankOwed` is set and the handler returns `Busy`.
  2. The Frame that holds the lock then returns normally (`DisplayServer.cs:192-197`) without looking at `_blankOwed`.
  3. The owed blank is paid only by the next request or the 2 s `Tick`, and Windows suspends long before that tick.
- **Why it is only low:** the user side (tray `SessionPowerWatcher` → `BLANK`) is a second line, and `Blank` waits for the lock without a
  limit. The display is still blanked, just not by the service. The existing test (`A_power_event_that_finds_the_device_busy_is_paid_by_the_next_tick`)
  proves payment by `Tick`, which never runs before sleep.
- **Remedial action:**
  1. At the end of the Frame case, still under the lock: `if (!Power.MayDrive) BlankAndCloseLocked();` (and clear `_blankOwed`).
     The transfer that held the device then blanks it right after, inside the budget.
  2. Add a test in which the blank happens without any `Tick` or later request.

### L3. The connector treats "busy" or "starting" as "no service" (confirmed)

- **Where:** `DisplayConnector.cs:21-26`. A `TimeoutException` from `NamedPipeClientStream.Connect(500)` means "no pipe", but also
  "all instances busy" (M2) and "the service is still starting at sign-in".
- **What happens:**
  1. The tray then opens USB directly as the user.
  2. It keeps that device until it fails, so it never moves to the service when the service appears.
  3. Shutdown blanking then relies only on the user side, the service's 20 s release no longer applies, and the service cannot open
     the device.
- **Remedial action:**
  1. When `ServiceControl.Query()` says the service is installed (Running or Starting), do not fall back to USB; throw a "service not
     ready" error, and `Supervise` retries.
  2. Fall back only for `NotInstalled` or `Stopped`.
  3. Optionally, a pusher that is on USB re-checks for the service every minute.

### L4. Installer robustness: results ignored, and `taskkill` triggers the recovery restart (confirmed)

- **Where:** `installer/Glance.iss:142-153` (`PrepareToInstall`) and `:157-175` (`InstallService`). `RunHidden` only logs exit codes.
- **What can go wrong:**
  - **Recovery restart mid-upgrade:** if `net stop GlanceService` times out, `taskkill /IM Glance.Service.exe /F` (`:151`) kills
    it. The SCM sees a crash and its recovery action (`restart/5000`) starts the old binary again 5 s later, while files are being
    replaced.
  - **Service marked for deletion:** if `sc create` fails (for example `ERROR_SERVICE_MARKED_FOR_DELETE` after an uninstall without
    a reboot), `Sc('query')` does not return 1060. The script then runs `config`, which also fails, and the user gets a "successful"
    install without a service.
  - **No error is ever shown** to the user.
- **Remedial action:**
  1. Before stopping, run `sc failure GlanceService reset= 0 actions= ""` (or `sc config GlanceService start= disabled`). `InstallService`
     restores both anyway.
  2. Check the `net stop` result, and wait on `sc query` for STOPPED before `taskkill`.
  3. In `InstallService`, check every `sc.exe` exit code. If `create`/`config`/`sdset`/`start` fails, show a message with the code and
     log it (`SuppressibleMsgBox`), and return an error from `PrepareToInstall`/`CurStepChanged` where it matters.

### L5. `PreshutdownTimeout` written straight to the registry (plausible)

- **Where:** `installer/Glance.iss:172`.
- **What happens:**
  - The SCM keeps its service database in memory and reads the registry at boot. The documented way to change this value is
    `ChangeServiceConfig2W(SERVICE_CONFIG_PRESHUTDOWN_INFO)`.
  - A direct registry write is probably only picked up after the next restart, so the first shutdown uses the default (longer)
    timeout. That is harmless, since longer only gives more time.
  - Nothing verifies the value, and `sc.exe` has no query for it.
- **Remedial action:**
  1. Call `ChangeServiceConfig2W` from the installer's `[Code]` (Inno can call advapi32 like it already calls kernel32).
  2. Or have the service call it at start (SYSTEM has `SERVICE_CHANGE_CONFIG`) and log the effective value from
     `QueryServiceConfig2W`, so test 2 of the plan can check it.

### L6. The service log is opened before the process knows it is the service (confirmed)

- **Where:** `src/Glance.Service/Program.cs:18` calls `ServiceLog.OpenDefault()` in `Main`, before `NativeService.Run`. That creates
  `%ProgramData%\Glance` with the current process's identity as owner (`ServiceLog.cs:41-50`).
- **What happens:**
  1. A normal user runs `Glance.Service.exe` (it ships in the zip and in Program Files), or simply creates `%ProgramData%\Glance`
     before the service first starts.
  2. The folder is then owned by that user. `FolderTrust` rejects it, correctly, and the service never writes a log.
  3. That is fail-safe, but the only diagnostics the manual test plan depends on are gone. `STATUS` shows the reason, but only the
     editor's service window shows `STATUS`.
- **Remedial action:**
  1. Open the log inside `ServiceMain` (or only when `WindowsIdentity.GetCurrent().IsSystem`, or `--console` elevated).
  2. If the folder exists but is untrusted, log to the Windows event log (Application, source "Glance") instead, and say so in `STATUS`.

### L7. The service keeps every LocalSystem privilege (confirmed)

- **What:** `Glance.iss:161` registers the service as LocalSystem with no required-privileges list and no service SID.
- **Why it matters:** the service only needs the PawnIO device, SMBus/MSR via LibreHardwareMonitor, libusb and two pipes. If anything
  in it is ever exploited, `SeImpersonatePrivilege`, `SeDebugPrivilege`, `SeTcbPrivilege`, `SeTakeOwnershipPrivilege` and the rest
  make it trivial to take over the PC.
- **Remedial action (hardening):**
  1. `sc sidtype GlanceService unrestricted`.
  2. `sc privs GlanceService SeChangeNotifyPrivilege/SeCreateGlobalPrivilege` (add what LibreHardwareMonitor proves to need).
  3. Then verify sensors and USB in the manual test.

### L8. The tray's control pipe can be squatted by another user (confirmed)

- **Where:** `RunnerContext.cs:326-340`, `ServiceNames.TrayControlPipe` (`Glance.Runner.Control.<session id>`).
- **What happens:**
  - The pipe namespace is global and session ids are predictable, so user B can create `Glance.Runner.Control.1` before user A's tray starts.
  - A's tray cannot create an instance (B's DACL does not grant it), so `ControlLoop` logs "Control pipe error" **every second** into
    A's `runner.log`.
  - A's editor refuses B's pipe (`CurrentUserOnly`, good), so live editing silently stops working.
- **Remedial action:**
  1. Use `FirstPipeInstance` for the first instance.
  2. Include the user SID in the name (`Glance.Runner.Control.<session>.<sid>`).
  3. Rate-limit the log line.

### L9. Dev-build fallback can start a planted program for another user (confirmed, pre-existing)

- **Where:** `src/Glance.App/Services/AppPaths.cs:6-14` and `src/Glance.Runner/RunnerContext.cs:405-417`.
- **What happens:**
  1. If the exe is not next to the running program, these walk up to the drive root looking for `<Project>\bin`, and start the
     newest matching exe below it.
  2. Normal users may create folders in `C:\`, so user B can plant `C:\Glance.Runner\bin\x\Glance.Runner.exe`.
  3. User A's editor, from a copy without the tray next to it, then starts B's program as A.
- **History:** on `main` the same code started the found exe **elevated** (`Verb = "runas"`), so this change makes it less severe.
- **Remedial action:** search only when `AppContext.BaseDirectory` contains `\bin\`, and stop at the folder that contains `src\Glance.sln`.

### L10. The sensor pipe exists only after LibreHardwareMonitor has opened, and its errors are silent (confirmed)

- **Where:** `ServiceRuntime.cs:37-46`: `new LocalSensorSource()` (seconds, at boot) runs before `SensorPipeServer.Start()`.
  `SensorPipeServer.cs:44` swallows every exception.
- **Why it matters:** this widens the M1 window at every boot (a user's Run-key program at a fast sign-in) and hides a failing or
  squatted sensor pipe.
- **Remedial action:** create the sensor pipe first (publishing `[]` until ready), and give `SensorPipeServer` the service's log.

### Nits

- **N1 Design-note accuracy.**
  - §13 "the only warnings are SKPath" (actually 138).
  - §4 "the service fails to create its pipe and logs it" (M1) and "a remote one that got past the ACL is refused" (L1).
  - §4 "No impersonation" says a squatter "could then act as another signed-in user". A normal user has no `SeImpersonatePrivilege`,
    so impersonating *another* user gives only an identification-level token. The test proves the same-user case. The anonymous level
    is still right: it closes identification and protects against squatters running as service accounts. Say so.
- **N2 `--console` ships in release builds** (`Glance.Service/Program.cs:21-25`). Run from an elevated prompt, it is a Glance .NET
  process elevated with the user's environment, which is the H1 pattern. Developers only, but consider `#if DEBUG`, or refuse when
  `CORECLR_ENABLE_PROFILING`/`DOTNET_*` diagnostics variables are set.
- **N3 Dead 0.1 code** (`AutostartTask.BuildXml` and friends still build a `HighestAvailable` task; `RunnerEnvironment` is unused).
  The author lists this as decision 9. Remove it soon. Reuse `RunnerEnvironment` for M3.
- **N4 Files dropped from the package are not removed on upgrade** (Inno never deletes files it does not install). Check whether any
  0.1.1 package contains `Glance.SensorHost.*` (an exe with `requireAdministrator`). If so, add `[InstallDelete] Type: files; Name: "{app}\Glance.SensorHost.*"`.
- **N5 `STATUS` is answered for every interactive user** and includes the driving session id and the last USB error text. Harmless,
  but other users can see when someone is at the PC.
- **N6 `[UninstallDelete] {commonappdata}\Glance\service.log`** deletes a file of a fixed name through whatever `%ProgramData%\Glance`
  points at (the comment already avoids deleting a tree). Negligible; consider checking that the folder is not a reparse point first.

---

## Test gaps

| ID | Gap | Suggested test |
|---|---|---|
| T1 | `PipeOptions.CurrentUserOnly` on the editor's control client is untested (mutation R1 survives) | A pipe owned by another account cannot be created by a non-admin test. Move the client's options into a Core constant (`ControlPipeClient.Options`) and assert it contains `CurrentUserOnly`, or add a `ServiceLayoutTests` rule on the source. |
| T2 | Squatted name at server start (M1) | `Review_F2_*` from this review: a squatter with .NET defaults, then `DisplayServer.Start()` must throw and `SensorPipeServer` must not serve. |
| T3 | Idle-connection exhaustion and log growth (M2) | 8 idle clients, then a console client still connects in < 1 s, and the log callback count stays bounded. |
| T4 | A refused HELLO closes the connection (mutation R2 survives) | `Raw(Hello with version 99)` then assert `ClosedAfter`. |
| T5 | Suspend blank without a later tick (L2) | The busy-device test, but assert the device is blanked when the stuck `Blit` returns, with no `Tick()`. |
| T6 | The installer folder guard (H1) and the DLL search guard (M4) | Layout tests on `Glance.iss` and `Glance.Service/Program.cs`. |
| T7 | The tray refuses to run elevated (M3) | A pure decision in Core plus a test, as for `UserCanAutostart`. |
| T8 | `NativeService` | Not unit-testable. A small integration harness (`--console` plus calling `HandleControl` with fabricated codes through an internal hook) would cover the mapping-to-runtime wiring, including the `WTSSESSION_NOTIFICATION` offset. |

The existing tests are good. They use real pipes, random names, fake sessions and a fake clock, and they never touch the real pipe
names. The squatter tests really serve the pipe and call `RunAsClient`. The only weak spot is that the display-squatter impersonation
test trusts `Me`, so it tests the anonymous flag *after* the owner check; that is correct for what it claims.

---

## Assessment of the author's "Decisions for the user" (§12)

| # | Decision | Assessment |
|---|---|---|
| 1 | Portable elevated runner dropped | **Agree.** An opt-in elevated mode would keep H1 for the users least able to judge it. Portable users get basic sensors; the installer gives full ones. |
| 2 | Display dark before sign-in | **Agree.** If a boot theme is wanted later, render it in a separate low-rights process (a virtual service account or LocalService with a restricted token, or an AppContainer) that streams frames to the service, never in SYSTEM. |
| 3 | Interactive users may start but not stop the service | **Agree.** Start is harmless; stop would let any signed-in user (including an RDP user) switch off the console user's display and sensors. Note that `IU` includes RDP sessions; that is fine for "start". |
| 4 | Tray starts at sign-in for every user (common Startup), on by default | **Agree with a note.** Every account on the PC gets a tray that connects to the service. That is harmless (non-console sessions are refused), but it wastes resources on shared PCs. Say "for every user" in the installer task text (it does) and mention it in the README. |
| 5 | The user side renders | **Strongly agree.** This is the property that makes the service safe; keep the AGENTS.md rule that SYSTEM never parses user files. |
| 6 | Console session wins | **Agree.** It matches "who is looking at the screen". |
| 7 | Version 0.2.0 | **Agree.** |
| 8 | FFmpeg per user | **Agree.** FFmpeg now only runs as the user, so a per-user install is the least-privilege choice. |
| 9 | 0.1 helpers not removed yet | **Agree for this change, but do it next.** The dead code still builds a `HighestAvailable` task definition. Reuse `RunnerEnvironment` for the M3 check before deleting it. |

**Decisions I would add for the user:**

- (a) Lock the install folder to Program Files (H1): recommended; the alternative is a folder check that refuses unsafe folders.
- (b) Trim the service's privileges (L7), which needs one manual sensor check.
- (c) Whether the service, as SYSTEM, should delete a leftover 0.1 task at start (M3).

---

## Manual test plan (checked and extended)

The author's plan (§14) is good and specific. I checked each step against the code, and the expected log lines match the code
(`"Glance service ... starting as"`, `"Display pipe ready"`, `"Sensors ready (n)"`, `"Windows Suspend: BlankAndHold"`,
`"Glance service stopping (SessionEnding)"`, `"Session N left the console; display off"`). Corrections and additions are in **bold**.

Use a PC with the display, libusb-win32 and PawnIO. Start with Glance 0.1.1 installed and its "start at sign-in" option on. Keep
a second, **standard (non-admin)** account available.

1. **Build:** `.\build\publish.ps1`, `.\build\make-installer.ps1` → `dist\Glance-Setup-0.2.0.exe`.
2. **Upgrade from 0.1.1** (author's step 2, unchanged), plus:
   - **`icacls "C:\Program Files\Glance"`**: only SYSTEM, Administrators and TrustedInstaller may write; Users have read and execute only.
   - **`sc qprivs GlanceService` and `sc qsidtype GlanceService`** (after L7).
   - **`Get-Process Glance.Service -IncludeUserName`** (elevated PowerShell): user `NT AUTHORITY\SYSTEM`, `SessionId` 0.
   - **Task Manager > Details: `Glance.Runner.exe` and `Glance.App.exe` have "Elevated" = No.**
   - **`schtasks /Query /TN "Glance Runner"`**: "cannot find".
3. **H1 end-to-end (new, the proof the change exists for).** As the normal user:
   - Run `setx CORECLR_ENABLE_PROFILING 1`, `setx CORECLR_PROFILER {11111111-1111-1111-1111-111111111111}` and
     `setx CORECLR_PROFILER_PATH C:\does-not-exist\p.dll`. Sign out and back in.
   - Expect: the tray and the editor (user processes) log ".NET Runtime" "profiler failed to load" events in Event Viewer > Application.
     That is harmless and proves the variables reach user processes.
   - Expect: **no** such event for `Glance.Service.exe` after `sc stop`/`sc start` (from an elevated prompt) or a reboot.
   - Process Explorer > `Glance.Service.exe` > Environment shows no `CORECLR_` variables.
   - Afterwards remove all three with `reg delete HKCU\Environment /v <name> /f`.
4. **Install-folder guard (new, after H1 is fixed):** run the setup on a test PC and try `C:\Glance` on the directory page. Expect:
   refused, or the page is not shown. On a 0.1.1 install in a custom folder, expect the upgrade to refuse or move to Program Files.
5. **Fresh install, before sign-in, sensors without the editor, editor, Background service window, AIDA64, sleep, shutdown/restart
   (with and without Fast Startup), sign-out and fast user switching, uninstall:** the author's steps 3-11 and 13, unchanged.
   **In step 9 (sleep), repeat it 5 times while a video theme is playing**, so the suspend arrives during a transfer (L2). The display
   must be black every time before the PC sleeps.
6. **Standard-user rights (new):** as the standard account:
   - `sc stop GlanceService` → "Access is denied".
   - `sc start GlanceService` after an admin stopped it → starts.
   - `sc config GlanceService binPath= x` → "Access is denied".
   - `sc sdshow GlanceService` → the SDDL from `ServiceNames.ServiceSddl`.
7. **Squatting (author's step 12, made concrete):**
   - As an admin, run `sc stop GlanceService`.
   - As the standard user, in PowerShell:
     `$p = New-Object System.IO.Pipes.NamedPipeServerStream('Glance.Sensors','InOut',254); $q = New-Object System.IO.Pipes.NamedPipeServerStream('Glance.Service.Display','InOut',254)`
   - As the admin, run `sc start GlanceService`.
   - **Today (M1):** the service joins both pipes. The tray falls back to USB, the sensors stay basic **even after you close that
     PowerShell**, until the service restarts. `service.log` says nothing about the sensor pipe.
   - **After the fix:** `service.log` says both names are taken, and the service serves normally within ~10 s of closing the PowerShell.
8. **Connection exhaustion (new, M2):** while the console user's tray shows the theme, sign in the standard user with fast user
   switching. Run a script that opens 8 `NamedPipeClientStream('.', 'Glance.Service.Display')` connections and keeps them. Switch back.
   - **Today:** the tray reconnects to USB or shows an error, and `service.log` grows by one line per second.
   - **After the fix:** the tray keeps working and the log stays quiet.
9. **Leftover task (new, M3):** as an admin, re-create a 0.1-style task with `RunLevel` Highest pointing at
   `C:\Program Files\Glance\Glance.Runner.exe`, and sign in again.
   - **Today:** the tray runs elevated (Task Manager "Elevated" = Yes).
   - **After the fix:** the tray exits with a message, or the service has already deleted the task.
10. **Pre-shutdown value (new, L5):** after the fix, `service.log` reports the effective pre-shutdown timeout (5000 ms) right after install, without a reboot.

---

## What was checked and found sound

- **Privilege split and data flow.** The service never reads a theme, image, font, video or user path, and never starts a process
  (checked: no `Process.Start` in any type the service uses; no file, directory or registry reads in `Core/Sensors`). The `LIVE`
  folder goes only to the same user's tray. FFmpeg runs only in the tray and the editor, as the user, so no `CreateProcessAsUser`
  from SYSTEM is needed. That is the right call, and it removes token, environment-block and handle-inheritance risk entirely.
- **Wire protocol** (`DisplayWire`). Exact per-op lengths are checked before allocating. Short reads are detected, the reply length
  is capped at 1 KiB, a UTF-8 cut is handled, unknown ops and statuses are rejected, and a malformed request closes the connection.
- **Authorization** (`DisplayAccess`). It is checked per request against the *current* console session, so a fast user switch takes
  effect at once. Session 0 and an unknown session are refused. A non-console user cannot blank, dim or draw (tested).
- **Pipe DACL** (`ServicePipeSecurity`). NETWORK is denied. Clients get read/write plus synchronize only: no `CreateNewInstance`,
  `WRITE_DAC` or `WRITE_OWNER`. There is no Everyone, Users or Anonymous entry. There is no explicit mandatory label, so the default
  medium label keeps Low-integrity and AppContainer processes out. `ReadPermissions` is included in `ReadWrite`, so clients can read
  the owner.
- **Client-side owner check** (`PipeServerCheck`). SYSTEM or Administrators only. It fails closed when the owner cannot be read, and
  it runs before any byte is sent. A non-elevated process cannot set either owner.
- **Anonymous impersonation level** on every Glance client (display, sensors, tray control), and in the installer's legacy `EXIT`.
- **Service DACL** (`ServiceNames.ServiceSddl`). It is the Windows default plus `RP` for `IU`; `CheckServiceSddl` is tested, and the
  installer text is tied to the constant by `ServiceLayoutTests`. The quoted `binPath` means there is no unquoted-path issue. Recovery
  uses restart actions only (no "run program" action).
- **Service entry** (`NativeService`). Delegates are kept in static fields. `ControlsAccepted` is 0 while pending. A second stop is
  ignored. The `WTSSESSION_NOTIFICATION.dwSessionId` offset 4 is right. Power events are handled synchronously and with a bound; stop
  and pre-shutdown answer the SCM at once and blank on a worker. `SERVICE_ACCEPT_SHUTDOWN` is correctly not set when pre-shutdown
  is accepted.
- **Power and session mapping** (`ServiceControlMap`). It is pure and tested. Logoff and console-disconnect blank only the driving
  session. The two lines (service and user side) mean that either side alone blanks the display.
- **Service log folder.** It is created with a protected admin-only DACL, checked with `FolderTrust`, and has a tested fallback (no
  log) for a planted folder. The uninstaller deletes only the file and an empty folder.
- **Installer.** It starts only Windows tools while elevated, with `{sys}` full paths and `cmd /d`. The tray and editor are started
  with `runasoriginaluser`. The Startup shortcut lives in the admin-only common Startup folder. The old task is deleted before files
  are replaced. Uninstall stops and deletes the service before files are removed.
- **Editor.** "Stop service" runs `{sys}\sc.exe` through UAC; "Start" uses the SCM directly with the granted right. The per-session
  `RunnerClient.IsRunning` and per-session pipe names keep two signed-in users apart.
- **Docs.** `AGENTS.md`, `README.md` and `TUTORIAL.md` describe the new split, the console-session rule, the 20 s release for AIDA64
  and the log locations accurately, except for the nits in N1.

---

## Fixes after review

Written by the session that made the change, after the review. The findings above are unchanged.

### Method

The user's rule was followed for every finding:
1. **Prove the bug.** The tests were written first, in `src/Glance.Tests/ReviewFixTests.cs` and `src/Glance.Tests/DisplayServiceReviewTests.cs`
   (a partial of `DisplayServiceTests`), one or more per finding, named after it. Where a test needed an entry point that did not exist
   yet (`ServiceStartGuard`, `TrayStart`, `LogLimiter`, `ServicePipeFactory`, `DevPaths`, `LegacyTask`, the connector's service-state
   argument, `ServiceControl.QueryPreshutdownTimeout`, `ServiceLog.ShouldOpen`), a stub with **today's behaviour** was added first. For
   example, the guard always allowed, the factory created pipes the old way, the limiter let every line through, and `DevPaths` was the
   old search. The installer cannot be run here (it would install a service), so for the installer the tests read `installer/Glance.iss`:
   they are the probe that the script does the right thing.
2. **Fix.** The stubs were replaced by the real rules, and the service, tray, editor and installer were changed.
3. **Prove the fix.** The same tests pass and the whole suite is green.

**Result:**
- 44 new tests. **Before: 34 failed, 10 passed.** The 10 that passed are guards that must hold either way:
  - the normal cases of the tray's elevation rule (3), of the log rule (2) and of the connector without a service (2);
  - the three mutation gaps the review found (T1 `CurrentUserOnly`, T4 a refused HELLO closes, R3 brightness clamped), which were
    correct but untested.
- **After: 44 / 44**, and the full suite **668 / 668** at commit `3a35e6e`.
- Removing the dead 0.1 helpers (N3, commit `4e2f119`) removed 61 tests that only covered that code: **607 / 607** after it. The mutation check below added one test and one test case: **608 / 608** at the end.
- Build: 0 errors.

Two tests were corrected between the runs. In both, the fix changed *how* the right result shows, not *whether* it shows:
- `L1_a_client_over_the_network_path_is_refused`: after the fix the server closes a network client before it can write its HELLO,
  so the write itself fails ("Pipe is broken"). That now counts as the refusal it is.
- `N2_the_console_mode_is_only_in_debug_builds` looked for the first `--console`. After the fix that is in the doc comment above
  `#if DEBUG`, so the test now looks for the code (`args.Contains("--console")`). Before the fix there was no `#if DEBUG` at all.

### H1. The service binary could be installed into a user-writable folder

| Test | Before | After |
|---|---|---|
| `H1_the_installer_always_installs_into_Program_Files_and_resets_the_folder_rules` | `Assert.Contains() Failure: Sub-string not found` (`DisableDirPage=yes`) | passes |
| `H1_the_service_refuses_to_run_from_a_folder_users_can_change` (a real folder in the user's temp, real `FolderTrust`) | `Assert.NotNull() Failure: Value is null` | passes |
| `H1_the_service_runs_from_a_trusted_folder_and_refuses_an_unknown_path` | `Assert.NotNull() Failure: Value is null` | passes |
| `H1_the_service_checks_its_folder_before_it_does_anything_else` | `Assert.Contains() Failure: Sub-string not found` | passes |

- **Installer folder:** `Glance.iss` sets `DisableDirPage=yes` and `UsePreviousAppDir=no`, so the service always lives in `{autopf}\Glance`.
- **Folder lock:** after the files are copied, `LockInstallFolder` runs `icacls "{app}" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F
  *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX *S-1-15-2-1:(OI)(CI)RX` and then `/setowner *S-1-5-32-544`.
  - It checks the exit codes.
  - If either fails, it registers **no** service and shows a message.
- **Self-check:** the service runs `ServiceStartGuard.Refusal(Environment.ProcessPath, FolderTrust.CheckExecutable(exe, TrustScope.FolderTree))`
  in `ServiceMain`, before anything else. If the folder is not admin-only:
  - it logs why, in the service log and in the Application event log (source "Glance");
  - it reports `SERVICE_STOPPED` with `ERROR_ACCESS_DENIED`.

### M1. The service joined a squatter's pipe

| Test | Before | After |
|---|---|---|
| `M1_the_display_server_does_not_start_on_a_squatted_name` | `Assert.ThrowsAny() Failure: No exception was thrown` | passes |
| `M1_the_sensor_server_reports_a_squatted_name_and_serves_once_it_is_gone` | `the squatted sensor pipe was not reported` | passes |
| `M1_L8_a_pipe_name_someone_else_serves_is_refused_by_every_Glance_server` | `Assert.IsType() Failure: Value is null` (no exception) | passes |

- **New `ServicePipeFactory.Create(..., first)`:**
  - The first instance a server creates uses `PipeOptions.FirstPipeInstance`. A taken name throws `PipeNameTakenException`.
  - Every instance, first or later, must be owned by this account (or Administrators). A foreign one is disposed and refused.
- **Used by** `DisplayServer`, `SensorPipeServer` and the tray's control pipe. Each counts its live instances and claims the name
  again with the flag when it has none left.
- **`SensorPipeServer` logs now:** it takes a log callback, and the service passes its log.
  - It writes "Sensor pipe error: The pipe name ... is taken by another program" (rate-limited).
  - It retries every 2 s, so it serves as soon as the squatter is gone.
- These are the reviewer's two tests in substance, kept as permanent regression tests.

### M2. Idle connections and log growth

| Test | Before | After |
|---|---|---|
| `M2_idle_connections_from_another_session_do_not_lock_out_the_console_user` (8 idle clients from another session) | `System.TimeoutException : The operation has timed out.` | passes (connects and draws within 2.5 s) |
| `M2_a_connection_that_sends_nothing_is_closed` | `Assert.Equal() Failure: Values differ, Expected: 0` (still open after 3 s) | passes |
| `M2_the_display_server_does_not_log_every_busy_second` | `Assert.Equal() Failure: Values differ, Expected: 1` (100 lines written) | passes |
| `M2_a_repeated_line_is_written_once_per_window_with_a_count` | `Assert.Null() Failure: Value is not null` | passes |
| `M2_the_service_log_rolls_over_at_its_size_limit` | `the log grew past its limit` | passes |

- **`DisplayServer`:**
  - at most `PerSessionLimit` (4) connections per Windows session;
  - a connection that sends no complete request within `IdleTimeout` is closed (30 s; clients send a frame at least every 10 s);
  - 16 instances.
- **`LogLimiter`:** a repeated line is written once a minute, then "(repeated N more times before this)". The display server, the sensor
  server and the tray's control loop use it.
- **`ServiceLog`** rolls over to `service.old.log` at 1 MiB.
- **The client side** retries instead of switching to USB (see L3).

### M3. A leftover 0.1 task starts the tray elevated

| Test | Before | After |
|---|---|---|
| `M3_the_tray_does_not_run_elevated_when_it_could_run_as_the_user` (5 cases) | 2 failed: `Expected: RelaunchUnelevated` / `Expected: Refuse`, actual `Run` | 5 / 5 |
| `M3_the_tray_checks_its_token_before_anything_else` | `Assert.Contains() Failure: Sub-string not found` | passes |
| `M3_the_service_deletes_the_old_task_without_a_prompt` | `Assert.False() Failure` (it used the UAC start info) | passes |
| `M3_the_installer_checks_that_the_old_task_is_gone` | `Assert.Contains() Failure: Sub-string not found` | passes |

- **`TrayStart.Decide`** (Core, pure):
  - the elevated half of a split token (`TokenElevationType` Full, and elevated): relaunch with normal rights;
  - still elevated after the relaunch: refuse;
  - UAC off (type Default): run, because there is no boundary to protect.
- **Tray:** `Glance.Runner` `Main` checks this before anything else.
  - It logs `RunnerEnvironment.Warning(...)` (reused, as suggested).
  - It starts itself again through `explorer.exe`. A named marker tells the new copy it was relaunched.
- **Service:** it deletes the task at every start, as SYSTEM, with `{sys}\schtasks.exe` (`LegacyTask`), only if the task exists, and logs it.
- **Installer:** it runs `schtasks /Query` after the delete and shows a message if the task is still there.

### M4. DLL search order

| Test | Before | After |
|---|---|---|
| `M4_the_service_limits_the_dll_search_before_anything_loads` | `Assert.Contains() Failure: Sub-string not found` | passes |
| `M4_libusb_is_only_loaded_from_System32` | `usb_init may load libusb0.dll from outside System32` | passes |

- **Service:** the first statement of `Glance.Service` `Main` is `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_APPLICATION_DIR)`.
- **Core:** `Glance.Core` has `[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]`.
  - The attribute cannot be put on a class.
  - Every native DLL that Core imports by name is a System32 DLL.
- **`libusb0.dll` on this PC** is in `C:\Windows\System32` (76 384 bytes; an x86 copy is in SysWOW64). The libusb-win32 driver
  installs it there, so the editor and the tray still find it.
- **LibreHardwareMonitor** (0.9.6, x64):
  - Its strings show `\GLOBALROOT\Device\PawnIO`: the driver is opened as a device, not through a DLL.
  - The vendor DLLs it names are `nvapi64.dll`, `nvml.dll`, `atiadlxx.dll`, `ControlLib.dll` and `Ftd2xx.dll`. Current NVIDIA, AMD and
    Intel drivers put these in System32.
  - **Must be checked by hand:** the GPU sensors in the service (manual test 5).

### L1. Network-path clients

| Test | Before | After |
|---|---|---|
| `L1_a_client_over_the_network_path_is_refused` (`\\localhost\pipe\...` through SMB, fake session = console) | `Assert.Equal() Failure: Values differ, Expected: Denied` (HELLO was allowed) | passes (closed before it can write) |
| `L1_the_tray_control_pipe_denies_the_network` | `Assert.Contains() Failure: Filter not matched in collection` | passes |

- **Redirector check:** `NativeSessions.IsRemoteClient` uses `GetNamedPipeClientComputerNameW`, which only succeeds for clients that
  came through the redirector. The display server and the tray's control pipe close such a connection at once.
- **NETWORK deny:** `ServicePipeSecurity.CurrentUserOnly()` (the tray pipe, and the sensor server's default) denies NETWORK.
- **Design note:** its "unknown session is refused" claim is corrected (§4).
- **Why not `PIPE_REJECT_REMOTE_CLIENTS`:** I kept the redirector check instead of creating the pipe through `CreateNamedPipeW` with that
  flag. The check is proven by the test, and it keeps .NET's pipe creation, which the first-instance fix depends on.

### L2. Suspend during a transfer

| Test | Before | After |
|---|---|---|
| `L2_a_suspend_during_a_transfer_blanks_as_soon_as_the_transfer_ends_without_a_tick` | `Assert.Equal() Failure: Values differ, Expected: 1` (no blank) | passes |

The frame request that held the device now checks `Power.MayDrive` right after its transfer, still under the lock. If Windows started to
sleep or shut down during the transfer, the request blanks and closes the device then, inside the budget, and answers "off while
Windows sleeps".

### L3. Busy or starting service treated as "no service"

| Test | Before | After |
|---|---|---|
| `L3_an_installed_service_that_does_not_answer_yet_is_waited_for_not_bypassed` | `Assert.ThrowsAny() Failure: No exception was thrown` (USB was opened) | passes |
| `L3_without_a_running_service_the_connector_opens_usb` (Stopped, NotInstalled) | passed (guard) | passes |

- **Fallback rule:** `DisplayConnector.Open` falls back to USB only when `ServiceControl.Query()` says the service is not installed or stopped.
  - If the service is running or starting, `Open` throws "running but did not answer yet; trying again", and the tray's `Supervise` retries.
  - `DisplayConnector.TryBlank` follows the same rule.
- **Switching back:** `DisplayPusher.ViaService` is new. When the service answers and the tray's pusher is on USB, the tray hands the
  display to the service (`MoveToServiceIfOnUsb`).

### L4. Installer robustness

| Test | Before | After |
|---|---|---|
| `L4_the_installer_turns_off_the_restart_before_stopping_and_checks_results` | `the recovery restart must be off before the service is stopped` | passes |

- **Stopping (`StopOldService`):** first `sc failure GlanceService reset= 0 actions= ""`, then `net stop`, then `WaitForServiceStopped(20)`,
  and only then `taskkill`. So no recovery restart follows. The uninstaller turns the restart off first too.
- **Checked steps (`InstallService`):** it checks the exit codes of `icacls`, `create`/`config`, `sdset`, `sidtype`, `privs`, `failure`
  and `start`.
  - Each failure is logged.
  - Each failure shows `ServiceStepFailed`: a `SuppressibleMsgBox` with the step and the code.

### L5. Pre-shutdown timeout

| Test | Before | After |
|---|---|---|
| `L5_the_preshutdown_timeout_is_read_through_the_service_api` (reads `EventLog`'s value as a normal user) | `Assert.NotNull() Failure: Value of type 'Nullable<int>' does not have a value` | passes |

- The service calls `ChangeServiceConfig2W(SERVICE_CONFIG_PRESHUTDOWN_INFO)` at start. SYSTEM holds `SERVICE_CHANGE_CONFIG` through
  the BA entry.
- It then logs the effective value read with `QueryServiceConfig2W` ("Pre-shutdown timeout: 5000 ms").
- The installer no longer writes the registry value.

### L6. Log opened too early

| Test | Before | After |
|---|---|---|
| `L6_only_the_service_or_an_elevated_console_opens_the_log` (3 cases) | `(false, false)` failed: `Expected: False` | 3 / 3 |

- **When:** the log is opened inside `ServiceMain`, and only when the process is SYSTEM (or, in a debug build, an elevated console).
- **If the log folder is not admin-only:** the reason goes to the Application event log (source "Glance") and into `STATUS`.

### L7. Privileges

| Test | Before | After |
|---|---|---|
| `L7_the_service_keeps_only_the_privileges_it_needs` | `Assert.Contains() Failure: Item not found in collection, Collection: []` | passes |

- **The change:** `sc sidtype GlanceService unrestricted` and `sc privs GlanceService SeChangeNotifyPrivilege/SeCreateGlobalPrivilege`
  (`ServiceNames.RequiredPrivileges`). The test also checks that no takeover privilege is in the list.
- **Why it should work, in principle:**
  - USB through libusb needs no privilege; the device ACL admits SYSTEM.
  - LibreHardwareMonitor talks to PawnIO through its device path and names no privilege in its code.
  - The pipes need no privilege. `SeCreateGlobalPrivilege` is kept for global objects.
- **The user must verify on the real PC** (manual test 5):
  - CPU temperatures and clocks (PawnIO);
  - motherboard fans and voltages (SMBus/LPC through PawnIO);
  - GPU sensors (NVAPI, NVML, ADLX);
  - drive sensors;
  - the display.
- **If sensors are missing:** run `sc qprivs GlanceService` and check `service.log`. `SeLoadDriverPrivilege` is the first candidate to
  add, in `ServiceNames.RequiredPrivileges` and in the installer.

### L8. Tray control pipe squatting

| Test | Before | After |
|---|---|---|
| `M1_L8_a_pipe_name_someone_else_serves_is_refused_by_every_Glance_server` | see M1 | passes |
| `L8_the_tray_pipe_name_carries_the_session_and_the_user` | `Assert.Contains() Failure: Sub-string not found, String: "Glance.Runner.Control.3"` | passes |

- The name is now `Glance.Runner.Control.<session>.<user SID>`.
- The tray claims it with the first-instance flag and checks the owner.
- "Control pipe error" lines are rate-limited.

### L9. Dev-build fallback

| Test | Before | After |
|---|---|---|
| `L9_an_installed_copy_never_searches_other_folders` (planted `C:\Glance.Runner\bin\x\Glance.Runner.exe`) | `Assert.Null() Failure: Value is not null` | passes |
| `L9_a_development_build_only_searches_inside_its_repository` | `Assert.Null() Failure: Value is not null` | passes |

`DevPaths.FindSibling` (Core, pure) searches only when the program runs from a `\bin\` folder, and never above the folder that holds
`Glance.sln`. The editor (`AppPaths`) and the tray (`FindEditor`) use it.

### L10. Sensor pipe at once

- The test `M1_the_sensor_server_reports_a_squatted_name_and_serves_once_it_is_gone` covers it: it proves that sensor pipe errors are
  now logged.
- `ServiceRuntime.Start` creates the sensor pipe (serving `[]`) right after the display pipe, before LibreHardwareMonitor opens.

### Nits

| Finding | Test | Before | After |
|---|---|---|---|
| N1 design-note claims | (documentation) | | corrected in `docs/design/runner-service.md` §4 and §13 |
| N2 `--console` in release builds | `N2_the_console_mode_is_only_in_debug_builds` | `--console is not inside #if DEBUG` | passes (debug builds only) |
| N3 dead 0.1 helpers | (removal) | | `AutostartTask` keeps only the task name and its removal; the 61 tests of the removed code are removed |
| N4 files left from 0.1.1 | `N4_files_from_older_versions_are_removed_on_upgrade` | `Assert.Contains() Failure: Sub-string not found` | `[InstallDelete] {app}\Glance.SensorHost.*` |
| N5 `STATUS` for other users | `N5_other_sessions_get_a_status_without_the_session_or_error_text` | `Assert.DoesNotContain() Failure: Sub-string found` ("showing session 1") | passes ("display in use; awake") |
| N6 uninstall delete through a link | `N6_the_uninstaller_does_not_delete_through_a_link` | `Assert.DoesNotContain() Failure: Sub-string found` (`[UninstallDelete]`) | passes: `CurUninstallStepChanged` skips a reparse point |

### Test gaps (surviving mutations R1-R3)

| Gap | Test | Result |
|---|---|---|
| T1 / R1 `CurrentUserOnly` on the tray client | `T1_the_editor_only_talks_to_a_tray_app_of_its_own_user` (the options are now `ControlPipeClient.Options`, and the client uses them) | passed before and after (a guard); the mutation now fails it |
| T4 / R2 a refused HELLO closes | `T4_a_refused_or_wrong_hello_closes_the_connection` | passed before and after (a guard) |
| R3 brightness clamped | `R3_brightness_out_of_range_is_clamped_by_the_service` | passed before and after (a guard) |

### Mutation check of the fixes

Each new rule was broken on purpose and the service test classes (154 tests at the time) were run. Every file was restored afterwards
(touched, so the next build really compiled it), and the full suite was green again (607 / 607).

| Mutation | Result |
|---|---|
| F1 no first-instance flag (`ServicePipeFactory`) | caught by 2 (display and sensor squatter tests) |
| F2 no per-session limit | **survived** at first (16 instances hid it); `M2_one_session_cannot_hold_more_than_its_share_of_connections` added, which fails with the mutation and passes without it |
| F3 no idle timeout | caught by 1 |
| F4 network clients accepted | caught by 1 |
| F5 no blank right after a transfer during a suspend | caught by 3 |
| F6 connector bypasses a running service | caught by 1 |
| F7 tray runs elevated | caught by 2 |
| F8 dev-build search outside `\bin\` | **survived** at first (the repository check covered every case); a case inside a repository but outside `\bin\` added to `L9_a_development_build_only_searches_inside_its_repository`, which now fails with the mutation |
| F9 service folder not checked | caught by 2 |
| F10 log lines not limited | caught by 2 |
| F11 log not rolled over | caught by 1 |
| F12 full status for every session | caught by 1 |
| F13 owner of later pipe instances not checked | **survives**. With the first-instance flag in place, a later instance can only join a foreign pipe if every instance of ours was gone at that moment *and* another account took the name, and a test cannot create a pipe owned by another account without a second account. It is defence in depth; the squatter tests cover the first instance. |

After the new test and the new case: 608 / 608.

### Not done here

- **Nothing was installed, started, stopped or registered.** The installer was compiled (`ISCC`, placeholder package, "Successful
  compile", output deleted) but not run.
- **So these remain manual:**
  - the folder lock (`icacls`);
  - the service's refusal from an unsafe folder under the real Service Control Manager;
  - the privilege list with real sensors;
  - `ChangeServiceConfig2W` as SYSTEM;
  - the tray's relaunch from a real 0.1 task;
  - the event-log entries.
- **The editor** still has no elevation check of its own (nothing starts it elevated). Only the tray has one, as asked.

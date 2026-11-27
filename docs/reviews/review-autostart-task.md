# Review: The elevated runner registers the autostart task (fix for H6)

- **Verdict:** merge after fixes — **now:** M1-M3, L1-L3 and L5 fixed; H1 partly fixed here and later fully addressed by the Windows-service redesign; L4 and L6 open; merged into main
- **Build/tests:** 369/369 pass on the branch after the fixes (build: 0 errors)
- **Proof:**
  - Before: on `main`, a non-elevated shell could overwrite `%TEMP%\GlanceRunnerTask.xml`, the file the elevated `schtasks` registers; `main` had no folder trust check at all
  - Before: H1: a harmless `DOTNET_STARTUP_HOOKS` hook ran inside the runner process (marker file written); 35 of the 54 review-fix tests failed
  - After: no file is used (XML built in memory); a user-owned `%TEMP%` folder is refused and `C:\Program Files\Glance` trusted; the hook no longer runs; 369/369

## Findings
- **High H1** — the user's environment (`HKCU\Environment`) reaches the elevated runner at every sign-in, so startup-hook, profiler and diagnostics variables can load code as admin (pre-existing; `AutostartTask.BuildXml`) → startup hooks turned off; risky variables logged to `runner.log` and shown in the window — **partly fixed: profiler and `DOTNET_DiagnosticPorts` variables still reached the runner on this branch; later fully addressed by the Windows-service redesign (`docs/reviews/review-runner-service.md`)**
- **Medium M1** — existing tasks were not checked, so an old task pointing at a writable copy stayed silently (`Autostart.cs:20-34`) → `AutostartTask.Assess` checks where the task points; the window offers repair or turn-off — **fixed**
- **Medium M2** — a standard user got a task that can never start, and the editor reported success (`AutostartInstaller.cs:35-58`) → refused in the editor and in the runner (exit code 20 `UserNotAdmin`) — **fixed**
- **Medium M3** — two trust engines with the same `TrustVerdict` name; the autostart one had no link or drive-type rule (`FolderTrust.cs`, `FfmpegTrust.cs`) → `FolderTrust` is the one engine with all rules; FFmpeg switched to it — **fixed**
- **Low L1** — "Remove" reported a failure when the task was already gone (`AutostartInstaller.cs:72-74`) → decided by HRESULT, not exception type — **fixed**
- **Low L2** — turning auto-start off from an untrusted copy elevated that copy (`Autostart.cs:70-74`) → removal runs System32 `schtasks.exe` with a fixed command line — **fixed**
- **Low L3** — a locked-down folder on a data drive was always refused (`FolderTrust.cs:71`) → DELETE is ignored on a drive root only — **fixed**
- **Low L4** — one task name for every user on the PC (`AutostartInstaller.cs:16,58`) → per-user task, or show whose task it is — **open**
- **Low L5** — the read-back only checked the action (`AutostartTask.cs:155-172`) → also checks working folder, trigger, principal and user SID — **fixed**
- **Low L6** — the Background service window starts `schtasks.exe` on the UI thread every second (`ServiceWindow.cs:56-58,90`) → cache the state off the UI thread — **open**
- **Low (test gap)** — the routing in `Program.Main` (install/remove modes must never reach the mutex) is untested — **open**
- Nits: fixed: N1 (System32-only DLL loading), N2 (separate exit codes and logging), N3 (`--user` refuses a switch), N5 (disposal), N6 (`AGENTS.md`); open: N4 (old runner in dev setups, dev only)

## Manual checks for the user
- The scheduled task this review covers was replaced by the Windows service; for start at sign-in, use the manual test plan in `docs/design/runner-service.md`.
- On a PC that still has the old "Glance Runner" task, open the editor's Background service window: it should flag the old task.
- Turn the old task off from that window (UAC), then check in Task Scheduler that it is gone.
- Optional, on a VM: set `DOTNET_STARTUP_HOOKS` in `HKCU\Environment` to a harmless hook, sign in, and check that no marker file appears.

# Review: move the elevated work into a Windows service (security review)

- **Verdict:** merge after fixes — **now:** all findings fixed (several still need a check on the real PC, see below); merged into main
- **Build/tests:** 608/608 (668 before the dead 0.1 helpers and their 61 tests were removed); build 0 errors
- **Proof:**
  - Before: on `main`, "no Glance program asks for administrator rights" failed (`Glance.Runner` and `Glance.SensorHost` manifests) and a squatter on the tray control pipe acted as the user
  - Before: review fixes: 34 of 44 new tests failed (squatted pipe joined, console user locked out by 8 idle clients, tray ran elevated, unsafe install folder accepted)
  - After: 44/44 new tests pass, full suite 608/608; 13 mutations checked, 1 still survives (see test gap)

## Findings
- **High H1** — The installer let the service binary live in a user-writable folder, so SYSTEM could run a user's code (`Glance.iss:23`) → `DisableDirPage=yes`, folder ACL reset with `icacls`, the service refuses to run from a folder users can change — **fixed**
- **Medium M1** — Service pipes had no first-instance flag, so the service joined a squatter's pipe under its rules (`DisplayServer.cs:86`, `SensorPipeServer.cs:38`) → `ServicePipeFactory` with `FirstPipeInstance` and an owner check; the sensor pipe logs and retries — **fixed**
- **Medium M2** — 8 idle connections locked the console user out, and SYSTEM logged a line per second without limit (`DisplayServer.cs:22`) → 4 connections per session, 30 s idle timeout, rate-limited log lines, `service.log` rolls at 1 MiB — **fixed**
- **Medium M3** — A leftover 0.1 "Glance Runner" task started the new tray elevated with the user's environment (`Runner/Program.cs:5`) → tray relaunches itself unelevated or refuses; the service deletes the old task; the installer checks it is gone — **fixed**
- **Medium M4** — SYSTEM loaded `libusb0.dll` and vendor DLLs with the default search order ending in `PATH` (`Libusb0.cs:8`) → `SetDefaultDllDirectories` first in `Main`, Core imports from System32 only — **fixed** (GPU sensors to check by hand)
- **Low L1** — Clients over the network path reached the pipe and got a random session id (`NativeSessions.cs:10`) → redirector check closes them; NETWORK denied on the tray pipe — **fixed**
- **Low L2** — A suspend that found the device busy left the blank to nobody (`DisplayServer.cs:241`) → the frame request blanks right after its transfer — **fixed**
- **Low L3** — The tray treated "service busy or starting" as "no service" and kept USB for good (`DisplayConnector.cs:21`) → falls back to USB only if the service is stopped or not installed; moves back to the service — **fixed**
- **Low L4** — Installer ignored `sc`/`net` results and `taskkill` triggered the recovery restart (`Glance.iss:142`) → restart turned off before stopping; every step checked and reported — **fixed**
- **Low L5** — `PreshutdownTimeout` written straight to the registry (`Glance.iss:172`) → the service sets it with `ChangeServiceConfig2W` and logs the value — **fixed**
- **Low L6** — The service log was opened before the process knew it was the service (`Service/Program.cs:18`) → opened only in `ServiceMain` as SYSTEM; event log fallback — **fixed**
- **Low L7** — The service kept every LocalSystem privilege (`Glance.iss:161`) → service SID and only `SeChangeNotifyPrivilege`/`SeCreateGlobalPrivilege` — **fixed** (sensors must be checked on the real PC)
- **Low L8** — Another user could squat the tray control pipe (`RunnerContext.cs:326`) → name carries session and user SID, first-instance flag, rate-limited log — **fixed**
- **Low L9** — The dev-build fallback walked up to the drive root and could start a planted exe (`AppPaths.cs:6`) → `DevPaths` searches only from `\bin\` and inside the repository — **fixed**
- **Low L10** — The sensor pipe appeared only after LibreHardwareMonitor opened, and its errors were silent (`ServiceRuntime.cs:37`) → pipe created first (serves `[]`), errors logged — **fixed**
- **Low (test gap)** — `NativeService` is not unit-testable (T8); the owner check of later pipe instances survives its mutation (needs a second account) — **open**
- Nits: N1 design-note claims corrected, N2 `--console` only in debug builds, N3 dead 0.1 helpers removed, N4 old `Glance.SensorHost.*` deleted on upgrade, N5 short `STATUS` for other sessions, N6 uninstall skips a reparse point
- Also open: the editor has no elevation check of its own (nothing starts it elevated); only the tray has one

## Decisions for the user (all agreed by the reviewer)
- Portable elevated runner dropped; portable users get basic sensors
- Display stays dark before sign-in
- Any signed-in user may start the service, not stop it
- Tray starts at sign-in for every user, on by default
- The user side renders; SYSTEM never parses user files
- The console session wins the display
- Version 0.2.0
- FFmpeg installed per user
- 0.1 helpers removed next (since done, N3)
- Added by the review and done: install folder locked to Program Files, service privileges trimmed, the service deletes a leftover 0.1 task

## Manual checks for the user
Full plan: `docs/design/runner-service.md` §14. Key steps, on the PC with the display:
- Upgrade from 0.1.1: `icacls "C:\Program Files\Glance"` shows only admins/SYSTEM can write; `Glance.Service` runs as SYSTEM in session 0; tray and editor are not elevated; `schtasks /Query /TN "Glance Runner"` finds nothing
- Set `CORECLR_ENABLE_PROFILING`/`CORECLR_PROFILER_PATH` with `setx`, sign out and in: profiler errors for the tray/editor only, none for `Glance.Service.exe`; remove the variables afterwards
- Sensors in the service with trimmed privileges: CPU, motherboard fans, GPU, drives, and the display (L7, M4); if any are missing, check `sc qprivs` and `service.log`
- Sleep 5 times while a video theme plays: black before every sleep (L2)
- As a standard user: `sc stop` is denied, `sc start` works, `sc config` is denied
- Squat both pipe names from a standard user's PowerShell, restart the service: `service.log` says the names are taken, and it serves again soon after the squatter closes
- Re-create a 0.1-style "Highest" task: the tray must not run elevated, or the service has deleted the task
- `service.log` shows "Pre-shutdown timeout: 5000 ms" right after install

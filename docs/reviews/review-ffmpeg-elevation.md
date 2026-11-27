# Review: Only start an admin-only ffmpeg.exe from the elevated runner (H3)

- **Verdict:** merge after fixes — **now:** M2, M3 and L1-L7 fixed; M1 partly fixed; M4 open on this branch (both later eased by the Windows-service redesign); merged into main
- **Build/tests:** 417/417 pass on the branch after Phase 2 (319/319 after Phase 1)
- **Proof:**
  - Before: on unfixed `main`, a user-writable `ffmpeg.exe` planted first on `PATH` was returned by `Ffmpeg.Find()`, the lookup the elevated runner used
  - Before: Phase 1's 18 new tests: 12 failed on the unfixed branch code
  - After: `FindForElevated()` refuses the planted file while the editor still uses it (now a permanent test); 417/417

## Findings
- **Medium M1** — "Install for all users" was never checked, and existing per-user installs stopped playing video in the runner (`SetupChecks.cs:86-101`) → winget's machine install keeps the user's permissions (code reading), so an elevated script now installs and then resets the folder to admin-only; clearer per-user messages — **partly fixed: the script was never run; one real install must confirm it.** Later the Windows-service redesign runs FFmpeg only as the user, so a per-user install works (`review-runner-service.md`)
- **Medium M2** — the line that turns the check on was untested; switching it off passed every test (`Ffmpeg.cs:37,87-98`) → `FfmpegFinder` with test seams; that mutation now fails 9 tests — **fixed** (only an elevated test run could catch the flag forced to `false`)
- **Medium M3** — two ACL trust engines with different rules and two `TrustVerdict` types (`FfmpegTrust`, `FolderTrust`) → Phase 2: rebased onto the autostart branch, `FfmpegTrust` deleted, lookup uses `FolderTrust.CheckExecutable` — **fixed**
- **Medium M4** — the trusted ffmpeg still ran as administrator on theme videos a normal user controls (`VideoSource.cs:37-44`) → decode without admin rights — **open** on this branch; the later Windows-service redesign runs FFmpeg only in the user's own processes (`review-runner-service.md`)
- **Low L1** — the winget `Links` symlink logged a misleading "not used" line even when a good copy was found (`Ffmpeg.cs:47-54`) → rejections are reported only when nothing usable is found — **fixed**
- **Low L2** — the setup warning said the wrong thing about when video plays (`SetupChecks.cs:71-75`) → reworded — **fixed**
- **Low L3** — after an install, the editor said "ready" based on the non-elevated search (`MainWindow.cs:1274`) → decided with `FindForElevated()` in `SetupChecks.FfmpegInstallOutcome` — **fixed**
- **Low L4** — a video that failed for lack of FFmpeg was never retried (`ThemeRenderer.cs:352`) → retried after 5 s — **fixed**
- **Low L5** — for relative PATH entries the full path was checked but the relative string was started (`Ffmpeg.cs:61-66`) → relative entries refused when elevated; the checked full path is returned — **fixed**
- **Low L6** — real-ACL tests depend on how the build machine's C:\ is set up (`FfmpegTrustTests.cs:208`) → skipped with a reason on an unusual drive root — **fixed**
- **Low L7** — the docs described the old search → `AGENTS.md`, `README.md`, `TUTORIAL.md`, `WALKTHROUGH.md` updated — **fixed** (`docs/walkthroughs/ffmpeg-elevation.md` still names FfmpegTrust)
- **Low (test gaps)** — no test for `SetupChecks.Run` with only a user copy, or for `AppendData` alone on the folder — **open**
- Nits: fixed: N2 (placeholder owners refused), N5 (events raised outside the lock); N3 no longer applies (FolderTrust reads raw ACLs); open: N1 ("see runner.log" in an elevated editor), N4 (`Process.Start(...)!` can return null)

## Manual checks for the user
- With the background app running, check a video background plays on the display.
- Open the setup window and check the FFmpeg row matches what the display does.
- Optional: click "Install for all users" (UAC) once and check the setup row turns green.

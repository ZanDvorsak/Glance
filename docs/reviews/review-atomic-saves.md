# Review: atomic theme, settings and package saves (fix for M4 and the export part of L3)

- **Verdict:** merge after fixes — **now:** all findings fixed except L3, which is partly fixed (see below); merged into main
- **Build/tests:** 581/581 (4 parallel runs under CPU load and 2 without, about 8 s); build 0 errors
- **Proof:**
  - Before: the first round's tests on the old behaviour: 13 of 560 failed (settings truncated to `""`, corrupt settings overwritten, `JsonException` on a half-written theme, broken zip after a failed export)
  - Before: review fixes: 18 of 581 failed; then the timing tests failed in 23 of 32 runs under load
  - After: 581/581; 32/32 runs under load and 30 in a row without load

## Findings
- **Medium M1** — Editor turned "could not read settings" into the defaults and saved them over the good file (`MainWindow.cs:60`, `AppSettings.cs:55`) → `AppSettings.Read` reports Unreadable; a stand-in never saves over the file; status bar explains — **fixed**
- **Low L1** — Runner started with the defaults when settings were unreadable and never retried (`RunnerContext.cs:41`) → retries 10 s later from the supervisor timer — **fixed** (not unit tested; runner has no tests)
- **Low L2** — A failed backup copy failed the whole save (`AtomicFile.cs:54`) → backup is best effort; save goes on — **fixed**
- **Low L3** — Saves and reloads waited up to 1-3 s on UI threads, also for permanent errors (`AtomicFile.cs:19`) → runner reads before taking `_gate`; read-only and other permanent errors fail at once — **partly fixed: editor still saves on the UI thread (up to 1 s while another program holds the file)**
- **Low L4** — Any open handle on the target blocked the swap, and a failed settings save was dropped silently (`AtomicFile.cs:63`) → POSIX rename through the temp file's handle, `MoveFileEx` fallback; failures are logged — **fixed** (a handle without delete sharing still blocks, but is reported)
- **Low L5** — Temp files left by a crash were never removed (`AtomicFile.cs:44`) → old `*.tmp` files are cleaned up on save and read — **fixed**
- **Low L6** — The user was not told their settings were damaged (`AppSettings.cs:99`) → `Read` returns Corrupt and where the file was kept; shown in the status bar — **fixed**
- **Low L7** — `theme.json.bak` was written but never read (`Theme.cs:119`) → a damaged `theme.json` falls back to the backup with a message — **fixed**
- **Low (test gap)** — No tests for: a failed `.bak` rename, the `KeepAside` branches, the watcher's event sequence for one save, the runner keeping its settings — **open**
- Nits: N1 unused variable, N2 valid JSON of the wrong type, N4 corrupt copies and log capped, N5 write-through after rename, N6 only sharing/lock errors retried, N8 design note, N9 faster tests (N3 `KeepAside` race and N7 stale read-only `.bak` left open)

## Manual checks for the user
- Change and save a theme in the editor: the runner reloads once and the display follows
- Close the editor, cut `settings.json` short by hand, start the editor: the status bar says the settings were damaged and a `settings.json.corrupt-*` copy is kept
- Cut `theme.json` short by hand: the editor and runner show the backup and say so; saving repairs it
- Export a `.glance` over an existing one, also to a USB stick (FAT/exFAT): the old file stays whole if the export fails
- Optional: a power cut right after a save (the only way to check the flush and rename for real)

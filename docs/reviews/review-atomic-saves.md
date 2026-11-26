# Code review: atomic theme, settings and package saves (fix for M4 and the export part of L3)

**Date:** 2026-11-26
**Branch:** `claude/fix-atomic-saves-54bf6d`, three commits on `main` (`9bf03b0`): `88db92c` (seam, no behaviour change), `7383b55` (tests),
`216ab94` (fix).
**Scope:** `src/Glance.Core/Storage/AtomicFile.cs` (new), `src/Glance.Core/Settings/AppSettings.cs`, `src/Glance.Core/Themes/Theme.cs`,
`src/Glance.Core/Themes/ThemePackage.cs`, `src/Glance.Runner/RunnerContext.cs`, `src/Glance.Tests/AtomicSaveTests.cs` (new), one expectation in
`src/Glance.Tests/ThemeTests.cs`. Author's design and proof: `docs/design/fix-atomic-saves.md`.
**Findings being fixed:** M4 in `docs/reviews/review-full-codebase.md` (theme and settings saves not atomic; corrupt settings silently reset and
overwritten; the runner could read a half-written file) and the export part of L3 (Export deleted the old `.glance` before writing the new one).
**Reviewer:** independent; did not write the change. No code was changed by this review.

Nothing here was run in the editor or the runner, and nothing was tried with a real crash or power cut. Everything below comes from reading the code,
the test suite on four commits, seven temporary probe tests and five temporary mutations. All of those ran in throw-away worktrees under the session
scratchpad, which were removed afterwards. The branch worktree only gained this file and `docs/walkthroughs/atomic-saves.md`.

---

## Verdict: merge after fixes

The change does what M4 and L3 ask, and it does it well. Every save of `theme.json`, `settings.json` and `.glance` now goes through one small helper
that writes a temporary file in the same folder, flushes it to the disk, and renames it over the target. The target is never opened for writing, so a crash,
a power cut or an error leaves the old file whole. A corrupt `settings.json` is logged and kept aside instead of being overwritten. The runner waits out a
busy or half-written file and, on a reload, keeps its current settings when the file cannot be read. The proof holds: I reproduced the author's 13 failures
at `7383b55`, each for the reason the finding describes, and 560/560 passing at the head.

One medium issue should be fixed before merging (or as the immediate follow-up). It is not a regression, but it is the last path to the data loss M4 is
about, and the fix is a few lines:

- **M1.** The editor still turns "could not read settings" into the defaults and then **saves those defaults over the good file**, at the latest when it
  closes. `TryLoad` already reports the case; the editor ignores it. I confirmed this with a probe.

The low findings are about waiting on UI threads, a backup step that can fail the whole save, a polite open handle blocking the swap, leftover temp
files, and telling the user. None blocks the merge.

## Build and test results

Run in the branch worktree:

| Command | Result |
|---|---|
| `dotnet build src/Glance.sln -c Release -p:SkipHost=true` | **Succeeded**, 0 errors, 37 warnings. Eight come from the new test file: CS8619 at `AtomicSaveTests.cs:21`, CA1416 (`FileStream.Lock`) at `:30`, and six xUnit1031 (blocking `Task.Wait`) at `:102,158,175,192,245,261`. None from the production code. |
| `dotnet test src/Glance.Tests -c Release` | **Passed 560 / 560**, about 14 s (it was about 8 s before; see N9) |

---

## Proof verification

The rule for this project is that every bug must be shown to exist, then shown fixed. I checked both halves, and that the seam really changed nothing.

**1. The seam `88db92c` changes no behaviour.** `git diff main 88db92c -- src` touches only production code (no tests). Read side by side:

| Old (`main`) | Seam |
|---|---|
| `AppSettings.Load()`: `try { Deserialize(File.ReadAllText(FilePath)) ?? new() } catch { new() }` | `TryLoad` does exactly that and always returns `true`; `Load()` returns its settings; the `log` argument is ignored |
| `Save()`: `File.WriteAllText(FilePath, ...)` inside `try/catch` | `Save()` calls `Save(FilePath)` = `AtomicFile.WriteAllText` = `File.WriteAllText` |
| `Export`: `File.Delete` then `ZipFile.Open(dest, Create)` | `AtomicFile.Write`: `File.Delete` then `File.Create` + `new ZipArchive(stream, Create, leaveOpen: true)` |
| Runner: `Theme.Load(p)`; `_settings = AppSettings.Load()` | `AtomicFile.ReadWithRetry(() => Theme.Load(p))` = `Theme.Load(p)`; `if (TryLoad(...)) _settings = fresh` where `TryLoad` always returns `true` |

The only textual difference with any effect is `ZipFile.Open` (`FileMode.CreateNew`) versus `File.Create` (`FileMode.Create`), right after a delete:
the same outcome. `Theme.Save` is not touched by the seam. Test results agree: **`main` 542/542, `88db92c` 542/542**.

**2. The tests fail on the unfixed code.** Scratch worktree at `7383b55` (seam + tests), `dotnet test src/Glance.Tests -c Release`:
**Failed 13, Passed 547, Total 560**. The failures, with the messages from the test log:

| Test | Failure at `7383b55` | Matches the design note |
|---|---|---|
| `A_settings_save_that_fails_mid_write_keeps_the_previous_file` | `Expected: "{"LastTheme":...` / `Actual: ""` (truncated to nothing) | yes |
| `Corrupt_settings_are_logged_kept_aside_and_never_overwritten` x4 (cut off, `""`, `null`, zero bytes) | `Assert.Single() Failure: The collection was empty` (no `.corrupt-*` file) | yes |
| `A_settings_reader_during_writes_always_sees_a_whole_file_and_no_save_is_lost` | writer threw `IOException: ... being used by another process` | yes |
| `Settings_locked_for_a_moment_are_read_once_the_lock_is_gone` | `LastTheme` expected the path, actual `null` | yes |
| `Settings_that_stay_unreadable_are_reported_as_such_and_left_where_they_are` | `Assert.False() Failure` (reported success with the defaults) | yes |
| `Settings_being_rewritten_in_place_by_another_program_are_not_mistaken_for_corrupt` | `LastTheme` actual `null` | yes |
| `A_theme_save_keeps_one_backup_of_the_previous_theme_file` | expected `["theme.json", "theme.json.bak"]`, actual `["theme.json"]` | yes |
| `A_theme_read_while_the_file_is_briefly_locked_succeeds` | `IOException ... being used by another process` | yes |
| `A_theme_read_of_a_half_written_file_waits_for_the_whole_file` | `JsonException ... reached end of data` | yes |
| `An_export_that_fails_midway_keeps_the_previous_package` | expected `"previous package"`, actual `"PK\u0003\u0004...assets/a.png"` (a broken zip) | yes |

The five tests the author lists as guards (`A_theme_save_that_fails_mid_write_keeps_the_previous_file`, `A_settings_save_replaces_the_file_and_leaves_nothing_else`,
`Missing_settings_give_the_defaults_quietly`, `A_theme_read_of_a_missing_file_fails_at_once`, `An_export_over_an_existing_package_replaces_it_completely`) passed there, as stated.

**3. The tests pass on the fix.** Branch head `216ab94`: **Passed 560 / 560** (542 + 18 new). The changed expectation in
`ThemeModelTests.Saving_again_to_the_same_folder_copies_nothing_extra` (`theme.json.bak` now expected) is the intended new behaviour.

**4. Mutation check (my own, on a copy of the head).** Each mutation was applied to `AtomicFile.cs` alone, then
`AtomicSaveTests`, `ThemeModelTests` and the package tests were run (53 tests), then the file was restored (verified identical):

| Mutation | Caught by |
|---|---|
| `Flush(flushToDisk: true)` -> `Flush()` | **nothing** (expected: durability cannot be tested without a power cut) |
| `JsonException` no longer retried in `ReadWithRetry` | `Settings_being_rewritten_in_place_...`, `A_theme_read_of_a_half_written_file_...` |
| temp file not deleted on failure | 4 tests (settings and theme "fails mid write", export, `ThemeModelTests` read-only target) |
| backup made by **renaming** the target instead of copying it (a window with no `theme.json`) | only `ThemeModelTests.A_save_that_fails_while_writing_the_theme_file_...`, and only incidentally (see test gaps) |
| no retry around the final `File.Move` | `A_settings_reader_during_writes_...` |

**Result: the proof is sound.** The bug is shown to exist on the old behaviour, every failure is the one M4/L3 describe, and the fix turns all of them green.
The one property that cannot be proven here is the `Flush(true)` ordering after a real power cut; it is correct by reading (section "What was checked").

---

## Findings

### Medium

#### M1. The editor still saves the defaults over settings it could not read
- **Where:** `src/Glance.App/MainWindow.cs:60` (`private readonly AppSettings _settings = AppSettings.Load();`), saves at `:169` (always, on close),
  `:818`, `:851`, `:932`; `src/Glance.Core/Settings/AppSettings.cs:55-61` (`Load` drops `TryLoad`'s result).
- **Evidence:** `TryLoad` returns `false` when the file exists but stays locked for the whole retry budget, and the design note says "callers that
  already have settings should keep them". The editor has none at start, so it gets the defaults, and nothing remembers that they are not real. Probe
  (file held with `FileShare.None` for 1.5 s while the editor's `Load` runs, then one `Save` as `MainWindow.cs:932` does):
  ```
  log: Could not read ...\settings.json: The process cannot access the file ... because it is being used by another process.
  editor got LastTheme=null Brightness=7
  file now: {"LastTheme":null,"Brightness":7,"PinDisplayToPage":false,"SetupCheckShown":true}
  ```
- **Failure scenario:** a backup or sync tool, or a virus scan of a redirected/roaming `%AppData%` on a slow share, holds `settings.json` for more than a
  second while the editor starts. The editor opens the starter theme. When the user closes it, `Closing` saves the defaults; the runner's watcher reloads and
  the display drops to the starter theme at brightness 7. The user's `LastTheme`, brightness and pin setting are gone. That is M4's third scenario
  ("silently overwritten with the defaults on the next save"), reached through "unreadable" instead of "corrupt". It is not a regression (`main` did the same
  for every error), but this change is the place to close it, and its own API already tells the cases apart.
- **Remedial action:**
  - In `MainWindow`, use `TryLoad`. Keep a `bool _settingsReadable`; when it is `false`, show it in the status bar ("Settings could not be read; changes to
    them will not be saved this session") and skip `_settings.Save()`, or re-read the file just before each save and copy over only the fields the editor changed.
  - Better still, give `AppSettings` the rule itself (for example `Load` returns an object that refuses `Save()` until a later `TryLoad` succeeds), so a
    test in Core can cover it.
  - Add a test: lock the file past the budget, load, save, assert the file still holds the original settings.

### Low

#### L1. The runner starts with the defaults when settings are unreadable, and nothing retries
- **Where:** `src/Glance.Runner/RunnerContext.cs:41` (`AppSettings.Load(Log)` in a field initializer).
- **Evidence:** only `Reload` (`:114`) keeps the current settings on `false`. At start there are none, so the runner uses the defaults; the next reload only
  happens on a watcher event or a tray Reload.
- **Failure scenario:** the runner starts at sign-in (auto-start task) while a scanner holds `settings.json` for over a second. The display shows the
  starter layout at full brightness until the user saves something or picks Reload, which they have no reason to do.
- **Remedial action:** when the start-up `TryLoad` returns `false`, log it and schedule one more `Reload` a few seconds later (the supervisor timer already
  ticks), instead of waiting for a file event.

#### L2. The backup copy is on the critical path, so failing to make a backup fails the save
- **Where:** `src/Glance.Core/Storage/AtomicFile.cs:54-60` (inside the `try`), compared with the comment at `:74` ("failing to keep the backup is not a reason to report it as failed").
- **Evidence:** a probe of the theme "fails mid write" test shows where it actually fails: after 1,039 ms,
  `IOException: ... another process has locked a portion of the file : '...\theme.json.bak.<guid>.tmp'` from `File.Copy` at `AtomicFile.cs:59`, not from the rename.
  A read-only, locked or unreadable old file, or a full disk for the extra copy, fails the save even when the rename itself would succeed.
- **Failure scenario:** in practice the cases where the copy fails but the rename would work are rare (most locks block both), so this is mostly about
  honesty of the contract and the extra second of waiting. It also means the test named "fails mid write" exercises the backup path, not the swap.
- **Remedial action:** wrap the backup copy in its own `try`, log or ignore a failure, and continue with the save (set `backupTemp = null`). Add a test where
  the copy fails and the save still succeeds.

#### L3. Saves and reloads now wait up to a second or more on UI threads, also for permanent errors
- **Where:** `AtomicFile.cs:19-20,89-101`; callers on UI threads: `MainWindow.cs:815,818` (editor save), `RunnerContext.cs:109-121` (runner reload, under `_gate`).
- **Evidence:** the retry budget is one second per step, with `Thread.Sleep(50)`. A theme save can spend up to ~1 s in the backup copy and ~1 s in the rename,
  then `_settings.Save()` another second. A runner reload can spend ~1 s on settings (~1 s more if the file is corrupt, since `JsonException` is retried)
  and ~1 s on the theme, all while holding `_gate`. `UnauthorizedAccessException` counts as busy, so permanent errors wait too: a read-only `theme.json` failed
  after 1,042 ms (probe), where it used to fail at once.
- **Failure scenario:** the editor freezes for one to three seconds on Save while an indexer holds the file. In the runner, a power event waiting for
  `_gate` hits its bounded wait (it then blanks the display itself, so this is handled, but it is the slow path). Same family as L8 and M7 in the full review.
- **Remedial action:** run `Theme.Save`/`AppSettings.Save` in `Task.Run` from the editor and show "Saving..."; in the runner, read settings and theme
  **before** taking `_gate` and only swap them in under the lock. Optionally stop retrying `UnauthorizedAccessException` when the target has the read-only attribute.

#### L4. Any open handle on the target blocks the swap, even one that allows writing and deleting
- **Where:** `AtomicFile.cs:63` (`File.Move(temp, target, overwrite: true)`), and `AppSettings.cs:129-133` (`Save()` swallows the failure).
- **Evidence:** probe, holding `settings.json` open for longer than the budget with three share modes:
  `ReadWrite|Delete`, `Read` and `ReadWrite` all ended in `UnauthorizedAccessException: Access to the path is denied` after ~1,050 ms, and the old content
  stayed. The old in-place `File.WriteAllText` would have succeeded against a handle that shares write access.
- **Failure scenario:** a program that keeps `settings.json` or `theme.json` open for a long time with full sharing (some sync, backup or live-reload tools do).
  Theme saves show "Save failed", which is honest. Settings saves are dropped silently, so the runner does not follow the editor's new theme.
- **Remedial action:** on Windows 10 1709 and later, replace with POSIX semantics (`SetFileInformationByHandle(FileRenameInfoEx)` with
  `FILE_RENAME_FLAG_POSIX_SEMANTICS | FILE_RENAME_FLAG_REPLACE_IF_EXISTS`), which succeeds while others hold the old file open with `FILE_SHARE_DELETE`; fall
  back to `File.Move`. At least log a failed `Save()` instead of swallowing it.

#### L5. Temporary files left by a crash are never cleaned up
- **Where:** `AtomicFile.cs:44` (`<name>.<guid>.tmp`), `:57` (`<name>.bak.<guid>.tmp`).
- **Evidence:** the author lists this as open. A crash or kill between creating the temp file and the rename leaves it; nothing deletes it later. For an export
  it lands in the user's chosen folder (for example the Desktop: `mine.glance.<guid>.tmp`, possibly large).
- **Failure scenario:** harmless to the runner (the watcher and loaders ignore `.tmp`), but clutter accumulates in theme folders and export folders, and a
  large half-written export stays on the user's disk.
- **Remedial action:** at the start of `Write`, delete files in the same folder matching `name + ".*.tmp"` (and `name + ".bak.*.tmp"`) that are older than,
  say, an hour. Add a test with a stale and a fresh temp file.

#### L6. The user is not told that their settings were damaged
- **Where:** `AppSettings.cs:99-115,118-126` (only `glance.log` or `runner.log`).
- **Evidence:** the corrupt file is kept and logged, which satisfies M4's remedial action. But the editor has no way to show it (the log line is the only trace),
  and `glance.log` is new, so the user does not know to look there.
- **Failure scenario:** after a power cut the display shows the starter theme and the editor opens a blank theme. The user assumes their theme is gone and
  rebuilds it, while `theme.json` and `settings.json.corrupt-...` are sitting in `%AppData%\Glance`.
- **Remedial action:** return the kept-aside path from `TryLoad` (an extra `out` or a result record) and show it once in the editor's status bar, with the
  last theme path if it can be recovered from the corrupt text. The runner can put a short note in its tray tooltip.

#### L7. `theme.json.bak` is written on every save but never read
- **Where:** `Theme.cs:119` (`keepBackup: true`); no reader anywhere (author lists it as open).
- **Evidence:** `RunnerContext.LoadTheme` (`:97-106`) falls back to the starter layout when `theme.json` does not parse, and the editor shows an error.
- **Failure scenario:** a `theme.json` damaged by an older version or a hand edit shows the starter layout although a good copy is next to it.
- **Remedial action:** in `LoadTheme` (and the editor's open), on a `JsonException` try `theme.json.bak`, log that the backup was used, and show it.
  Make it a Core helper (`Theme.LoadWithBackup`) so it can be tested.

### Nits

- **N1. Unused variable.** `AppSettings.cs:75,80`: `text` is assigned and never read. Remove it, or include its length in the corrupt log line.
- **N2. "Corrupt" also means "valid JSON of the wrong type".** A probe with `"Brightness": "high"` was kept aside as corrupt after a 1,012 ms wait. Reasonable,
  but the doc comment says "does not parse". Say "cannot be read as settings" and add the case to the theory test.
- **N3. A small race in `KeepAside`.** Between the last failed read (`AppSettings.cs:78-82`) and `File.Move` (`:106`), the editor can finish an atomic save of a
  *good* file, which then gets moved aside as corrupt. The window is microseconds and needs a corrupt file to start with. Fix by re-reading just before the
  move (or moving, re-parsing the moved file, and moving it back if it now parses).
- **N4. Unbounded leftovers.** `settings.json.corrupt-*` files and `glance.log` are never capped or rotated. Rare, but cheap to cap (keep the newest five).
- **N5. The rename is atomic, not durable.** `File.Move` does not pass `MOVEFILE_WRITE_THROUGH`, so after a power cut right after a save the old file can come
  back. It is whole, so this is acceptable; the doc comment ("flushed to the disk") is accurate for the data only. Say so.
- **N6. `IsBusy` is broad.** `AtomicFile.cs:100-101` retries every `IOException` that is not "not found", including `PathTooLongException` and disk full,
  which cannot heal in a second. Narrow it to sharing and lock violations (HResult `0x80070020`, `0x80070021`) plus `UnauthorizedAccessException`.
- **N7. A read-only `.bak` silently goes stale.** Probe: after `theme.json.bak` was made read-only, two more saves left it at the version from two saves ago,
  with no log. Fine as behaviour; worth a log line.
- **N8. Design note wording.** "The runner's `*.json` watcher only sees the final rename": it also sees `Deleted theme.json` just before the `Renamed`
  (probe). Harmless, because the runner does not subscribe to `Deleted`, but anyone adding a `Deleted` handler later must know.
- **N9. Test time.** The suite went from about 8 s to about 14 s (four corrupt cases wait the full second each, and one test holds a lock for 4 s). `AGENTS.md`
  says "about 5 seconds". Make the retry budget injectable (internal setter or a `RetryPolicy` parameter) and use 200 ms in tests.

---

## Test gaps

| Missing test | Why it matters |
|---|---|
| The editor's load-then-save when the file is unreadable | M1. Needs the rule moved into Core to be testable. |
| A reader loading `theme.json` in a loop while `Theme.Save` runs many times | The settings version exists; the theme path, which has the extra backup step, has none. The "backup by rename" mutation (a window with no `theme.json`) was caught only incidentally by a read-only-target test. |
| The backup copy fails but the save succeeds | L2 (today it fails, and the test that triggers it is named for a different thing) |
| The `.bak` rename fails and the save still succeeds | The swallow at `AtomicFile.cs:75-76` is untested (probe N7 shows it works) |
| `KeepAside`: the `-2` suffix, the copy fallback, "another process moved it first" | Three branches in `AppSettings.cs:103-108`, none covered |
| The watcher's event sequence for one save | Locks in "one reload per save, temp files invisible" (section 4.10 of the walkthrough lists the measured sequence) |
| Runner keeps current settings when `TryLoad` returns `false` | Lives in `RunnerContext`, which has no tests; a tiny Core helper would make it testable |
| Stale temp-file cleanup | L5, once implemented |
| Timing robustness | Three tests depend on 250-300 ms finishing well inside the 1 s budget; on a loaded CI runner that margin is the only thing between pass and fail |

---

## What was checked and found sound

- **The write is atomic on NTFS.** The temp file is in the target's folder (`AtomicFile.cs:44`), so the swap is a same-volume rename, not `MoveFileEx`'s
  copy fallback. It is created with `FileMode.CreateNew` and a random name, so two writers never share a temp file. `FileShare.None` hides it while it is
  incomplete. The target is never opened for writing; the only change to it is the final `File.Move(..., overwrite: true)` (`MoveFileEx` with
  `MOVEFILE_REPLACE_EXISTING`), which on one volume replaces the name in one journaled step. Readers see the whole old or the whole new file.
- **Data before name.** `fs.Flush(flushToDisk: true)` (`:51`) calls `FlushFileBuffers` before the rename, after the callback's own `StreamWriter`/`ZipArchive`
  have been disposed into `fs` (`leaveOpen: true` keeps `fs` open for it). That is the right order for power-cut safety.
- **A missing target works.** `File.Move` with `overwrite` creates it; `keepBackup` is skipped when there is nothing to back up (tested: first save has no `.bak`).
- **The backup never opens a window without a target.** It is a copy, not a rename (the mutation shows why that matters).
- **`File.Move` over `File.Replace` is the right call**, for the reason given: `ReplaceFile` can fail with `ERROR_UNABLE_TO_MOVE_REPLACEMENT` and leave no
  file under the target name. Losing the old file's ACL and attributes is fine in `%AppData%\Glance` and for exports.
- **Interaction with H1 (Save As).** `File.Move` is the last statement in the `try`, so once `AtomicFile.Write` returns, the theme file is in place and the
  backup step cannot throw. If it throws, the target is untouched and H1's rollback deletes the copied assets. The two never disagree. The existing H1 tests
  (`A_save_that_fails_while_copying_...`, `A_save_that_fails_while_writing_the_theme_file_...`) still pass, and the second one now also proves no temp file is left.
- **Retries are bounded and cannot loop.** One `Stopwatch` budget of one second per call, 50 ms steps, the original exception rethrown by the filter.
  A missing file fails at once (tested, under 500 ms).
- **No watcher storm and no reload loop.** Measured with a watcher configured like the runner's: one save produces `Deleted` + `Renamed tmp -> settings.json`;
  temp-file create/write events are filtered out by `*.json`; the runner only acts on `Changed`/`Created`/`Renamed`, debounced. Keeping a corrupt file
  aside raises one `Renamed`; the resulting reload finds no file, uses the defaults and writes nothing, so it does not trigger another event.
  `.bak`, `.tmp` and `.corrupt-*` names never match `*.json`.
- **Corrupt settings are never overwritten.** Renamed aside with a timestamp (a copy if the rename fails), logged, and the next save writes a fresh file
  next to it (tested for four corrupt shapes, including `null` and zero bytes).
- **Unreadable is not corrupt.** A long lock returns `false` and leaves the file alone (tested); the runner's `Reload` keeps its settings in that case.
- **Export (L3).** The previous package survives a failed export and no temp file remains (tested); a successful export over a longer old file leaves no
  trailing bytes (tested). The zip's central directory is written before the flush.
- **Compatibility.** UTF-8 without BOM, as `File.WriteAllText` wrote; no JSON shape change; the legacy `MiniLcd` migration is unaffected.
- **Scope discipline.** `Theme.Load`, `Normalize`, `ResolveAsset` and `ThemePackage.Import` are untouched, as the design note says (another branch works on them).
- **The seam technique.** Splitting "route through a seam" from "change behaviour" made the red run possible without touching the real `%AppData%`, and it
  is verified above to be behaviour-neutral.

## Not verified

- The flush and the rename after a real power cut or a hard reset (cannot be simulated here).
- The editor and the runner were not started; the runner's reaction to the new event sequence was checked by reading `RunnerContext.cs:139-149` and
  with a stand-alone watcher configured the same way.
- Behaviour on non-NTFS targets (an export to a FAT32/exFAT USB stick or a network share): the code still works there, but the rename is not guaranteed atomic.

---

## Fixes after review

Written by the author of the change, not the reviewer. The findings above are unchanged.

**Commits** (on top of `8cbd1e8`, which added this review):

| Commit | What |
|---|---|
| `d5f9359` | Seam, no behaviour change: `AppSettings.Read`/`TrySave`/`AppendLog`, `SettingsRead`, `ThemeFile.Load` and a settable `AtomicFile.RetryBudget`, all doing what the branch already did, so the new tests compile and run red. |
| `7a5ad32` | Tests: `src/Glance.Tests/AtomicSaveReviewTests.cs` (new, 19 test methods, 22 cases), `TestTiming.cs` (retry budget 400 ms for the test assembly), shorter waits in `AtomicSaveTests`; the `"null"` corrupt case moved to the N2 test. |
| `edbf3c3` | The fixes, the editor and runner wiring, and the updated design note. |

**Method**, as for the first round: the tests were run against `7a5ad32` (the branch's behaviour behind the new entry
points) and the failures captured, then against `edbf3c3`.

**Before** (`7a5ad32`, `dotnet test src/Glance.Tests -c Release`): **Failed 18, Passed 563, Total 581** (13.1 s).
All 18 failures are in `AtomicSaveReviewTests`; nothing else failed.
**After** (`edbf3c3`): **Passed 581 / 581**, three runs in a row, 9-10 s. Build `dotnet build src/Glance.sln -c Release
-p:SkipHost=true`: succeeded; the new test files add no warnings (both test classes are marked Windows-only for the
file-lock and ACL calls, and the CS8619 at `AtomicSaveTests.cs:21` is fixed).

### Per finding

| Finding | Test | Before (`7a5ad32`) | After (`edbf3c3`) |
|---|---|---|---|
| **M1** editor saves defaults over unreadable settings | `M1_settings_that_could_not_be_read_are_never_saved_over` (file locked 1.5 s while read, then the editor's first-run save) | **Fail**: `Expected: "{"LastTheme":"C:\\themes\\mine\\theme.jso"...` / `Actual: "{"LastTheme":null,"Brightness":7,"PinDisp"...` (the reviewer's probe, reproduced) | Pass: `State = Unreadable`, `TrySave` returns false and logs "Settings not saved", the file still holds the user's settings |
| **L2** backup copy fails the save | `L2_a_theme_save_still_succeeds_when_the_backup_copy_cannot_be_made` (old `theme.json` denies read, allows delete) | **Fail**: `UnauthorizedAccessException: Access to the path '...\theme.json' is denied.` at `File.Copy` | Pass: saved, no `.bak`, no temp files |
| **L2** "fails mid write" did not reach the swap | `L2_a_theme_save_that_fails_mid_write_fails_at_the_swap_not_at_the_backup` | **Fail**: `Assert.DoesNotContain() Failure: Sub-string found` `...\theme.json.bak.73a153090c694855...` (the error came from the backup copy) | Pass: the error names `theme.json` (the swap), previous file kept |
| **L3** waits on permanent errors | `L3_a_save_over_a_read_only_file_fails_at_once` | **Fail**: `took 428 ms` (the whole budget) | Pass: fails at once, old file kept |
| **L4** a handle with full sharing blocks the swap | `L4_a_settings_save_succeeds_while_another_program_holds_the_file_with_full_sharing` | **Fail**: `Assert.True() Failure` (save returned false) | Pass |
| | `L4_a_theme_save_succeeds_while_another_program_holds_the_file_with_full_sharing` | **Fail**: `UnauthorizedAccessException: Access to the path is denied.` at `FileSystem.MoveFile` | Pass: saved, `.bak` holds the previous version |
| **L4** a blocked settings save is dropped silently | `L4_a_settings_save_that_cannot_happen_is_reported_not_dropped` (holder without `FILE_SHARE_DELETE`) | **Fail**: `Assert.Contains() Failure: Filter not matched in collection` `Collection: []` (nothing logged) | Pass: false, logged with the path, file unchanged |
| **L5** temp files from a crash stay | `L5_a_save_removes_old_temporary_files_left_by_a_crash_and_nothing_else` | **Fail**: `Collections differ`: both two-hour-old temp files still there | Pass: the two stale ones are gone; a fresh one, `theme.json.notes.tmp` and another file's temp are kept |
| | `L5_reading_settings_removes_old_temporary_files_left_by_a_crash` | **Fail**: `Expected: ["settings.json"]` / `Actual: ["settings.json", "settings.json.d42c...tmp"]` | Pass |
| **L6** caller not told about corrupt settings | `L6_a_corrupt_settings_file_is_reported_to_the_caller_with_where_it_was_kept` | **Fail**: `Expected: Corrupt` / `Actual: Loaded` | Pass: `State = Corrupt`, `KeptAs` exists, `Problem` names it (shown in the editor's status bar) |
| **L7** `.bak` never read | `L7_a_damaged_theme_file_falls_back_to_the_backup_and_says_so` | **Fail**: `JsonException: Expected end of string, but instead reached end of data. Path: $.Name` | Pass: the backup's theme, `BaseFolder` = the theme folder, `FromBackup`, a message, logged |
| N2 wrong content | `N2_valid_json_that_is_not_settings_is_kept_aside_as_wrong_content_without_waiting` x4 (`"Brightness": "high"`, `null`, an array, a string) | **Fail** x4: `Expected: WrongContent` / `Actual: Loaded` | Pass x4: kept aside, logged as "not Glance settings", no wait |
| N4 corrupt copies unbounded | `N4_only_the_newest_five_corrupt_settings_copies_are_kept` | **Fail**: `Expected: 5` / `Actual: 7` | Pass |
| N4 log unbounded | `N4_the_log_is_started_afresh_when_it_gets_too_big` | **Fail**: `Assert.True() Failure` (the log kept growing) | Pass: rolled to `.old`, new log small |
| N6 `IsBusy` too broad | `N6_a_read_does_not_wait_on_errors_that_are_not_sharing_or_lock_violations` (disk full, path too long, access denied) | **Fail**: `took 1286 ms` (three budgets) | Pass: all three fail at once |

Guards added with the same commit, passing before and after: `N6_a_read_still_waits_out_a_sharing_violation`,
`L7_a_damaged_theme_file_without_a_backup_still_reports_the_error`, `L7_a_good_theme_file_is_read_as_it_is`, and the
test gap below.

**Test gap: a theme reader during saves.** `A_theme_reader_during_many_saves_never_sees_the_file_missing_or_partial`:
200 `Theme.Save` calls while another thread reads the way the runner does (`ThemeFile.Load`) every 2 ms. It asserts
that no read fails, none falls back to the backup and every read is a whole theme, that no save fails, and that only
`theme.json` and `theme.json.bak` are left. It passes before and after, so it is a guard, not a bug proof. Two things
found while writing it, both recorded in the test:
- a raw `Theme.Load` (`File.ReadAllText`) sometimes gets a **sharing violation** during the swap (the rename opens the
  old file exclusively for an instant). That is neither missing nor partial, and it is what `ReadWithRetry` exists for;
- a reader that re-opens the file with no pause at all can **starve the rename** past the budget (with 400 ms in
  tests). The runner reads once per change, so the test pauses 2 ms between reads.

### Findings without a unit test

| Finding | What changed | How it was checked |
|---|---|---|
| **M1** (editor side) | `MainWindow` reads with `AppSettings.Read()`; `_settings` is the result's settings (a stand-in when unreadable). When the window opens, the status bar shows `SettingsRead.Problem` (unreadable or damaged settings) and/or the theme-from-backup message. After a theme save or an AIDA import, a settings save that failed adds a note ("Settings were not saved: they could not be read when Glance started ..." or "... see glance.log"). | The rule itself is in Core and tested (M1 above). The editor was built, not run. |
| **L1** runner never retries | `RunnerContext` reads with `AppSettings.Read(Log)` in the constructor and on every reload. `Unreadable` sets `_rereadSettingsAt` = now + 10 s; the 2 s supervisor timer then calls `Reload()`, which keeps the settings it has until a read succeeds. | Read and built; the runner has no tests and was not started. The Core part ("unreadable" is reported, not turned into the defaults) is covered by `Settings_that_stay_unreadable_...` and the M1 test. |
| **L3** (runner) waits under `_gate` | `Reload` reads settings and the theme before `lock (_gate)` and only assigns them inside it. `Reload` runs on the UI thread only, which is also the only writer of `_settings`. | Read and built. |
| **L3** (editor) waits on the UI thread | Not moved. `Theme.Save` changes the open theme's widget paths at its end, which must happen on the UI thread (the property panel and the pusher read them), so a `Task.Run` would need the save split in two. With N6 the wait now only happens while another program really holds the file (up to one second), and read-only or other permanent errors fail at once. | The L3 test above. |
| **L4** (POSIX rename) | `AtomicFile` creates the temp file with `GENERIC_WRITE | DELETE`, writes and flushes it, then renames it through the same handle with `SetFileInformationByHandle(FileRenameInfoEx, POSIX_SEMANTICS | REPLACE_IF_EXISTS)`; falls back to `MoveFileEx(REPLACE_EXISTING | WRITE_THROUGH)` on `ERROR_INVALID_PARAMETER`/`ERROR_NOT_SUPPORTED`/`ERROR_INVALID_FUNCTION`. | The two L4 "succeeds" tests pass only with the POSIX rename (mutation below). With POSIX forced off, every other test still passes, so the fallback works too. A handle **without** `FILE_SHARE_DELETE` still blocks the rename; that case is reported (third L4 test). |
| **N5** rename durability | POSIX path: the handle is flushed again after the rename; fallback: `MOVEFILE_WRITE_THROUGH`. The design note no longer says more than that. | Cannot be tested without a power cut. |
| **N1** unused variable | `text` in the old `TryLoad` is gone (the reading code was rewritten). | Build. |
| **N8** design note | `docs/design/fix-atomic-saves.md`, section "The fix", rewritten for the code as it now is, including the watcher events (`Renamed`; with the fallback also `Deleted` first). | — |
| **N9** test time | `AtomicFile.RetryBudget` is an internal settable property; `TestTiming` sets 400 ms for the test assembly by a module initializer, and the brief/long locks in the tests are 100 ms / 1.5 s. | Suite: 13.1 s before, 9-10 s after (about 8 s on `main`). Each atomic-save test now takes at most about 0.45 s; the slowest tests in the suite are the display and power tests (2-3 s each). |

**Mutation checks** (applied to a working copy, filtered tests run, files restored and verified unchanged by `git diff`):

| Mutation | Caught by |
|---|---|
| POSIX rename disabled (always use the `MoveFileEx` fallback) | both L4 "succeeds while another program holds the file" tests; the other 58 atomic-save and theme tests still pass |
| Stand-in guard in `AppSettings.Save(path)` removed | `M1_settings_that_could_not_be_read_are_never_saved_over` |

### Not changed

- **L7, the choice:** the backup is **used**, not dropped. `ThemeFile.Load` falls back to `theme.json.bak` when
  `theme.json` does not parse; nothing on disk changes. The runner shows the backup and logs it; the editor opens it,
  keeps the real `theme.json` path, and the status bar says the backup is shown and that saving repairs `theme.json`.
  This is an automatic open with a message rather than a yes/no question: the editor has no generic confirmation
  dialog, and opening the backup changes nothing until the user saves. After that save, the damaged file becomes the
  new `.bak`.
- **N3** (race in `KeepAside`) and **N7** (a read-only `.bak` silently goes stale): not asked for in this round; left as they are.
- **xUnit1031** warnings (`Task.Wait` in tests): the tests wait on helper threads on purpose; left as they are.
- Not verified: the editor and the runner were not started; nothing was tried with a real crash, a power cut, or a
  FAT/network target (only the `MoveFileEx` fallback path was exercised, by the mutation, on NTFS).

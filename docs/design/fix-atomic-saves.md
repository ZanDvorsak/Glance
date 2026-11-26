# Fix: atomic saves, corrupt settings, reads during writes (M4, plus the export part of L3)

Branch `claude/fix-atomic-saves-54bf6d`. Findings: `docs/reviews/review-full-codebase.md`, M4 and L3 ("Export deletes
the old `.glance` before writing the new one").

## The problem

| File | Before |
|---|---|
| `settings.json` | `AppSettings.Save` wrote over the live file with `File.WriteAllText`, which truncates first. `Load` swallowed every error and returned the defaults. |
| `theme.json` | Since H1, `Theme.Save` wrote `theme.json.saving` and renamed it over `theme.json`, so it was already all or nothing, but without a flush to the disk and without a backup. |
| `.glance` export | `ThemePackage.Export` deleted the old package, then wrote the new one in place. |
| Runner reads | `Theme.Load` and `AppSettings.Load` once, no retry. A failed settings read became the defaults (`LastTheme = null`, `Brightness = 7`). |

How it went wrong:
- A crash, a power cut or an I/O error during a settings save leaves an empty or half-written `settings.json`.
  The next start silently used the defaults, and the next save destroyed whatever was left.
- The editor's save and the runner's read collide: one of them gets a sharing violation. When the runner lost, it
  switched to the defaults and briefly showed the starter theme at full brightness. When the editor lost, the save
  was silently dropped (`Save()` ignores errors).
- A failed export (for example an image that another program keeps locked) destroyed the previous `.glance` and
  left a broken zip behind.

## The fix

### `Glance.Core.Storage.AtomicFile` (new, the one helper)

- `Write(path, Action<Stream>, keepBackup)` / `WriteAllText(path, text, keepBackup)`:
  1. write to `<name>.<random>.tmp` in the same folder (`FileMode.CreateNew`), then `Flush(flushToDisk: true)`;
  2. with `keepBackup` and an existing file: copy it to `<name>.bak.<random>.tmp` (a copy, so the target keeps its
     whole content until the end);
  3. `File.Move(temp, target, overwrite: true)`: a rename on the same NTFS volume, all or nothing;
  4. rename the backup copy to `<name>.bak` (one copy; a failure here does not fail the save, the save already happened).
  On any failure before step 3 the target is untouched, both temporary files are deleted, and the error is thrown.
- Temporary names end in `.tmp` and the backup in `.bak`, so the runner's `*.json` watcher only sees the final rename.
- The rename and the backup copy are retried for about one second while the file is busy (a reader, an indexer or
  a virus scanner has it open; the rename then reports a sharing violation or "access denied").
- `ReadWithRetry(Func<T>)`: retries for about one second on busy files (`IOException` other than file/folder not found,
  `UnauthorizedAccessException`) and on `JsonException` (another program is halfway through writing the file in place).
  A missing file fails at once.

`File.Move(overwrite)` was chosen over `File.Replace`: `File.Replace` can fail halfway
(`ERROR_UNABLE_TO_MOVE_REPLACEMENT`) and leave no file under the target name. The cost is that the new file gets the
folder's default permissions and attributes instead of the old file's; that is fine for files in `%AppData%\Glance`
and for exported packages.

### Callers

- `Theme.Save` uses `AtomicFile.WriteAllText(target, json, keepBackup: true)`, so `theme.json.bak` holds the previous
  version. The H1 order is unchanged: assets are copied first, the theme file is written next, and the open theme only
  changes after both succeed; on failure the copied assets are removed and the previous theme file stays.
- `AppSettings.Save(path)` uses `AtomicFile.WriteAllText` (no backup). `Save()` still ignores errors, as before.
- `AppSettings.TryLoad(path, out settings, log)` (new) tells the cases apart:
  - missing: `true`, defaults, nothing logged (first run);
  - valid: `true`, the saved settings;
  - corrupt (does not parse, `null`, empty, zeroed), checked again for about a second in case another program is
    still writing it: logged, renamed to `settings.json.corrupt-<yyyyMMdd-HHmmss>` (a copy if it cannot be renamed),
    `true` with the defaults. The next save writes a fresh file and leaves the corrupt copy alone;
  - unreadable (still locked after the retries): logged, `false` with the defaults, and the file is left where it is.
- `AppSettings.Load(...)` = `TryLoad` that always returns the settings. Without a log, the parameterless overloads
  write to `%AppData%\Glance\glance.log` (the editor has no log of its own).
- `ThemePackage.Export` writes the zip through `AtomicFile.Write` (no backup), so a failed export keeps the previous package.
- Runner (`RunnerContext`): the theme is loaded with `AtomicFile.ReadWithRetry(() => Theme.Load(p))`; settings with
  `AppSettings.TryLoad(out var fresh, Log)`, and when that returns `false` the runner keeps the settings it already
  has instead of switching to the defaults. Corrupt-settings messages go to `runner.log`.

`Theme.Load`, `Theme.Normalize`, `ResolveAsset` and `ThemePackage.Import` are untouched (another session works on them).

## Proof, per case

Method: commit `88db92c` routes the call sites through `AtomicFile` and the new `AppSettings` overloads with the **old
behaviour** (write in place, delete then create, no retry, swallow everything). Commit `7383b55` adds
`src/Glance.Tests/AtomicSaveTests.cs` and was run against that code. The fix follows in the next commit.

"Crash mid-write" is simulated deterministically: the test opens the target the way an indexer does (sharing read
and write) and locks its first byte. A write in place then truncates the file and fails on the lock, which is what a
crash after the truncation leaves. Measured on this machine with the old code: the file was 0 bytes afterwards.

| # | Test | Before (old code) | After |
|---|---|---|---|
| 1 | `A_settings_save_that_fails_mid_write_keeps_the_previous_file` | **Fail**: `settings.json` was `""` (truncated, previous settings lost) | Pass: previous content intact, no temporary file |
| 2 | `Corrupt_settings_are_logged_kept_aside_and_never_overwritten` (4 cases: cut off, empty, `null`, zero bytes) | **Fail** x4: no `settings.json.corrupt-*` file, nothing logged; defaults returned silently | Pass x4: logged, kept aside with the original bytes, defaults used, the next save leaves the copy alone |
| 3 | `A_settings_reader_during_writes_always_sees_a_whole_file_and_no_save_is_lost` (300 saves while another thread reads) | **Fail**: the writer threw `IOException: ... being used by another process` (with `Save()` that save would have been dropped silently) | Pass: every read saw a whole file, every save landed |
| 4 | `Settings_locked_for_a_moment_are_read_once_the_lock_is_gone` (locked 300 ms) | **Fail**: `LastTheme` was `null` (defaults) | Pass: the saved settings |
| 5 | `Settings_that_stay_unreadable_are_reported_as_such_and_left_where_they_are` (locked 4 s) | **Fail**: reported success with the defaults | Pass: `false`, logged, file untouched, not mistaken for corrupt |
| 6 | `Settings_being_rewritten_in_place_by_another_program_are_not_mistaken_for_corrupt` | **Fail**: `LastTheme` was `null` (defaults) | Pass: waits, reads the finished file, nothing kept aside |
| 7 | `A_theme_save_keeps_one_backup_of_the_previous_theme_file` | **Fail**: no `theme.json.bak` | Pass: one `.bak` with the previous version, no pile-up, none on the first save |
| 8 | `A_theme_read_while_the_file_is_briefly_locked_succeeds` (the runner's theme read) | **Fail**: `IOException ... being used by another process` | Pass |
| 9 | `A_theme_read_of_a_half_written_file_waits_for_the_whole_file` | **Fail**: `JsonException ... reached end of data` | Pass |
| 10 | `An_export_that_fails_midway_keeps_the_previous_package` (an asset locked by another program) | **Fail**: the old package was replaced by a broken zip (`PK...assets/a.png`) | Pass: previous package intact, no temporary file |
| 11 | `A_theme_save_that_fails_mid_write_keeps_the_previous_file` | Pass (H1 already wrote a temporary file and renamed it) | Pass (regression guard) |
| 12 | `A_settings_save_replaces_the_file_and_leaves_nothing_else` | Pass | Pass (guard: no `.tmp`/`.bak` left for settings) |
| 13 | `Missing_settings_give_the_defaults_quietly` | Pass | Pass (guard: first run is not "corrupt") |
| 14 | `A_theme_read_of_a_missing_file_fails_at_once` | Pass | Pass (guard: no one-second wait for a missing file) |
| 15 | `An_export_over_an_existing_package_replaces_it_completely` | Pass | Pass (guard) |

Totals of the new file: before **13 failed, 5 passed** (18); after **18 passed**.

Two assertions were loosened after the first run, without changing what is proven: tests 1 and 11 first expected an
`IOException`, but a rename over a file that another process holds open reports `UnauthorizedAccessException`
("access denied"). Test 11 failed on the old code only because of that type (the file itself was kept), so it is
listed as already passing. Both now accept any exception; the content checks are the proof.

One existing test changed on purpose: `ThemeModelTests.Saving_again_to_the_same_folder_copies_nothing_extra` now
expects `theme.json` and `theme.json.bak` after two saves.

Full suite after the fix: **560 passed, 0 failed** (542 on `main` + 18 new). The new tests were run five more times
together with `ThemeModelTests`: all green each time. Build: `dotnet build src/Glance.sln -c Release -p:SkipHost=true` succeeded.

## Not covered / left open

- The flush to the disk (`Flush(true)`) and the behaviour after a real power cut cannot be tested here; only the
  crash-after-truncation case is simulated.
- Nobody reads `theme.json.bak` automatically yet. A corrupt `theme.json` (for example left by an older version)
  still makes the runner show the starter layout (logged in `runner.log`); the user can rename the `.bak` by hand.
  An automatic fallback to the backup would be a small follow-up.
- A crash between writing the temporary file and the rename leaves a `theme.json.<random>.tmp` in the folder. It is
  harmless (the watcher ignores it) but is not cleaned up.
- Saves and reads can now wait up to about one second when another program holds the file. The runner's reload runs on
  its UI thread, so a locked file can delay the tray menu by that much.
- The runner is being redesigned into a service on another branch; the two `RunnerContext` lines changed here
  (`ReadWithRetry` around `Theme.Load`, `TryLoad` keeping the current settings) need to move with it.
- Other writers in the code base (`AidaImporter` writing extracted images, the legacy-folder migration) are not
  covered; they write new files, not files the runner watches.

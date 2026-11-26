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

This section describes the code after the review fixes (`docs/reviews/review-atomic-saves.md`, section "Fixes after
review"). The proof below is for the first version; the review fixes have their own proof in the review file.

### `Glance.Core.Storage.AtomicFile` (new, the one helper)

- `Write(path, Action<Stream>, keepBackup)` / `WriteAllText(path, text, keepBackup)`:
  1. delete temporary files of this name left by a crash (`<name>.<32 hex>.tmp`, `<name>.bak.<32 hex>.tmp`) that are
     older than an hour (`CleanUpStale`; a younger one may belong to a save running in another process);
  2. with `keepBackup` and an existing file: copy it to `<name>.bak.<32 hex>.tmp`, **best effort**: one try, no waiting,
     and if it fails (the old file cannot be read, the disk is full) the save goes ahead without a backup;
  3. write to `<name>.<32 hex>.tmp` in the same folder (created new, opened with the DELETE right), then
     `Flush(flushToDisk: true)`;
  4. rename it over the target **through its own handle with POSIX semantics** (`SetFileInformationByHandle`,
     `FileRenameInfoEx`, `FILE_RENAME_FLAG_POSIX_SEMANTICS | FILE_RENAME_FLAG_REPLACE_IF_EXISTS`), then flush the handle
     again so the rename reaches the disk soon. Where Windows or the file system does not support that (before
     Windows 10 1709, FAT, some network shares: `ERROR_INVALID_PARAMETER`, `ERROR_NOT_SUPPORTED`,
     `ERROR_INVALID_FUNCTION`), fall back to `MoveFileEx(MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)`;
  5. rename the backup copy to `<name>.bak` (one copy; a failure here does not fail the save, the save already happened).
  On any failure before step 4 completes, the target is untouched, the temporary files are deleted, and the error is thrown.
- With POSIX semantics, a program that holds the old file open **and allows deleting it** (`FILE_SHARE_DELETE`, common
  for indexers, sync and backup tools) no longer blocks the save; it keeps reading the old, now nameless, file. A handle
  that does not allow deleting (for example `File.ReadAllText`) still blocks the rename.
- Only errors that can clear by waiting are retried, for `RetryBudget` (one second; tests set 400 ms):
  - the rename: sharing and lock violations, and "access denied" unless the target is read-only (a file that another
    program holds open reports "access denied" to a rename; a read-only target says the same and fails at once);
  - `ReadWithRetry(Func<T>)`: sharing and lock violations (`HRESULT` `0x80070020`, `0x80070021`) and `JsonException`
    (another program is halfway through writing the file in place). A missing file, "access denied", disk full or a path
    that is too long fail at once.
- Temporary names end in `.tmp`, the backup in `.bak`, kept-aside settings in `.corrupt-<time>`, so the runner's
  `*.json` watcher does not see them. One save raises a `Renamed` event (temp name to `theme.json`). With the
  `MoveFileEx` fallback, a `Deleted` event for the old file comes just before it (measured by the reviewer). The runner
  does not subscribe to `Deleted`; anyone who adds such a handler must expect it on every save.

A rename was chosen over `File.Replace`: `File.Replace` can fail halfway (`ERROR_UNABLE_TO_MOVE_REPLACEMENT`) and
leave no file under the target name. The cost is that the new file gets the folder's default permissions and
attributes instead of the old file's; that is fine for files in `%AppData%\Glance` and for exported packages.

### Callers

- `Theme.Save` uses `AtomicFile.WriteAllText(target, json, keepBackup: true)`, so `theme.json.bak` holds the version
  before the last save. The H1 order is unchanged: assets are copied first, the theme file is written next, and the
  open theme only changes after both succeed; on failure the copied assets are removed and the previous theme file stays.
- `ThemeFile.Load(path, log)` (new) is how the editor and the runner read a theme: `Theme.Load` with `ReadWithRetry`;
  when `theme.json` is damaged (`JsonException`, `InvalidDataException`) and `theme.json.bak` can be read, the backup is
  returned with `FromBackup = true` and a message, and nothing on disk changes. The editor shows the message in its
  status bar and keeps the real `theme.json` path, so the next save repairs it; the runner logs it and shows the backup.
- `AppSettings.Read(path, log)` (new) returns a `SettingsRead` (`Settings`, `State`, `KeptAs`, `Problem`):
  - `Missing`: the defaults, nothing logged (first run);
  - `Loaded`: the saved settings;
  - `Corrupt` (not valid JSON: cut off, empty, zeroed), checked again for up to a second in case another program is
    still writing it, and `WrongContent` (valid JSON that is not settings: wrong types, `null`, an array), not waited
    for: logged, renamed to `settings.json.corrupt-<yyyyMMdd-HHmmss>` (a copy if it cannot be renamed; the newest five
    are kept), the defaults are used, and `KeptAs`/`Problem` say where the file went;
  - `Unreadable` (still locked after the retries): logged, and the defaults are returned as a **stand-in** that refuses
    to be saved, so the file keeps the user's settings (review M1).
  `Load` and `TryLoad` remain as wrappers (`TryLoad` is false only for `Unreadable`).
- `AppSettings.TrySave(path, log)` saves all or nothing and returns false (and logs why) when it could not; `Save()`
  does the same for `%AppData%\Glance\settings.json` and returns the result. `Save(path)` throws, including
  `InvalidOperationException` for a stand-in.
- Without a log, the parameterless overloads write to `%AppData%\Glance\glance.log`; past 512 KB it is renamed to
  `glance.log.old` and started afresh.
- Editor (`MainWindow`): reads settings once with `AppSettings.Read()`. A damaged or unreadable settings file, and a
  theme shown from its backup, are reported in the status bar when the window opens. A save that could not save the
  settings says so after "Saved ..." (and why). The theme save itself still runs on the UI thread: `Theme.Save` changes
  the open theme's widget paths at the end, which must happen on the UI thread, and with the narrower retries it only
  waits when another program really holds the file.
- Runner (`RunnerContext`): reads settings and the theme **before** taking its lock and only swaps them in under it,
  so a power event never waits behind a busy file. When settings are unreadable, at start or on a reload, it keeps the
  settings it has (the defaults at start) and reads again 10 s later from its supervisor timer, instead of waiting for
  the next file change. Messages go to `runner.log`.
- `ThemePackage.Export` writes the zip through `AtomicFile.Write` (no backup), so a failed export keeps the previous package.

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

(Updated after the review fixes.)

- The flush to the disk and the rename after a real power cut cannot be tested here; only the crash-after-truncation
  case is simulated. The rename is flushed (POSIX path: the handle is flushed after the rename; fallback:
  `MOVEFILE_WRITE_THROUGH`), which is as far as Windows lets a program go.
- A program that holds `theme.json` or `settings.json` open **without** allowing deletes still blocks a save for the
  retry budget; the save then fails and says so (theme: "Save failed"; settings: a note after "Saved ..." and a line
  in `glance.log`).
- After the editor shows a theme from `theme.json.bak` and the user saves, the damaged `theme.json` becomes the new
  `.bak` (the good content is then in `theme.json`).
- The editor's theme save still runs on the UI thread (see "Callers"); it can wait up to a second while another
  program holds the file.
- Review N3 (a microsecond race in which a just-saved good settings file is moved aside) and N7 (a read-only `.bak`
  goes stale without a log line) are not changed.
- The runner is being redesigned into a service on another branch; the `RunnerContext` changes here (`ReadTheme` with
  `ThemeFile.Load`, `Reload` reading before the lock, `NoteSettingsRead`/`RereadLockedSettings`) need to move with it.
- Other writers in the code base (`AidaImporter` writing extracted images, the legacy-folder migration) are not
  covered; they write new files, not files the runner watches.

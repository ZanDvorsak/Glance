# Code review: Save As keeps the theme's assets (fix for H1)

**Date:** 2026-11-26
**Branch:** `claude/fix-saveas-assets-ee5527` (one commit, `736133d`, on top of `main` at `0a9f10b`)
**Scope:** `src/Glance.Core/Themes/Theme.cs` (`Save`, new `SameFolder`) and four new tests in `src/Glance.Tests/ThemeTests.cs`.
**Finding being fixed:** H1 in `docs/reviews/review-full-codebase.md`: Save As to another folder left the theme's images, videos and fonts
behind, because `Adopt` copied only absolute paths.
**Reviewer:** independent; did not write the change. No code was changed by this review.

Nothing here was tested in the running editor or on the physical display. Everything below comes from reading the code, the test suite,
four temporary mutations and two temporary probe tests (all reverted; `git status` was clean afterwards).

---

## Verdict: merge after fixes

The change does fix H1. On the normal path (the old folder exists, the files are there, the new folder is empty) Save As now copies
every relative image, video and font, on every page, into the new folder, and the new theme no longer depends on the old one. The rule is
small, it reads well, and the tests are good: each of four mutations I tried is caught by at least one test.

Two medium issues should be fixed before merging, or straight after as the first follow-up. Neither is new code; both are older patterns in `Save` that
this change now reaches far more often, because every relative asset now goes through the copy path on Save As:

- **M1.** If one copy fails partway, the open theme is left half-rewritten. The next Ctrl+S writes the broken paths into the
  *original* theme. I confirmed this with a probe test.
- **M2.** Two different files with the same name and size are treated as one, so Save As can silently show the wrong image. I confirmed this with a probe test.

Both fixes are small (about 20 lines in `Save` plus two tests).

## Build and test results

Run in the worktree:

| Command | Result |
|---|---|
| `dotnet build src/Glance.sln -c Release -p:SkipHost=true` | **Succeeded**, 0 errors, 30 warnings (all were already on `main`: CA1416, CS8602, CS0618 obsolete Skia calls) |
| `dotnet test src/Glance.Tests -c Release` | **Passed 257 / 257**, about 2 s |

**Mutation check.** I made each change to `Theme.cs` on its own, ran the full suite, then restored the file byte for byte (same hash):

| Mutation | Caught by |
|---|---|
| Back to the old rule: relative paths never copied (`if (!rooted) return path;`) | `Save_as_to_another_folder_takes_every_image_video_and_font_along`, `Save_as_keeps_two_different_files_with_the_same_name_apart` |
| Same-folder short-cut removed | `Saving_again_to_the_same_folder_copies_nothing_extra` |
| `FontFile` no longer adopted | `Save_as_to_another_folder_...`, `FontTests.Saving_copies_an_absolute_font_file_into_the_theme_assets` |
| Size check in the name-clash loop removed (`while (false)`) | `Save_as_keeps_two_different_files_with_the_same_name_apart` |

**Probe tests.** I added two temporary tests in a throw-away file, ran them, and deleted the file. Their output is quoted under M1 and M2.

**Merge check with the sibling branches.** I ran `git merge-tree --write-tree HEAD <branch>` against each sibling. None has a textual conflict:

| Branch | Overlap with this change |
|---|---|
| `claude/fix-display-loop-693991` | Also edits `Theme.cs`, but only `ApplySnapshot` (a separate hunk). No conflict, no semantic overlap. |
| `claude/fix-live-empty-folder-5aa7c3` | Edits `MainWindow.PushLive`. No conflict. See the nit N5 about live preview after Save As. |
| `claude/fix-ffmpeg-elevation-a3dfcc`, `claude/fix-autostart-task-0011d2`, `claude/feature-shutdown-blank-60ded2` | Touch none of these files. |

---

## Findings

### Medium

#### M1. A failed copy leaves the open theme half-rewritten, and the next save breaks the original theme
- **Where:** `src/Glance.Core/Themes/Theme.cs:91-96`. The loop assigns each widget's new path as it goes, and `BaseFolder = dir` runs only after
  the loop. The caller is `src/Glance.App/MainWindow.cs:801-809`.
- **Evidence:** `Adopt` returns a path relative to the **new** folder, and the loop writes it into the widget at once. If a later `File.Copy`
  throws (file locked, disk full, access denied, path too long), the exception leaves `Save` with some widgets already pointing at
  `assets/<new name>` while `BaseFolder` is still the **old** folder. `SaveAsync` only shows "Save failed" and keeps `_themePath` pointing at the old file.
  Probe test (old folder A has `a.png` and `locked.png`; B already has a different-size `a.png`; `locked.png` is held open with `FileShare.None`):
  ```
  Save threw IOException
  BaseFolder=A (old); w0=assets/a_1.png exists=False; w1=assets/locked.png
  after Ctrl+S to A: w0=assets/a_1.png exists=False
  ```
- **Failure scenario:** the user does Save As of a theme with a large video to a nearly full disk, or the video is locked. The status bar says the save
  failed. The user shrugs and presses Ctrl+S. That writes `A\theme.json` with `assets/a_1.png`, a file that exists only in B or nowhere.
  The original theme now has a missing background. The property changes also feed the undo timer, so the broken paths become an undo step.
  This pattern already existed for absolute paths. The fix makes it much more likely, because Save As now copies every asset, including
  the largest files a theme has (videos).
- **Remedial action:** make `Save` all-or-nothing for the in-memory theme. Either:
  1. Two phases: first build a `Dictionary<string, string>` from old value to new value for every `ImagePath`/`FontFile` and copy every file
     (copies can be undone by deleting the files this call created). Only when every copy has succeeded, assign the new paths and `BaseFolder`, then write the JSON.
     Or:
  2. Do what `ThemePackage.Export` already does: work on a clone (`new Theme { BaseFolder = BaseFolder }` + `ApplySnapshot(ToSnapshot())`),
     adopt and write the clone, and copy the paths back into `this` only after `File.WriteAllText` succeeds.

  Add a test that holds one source open with `FileShare.None`, asserts that `Save` throws, and asserts that every widget path and `BaseFolder`
  are unchanged afterwards.

#### M2. Same name and same size counts as the same file, so Save As can silently use the wrong image
- **Where:** `src/Glance.Core/Themes/Theme.cs:83-88` (the clash loop compares only `FileInfo.Length`). The same heuristic is L2 in the
  full-codebase review.
- **Evidence:** probe test. A has `assets/bg.bmp` = `[1,1,1]`; B already has its own `assets/bg.bmp` = `[2,2,2]`. A also has `assets/x/p.png` = `[3,3]`
  and `assets/y/p.png` = `[4,4]`. After `Save` to B:
  ```
  assets/bg.bmp -> [2,2,2]     (A's background now shows B's picture)
  assets/p.png  -> [3,3]
  assets/p.png  -> [3,3]       (the second image became the first one)
  ```
- **Failure scenario:** before this change, the heuristic only met files the user had just picked with Browse. Now every relative asset meets it on
  every Save As. Equal sizes are common for fixed-size formats: an uncompressed 480x320 BMP is always the same number of bytes, and imported
  AIDA64 themes often use BMP. The editor's Save dialog also starts in the `Themes` root (`MainWindow.cs:795`), so themes saved there as
  `Themes\X.json` all share one `Themes\assets` folder. That makes name clashes between themes likely. There is no error; the theme just shows another
  theme's picture, and the original file is never copied.
- **Remedial action:**
  - When the sizes match, compare the contents (byte by byte, or a SHA-256 of both files) before reusing an existing file. Otherwise pick the next `_n` name.
  - Within one `Save`, keep a `source full path -> dest` map, so one source is copied once and two different sources never share one dest.
    `ThemePackage.Export` already uses a `used` dictionary for this.
  - Add tests for "same name, same size, different bytes" both in the destination folder and between two subfolders of the source.

### Low

#### L1. Undo after Save As can bring back paths that the new folder does not have
- **Where:** `src/Glance.App/MainWindow.cs:804` (`_undo.Rebase`) and `src/Glance.Core/Editing/UndoManager.cs:20`. `Rebase` replaces only the
  baseline and keeps the whole undo stack.
- **Evidence:** undo snapshots taken before the Save As hold paths relative to folder A. After the save they resolve against B.
- **Failure scenario:**
  - The user deletes an image widget, does Save As to B, then presses Ctrl+Z. The widget comes back with `assets/old.png`, which was never
    copied to B because nothing referenced it at save time, so it shows as a missing image.
  - Or a file was renamed during the save (`bg.png` became `bg_1.png` because of a clash). An older snapshot then says `assets/bg.png`, and in B that is a different file.

  Before this change, Save As also rewrote only absolute paths, which still resolve after undo, so this is new.
- **Remedial action:** when the save changed the folder (the old `BaseFolder` differs from the new one), call `_undo.Reset(...)` instead of
  `Rebase`. Or have `Save` return the folder change and let the editor decide. A cheaper fallback is to say in the status bar that undo
  history was cleared by Save As.

#### L2. Save As now copies every video on the UI thread
- **Where:** `Theme.cs:88` (`File.Copy`), called synchronously from `MainWindow.SaveAsync` on the UI thread.
- **Evidence:** this is M7 in the full-codebase review. The change makes it hit on every Save As to a new folder: all assets are copied, not only newly
  browsed ones.
- **Failure scenario:** a theme with a 500 MB video background freezes the editor for several seconds on Save As. AGENTS.md asks that the UI
  thread is not blocked by slow work.
- **Remedial action:** run `Save` in `Task.Run` from `SaveAsync`. Snapshot the theme first, or disable editing during the save, because `Save`
  mutates widgets. The clone-based design from M1 option 2 makes this safe. Show "Saving..." in the status bar.

#### L3. Save As now copies files that relative `..` paths point at
- **Where:** `Theme.cs:80` (`ResolveAsset(path)` with no containment check).
- **Evidence:** a theme from someone else can contain `"ImagePath": "../../../Users/me/Documents/secret.png"`. Before this change, Save As left
  that as a reference. Now Save As copies the file into the new theme's `assets` folder, where a later **Export** packs it.
  `ThemePackage.Export` already follows `..` paths by itself, so there is no new leak through export. The new part is that a private file gets
  copied into the theme folder without notice. This belongs with M2 in the full-codebase review (paths inside `theme.json` are not validated).
- **Remedial action:** fix it where M2 says, by cleaning widget paths on import. In `Adopt`, also consider not following relative paths that
  resolve outside the old `BaseFolder`: leave them as they are, or log them.

### Nits

- **N1. Save As flattens subfolders.** `Theme.cs:83-89` always writes `assets/<file name>`. So `assets/fonts/digits.ttf` becomes `assets/digits.ttf` on
  Save As but stays put on a same-folder save. That is harmless by itself, but it is what turns M2's subfolder case into a real clash.
  Either keep the path below `assets/` when the source is inside the old `assets` folder, or say in the doc comment that Save As flattens.
- **N2. `SameFolder` compares strings, not folders** (`Theme.cs:100-104`). A junction, a `subst` drive or an 8.3 short name makes the same folder look
  different. The result is benign: files directly under `assets/` resolve to the same path, so `dest == source`, the sizes match and nothing is copied.
  Files in subfolders get a flattened duplicate. A comment saying this is deliberate would help the next reader.
- **N3. The doc comment leaves out the `BaseFolder == null` case** (`Theme.cs:62-66`). When a theme has never been saved, relative paths cannot be
  resolved and are kept as they are. That is correct, but the comment says relative paths "are copied too when the theme moves".
- **N4. The `path` parameter is shadowed.** The local function `Adopt(string path)` hides `Save(string path)` (`Theme.cs:67,75`). This was already there,
  but the function grew, and `source`/`path`/`dest` now all appear in it. Renaming it to `stored` or `value` would make it easier to read.
- **N5. Live preview keeps the old folder after Save As (integration with `fix-live-empty-folder`).** `MainWindow.PushLive` (`MainWindow.cs:1335`)
  skips a send when the JSON snapshot is unchanged, and the snapshot does not include `BaseFolder`. A Save As that renames nothing leaves the
  runner's live theme resolving against folder A. That works until A is deleted. If the live sender is reworked (H2/M6), include the folder in the
  "changed?" key.
- **N6. Test `Saving_again_to_the_same_folder_copies_nothing_extra`** (`ThemeTests.cs:191-212`) checks the paths on `t`, the first instance,
  not on the theme reloaded and saved the second time. Reading `theme.json` back after the second save would test what is actually on disk.
- **N7. Test `Save_as_keeps_two_different_files_with_the_same_name_apart`** (`ThemeTests.cs:170-188`) passes only because the two files have
  different sizes (1 and 2 bytes). Its name promises more than the code delivers (see M2). Give the files the same size once M2 is fixed.
- **N8. Stale copies pile up.** Saving As repeatedly into the same existing folder leaves `bg_1.png`, `bg_2.png`, ... that no theme references.
  No action needed now; a later "clean unused assets" command could remove them.

---

## Test gaps

| Missing test | Why it matters |
|---|---|
| A copy fails partway (source locked with `FileShare.None`) and the in-memory theme is unchanged | M1 |
| Same name, same size, different bytes: in the destination, and between two source subfolders | M2 |
| Save As into a folder that already holds another theme's `assets` | M2, and the real Save dialog defaults to the shared `Themes` root |
| Relative path with `BaseFolder == null` (a theme never saved) | Documents the "left as is" rule (N3) |
| Relative path containing `..` | L3, locks in whatever rule is chosen |
| Absolute path that already points inside the new `assets` folder | Already works (`dest == source`); a test would keep it that way |
| Undo after Save As | L1. This is `Glance.App`, which has no tests. A Core-level helper ("did the folder change?") could be tested. |

## What was checked and found sound

- **It fixes H1 as the remedial action asked.** Relative paths are resolved with `ResolveAsset` *before* `BaseFolder` is reassigned
  (`Theme.cs:80` runs inside the loop, `BaseFolder = dir` at line 96), so they resolve against the old folder. The two tests
  (`...takes_every_image_video_and_font_along` and the mutation "back to the old rule") show this.
- **Every page and both path kinds.** The loop walks `Pages.SelectMany(p => p.Widgets)` and adopts both `ImagePath` and `FontFile`. The first test covers two
  pages, an image, a video, a font, an absolute file from outside and a file used twice, and compares the bytes of each.
- **The old folder is left alone.** It copies, never moves. This is asserted.
- **Same-folder saves are unchanged.** Relative paths are left as they are, including subfolders. The normalisation is case-insensitive and ignores a
  trailing slash, and the test uses a trailing slash on `BaseFolder`. Ctrl+S on an existing theme therefore costs no file I/O.
- **Missing files** are left as they are, and no empty `assets` folder is created. This is tested.
- **Absolute paths behave as before.** `FontTests` and `Save_bundles_images_next_to_the_theme_...` still pass.
- **Assets are written before `theme.json`.** A runner that reloads on the file change will find its files.
- **Other callers are unaffected.** The AIDA import saves into the folder it imported into, so it is a same-folder save. `.glance` import
  loads from its extraction folder. `ThemePackage.Export` does not call `Save`.
- **No JSON format change.** No new properties were added, so saved themes stay compatible.
- **Conventions.** The code is in `Glance.Core` and tested there, with nullable-clean code and no new warnings. The commit message explains what changed and why, and records the mutation check.

---

## Proof: the bug existed and is fixed

The branch's new tests were run against the **unfixed** code on `main` (`0a9f10b`, with only `ThemeTests.cs` taken from the branch) and then on the branch (`736133d`):

```
dotnet test src/Glance.Tests -c Release --filter "FullyQualifiedName~Save_as|FullyQualifiedName~Saving_again"
```

| Test | `main` (unfixed) | Branch (fixed) |
|---|---|---|
| `Save_as_to_another_folder_takes_every_image_video_and_font_along` | **Failed**: expected `bg.png, clip.mp4, digits.ttf, photo.jpg` in the new `assets`, actual `photo.jpg` only | Passed |
| `Save_as_keeps_two_different_files_with_the_same_name_apart` | **Failed**: both widgets ended up on `assets/pic.png` | Passed |
| `Saving_again_to_the_same_folder_copies_nothing_extra` | Passed (regression guard) | Passed |
| `Save_as_leaves_missing_files_as_they_are` | Passed (regression guard) | Passed |

On `main` the result was `Failed: 2, Passed: 2, Total: 4`; on the branch it was `Passed: 4, Total: 4`, with the full suite at 257/257. The first failure is the reported bug itself: only the one absolute-path asset was copied, and every relative asset was left behind.

---

## Fixes after review

**Added by the fixing session on 2026-11-26.** The reviewer's findings above are unchanged. Fixed: M1, M2, L1, N6, N7. Also covered: N3 and N2
(doc comment and code comment) and N4 (the `Adopt` parameter is now `stored`). Left open on purpose, as tracked separately: L2 (copying on
the UI thread), L3 (`..` paths) and N1 (Save As flattens subfolders; now stated in the doc comment). N5 and N8 are unchanged.

### How it was proven

The rule was: test first, run it red on the branch's code, fix, run it green, then run the full suite.

1. **Behaviour-preserving refactor (L1 only).** The undo decision lived in `MainWindow`, which has no tests. It moved into Core
   without changing behaviour: `Theme.Save` now returns `true` when the theme moved from one folder to another, and the new
   `UndoManager.AfterSave(snapshot, themeMoved)` at first did exactly what the editor did before (`Rebase`). `MainWindow.SaveAsync` calls it.
2. **Red.** I added the new tests and changed the two existing ones (N6, N7), then ran the whole suite on the branch code
   (`fbe9400` plus the refactor above, no fix):
   `Failed: 6, Passed: 258, Total: 264`. Each of the six failures is a finding. The three guard tests passed, as they should.
3. **Fix**, then **green**: `Passed: 264, Total: 264`. `dotnet build src/Glance.sln -c Release`: 0 errors, 28 warnings (all already on `main`).
4. **Extra mutation check.** Removing the rollback that deletes the files a failed save copied makes both M1 tests fail. The code was restored afterwards.

### M1: a failed save left the open theme half-changed

**Fix (`Theme.Save`):** the save now runs in three phases.
- (1) Copy every file and work out the new paths. The theme is not touched.
- (2) Write the new JSON from a copy of the theme to `theme.json.saving`, then swap it into place with `File.Move(..., overwrite: true)`.
- (3) Only then set the widget paths and `BaseFolder`.

If any step throws, the temporary file and every file this call copied are deleted, and an `assets` folder this call created is removed
if it is empty. The previous `theme.json` stays as it was, and the exception goes to the caller as before (`SaveAsync` shows "Save failed").
The temporary name does not end in `.json`, so the runner's `*.json` watcher ignores it. The runner already reloads on `Renamed`
(`RunnerContext.cs:132`), so the swap still triggers a reload.

| Test | Before (branch code) | After |
|---|---|---|
| `A_save_that_fails_while_copying_leaves_the_open_theme_and_the_target_unchanged` (the reviewer's probe: `locked.png` held with `FileShare.None`, and B already has a different `a.png`) | **Failed:** `Assert.Equal() Failure: Strings differ` at `"ImagePath": "assets/a.png"`; the actual value was `"assets/a_1.png"`, so the open theme was half-rewritten | Passed |
| `A_save_that_fails_while_writing_the_theme_file_leaves_the_open_theme_unchanged` (the target `theme.json` is read-only, so the copies succeed and the write fails) | **Failed:** `BaseFolder` expected `...\A`, actual `...\B` | Passed |

### M2: same name and same size counted as the same file

**Fix:** an existing file in `assets` is reused only when it is the same file or has the same bytes (the sizes are compared first, then the
content, in 80 KB chunks). Otherwise the next `_n` name is used. Within one save, a `source -> stored path` map copies each source once.

| Test | Before | After |
|---|---|---|
| `Save_as_does_not_reuse_a_different_file_of_the_same_name_and_size_in_the_target` (`bg.bmp` = `[1,1,1]` in A, `[2,2,2]` already in B) | **Failed:** expected `[1, 1, 1]`, actual `[2, 2, 2]` (A's background showed B's picture) | Passed |
| `Save_as_keeps_same_named_files_from_different_subfolders_apart` (`assets/x/p.png` = `[3,3]`, `assets/y/p.png` = `[4,4]`) | **Failed:** expected `[4, 4]`, actual `[3, 3]` | Passed |
| `Save_as_keeps_two_different_files_with_the_same_name_apart` (N7: now one byte each, so the sizes match) | **Failed:** `Assert.NotEqual() Failure: Strings are equal`, both `"assets/pic.png"` | Passed |
| `Save_as_reuses_an_identical_file_already_in_the_target` (guard: identical bytes are still reused, so no `bg_1.bmp`) | Passed | Passed |

### L1: undo after Save As could bring back paths the new folder lacks

**Decision: clear the undo history when the save moves the theme to another folder.** A plain save to the same folder keeps the history,
as before.
- **Why not keep the history?** Old snapshots hold paths relative to the old folder. Keeping them valid would mean copying every file
  that any snapshot refers to (including deleted widgets' files) and rewriting every snapshot after any renames. That is complex and
  copies files the user removed.
- **Why clearing is safe:** it is what many editors do on Save As, and the old theme is still on disk in its old folder.

The status bar now says `Saved <path> (undo history cleared: the theme now lives in a new folder)`, and the undo/redo buttons are refreshed.
A theme that was never saved (`BaseFolder == null`) does not count as moved: its relative paths could not be resolved before either, and its
absolute paths still work, so its history is kept.

| Test | Before | After |
|---|---|---|
| `After_save_as_to_another_folder_undo_cannot_bring_back_files_the_new_folder_lacks` (delete a widget, Save As to B, undo) | **Failed:** `undo brought back assets/old.png, which is not in the new folder` | Passed |
| `Saving_to_the_same_folder_keeps_the_undo_history` (guard) | Passed | Passed |

### Test nits

- **N6:** `Saving_again_to_the_same_folder_copies_nothing_extra` now also reloads `theme.json` after the second save and checks the paths that were written to disk.
  It passed before and after; it is a regression guard, not a bug.
- **N7:** covered in the M2 table above.

### Not verified

- The editor itself was not run. The status-bar text and the undo buttons after Save As are untested UI code. They are a few lines in
  `MainWindow.SaveAsync`; the decision itself is tested in Core.
- `File.Move` replacing `theme.json` was checked against the runner's watcher code by reading it, not by running the runner.
- M4 in the full-codebase review (atomic writes) partly overlaps: `Theme.Save` now writes through a temporary file. If another branch
  changes `Theme.Save` for M4, expect a conflict in this method.

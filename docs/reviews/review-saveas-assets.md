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
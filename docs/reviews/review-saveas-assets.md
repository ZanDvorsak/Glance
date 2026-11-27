# Review: Save As keeps the theme's assets (fix for H1)

- **Verdict:** merge after fixes — **now:** M1, M2 and L1 fixed; L2 and L3 still open; merged into main
- **Build/tests:** 264/264 pass on the branch after the fixes (build: 0 errors)
- **Proof:**
  - Before: on unfixed `main`, `Save_as_to_another_folder_takes_every_image_video_and_font_along` failed (only `photo.jpg` was copied; every relative asset was left behind)
  - Before: on `main`, `Save_as_keeps_two_different_files_with_the_same_name_apart` failed (both widgets ended up on `assets/pic.png`); the review-fix tests failed 6 of 264 on the branch before the fixes
  - After: 4/4 new tests pass on the branch; after the review fixes, 264/264

## Findings
- **Medium M1** — a copy that fails partway left the open theme half-rewritten, so the next Ctrl+S broke the original theme (`Theme.cs:91-96`) → `Save` copies first, writes `theme.json` through a temp file, and only then changes the paths; a failure deletes what it copied — **fixed**
- **Medium M2** — two different files with the same name and size counted as one, so Save As could silently show the wrong image (`Theme.cs:83-88`) → an existing file is reused only when its bytes match; each source is copied once per save — **fixed**
- **Low L1** — undo after Save As could bring back paths the new folder does not have (`MainWindow.cs:804`) → undo history is cleared when the theme moves to another folder, and the status bar says so — **fixed**
- **Low L2** — Save As copies every file, videos included, on the UI thread, so big themes freeze the editor (`Theme.cs:88`) → run `Save` in `Task.Run` — **open**
- **Low L3** — Save As now copies files that `..` paths point at, from outside the theme folder (`Theme.cs:80`) → clean widget paths on import; do not follow paths outside the old folder — **open**
- Nits: fixed: N2 and N3 (comments), N4 (`Adopt` parameter renamed), N6 (test reads `theme.json` back), N7 (test uses same-size files); open: N1 (Save As flattens subfolders, now documented), N5 (live preview keeps the old folder after Save As), N8 (unused `_1`, `_2` copies pile up)

## Manual checks for the user
- In the editor, Save As a theme with images, a video and a font into a new folder; reopen it and check everything shows.
- Right after that Save As, check the status bar says the undo history was cleared and Undo is greyed out.
- With the runner running, save a theme and check the display reloads it (`theme.json` is now replaced by a rename).

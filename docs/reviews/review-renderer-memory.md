# Code review: renderer caches (M10) and graph history seeding (M11)

**Date:** 2026-11-26
**Branch:** `claude/fix-renderer-memory-8913bc` (one commit, `750aeb0`, on top of `main` at `9bf03b0`)
**Scope:** `src/Glance.Core/Themes/ThemeRenderer.cs` (image and font-file caches, budget, file stamps, graph seeding),
`src/Glance.Core/Media/AnimatedImage.cs` (scaled decode, frame cleanup), `src/Glance.Core/Sensors/SensorHistory.cs`
(ordered seeding, copied window), `src/Glance.App/MainWindow.cs` (four `ClearImageCache` calls removed), 19 new tests in
`src/Glance.Tests/RendererMemoryTests.cs`, and the author's proof in `docs/design/fix-renderer-memory.md`.
**Findings being fixed:** M10 and M11 in `docs/reviews/review-full-codebase.md`.
**Reviewer:** independent; did not write the change. No code in the branch was changed by this review.

Nothing here was checked in the running editor or on the physical display. Everything below comes from reading the code, the
test suite, a scratch worktree of `main` for the before/after proof, seven temporary mutations, and nine temporary probe tests
(all in the scratch worktree, which has been removed).

---

## Verdict: merge after fixes

The change fixes what M10 and M11 describe, and the proof holds up: I reproduced the author's "13 of 19 fail on the old code" exactly,
and independently confirmed the same bugs on untouched `main` with tests that use only public API and reflection. Typeface cleanup,
the file-stamp cache, the missing-file retry, the scaled decode and the M11 ordering are all correct in the cases I could think of,
and the downscaled pictures look the same as before (a checkerboard probe gives the same flat grey on both branches).

Two medium problems are new with this change and should be fixed before merging. Both are small:

- **M1.** The 96 MB budget is handed out first come, first served. A second animation on the same page can be cut to a single
  frame and then stays frozen. One long full-screen GIF is cut to about 160 frames, silently. Confirmed with a probe.
- **M2.** Pictures are decoded at the widget's size in *theme* pixels, but the display pusher scales the theme to fit the panel.
  A 320 x 240 theme on the 480 x 320 LCD now gets its pictures decoded at 320 px and blown up by 1.33, so they look softer than on
  `main`, which always decoded at full size. Confirmed with a probe.

The rest are low-severity polish and test gaps.

## Build and test results

Run in the branch worktree:

| Command | Result |
|---|---|
| `dotnet build src/Glance.sln -c Release -p:SkipHost=true` | **Succeeded**, 0 errors, 29 warnings (all of kinds already on `main`: CA1416, CS0618, CS8602, CS9057, xUnit analyzers) |
| `dotnet test src/Glance.Tests -c Release` | **Passed 561 / 561**, about 8 s (542 existing + 19 new) |

## Proof verification

The user's rule is that every bug must be shown to exist, then shown fixed. I checked both halves in a scratch worktree of `main`
(`git worktree add --detach <scratch> 9bf03b0`), removed afterwards.

**1. The author's tests that compile against `main` unchanged.** 5 of the 19 new tests use only API that already exists on `main`
(`ThemeRenderer.Now` was already an internal test clock). I copied the test file in with the other 14 tests compiled out:

| Test | On `main` |
|---|---|
| `An_image_replaced_on_disk_is_drawn_again_without_clearing_the_cache` | **Fail**: expected `#ff00ff00`, got `#ffff0000` (old picture forever) |
| `A_missing_image_is_tried_again_after_a_while` | **Fail**: got the placeholder `#ff282828` forever |
| `Seeding_after_real_samples_keeps_time_order` | **Fail**: "the window must stay oldest first" |
| `Window_returns_a_copy_not_the_live_list` | **Fail**: the returned list grew to 2 items |
| `Disposing_one_renderer_leaves_installed_fonts_working_for_another` | Pass (guard, as the author says) |

**2. The other 14 need test hooks.** They read `CachedImage`, `CachedFileFace`, `CachedImageBytes`, `ImageBudgetBytes`,
`ImageDecodes`, `History`, `AnimatedImage.Bytes` or `AnimatedImage.FrameCreated`, which do not exist on `main`. The author proved
them by adding those hooks to the old code first, with no change in behaviour. I did the same independently in the scratch copy
(read-only accessors over `_images`, `_fileFaces`, `_history`; a decode counter in `LoadImage`; a frame callback after
`SKImage.FromBitmap` in `AnimatedImage.Load`; `ImageBudgetBytes` defaulting to 96 MB as the author did) and ran all 19:

**Result: 13 failed, 6 passed, exactly as claimed.** The 13 failures and their messages match the table in the design doc
(decoded width 2000; `page 1: 1280000 bytes cached`; typeface handles still non-zero after clearing; frame handles non-zero after a
failed GIF load; "no zigzag back to the left edge"; and so on). The font-replacement test fails on `main` with the file still
locked by `SKTypeface.FromFile`, which is a real second bug the change also fixes.

The 6 passes are the ones the author labels guards. One of them is weaker than it looks: `The_default_budget_is_far_below_the_old_600_MB_per_image`
passed on the old code only because the hook's default was set to 96 MB; it asserts a constant and proves nothing about the old
code (see N1).

**3. Independent proof without hooks.** Because hooks added to old code are part of the author's own story, I also wrote nine tests that
use only public API and reflection on `main`'s private fields (no product code touched). Results on untouched `main`:

| What | On `main` |
|---|---|
| `ClearImageCache` frees a font-file typeface | **Fail**: handle still `2053034402000` |
| 2000 x 1500 PNG in a 100 x 75 widget | **Fail**: decoded width 2000 |
| Three pages with one 400 x 400 picture each | **Fail**: 1 920 000 bytes cached, no total limit |
| Replaced picture redrawn; missing picture retried | **Fail** both (also with a real 1.1 s wait, not only the swapped clock) |
| Graph with a real sample, then seeded | **Fail**: "first 1, last -495, count 121" (the zigzag) |
| Seeding a page twice | **Fail**: 240 samples instead of 120 |
| `Window` is a copy | **Fail** |
| Undo-style snapshot restore, renderer alone | Pass: the renderer itself did not decode again; the repeated decoding came from the editor's `ClearImageCache` call in `Restore` (`main` `MainWindow.cs:722`), which is code reading only |

The only claim that needs a hook to show at all is the GIF frame leak: on `main` the leak happens only when an exception is thrown
between frames (the old code ignored `GetPixels` results, so a corrupt frame does not throw). The hook-based test is the right way to
show it, and the code reading agrees.

**4. Fixed on the branch.** All 19 pass on `750aeb0` (in the full run of 561 and in a filtered run in the scratch copy).

**5. Mutation checks on the branch** (each made alone, full suite run, file restored):

| Mutation | Caught by |
|---|---|
| `ClearImageCache` no longer disposes font-file typefaces | `Clearing_the_cache_frees_font_file_typefaces`, `Disposing_the_renderer_frees_font_file_typefaces` |
| `SensorHistory.Record` out-of-order guard removed | **Nothing**: the guard is unreachable (see N7) |
| `IsSharpEnoughFor` always true | `Growing_an_image_widget_decodes_it_again_at_the_bigger_size` |
| Failed-load frames not freed (`finally { }`) | `A_gif_that_fails_partway_frees_the_frames_it_already_made` |
| File stamp never compared | `An_image_replaced_on_disk_...`, `A_missing_image_is_tried_again_...` |
| `Seed` appends instead of inserting | `Seeding_after_real_samples_keeps_time_order` |
| `Window` returns the live list again | `Window_returns_a_copy_not_the_live_list` |

---

## Findings

### Medium

#### M1. The image budget is first come, first served: a second animation can freeze on one frame, and long GIFs are cut short silently
- **Where:** `src/Glance.Core/Themes/ThemeRenderer.cs:509` (`AnimatedImage.Load(path, AvailableImageBytes(path), ...)`),
  `ThemeRenderer.cs:516-520` (`AvailableImageBytes`), `ThemeRenderer.cs:523-535` (`TrimImages`), `src/Glance.Core/Media/AnimatedImage.cs:102-104`.
- **Evidence:** `AvailableImageBytes` gives a picture whatever is left of the 96 MB after the pictures already drawn *in this
  frame*. The first animation drawn on a page therefore gets the whole budget, and `TrimImages` then evicts every picture not yet drawn
  this frame (including the page's other animation, cached from the previous frame). The next animation gets `max(0, budget - first)`,
  and `Load` turns that into `Math.Max(1, maxBytes / perFrame)` = **1 frame**. `IsSharpEnoughFor` only looks at the pixel size, so
  the frozen entry is never decoded again unless the file changes, the widget grows, or the cache is cleared.
  Probe (two identical 10-frame GIFs on one page, `ImageBudgetBytes` set to exactly one whole animation):
  ```
  a frames 10, b frames 1      -> "second GIF frozen with 1 frame(s)"
  ```
- **Failure scenario:** a theme with an animated full-screen background (480 x 320 = 614 KB per frame) and a small animated icon.
  A 200-frame background is cut to about 160 frames (the loop jumps back after 80% of the animation) and the icon is shown frozen.
  On `main` each picture had its own 600 MB cap, so both played in full. Nothing tells the user why.
- **Remedial action:**
  1. Share the budget fairly between the animations on a page, for example cap one animation at `budget / animationsDrawnLastFrame`
     (count them in `Render`) instead of "what is left right now".
  2. When an animation does not fit, keep the whole loop by dropping every k-th frame and adding its duration to the frame kept,
     rather than keeping only the first frames.
  3. Remember on the entry that it was cut (`Frames.Length < codec.FrameCount`) and decode it again when the budget allows.
  4. Add a test with two animations on one page that asserts both animate.

#### M2. Pictures are decoded for the theme's pixels, not the display's: a theme smaller than the panel gets blurrier pictures than before
- **Where:** `src/Glance.Core/Themes/ThemeRenderer.cs:322` (box size from `r.Width`/`r.Height` in theme units) together with
  `src/Glance.Core/Display/DisplayPusher.cs:215-220` (`canvas.Scale(s, s)` to fit the theme to the panel).
- **Evidence:** `DisplayPusher` scales the canvas by `s = min(panelW / themeW, panelH / themeH)`. `DrawImage` ignores the canvas
  matrix, so the decode is sized for the box in theme pixels and then drawn `s` times larger. Probe (a 1200 x 900 picture filling a
  320 x 240 theme, rendered the way the pusher does onto 480 x 320):
  ```
  device box 426.67x320, decoded 320x240
  ```
  On `main` the same picture was decoded at 1200 x 900 and drawn straight into the 427 px box.
- **Failure scenario:** the editor's screen presets include "Small LCD 320 x 240", and AIDA64 imports keep the `.axlcd` size. Any
  such theme shown on the 480 x 320 LCD (or in the editor's pusher) now shows its photos upscaled by 1.33 with linear filtering:
  visibly softer text in screenshots and photos. The editor preview and virtual display are not affected, because they render at theme
  resolution (`MainWindow.cs:1217`, `PreviewSurface.cs:27`, `VirtualDisplayWindow.cs:72`).
- **Remedial action:** compute the box in device pixels: `var device = c.TotalMatrix.MapRect(r);` and pass
  `ceil(device.Width)`, `ceil(device.Height)` to `LoadImage` (the cache entry already re-decodes when a bigger box asks for it). Add a
  test that renders with `canvas.Scale(1.5f, 1.5f)` and asserts the decode is at least the device size. This also stops a theme
  *larger* than the panel (800 x 480) from decoding more pixels than the panel can show.

### Low

#### L1. A changed file that cannot be decoded (yet) replaces the old picture with the placeholder
- **Where:** `ThemeRenderer.cs:497-511`. On a stamp change the old image is disposed first (`e.Image?.Dispose(); e.Image = null;`) and
  only then is the new file decoded.
- **Evidence:** probe: draw a red PNG, overwrite it with its first 20 bytes (a save caught half way), advance the clock:
  `while half-written: #ff282828` (the placeholder). It recovers on the next stamp change once the save finishes.
- **Failure scenario:** a user saves over `assets/bg.png` from an image editor that writes in place. For a second or so the preview and
  the LCD show the grey placeholder box instead of the old picture, and the pusher sends that frame. If the writer keeps the file
  locked after its final write (stamp no longer changes), the placeholder stays until the next change.
- **Remedial action:** decode into a local first and swap only on success. If the decode fails right after a change, keep the old image
  and retry on the next stat until the stamp has been stable for about two seconds. Add a test with a truncated file.

#### L2. Growing a widget decodes the full-size file again on the UI thread at every 25% step
- **Where:** `ThemeRenderer.cs:497-498` (`IsSharpEnoughFor` then `boxW += boxW / 4 + 1`), `AnimatedImage.cs:172-185` (PNG, GIF
  and WebP always decode the full-size frame first).
- **Evidence:** probe dragging a 2000 x 1500 PNG widget from 40 to 800 px wide in 4 px steps: **13 decodes**, 1.4 s in total
  including 190 renders. On `main` the picture was decoded once.
- **Failure scenario:** resizing a large photo in the editor stutters on each step where a re-decode happens (each is a full-size PNG
  decode on the UI thread). Not wrong, but the editor feels worse than before for big pictures.
- **Remedial action:** while the size keeps changing, draw the existing decode scaled up and only decode again once the size has been
  stable for about 300 ms (or on mouse-up); or grow in steps of 2x instead of 1.25x.

#### L3. A new graph with a longer window than the sensor's existing history is left mostly empty
- **Where:** `ThemeRenderer.cs:103` (`if (_history.HasSamples(id)) continue;`).
- **Evidence:** probe: seed a page with a 30 s graph, add a 300 s graph for the same sensor and seed again: 60 samples, about 30 s
  of a 300 s graph. Real samples fill it in over five minutes.
- **Failure scenario:** adding a "5-minute CPU" graph to a page that already shows a 1-minute CPU graph draws a line on only the right
  tenth of the new graph. All built-in presets use 60 s today, so the gallery is not affected.
- **Remedial action:** now that `Seed` inserts in order, seed only the part that is missing: from `now - seconds` up to the oldest existing
  sample. That keeps real samples untouched and fills the new graph.

#### L4. A replaced video file is no longer restarted by picking it again
- **Where:** `src/Glance.App/MainWindow.cs:866` (the image picker no longer calls `ClearImageCache`), `ThemeRenderer.cs:368-386`
  (videos are keyed by `path|size|fit` with no file stamp).
- **Evidence:** code reading. On `main`, choosing a file in the picker cleared the cache, which also stopped and restarted every video.
  Now pictures notice a changed file by themselves but videos do not, and nothing else restarts them.
- **Failure scenario:** the user replaces `clip.mp4` on disk with a new clip of the same name and picks it again; the old clip keeps
  playing until the theme is reopened.
- **Remedial action:** give video entries the same `FileStamp` check (stat once a second; on a change dispose the `VideoSource` and
  create a new one), or keep the `ClearImageCache` call in the picker only when the chosen file is a video.

#### L5. A stat once a second per picture can block the UI thread on an unreachable network path
- **Where:** `ThemeRenderer.cs:492-496` and `566-577` (`new FileInfo(path)` on the rendering thread).
- **Evidence:** code reading. On `main`, `File.Exists` ran once per picture; now every cached picture and font file is checked once a
  second, on the editor's UI thread for the preview.
- **Failure scenario:** a theme that uses a picture on a mapped network drive; when the share goes away, each check can wait for the SMB
  timeout, freezing the editor every second.
- **Remedial action:** if a stat takes longer than, say, 200 ms, back off that entry's interval (exponentially, up to a minute), or skip polling
  for UNC paths. Low priority: assets normally live in the theme's own folder.

### Nits

- **N1.** `The_default_budget_is_far_below_the_old_600_MB_per_image` (`RendererMemoryTests.cs:203-207`) only asserts a constant; it
  passed on the old code because the hook default was set to the new value. Replace it with a behaviour test (a long animation whose frames
  are limited by the total budget), or drop it.
- **N2.** On a change, the file is stat-ed twice in a row (`ThemeRenderer.cs:495` and `505`; `FileFace` at `451` and `459`). Keep the first stamp.
- **N3.** `AnimatedImage.Load` (`AnimatedImage.cs:130-152`): if anything throws between `new SKBitmap(full)` and `prev = bmp` (for example
  the test hook, or `Shrink`), that full-size working bitmap is left to the finalizer. Wrap the loop body so `bmp` is disposed on an exception.
- **N4.** `SensorHistory.Window` now always copies: a full 900 s window is about 29 KB per call (probe), once per graph per frame (15 fps
  in the editor preview). `main` returned the live list when the window started at index 0. The draw path could use an internal
  non-copying accessor and keep the copy for the public method.
- **N5.** A decode never shrinks when a widget is made smaller; memory stays at the largest size until the entry is evicted or idle for
  5 minutes. Fine, but worth a comment.
- **N6.** Stretch decodes at `max(sx, sy)` with the source aspect ratio; decoding non-uniformly to the exact box would save memory for
  stretched pictures. Optional.
- **N7.** `SensorHistory.Record`'s new guard (`SensorHistory.cs:17`, `list[^1].ms >= nowMs`) can never fire: every writer keeps
  `_lastSample` equal to the newest sample's time (`Record` sets it before appending, `Seed` raises it when it inserts a newer sample), so
  the throttle on line 16 has already returned. That is why removing it breaks no test (mutation table). Keep it as a cheap safety net, but
  say in the comment that it is defensive, or drop it.
- **N8.** File stamps on FAT32 or exFAT (USB sticks) have 2 s or 10 ms resolution; a same-size rewrite within that window would be missed. Acceptable;
  mention it in the comment on `FileStamp`.

---

## Test gaps

- No test for two animations on one page under the budget (M1), and none for rendering through a scaled canvas (M2).
- No test for multi-frame GIF compositing at reduced size. I checked it with a probe (hand-built 8 x 8 GIF: a partial frame, a
  "restore to previous" frame and a frame that depends on a frame other than the previous one, decoded at 8 x 8 and 4 x 4). **All pixels
  were right at both sizes**, so the `reused`/`kept` logic works. That probe would make a good permanent test.
- No test for a changed file that fails to decode (L1), or for a longer graph added next to a shorter one (L3).
- Picture quality is checked only with solid colours. A checkerboard probe (2000 x 1500, 2 px squares, PNG and JPEG, drawn at
  100 x 75) gave a flat 127/128 grey on both the branch and `main`, so downscaling quality is unchanged. A test like that would guard
  against someone dropping the mipmap sampling in `Shrink`.
- The editor change (no `ClearImageCache` in `Restore`, Save and the pickers) is UI code and has no automated test (as usual for `Glance.App`).

## Manual check for the user

These need the real editor and display; I could not do them.

1. **Editor zoom quality.** Open a theme with a detailed photo in a small widget. Set zoom to 300%. It should look exactly as on `main`
   (the preview renders at theme size and the zoom only enlarges that bitmap).
2. **Replace a picture on disk.** With the editor open and the display running, copy another picture over the theme's
   `assets\<name>.png` in Explorer. Within about a second both the preview and the LCD should show the new picture. Watch for a brief grey
   box (L1).
3. **Replace or delete a font file** used by a text widget while the editor is open. On `main` Windows refused ("file is open"); now it
   should work and the text should change within a second.
4. **A 320 x 240 theme on the LCD** (screen preset "Small LCD") with a photo: compare its sharpness with `main` (M2).
5. **Two GIFs on one page:** both should animate (M1). Then a long full-screen GIF: does the loop play to the end?
6. **Undo and redo** on a theme with big pictures and a video: no pause on each step, and the video keeps playing instead of restarting.
7. Optional: in Task Manager, the editor's memory with a large GIF should be far below what `main` uses.

## What was checked and found sound

- **Font-file typefaces** are per renderer, read into memory (`SKData.CreateCopy` + `SKTypeface.FromData`, so the file is no longer locked),
  and disposed on clear, on `Dispose`, on replacement and after 5 idle minutes. An `SKFont` made from a typeface holds its own native
  reference, so a typeface disposed while a font made from it is still alive in the same frame is not a use-after-free.
- **Installed-font typefaces are not disposed, and that is right.** I re-ran the author's evidence with my own probe on SkiaSharp 4.157:
  two `FromFamilyName("Arial", Bold)` calls return the same managed object, `Dispose` leaves the handle unchanged and text still measures,
  and an unknown family returns `SKTypeface.Default` itself. Disposing them, as M10's remedy suggested, would be a no-op today and a shared-state
  bug if SkiaSharp ever honoured it.
- **No cross-thread sharing.** Each consumer owns its renderer: the pusher's is a local in `DisplayPusher.Run`, the editor's is used only
  from `DispatcherTimer` ticks and UI handlers (including the remaining `ClearImageCache` calls at `MainWindow.cs:654` and `1284`), and
  the gallery and templates make their own. `AnimatedImage.FrameCreated` is `[ThreadStatic]`.
- **No disposal of a picture still in use.** `TrimImages` never evicts an entry drawn in the current `Render` pass, `SweepIdle` runs after
  drawing, and the frame returned by `LoadImage` is drawn at once on a raster canvas (immediate), so a later re-decode of the same path in the
  same frame cannot pull pixels from under an earlier draw.
- **File stamps compare for equality, not order,** so clock skew and files with older timestamps (Explorer keeps the source's time on copy) are still
  seen as changes. An atomic replace (`File.Replace`, rename over) gets the new file's time. NTFS times have 100 ns resolution.
- **Missing and broken files:** a missing file is looked for once a second; a file that exists but cannot be decoded is not retried until its stamp
  changes. Both behave as described.
- **Two widgets sharing one file at different sizes** converge on the larger decode: 2 decodes over 10 frames in a probe, no thrashing.
- **GIF compositing at reduced size** is correct, including "restore to previous" frames (probe above). Peak memory while decoding is one full-size
  working frame plus any frames later frames depend on, as the design doc says.
- **Downscale quality** matches `main` (checkerboard probe), and JPEG uses the codec's native scaled decode.
- **Hot path:** per picture per frame it is one dictionary lookup and a clock read; a stat at most once a second; the LINQ over the cache
  (`AvailableImageBytes`, `TrimImages`, `CachedImageBytes`) runs only when something is decoded.
- **M11:** `Seed` inserts by binary search and drops duplicate times, `Record` never appends out of order, `Window` searches its start and
  returns a copy, and `SeedGraphHistory` seeds each sensor once for its longest graph and never over real samples. The ordering invariant
  holds for every writer.
- **Editor call sites:** `Restore` (undo/redo), Save and the two pickers no longer clear caches; opening another theme and installing FFmpeg
  still do. Undo that points a widget at a different file simply loads that file; undo while a file changed on disk shows the current file
  (files are not versioned, same as before).

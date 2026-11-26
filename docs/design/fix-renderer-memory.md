# Fix: renderer caches (M10) and graph history seeding (M11)

Findings M10 and M11 in `docs/reviews/review-full-codebase.md`. Branch `claude/fix-renderer-memory-8913bc`.

Video code (`VideoSource`, `Ffmpeg`) and the runner are untouched.

## How it was proved

Each case got a test first: `src/Glance.Tests/RendererMemoryTests.cs` (classes `RendererMemoryTests` and
`GraphHistorySeedTests`). Memory is measured through proxies: the size of the decoded picture, the cache's byte total,
and whether a typeface's or frame's native handle was freed (`Handle == IntPtr.Zero`).

To compile the tests against the old code, a few internal test hooks were added first, with no change in behaviour:
`ThemeRenderer.CachedImage`, `CachedFileFace`, `CachedImageBytes`, `ImageBudgetBytes`, `ImageDecodes`, `History`,
plus `AnimatedImage.Bytes` and the `[ThreadStatic] AnimatedImage.FrameCreated` hook.

**Before** (old code plus the hooks): 13 of the 19 new tests failed, 6 passed (the 6 are guards for behaviour that was
already right and must stay right). **After**: 19 of 19 pass; the full suite is 561 of 561 (542 before plus 19 new).

Mutation check after the fix: disabling "skip sensors that already have history" and the budget trim made
`A_graph_that_already_has_real_samples_is_not_seeded_over` and `Images_stay_within_the_renderer_memory_budget` fail.

## M10: renderer caches

| Case | Test | Before | After |
|---|---|---|---|
| `ClearImageCache` frees font-file typefaces | `Clearing_the_cache_frees_font_file_typefaces` | fail: handle still `2519038641488` after clearing (forgotten, not freed) | pass: handle 0 |
| `Dispose` frees font-file typefaces | `Disposing_the_renderer_frees_font_file_typefaces` | pass (guard) | pass |
| A font file replaced on disk is read again, old typeface freed | `A_font_file_replaced_on_disk_is_read_again_and_the_old_typeface_freed` | fail: `IOException: ... a file with a user-mapped section open` (the renderer kept the font file locked) | pass |
| Installed fonts keep working after another renderer is disposed | `Disposing_one_renderer_leaves_installed_fonts_working_for_another` | pass (guard) | pass |
| A large picture is decoded near its drawn size | `A_large_image_is_decoded_near_the_size_it_is_drawn_at` (2000x1500 in a 100x75 box) | fail: decoded width 2000 | pass: about 100x75 |
| Cover keeps enough pixels to fill the box | `A_cover_image_keeps_enough_pixels_to_fill_its_box` (2000x1000 in 100x100) | fail: width 2000 | pass: 200x100 |
| A small picture is not scaled up | `A_small_image_is_not_scaled_up_when_decoded` | pass (guard) | pass |
| A widget made bigger gets a sharper decode | `Growing_an_image_widget_decodes_it_again_at_the_bigger_size` | pass (guard: old code was always full size) | pass |
| Total memory budget per renderer | `Images_stay_within_the_renderer_memory_budget` (3 pages of 640 KB, budget 1 MB) | fail: `page 1: 1280000 bytes cached` | pass: at most one picture cached, the page on screen always drawn |
| Default budget far below 600 MB per picture | `The_default_budget_is_far_below_the_old_600_MB_per_image` | pass (hook default) | pass: 96 MB in total |
| A picture replaced on disk is drawn again | `An_image_replaced_on_disk_is_drawn_again_without_clearing_the_cache` | fail: still red (`#ffff0000`), expected green | pass |
| A missing picture is looked for again | `A_missing_image_is_tried_again_after_a_while` | fail: placeholder (`#ff282828`) forever | pass |
| Undo-style restores do not decode again | `Unchanged_images_are_decoded_once_even_after_an_undo_style_snapshot_restore` | pass (guard: the decoding came from the editor's `ClearImageCache` call, not from the renderer) | pass: 1 decode across 6 renders and 5 restores |
| `AnimatedImage.Load` frees frames when it fails partway | `A_gif_that_fails_partway_frees_the_frames_it_already_made` | fail: both frame handles non-zero | pass: both 0 |

### What changed

- **Typefaces.** `ClearImageCache` and `Dispose` free font-file typefaces. Font files are now read into memory
  (`SKTypeface.FromData`) instead of `SKTypeface.FromFile`, which memory-maps the file and keeps it locked; before, a
  user could not replace or delete a font file while the editor or runner had it loaded.
- **Installed-font typefaces (`_faces`) are deliberately not disposed.** This differs from the review's remedy, on
  evidence: a probe with SkiaSharp 4.157 showed that `SKTypeface.FromFamilyName` returns *the same managed object* to
  every caller for one family and style (and `SKTypeface.Default` itself for "Segoe UI" regular and for unknown
  families), and that `Dispose` on them is ignored (the handle stays valid and the typeface keeps working). So there
  is no per-renderer leak, disposing would be a no-op, and if SkiaSharp ever honoured it, one renderer's `Dispose`
  would break the editor preview, the pusher and the virtual display at once. `Dispose` now clears the dictionary and
  the comment on `Face` explains why. The guard test above pins this down.
- **Cache entries follow the file.** Each picture and font-file entry stores the file's last-write time and size.
  At most once a second (`StatIntervalMs`) it is compared with the file; a change means the entry is decoded again
  (and an old typeface freed). A missing file is looked for again on the same schedule; a file that exists but cannot
  be decoded is not retried until it changes. This covers the editor's display pusher, which never refreshed before.
- **Decode size.** `AnimatedImage.Load` takes the box and fit. `ScaleFor` picks the smallest scale (never above 1)
  that still gives every screen pixel of the box a picture pixel: Contain uses the smaller axis, Stretch and Cover the
  larger. Every consumer (editor preview, pusher, virtual display, thumbnails) renders at theme resolution and the
  editor's zoom only scales that bitmap, so this is enough for both the display and the editor zoom. JPEG decodes
  smaller natively (`GetScaledDimensions`), so a large photo is never held at full size; other formats decode full
  size one frame at a time and are scaled with linear and mipmap sampling. Animations keep a full-size working frame
  only for the previous frame and for frames a later frame builds on. When a widget is drawn bigger than its decode,
  the picture is decoded again with 25% room to spare, so dragging a resize handle decodes only every so often.
- **Budget.** One renderer keeps at most 96 MB of decoded pictures (`DefaultImageBudgetBytes`; before, 600 MB per
  picture). Pictures drawn in the current frame are never evicted; others go least recently drawn first. A long
  animation gets only the frames that fit the space left (always at least one). Pictures and font files not drawn for
  5 minutes are freed.
- **Editor.** Undo/redo (`Restore`), Save and the image and font pickers no longer call `ClearImageCache`. Undo now
  neither decodes every picture again on the UI thread nor restarts videos. Opening another theme (`SetTheme`) and the
  FFmpeg install still clear everything, as before.
- **`AnimatedImage.Load`** frees every frame it made when it fails partway, and also stops at a frame the codec
  reports as broken (keeping the frames before it) instead of ignoring the result.

## M11: graph history

| Case | Test | Before | After |
|---|---|---|---|
| Seeding after real samples keeps time order | `Seeding_after_real_samples_keeps_time_order` | fail: window not oldest first | pass |
| `Window` returns a copy | `Window_returns_a_copy_not_the_live_list` | fail: the returned list grew to 2 items after a later `Record` | pass |
| Seeding a page again does not duplicate | `Seeding_a_page_again_does_not_duplicate_the_history` | fail: unsorted, twice the samples | pass: 120 samples, sorted |
| Two graphs on one sensor seed once, for the longer window | `Two_graphs_sharing_a_sensor_are_seeded_once_for_the_longer_window` | fail: unsorted (60 + 240 samples) | pass: 240 samples, sorted |
| A graph with real samples is not seeded over (the zigzag) | `A_graph_that_already_has_real_samples_is_not_seeded_over` | fail: "no zigzag back to the left edge" | pass |

### What changed

- `SensorHistory.Seed` inserts in time order (binary search) and ignores a sample at a time that is already there;
  `Record` never appends out of order; `Window` finds its start by binary search and always returns a copy.
- `SensorHistory.HasSamples` is new. `ThemeRenderer.SeedGraphHistory` groups the page's graphs by sensor, seeds each
  sensor once for its longest graph, and skips sensors that already have history (real or seeded).

## Not verified here

- Nothing was checked on the physical display or in the running editor. Picture quality after the smaller decode was
  checked only by pixel tests (solid colours); a detailed picture drawn much smaller than its file should look the
  same or better (mipmapped downscale), but nobody has looked.
- Peak memory while decoding a large non-JPEG still is still one full-size frame (PNG, GIF and WebP cannot decode
  smaller natively in SkiaSharp's `SKCodec`). Only the cached result is small.
- Decoding still happens on the thread that renders (the editor's UI thread for the preview). Undo no longer causes it,
  but adding or replacing a large picture still decodes once on the UI thread.

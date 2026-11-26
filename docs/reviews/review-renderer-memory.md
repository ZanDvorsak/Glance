# Review: renderer caches (M10) and graph history seeding (M11)

- **Verdict:** merge after fixes — **now:** all findings fixed (see below); merged into main
- **Build/tests:** 578/578, three runs in a row; build 0 errors
- **Proof:**
  - Before: on `main`, 13 of 19 new tests failed (a 2000 px PNG decoded full size for a 100 px widget, 1.92 MB cached with no total limit, font files stayed locked, a replaced picture never redrawn, graph seeding zigzagged)
  - Before: review fixes: 16 of 19 failed on the reviewed branch (second GIF frozen on 1 frame, 320 px decode for a 427 px box, 13 decodes while dragging)
  - After: all pass, 578/578

## Findings
- **Medium M1** — The 96 MB image budget was first come, first served: a second animation froze on 1 frame, long GIFs were cut short (`ThemeRenderer.cs:509`) → budget shared evenly between animations; drop every k-th frame so the loop keeps its length; never fewer than 10 frames — **fixed** (the "shortened" log line is not shown anywhere yet)
- **Medium M2** — Pictures were decoded for theme pixels, not panel pixels, so a 320 x 240 theme looked softer on the LCD (`ThemeRenderer.cs:322`) → box measured with the canvas matrix — **fixed**
- **Low L1** — A changed file caught half-written showed the grey placeholder (`ThemeRenderer.cs:497`) → keep the old picture until the new file decodes; placeholder only after 2 s of failing — **fixed**
- **Low L2** — Growing a widget decoded the full file again at every 25% step (13 decodes per drag) (`ThemeRenderer.cs:497`) → re-decode only once the size has settled for 300 ms — **fixed**
- **Low L3** — A longer graph next to a shorter one stayed mostly empty (`ThemeRenderer.cs:103`) → seed only the missing past — **fixed**
- **Low L4** — A video file replaced on disk was not restarted (`ThemeRenderer.cs:368`) → videos get the same file check and restart — **fixed**
- **Low L5** — A once-a-second file check could block the UI thread on a dead network path (`ThemeRenderer.cs:492`) → the check runs in the background, slow paths back off — **fixed**
- **Low (test gap)** — No permanent test for multi-frame GIF compositing at reduced size (a probe showed it correct); the editor change has no test — **open**
- Nits: N1 tautological test removed, N2 one stat per change, N3 working bitmaps freed, N4 no history copy per frame, N5 smaller widget gets a smaller decode, N6 stretch decodes to the box, N7 dead guard removed, N8 FAT32/exFAT comment
- Also open: PNG, GIF and WebP still need one full-size frame while decoding; a new picture is still decoded on the rendering thread the first time; `docs/walkthroughs/renderer-memory.md` describes the first version

## Manual checks for the user
- A 320 x 240 theme ("Small LCD" preset) with a photo on the LCD: as sharp as before (M2)
- Two GIFs on one page: both animate; a long full-screen GIF plays its whole loop, maybe a little choppier, not shorter (M1)
- Drag-resize a large photo in the editor: smooth, slightly soft while dragging, sharp about 0.3 s after letting go (L2)
- Save over a picture from an image editor while the display runs: no grey box in between (L1)
- Replace a picture, a font file and a video file on disk: each updates within about a second (L4)
- Undo and redo with big pictures and a video: no pause per step, the video keeps playing
- Editor zoom at 300% with a detailed photo: same as before

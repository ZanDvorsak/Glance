# Renderer memory and graph history: a walkthrough of the M10 and M11 fixes

This walkthrough is for a mid-level developer who wants to understand how Glance's renderer looks after its pictures, fonts and graph
history, and what one change did to make that cheaper and more correct. It covers the native objects SkiaSharp hands you and how to free
them, how a codec can decode a picture smaller than the file, how a cache notices that a file changed on disk, how the new memory budget
works, and how graph history keeps its samples in time order. It ends with five ways the change itself could still be better.

It assumes you know C# and .NET. You do not need to know the rest of Glance; the parts that matter are explained here. For the big
picture, read `docs/WALKTHROUGH.md` (section 4.5, "The renderer", is the background for this change).

**The change:** branch `claude/fix-renderer-memory-8913bc`, commit `750aeb0`, findings M10 and M11 in `docs/reviews/review-full-codebase.md`.
**Files:** `src/Glance.Core/Themes/ThemeRenderer.cs`, `src/Glance.Core/Media/AnimatedImage.cs`, `src/Glance.Core/Sensors/SensorHistory.cs`,
`src/Glance.App/MainWindow.cs`, `src/Glance.Tests/RendererMemoryTests.cs`.
**Author's proof:** `docs/design/fix-renderer-memory.md`. **Independent review:** `docs/reviews/review-renderer-memory.md`.

**Contents**

1. [The problem](#1-the-problem)
2. [Technologies involved](#2-technologies-involved)
3. [High-level walkthrough](#3-high-level-walkthrough)
4. [Detailed code review](#4-detailed-code-review)
5. [Self-review: five ways to improve the change](#5-self-review-five-ways-to-improve-the-change)

---

## 1. The problem

`ThemeRenderer` draws one page of a theme onto a Skia canvas. The same class serves four consumers, and each one owns its own
instance: the editor preview, the display pusher (which sends frames to the LCD from a background thread), the virtual display window, and
the gallery thumbnails. Each instance keeps caches so that it does not reopen files on every frame: decoded pictures and GIFs, typefaces
loaded from font files, video processes, and the recent values of the sensors that graphs show.

The full-codebase review found two groups of problems in those caches.

**M10, the picture and font caches:**

- `ClearImageCache` *forgot* the font-file typefaces without freeing them, and the editor called it on every undo and redo step. So each
  undo leaked native typefaces, and also threw away every decoded picture, which then had to be decoded again on the UI thread.
- Pictures were decoded at full resolution. A 4000 x 3000 photo shown in a 100 px widget cost 48 MB, and the only limit was 600 MB *per
  picture, per renderer*. The editor can have three renderers alive, so one big GIF could cost about 1.8 GB.
- The caches never looked at the disk again. A picture replaced on disk kept showing the old version on the LCD until the theme was
  reopened, and a missing file was remembered as missing forever.
- `AnimatedImage.Load` leaked the frames it had already made if decoding failed partway.

**M11, graph history:**

- `SensorHistory.Seed` appended samples without looking at their time. When the editor seeded a page again (it does after adding a preset),
  synthetic samples from the past landed *after* real samples from the present, and a graph drew a line back to its left edge: a zigzag.
- Two graphs that show the same sensor seeded it twice.
- `Window` returned the live list, so a caller could see it change under its feet.

---

## 2. Technologies involved

| Area | Technology | What it does in this change |
|---|---|---|
| Drawing | **SkiaSharp 4.157** (`SKCanvas`, `SKImage`, `SKBitmap`, `SKTypeface`, `SKFont`) | Everything is drawn with Skia. Pictures are `SKImage`s, text uses `SKFont` over an `SKTypeface`. |
| Native lifetime | SkiaSharp's **`IDisposable` wrappers over reference-counted native objects** | Each managed object wraps a native pointer. `Dispose` releases the managed object's reference. The change is mostly about calling it at the right time. |
| Decoding | **`SKCodec`**, `GetScaledDimensions`, `SKCodecFrameInfo.RequiredFrame` | Reads PNG, JPEG, GIF and WebP. JPEG can decode at 1/2, 1/4 or 1/8 size natively. GIF frames can depend on earlier frames. |
| Resampling | **`SKPixmap.ScalePixels`** with `SKSamplingOptions(Linear, Linear mipmap)` | Shrinks a decoded frame to the size the widget needs, with good quality. |
| Files | `System.IO.FileInfo` (`LastWriteTimeUtc`, `Length`) | A cheap "has this file changed?" check: the file stamp. |
| Clock | `Environment.TickCount64` behind an injectable `Func<long> Now` | Paces the stamp checks and lets tests jump time forward. |
| Collections | `List<T>` with a hand-written **binary search** | Keeps graph samples sorted and finds a window's start in O(log n). |
| Tests | **xUnit**, internal test hooks via `InternalsVisibleTo` | Memory is measured through proxies: decoded size, cached bytes, and whether a native handle became zero. |

### 2.1 SkiaSharp objects are native, and `Dispose` matters

An `SKImage` holding a 480 x 320 picture is a few dozen bytes of managed object and 600 KB of native memory. The garbage collector only
sees the few dozen bytes, so it feels no pressure to run, and the native memory stays until a finalizer gets to it, if ever. Therefore:
**dispose SkiaSharp objects you own as soon as you are done with them.** After `Dispose`, the wrapper's `Handle` is `IntPtr.Zero`.
The new tests use exactly that as their memory probe:

```csharp
r.ClearImageCache();
Assert.Equal(IntPtr.Zero, face.Handle);   // the native typeface was freed, not just forgotten
```

Two subtleties showed up in this change:

1. **Reference counting.** Most Skia objects are reference counted natively. An `SKFont` made from a typeface holds its own native reference
   to it, so disposing the `SKTypeface` wrapper while a font still uses it does not pull the glyphs out from under the font. The native typeface
   goes away when the last reference is released.
2. **Some objects are shared and must not be disposed.** `SKTypeface.FromFamilyName("Arial", Bold)` returns *the same managed object* to every
   caller in the process, and an unknown family returns `SKTypeface.Default` itself. SkiaSharp ignores `Dispose` on them. The author checked this
   with a probe, and the reviewer checked it again. So "dispose everything you cache" has an exception: installed-font typefaces are not owned by
   any one renderer.

### 2.2 Decoding smaller than the file

A codec reports the picture's size (`codec.Info`) and decodes into a buffer you give it. Some formats can decode directly to a smaller size:
JPEG stores its pixels as 8 x 8 blocks of frequencies, and can rebuild each block at 1, 2 or 4 pixels instead of 8. `GetScaledDimensions(scale)`
returns the nearest size the codec can do natively. PNG and GIF cannot, so they are decoded full size and then shrunk with
`ScalePixels`. With mipmap sampling, the shrink averages many source pixels per destination pixel, which avoids the shimmering (aliasing)
you get from plain linear filtering at large reductions.

### 2.3 GIF frames depend on each other

A GIF frame is often only a small rectangle that changes; the rest of the picture comes from an earlier frame. `SKCodecFrameInfo.RequiredFrame`
says which earlier frame must already be on the canvas before this one is decoded on top (`-1` means "none, start from transparent"). Usually it
is the previous frame, but with "restore to previous" disposal it can be an older one. This is why the new code keeps full-size working
bitmaps even when it stores smaller frames: a frame must be composited at full size on top of the right base, and only then shrunk.

### 2.4 Cache invalidation by file stamp

Cache invalidation is famously one of the hard problems. The cheap, common answer for files is a **stamp**:
remember the file's last-write time and size when you read it, and later compare. If either differs, the file changed. Three details matter:

- Compare for **equality**, not "newer than". A file copied in by Explorer keeps its source's (possibly older) timestamp; clocks can be wrong.
- Stat **occasionally**, not every frame. A stat is a system call; doing it once a second per file is negligible.
- A **missing** file has a stamp too ("does not exist"), so a file that appears later is noticed by the same comparison.

---

## 3. High-level walkthrough

### 3.1 What a cache entry looks like now

Before, the picture cache was `Dictionary<string, AnimatedImage?>`: a path and its decoded picture, or `null` for "missing" (forever). Now each
path has an entry with bookkeeping:

```csharp
private sealed class ImageEntry
{
    public AnimatedImage? Image;
    public FileStamp Stamp;
    public long CheckedAt, LastUsedAt, LastPass;
}
```

- `Stamp`: which version of the file `Image` came from.
- `CheckedAt`: when the file was last stat-ed, so it happens at most once a second.
- `LastUsedAt`: for "least recently drawn" eviction and the 5-minute idle sweep.
- `LastPass`: which `Render` call last drew it. Pictures drawn in the current frame are never evicted.

Font files get the same shape (`FontEntry`) without `LastPass`.

### 3.2 One frame, step by step

1. `Render` increments `_pass`.
2. For each image widget, `DrawImage` asks `LoadImage(path, boxW, boxH, fit)` for the picture, passing the size it will be drawn at.
3. `LoadImage` returns the cached picture when the file has not changed and the picture has enough pixels for the box. Otherwise it decodes
   again, at about the box's size, within what is left of the 96 MB budget, then trims other pictures if the cache is over budget.
4. Text widgets with a font file go through `FileFace`, which works the same way for typefaces.
5. After drawing, `SweepIdle` (at most every 10 s) frees pictures and font files not drawn for 5 minutes.

### 3.3 What the editor stopped doing

The editor used to call `ClearImageCache` after undo, redo, save, and after the image and font pickers. That was its only way to make the
renderer see changed files. Since the renderer now notices changes by itself, those calls are gone. Undo no longer decodes every picture
on the UI thread, and videos no longer restart on every undo step. Opening another theme and installing FFmpeg still clear everything.

### 3.4 Graph history

`SensorHistory` keeps, per sensor, a list of `(ms, value)` samples that must stay sorted by time. Now:

- `Seed` inserts at the right place (binary search) and ignores a sample at a time that is already there.
- `Window` finds its first sample by binary search and returns a copy.
- `ThemeRenderer.SeedGraphHistory` groups a page's graphs by sensor, seeds each sensor once for its longest graph, and skips sensors that already
  have samples. Seeding a page again is therefore harmless.

---

## 4. Detailed code review

### 4.1 Font files: free them, and stop locking them

The old code:

```csharp
private SKTypeface? FileFace(string path)
{
    if (_fileFaces.TryGetValue(path, out var f)) return f;
    try { f = File.Exists(path) ? SKTypeface.FromFile(path) : null; } catch { f = null; }
    return _fileFaces[path] = f;
}

public void ClearImageCache()
{
    ...
    _fileFaces.Clear();   // font files are re-read the next time they are drawn
}
```

`Clear()` drops the references from the dictionary, but nobody disposes the typefaces, so their native memory waits for finalizers. The new
`ClearImageCache` disposes before clearing:

```csharp
foreach (var e in _fileFaces.Values) e.Face?.Dispose();
_fileFaces.Clear();
```

The author found a second problem while writing the replacement test: `SKTypeface.FromFile` memory-maps the file and keeps it open for the
typeface's lifetime. On Windows that means the user cannot replace or delete the font file while the editor or the runner is running (the test
failed with "a file with a user-mapped section open"). The fix reads the bytes and builds the typeface from memory:

```csharp
private static SKTypeface? ReadFontFile(string path, FileStamp stamp)
{
    if (!stamp.Exists || stamp.Length > MaxFontFileBytes) return null;
    try
    {
        using var data = SKData.CreateCopy(File.ReadAllBytes(path));
        return SKTypeface.FromData(data);
    }
    catch { return null; }
}
```

`using var data` looks like it frees the bytes too early, but it does not: the typeface takes its own native reference to the data, and
`Dispose` on the wrapper only releases ours (section 2.1). The 64 MB cap keeps a mistaken "font" file from being read whole.

`FileFace` itself now follows the file (section 4.3 explains the pattern):

```csharp
if (_fileFaces.TryGetValue(path, out var e))
{
    e.LastUsedAt = now;
    if (now - e.CheckedAt < StatIntervalMs) return e.Face;
    e.CheckedAt = now;
    var stamp = FileStamp.Of(path);
    if (stamp == e.Stamp) return e.Face;
    e.Face?.Dispose();          // the file changed: free the old typeface
    e.Face = null;
}
```

**Why `_faces` (installed fonts) is not disposed.** M10's remedy said to dispose `_faces` in `Dispose`. The author probed SkiaSharp and found
that installed-font typefaces are process-wide singletons whose `Dispose` is ignored (section 2.1). Disposing them would do nothing today and
would break the other renderers if SkiaSharp ever honoured it. The code documents this on `Face` and pins it with a guard test,
`Disposing_one_renderer_leaves_installed_fonts_working_for_another`. Departing from a review's remedy *with evidence* is the right call.

### 4.2 Decoding at the size the widget needs

`DrawImage` now passes the box:

```csharp
else if (LoadImage(path, Math.Max(1, (int)Math.Ceiling(r.Width)), Math.Max(1, (int)Math.Ceiling(r.Height)), w.Fit) is { } anim)
```

and `AnimatedImage.ScaleFor` decides how far the picture may shrink:

```csharp
public static double ScaleFor(int srcW, int srcH, int boxW, int boxH, ImageFit fit)
{
    if (boxW <= 0 || boxH <= 0 || srcW <= 0 || srcH <= 0) return 1;
    boxW = Math.Min(boxW, MaxBoxSide); boxH = Math.Min(boxH, MaxBoxSide);
    double sx = (double)boxW / srcW, sy = (double)boxH / srcH;
    // Contain draws the whole picture inside the box (the smaller scale); Stretch and Cover fill both sides
    var s = fit == ImageFit.Contain ? Math.Min(sx, sy) : Math.Max(sx, sy);
    return Math.Clamp(s, 1e-6, 1);
}
```

Worked examples, for a 2000 x 1000 picture in a 100 x 100 box:

| Fit | `sx`, `sy` | Scale | Decoded | Why |
|---|---|---|---|---|
| Contain | 0.05, 0.1 | 0.05 | 100 x 50 | The whole picture fits inside; the limiting side is the width. |
| Cover | 0.05, 0.1 | 0.1 | 200 x 100 | The picture fills the box and is cropped; the height must cover 100 px. |
| Stretch | 0.05, 0.1 | 0.1 | 200 x 100 | Each axis is scaled independently; the larger factor keeps both axes sharp. |

The clamp to 1 means a picture is never decoded *larger* than its file (`A_small_image_is_not_scaled_up_when_decoded`).

For stills, `DecodeStill` uses the codec's native scaling when it can (JPEG), and `Shrink` does the rest:

```csharp
var scaled = codec.GetScaledDimensions((float)target.Width / full.Width);
if (scaled.Width >= target.Width && scaled.Height >= target.Height && scaled.Width < full.Width)
    info = full.WithSize(scaled.Width, scaled.Height);
```

The check `scaled >= target` matters: a native 1/8 decode that is *smaller* than the box would be blurry, so it is used only when it still has
enough pixels.

**Growing a widget.** A cached picture is reused only if it is sharp enough for the box it is now drawn in:

```csharp
if (!changed && (e.Image is null || e.Image.IsSharpEnoughFor(boxW, boxH, fit))) return e.Image;
if (!changed) { boxW += boxW / 4 + 1; boxH += boxH / 4 + 1; }   // a widget being enlarged: leave room
```

The extra 25% means dragging a resize handle decodes every so often rather than on every mouse move. If two widgets use the same file at
different sizes, the larger decode satisfies both, so the cache settles after one extra decode (the reviewer measured 2 decodes over 10 frames).

**A caveat the review found (M2).** The box is measured in *theme* pixels. The editor preview, the virtual display and the thumbnails all
render at theme size, so that is right for them, and zooming the editor only enlarges that bitmap. But `DisplayPusher` scales the theme to fit the
panel; a 320 x 240 theme on the 480 x 320 LCD is drawn 1.33 times larger, so its pictures are now decoded 1.33 times too small. The fix is to
measure the box through the canvas matrix (`c.TotalMatrix.MapRect(r)`).

### 4.3 Noticing files that change

The stamp is a small value type, so comparing two of them is `==`:

```csharp
private readonly record struct FileStamp(bool Exists, DateTime Modified, long Length)
{
    public static FileStamp Of(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? new FileStamp(true, fi.LastWriteTimeUtc, fi.Length) : default;
        }
        catch { return default; }
    }
}
```

`default` is `(false, 0001-01-01, 0)`: "does not exist". `record struct` gives value equality for free.

`LoadImage` checks the stamp at most once a second, and reads the stamp *before* decoding:

```csharp
e.CheckedAt = now;
e.Stamp = FileStamp.Of(path);   // read before decoding, so a change made during the decode is seen next time
if (e.Stamp.Exists)
{
    ImageDecodes++;
    e.Image = AnimatedImage.Load(path, AvailableImageBytes(path), boxW, boxH, fit);
    TrimImages(path);
}
```

The order is a small but real race fix. If the stamp were read after decoding, a save that finished *during* the decode would be recorded as
"already seen" and the old pixels would stay forever. Read first, and the worst case is one extra decode.

Three behaviours fall out of this:

| Situation | Result |
|---|---|
| File replaced (any new time or size) | Decoded again within a second. Works for the editor's pusher too, which nobody ever cleared before. |
| File missing | Stamp `default`; each second the stamp is compared again, so the file is picked up when it appears. |
| File exists but is broken | Stamp stored, `Image` null; not retried until the file changes, so a corrupt file is not decoded every second. |

One rough edge (review L1): on a change the old picture is disposed *before* the new decode, so a file caught half-written shows the placeholder
for a moment. Decoding into a local and swapping only on success would avoid that.

### 4.4 The memory budget and eviction

The old limit was "600 MB per picture". The new one is 96 MB of decoded pixels per renderer, enforced in two places.

**When decoding an animation**, `Load` is told how much is left:

```csharp
private long AvailableImageBytes(string path)
{
    long inUse = _images.Where(kv => kv.Key != path && kv.Value.LastPass == _pass).Sum(kv => kv.Value.Image?.Bytes ?? 0);
    return Math.Max(0, ImageBudgetBytes - inUse);
}
```

and keeps only the frames that fit (always at least one):

```csharp
long perFrame = (long)tw * th * 4;
if (n > 1) n = (int)Math.Min(n, Math.Max(1, maxBytes / perFrame)); // cap memory on long animations
```

**After any decode**, `TrimImages` frees least-recently-drawn pictures until the cache fits, never touching one drawn in this frame:

```csharp
foreach (var (k, e) in _images.Where(kv => kv.Key != keep && kv.Value.LastPass != _pass && kv.Value.Image is not null)
             .OrderBy(kv => kv.Value.LastUsedAt).ToList())
{
    total -= e.Image!.Bytes;
    e.Image.Dispose();
    _images.Remove(k);
    if (total <= ImageBudgetBytes) break;
}
```

This is a simple LRU: sort by last use, drop from the oldest. The `.ToList()` matters: it copies the candidates before the loop removes from
`_images`, because changing a dictionary while enumerating it throws.

The "never evict what this frame drew" rule is what makes disposal safe. The frame the renderer just drew from cannot disappear mid-frame,
and pictures larger than the budget still show (the cache simply goes over budget while they are on screen).

**A caveat the review found (M1).** "What is left" is first come, first served. The first animation drawn on a page can take the whole budget,
the trim then evicts the page's other animation (drawn last frame but not yet this frame), and that one comes back with a single frame and stays
frozen. Splitting the budget between a page's animations, and dropping every k-th frame instead of the tail, would fix both that and long GIFs
being cut short.

### 4.5 GIF frames at reduced size, and cleaning up after a failure

The heart of the new animation loop:

```csharp
var bmp = new SKBitmap(full);                      // full-size working canvas for this frame
var opts = new SKCodecOptions(i);
var req = infos[i].RequiredFrame;
if (req >= 0 && req < i)
{
    var basis = req == i - 1 ? prev : kept.GetValueOrDefault(req);
    if (basis is null) { bmp.Dispose(); break; }
    opts.PriorFrame = req;
    basis.GetPixelSpan().CopyTo(bmp.GetPixelSpan());   // start from the frame this one builds on
}
var result = codec.GetPixels(full, bmp.GetPixels(), bmp.RowBytes, opts);
...
Keep(Shrink(bmp, target));                          // store a small copy
if (prev is not null && !kept.ContainsValue(prev)) prev.Dispose();
if (reused.Contains(i)) kept[i] = bmp;              // an older frame a later one needs
prev = bmp;
```

Before the loop, `reused` collects every frame that some later frame requires *and* that is not just its predecessor. Only those, plus the
previous frame, stay at full size. A typical GIF therefore needs one or two full-size bitmaps while decoding, whatever its length, and stores only
small frames. The reviewer checked a hand-built GIF with a partial frame, a "restore to previous" frame and a frame that depends on an older
frame: all pixels were right at full size and at half size.

The leak fix is a classic ownership pattern. Every `SKImage` made goes into `made`; when the `AnimatedImage` takes ownership, `made` is cleared;
whatever is still in `made` when the method exits belongs to a failed load and is freed:

```csharp
var made = new List<SKImage>();
try
{
    ...
    var result2 = new AnimatedImage(made.ToArray(), durations.ToArray(), srcW, srcH);
    made.Clear();   // owned by the image now
    return result2;
}
catch { return null; }
finally { foreach (var f in made) f.Dispose(); }   // frames left here belong to a load that failed
```

Note that `finally` runs after `return result2` evaluates, so the order "construct, clear, return" is what hands the frames over safely.
The code also stops checking only for exceptions: a frame that `GetPixels` reports as broken ends the animation at the frames that worked.

### 4.6 The editor call sites

```csharp
_restoring = true;
try { _theme.ApplySnapshot(snapshot); HookAll(); }
finally { _restoring = false; }
// no cache clearing here: the renderer keys pictures by file and notices files that change on disk, so undo
// neither decodes every picture again on the UI thread nor restarts videos
```

Undo replaces every widget object with a fresh one from a snapshot, but the *paths* are the same, and the cache is keyed by path. So the
pictures are found again without decoding. The test `Unchanged_images_are_decoded_once_even_after_an_undo_style_snapshot_restore` renders six
times with five restores in between and expects exactly one decode.

Removing `ClearImageCache` from the image picker has a side effect the review points out (L4): the call also restarted videos, and videos do
not have file stamps yet, so a replaced video file is no longer picked up by choosing it again.

### 4.7 Graph history in time order

The core is a lower-bound binary search:

```csharp
private static int IndexOf(List<(long ms, float value)> list, long ms)
{
    int lo = 0, hi = list.Count;
    while (lo < hi)
    {
        int mid = (lo + hi) >>> 1;
        if (list[mid].ms < ms) lo = mid + 1; else hi = mid;
    }
    return lo;
}
```

It returns the first index whose time is `>= ms`. `>>>` is C# 11's unsigned right shift; `(lo + hi) >>> 1` is the overflow-safe midpoint.
`Seed` uses it to insert in order and to skip duplicates:

```csharp
int i = IndexOf(list, ms);
if (i < list.Count && list[i].ms == ms) return;
list.Insert(i, (ms, value));
if (!_lastSample.TryGetValue(sensorId, out var last) || ms > last) _lastSample[sensorId] = ms;
```

`Window` uses it to find its start, and now always copies:

```csharp
int start = IndexOf(list, nowMs - (long)(seconds * 1000));
return list.GetRange(start, list.Count - start);
```

`SeedGraphHistory` was the real source of the zigzag. It now seeds each sensor once, for its longest graph, and only if it has no history:

```csharp
var graphs = page.Widgets.Where(w => w.Kind == WidgetKind.Graph && w.SensorId.Length > 0)
    .GroupBy(w => w.SensorId)
    .Select(g => (id: g.Key, seconds: g.Max(w => Math.Clamp(w.HistorySeconds, 5, 900))));
foreach (var (id, seconds) in graphs.ToList())
{
    if (_history.HasSamples(id)) continue;
    ...
}
```

With ordered inserts *and* the skip, both causes of the zigzag are covered: even if something did seed over real samples, they would interleave
in time order instead of drawing back to the left edge.

### 4.8 How the tests prove it

The tests could not see memory directly, so they measure proxies:

| Proxy | Used for |
|---|---|
| `face.Handle == IntPtr.Zero` | A typeface or frame was really freed. |
| `CachedImage(path).Width` | The decode size. |
| `CachedImageBytes` vs `ImageBudgetBytes` | The budget. |
| `ImageDecodes` | No decode on undo. |
| Pixel colour at the widget's centre | A replaced or late file is drawn. |
| `Sorted(window)` and counts | M11 ordering and single seeding. |

The swapped clock (`new ThemeRenderer { Now = () => now }`) lets a test move time forward by 10 s instantly, so the one-second stamp check
does not make tests slow. `Touch(path, -60)` sets file times explicitly, so a rewrite looks changed even on a file system with coarse
timestamps.

To prove the bugs existed, the author added the same internal hooks to the old code (no behaviour change) and ran the new tests: 13 of 19
failed. The reviewer reproduced that number and also confirmed the main bugs on untouched `main` using only public API and reflection.

---

## 5. Self-review: five ways to improve the change

### 1. Decode for the device, not the theme

`DrawImage` measures the box in theme units. Pass the canvas transform through:

```csharp
var device = c.TotalMatrix.MapRect(r);
LoadImage(path, Math.Max(1, (int)Math.Ceiling(device.Width)), Math.Max(1, (int)Math.Ceiling(device.Height)), w.Fit);
```

Then a theme smaller than the LCD keeps sharp pictures (review M2), and a theme larger than the LCD decodes fewer pixels. Add a test that renders
through `canvas.Scale(1.5f, 1.5f)`.

### 2. Share the animation budget fairly, and keep whole loops

Instead of "whatever is left right now", give each animation on a page an equal share of the budget, counted from the previous frame. When an
animation does not fit, keep every k-th frame and add the dropped frames' durations to the kept ones, so the loop still runs from start to end
at a lower frame rate. Remember on the entry that it was reduced, and try again when the budget allows (review M1).

### 3. Decode off the render thread, and swap only on success

Today a decode runs where the frame is drawn: on the editor's UI thread for the preview. Start decodes on a worker (`Task.Run`), keep drawing the
old picture (or the placeholder for a new file) until the new one is ready, then swap it in on the next frame. This removes the resize stutter
(review L2) and the placeholder flash on a half-written file (review L1) in one go. The entry would gain a `Task<AnimatedImage?>? Pending` field.

### 4. Move the caches out of `ThemeRenderer` into a small, testable class

`ThemeRenderer` is now half renderer, half cache manager, and the tests reach into it through seven internal hooks. A separate
`AssetCache` (pictures, font files, stamps, budget, LRU, idle sweep) with an injectable clock and an injectable "stat" function would be easier to
test directly (no rendering needed), could add stamps to videos as well (review L4), and would let a slow-stat back-off (review L5) live in one
place. `ThemeRenderer` would just ask it for `Image(path, box, fit)` and `Typeface(path)`.

### 5. Seed only what is missing, and stop copying on the draw path

Now that `Seed` inserts in order, `SeedGraphHistory` does not need the all-or-nothing `HasSamples` skip. It can fill only the gap between
`now - seconds` and the oldest existing sample, so a new 5-minute graph next to a 1-minute one is full at once (review L3) without touching real
samples. And `DrawGraph` could read the samples through an internal non-copying accessor (for example a `ReadOnlySpan` from
`CollectionsMarshal.AsSpan(list)` sliced at the window's start), keeping the copying `Window` for outside callers; a full 900 s window is
about 29 KB per call today, once per graph per frame.

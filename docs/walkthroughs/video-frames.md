# Video frames, resize restarts and silent exits: a walkthrough of the M9 fix

This walkthrough is for a mid-level developer who knows C# and has used threads and `IDisposable`, but has not worked with
native graphics objects or child processes much. It explains one change: branch `claude/fix-video-frames-758f1a`
(commits `0b69dcf` and `990461e`), which fixes finding **M9** of the full codebase review and two smaller problems.

The author's design note is `docs/design/fix-video-frames.md`; the independent review is `docs/reviews/review-video-frames.md`.

**Contents**

1. [The problem](#1-the-problem)
2. [Technologies involved](#2-technologies-involved)
3. [High-level walkthrough](#3-high-level-walkthrough)
4. [Detailed code review](#4-detailed-code-review)
5. [Self-review: five ways to improve the change](#5-self-review-five-ways-to-improve-the-change)

---

## 1. The problem

A Glance theme can have a video background. The video is not decoded inside Glance: an `ffmpeg` child process reads the file,
scales it to the widget's size and writes raw pixels (BGRA, 4 bytes per pixel) to its standard output. `VideoSource` reads those
bytes, turns each frame into an `SKImage`, and `ThemeRenderer` draws the newest one whenever it draws the page.

Video runs only in the user's own processes: the tray app (which renders the theme for the LCD) and the editor (preview and
virtual display). The tray app runs for days; the editor is where widgets are resized. On `main` there were four problems:

| Problem | What the user would see |
|---|---|
| Each new frame replaced the old `SKImage` without disposing it | Memory climbing by up to ~9 MB/s until the garbage collector's finalizer caught up; `Dispose` never freed the last frame either |
| The cache key is `path|WxH|fit`, so every pixel of a resize drag was a new key | One new `ffmpeg.exe` per mouse move (51 for a 50-pixel drag), each living ~3 s, each started **on the UI thread** |
| ffmpeg that stopped without writing an error | "Loading video..." forever |
| A `CancellationTokenSource` never disposed | Nothing visible (no timer or registration), but untidy |

---

## 2. Technologies involved

### 2.1 SkiaSharp objects are native

`SKImage`, `SKBitmap`, `SKPaint` and friends are thin .NET wrappers around C++ objects in Skia. The pixels live in **native**
memory, which the .NET garbage collector cannot see. A 480 x 320 frame is 600 KB of native memory but only a few dozen bytes of
managed object, so the GC feels no pressure to run. If you drop the wrapper without calling `Dispose`, the native memory is freed
only when the wrapper's finalizer eventually runs.

Two more facts matter here:

- Skia's objects are **reference counted** in C++. `Dispose` on the wrapper drops the wrapper's reference; if a canvas or a recorded
  picture still holds one, the native object lives on. So native code is safe. The danger is managed code calling into a wrapper
  that was already disposed: its `Handle` is `IntPtr.Zero`, and a draw with it can crash the process.
- Disposing on a different thread than the one that created the image is fine for raster images like these.

### 2.2 Producer and consumer with a "latest" slot

The decoder thread produces frames at ~15 fps; the renderer consumes them whenever it draws (every 66 ms in the editor, every
few hundred ms in the tray app). Neither waits for the other: the consumer always wants the **newest** frame and skipping frames is
fine. That is a single-slot mailbox: the producer overwrites one field, the consumer reads it.

On `main` that field was `private volatile SKImage? _latest`. `volatile` makes the swap visible across threads, but it cannot answer
"may I dispose the old frame now?": the renderer may have read the old value a microsecond ago and be in the middle of drawing it.
The usual answers are a lock held for the whole draw (slow: it blocks the decoder), double buffering, or **reference counting**:
whoever uses the frame holds a reference, and the last one to let go frees it. This change uses reference counting.

### 2.3 Child process lifetime

A redirected child process gives you three things to manage: the process handle, its stdout pipe (frames) and its stderr pipe
(error lines, read asynchronously with `BeginErrorReadLine`). The rules this change relies on:

- **End of stream means the writer is gone.** `Read` returning 0 means ffmpeg closed stdout, normally because it exited.
- **Kill before you close.** Killing ffmpeg closes its end of the pipe, so a reader blocked in `Read` wakes up. Closing our end
  first could leave the reader blocked.
- **`WaitForExit(ms)` vs `WaitForExit()`.** On .NET, only the overload without a timeout also waits until the asynchronous
  stderr reader has delivered its last line. To get the final error message, call the timed one first, then the untimed one.
- **`ArgumentList`, never a joined string.** Each argument is quoted for you, so a path with spaces, quotes or `&` stays one
  argument. Glance also only starts the exe that `Ffmpeg.Find` returns, which applies the `FolderTrust` rule when the caller is
  elevated. Neither rule may change.

### 2.4 Debouncing

Debouncing means "act only after the input has stopped changing for a while". A resize drag produces a new size every 16 ms; the
fix waits until **one** size has been asked for over 500 ms before starting a decoder for it. Meanwhile it reuses the decoder that
is already running and lets Skia stretch its frames to the new box. The renderer-memory fix used the same idea for pictures
(re-decode after 300 ms of a stable size), so the two now behave alike.

---

## 3. High-level walkthrough

### 3.1 Who does what

```
render thread (editor UI thread, or the tray's DisplayPusher loop)      "Glance video decoder" thread (one per VideoSource)
  ThemeRenderer.DrawImage                                                 Run: Ffmpeg.Find -> launch ffmpeg
    GetVideoFrame -> GetVideo (cache, debounce, retry)                    ReadLoop: read W*H*4 bytes -> SKImage -> Publish
    lease = source.AcquireFrame()       <-- lock --> _latest -->          Publish: swap _latest, Release the old frame
    canvas.DrawImage(lease.Image)                                         Ended: output closed -> Stopped + Error
    lease.Dispose()  (may free the frame)
```

The constructor of `VideoSource` now only computes the size and starts the decoder thread, so nothing slow (disk search, process
start) ever runs on the render thread.

### 3.2 The life of one frame

1. The decoder thread reads a full frame and creates an `SKImage`. It is wrapped in a `Frame` with a reference count of **1**
   (the source's own reference, because it is the newest).
2. `Publish` swaps it into `_latest` under the lock, then calls `Release` on the previous frame.
3. The renderer calls `AcquireFrame()`: under the same lock it adds a reference (count 2) and returns a lease.
4. A newer frame arrives: `Publish` releases this one (count 1). It is still valid because the lease holds it.
5. The draw finishes and the lease is disposed (count 0): the `SKImage` is disposed right away.

### 3.3 One resize drag

```
t=0     widget 100x100, decoder A (100x100) playing
t=16    size 101x100: no decoder for that key; A exists -> remember (first=16, last=16), draw A's frame stretched
t=32    size 102x100: same, new pending key ... (one pending key per pixel, no processes)
t=800   mouse stops at 150x100; that key keeps being asked for every 66 ms
t=1300  150x100 has been asked for 500 ms without a gap > 1 s -> create decoder B (thread starts ffmpeg)
t=1300+ B has no frame yet -> keep drawing A's frames stretched (no "Loading video..." flash)
        B's first frame -> draw B; A is no longer used and the sweep stops it 3 s later
```

### 3.4 A silent exit

ffmpeg closes stdout without writing to stderr (crashed, killed in Task Manager). `ReadLoop` sees `Read` return 0 and calls
`Ended`, which waits up to 2 s for the exit code and sets `Stopped` with an error such as "FFmpeg stopped without a message
(exit code 1); trying again shortly". The renderer treats `Stopped` like a missing FFmpeg: it throws the source away and makes a
new one once 5 s have passed since that source was created. Until then the widget keeps the last frame, or shows the error.

---

## 4. Detailed code review

### 4.1 The seam first: `IVideoProcess` and `FfmpegProcess`

To test frame lifetime without ffmpeg, the first commit (`0b69dcf`) puts the child process behind a small interface and changes
nothing else:

```csharp
internal interface IVideoProcess : IDisposable
{
    Stream Output { get; }
    bool WaitForExit(int ms);
    int? ExitCode { get; }
    void Kill();
}
```

The real implementation keeps the old start code, including `ArgumentList`:

```csharp
foreach (var a in args) psi.ArgumentList.Add(a);
var proc = Process.Start(psi) ?? throw new InvalidOperationException("FFmpeg did not start.");
proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onErrorLine(e.Data); };
proc.BeginErrorReadLine();
```

and the two-step wait from section 2.3:

```csharp
if (!_proc.WaitForExit(ms)) return false;
_proc.WaitForExit();   // returns at once now; also waits for the last stderr lines to be delivered
```

Why commit the seam alone? Because the tests can then be run against it to show they fail on the old behaviour. The review
re-ran them: 7 of 11 fail at `0b69dcf`, the rest of the suite passes there, and all pass at the fix.

### 4.2 Nothing slow on the caller's thread

The constructor used to call `findFfmpeg()` and `Process.Start` directly. Now it stores its inputs and starts a dedicated thread:

```csharp
(_path, _fit, _fps, _findFfmpeg, _lastRejection, _launch) = (path, fit, fps, findFfmpeg, lastRejection, launch);
// its own thread: the read loop blocks for the life of the video, which would hold a thread-pool thread
new Thread(Run) { IsBackground = true, Name = "Glance video decoder" }.Start();
```

A dedicated thread is the right choice for a loop that blocks for minutes: `Task.Run` would hold a thread-pool thread the whole
time, and the pool grows slowly when its threads are blocked. `IsBackground = true` means the thread never keeps the process alive.

The flags the renderer reads (`FfmpegMissing`, `Stopped`, `Error`) are now set later, from that thread, so they became
`volatile` fields. That is also why `VideoRetryTests` now waits for `FfmpegMissing` before it goes on.

### 4.3 Reference-counted frames and leases

The frame:

```csharp
internal sealed class Frame
{
    private readonly VideoSource _owner;
    private int _refs = 1;
    ...
    public void AddRef() => Interlocked.Increment(ref _refs);

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) != 0) return;
        Image.Dispose();
        Interlocked.Decrement(ref _owner._liveFrames);
    }
}
```

Publishing a new frame swaps under the lock and releases the old one **outside** it, so disposing native memory never holds up a
drawer:

```csharp
lock (_gate)
{
    if (_disposed) old = frame;
    else { old = _latest; _latest = frame; }
}
old?.Release();   // the source's own reference; a drawer that still borrows it keeps it alive
```

Borrowing takes the reference **inside** the lock. This is the key line of the whole fix: if `AddRef` happened after the lock,
`Publish` could release the frame to zero in between and the drawer would hold a freed image.

```csharp
lock (_gate)
{
    if (_latest is null) return VideoFrameLease.Empty;
    _latest.AddRef();   // under the lock, so the source cannot drop its reference in between
    return new VideoFrameLease(_latest);
}
```

The lease makes a second `Dispose` harmless with `Interlocked.Exchange`, so a caller cannot release twice by mistake:

```csharp
public void Dispose() => Interlocked.Exchange(ref _frame, null)?.Release();
```

On the renderer side, `DrawImage` is split so a `finally` always returns the lease, even if drawing throws:

```csharp
VideoFrameLease? lease = null;   // a video frame stays valid until the draw is done
try { DrawImage(c, t, w, ref lease); }
finally { lease?.Dispose(); }
```

The old public `Latest` property is gone, so no caller can get an image without a lease.

### 4.4 Dispose and its races

`Dispose` can run on any thread at any moment of the decoder's life. It flips `_disposed` and takes both the process and the
newest frame under the lock, then does the slow work outside it:

```csharp
lock (_gate)
{
    if (_disposed) return;
    _disposed = true;
    (proc, _proc) = (_proc, null);
    (last, _latest) = (_latest, null);
}
last?.Release();
if (proc is not null) StopProcess(proc);
```

The decoder thread checks the same flag at each step:

- **Before starting ffmpeg:** `lock (_gate) if (_disposed) return;`, so a source disposed while `Ffmpeg.Find` ran never starts one.
- **Right after starting it:** if `Dispose` ran during `Process.Start`, the new process is stopped at once (`if (!keep) { StopProcess(proc); return; }`).
- **When the read fails or ends:** if disposed, it returns quietly instead of reporting an error.
- **A frame that arrives after `Dispose`** goes through `Publish`, which releases it immediately.

`StopProcess` kills first, then disposes, which is the order section 2.3 asks for.

### 4.5 Silent exits and the retry

```csharp
private void Ended(IVideoProcess proc)
{
    lock (_gate) if (_disposed) return;
    int? code = null;
    try { if (proc.WaitForExit(2000)) code = proc.ExitCode; } catch { }
    Stop(_error ?? (code is { } c
        ? $"FFmpeg stopped without a message (exit code {c}); trying again shortly"
        : "FFmpeg stopped sending frames without a message; trying again shortly"));
}
```

ffmpeg's own last error line wins; otherwise there is always a message. In the renderer the retry is the existing one for a
missing FFmpeg, widened by one condition:

```csharp
if (_videos.TryGetValue(key, out var v) && (v.FfmpegMissing || v.Stopped) && now - v.CreatedTicks >= VideoRetryMs)
```

Because the clock counts from when the source was **created**, a broken file gets at most one ffmpeg every 5 s: no spawn storm.
A video that played for an hour and then died is restarted on the next frame, which is what you want.

### 4.6 The renderer: debounce and fallback

When there is no decoder for the exact key, the renderer looks for another decoder of the same file and, if there is one, waits:

```csharp
other = OtherVideo(path, key);
if (other is not null && !VideoSizeSettled(key, now)) return null;
_pendingVideos.Remove(key);
// cheap: the source finds and starts ffmpeg on its own thread, never on the rendering (UI) thread
_videos[key] = v = VideoFactory(path, w, h, fit);
```

A file with no decoder at all starts at once, so opening a theme is not delayed. The debounce itself is two timestamps per size:

```csharp
if (!_pendingVideos.TryGetValue(key, out var p) || now - p.Last > VideoSizeGapMs) p = (now, now);
else p.Last = now;
_pendingVideos[key] = p;
return now - p.First >= VideoSettleMs;
```

`First` is when this size was first asked for, `Last` the most recent time. A gap of more than 1 s starts the wait again, so a
size you passed through earlier in the drag does not count as "stable". `GetVideoFrame` then picks what to draw: the exact
decoder's frame if it has one, otherwise the other decoder's newest frame (and it keeps that decoder alive by touching its
`LastUsedTicks`), otherwise a note. Because the image is drawn with `ImageFit.Stretch` into the widget's box, a frame of the old
size simply stretches.

Two interactions with the earlier renderer-memory fix: the file stamp check (a replaced video restarts) still disposes every
decoder of that file, after which there is no "other" and the new decoder starts at once; and the sweep that forgets file checks
still sees the fallback decoder in `_videos`, so the check is kept during a drag.

### 4.7 How the tests prove it

`FakeVideoProcess` stands in for ffmpeg: the test pushes frames of one colour, writes stderr lines, and decides when it exits. The
frame size is read back from the `scale=W:H` filter, so the fake also checks that the right size was asked for. Some highlights:

- **Freed frames:** a frame's `Handle` turns `IntPtr.Zero` when it is disposed, so the tests can check lifetime directly:
  `Assert.True(first.Handle == IntPtr.Zero, "frame 1 was replaced but not disposed")`.
- **Borrowed frames stay valid:** the test holds a lease, pushes a new frame, draws the held one and checks the pixel is still red.
- **Threads:** 300 frames are pushed from one task while the test thread borrows and draws; afterwards every image seen must be freed.
- **Resize drag:** 50 renders, each one pixel wider and 16 ms later on a fake clock, must start no new ffmpeg and still draw
  red at x=140 (the old frame stretched); 600 ms later exactly one new ffmpeg starts at 150x100, not on the test thread.
- **Real process:** `cmd /c exit 3` checks the exit code path of `FfmpegProcess`; a path with spaces, quotes and `&` must reach
  ffmpeg as one argument; when FFmpeg is installed, a 1 s test clip it generates must keep producing frames that are freed.

The author also broke the fix on purpose (no release in `Publish`; `VideoSettleMs = 0`) and saw the tests fail. The reviewer
removed `|| v.Stopped` (one test failed) and the `AddRef` in `AcquireFrame` (the test host crashed: see suggestion 5).

---

## 5. Self-review: five ways to improve the change

### 1. Make the settle gap follow the renderer's speed

`VideoSizeGapMs` is a fixed second. A renderer that draws less often than that (the tray app while USB is backing off, a very slow
page) restarts the wait on every frame, so the new size never gets its own decoder and stays soft. Restart the wait only when a
*different* size of the same file was asked for in between, or set the gap to a few times the measured frame interval. Add a test
that renders every 1.2 s.

### 2. Stop ffmpeg off the UI thread

`Dispose` still calls `Process.Kill(true)` on the caller's thread. `Kill(entireProcessTree: true)` lists every process on the PC to
find children, which can take tens of milliseconds, and in the editor that is the UI thread (sweep after a resize, retry, closing a
theme). ffmpeg starts no children, so `Kill()` is enough, and `StopProcess` can be handed to the thread pool. The frame and flag
handling stay synchronous, so nothing else changes.

### 3. Back off a file that keeps failing

A permanently broken file starts a short-lived ffmpeg every 5 s for as long as it is on screen, and the error is never logged.
Double the wait after each failure up to about a minute, reset it after a run that produced frames, and write the error to
`runner.log` once. Keep the retry clock based on creation time so a long run is still restarted at once.

### 4. Reuse frame memory

Frames are now freed at once, but each one is still a fresh 600 KB native allocation (up to 8 MB at the 1920 x 1080 cap), 15
times a second. A small pool of two or three pixel buffers, wrapped with `SKImage.FromPixels` and a release delegate that returns
the buffer to the pool when the last reference goes, would remove that churn. The reference counting from 4.3 is exactly what
such a pool needs to know when a buffer is free again.

### 5. Make lifetime bugs fail loudly, and give the video cache one clock

If a frame were ever drawn after being freed, the tests would not fail with a name; the test host would crash and abort the run.
Assert `img.Handle != IntPtr.Zero` before every draw in the threaded test so a regression shows up as one red test. While there,
the debounce uses the renderer's injectable `Now()` but `LastUsedTicks` and the 3 s sweep use `Environment.TickCount64`; moving the
video cache (`_videos`, `_pendingVideos`, `_videoFiles`) into a small class with a single injected clock would make the sweep and
the fallback testable on a fake clock too, and would take the video bookkeeping out of `ThemeRenderer`.

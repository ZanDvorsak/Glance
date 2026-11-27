using SkiaSharp;

namespace Glance.Core.Media;

public enum ImageFit { Stretch, Contain, Cover }

/// <summary>
/// Plays a video in a loop by piping raw BGRA frames out of ffmpeg, already scaled to the widget size.
/// <para>
/// Nothing slow happens on the caller's thread: finding ffmpeg, starting it and reading its frames all run on the
/// source's own background thread. Frames are reference counted: the source holds the newest one, a drawer borrows it
/// with <see cref="AcquireFrame"/>, and a frame is disposed as soon as it is neither the newest nor borrowed
/// (review M9: every frame used to be left to the finalizer, about 9 MB/s at 15 fps).
/// </para>
/// </summary>
public sealed class VideoSource : IDisposable
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly ImageFit _fit;
    private readonly int _fps;
    private readonly Func<string?> _findFfmpeg;
    private readonly Func<string?> _lastRejection;
    private readonly Func<string, IReadOnlyList<string>, Action<string>, IVideoProcess> _launch;
    private IVideoProcess? _proc;      // guarded by _gate
    private Frame? _latest;            // guarded by _gate
    private bool _disposed;            // guarded by _gate
    private volatile string? _error;
    private volatile bool _ffmpegMissing, _stopped;
    private int _framesRead, _liveFrames;

    public int Width { get; }
    public int Height { get; }
    public long LastUsedTicks { get; set; } = Environment.TickCount64;

    /// <summary>What went wrong (last ffmpeg error line, or why it is not playing), or null.</summary>
    public string? Error => _error;

    /// <summary>True when no usable ffmpeg.exe was found, so nothing is playing (the renderer may try again later).</summary>
    public bool FfmpegMissing => _ffmpegMissing;

    /// <summary>True when ffmpeg could not start or stopped by itself; <see cref="Error"/> says why. The renderer starts it again later.</summary>
    public bool Stopped => _stopped;

    /// <summary>When the renderer created this source (its own clock), to pace retries.</summary>
    public long CreatedTicks { get; internal set; }

    /// <summary>Frames read from the decoder so far (tests wait on it).</summary>
    internal int FramesRead => Volatile.Read(ref _framesRead);

    /// <summary>Frames decoded and not yet disposed (the newest one plus any still borrowed).</summary>
    internal int LiveFrames => Volatile.Read(ref _liveFrames);

    public VideoSource(string path, int width, int height, ImageFit fit, int fps = 15)
        : this(path, width, height, fit, fps, Ffmpeg.Find, () => Ffmpeg.LastRejection) { }

    /// <summary>Largest frame ffmpeg is asked for (1920 x 1080 worth of pixels, about 8 MiB per frame). The display is
    /// 480 x 320; a bigger widget gets a smaller frame with the same aspect, which the renderer stretches to the box.
    /// Without the cap an 8192 x 8192 widget from a shared theme made the (elevated) runner allocate 256 MiB per frame.</summary>
    public const int MaxPixels = 1920 * 1080;

    internal VideoSource(string path, int width, int height, ImageFit fit, int fps, Func<string?> findFfmpeg, Func<string?> lastRejection)
        : this(path, width, height, fit, fps, findFfmpeg, lastRejection, FfmpegProcess.Start) { }

    internal VideoSource(string path, int width, int height, ImageFit fit, int fps, Func<string?> findFfmpeg, Func<string?> lastRejection,
        Func<string, IReadOnlyList<string>, Action<string>, IVideoProcess> launch)
    {
        (Width, Height) = CapSize(Math.Max(2, width), Math.Max(2, height));
        (_path, _fit, _fps, _findFfmpeg, _lastRejection, _launch) = (path, fit, fps, findFfmpeg, lastRejection, launch);
        // its own thread: the read loop blocks for the life of the video, which would hold a thread-pool thread
        new Thread(Run) { IsBackground = true, Name = "Glance video decoder" }.Start();
    }

    /// <summary>Scales (w, h) down, keeping the aspect, until it fits in <see cref="MaxPixels"/>.</summary>
    internal static (int w, int h) CapSize(int w, int h)
    {
        long px = (long)w * h;
        if (px <= MaxPixels) return (w, h);
        double s = Math.Sqrt((double)MaxPixels / px);
        return (Math.Max(2, (int)(w * s)), Math.Max(2, (int)(h * s)));
    }

    /// <summary>The ffmpeg arguments, one list item each (they go to <c>ProcessStartInfo.ArgumentList</c>, never a joined string).</summary>
    internal IReadOnlyList<string> Arguments()
    {
        var scale = _fit switch
        {
            ImageFit.Contain => $"scale={Width}:{Height}:force_original_aspect_ratio=decrease,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2:color=black",
            ImageFit.Cover => $"scale={Width}:{Height}:force_original_aspect_ratio=increase,crop={Width}:{Height}",
            _ => $"scale={Width}:{Height}",
        };
        // -re reads the file at its native speed; without it ffmpeg decodes as fast as the CPU allows and the video plays far too fast
        return new[] { "-hide_banner", "-loglevel", "error", "-stream_loop", "-1", "-re", "-i", _path, "-an", "-vf", $"{scale},fps={_fps}", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" };
    }

    private void Run()
    {
        IVideoProcess proc;
        try
        {
            var ffmpeg = _findFfmpeg();
            if (ffmpeg is null)
            {
                _error = _lastRejection() is not null ? "FFmpeg is only in a folder normal users can change (see runner.log)" : "FFmpeg not found";
                _ffmpegMissing = true;
                return;
            }
            lock (_gate) if (_disposed) return;
            proc = _launch(ffmpeg, Arguments(), line => _error = line);
        }
        catch (Exception ex) { Stop(ex.Message); return; }

        bool keep;
        lock (_gate)
        {
            keep = !_disposed;
            if (keep) _proc = proc;
        }
        if (!keep) { StopProcess(proc); return; }   // disposed while it was starting

        ReadLoop(proc);
    }

    private void ReadLoop(IVideoProcess proc)
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var buf = new byte[Width * Height * 4];
        try
        {
            var stream = proc.Output;
            while (true)
            {
                int got = 0;
                while (got < buf.Length)
                {
                    int n = stream.Read(buf, got, buf.Length - got);
                    if (n <= 0) { Ended(proc); return; }
                    got += n;
                }
                var image = SKImage.FromPixelCopy(info, buf);
                if (image is null) continue;
                Interlocked.Increment(ref _liveFrames);
                Publish(new Frame(image, this));
                Interlocked.Increment(ref _framesRead);
            }
        }
        catch (Exception ex)
        {
            lock (_gate) if (_disposed) return;   // killed by Dispose
            Stop(ex.Message);
        }
    }

    /// <summary>ffmpeg's output ended. Unless we stopped it, say why, even when ffmpeg itself said nothing.</summary>
    private void Ended(IVideoProcess proc)
    {
        lock (_gate) if (_disposed) return;
        int? code = null;
        try { if (proc.WaitForExit(2000)) code = proc.ExitCode; } catch { }
        Stop(_error ?? (code is { } c
            ? $"FFmpeg stopped without a message (exit code {c}); trying again shortly"
            : "FFmpeg stopped sending frames without a message; trying again shortly"));
    }

    private void Stop(string error)
    {
        _error = string.IsNullOrWhiteSpace(error) ? "FFmpeg stopped" : error;
        _stopped = true;
    }

    private void Publish(Frame frame)
    {
        Frame? old;
        lock (_gate)
        {
            if (_disposed) old = frame;
            else { old = _latest; _latest = frame; }
        }
        old?.Release();   // the source's own reference; a drawer that still borrows it keeps it alive
    }

    /// <summary>
    /// Borrows the newest frame for drawing (its <see cref="VideoFrameLease.Image"/> is null when there is none yet).
    /// The image stays valid, even if a newer frame arrives, until the lease is disposed. Any thread.
    /// </summary>
    public VideoFrameLease AcquireFrame()
    {
        lock (_gate)
        {
            if (_latest is null) return VideoFrameLease.Empty;
            _latest.AddRef();   // under the lock, so the source cannot drop its reference in between
            return new VideoFrameLease(_latest);
        }
    }

    /// <summary>Stops ffmpeg and frees the newest frame (borrowed frames are freed when their lease is disposed). Any thread.</summary>
    public void Dispose()
    {
        IVideoProcess? proc;
        Frame? last;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            (proc, _proc) = (_proc, null);
            (last, _latest) = (_latest, null);
        }
        last?.Release();
        // killing a process can take a while and Dispose may be called on the editor's UI thread (review L2). If this
        // process ends first, ffmpeg still stops at its next frame: its output pipe is gone.
        if (proc is not null) ThreadPool.UnsafeQueueUserWorkItem(static p => StopProcess(p), proc, preferLocal: false);
    }

    private static void StopProcess(IVideoProcess proc)
    {
        try { proc.Kill(); } catch { }
        try { proc.Dispose(); } catch { }
    }

    /// <summary>One decoded frame with a reference count: one for the source while it is the newest, one per lease.</summary>
    internal sealed class Frame
    {
        private readonly VideoSource _owner;
        private int _refs = 1;

        public Frame(SKImage image, VideoSource owner) => (Image, _owner) = (image, owner);

        public SKImage Image { get; }

        public void AddRef() => Interlocked.Increment(ref _refs);

        public void Release()
        {
            if (Interlocked.Decrement(ref _refs) != 0) return;
            Image.Dispose();
            Interlocked.Decrement(ref _owner._liveFrames);
        }
    }
}

/// <summary>A video frame lent to a drawer. The image stays valid until the lease is disposed; disposing twice is harmless.</summary>
public sealed class VideoFrameLease : IDisposable
{
    internal static readonly VideoFrameLease Empty = new(null);
    private VideoSource.Frame? _frame;

    internal VideoFrameLease(VideoSource.Frame? frame) { _frame = frame; Image = frame?.Image; }

    /// <summary>The frame, or null when the video has none yet.</summary>
    public SKImage? Image { get; }

    public void Dispose() => Interlocked.Exchange(ref _frame, null)?.Release();
}

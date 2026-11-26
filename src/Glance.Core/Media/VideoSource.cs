using SkiaSharp;

namespace Glance.Core.Media;

public enum ImageFit { Stretch, Contain, Cover }

/// <summary>Plays a video in a loop by piping raw BGRA frames out of ffmpeg, already scaled to the widget size.</summary>
public sealed class VideoSource : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private IVideoProcess? _proc;
    private volatile SKImage? _latest;
    private int _framesRead;

    public int Width { get; }
    public int Height { get; }
    public long LastUsedTicks { get; set; } = Environment.TickCount64;
    public string? Error { get; private set; }
    public SKImage? Latest => _latest;

    /// <summary>The newest frame, for drawing. Dispose the lease when the draw is done.</summary>
    public VideoFrameLease AcquireFrame() => new(_latest);

    /// <summary>Frames read from the decoder so far (tests wait on it).</summary>
    internal int FramesRead => Volatile.Read(ref _framesRead);

    /// <summary>True when no usable ffmpeg.exe was found, so nothing is playing (the renderer may try again later).</summary>
    public bool FfmpegMissing { get; }

    /// <summary>When the renderer created this source (its own clock), to pace retries.</summary>
    public long CreatedTicks { get; internal set; }

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
        var ffmpeg = findFfmpeg();
        if (ffmpeg is null)
        {
            FfmpegMissing = true;
            Error = lastRejection() is not null ? "FFmpeg is only in a folder normal users can change (see runner.log)" : "FFmpeg not found";
            return;
        }

        var scale = fit switch
        {
            ImageFit.Contain => $"scale={Width}:{Height}:force_original_aspect_ratio=decrease,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2:color=black",
            ImageFit.Cover => $"scale={Width}:{Height}:force_original_aspect_ratio=increase,crop={Width}:{Height}",
            _ => $"scale={Width}:{Height}",
        };
        // -re reads the file at its native speed; without it ffmpeg decodes as fast as the CPU allows and the video plays far too fast
        var args = new[] { "-hide_banner", "-loglevel", "error", "-stream_loop", "-1", "-re", "-i", path, "-an", "-vf", $"{scale},fps={fps}", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" };
        try { _proc = launch(ffmpeg, args, line => Error = line); }
        catch (Exception ex) { Error = ex.Message; return; }
        Task.Run(ReadLoop);
    }

    /// <summary>Scales (w, h) down, keeping the aspect, until it fits in <see cref="MaxPixels"/>.</summary>
    internal static (int w, int h) CapSize(int w, int h)
    {
        long px = (long)w * h;
        if (px <= MaxPixels) return (w, h);
        double s = Math.Sqrt((double)MaxPixels / px);
        return (Math.Max(2, (int)(w * s)), Math.Max(2, (int)(h * s)));
    }

    private void ReadLoop()
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var buf = new byte[Width * Height * 4];
        try
        {
            var stream = _proc!.Output;
            while (!_cts.IsCancellationRequested)
            {
                int got = 0;
                while (got < buf.Length)
                {
                    int n = stream.Read(buf, got, buf.Length - got);
                    if (n <= 0) return;
                    got += n;
                }
                _latest = SKImage.FromPixelCopy(info, buf);
                Interlocked.Increment(ref _framesRead);
            }
        }
        catch { /* process killed */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _proc?.Kill();
        _proc?.Dispose();
    }
}

/// <summary>A frame lent to a drawer. The image stays valid until the lease is disposed.</summary>
public readonly struct VideoFrameLease : IDisposable
{
    internal VideoFrameLease(SKImage? image) => Image = image;
    public SKImage? Image { get; }
    public void Dispose() { }
}

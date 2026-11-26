using System.Diagnostics;
using SkiaSharp;

namespace Glance.Core.Media;

public enum ImageFit { Stretch, Contain, Cover }

/// <summary>Plays a video in a loop by piping raw BGRA frames out of ffmpeg, already scaled to the widget size.</summary>
public sealed class VideoSource : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private Process? _proc;
    private volatile SKImage? _latest;

    public int Width { get; }
    public int Height { get; }
    public long LastUsedTicks { get; set; } = Environment.TickCount64;
    public string? Error { get; private set; }
    public SKImage? Latest => _latest;

    /// <summary>True when no usable ffmpeg.exe was found, so nothing is playing (the renderer may try again later).</summary>
    public bool FfmpegMissing { get; }

    /// <summary>When the renderer created this source (its own clock), to pace retries.</summary>
    public long CreatedTicks { get; internal set; }

    public VideoSource(string path, int width, int height, ImageFit fit, int fps = 15)
        : this(path, width, height, fit, fps, Ffmpeg.Find, () => Ffmpeg.LastRejection) { }

    internal VideoSource(string path, int width, int height, ImageFit fit, int fps, Func<string?> findFfmpeg, Func<string?> lastRejection)
    {
        Width = Math.Max(2, width); Height = Math.Max(2, height);
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
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        // -re reads the file at its native speed; without it ffmpeg decodes as fast as the CPU allows and the video plays far too fast
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-stream_loop", "-1", "-re", "-i", path, "-an", "-vf", $"{scale},fps={fps}", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1" })
            psi.ArgumentList.Add(a);
        try { _proc = Process.Start(psi); }
        catch (Exception ex) { Error = ex.Message; return; }
        _proc!.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Error = e.Data; };
        _proc.BeginErrorReadLine();
        Task.Run(ReadLoop);
    }

    private void ReadLoop()
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var buf = new byte[Width * Height * 4];
        try
        {
            var stream = _proc!.StandardOutput.BaseStream;
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
            }
        }
        catch { /* process killed */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        _proc?.Dispose();
    }
}

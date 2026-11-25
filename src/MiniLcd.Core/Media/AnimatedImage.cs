using SkiaSharp;

namespace MiniLcd.Core.Media;

/// <summary>A still image or an animated GIF/WebP, fully decoded into memory.</summary>
public sealed class AnimatedImage : IDisposable
{
    private readonly int[] _durations;
    private readonly int _total;

    public SKImage[] Frames { get; }
    public int Width => Frames[0].Width;
    public int Height => Frames[0].Height;
    public bool IsAnimated => Frames.Length > 1;

    private AnimatedImage(SKImage[] frames, int[] durations)
    {
        Frames = frames; _durations = durations; _total = Math.Max(1, durations.Sum());
    }

    /// <summary>The frame to show <paramref name="ms"/> milliseconds after playback started (loops).</summary>
    public SKImage At(long ms)
    {
        if (Frames.Length == 1) return Frames[0];
        var t = (int)(ms % _total);
        for (int i = 0; i < Frames.Length; i++)
        {
            if (t < _durations[i]) return Frames[i];
            t -= _durations[i];
        }
        return Frames[^1];
    }

    public static AnimatedImage? Load(string path, long maxBytes = 600L * 1024 * 1024)
    {
        try
        {
            using var data = SKData.Create(path);
            if (data is null) return null;
            using var codec = SKCodec.Create(data);
            if (codec is null) return null;

            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            int n = Math.Max(1, codec.FrameCount);
            long perFrame = (long)info.Width * info.Height * 4;
            if (n > 1) n = (int)Math.Min(n, Math.Max(1, maxBytes / perFrame)); // cap memory on huge animations

            if (n == 1)
            {
                using var bmp = new SKBitmap(info);
                codec.GetPixels(info, bmp.GetPixels());
                return new AnimatedImage(new[] { SKImage.FromBitmap(bmp) }, new[] { 100 });
            }

            var infos = codec.FrameInfo;
            var bitmaps = new SKBitmap[n];
            var frames = new SKImage[n];
            var durations = new int[n];
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var bmp = new SKBitmap(info);
                    var opts = new SKCodecOptions(i);
                    var req = infos[i].RequiredFrame;
                    if (req >= 0 && req < i)
                    {
                        opts.PriorFrame = req;
                        bitmaps[req].GetPixelSpan().CopyTo(bmp.GetPixelSpan());
                    }
                    codec.GetPixels(info, bmp.GetPixels(), bmp.RowBytes, opts);
                    bitmaps[i] = bmp;
                    frames[i] = SKImage.FromBitmap(bmp);
                    durations[i] = infos[i].Duration <= 0 ? 100 : infos[i].Duration;
                }
            }
            finally { foreach (var b in bitmaps) b?.Dispose(); }
            return new AnimatedImage(frames, durations);
        }
        catch { return null; }
    }

    public void Dispose() { foreach (var f in Frames) f.Dispose(); }
}

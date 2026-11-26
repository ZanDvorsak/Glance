using SkiaSharp;

namespace Glance.Core.Media;

/// <summary>
/// A still image or an animated GIF/WebP, decoded into memory. It can be decoded smaller than the file (never larger)
/// so it keeps only the pixels the widget needs.
/// </summary>
public sealed class AnimatedImage : IDisposable
{
    private readonly int[] _durations;
    private readonly int _total;

    public SKImage[] Frames { get; }
    public int Width => Frames[0].Width;
    public int Height => Frames[0].Height;
    public bool IsAnimated => Frames.Length > 1;

    /// <summary>Size of the picture in the file (the decoded frames may be smaller).</summary>
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    /// <summary>True when the frames have every pixel of the file.</summary>
    public bool IsFullSize => Width >= SourceWidth && Height >= SourceHeight;

    /// <summary>Decoded pixel memory (4 bytes per pixel, every frame).</summary>
    public long Bytes => Frames.Sum(f => (long)f.Width * f.Height * 4);

    /// <summary>Test hook: called on the loading thread after each frame image is created.</summary>
    [ThreadStatic] internal static Action<int, SKImage>? FrameCreated;

    /// <summary>The old per-image limit; callers with a memory budget pass a smaller one.</summary>
    public const long DefaultMaxBytes = 600L * 1024 * 1024;

    /// <summary>Largest side a box may ask for, so a huge widget cannot force a huge decode.</summary>
    public const int MaxBoxSide = 4096;

    private AnimatedImage(SKImage[] frames, int[] durations, int sourceWidth, int sourceHeight)
    {
        Frames = frames; _durations = durations; _total = Math.Max(1, durations.Sum());
        SourceWidth = sourceWidth; SourceHeight = sourceHeight;
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

    /// <summary>
    /// How much a <paramref name="srcW"/> x <paramref name="srcH"/> picture can be shrunk (0..1] and still have a pixel
    /// for every screen pixel of a <paramref name="boxW"/> x <paramref name="boxH"/> box drawn with <paramref name="fit"/>.
    /// A box of 0 means "full size".
    /// </summary>
    public static double ScaleFor(int srcW, int srcH, int boxW, int boxH, ImageFit fit)
    {
        if (boxW <= 0 || boxH <= 0 || srcW <= 0 || srcH <= 0) return 1;
        boxW = Math.Min(boxW, MaxBoxSide); boxH = Math.Min(boxH, MaxBoxSide);
        double sx = (double)boxW / srcW, sy = (double)boxH / srcH;
        // Contain draws the whole picture inside the box (the smaller scale); Stretch and Cover fill both sides
        var s = fit == ImageFit.Contain ? Math.Min(sx, sy) : Math.Max(sx, sy);
        return Math.Clamp(s, 1e-6, 1);
    }

    /// <summary>True when this image has enough pixels to draw in the box without going back to the file.</summary>
    public bool IsSharpEnoughFor(int boxW, int boxH, ImageFit fit)
    {
        if (IsFullSize) return true;
        var s = ScaleFor(SourceWidth, SourceHeight, boxW, boxH, fit);
        return Width + 1 >= Math.Ceiling(SourceWidth * s) && Height + 1 >= Math.Ceiling(SourceHeight * s);
    }

    /// <summary>
    /// Decodes a file. With a box, frames are decoded at the size the box needs (see <see cref="ScaleFor"/>);
    /// animations keep only as many frames as fit in <paramref name="maxBytes"/> (always at least one).
    /// Returns null when the file is missing or cannot be decoded.
    /// </summary>
    public static AnimatedImage? Load(string path, long maxBytes = DefaultMaxBytes, int boxWidth = 0, int boxHeight = 0, ImageFit fit = ImageFit.Stretch)
    {
        var made = new List<SKImage>();
        try
        {
            using var data = SKData.Create(path);
            if (data is null) return null;
            using var codec = SKCodec.Create(data);
            if (codec is null) return null;

            int srcW = codec.Info.Width, srcH = codec.Info.Height;
            if (srcW <= 0 || srcH <= 0) return null;
            var scale = ScaleFor(srcW, srcH, boxWidth, boxHeight, fit);
            int tw = Math.Max(1, (int)Math.Ceiling(srcW * scale)), th = Math.Max(1, (int)Math.Ceiling(srcH * scale));
            var target = new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul);
            var full = new SKImageInfo(srcW, srcH, SKColorType.Bgra8888, SKAlphaType.Premul);

            int n = Math.Max(1, codec.FrameCount);
            long perFrame = (long)tw * th * 4;
            if (n > 1) n = (int)Math.Min(n, Math.Max(1, maxBytes / perFrame)); // cap memory on long animations

            SKImage Keep(SKImage img) { made.Add(img); FrameCreated?.Invoke(made.Count - 1, img); return img; }

            if (n == 1)
            {
                var img = DecodeStill(codec, full, target);
                if (img is null) return null;
                var still = new AnimatedImage(new[] { Keep(img) }, new[] { 100 }, srcW, srcH);
                made.Clear();   // owned by the image now
                return still;
            }

            var infos = codec.FrameInfo;
            // frames that a later frame builds on but that are not simply the previous one must stay at full size
            var reused = new HashSet<int>();
            for (int j = 0; j < n; j++)
                if (infos[j].RequiredFrame is var r && r >= 0 && r != j - 1) reused.Add(r);

            var kept = new Dictionary<int, SKBitmap>();
            SKBitmap? prev = null;
            var durations = new List<int>();
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var bmp = new SKBitmap(full);
                    var opts = new SKCodecOptions(i);
                    var req = infos[i].RequiredFrame;
                    if (req >= 0 && req < i)
                    {
                        var basis = req == i - 1 ? prev : kept.GetValueOrDefault(req);
                        if (basis is null) { bmp.Dispose(); break; }   // cannot happen for a valid file; stop the animation here
                        opts.PriorFrame = req;
                        basis.GetPixelSpan().CopyTo(bmp.GetPixelSpan());
                    }
                    var result = codec.GetPixels(full, bmp.GetPixels(), bmp.RowBytes, opts);
                    if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                    {
                        bmp.Dispose();
                        if (i == 0) return null;
                        break;   // show the frames that did decode
                    }
                    Keep(Shrink(bmp, target));
                    durations.Add(infos[i].Duration <= 0 ? 100 : infos[i].Duration);

                    if (prev is not null && !kept.ContainsValue(prev)) prev.Dispose();
                    if (reused.Contains(i)) kept[i] = bmp;
                    prev = bmp;
                }
            }
            finally
            {
                if (prev is not null && !kept.ContainsValue(prev)) prev.Dispose();
                foreach (var b in kept.Values) b.Dispose();
            }
            var result2 = new AnimatedImage(made.ToArray(), durations.ToArray(), srcW, srcH);
            made.Clear();   // owned by the image now
            return result2;
        }
        catch { return null; }
        finally { foreach (var f in made) f.Dispose(); }   // frames left here belong to a load that failed
    }

    /// <summary>
    /// A still picture at the target size. Formats that can decode smaller themselves (JPEG) do so, which avoids
    /// ever holding the full-size picture.
    /// </summary>
    private static SKImage? DecodeStill(SKCodec codec, SKImageInfo full, SKImageInfo target)
    {
        var info = full;
        if (target.Width < full.Width)
        {
            var scaled = codec.GetScaledDimensions((float)target.Width / full.Width);
            if (scaled.Width >= target.Width && scaled.Height >= target.Height && scaled.Width < full.Width)
                info = full.WithSize(scaled.Width, scaled.Height);
        }
        using var bmp = new SKBitmap(info);
        var result = codec.GetPixels(info, bmp.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;
        return Shrink(bmp, target);
    }

    /// <summary>A copy of <paramref name="bmp"/> at the target size (an exact copy when it already is that size).</summary>
    private static SKImage Shrink(SKBitmap bmp, SKImageInfo target)
    {
        if (bmp.Width == target.Width && bmp.Height == target.Height) return SKImage.FromBitmap(bmp);
        using var small = new SKBitmap(target);
        using (var src = bmp.PeekPixels())
        using (var dst = small.PeekPixels())
            src.ScalePixels(dst, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        return SKImage.FromBitmap(small);
    }

    public void Dispose() { foreach (var f in Frames) f.Dispose(); }
}

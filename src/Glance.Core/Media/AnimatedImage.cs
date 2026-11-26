using SkiaSharp;

namespace Glance.Core.Media;

/// <summary>
/// A still image or an animated GIF/WebP, decoded into memory. It can be decoded smaller than the file (never larger)
/// so it keeps only the pixels the widget needs, and a long animation can keep every k-th frame to fit a memory cap
/// (the loop keeps its length).
/// </summary>
public sealed class AnimatedImage : IDisposable
{
    private readonly int[] _durations;
    private readonly int _total;

    public SKImage[] Frames { get; }
    public int Width => Frames[0].Width;
    public int Height => Frames[0].Height;
    public bool IsAnimated => Frames.Length > 1;

    /// <summary>Size of the picture in the file (the decoded frames may be smaller, and stretched to another shape).</summary>
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    /// <summary>Frames in the file (the decoded animation may keep fewer).</summary>
    public int SourceFrameCount { get; }

    /// <summary>True when frames were left out to fit the memory cap.</summary>
    public bool IsCut => Frames.Length < SourceFrameCount;

    /// <summary>Decoded pixel memory (4 bytes per pixel, every frame).</summary>
    public long Bytes => Frames.Sum(f => (long)f.Width * f.Height * 4);

    /// <summary>Length of one loop in milliseconds.</summary>
    internal int TotalMs => _total;

    /// <summary>Test hook: called on the loading thread after each frame image is created.</summary>
    [ThreadStatic] internal static Action<int, SKImage>? FrameCreated;

    /// <summary>Test hook: called on the loading thread for each full-size working bitmap of an animation.</summary>
    [ThreadStatic] internal static Action<SKBitmap>? WorkingBitmapCreated;

    /// <summary>Where notes about shortened animations go (the owner may route it to its log).</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>The old per-image limit; callers with a memory budget pass a smaller one.</summary>
    public const long DefaultMaxBytes = 600L * 1024 * 1024;

    /// <summary>Largest side a box may ask for, so a huge widget cannot force a huge decode.</summary>
    public const int MaxBoxSide = 4096;

    /// <summary>An animation keeps at least this many frames (or all it has), whatever the cap: it never freezes.</summary>
    public const int MinAnimationFrames = 10;

    private AnimatedImage(SKImage[] frames, int[] durations, int sourceWidth, int sourceHeight, int sourceFrames)
    {
        Frames = frames; _durations = durations; _total = Math.Max(1, durations.Sum());
        SourceWidth = sourceWidth; SourceHeight = sourceHeight; SourceFrameCount = Math.Max(1, sourceFrames);
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
    /// The pixel size a <paramref name="srcW"/> x <paramref name="srcH"/> picture needs so that every pixel of a
    /// <paramref name="boxW"/> x <paramref name="boxH"/> box drawn with <paramref name="fit"/> has a picture pixel:
    /// Stretch needs exactly the box (shape changes), Contain and Cover keep the shape. Never bigger than the file;
    /// a box of 0 means "full size".
    /// </summary>
    public static SKSizeI NeededSize(int srcW, int srcH, int boxW, int boxH, ImageFit fit)
    {
        if (boxW <= 0 || boxH <= 0 || srcW <= 0 || srcH <= 0) return new SKSizeI(Math.Max(1, srcW), Math.Max(1, srcH));
        boxW = Math.Min(boxW, MaxBoxSide); boxH = Math.Min(boxH, MaxBoxSide);
        if (fit == ImageFit.Stretch) return new SKSizeI(Math.Min(boxW, srcW), Math.Min(boxH, srcH));
        double sx = (double)boxW / srcW, sy = (double)boxH / srcH;
        // Contain draws the whole picture inside the box (the smaller scale); Cover fills both sides (the larger)
        var s = Math.Min(1, fit == ImageFit.Contain ? Math.Min(sx, sy) : Math.Max(sx, sy));
        return new SKSizeI(Math.Clamp((int)Math.Ceiling(srcW * s), 1, srcW), Math.Clamp((int)Math.Ceiling(srcH * s), 1, srcH));
    }

    /// <summary>Decodes a file at the size a box needs (see <see cref="NeededSize"/>). See the other overload.</summary>
    public static AnimatedImage? Load(string path, long maxBytes = DefaultMaxBytes, int boxWidth = 0, int boxHeight = 0, ImageFit fit = ImageFit.Stretch)
        => Load(path, maxBytes, (w, h) => NeededSize(w, h, boxWidth, boxHeight, fit));

    /// <summary>
    /// Decodes a file at the size <paramref name="sizeFor"/> picks from the file's own size (clamped to it).
    /// An animation keeps all its frames when they fit in <paramref name="maxBytes"/>; otherwise every k-th frame, so
    /// the loop keeps its length, and never fewer than <see cref="MinAnimationFrames"/>.
    /// Returns null when the file is missing or cannot be decoded.
    /// </summary>
    public static AnimatedImage? Load(string path, long maxBytes, Func<int, int, SKSizeI> sizeFor)
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
            var size = sizeFor(srcW, srcH);
            int tw = Math.Clamp(size.Width, 1, srcW), th = Math.Clamp(size.Height, 1, srcH);
            var target = new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul);
            var full = new SKImageInfo(srcW, srcH, SKColorType.Bgra8888, SKAlphaType.Premul);

            int n = Math.Max(1, codec.FrameCount);
            SKImage Keep(SKImage img) { made.Add(img); FrameCreated?.Invoke(made.Count - 1, img); return img; }

            if (n == 1)
            {
                var img = DecodeStill(codec, full, target);
                if (img is null) return null;
                var still = new AnimatedImage(new[] { Keep(img) }, new[] { 100 }, srcW, srcH, 1);
                made.Clear();   // owned by the image now
                return still;
            }

            // how many frames fit: all, or every k-th frame (at least a few, so it never freezes)
            long perFrame = (long)tw * th * 4;
            int keep = (long)n * perFrame <= maxBytes ? n : (int)Math.Clamp(maxBytes / perFrame, Math.Min(n, MinAnimationFrames), n);

            var infos = codec.FrameInfo;
            // frames that a later frame builds on but that are not simply the previous one must stay at full size
            var reused = new HashSet<int>();
            for (int j = 0; j < n; j++)
                if (infos[j].RequiredFrame is var r && r >= 0 && r != j - 1) reused.Add(r);

            var kept = new Dictionary<int, SKBitmap>();
            SKBitmap? prev = null;
            var durations = new List<int>();
            int decoded = 0;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    var bmp = new SKBitmap(full);
                    try
                    {
                        WorkingBitmapCreated?.Invoke(bmp);
                        var opts = new SKCodecOptions(i);
                        var req = infos[i].RequiredFrame;
                        if (req >= 0 && req < i)
                        {
                            var basis = req == i - 1 ? prev : kept.GetValueOrDefault(req);
                            if (basis is null) break;   // cannot happen for a valid file; stop the animation here
                            opts.PriorFrame = req;
                            basis.GetPixelSpan().CopyTo(bmp.GetPixelSpan());
                        }
                        var result = codec.GetPixels(full, bmp.GetPixels(), bmp.RowBytes, opts);
                        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                        {
                            if (i == 0) return null;
                            break;   // show the frames that did decode
                        }
                        var duration = infos[i].Duration <= 0 ? 100 : infos[i].Duration;
                        // keep frame i when it starts a new slot of n/keep source frames; a skipped frame lengthens the one before
                        if (i == 0 || (long)i * keep / n != (long)(i - 1) * keep / n)
                        {
                            Keep(Shrink(bmp, target));
                            durations.Add(duration);
                        }
                        else durations[^1] += duration;
                        decoded = i + 1;

                        if (prev is not null && !kept.ContainsValue(prev)) prev.Dispose();
                        if (reused.Contains(i)) kept[i] = bmp;
                        prev = bmp;
                        bmp = null!;   // owned by prev or kept now
                    }
                    finally { bmp?.Dispose(); }   // a frame that failed or threw (review N3)
                }
            }
            finally
            {
                if (prev is not null && !kept.ContainsValue(prev)) prev.Dispose();
                foreach (var b in kept.Values) b.Dispose();
            }
            if (made.Count < n)
                Log?.Invoke($"{Path.GetFileName(path)}: kept {made.Count} of {n} frames ({tw}x{th}, {(decoded < n ? "decoding stopped early" : $"memory cap {maxBytes / 1024} KB")}); the loop keeps its length");
            var anim = new AnimatedImage(made.ToArray(), durations.ToArray(), srcW, srcH, n);
            made.Clear();   // owned by the image now
            return anim;
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
        if (target.Width < full.Width || target.Height < full.Height)
        {
            var scaled = codec.GetScaledDimensions(Math.Max((float)target.Width / full.Width, (float)target.Height / full.Height));
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

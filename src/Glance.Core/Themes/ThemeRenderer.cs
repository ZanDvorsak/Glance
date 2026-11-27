using Glance.Core.Media;
using Glance.Core.Sensors;
using SkiaSharp;

namespace Glance.Core.Themes;

/// <summary>Draws a <see cref="Theme"/> page onto a Skia canvas. Used for both the editor preview and the real display.</summary>
public sealed class ThemeRenderer : IDisposable
{
    private readonly Dictionary<string, ImageEntry> _images = new();
    private readonly Dictionary<string, VideoSource> _videos = new();
    private readonly Dictionary<string, FileCheck> _videoFiles = new();

    /// <summary>Creates video sources; tests swap it to avoid starting ffmpeg.</summary>
    internal Func<string, int, int, ImageFit, VideoSource> VideoFactory { get; set; } = (path, w, h, fit) => new VideoSource(path, w, h, fit);

    /// <summary>The clock used to pace video retries (milliseconds); tests swap it.</summary>
    internal Func<long> Now { get; set; } = () => Environment.TickCount64;

    /// <summary>How long a video that had no usable FFmpeg waits before it is tried again.</summary>
    internal const long VideoRetryMs = 5000;

    /// <summary>How long a new video size must keep being asked for before ffmpeg is started for it (a resize drag).</summary>
    internal const long VideoSettleMs = 500;

    /// <summary>A pending size not asked for within this long starts its wait again.</summary>
    internal const long VideoSizeGapMs = 1000;
    private readonly Dictionary<string, (long First, long Last)> _pendingVideos = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Dictionary<(string, bool, bool), SKTypeface> _faces = new();
    private readonly Dictionary<string, FontEntry> _fileFaces = new();
    private long _pass;         // one per Render call: pictures drawn in the current frame are never evicted
    private long _lastSweep;
    private readonly HashSet<string> _themePictures = new();
    private Theme? _theme;
    private readonly SensorHistory _history = new();
    private IReadOnlyList<SensorInfo>? _indexedFor;
    private Dictionary<string, SensorInfo> _index = new();

    /// <summary>Size of the selection handles in theme pixels.</summary>
    public const float HandleSize = 6;

    /// <param name="page">Which page to draw; the theme's active (editor) page when null.</param>
    public void Render(SKCanvas canvas, Theme theme, ISensorSource? sensors, Widget? selected = null, ThemePage? page = null)
    {
        Index(sensors);
        _theme = theme;
        _pass++;
        page ??= theme.ActivePage;
        CollectThemePictures(theme, page);
        var bg = string.IsNullOrWhiteSpace(page.BackgroundColor) ? theme.BackgroundColor : page.BackgroundColor!;
        canvas.Clear(Parse(bg, SKColors.Black));

        foreach (var w in page.Widgets)
        {
            if (!w.Visible) { w.LastBounds = default; continue; }
            switch (w.Kind)
            {
                case WidgetKind.Panel: DrawPanel(canvas, w); break;
                case WidgetKind.Bar: DrawBar(canvas, w); break;
                case WidgetKind.Gauge: DrawGauge(canvas, w); break;
                case WidgetKind.Graph: DrawGraph(canvas, w); break;
                case WidgetKind.Image: DrawImage(canvas, theme, w); break;
                case WidgetKind.Text: DrawText(canvas, w, w.Text); break;
                case WidgetKind.Clock: DrawText(canvas, w, SafeDate(w.Text)); break;
                case WidgetKind.Sensor: DrawText(canvas, w, w.Text + FormatValue(w), ColorFor(w, Value(w))); break;
            }
        }

        SettleImages();
        SweepVideos();
        SweepIdle();

        if (selected is not null && selected.Visible)
        {
            using var p = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(0x40, 0xC4, 0xFF), IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0) };
            canvas.DrawRect(selected.LastBounds, p);

            // a widget in a group is resized as a whole: draw the group's outline and put the handles on it
            var members = GroupOps.Members(page.Widgets, selected);
            var box = members.Count > 1 ? GroupOps.Union(members) : selected.LastBounds;
            if (members.Count > 1)
            {
                using var gp = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(0xFF, 0xC8, 0x40), IsAntialias = true };
                canvas.DrawRect(box, gp);
            }

            using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
            using var edge = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(0x40, 0xC4, 0xFF), IsAntialias = true };
            foreach (var (_, rect) in Handles.Layout(box, members.Count > 1 || Handles.IsFree(selected), HandleSize))
            {
                canvas.DrawRect(rect, fill);
                canvas.DrawRect(rect, edge);
            }
        }
    }

    /// <summary>
    /// Pre-fills graph history with a gentle synthetic curve around each graph's current value, so a freshly placed
    /// graph (or a gallery thumbnail) does not look empty. Real samples replace it as time passes.
    /// Each sensor is seeded once for the longest graph showing it, and only for the past its history does not cover
    /// yet: seeding a page again (for example after adding a preset) never touches real or earlier samples, and a new,
    /// longer graph next to a shorter one still gets a full line.
    /// </summary>
    public void SeedGraphHistory(ThemePage page, ISensorSource sensors)
    {
        Index(sensors);
        long now = _clock.ElapsedMilliseconds;
        var graphs = page.Widgets.Where(w => w.Kind == WidgetKind.Graph && w.SensorId.Length > 0)
            .GroupBy(w => w.SensorId)
            .Select(g => (id: g.Key, seconds: g.Max(w => Math.Clamp(w.HistorySeconds, 5, 900))));
        foreach (var (id, seconds) in graphs.ToList())
        {
            if (!_index.TryGetValue(id, out var s) || s.Value is not { } raw) continue;
            long? oldest = _history.Oldest(id);
            int n = (int)(seconds * 1000 / SensorHistory.SampleMs);
            for (int i = 0; i < n; i++)
            {
                long ms = now - (n - i) * SensorHistory.SampleMs;
                if (oldest is { } o && ms > o - SensorHistory.SampleMs) break;   // from here on the history already has samples
                double phase = i * 0.11 + id.Length;
                double wobble = 0.04 * Math.Sin(phase) + 0.015 * Math.Sin(phase * 2.3);
                _history.Seed(id, ms, (float)(raw * (1 + wobble)));
            }
        }
    }

    private void Index(ISensorSource? s)
    {
        if (s is null) { _index = new(); _indexedFor = null; return; }
        var snap = s.Snapshot;
        if (ReferenceEquals(snap, _indexedFor)) return;
        _indexedFor = snap;
        _index = new Dictionary<string, SensorInfo>(snap.Count);
        foreach (var i in snap) _index[i.Id] = i;
    }

    private float? Value(Widget w) => _index.TryGetValue(w.SensorId, out var s) && s.Value is { } v ? (float)(v * w.Multiplier) : null;

    private string FormatNumber(Widget w)
    {
        if (Value(w) is not { } v) return "--";
        try { return v.ToString(w.Format); } catch (FormatException) { return v.ToString("0"); }
    }

    private string FormatValue(Widget w) => FormatNumber(w) + w.Unit;

    /// <summary>The widget colour, switched to the warning or critical colour when the value crosses a threshold.</summary>
    private static SKColor ColorFor(Widget w, float? v)
    {
        var normal = Parse(w.Color, SKColors.White);
        if (v is not { } x) return normal;
        if (w.CritAt > 0 && x >= w.CritAt) return Parse(w.CritColor, new SKColor(0xFF, 0x52, 0x52));
        if (w.WarnAt > 0 && x >= w.WarnAt) return Parse(w.WarnColor, new SKColor(0xFF, 0xC1, 0x07));
        return normal;
    }

    private static string SafeDate(string fmt)
    {
        try { return DateTime.Now.ToString(string.IsNullOrWhiteSpace(fmt) ? "HH:mm:ss" : fmt); } catch (FormatException) { return "??"; }
    }

    private static SKRect Box(Widget w) => new((float)w.X, (float)w.Y, (float)(w.X + w.Width), (float)(w.Y + w.Height));

    private void DrawPanel(SKCanvas c, Widget w)
    {
        var r = Box(w);
        w.LastBounds = r;
        using var p = new SKPaint { Color = Parse(w.Color, SKColors.Black), IsAntialias = true };
        c.DrawRoundRect(r, (float)w.CornerRadius, (float)w.CornerRadius, p);
    }

    private void DrawBar(SKCanvas c, Widget w)
    {
        var r = Box(w);
        w.LastBounds = r;
        float rad = (float)Math.Min(w.CornerRadius, (w.Vertical ? w.Width : w.Height) / 2);
        using var back = new SKPaint { Color = Parse(w.BackColor, SKColors.Gray), IsAntialias = true };
        c.DrawRoundRect(r, rad, rad, back);
        var v = Value(w);
        if (v is null || w.Max <= w.Min) return;
        var frac = (float)Math.Clamp((v.Value - w.Min) / (w.Max - w.Min), 0, 1);
        if (frac <= 0) return;
        c.Save();
        c.ClipRoundRect(new SKRoundRect(r, rad, rad), antialias: true);
        using var fill = new SKPaint { Color = ColorFor(w, v), IsAntialias = true };
        c.DrawRect(w.Vertical ? new SKRect(r.Left, r.Bottom - r.Height * frac, r.Right, r.Bottom) : new SKRect(r.Left, r.Top, r.Left + r.Width * frac, r.Bottom), fill);
        c.Restore();
    }

    // ---- gauge --------------------------------------------------------------------------------------------------

    private void DrawGauge(SKCanvas c, Widget w)
    {
        var box = Box(w);
        w.LastBounds = box;
        float size = Math.Min(box.Width, box.Height);
        if (size < 4) return;
        float thick = Math.Clamp((float)w.Thickness, 1, size / 2);
        float cx = box.MidX, cy = box.MidY;
        var arc = SKRect.Create(cx - size / 2 + thick / 2, cy - size / 2 + thick / 2, size - thick, size - thick);
        float start = (float)w.StartAngle, sweep = (float)Math.Clamp(w.Sweep, 1, 360);

        using var track = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = thick, StrokeCap = SKStrokeCap.Round, Color = Parse(w.BackColor, SKColors.Gray), IsAntialias = true };
        c.DrawArc(arc, start, sweep, false, track);

        var v = Value(w);
        if (v is { } val && w.Max > w.Min)
        {
            var frac = (float)Math.Clamp((val - w.Min) / (w.Max - w.Min), 0, 1);
            if (frac > 0.001f)
            {
                using var fill = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = thick, StrokeCap = SKStrokeCap.Round, Color = ColorFor(w, v), IsAntialias = true };
                c.DrawArc(arc, start, sweep * frac, false, fill);
            }
        }

        // value in the middle (number large, unit small), optional caption below
        using var vf = MakeFont(w, w.FontSize, w.Bold);
        using var uf = MakeFont(w, w.FontSize * 0.5, false);
        using var tp = new SKPaint { Color = Parse(w.TextColor, SKColors.White), IsAntialias = true };
        var number = FormatNumber(w);
        var unit = w.Unit ?? "";
        float vw = vf.MeasureText(number), uw = unit.Length > 0 ? uf.MeasureText(unit) + 2 : 0;
        var m = vf.Metrics;
        bool caption = !string.IsNullOrWhiteSpace(w.Text);
        float baseline = cy - (m.Ascent + m.Descent) / 2 - (caption ? (float)w.FontSize * 0.18f : 0);
        float left = cx - (vw + uw) / 2;
        c.DrawText(number, left, baseline, SKTextAlign.Left, vf, tp);
        if (unit.Length > 0) c.DrawText(unit, left + vw + 2, baseline, SKTextAlign.Left, uf, tp);
        if (caption)
        {
            using var cf = MakeFont(w, Math.Max(8, w.FontSize * 0.42), false);
            using var cp = new SKPaint { Color = tp.Color.WithAlpha(170), IsAntialias = true };
            c.DrawText(w.Text, cx, baseline + (float)w.FontSize * 0.62f, SKTextAlign.Center, cf, cp);
        }
    }

    // ---- graph --------------------------------------------------------------------------------------------------

    private void DrawGraph(SKCanvas c, Widget w)
    {
        var box = Box(w);
        w.LastBounds = box;
        if (box.Width < 4 || box.Height < 4) return;
        long now = _clock.ElapsedMilliseconds;

        if (_index.TryGetValue(w.SensorId, out var s) && s.Value is { } raw) _history.Record(w.SensorId, raw, now);
        double window = Math.Clamp(w.HistorySeconds, 5, 900);
        var samples = _history.View(w.SensorId, now, window);   // no copy per frame; not changed until the next Record

        // value range: fixed when Max > Min, otherwise fitted to what is on screen
        double min = w.Min, max = w.Max;
        if (max <= min)
        {
            if (samples.Length == 0) { min = 0; max = 1; }
            else
            {
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var (_, v) in samples) { if (v < lo) lo = v; if (v > hi) hi = v; }
                min = lo * w.Multiplier; max = hi * w.Multiplier;
                if (max - min < 1e-6) { min -= 1; max += 1; }
                var pad = (max - min) * 0.1; min -= pad; max += pad;
            }
        }
        if (w.Multiplier < 0) (min, max) = (max, min);

        c.Save();
        c.ClipRect(box);

        if (w.ShowGrid)
        {
            using var grid = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = Parse(w.BackColor, SKColors.Gray), IsAntialias = false };
            for (int i = 0; i <= 4; i++)
            {
                float gy = box.Top + box.Height * i / 4f;
                c.DrawLine(box.Left, Math.Min(gy, box.Bottom - 0.5f), box.Right, Math.Min(gy, box.Bottom - 0.5f), grid);
            }
        }

        if (samples.Length > 0)
        {
            float X(long t) => box.Left + (float)((t - (now - window * 1000)) / (window * 1000)) * box.Width;
            float Y(float v)
            {
                var f = (float)Math.Clamp((v * w.Multiplier - min) / (max - min), 0, 1);
                return box.Bottom - f * box.Height;
            }

            var lastValue = samples[^1].value * (float)w.Multiplier;
            var color = ColorFor(w, lastValue);

            using var line = new SKPath();
            line.MoveTo(X(samples[0].ms), Y(samples[0].value));
            for (int i = 1; i < samples.Length; i++) line.LineTo(X(samples[i].ms), Y(samples[i].value));

            using var area = new SKPath(line);
            area.LineTo(X(samples[^1].ms), box.Bottom);
            area.LineTo(X(samples[0].ms), box.Bottom);
            area.Close();
            using var shader = SKShader.CreateLinearGradient(new SKPoint(0, box.Top), new SKPoint(0, box.Bottom),
                new[] { color.WithAlpha(110), color.WithAlpha(0) }, null, SKShaderTileMode.Clamp);
            using var areaPaint = new SKPaint { Style = SKPaintStyle.Fill, Shader = shader, IsAntialias = true };
            c.DrawPath(area, areaPaint);

            using var stroke = new SKPaint
            {
                Style = SKPaintStyle.Stroke, StrokeWidth = (float)Math.Max(1, w.Thickness), Color = color, IsAntialias = true,
                StrokeJoin = SKStrokeJoin.Round, StrokeCap = SKStrokeCap.Round,
            };
            c.DrawPath(line, stroke);

            using var dot = new SKPaint { Style = SKPaintStyle.Fill, Color = color, IsAntialias = true };
            c.DrawCircle(X(samples[^1].ms), Y(samples[^1].value), (float)Math.Max(2, w.Thickness * 1.3), dot);
        }
        c.Restore();
    }

    // ---- images and text ----------------------------------------------------------------------------------------

    private void DrawImage(SKCanvas c, Theme t, Widget w)
    {
        VideoFrameLease? lease = null;   // a video frame stays valid until the draw is done
        try { DrawImage(c, t, w, ref lease); }
        finally { lease?.Dispose(); }
    }

    private void DrawImage(SKCanvas c, Theme t, Widget w, ref VideoFrameLease? lease)
    {
        var r = Box(w);
        w.LastBounds = r;
        var path = t.ResolveAsset(w.ImagePath);
        SKImage? img = null;
        var fit = w.Fit;
        string? note = null;
        float srcW = 0, srcH = 0;   // the picture's shape (a decode may be stretched to another one)

        if (Ffmpeg.IsVideo(path))
        {
            lease = GetVideoFrame(path!, (int)Math.Round(w.Width), (int)Math.Round(w.Height), w.Fit, out note);
            img = lease.Image;
            fit = ImageFit.Stretch; // ffmpeg already fitted the frame to the widget box (or to its old size, while it is resized)
            if (img is not null) (srcW, srcH) = (img.Width, img.Height);
        }
        else
        {
            // the size the box really covers on the target (the display pusher scales the theme to fit the panel)
            var device = c.TotalMatrix.MapRect(r);
            int bw = Math.Max(1, (int)Math.Ceiling(device.Width)), bh = Math.Max(1, (int)Math.Ceiling(device.Height));
            if (LoadImage(path, bw, bh, w.Fit) is { } anim)
            {
                img = anim.At(_clock.ElapsedMilliseconds);
                (srcW, srcH) = (anim.SourceWidth, anim.SourceHeight);
            }
        }

        if (img is null)
        {
            using var p = new SKPaint { Color = new SKColor(255, 255, 255, 40), IsAntialias = true };
            c.DrawRect(r, p);
            if (note is not null)
            {
                using var f = new SKFont(Face("Segoe UI", false), 12);
                using var tp = new SKPaint { Color = new SKColor(255, 255, 255, 180), IsAntialias = true };
                c.DrawText(note, r.Left + 6, r.Top + 18, SKTextAlign.Left, f, tp);
            }
            return;
        }

        using var paint = new SKPaint { IsAntialias = true, Color = SKColors.White.WithAlpha((byte)Math.Round(Math.Clamp(w.Opacity, 0, 100) * 2.55)) };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        float sx = r.Width / srcW, sy = r.Height / srcH;
        switch (fit)
        {
            case ImageFit.Contain:
            {
                var s = Math.Min(sx, sy);
                float dw = srcW * s, dh = srcH * s;
                c.DrawImage(img, SKRect.Create(r.Left + (r.Width - dw) / 2, r.Top + (r.Height - dh) / 2, dw, dh), sampling, paint);
                break;
            }
            case ImageFit.Cover:
            {
                var s = Math.Max(sx, sy);
                float dw = srcW * s, dh = srcH * s;
                c.Save();
                c.ClipRect(r);
                c.DrawImage(img, SKRect.Create(r.Left + (r.Width - dw) / 2, r.Top + (r.Height - dh) / 2, dw, dh), sampling, paint);
                c.Restore();
                break;
            }
            default:
                c.DrawImage(img, r, sampling, paint);
                break;
        }
    }

    /// <summary>
    /// The frame to draw for a video widget, and the note to show when there is none. While the widget's size (or fit)
    /// keeps changing, the decoder already running for this file goes on and its frames are stretched to the new box; a
    /// decoder for the new size starts only once that size has been asked for during <see cref="VideoSettleMs"/>, and
    /// the old frames are shown until it has its first one (review M9: a resize drag started an ffmpeg per pixel).
    /// </summary>
    private VideoFrameLease GetVideoFrame(string path, int w, int h, ImageFit fit, out string? note)
    {
        var key = $"{path}|{w}x{h}|{fit}";
        var now = Now();
        var v = GetVideo(path, key, w, h, fit, now, out var other);
        if (v is not null)
        {
            v.LastUsedTicks = Environment.TickCount64;
            var lease = v.AcquireFrame();
            if (lease.Image is not null) { note = null; return lease; }
            other ??= OtherVideo(path, key);
        }
        if (other is not null)
        {
            var lease = other.AcquireFrame();
            if (lease.Image is not null)
            {
                other.LastUsedTicks = Environment.TickCount64;   // keep it until the new decoder has a frame
                note = null;
                return lease;
            }
        }
        note = v?.Error ?? other?.Error ?? "Loading video...";
        return VideoFrameLease.Empty;
    }

    /// <summary>The most recently used decoder of this file for another size or fit, or null.</summary>
    private VideoSource? OtherVideo(string path, string key)
    {
        VideoSource? best = null;
        foreach (var (k, v) in _videos)
            if (k != key && k.StartsWith(path + "|", StringComparison.Ordinal) && (best is null || v.LastUsedTicks > best.LastUsedTicks))
                best = v;
        return best;
    }

    /// <summary>True once <paramref name="key"/> has been asked for without a gap for <see cref="VideoSettleMs"/>.</summary>
    private bool VideoSizeSettled(string key, long now)
    {
        if (!_pendingVideos.TryGetValue(key, out var p) || now - p.Last > VideoSizeGapMs) p = (now, now);
        else p.Last = now;
        _pendingVideos[key] = p;
        return now - p.First >= VideoSettleMs;
    }

    /// <summary>The decoder for exactly this size and fit, or null while a resize has not settled (then <paramref name="other"/> is the one to show).</summary>
    private VideoSource? GetVideo(string path, string key, int w, int h, ImageFit fit, long now, out VideoSource? other)
    {
        other = null;
        // a video file replaced on disk starts again from the new file (review L4; picking it again used to do that)
        if (!_videoFiles.TryGetValue(path, out var check)) _videoFiles[path] = check = new FileCheck(null, now);   // first stamp comes from the background check
        if (check.Poll(path, now, StatFile, StatWaitMs) is { } stamp)
        {
            if (check.Stamp is { } known && known != stamp)
                foreach (var k in _videos.Keys.Where(k => k.StartsWith(path + "|", StringComparison.Ordinal)).ToList())
                {
                    _videos[k].Dispose();
                    _videos.Remove(k);
                }
            check.Stamp = stamp;
        }
        if (_videos.TryGetValue(key, out var v) && (v.FfmpegMissing || v.Stopped) && now - v.CreatedTicks >= VideoRetryMs)
        {
            // FFmpeg was missing (or not trusted by the elevated runner) when this source was made, or it stopped: try
            // again, so a fresh install shows up without reloading the theme. Ffmpeg.Find paces its own disk search too.
            v.Dispose();
            _videos.Remove(key);
        }
        if (!_videos.TryGetValue(key, out v))
        {
            other = OtherVideo(path, key);
            if (other is not null && !VideoSizeSettled(key, now)) return null;
            _pendingVideos.Remove(key);
            // cheap: the source finds and starts ffmpeg on its own thread, never on the rendering (UI) thread
            _videos[key] = v = VideoFactory(path, w, h, fit);
            v.CreatedTicks = now;
        }
        return v;
    }

    /// <summary>Stops videos that are no longer shown (widget deleted, resized, hidden, or on another page).</summary>
    private void SweepVideos()
    {
        var now = Environment.TickCount64;
        foreach (var k in _videos.Where(kv => now - kv.Value.LastUsedTicks > 3000).Select(kv => kv.Key).ToList())
        {
            _videos[k].Dispose();
            _videos.Remove(k);
        }
        if (_pendingVideos.Count > 0)   // sizes passed during a resize drag
        {
            var clock = Now();
            foreach (var k in _pendingVideos.Where(kv => clock - kv.Value.Last > VideoSizeGapMs).Select(kv => kv.Key).ToList())
                _pendingVideos.Remove(k);
        }
        if (_videoFiles.Count > _videos.Count)   // forget file checks of videos that no longer play
            foreach (var p in _videoFiles.Keys.Where(p => !_videos.Keys.Any(k => k.StartsWith(p + "|", StringComparison.Ordinal))).ToList())
                _videoFiles.Remove(p);
    }

    private void DrawText(SKCanvas c, Widget w, string text, SKColor? color = null)
    {
        using var font = MakeFont(w, w.FontSize, w.Bold);
        using var paint = new SKPaint { Color = color ?? Parse(w.Color, SKColors.White), IsAntialias = true };
        var m = font.Metrics;
        float baseline = (float)w.Y - m.Ascent;
        var align = w.Align switch { TextAlign.Right => SKTextAlign.Right, TextAlign.Center => SKTextAlign.Center, _ => SKTextAlign.Left };
        c.DrawText(text, (float)w.X, baseline, align, font, paint);
        float width = Math.Max(font.MeasureText(text), 8);
        float left = w.Align switch { TextAlign.Right => (float)w.X - width, TextAlign.Center => (float)w.X - width / 2, _ => (float)w.X };
        w.LastBounds = new SKRect(left, (float)w.Y, left + width, (float)w.Y + (m.Descent - m.Ascent));
    }

    /// <summary>
    /// An installed font. SkiaSharp returns one shared object per family and style to every caller in the process
    /// (and <see cref="SKTypeface.Default"/> for unknown families) and ignores Dispose on them, so this cache only
    /// remembers them; disposing would be a no-op today and would break the other renderers if that ever changed.
    /// </summary>
    private SKTypeface Face(string family, bool bold, bool italic = false)
    {
        var key = (family, bold, italic);
        if (_faces.TryGetValue(key, out var f)) return f;
        f = SKTypeface.FromFamilyName(string.IsNullOrWhiteSpace(family) ? "Segoe UI" : family,
            new SKFontStyle(bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal,
                italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright)) ?? SKTypeface.Default;
        return _faces[key] = f;
    }

    /// <summary>The widget's font: its font file when it has a usable one, otherwise the installed family.
    /// A font file is one style, so bold and italic are synthesized for it.</summary>
    private SKFont MakeFont(Widget w, double size, bool bold)
    {
        var path = _theme?.ResolveAsset(w.FontFile);
        if (!string.IsNullOrEmpty(path) && FileFace(path) is { } custom)
        {
            return new SKFont(custom, (float)size) { Edging = SKFontEdging.SubpixelAntialias, Embolden = bold, SkewX = w.Italic ? -0.25f : 0 };
        }
        return new SKFont(Face(w.FontFamily, bold, w.Italic), (float)size) { Edging = SKFontEdging.SubpixelAntialias };
    }

    /// <summary>
    /// A font file's typeface (null when it is missing or not a font). Like pictures, it is read again when the file
    /// changes and looked for again while it is missing; a replaced typeface is freed.
    /// </summary>
    private SKTypeface? FileFace(string path)
    {
        var now = Now();
        if (!_fileFaces.TryGetValue(path, out var e))
        {
            var stamp = StatFile(path);
            _fileFaces[path] = e = new FontEntry { Check = new FileCheck(stamp, now), LastUsedAt = now, Face = ReadFontFile(path, stamp) };
            return e.Face;
        }
        e.LastUsedAt = now;
        if (e.Check.Poll(path, now, StatFile, StatWaitMs) is { } s && s != e.Check.Stamp)
        {
            e.Check.Stamp = s;   // the stamp that was checked is the one read (review N2: one stat per change)
            e.Face?.Dispose();
            e.Face = ReadFontFile(path, s);
        }
        return e.Face;
    }

    /// <summary>
    /// Reads the font into memory. <c>SKTypeface.FromFile</c> maps the file and keeps it locked for as long as the
    /// typeface lives, so the user could not replace or delete it while the editor or the runner was open.
    /// </summary>
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

    /// <summary>
    /// The picture for <paramref name="path"/>, decoded at the size its box covers on the target
    /// (<paramref name="boxW"/> x <paramref name="boxH"/> device pixels). Cached entries notice when the file changes
    /// (a background check at most every <see cref="StatIntervalMs"/>), a missing file is looked for again on the same
    /// schedule, and a changed file that does not decode yet keeps the old picture. Size changes are handled after the
    /// frame by <see cref="SettleImages"/>, once the size has stopped changing.
    /// </summary>
    private AnimatedImage? LoadImage(string? path, int boxW, int boxH, ImageFit fit)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var now = Now();
        if (!_images.TryGetValue(path, out var e))
        {
            var stamp = StatFile(path);   // before decoding, so a change made during the decode is seen next time
            _images[path] = e = new ImageEntry { Check = new FileCheck(stamp, now), LastUsedAt = now };
            if (stamp.Exists)
            {
                e.Image = Decode(e, path, (w, h) => AnimatedImage.NeededSize(w, h, boxW, boxH, fit));
                TrimImages(path);
            }
        }
        else
        {
            e.LastUsedAt = now;
            if (e.Check.Poll(path, now, StatFile, StatWaitMs) is { } s && s != e.Check.Stamp)
                Reload(e, path, s, now, (w, h) => AnimatedImage.NeededSize(w, h, boxW, boxH, fit));
        }

        if (e.LastPass != _pass) { e.LastPass = _pass; e.Need = default; }
        if (e.Image is { } img)
        {
            // the biggest size any widget showing this file needs this frame
            var need = AnimatedImage.NeededSize(img.SourceWidth, img.SourceHeight, boxW, boxH, fit);
            e.Need = new SKSizeI(Math.Max(e.Need.Width, need.Width), Math.Max(e.Need.Height, need.Height));
        }
        return e.Image;
    }

    private AnimatedImage? Decode(ImageEntry e, string path, Func<int, int, SKSizeI> sizeFor)
    {
        ImageDecodes++;
        e.Decoding = true;   // counts as an animation while its share is worked out (it may be one)
        long cap = AnimationShare();
        e.Decoding = false;
        var img = AnimatedImage.Load(path, cap, sizeFor);
        if (img is not null) e.Cap = cap;
        return img;
    }

    /// <summary>
    /// The file changed on disk. The new version replaces the picture only when it decodes (review L1): a file caught
    /// half-written, or briefly missing during a save, keeps showing the old picture. If the new version still fails
    /// after <see cref="BrokenFileMs"/>, the file really is broken or gone and the placeholder is shown.
    /// </summary>
    private void Reload(ImageEntry e, string path, FileStamp stamp, long now, Func<int, int, SKSizeI> sizeFor)
    {
        var fresh = stamp.Exists ? Decode(e, path, sizeFor) : null;
        if (fresh is not null || e.Image is null)
        {
            e.Image?.Dispose();
            e.Image = fresh;
            e.Check.Stamp = stamp;
            e.Pending = null;
            if (fresh is not null) TrimImages(path);
        }
        else if (e.Pending != stamp) { e.Pending = stamp; e.PendingSince = now; }   // retried at the next check
        else if (now - e.PendingSince >= BrokenFileMs)
        {
            e.Image.Dispose();
            e.Image = null;
            e.Check.Stamp = stamp;
            e.Pending = null;
        }
    }

    /// <summary>
    /// After a frame: a picture drawn bigger than its decode (L2), much smaller (N5), or an animation that was cut and
    /// now has a clearly bigger share of the budget, or one using far more than its share while the cache is over
    /// budget (M1), is decoded again, but only once that need has not changed for <see cref="SettleMs"/>. Until then the
    /// current decode is drawn scaled, so dragging a resize handle never decodes on the UI thread at every step.
    /// </summary>
    private void SettleImages()
    {
        var now = Now();
        long share = -1;
        foreach (var (path, e) in _images.Where(kv => kv.Value.LastPass == _pass && kv.Value.Image is not null && kv.Value.Need.Width > 0).ToList())
        {
            var img = e.Image!;
            var need = e.Need;
            bool tooSmall = img.Width + 1 < need.Width || img.Height + 1 < need.Height;
            bool tooBig = img.Width > 2 * need.Width + 1 || img.Height > 2 * need.Height + 1;
            bool budget = false;
            if (img.SourceFrameCount > 1)
            {
                if (share < 0) share = AnimationShare();
                budget = (img.IsCut && share >= e.Cap + e.Cap / 2)
                      || (img.Bytes > share + share / 4 && CachedImageBytes > ImageBudgetBytes);
            }
            if (!tooSmall && !tooBig && !budget) { e.Want = default; continue; }
            if (e.Want != need) { e.Want = need; e.WantSince = now; continue; }
            if (now - e.WantSince < SettleMs) continue;
            e.Want = default;
            if (Decode(e, path, (_, _) => need) is not { } fresh) continue;   // keep what is shown
            img.Dispose();
            e.Image = fresh;
            TrimImages(path);
        }
    }

    /// <summary>
    /// The byte cap for one animation: the budget left after the theme's still pictures, shared evenly between its
    /// animations (pictures not decoded yet count as animations). Every animation of the theme gets the same share,
    /// whichever page is drawn first, so none is left with nothing (review M1).
    /// </summary>
    private long AnimationShare()
    {
        long stills = 0; int animations = 0;
        foreach (var p in _themePictures)
        {
            if (_images.TryGetValue(p, out var e) && !e.Decoding && (e.Image is null || e.Image.SourceFrameCount == 1)) stills += e.Image?.Bytes ?? 0;
            else animations++;
        }
        return Math.Max(0, ImageBudgetBytes - stills) / Math.Max(1, animations);
    }

    /// <summary>
    /// Every picture file of the theme being drawn (all pages), recomputed each frame. It only sizes the budget shares,
    /// so it is best effort: the display pusher renders while the editor changes the theme (undo replaces every page),
    /// and a page caught mid-change is skipped instead of failing the frame.
    /// </summary>
    private void CollectThemePictures(Theme theme, ThemePage page)
    {
        _themePictures.Clear();
        ThemePage[] pages;
        try { pages = Display.DisplayPusher.StablePages(theme); } catch { pages = Array.Empty<ThemePage>(); }
        foreach (var pg in pages.Append(page))
        {
            try
            {
                var widgets = pg.Widgets;
                for (int i = 0; i < widgets.Count; i++)
                    if (widgets[i] is { Kind: WidgetKind.Image } w && theme.ResolveAsset(w.ImagePath) is { Length: > 0 } p && !Ffmpeg.IsVideo(p))
                        _themePictures.Add(p);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NullReferenceException) { /* changing right now */ }
        }
    }

    /// <summary>
    /// Frees pictures not drawn this frame until the cache fits the budget: first those that are not in the theme any
    /// more, then the least recently drawn.
    /// </summary>
    private void TrimImages(string keep)
    {
        long total = CachedImageBytes;
        if (total <= ImageBudgetBytes) return;
        foreach (var (k, e) in _images.Where(kv => kv.Key != keep && kv.Value.LastPass != _pass && kv.Value.Image is not null)
                     .OrderBy(kv => _themePictures.Contains(kv.Key)).ThenBy(kv => kv.Value.LastUsedAt).ToList())
        {
            total -= e.Image!.Bytes;
            e.Image.Dispose();
            _images.Remove(k);
            if (total <= ImageBudgetBytes) break;
        }
    }

    /// <summary>Drops pictures and font files that have not been drawn for a long time (a deleted widget, an old theme).</summary>
    private void SweepIdle()
    {
        var now = Now();
        if (now - _lastSweep < SweepEveryMs) return;
        _lastSweep = now;
        foreach (var k in _images.Where(kv => now - kv.Value.LastUsedAt > IdleMs).Select(kv => kv.Key).ToList())
        {
            _images[k].Image?.Dispose();
            _images.Remove(k);
        }
        foreach (var k in _fileFaces.Where(kv => now - kv.Value.LastUsedAt > IdleMs).Select(kv => kv.Key).ToList())
        {
            _fileFaces[k].Face?.Dispose();
            _fileFaces.Remove(k);
        }
    }

    /// <summary>How often a cached file is checked for changes on disk (and a missing one looked for again).</summary>
    internal const long StatIntervalMs = 1000;
    /// <summary>A cached picture or font file not drawn for this long is freed.</summary>
    internal const long IdleMs = 5 * 60 * 1000;
    private const long SweepEveryMs = 10_000;
    /// <summary>Total decoded picture memory one renderer keeps (each consumer has its own renderer).</summary>
    public const long DefaultImageBudgetBytes = 96L * 1024 * 1024;
    /// <summary>Font files bigger than this are not loaded (the installed font is used instead).</summary>
    private const long MaxFontFileBytes = 64L * 1024 * 1024;

    /// <summary>How long a picture's needed size must stay the same before it is decoded again at that size.</summary>
    internal const long SettleMs = 300;
    /// <summary>How long a changed file may fail to decode before the old picture gives way to the placeholder.</summary>
    internal const long BrokenFileMs = 2000;
    /// <summary>A file check slower than this (a network share that went away) backs off, doubling up to a minute.</summary>
    private const long SlowStatMs = 200;
    private const long MaxStatIntervalMs = 60_000;

    /// <summary>
    /// What identifies one version of a file: replacing or editing it changes the time or the size. On FAT32 and exFAT
    /// (USB sticks) times have 2 s or 10 ms steps, so a rewrite of the same size within one step can be missed until
    /// the next change; NTFS has 100 ns steps.
    /// </summary>
    internal readonly record struct FileStamp(bool Exists, DateTime Modified, long Length)
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

    /// <summary>
    /// Watches one file for changes without blocking the rendering thread (review L5): the stat runs on the thread
    /// pool, the renderer waits for it only briefly when it starts it, and otherwise picks the result up on a later
    /// frame. A slow stat (an unreachable network path) makes the checks rarer.
    /// </summary>
    private sealed class FileCheck
    {
        /// <summary>The version the cache holds (null: not known yet).</summary>
        public FileStamp? Stamp;
        private long _checkedAt, _interval = StatIntervalMs;
        private Task<(FileStamp stamp, long ms)>? _pending;

        public FileCheck(FileStamp? stamp, long now)
        {
            Stamp = stamp;
            _checkedAt = stamp is null ? long.MinValue / 2 : now;   // no stamp yet: check at once
        }

        /// <summary>A fresh stamp when a check finished, otherwise null (not due yet, or still running).</summary>
        public FileStamp? Poll(string path, long now, Func<string, FileStamp> stat, int waitMs)
        {
            bool started = false;
            if (_pending is null)
            {
                if (now - _checkedAt < _interval) return null;
                _checkedAt = now;
                _pending = Task.Run(() =>
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    FileStamp s;
                    try { s = stat(path); } catch { s = default; }
                    return (s, sw.ElapsedMilliseconds);
                });
                started = true;
            }
            if (started ? !_pending.Wait(waitMs) : !_pending.IsCompleted) return null;
            var (stamp, ms) = _pending.Result;
            _pending = null;
            _interval = ms > SlowStatMs ? Math.Min(_interval * 2, MaxStatIntervalMs) : StatIntervalMs;
            return stamp;
        }
    }

    private sealed class ImageEntry
    {
        public AnimatedImage? Image;
        public FileCheck Check = null!;
        public FileStamp? Pending;          // a changed version that has not decoded yet
        public long PendingSince, LastUsedAt, LastPass;
        public long Cap;                    // the byte cap the picture was decoded with
        public bool Decoding;               // being decoded right now (AnimationShare counts it as an animation)
        public SKSizeI Need;                // the biggest size asked for in LastPass (device pixels)
        public SKSizeI Want;                // a decode size waiting to settle
        public long WantSince;
    }

    private sealed class FontEntry
    {
        public SKTypeface? Face;
        public FileCheck Check = null!;
        public long LastUsedAt;
    }

    // ---- test hooks ---------------------------------------------------------------------------------------------
    internal AnimatedImage? CachedImage(string path) => _images.TryGetValue(path, out var e) ? e.Image : null;
    internal SKTypeface? CachedFileFace(string path) => _fileFaces.TryGetValue(path, out var e) ? e.Face : null;
    internal long CachedImageBytes => _images.Values.Sum(e => e.Image?.Bytes ?? 0);
    internal long ImageBudgetBytes { get; set; } = DefaultImageBudgetBytes;
    internal int ImageDecodes { get; private set; }
    internal Func<string, FileStamp> StatFile { get; set; } = FileStamp.Of;
    internal int StatWaitMs { get; set; } = 25;
    internal SensorHistory History => _history;

    /// <summary>
    /// Forget cached images and font files and stop videos. Not needed when a file changes on disk (entries notice
    /// that by themselves); use it to let go of everything, for example when another theme is opened.
    /// </summary>
    public void ClearImageCache()
    {
        foreach (var e in _images.Values) e.Image?.Dispose();
        _images.Clear();
        foreach (var v in _videos.Values) v.Dispose();
        _videos.Clear();
        _videoFiles.Clear();
        _pendingVideos.Clear();
        foreach (var e in _fileFaces.Values) e.Face?.Dispose();
        _fileFaces.Clear();
    }

    private static SKColor Parse(string s, SKColor fallback) => SKColor.TryParse(s, out var c) ? c : fallback;

    public void Dispose()
    {
        ClearImageCache();
        _faces.Clear();   // installed-font typefaces are shared process-wide by SkiaSharp: never disposed here (see Face)
        _history.Clear();
    }
}

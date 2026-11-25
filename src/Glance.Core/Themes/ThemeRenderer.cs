using Glance.Core.Media;
using Glance.Core.Sensors;
using SkiaSharp;

namespace Glance.Core.Themes;

/// <summary>Draws a <see cref="Theme"/> page onto a Skia canvas. Used for both the editor preview and the real display.</summary>
public sealed class ThemeRenderer : IDisposable
{
    private readonly Dictionary<string, AnimatedImage?> _images = new();
    private readonly Dictionary<string, VideoSource> _videos = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly Dictionary<(string, bool, bool), SKTypeface> _faces = new();
    private readonly Dictionary<string, SKTypeface?> _fileFaces = new();
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
        page ??= theme.ActivePage;
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

        SweepVideos();

        if (selected is not null && selected.Visible)
        {
            using var p = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(0x40, 0xC4, 0xFF), IsAntialias = true,
                PathEffect = SKPathEffect.CreateDash(new[] { 4f, 3f }, 0) };
            canvas.DrawRect(selected.LastBounds, p);

            using var fill = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
            using var edge = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = new SKColor(0x40, 0xC4, 0xFF), IsAntialias = true };
            foreach (var (_, rect) in Handles.Layout(selected, HandleSize))
            {
                canvas.DrawRect(rect, fill);
                canvas.DrawRect(rect, edge);
            }
        }
    }

    /// <summary>
    /// Pre-fills graph history with a gentle synthetic curve around each graph's current value, so a freshly placed
    /// graph (or a gallery thumbnail) does not look empty. Real samples replace it as time passes.
    /// </summary>
    public void SeedGraphHistory(ThemePage page, ISensorSource sensors)
    {
        Index(sensors);
        long now = _clock.ElapsedMilliseconds;
        foreach (var w in page.Widgets.Where(w => w.Kind == WidgetKind.Graph && w.SensorId.Length > 0))
        {
            if (!_index.TryGetValue(w.SensorId, out var s) || s.Value is not { } raw) continue;
            int n = (int)(Math.Clamp(w.HistorySeconds, 5, 900) * 1000 / SensorHistory.SampleMs);
            for (int i = 0; i < n; i++)
            {
                double phase = i * 0.11 + w.SensorId.Length;
                double wobble = 0.04 * Math.Sin(phase) + 0.015 * Math.Sin(phase * 2.3);
                _history.Seed(w.SensorId, now - (n - i) * SensorHistory.SampleMs, (float)(raw * (1 + wobble)));
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
        var samples = _history.Window(w.SensorId, now, window);

        // value range: fixed when Max > Min, otherwise fitted to what is on screen
        double min = w.Min, max = w.Max;
        if (max <= min)
        {
            if (samples.Count == 0) { min = 0; max = 1; }
            else
            {
                min = samples.Min(p => p.value) * w.Multiplier; max = samples.Max(p => p.value) * w.Multiplier;
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

        if (samples.Count > 0)
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
            for (int i = 1; i < samples.Count; i++) line.LineTo(X(samples[i].ms), Y(samples[i].value));

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
        var r = Box(w);
        w.LastBounds = r;
        var path = t.ResolveAsset(w.ImagePath);
        SKImage? img = null;
        var fit = w.Fit;
        string? note = null;

        if (Ffmpeg.IsVideo(path))
        {
            var v = GetVideo(path!, (int)Math.Round(w.Width), (int)Math.Round(w.Height), w.Fit);
            img = v.Latest;
            fit = ImageFit.Stretch; // ffmpeg already fitted the frame to the widget box
            if (img is null) note = v.Error is null ? "Loading video..." : v.Error;
        }
        else if (LoadImage(path) is { } anim)
        {
            img = anim.At(_clock.ElapsedMilliseconds);
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
        float sx = r.Width / img.Width, sy = r.Height / img.Height;
        switch (fit)
        {
            case ImageFit.Contain:
            {
                var s = Math.Min(sx, sy);
                float dw = img.Width * s, dh = img.Height * s;
                c.DrawImage(img, SKRect.Create(r.Left + (r.Width - dw) / 2, r.Top + (r.Height - dh) / 2, dw, dh), sampling, paint);
                break;
            }
            case ImageFit.Cover:
            {
                var s = Math.Max(sx, sy);
                float dw = img.Width * s, dh = img.Height * s;
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

    private VideoSource GetVideo(string path, int w, int h, ImageFit fit)
    {
        var key = $"{path}|{w}x{h}|{fit}";
        if (!_videos.TryGetValue(key, out var v)) _videos[key] = v = new VideoSource(path, w, h, fit);
        v.LastUsedTicks = Environment.TickCount64;
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

    /// <summary>Loads a font file once (null when it is missing or not a font).</summary>
    private SKTypeface? FileFace(string path)
    {
        if (_fileFaces.TryGetValue(path, out var f)) return f;
        try { f = File.Exists(path) ? SKTypeface.FromFile(path) : null; } catch { f = null; }
        return _fileFaces[path] = f;
    }

    private AnimatedImage? LoadImage(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_images.TryGetValue(path, out var b)) return b;
        b = File.Exists(path) ? AnimatedImage.Load(path) : null;
        return _images[path] = b;
    }

    /// <summary>Forget cached images and stop videos (call after a file changes on disk).</summary>
    public void ClearImageCache()
    {
        foreach (var b in _images.Values) b?.Dispose();
        _images.Clear();
        foreach (var v in _videos.Values) v.Dispose();
        _videos.Clear();
        _fileFaces.Clear();   // font files are re-read the next time they are drawn
    }

    private static SKColor Parse(string s, SKColor fallback) => SKColor.TryParse(s, out var c) ? c : fallback;

    public void Dispose()
    {
        foreach (var f in _fileFaces.Values) f?.Dispose();
        ClearImageCache();
        _history.Clear();
    }
}

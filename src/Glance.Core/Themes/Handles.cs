using SkiaSharp;

namespace Glance.Core.Themes;

public enum Handle { None, TopLeft, Top, TopRight, Left, Right, BottomLeft, Bottom, BottomRight }

/// <summary>Where the resize handles of a selected widget sit. Shared by the renderer (draws them) and the editor (hit-tests them).</summary>
public static class Handles
{
    /// <summary>Bars, gauges, graphs, images and panels resize freely; text widgets scale their font from the corners.</summary>
    public static bool IsFree(Widget w) => w.Kind is WidgetKind.Bar or WidgetKind.Gauge or WidgetKind.Graph or WidgetKind.Image or WidgetKind.Panel;

    public static IEnumerable<(Handle handle, SKRect rect)> Layout(Widget w, float size)
    {
        var b = w.LastBounds;
        if (b.Width <= 0 && b.Height <= 0) yield break;
        float h = size / 2, cx = b.MidX, cy = b.MidY;
        SKRect At(float x, float y) => new(x - h, y - h, x + h, y + h);

        yield return (Handle.TopLeft, At(b.Left, b.Top));
        yield return (Handle.TopRight, At(b.Right, b.Top));
        yield return (Handle.BottomLeft, At(b.Left, b.Bottom));
        yield return (Handle.BottomRight, At(b.Right, b.Bottom));
        if (!IsFree(w)) yield break;
        yield return (Handle.Top, At(cx, b.Top));
        yield return (Handle.Bottom, At(cx, b.Bottom));
        yield return (Handle.Left, At(b.Left, cy));
        yield return (Handle.Right, At(b.Right, cy));
    }

    /// <summary>The handle under a point (theme coordinates), or None. <paramref name="tolerance"/> enlarges the hit area.</summary>
    public static Handle HitTest(Widget w, float x, float y, float size, float tolerance)
    {
        foreach (var (handle, rect) in Layout(w, size))
        {
            var r = rect;
            r.Inflate(tolerance, tolerance);
            if (r.Contains(x, y)) return handle;
        }
        return Handle.None;
    }
}

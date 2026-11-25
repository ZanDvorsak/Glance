using SkiaSharp;

namespace Glance.Core.Themes;

/// <summary>Widgets that share a <see cref="Widget.Group"/> move and resize together. This holds the logic for that,
/// kept out of the editor so it can be tested.</summary>
public static class GroupOps
{
    /// <summary>The widgets that go with <paramref name="w"/>: everything sharing its group, or just itself when it has none.</summary>
    public static List<Widget> Members(IEnumerable<Widget> page, Widget w)
    {
        if (string.IsNullOrEmpty(w.Group)) return new List<Widget> { w };
        var list = page.Where(x => x.Group == w.Group).ToList();
        if (!list.Contains(w)) list.Add(w);
        return list;
    }

    /// <summary>The rectangle around the visible members (from where they were last drawn); empty when none was drawn.</summary>
    public static SKRect Union(IEnumerable<Widget> members)
    {
        bool any = false;
        SKRect u = default;
        foreach (var m in members)
        {
            var b = m.LastBounds;
            if (!m.Visible || (b.Width <= 0 && b.Height <= 0)) continue;
            u = any ? SKRect.Union(u, b) : b;
            any = true;
        }
        return u;
    }

    /// <summary>A name for a new group that no widget on the given pages uses yet.</summary>
    public static string NewGroupName(IEnumerable<string> existing)
    {
        var used = existing.ToHashSet();
        int n = 1;
        while (used.Contains($"Group {n}")) n++;
        return $"Group {n}";
    }

    /// <summary>Scales a set of widgets as one object. Created when a resize starts (it remembers how things were);
    /// <see cref="Apply"/> then lays the widgets out for the new outer rectangle, always starting from the remembered state,
    /// so dragging back and forth does not accumulate rounding errors.</summary>
    public sealed class Scaler
    {
        private sealed record Start(Widget W, double X, double Y, double Width, double Height, double FontSize, double Thickness, double CornerRadius);

        private readonly List<Start> _start;
        private readonly SKRect _box;

        public Scaler(IEnumerable<Widget> members, SKRect startBox)
        {
            _box = startBox;
            _start = members.Select(w => new Start(w, w.X, w.Y, w.Width, w.Height, w.FontSize, w.Thickness, w.CornerRadius)).ToList();
        }

        public void Apply(SKRect target)
        {
            double sx = _box.Width > 0 ? target.Width / _box.Width : 1;
            double sy = _box.Height > 0 ? target.Height / _box.Height : 1;
            double s = Math.Min(sx, sy);   // text, line and corner sizes follow the smaller factor so nothing overflows

            foreach (var st in _start)
            {
                var w = st.W;
                w.X = Math.Round(target.Left + (st.X - _box.Left) * sx);
                w.Y = Math.Round(target.Top + (st.Y - _box.Top) * sy);
                if (Handles.IsFree(w))
                {
                    w.Width = Math.Max(1, Math.Round(st.Width * sx));
                    w.Height = Math.Max(1, Math.Round(st.Height * sy));
                }
                if (w.Kind is WidgetKind.Text or WidgetKind.Sensor or WidgetKind.Clock or WidgetKind.Gauge)
                    w.FontSize = Math.Clamp(Math.Round(st.FontSize * s, 1), 4, 400);
                if (w.Kind == WidgetKind.Gauge) w.Thickness = Math.Max(1, Math.Round(st.Thickness * s, 1));
                if (w.Kind is WidgetKind.Panel or WidgetKind.Bar) w.CornerRadius = Math.Max(0, Math.Round(st.CornerRadius * s, 1));
            }
        }
    }

    /// <summary>The rectangle a free-form resize drag produces: the dragged edges follow the pointer, the others stay, and
    /// <paramref name="keepAspect"/> holds the ratio on corners.</summary>
    public static SKRect DragRect(SKRect start, Handle handle, double px, double py, bool keepAspect, double min = 2)
    {
        bool left = handle is Handle.TopLeft or Handle.Left or Handle.BottomLeft;
        bool right = handle is Handle.TopRight or Handle.Right or Handle.BottomRight;
        bool top = handle is Handle.TopLeft or Handle.Top or Handle.TopRight;
        bool bottom = handle is Handle.BottomLeft or Handle.Bottom or Handle.BottomRight;

        double l = start.Left, t = start.Top, rt = start.Right, b = start.Bottom;
        if (left) l = Math.Min(px, rt - min);
        if (right) rt = Math.Max(px, l + min);
        if (top) t = Math.Min(py, b - min);
        if (bottom) b = Math.Max(py, t + min);
        bool corner = (left || right) && (top || bottom);
        if (keepAspect && corner && start.Height > 0)
        {
            double ratio = start.Width / start.Height, nw = rt - l, nh = b - t;
            if (nw / nh > ratio) nw = nh * ratio; else nh = nw / ratio;
            if (left) l = rt - nw; else rt = l + nw;
            if (top) t = b - nh; else b = t + nh;
        }
        return new SKRect((float)l, (float)t, (float)rt, (float)b);
    }
}

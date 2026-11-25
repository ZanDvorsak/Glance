using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using SkiaSharp;

namespace Glance.Core.Themes;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WidgetKind { Text, Sensor, Clock, Bar, Gauge, Graph, Image, Panel }

/// <summary>For text widgets, X is the left edge, the centre, or the right edge.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TextAlign { Left, Center, Right }

/// <summary>One element on the screen. A single flat type keeps JSON and the property editor simple.</summary>
public partial class Widget : ObservableObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    [ObservableProperty] private string _name = "Widget";
    [ObservableProperty] private WidgetKind _kind = WidgetKind.Text;

    [ObservableProperty] private double _x;
    [ObservableProperty] private double _y;
    /// <summary>Used by Bar, Gauge, Graph, Image and Panel. Text-like widgets size themselves.</summary>
    [ObservableProperty] private double _width = 100;
    [ObservableProperty] private double _height = 20;

    /// <summary>Static text (Text), label prefix (Sensor), caption (Gauge), or date format (Clock, e.g. HH:mm:ss).</summary>
    [ObservableProperty] private string _text = "Text";
    [ObservableProperty] private string _sensorId = "";
    /// <summary>.NET numeric format for sensor values, e.g. 0 or 0.0.</summary>
    [ObservableProperty] private string _format = "0";
    [ObservableProperty] private string _unit = "";
    /// <summary>Multiplies the sensor value before display (e.g. 1024 to show GB as MB).</summary>
    [ObservableProperty] private double _multiplier = 1;

    [ObservableProperty] private string _fontFamily = "Segoe UI";
    [ObservableProperty] private double _fontSize = 20;
    [ObservableProperty] private bool _bold;
    [ObservableProperty] private TextAlign _align = TextAlign.Left;

    /// <summary>Text colour / bar, gauge and graph colour / panel colour, as #AARRGGBB.</summary>
    [ObservableProperty] private string _color = "#FFFFFFFF";
    /// <summary>Background of a bar, the track of a gauge, the grid of a graph, as #AARRGGBB.</summary>
    [ObservableProperty] private string _backColor = "#55FFFFFF";
    /// <summary>Colour of the value text inside a gauge.</summary>
    [ObservableProperty] private string _textColor = "#FFFFFFFF";
    [ObservableProperty] private double _cornerRadius = 6;
    [ObservableProperty] private double _min;
    [ObservableProperty] private double _max = 100;

    // ---- gauge and graph ----------------------------------------------------------------------------------------
    /// <summary>Arc thickness (Gauge) or line width (Graph), in pixels.</summary>
    [ObservableProperty] private double _thickness = 8;
    /// <summary>Gauge arc start, in degrees clockwise from the 3 o'clock position (135 = bottom left).</summary>
    [ObservableProperty] private double _startAngle = 135;
    /// <summary>Gauge arc length in degrees (270 = classic gauge, 360 = full ring).</summary>
    [ObservableProperty] private double _sweep = 270;
    /// <summary>How many seconds of history a Graph shows.</summary>
    [ObservableProperty] private double _historySeconds = 60;
    [ObservableProperty] private bool _showGrid = true;

    // ---- value thresholds (Sensor text, Bar, Gauge, Graph); 0 = off --------------------------------------------
    [ObservableProperty] private double _warnAt;
    [ObservableProperty] private string _warnColor = "#FFFFC107";
    [ObservableProperty] private double _critAt;
    [ObservableProperty] private string _critColor = "#FFFF5252";

    /// <summary>Image, GIF or video file.</summary>
    [ObservableProperty] private string _imagePath = "";
    [ObservableProperty] private Glance.Core.Media.ImageFit _fit = Glance.Core.Media.ImageFit.Stretch;
    /// <summary>0 (invisible) to 100 (opaque). Applies to images, GIFs and videos.</summary>
    [ObservableProperty] private double _opacity = 100;
    [ObservableProperty] private bool _visible = true;
    /// <summary>Widgets sharing a non-empty group move together in the editor (hold Alt to move just one).</summary>
    [ObservableProperty] private string _group = "";

    /// <summary>Bounds computed during the last render, used for hit-testing in the editor.</summary>
    [JsonIgnore] public SKRect LastBounds { get; set; }

    public Widget Clone()
    {
        var c = (Widget)MemberwiseClone();
        c.Id = Guid.NewGuid().ToString("N");
        c.Name = Name + " copy";
        return c;
    }

    public static Widget Create(WidgetKind kind, double x = 20, double y = 20) => kind switch
    {
        WidgetKind.Text => new Widget { Kind = kind, Name = "Text", X = x, Y = y, Text = "Label", FontSize = 18 },
        WidgetKind.Sensor => new Widget { Kind = kind, Name = "Sensor", X = x, Y = y, Text = "", FontSize = 32, Bold = true },
        WidgetKind.Clock => new Widget { Kind = kind, Name = "Clock", X = x, Y = y, Text = "HH:mm:ss", FontSize = 22 },
        WidgetKind.Bar => new Widget { Kind = kind, Name = "Bar", X = x, Y = y, Width = 160, Height = 14, Color = "#FF40C4FF", BackColor = "#55FFFFFF" },
        WidgetKind.Gauge => new Widget
        {
            Kind = kind, Name = "Gauge", X = x, Y = y, Width = 130, Height = 130, Text = "", FontSize = 28, Bold = true, Thickness = 12,
            Color = "#FF40C4FF", BackColor = "#33FFFFFF", WarnAt = 70, CritAt = 90,
        },
        WidgetKind.Graph => new Widget
        {
            Kind = kind, Name = "Graph", X = x, Y = y, Width = 220, Height = 80, Thickness = 2, Color = "#FF40C4FF", BackColor = "#26FFFFFF", HistorySeconds = 60,
        },
        WidgetKind.Image => new Widget { Kind = kind, Name = "Image", X = x, Y = y, Width = 120, Height = 120 },
        WidgetKind.Panel => new Widget { Kind = kind, Name = "Panel", X = x, Y = y, Width = 200, Height = 100, Color = "#78000000" },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

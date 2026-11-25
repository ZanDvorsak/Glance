using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Glance.Core.Themes;

namespace Glance.App.Views;

/// <summary>Builds an editor for the selected widget (or the theme itself). Editors stay in sync with the model while dragging.</summary>
public sealed class PropertiesPanel : ScrollViewer
{
    private readonly StackPanel _stack = new() { Spacing = 6, Margin = new Thickness(10) };
    private readonly Dictionary<string, Action> _updaters = new();
    private ObservableObject? _target;
    private bool _suppress;

    /// <summary>Raised after any value is edited by the user.</summary>
    public event Action<string>? Edited;
    /// <summary>Asks the host to show a file picker; returns the chosen path or null.</summary>
    public Func<Task<string?>>? PickImage { get; set; }

    private static readonly Lazy<string[]> Fonts = new(() =>
        Avalonia.Media.FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().OrderBy(n => n).ToArray());

    public PropertiesPanel() { Content = _stack; HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled; }

    public void Show(Theme theme, Widget? w)
    {
        if (_target is not null) _target.PropertyChanged -= OnModelChanged;
        _updaters.Clear();
        _stack.Children.Clear();

        if (w is null)
        {
            _target = theme;
            Header("Theme");
            Text("Name", "Name"); Num("Width", "Width", 1, 4096, 1); Num("Height", "Height", 1, 4096, 1);
            ColorRow("Background", "BackgroundColor");
            Hint("Click a widget in the preview or the layer list to edit it.");
        }
        else
        {
            _target = w;
            Header(w.Kind.ToString());
            Text("Name", "Name"); Check("Visible", "Visible"); Text("Group", "Group");
            Num("X", "X", -2000, 4000, 1); Num("Y", "Y", -2000, 4000, 1);
            switch (w.Kind)
            {
                case WidgetKind.Text:
                    Text("Text", "Text"); FontRows(); ColorRow("Colour", "Color"); break;
                case WidgetKind.Sensor:
                    Text("Label", "Text"); Text("Sensor", "SensorId"); Hint("Double-click a sensor in the list on the right to assign it.");
                    Text("Format", "Format"); Text("Unit", "Unit"); Num("Multiplier", "Multiplier", -1000000, 1000000, 1);
                    FontRows(); ColorRow("Colour", "Color"); Thresholds(); break;
                case WidgetKind.Gauge:
                    Num("Width", "Width", 4, 4000, 1); Num("Height", "Height", 4, 4000, 1);
                    Text("Sensor", "SensorId"); Hint("Double-click a sensor in the list on the right to assign it.");
                    Text("Caption", "Text"); Text("Unit", "Unit"); Text("Format", "Format");
                    Num("Min", "Min", -100000, 100000, 1); Num("Max", "Max", -100000, 100000, 1); Num("Multiplier", "Multiplier", -1000000, 1000000, 1);
                    Num("Thickness", "Thickness", 1, 200, 1); Num("Start angle", "StartAngle", -360, 360, 5); Num("Sweep", "Sweep", 10, 360, 5);
                    Hint("Sweep 270 with start 135 is a classic gauge; sweep 360 with start -90 is a full ring.");
                    FontRows(); ColorRow("Arc", "Color"); ColorRow("Track", "BackColor"); ColorRow("Text", "TextColor"); Thresholds(); break;
                case WidgetKind.Graph:
                    Num("Width", "Width", 4, 4000, 1); Num("Height", "Height", 4, 4000, 1);
                    Text("Sensor", "SensorId"); Hint("Double-click a sensor in the list on the right to assign it.");
                    Num("Min", "Min", -100000, 100000, 1); Num("Max", "Max", -100000, 100000, 1);
                    Hint("Set Min and Max equal (for example both 0) to fit the graph to the data automatically.");
                    Num("Multiplier", "Multiplier", -1000000, 1000000, 1); Num("History (s)", "HistorySeconds", 5, 900, 5);
                    Num("Line width", "Thickness", 1, 12, 0.5); Check("Grid lines", "ShowGrid");
                    ColorRow("Line", "Color"); ColorRow("Grid", "BackColor"); Thresholds(); break;
                case WidgetKind.Clock:
                    Text("Format", "Text"); FontRows(); ColorRow("Colour", "Color"); break;
                case WidgetKind.Bar:
                    Num("Width", "Width", 1, 4000, 1); Num("Height", "Height", 1, 4000, 1);
                    Text("Sensor", "SensorId"); Hint("Double-click a sensor in the list on the right to assign it.");
                    Num("Min", "Min", -100000, 100000, 1); Num("Max", "Max", -100000, 100000, 1);
                    Num("Corner radius", "CornerRadius", 0, 200, 1); ColorRow("Fill", "Color"); ColorRow("Track", "BackColor"); Thresholds(); break;
                case WidgetKind.Image:
                    Num("Width", "Width", 1, 4000, 1); Num("Height", "Height", 1, 4000, 1);
                    Text("File", "ImagePath"); BrowseButton();
                    EnumRow("Fit", "Fit"); Num("Opacity %", "Opacity", 0, 100, 5);
                    Hint("Supports PNG, JPG, animated GIF and video (MP4, WebM, MKV...; video needs FFmpeg)."); break;
                case WidgetKind.Panel:
                    Num("Width", "Width", 1, 4000, 1); Num("Height", "Height", 1, 4000, 1);
                    Num("Corner radius", "CornerRadius", 0, 200, 1); ColorRow("Colour", "Color"); break;
            }
        }
        _target.PropertyChanged += OnModelChanged;
    }

    private void OnModelChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } n && _updaters.TryGetValue(n, out var up))
        {
            var prev = _suppress; _suppress = true;
            try { up(); } finally { _suppress = prev; }
        }
    }

    // ---- row builders -------------------------------------------------------------------------------------------

    private PropertyInfo Prop(string name) => _target!.GetType().GetProperty(name)!;

    private void Set(string prop, object? value)
    {
        if (_suppress || _target is null) return;
        var pi = Prop(prop);
        pi.SetValue(_target, pi.PropertyType.IsEnum ? value : Convert.ChangeType(value, pi.PropertyType));
        Edited?.Invoke(prop);
    }

    private void EnumRow(string label, string prop)
    {
        var type = Prop(prop).PropertyType;
        var combo = new ComboBox { ItemsSource = Enum.GetValues(type), SelectedItem = Prop(prop).GetValue(_target) };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is { } v) Set(prop, v); };
        _updaters[prop] = () => combo.SelectedItem = Prop(prop).GetValue(_target);
        Row(label, combo);
    }

    private void Header(string t) => _stack.Children.Add(new TextBlock { Text = t.ToUpperInvariant(), FontWeight = FontWeight.Bold, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 4) });
    private void Hint(string t) => _stack.Children.Add(new TextBlock { Text = t, Opacity = 0.55, FontSize = 11, TextWrapping = TextWrapping.Wrap });

    private void Row(string label, Control editor)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*") };
        g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 });
        Grid.SetColumn(editor, 1);
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        g.Children.Add(editor);
        _stack.Children.Add(g);
    }

    private void Text(string label, string prop)
    {
        var tb = new TextBox { Text = Convert.ToString(Prop(prop).GetValue(_target)) };
        tb.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) Set(prop, tb.Text ?? ""); };
        _updaters[prop] = () => { var v = Convert.ToString(Prop(prop).GetValue(_target)); if (tb.Text != v) tb.Text = v; };
        Row(label, tb);
    }

    private void Num(string label, string prop, double min, double max, double step)
    {
        var nud = new NumericUpDown { Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, FormatString = "0.##",
            Value = Convert.ToDecimal(Prop(prop).GetValue(_target)) };
        nud.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.ValueProperty && nud.Value is { } v) Set(prop, (double)v); };
        _updaters[prop] = () => { var v = Convert.ToDecimal(Prop(prop).GetValue(_target)); if (nud.Value != v) nud.Value = v; };
        Row(label, nud);
    }

    private void Check(string label, string prop)
    {
        var cb = new CheckBox { IsChecked = (bool)Prop(prop).GetValue(_target)! };
        cb.PropertyChanged += (_, e) => { if (e.Property == ToggleButton_IsChecked) Set(prop, cb.IsChecked == true); };
        _updaters[prop] = () => cb.IsChecked = (bool)Prop(prop).GetValue(_target)!;
        Row(label, cb);
    }
    private static readonly AvaloniaProperty ToggleButton_IsChecked = Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty;

    private void ColorRow(string label, string prop)
    {
        var cp = new ColorPicker { IsAlphaEnabled = true };
        Color Cur() => Color.TryParse(Convert.ToString(Prop(prop).GetValue(_target)), out var c) ? c : Colors.White;
        cp.Color = Cur();
        cp.PropertyChanged += (_, e) => { if (e.Property == ColorPicker.ColorProperty) Set(prop, cp.Color.ToString()); };
        _updaters[prop] = () => { if (cp.Color != Cur()) cp.Color = Cur(); };
        Row(label, cp);
    }

    /// <summary>Colour changes when the value reaches the warning or critical level (0 turns a level off).</summary>
    private void Thresholds()
    {
        Header("Colour thresholds");
        Num("Warn at", "WarnAt", 0, 1000000, 1); ColorRow("Warn colour", "WarnColor");
        Num("Critical at", "CritAt", 0, 1000000, 1); ColorRow("Critical colour", "CritColor");
        Hint("Levels are in the displayed unit (after the multiplier). 0 turns a level off.");
    }

    private void FontRows()
    {
        var combo = new ComboBox { ItemsSource = Fonts.Value, SelectedItem = Prop("FontFamily").GetValue(_target) as string, MaxDropDownHeight = 300 };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is string s) Set("FontFamily", s); };
        _updaters["FontFamily"] = () => combo.SelectedItem = Prop("FontFamily").GetValue(_target) as string;
        Row("Font", combo);
        Num("Size", "FontSize", 4, 400, 1);
        Check("Bold", "Bold");
        EnumRow("Align", "Align");
    }

    private void BrowseButton()
    {
        var b = new Button { Content = "Browse for image..." };
        b.Click += async (_, _) =>
        {
            if (PickImage is null) return;
            var path = await PickImage();
            if (path is not null) Set("ImagePath", path);
            if (path is not null && _updaters.TryGetValue("ImagePath", out var up)) up();
        };
        _stack.Children.Add(b);
    }
}

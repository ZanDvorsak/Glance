using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Glance.Core.Presets;
using Glance.Core.Sensors;
using Glance.Core.Themes;
using SkiaSharp;

namespace Glance.App.Views;

/// <summary>Premade widgets bound to this PC's sensors. Pick a style and colour, then add them to the screen in one click.</summary>
public sealed class GalleryWindow : Window
{
    private static readonly (string name, string argb)[] Accents =
    {
        ("Blue", "#FF40C4FF"), ("Green", "#FF34D399"), ("Orange", "#FFFF9F43"), ("Purple", "#FFA78BFA"),
        ("Pink", "#FFF472B6"), ("Red", "#FFFF6B6B"), ("White", "#FFFFFFFF"),
    };

    private readonly ISensorSource _sensors;
    private readonly Action<WidgetPreset, PresetOptions> _onAdd;
    private readonly WrapPanel _cards = new() { Margin = new Thickness(2) };
    private readonly WrapPanel _accentPanel = new();
    private readonly WrapPanel _categoryPanel = new();
    private readonly ComboBox _panelStyle = new() { ItemsSource = Enum.GetValues<PanelStyle>(), SelectedItem = PanelStyle.Glass, MinWidth = 110 };
    private string _accent = Accents[0].argb;
    private string? _category;

    public GalleryWindow(ISensorSource sensors, Action<WidgetPreset, PresetOptions> onAdd)
    {
        _sensors = sensors; _onAdd = onAdd;
        Title = "Widget gallery";
        Width = 1040; Height = 740; MinWidth = 700; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        BuildAccentChips();
        BuildCategoryChips();
        _panelStyle.SelectionChanged += (_, _) => Rebuild();

        var title = new TextBlock { Text = "Widget gallery", FontSize = 20, FontWeight = FontWeight.Bold };
        var subtitle = new TextBlock { Text = "Ready-made widgets wired to this PC's sensors. Add as many as you like, then fine-tune them in the editor.", Opacity = 0.65, FontSize = 12, Margin = new Thickness(0, 2, 0, 12) };

        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Margin = new Thickness(0, 0, 0, 10) };
        options.Children.Add(Labeled("Accent", _accentPanel));
        options.Children.Add(Labeled("Card style", _panelStyle));
        var cats = Labeled("Show", _categoryPanel);

        var scroll = new ScrollViewer { Content = _cards, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var dock = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(title, Dock.Top); DockPanel.SetDock(subtitle, Dock.Top); DockPanel.SetDock(options, Dock.Top); DockPanel.SetDock(cats, Dock.Top);
        dock.Children.Add(title); dock.Children.Add(subtitle); dock.Children.Add(options); dock.Children.Add(cats);
        cats.Margin = new Thickness(0, 0, 0, 10);
        dock.Children.Add(scroll);
        Content = dock;

        Rebuild();
    }

    private static Control Labeled(string label, Control c)
    {
        var cap = new TextBlock { Text = label.ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        cap.Classes.Add("caption");
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { cap, c } };
    }

    private void BuildAccentChips()
    {
        _accentPanel.Children.Clear();
        foreach (var (name, argb) in Accents)
        {
            var color = Color.Parse(argb);
            bool selected = argb == _accent;
            var chip = new Button
            {
                Width = 26, Height = 26, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(color), BorderBrush = selected ? Brushes.White : Brushes.Transparent, BorderThickness = new Thickness(2),
            };
            ToolTip.SetTip(chip, name);
            chip.Click += (_, _) => { _accent = argb; BuildAccentChips(); Rebuild(); };
            _accentPanel.Children.Add(chip);
        }
    }

    private void BuildCategoryChips()
    {
        _categoryPanel.Children.Clear();
        var all = new List<ToggleButton>();
        ToggleButton Chip(string text, string? value)
        {
            var chip = new ToggleButton { Content = text, IsChecked = _category == value, FontSize = 12, Padding = new Thickness(11, 4), Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(999), MinHeight = 0 };
            chip.Click += (_, _) => { foreach (var c in all) c.IsChecked = ReferenceEquals(c, chip); _category = value; Rebuild(); };
            all.Add(chip); _categoryPanel.Children.Add(chip);
            return chip;
        }
        Chip("All", null);
        foreach (var c in PresetLibrary.Categories) Chip(c, c);
    }

    private void Rebuild()
    {
        _cards.Children.Clear();
        var snapshot = _sensors.Snapshot;
        var options = new PresetOptions(_accent, _panelStyle.SelectedItem is PanelStyle s ? s : PanelStyle.Glass);
        using var renderer = new ThemeRenderer();

        foreach (var preset in PresetLibrary.All.Where(p => _category is null || p.Category == _category))
        {
            var result = preset.Build(snapshot, options);
            _cards.Children.Add(MakeCard(preset, result, options, renderer));
        }
    }

    private Control MakeCard(WidgetPreset preset, PresetResult result, PresetOptions options, ThemeRenderer renderer)
    {
        // shown at real size, but scaled down (never up) when a widget is wider than the card
        var image = new Image { Source = Thumbnail(result, renderer), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, HorizontalAlignment = HorizontalAlignment.Center };
        var stage = new Border
        {
            Child = image, Height = 190, CornerRadius = new CornerRadius(10), Padding = new Thickness(10), ClipToBounds = true,
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse("#5B3A1E"), 0), new GradientStop(Color.Parse("#1D2A4F"), 1) },
            },
        };
        var name = new TextBlock { Text = preset.Name, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 10, 0, 2) };
        var desc = new TextBlock { Text = preset.Description, Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 8) };
        var add = new Button { Content = "Add to screen", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Background = new SolidColorBrush(Color.Parse("#4F8CFF")), Foreground = Brushes.White };
        add.Click += async (_, _) =>
        {
            _onAdd(preset, options);
            add.Content = "Added  ✓";
            await Task.Delay(1200);
            add.Content = "Add to screen";
        };

        var stack = new StackPanel { Children = { stage, name, desc } };
        if (result.Missing.Count > 0)
            stack.Children.Add(new TextBlock
            {
                Text = "No sensor found for: " + string.Join(", ", result.Missing) + ". You can assign one after adding.",
                FontSize = 11, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#FF9F43")), Margin = new Thickness(2, 0, 0, 8),
            });
        stack.Children.Add(add);

        var card = new Border { Child = stack, Width = 300, Margin = new Thickness(0, 0, 14, 14) };
        card.Classes.Add("card");
        return card;
    }

    /// <summary>Renders the preset with this PC's current values (graphs get a pre-filled history) into an Avalonia bitmap.</summary>
    private Bitmap Thumbnail(PresetResult result, ThemeRenderer renderer)
    {
        var theme = new Theme { Width = (int)Math.Ceiling(result.Width), Height = (int)Math.Ceiling(result.Height), BackgroundColor = "#00000000" };
        foreach (var w in result.Widgets) theme.Widgets.Add(w);
        renderer.SeedGraphHistory(theme.ActivePage, _sensors);

        using var bmp = new SKBitmap(new SKImageInfo(theme.Width, theme.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        renderer.Render(canvas, theme, _sensors);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return new Bitmap(new MemoryStream(data.ToArray()));
    }
}

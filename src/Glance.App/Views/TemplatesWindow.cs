using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Glance.Core.Presets;
using Glance.Core.Sensors;
using Glance.Core.Themes;
using SkiaSharp;

namespace Glance.App.Views;

/// <summary>"New theme": pick a starter layout, an accent colour and a card style. Previews use this PC's real sensor values.</summary>
public sealed class TemplatesWindow : Window
{
    private static readonly (string name, string argb)[] Accents =
    {
        ("Blue", "#FF40C4FF"), ("Green", "#FF34D399"), ("Orange", "#FFFF9F43"), ("Purple", "#FFA78BFA"),
        ("Pink", "#FFF472B6"), ("Red", "#FFFF6B6B"), ("White", "#FFFFFFFF"),
    };

    private readonly ISensorSource _sensors;
    private readonly Action<ThemeTemplate, PresetOptions> _onPick;
    private readonly WrapPanel _cards = new() { Margin = new Thickness(2) };
    private readonly WrapPanel _accentPanel = new();
    private readonly ComboBox _panelStyle = new() { ItemsSource = Enum.GetValues<PanelStyle>(), SelectedItem = PanelStyle.Glass, MinWidth = 110 };
    private string _accent = Accents[0].argb;

    public TemplatesWindow(ISensorSource sensors, Action<ThemeTemplate, PresetOptions> onPick)
    {
        _sensors = sensors; _onPick = onPick;
        Title = "New theme";
        Width = 1090; Height = 760; MinWidth = 700; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        BuildAccentChips();
        _panelStyle.SelectionChanged += (_, _) => Rebuild();

        var title = new TextBlock { Text = "New theme", FontSize = 20, FontWeight = FontWeight.Bold };
        var subtitle = new TextBlock { Text = "Start from a ready-made layout wired to this PC's sensors, then change anything you like. Your current theme is not touched until you save over it.", Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12) };

        Control Labeled(string label, Control c)
        {
            var cap = new TextBlock { Text = label.ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            cap.Classes.Add("caption");
            return new StackPanel { Orientation = Orientation.Horizontal, Children = { cap, c } };
        }
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, Margin = new Thickness(0, 0, 0, 12) };
        options.Children.Add(Labeled("Accent", _accentPanel));
        options.Children.Add(Labeled("Card style", _panelStyle));

        var cancel = new Button { Content = "Cancel", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        cancel.Click += (_, _) => Close();

        var dock = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(title, Dock.Top); DockPanel.SetDock(subtitle, Dock.Top); DockPanel.SetDock(options, Dock.Top); DockPanel.SetDock(cancel, Dock.Bottom);
        dock.Children.Add(title); dock.Children.Add(subtitle); dock.Children.Add(options); dock.Children.Add(cancel);
        dock.Children.Add(new ScrollViewer { Content = _cards, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        Content = dock;
        Rebuild();
    }

    private void BuildAccentChips()
    {
        _accentPanel.Children.Clear();
        foreach (var (name, argb) in Accents)
        {
            bool selected = argb == _accent;
            var chip = new Button
            {
                Width = 26, Height = 26, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0), CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Color.Parse(argb)), BorderBrush = selected ? Brushes.White : Brushes.Transparent, BorderThickness = new Thickness(2),
            };
            ToolTip.SetTip(chip, name);
            chip.Click += (_, _) => { _accent = argb; BuildAccentChips(); Rebuild(); };
            _accentPanel.Children.Add(chip);
        }
    }

    private void Rebuild()
    {
        _cards.Children.Clear();
        var options = new PresetOptions(_accent, _panelStyle.SelectedItem is PanelStyle s ? s : PanelStyle.Glass);
        using var renderer = new ThemeRenderer();
        foreach (var template in ThemeTemplates.All)
            _cards.Children.Add(MakeCard(template, options, renderer));
    }

    private Control MakeCard(ThemeTemplate template, PresetOptions options, ThemeRenderer renderer)
    {
        var theme = template.Build(_sensors.Snapshot, options);
        var image = new Image { Source = Thumbnail(theme, renderer), Width = 300, Height = 200, Stretch = Stretch.Uniform };
        var stage = new Border { Child = image, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = Brushes.Black };
        var pages = theme.Pages.Count > 1 ? $"  ·  {theme.Pages.Count} pages" : "";
        var name = new TextBlock { Text = template.Name + pages, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 10, 0, 2) };
        var desc = new TextBlock { Text = template.Description, Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 0, 0, 10), MinHeight = 32 };
        var use = new Button { Content = "Use this theme", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Background = new SolidColorBrush(Color.Parse("#4F8CFF")), Foreground = Brushes.White };
        use.Click += (_, _) => { _onPick(template, options); Close(); };

        var card = new Border { Child = new StackPanel { Children = { stage, name, desc, use } }, Width = 320, Margin = new Thickness(0, 0, 14, 14) };
        card.Classes.Add("card");
        return card;
    }

    private Bitmap Thumbnail(Theme theme, ThemeRenderer renderer)
    {
        renderer.SeedGraphHistory(theme.Pages[0], _sensors);
        using var bmp = new SKBitmap(new SKImageInfo(theme.Width, theme.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        for (int i = 0; i < 2; i++) renderer.Render(canvas, theme, _sensors, null, theme.Pages[0]);   // twice: the first pass measures text
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return new Bitmap(new MemoryStream(data.ToArray()));
    }
}

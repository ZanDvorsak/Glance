using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MiniLcd.Core.Import;
using MiniLcd.Core.Sensors;

namespace MiniLcd.App.Views;

/// <summary>Shows what an AIDA64 import would look like (with live sensor values) and asks for confirmation.</summary>
public sealed class ImportPreviewWindow : Window
{
    private readonly PreviewSurface _surface;

    public ImportPreviewWindow(string fileName, AidaImportResult result, ISensorSource sensors, bool helperRunning)
    {
        Title = "Import preview - " + fileName;
        Width = 1240; Height = 700; MinWidth = 900; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _surface = new PreviewSurface(result.Theme, sensors, 1.5);
        Closed += (_, _) => _surface.Dispose();

        // left: the screen
        var screen = new Border { Child = _surface, CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = Brushes.Black };
        var bezel = new Border
        {
            Child = screen, Padding = new Thickness(14), CornerRadius = new CornerRadius(20), Background = new SolidColorBrush(Color.Parse("#06070A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2B3342")), BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 40, OffsetY = 14, Color = Color.Parse("#99000000") }),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var caption = new TextBlock { Text = "Live preview with this PC's sensors", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0), FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#8C96A8")) };
        var left = new Border { Padding = new Thickness(12) };
        left.Classes.Add("card");
        left.Background = new SolidColorBrush(Color.Parse("#10131A"));
        left.Child = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { bezel, caption } };

        // right: summary + notes
        var headline = new TextBlock
        {
            Text = $"{result.Imported} items → {result.Theme.Widgets.Count} widgets", FontSize = 18, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4),
        };
        var sub = new TextBlock
        {
            Text = result.Unmapped == 0 ? "Every value was matched to a sensor on this PC." : $"{result.Unmapped} value(s) have no matching sensor and show \"--\" until you assign one.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 0, 0, 10),
        };
        var items = new List<Control>();
        if (!helperRunning)
            items.Add(new TextBlock { Text = "! The elevated sensor helper isn't running, so CPU and motherboard sensors may be unmatched. Start it and import again for a fuller match.", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#FF9F43")), FontSize = 12 });
        foreach (var n in result.Notes)
        {
            var line = new TextBlock { Text = n, TextWrapping = TextWrapping.Wrap, FontSize = 12, FontFamily = new FontFamily("Consolas") };
            if (n.StartsWith("No matching")) line.Foreground = new SolidColorBrush(Color.Parse("#FF9F43")); // never set Foreground to null: that makes the text invisible
            items.Add(line);
        }
        var notes = new ListBox { ItemsSource = items };

        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close(false);
        var import = new Button { Content = "Import this theme", Background = new SolidColorBrush(Color.Parse("#4F8CFF")), Foreground = Brushes.White };
        import.Click += (_, _) => Close(true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { cancel, import } };

        var right = new DockPanel();
        var notesCap = new TextBlock { Text = "WHAT WAS MAPPED", FontSize = 11, FontWeight = FontWeight.SemiBold, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(headline, Dock.Top); DockPanel.SetDock(sub, Dock.Top); DockPanel.SetDock(notesCap, Dock.Top); DockPanel.SetDock(buttons, Dock.Bottom);
        right.Children.Add(headline); right.Children.Add(sub); right.Children.Add(notesCap); right.Children.Add(buttons); right.Children.Add(notes);
        var rightCard = new Border { Child = right, Width = 440 };
        rightCard.Classes.Add("card");

        var root = new DockPanel { Margin = new Thickness(14) };
        DockPanel.SetDock(rightCard, Dock.Right);
        rightCard.Margin = new Thickness(12, 0, 0, 0);
        root.Children.Add(rightCard);
        root.Children.Add(left);
        Content = root;
    }
}

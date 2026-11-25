using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Glance.App.Views;

/// <summary>Simple modal dialog: a headline plus a scrollable list of lines.</summary>
public sealed class ReportWindow : Window
{
    public ReportWindow(string title, string headline, IEnumerable<string> lines)
    {
        Title = title;
        Width = 760; Height = 540; MinWidth = 480; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var list = new ListBox
        {
            ItemsSource = lines.Select(l => new TextBlock { Text = l, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12 }).ToList(),
        };
        var ok = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        ok.Click += (_, _) => Close();

        var head = new TextBlock { Text = headline, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        var dock = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(head, Dock.Top); DockPanel.SetDock(ok, Dock.Bottom);
        dock.Children.Add(head); dock.Children.Add(ok); dock.Children.Add(list);
        Content = dock;
    }
}

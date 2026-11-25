using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MiniLcd.Core.Sensors;

namespace MiniLcd.App.Views;

/// <summary>
/// Searchable list of every sensor with live values, grouped by hardware (CPU, GPU, ...) and filterable by group,
/// type (Temperature, Fan, ...) and free text. Double-click to pick one.
/// </summary>
public sealed class SensorBrowser : DockPanel
{
    private sealed record Row(SensorInfo Info, string Category, ListBoxItem Item, TextBlock Value, string Haystack);

    private readonly TextBox _filter = new() { PlaceholderText = "Search sensors (e.g. fan, nct, core #3)...", Margin = new Thickness(0, 0, 0, 6) };
    private readonly WrapPanel _groupChips = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly WrapPanel _typeChips = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly ListBox _list = new();
    private readonly TextBlock _count = new() { Opacity = 0.6, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    private readonly Dictionary<string, Row> _rows = new();
    private readonly Dictionary<string, (ListBoxItem item, TextBlock text)> _headers = new();
    private int _knownCount = -1;
    private string? _group;     // null = all
    private string? _type;      // null = all

    public event Action<SensorInfo>? Picked;

    public SensorBrowser()
    {
        var top = new StackPanel { Children = { _filter, Caption("GROUP"), _groupChips, Caption("TYPE"), _typeChips } };
        SetDock(top, Dock.Top);
        SetDock(_count, Dock.Bottom);
        Children.Add(top);
        Children.Add(_count);
        Children.Add(_list);

        _filter.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) ApplyFilter(); };
        _list.DoubleTapped += (_, _) =>
        {
            if (_list.SelectedItem is ListBoxItem { Tag: SensorInfo info }) Picked?.Invoke(info);
        };
    }

    private static TextBlock Caption(string text)
    {
        var t = new TextBlock { Text = text, Margin = new Thickness(2, 0, 0, 3) };
        t.Classes.Add("caption");
        return t;
    }

    public void Refresh(IReadOnlyList<SensorInfo> sensors)
    {
        if (sensors.Count != _knownCount) Rebuild(sensors);

        foreach (var s in sensors)
            if (_rows.TryGetValue(s.Id, out var row))
                row.Value.Text = s.Value is { } v ? $"{v:0.##} {s.Unit}".Trim() : "n/a";
    }

    private void Rebuild(IReadOnlyList<SensorInfo> sensors)
    {
        _knownCount = sensors.Count;
        _rows.Clear();
        _headers.Clear();

        var ordered = sensors
            .Select(s => (info: s, cat: SensorCategory.Of(s)))
            .OrderBy(x => SensorCategory.CategoryRank(x.cat)).ThenBy(x => x.info.Hardware)
            .ThenBy(x => SensorCategory.TypeRank(x.info.Type)).ThenBy(x => x.info.Name)
            .ToList();

        var items = new List<ListBoxItem>();
        string? lastCat = null;
        foreach (var (info, cat) in ordered)
        {
            if (cat != lastCat)
            {
                lastCat = cat;
                var headerText = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#4F8CFF")) };
                var header = new ListBoxItem { Content = headerText, IsHitTestVisible = false, Focusable = false, Margin = new Thickness(0, 8, 0, 2), Padding = new Thickness(4, 2) };
                _headers[cat] = (header, headerText);
                items.Add(header);
            }

            var value = new TextBlock { HorizontalAlignment = HorizontalAlignment.Right, FontWeight = FontWeight.SemiBold };
            var name = new TextBlock { Text = info.Name, TextTrimming = TextTrimming.CharacterEllipsis };
            var sub = new TextBlock { Text = $"{info.Hardware}  ·  {info.Type}", FontSize = 11, Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            grid.Children.Add(new StackPanel { Children = { name, sub } });
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
            var item = new ListBoxItem { Content = grid, Tag = info };
            ToolTip.SetTip(item, info.Id);
            _rows[info.Id] = new Row(info, cat, item, value, $"{cat} {info.Hardware} {info.Name} {info.Type} {info.Id}".ToLowerInvariant());
            items.Add(item);
        }
        _list.ItemsSource = items;

        // chips: only groups and types that actually exist on this machine
        var groups = ordered.GroupBy(x => x.cat).OrderBy(g => SensorCategory.CategoryRank(g.Key)).Select(g => (g.Key, g.Count())).ToList();
        var types = ordered.GroupBy(x => x.info.Type).OrderBy(g => SensorCategory.TypeRank(g.Key)).ThenBy(g => g.Key).Select(g => (g.Key, g.Count())).ToList();
        if (_group is not null && groups.All(g => g.Key != _group)) _group = null;
        if (_type is not null && types.All(t => t.Key != _type)) _type = null;
        BuildChips(_groupChips, groups, _group, v => { _group = v; ApplyFilter(); });
        BuildChips(_typeChips, types, _type, v => { _type = v; ApplyFilter(); });
        ApplyFilter();
    }

    private static void BuildChips(WrapPanel panel, List<(string name, int count)> entries, string? selected, Action<string?> onPick)
    {
        panel.Children.Clear();
        var all = new List<ToggleButton>();

        ToggleButton Chip(string text, string? value, bool isOn)
        {
            var chip = new ToggleButton
            {
                Content = text, IsChecked = isOn, FontSize = 11, Padding = new Thickness(9, 3), Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(999), MinHeight = 0,
            };
            chip.Click += (_, _) =>
            {
                foreach (var c in all) c.IsChecked = ReferenceEquals(c, chip);   // behaves like a radio group
                onPick(value);
            };
            all.Add(chip);
            panel.Children.Add(chip);
            return chip;
        }

        Chip("All", null, selected is null);
        foreach (var (name, count) in entries) Chip($"{name}  {count}", name, name == selected);
    }

    private void ApplyFilter()
    {
        var words = (_filter.Text ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shownPerGroup = new Dictionary<string, int>();
        int shown = 0;
        foreach (var r in _rows.Values)
        {
            var ok = (_group is null || r.Category == _group)
                     && (_type is null || r.Info.Type == _type)
                     && words.All(w => r.Haystack.Contains(w));
            r.Item.IsVisible = ok;
            if (!ok) continue;
            shown++;
            shownPerGroup[r.Category] = shownPerGroup.GetValueOrDefault(r.Category) + 1;
        }
        foreach (var (cat, h) in _headers)
        {
            var n = shownPerGroup.GetValueOrDefault(cat);
            h.item.IsVisible = n > 0;
            h.text.Text = $"{cat.ToUpperInvariant()}  ·  {n}";
        }
        _count.Text = $"{shown} of {_rows.Count} sensors · double-click to assign to the selected widget";
    }
}

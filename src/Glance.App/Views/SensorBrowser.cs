using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Glance.Core.Sensors;

namespace Glance.App.Views;

/// <summary>
/// Searchable list of every sensor with live values, grouped by hardware (CPU, GPU, ...) and filterable by group,
/// type (Temperature, Fan, ...) and free text. Double-click to pick one.
/// </summary>
public sealed class SensorBrowser : DockPanel
{
    private sealed class Row(SensorInfo info, string category, string haystack)
    {
        public SensorInfo Info { get; } = info;
        public string Category { get; } = category;
        public string Haystack { get; } = haystack;
        public TextBlock? Value { get; set; }   // the value cell of the row's current list item
    }

    private readonly TextBox _filter = new() { PlaceholderText = "Search sensors (e.g. fan, nct, core #3)...", Margin = new Thickness(0, 0, 0, 6) };
    private readonly WrapPanel _groupChips = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly WrapPanel _typeChips = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly ListBox _list = new();
    private readonly TextBlock _count = new() { Opacity = 0.6, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    private readonly Dictionary<string, Row> _rows = new();
    /// <summary>Every header and row in display order. The list is given only the entries that pass the filter, because hiding items
    /// inside a virtualizing list does not refresh reliably (stale rows stayed visible).</summary>
    private readonly List<(string? header, Row? row)> _entries = new();
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
                if (row.Value is { } cell) cell.Text = s.Value is { } v ? $"{v:0.##} {s.Unit}".Trim() : "n/a";
    }

    private void Rebuild(IReadOnlyList<SensorInfo> sensors)
    {
        _knownCount = sensors.Count;
        _rows.Clear();
        _entries.Clear();

        var ordered = sensors
            .Select(s => (info: s, cat: SensorCategory.Of(s)))
            .OrderBy(x => SensorCategory.CategoryRank(x.cat)).ThenBy(x => x.info.Hardware)
            .ThenBy(x => SensorCategory.TypeRank(x.info.Type)).ThenBy(x => x.info.Name)
            .ToList();

        string? lastCat = null;
        foreach (var (info, cat) in ordered)
        {
            if (cat != lastCat) { lastCat = cat; _entries.Add((cat, null)); }
            var row = new Row(info, cat, $"{cat} {info.Hardware} {info.Name} {info.Type} {info.Id}".ToLowerInvariant());
            _rows[info.Id] = row;
            _entries.Add((null, row));
        }
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
        bool Passes(Row r) => (_group is null || r.Category == _group)
                              && (_type is null || r.Info.Type == _type)
                              && words.All(w => r.Haystack.Contains(w));

        var shownPerGroup = new Dictionary<string, int>();
        foreach (var r in _rows.Values.Where(Passes))
            shownPerGroup[r.Category] = shownPerGroup.GetValueOrDefault(r.Category) + 1;

        // build fresh items for exactly what passes (a group header only when it has rows); reusing or hiding old
        // items in the list left stale or blank rows
        var visible = new List<ListBoxItem>();
        foreach (var (header, row) in _entries)
        {
            if (header is not null)
            {
                if (!shownPerGroup.TryGetValue(header, out var n)) continue;
                var text = new TextBlock { Text = $"{header.ToUpperInvariant()}  ·  {n}", FontWeight = FontWeight.SemiBold, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#4F8CFF")) };
                visible.Add(new ListBoxItem { Content = text, IsHitTestVisible = false, Focusable = false, Margin = new Thickness(0, 8, 0, 2), Padding = new Thickness(4, 2) });
            }
            else if (row is not null && Passes(row))
            {
                visible.Add(MakeItem(row));
            }
        }
        _list.ItemsSource = visible;

        _count.Text = $"{shownPerGroup.Values.Sum()} of {_rows.Count} sensors · double-click to assign to the selected widget";
    }

    private static ListBoxItem MakeItem(Row row)
    {
        var info = row.Info;
        var value = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Right, FontWeight = FontWeight.SemiBold,
            Text = info.Value is { } v ? $"{v:0.##} {info.Unit}".Trim() : "n/a",
        };
        row.Value = value;
        var name = new TextBlock { Text = info.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        var sub = new TextBlock { Text = $"{info.Hardware}  ·  {info.Type}", FontSize = 11, Opacity = 0.55, TextTrimming = TextTrimming.CharacterEllipsis };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new StackPanel { Children = { name, sub } });
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        var item = new ListBoxItem { Content = grid, Tag = info };
        ToolTip.SetTip(item, info.Id);
        return item;
    }
}
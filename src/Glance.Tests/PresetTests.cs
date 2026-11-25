using Glance.Core.Presets;
using Glance.Core.Themes;

namespace Glance.Tests;

public class PresetTests
{
    private static readonly PresetOptions Options = new("#FF40C4FF", PanelStyle.Glass);

    public static IEnumerable<object[]> AllPresets() => PresetLibrary.All.Select(p => new object[] { p.Id });
    private static WidgetPreset Get(string id) => PresetLibrary.All.Single(p => p.Id == id);

    [Fact]
    public void Presets_have_unique_ids_names_and_descriptions_in_known_categories()
    {
        Assert.True(PresetLibrary.All.Count >= 25);
        Assert.Equal(PresetLibrary.All.Count, PresetLibrary.All.Select(p => p.Id).Distinct().Count());
        Assert.Equal(PresetLibrary.All.Count, PresetLibrary.All.Select(p => p.Name).Distinct().Count());
        Assert.All(PresetLibrary.All, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
            Assert.Contains(p.Category, PresetLibrary.Categories);
        });
    }

    [Theory, MemberData(nameof(AllPresets))]
    public void Every_preset_finds_all_of_its_sensors_on_a_typical_PC(string id)
    {
        var r = Get(id).Build(Fake.Typical(), Options);
        Assert.Empty(r.Missing);
        Assert.NotEmpty(r.Widgets);
        foreach (var w in r.Widgets.Where(w => w.Kind is WidgetKind.Sensor or WidgetKind.Bar or WidgetKind.Gauge or WidgetKind.Graph))
            Assert.False(string.IsNullOrEmpty(w.SensorId), $"{id}: '{w.Name}' has no sensor");
    }

    [Theory, MemberData(nameof(AllPresets))]
    public void Every_preset_keeps_its_widgets_inside_its_own_size(string id)
    {
        var r = Get(id).Build(Fake.Typical(), Options);
        foreach (var w in r.Widgets)
        {
            Assert.InRange(w.X, -0.5, r.Width + 0.5);
            Assert.InRange(w.Y, -0.5, r.Height + 0.5);
            if (w.Kind is WidgetKind.Panel or WidgetKind.Bar or WidgetKind.Gauge or WidgetKind.Graph)
            {
                Assert.True(w.X + w.Width <= r.Width + 0.5, $"{id}: '{w.Name}' sticks out on the right");
                Assert.True(w.Y + w.Height <= r.Height + 0.5, $"{id}: '{w.Name}' sticks out at the bottom");
            }
        }
    }

    [Theory, MemberData(nameof(AllPresets))]
    public void Every_preset_still_builds_when_no_sensors_exist(string id)
    {
        var r = Get(id).Build(new List<Glance.Core.Sensors.SensorInfo>(), Options);   // a PC with nothing readable
        Assert.NotEmpty(r.Widgets);
        Assert.All(r.Widgets.Where(w => w.Kind is WidgetKind.Sensor or WidgetKind.Bar or WidgetKind.Gauge or WidgetKind.Graph), w => Assert.Equal("", w.SensorId));
    }

    [Fact]
    public void Sensor_based_presets_report_what_is_missing_and_clock_presets_do_not()
    {
        var none = new List<Glance.Core.Sensors.SensorInfo>();
        Assert.Contains("CPU temperature", Get("cpu-temp-card").Build(none, Options).Missing);
        Assert.Empty(Get("clock-card").Build(none, Options).Missing);
        Assert.Empty(Get("clock-compact").Build(none, Options).Missing);
    }

    [Fact]
    public void Card_style_controls_the_background_panel()
    {
        var sensors = Fake.Typical();
        Assert.Equal(WidgetKind.Panel, Get("cpu-temp-card").Build(sensors, new PresetOptions(Options.Accent, PanelStyle.Glass)).Widgets[0].Kind);
        Assert.Equal(WidgetKind.Panel, Get("cpu-temp-card").Build(sensors, new PresetOptions(Options.Accent, PanelStyle.Solid)).Widgets[0].Kind);
        Assert.DoesNotContain(Get("cpu-temp-card").Build(sensors, new PresetOptions(Options.Accent, PanelStyle.None)).Widgets, w => w.Kind == WidgetKind.Panel);
    }

    [Fact]
    public void The_accent_colour_reaches_bars_gauges_and_graphs()
    {
        var sensors = Fake.Typical();
        var pink = new PresetOptions("#FFF472B6", PanelStyle.Glass);
        foreach (var id in new[] { "cpu-temp-card", "cpu-temp-gauge", "cpu-temp-graph" })
            Assert.Contains(Get(id).Build(sensors, pink).Widgets, w => w.Kind is WidgetKind.Bar or WidgetKind.Gauge or WidgetKind.Graph && w.Color == "#FFF472B6");
    }

    [Fact]
    public void Temperature_cards_turn_amber_and_red_at_75_and_90()
    {
        var value = Get("cpu-temp-card").Build(Fake.Typical(), Options).Widgets.Single(w => w.Kind == WidgetKind.Sensor);
        Assert.Equal((75.0, 90.0), (value.WarnAt, value.CritAt));
    }

    [Fact]
    public void Core_bars_match_the_number_of_cores_and_fill_upwards()
    {
        var r = Get("cpu-cores").Build(Fake.Typical(), Options);
        var bars = r.Widgets.Where(w => w.Kind == WidgetKind.Bar).ToList();
        Assert.Equal(8, bars.Count);
        Assert.All(bars, b => Assert.True(b.Vertical));
        Assert.Equal(bars.Select(b => b.X).OrderBy(x => x), bars.Select(b => b.X));   // left to right in core order
    }

    [Fact]
    public void Units_are_converted_where_AIDA_and_the_sensors_disagree()
    {
        var net = Get("network-card").Build(Fake.Typical(), Options).Widgets.Single(w => w.Name == "Download");
        Assert.Equal(1.0 / 1024, net.Multiplier, 6);   // bytes per second shown as KB/s
    }

    [Fact]
    public void The_drive_card_uses_the_first_drive_found()
    {
        var r = Get("drive-card").Build(Fake.Typical(), Options);
        Assert.Contains(r.Widgets, w => w.SensorId == "/drive/c/data/0");
        Assert.Contains(r.Widgets, w => w.Kind == WidgetKind.Text && w.Text == "DRIVE C:");
    }
}

public class TemplateTests
{
    private static readonly PresetOptions Options = new("#FF40C4FF", PanelStyle.Glass);

    [Fact]
    public void The_expected_starter_themes_exist()
    {
        var ids = ThemeTemplates.All.Select(t => t.Id).ToList();
        foreach (var id in new[] { "minimal", "dashboard", "gamer", "pages", "desk-clock", "blank" }) Assert.Contains(id, ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Every_template_fits_the_480_by_320_screen()
    {
        foreach (var template in ThemeTemplates.All)
        {
            var theme = template.Build(Fake.Typical(), Options);
            Assert.Equal((480, 320), (theme.Width, theme.Height));
            foreach (var page in theme.Pages)
                foreach (var w in page.Widgets.Where(w => w.Kind is WidgetKind.Panel or WidgetKind.Bar or WidgetKind.Gauge or WidgetKind.Graph))
                {
                    Assert.True(w.X >= -0.5 && w.Y >= -0.5, $"{template.Id}: '{w.Name}' starts off-screen");
                    Assert.True(w.X + w.Width <= 480.5, $"{template.Id}: '{w.Name}' runs past the right edge");
                    Assert.True(w.Y + w.Height <= 320.5, $"{template.Id}: '{w.Name}' runs past the bottom edge");
                }
        }
    }

    [Fact]
    public void Template_widgets_come_in_groups_so_they_move_together()
    {
        var theme = ThemeTemplates.All.Single(t => t.Id == "dashboard").Build(Fake.Typical(), Options);
        var groups = theme.Pages[0].Widgets.GroupBy(w => w.Group).ToList();
        Assert.Equal(6, groups.Count);                          // six gauges
        Assert.All(groups, g => Assert.False(string.IsNullOrEmpty(g.Key)));
    }

    [Fact]
    public void The_overview_and_history_template_has_two_named_pages()
    {
        var theme = ThemeTemplates.All.Single(t => t.Id == "pages").Build(Fake.Typical(), Options);
        Assert.Equal(new[] { "Overview", "History" }, theme.Pages.Select(p => p.Name));
        Assert.True(theme.AutoRotate);
        Assert.Contains(theme.Pages[1].Widgets, w => w.Kind == WidgetKind.Graph);
    }

    [Fact]
    public void Blank_is_empty_and_desk_clock_centres_its_big_time()
    {
        Assert.Empty(ThemeTemplates.All.Single(t => t.Id == "blank").Build(Fake.Typical(), Options).Pages[0].Widgets);
        var clock = ThemeTemplates.All.Single(t => t.Id == "desk-clock").Build(Fake.Typical(), Options).Widgets.First(w => w.Kind == WidgetKind.Clock);
        Assert.Equal((TextAlign.Center, 240.0), (clock.Align, clock.X));
    }

    [Fact]
    public void Templates_do_not_share_widget_instances_between_builds()
    {
        var t = ThemeTemplates.All.Single(x => x.Id == "minimal");
        var a = t.Build(Fake.Typical(), Options); var b = t.Build(Fake.Typical(), Options);
        a.Widgets[0].X = 12345;
        Assert.NotEqual(12345, b.Widgets[0].X);
        Assert.NotEqual(a.Widgets[0].Id, b.Widgets[0].Id);
    }
}

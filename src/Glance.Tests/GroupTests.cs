using Glance.Core.Themes;
using SkiaSharp;

namespace Glance.Tests;

public class GroupTests
{
    private static (Theme theme, Widget card, Widget label, Widget lone) Sample()
    {
        var t = Fake.Canvas(400, 300);
        var card = new Widget { Kind = WidgetKind.Panel, X = 20, Y = 20, Width = 100, Height = 60, CornerRadius = 10, Group = "G" };
        var label = new Widget { Kind = WidgetKind.Text, Text = "CPU", X = 30, Y = 30, FontSize = 20, Group = "G" };
        var lone = new Widget { Kind = WidgetKind.Panel, X = 300, Y = 200, Width = 50, Height = 50 };
        t.Widgets.Add(card); t.Widgets.Add(label); t.Widgets.Add(lone);
        using var bmp = Fake.Render(t, null);   // gives every widget its LastBounds
        return (t, card, label, lone);
    }

    [Fact]
    public void Members_are_everything_in_the_group_or_just_the_widget()
    {
        var (t, card, label, lone) = Sample();
        Assert.Equal(new[] { card, label }, GroupOps.Members(t.Widgets, label));
        Assert.Equal(new[] { lone }, GroupOps.Members(t.Widgets, lone));
    }

    [Fact]
    public void Union_covers_all_visible_members_and_ignores_hidden_ones()
    {
        var (t, card, label, _) = Sample();
        var u = GroupOps.Union(new[] { card, label });
        Assert.True(u.Left <= 20 && u.Top <= 20 && u.Right >= 120 && u.Bottom >= 80);
        label.Visible = false;
        Assert.Equal(card.LastBounds, GroupOps.Union(new[] { card, label }));
    }

    [Fact]
    public void New_group_names_do_not_collide()
    {
        Assert.Equal("Group 1", GroupOps.NewGroupName(new[] { "", "Card 1" }));
        Assert.Equal("Group 3", GroupOps.NewGroupName(new[] { "Group 1", "Group 2" }));
        Assert.Equal("Group 2", GroupOps.NewGroupName(new[] { "Group 1", "Group 3" }));
    }

    [Fact]
    public void Scaling_a_group_resizes_every_member_not_just_one()
    {
        var (t, card, label, lone) = Sample();
        var box = GroupOps.Union(new[] { card, label });
        var scaler = new GroupOps.Scaler(new[] { card, label }, box);

        // the group's outline becomes twice as wide and twice as tall, anchored at its top-left corner
        scaler.Apply(new SKRect(box.Left, box.Top, box.Left + box.Width * 2, box.Top + box.Height * 2));

        Assert.Equal(200, card.Width); Assert.Equal(120, card.Height);
        Assert.Equal(20, card.X); Assert.Equal(20, card.Y);
        Assert.Equal(40, label.FontSize);                       // text grows with the group
        Assert.Equal(20, card.CornerRadius);
        Assert.Equal(box.Left + (30 - box.Left) * 2, label.X);  // and moves to keep its place inside it
        Assert.Equal(50, lone.Width);                           // widgets outside the group are untouched
    }

    [Fact]
    public void Scaling_back_to_the_start_restores_the_original_layout()
    {
        var (_, card, label, _) = Sample();
        var members = new[] { card, label };
        var box = GroupOps.Union(members);
        var scaler = new GroupOps.Scaler(members, box);
        scaler.Apply(new SKRect(box.Left, box.Top, box.Left + 333, box.Top + 77));
        scaler.Apply(box);
        Assert.Equal((20, 20, 100, 60, 10), (card.X, card.Y, card.Width, card.Height, card.CornerRadius));
        Assert.Equal((30, 30, 20), (label.X, label.Y, label.FontSize));
    }

    [Fact]
    public void Gauge_thickness_and_text_follow_the_smaller_scale_factor()
    {
        var g = new Widget { Kind = WidgetKind.Gauge, X = 0, Y = 0, Width = 100, Height = 100, FontSize = 20, Thickness = 10, Group = "G" };
        var scaler = new GroupOps.Scaler(new[] { g }, new SKRect(0, 0, 100, 100));
        scaler.Apply(new SKRect(0, 0, 300, 150));   // 3x wide, 1.5x tall
        Assert.Equal(300, g.Width); Assert.Equal(150, g.Height);
        Assert.Equal(30, g.FontSize); Assert.Equal(15, g.Thickness);
    }

    [Fact]
    public void Drag_rect_moves_only_the_dragged_edges_and_can_keep_the_aspect_ratio()
    {
        var start = new SKRect(10, 10, 110, 60);   // 100 x 50
        var right = GroupOps.DragRect(start, Handle.Right, 210, 999, false);
        Assert.Equal(new SKRect(10, 10, 210, 60), right);

        var corner = GroupOps.DragRect(start, Handle.BottomRight, 210, 70, keepAspect: true);   // wants 200 x 60, ratio 2:1 -> 120 x 60
        Assert.Equal(corner.Width / corner.Height, 2f, 0.01);
        Assert.Equal(10, corner.Left); Assert.Equal(10, corner.Top);

        var tiny = GroupOps.DragRect(start, Handle.Right, -500, 0, false, min: 8);   // cannot collapse below the minimum
        Assert.Equal(8, tiny.Width, 0.01);
    }
}

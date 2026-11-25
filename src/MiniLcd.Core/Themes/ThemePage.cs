using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MiniLcd.Core.Themes;

/// <summary>One screen of a theme. A theme with several pages rotates through them on the display.</summary>
public partial class ThemePage : ObservableObject
{
    [ObservableProperty] private string _name = "Page";
    /// <summary>How long the page stays on screen before the next one (when the theme auto-rotates).</summary>
    [ObservableProperty] private double _durationSeconds = 10;
    /// <summary>Overrides the theme background for this page; null/empty uses the theme's.</summary>
    [ObservableProperty] private string? _backgroundColor;

    /// <summary>Drawn bottom to top (first = back).</summary>
    public ObservableCollection<Widget> Widgets { get; set; } = new();
}

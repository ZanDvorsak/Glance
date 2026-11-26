using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Threading;
using Glance.App.Services;
using Glance.Core.Display;
using Glance.Core.Editing;
using Glance.Core.Usb;
using Glance.Core.Settings;
using Glance.App.Views;
using Glance.Core.Media;
using Glance.Core.Presets;
using Glance.Core.Setup;
using Glance.Core.Sensors;
using Glance.Core.Themes;
using SkiaSharp;
using AppTheme = Glance.Core.Themes.Theme;

namespace Glance.App;

public sealed class MainWindow : Window
{
    private double _scale = 1.5;            // current preview zoom (pixels per theme pixel)
    private string _zoom = "Fit";           // "Fit" or a percentage like "150%"

    // pages and screen size
    private readonly WrapPanel _pageChips = new();
    private readonly TextBox _pageName = new() { Width = 130 };
    private readonly NumericUpDown _pageDuration = new() { Width = 110, Minimum = 1, Maximum = 3600, Increment = 1, Value = 10, FormatString = "0" };
    private readonly CheckBox _autoRotate = new() { Content = "Rotate pages automatically", IsChecked = true };
    private readonly ComboBox _screenCombo = new() { MinWidth = 250 };
    private readonly ComboBox _zoomCombo = new() { Width = 90, ItemsSource = new[] { "Fit", "50%", "100%", "150%", "200%", "300%" }, SelectedIndex = 0 };
    private Border _stage = null!;
    private bool _syncingPageBar;
    private VirtualDisplayWindow? _virtual;

    private sealed record ScreenPreset(string Label, int W, int H);
    private static readonly ScreenPreset[] ScreenPresets =
    {
        new("Mini LCD · 480 × 320 (your display)", 480, 320),
        new("Small LCD · 320 × 240", 320, 240),
        new("Wide LCD · 480 × 272", 480, 272),
        new("Tall LCD · 320 × 480", 320, 480),
        new("7\" display · 800 × 480", 800, 480),
        new("10\" display · 1024 × 600", 1024, 600),
        new("HD monitor · 1280 × 720", 1280, 720),
        new("Full HD monitor · 1920 × 1080", 1920, 1080),
    };

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly SensorHub _hub = new();
    private readonly ThemeRenderer _renderer = new();
    private readonly PropertiesPanel _props = new();
    private readonly SensorBrowser _sensors = new();
    private readonly ListBox _layers = new();
    private readonly Image _preview = new() { Stretch = Stretch.Fill };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 };
    private readonly TextBlock _sensorText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly TextBlock _displayText = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Text = "Display idle" };
    private readonly Ellipse _sensorDot = new() { Width = 8, Height = 8, Fill = Brushes.Gray };
    private readonly Ellipse _displayDot = new() { Width = 8, Height = 8, Fill = Brushes.Gray };
    private readonly ToggleButton _send = new() { Content = "Send to display" };
    private readonly Button _helperBtn = new() { Content = "Start background service", IsVisible = false };
    private readonly Button _ffmpegBtn = new() { Content = "Install FFmpeg for video", IsVisible = false };
    private readonly TextBlock _previewCaption = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };

    private AppTheme _theme;
    private string? _themePath;
    private Widget? _selected;
    private bool _syncingLayers;
    private WriteableBitmap? _wb;
    private SKBitmap? _bmp;
    private SKCanvas? _canvas;
    private readonly PusherSlot _display;          // the editor's own display, when no runner owns it
    private DisplayPusher? _pusher => _display.Current;
    private bool _installingFfmpeg;

    // drag / resize state
    private bool _dragging;
    private Point _dragOffset;
    private List<(Widget w, double x, double y)> _dragMembers = new();
    private GalleryWindow? _gallery;
    private int _presetsAdded;
    private Handle _handle;                       // resize handle currently being dragged
    private SKRect _startRect;
    private double _startFont;
    private GroupOps.Scaler? _groupScaler;   // set while a whole group is being resized

    // undo / redo
    private UndoManager _undo = null!;
    private readonly DispatcherTimer _commitTimer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private readonly HashSet<Widget> _hooked = new();
    private AppTheme? _hookedTheme;
    private bool _restoring;
    private Button _undoBtn = null!, _redoBtn = null!;
    private readonly Dictionary<StandardCursorType, Cursor> _cursors = new();

    private static readonly Dictionary<WidgetKind, (string letter, string color)> KindStyle = new()
    {
        [WidgetKind.Text] = ("T", "#4F8CFF"),
        [WidgetKind.Sensor] = ("S", "#FF9F43"),
        [WidgetKind.Clock] = ("C", "#A78BFA"),
        [WidgetKind.Bar] = ("B", "#34D399"),
        [WidgetKind.Gauge] = ("G", "#22D3EE"),
        [WidgetKind.Graph] = ("L", "#FACC15"),
        [WidgetKind.Image] = ("I", "#F472B6"),
        [WidgetKind.Panel] = ("P", "#94A3B8"),
    };

    public MainWindow()
    {
        Title = "Glance";
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Glance.App/Assets/glance.ico"))); } catch { /* cosmetic */ }
        Width = 1440; Height = 880; MinWidth = 1280; MinHeight = 680;
        _display = new PusherSlot(_power, OpenPusher);
        _theme = LoadInitialTheme();
        _undo = new UndoManager(_theme.ToSnapshot());
        HookTheme();
        _commitTimer.Tick += (_, _) => { _commitTimer.Stop(); CommitNow(); };
        _props.PickImage = PickImageAsync;
        _props.PickFont = PickFontAsync;
        _props.Edited += name => { if (name is "Name" or "Visible") RefreshLayers(); };

        Content = BuildLayout();
        WireEvents();

        RefreshLayers();
        SelectWidget(null);
        RefreshPageBar();
        Title = "Glance - " + _theme.Name;

        var preview = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
        preview.Tick += (_, _) => RenderPreview();
        preview.Start();

        var slow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        slow.Tick += (_, _) => SlowTick();
        slow.Start();

        Opened += async (_, _) =>
        {
            var startArgs = Environment.GetCommandLineArgs();   // handy for demos and testing: open a window straight away
            if (startArgs.Contains("--setup")) OpenSetupCheck();
            if (startArgs.Contains("--new")) NewTheme();
            if (startArgs.Contains("--gallery")) OpenGallery();
            if (startArgs.Contains("--service")) new ServiceWindow().Show(this);
            if (startArgs.Contains("--virtual")) OpenVirtualDisplay();
            try { await MaybeShowSetupOnFirstRunAsync(); } catch { /* never block startup */ }
        };

        Closing += (_, _) =>
        {
            StopLive();
            _power.Handle(PowerEvent.Quit);   // nothing may reopen the display from here on
            // Avalonia also closes the window this way when Windows shuts down (WM_QUERYENDSESSION), so this is usually where the
            // session-end blank happens. It runs off the UI thread so the window closes at once; Program.Main waits for it (bounded).
            if (_display.Current is not null) PendingExitBlank = Task.Run(() => _display.Release(blank: true, DisplayPusher.QuickStopTimeout));
            if (OperatingSystem.IsWindows()) _powerWatcher?.Dispose();
            _renderer.Dispose(); _hub.Dispose(); _settings.Save();
        };
        StartPowerWatcher();   // blank the display at shutdown, sign-out and sleep while the editor drives it
    }

    // ---- layout -------------------------------------------------------------------------------------------------

    private Control BuildLayout()
    {
        var root = new Grid { Margin = new Thickness(14), RowSpacing = 12, RowDefinitions = new RowDefinitions("Auto,*,Auto") };

        root.Children.Add(BuildTopBar());

        var body = new Grid { ColumnSpacing = 12, ColumnDefinitions = new ColumnDefinitions("250,*,380") };
        Grid.SetRow(body, 1);
        var left = BuildLeft(); Grid.SetColumn(left, 0);
        var centre = BuildCentre(); Grid.SetColumn(centre, 1);
        var right = BuildRight(); Grid.SetColumn(right, 2);
        body.Children.Add(left); body.Children.Add(centre); body.Children.Add(right);
        root.Children.Add(body);

        var status = BuildStatusBar(); Grid.SetRow(status, 2);
        root.Children.Add(status);
        return root;
    }

    private Control BuildTopBar()
    {
        var logo = new Border
        {
            Width = 34, Height = 34, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.Parse("#4F8CFF")),
            Child = new TextBlock { Text = "G", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 18, 0), Children =
        {
            new TextBlock { Text = "Glance", FontWeight = FontWeight.Bold, FontSize = 15 },
            new TextBlock { Text = "Display designer", FontSize = 11, Foreground = Brush("#8C96A8") },
        }};

        var files = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };   // wraps to a second line on narrow windows
        files.Children.Add(Btn("New", (_, _) => NewTheme()));
        files.Children.Add(Btn("Open", async (_, _) => await OpenAsync()));
        files.Children.Add(Btn("Save", async (_, _) => await SaveAsync(false)));
        files.Children.Add(Btn("Save as", async (_, _) => await SaveAsync(true)));
        files.Children.Add(Btn("Import AIDA64", async (_, _) => await ImportAidaAsync()));
        var export = Btn("Export", async (_, _) => await ExportAsync()); ToolTip.SetTip(export, "Share this theme as a single .glance file");
        files.Children.Add(export);
        files.Children.Add(new Border { Width = 1, Margin = new Thickness(6, 4), Background = Brush("#2B3342") });
        _undoBtn = Btn("Undo", (_, _) => DoUndo()); ToolTip.SetTip(_undoBtn, "Undo (Ctrl+Z)");
        _redoBtn = Btn("Redo", (_, _) => DoRedo()); ToolTip.SetTip(_redoBtn, "Redo (Ctrl+Y)");
        files.Children.Add(_undoBtn); files.Children.Add(_redoBtn);
        foreach (var c in files.Children.OfType<Button>()) c.Margin = new Thickness(0, 3, 6, 3);   // WrapPanel has no spacing of its own
        UpdateUndoButtons();

        var slider = new Slider { Minimum = 0, Maximum = 7, Value = _settings.Brightness, Width = 120, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            _settings.Brightness = (int)Math.Round(slider.Value);
            _pusher?.SetBrightness(_settings.Brightness);
            if (_liveToRunner) { var b = _settings.Brightness; Task.Run(() => RunnerClient.Send(ControlCommand.Brightness(b))); }
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(new TextBlock { Text = "Backlight", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#8C96A8"), Margin = new Thickness(8, 0, 0, 0) });
        right.Children.Add(slider);
        var off = Btn("Display off", (_, _) => TurnDisplayOff());
        ToolTip.SetTip(off, "Turn the display black (backlight off). Click \"Send to display\" to turn it back on.");
        right.Children.Add(off);
        right.Children.Add(_send);

        // two columns: the file buttons take what is left and wrap onto more lines when needed; the display controls keep their own column.
        // (A DockPanel let the two groups draw over each other on narrow windows.)
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { logo, title } };
        var leftSide = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Children = { brand, files } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        right.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(right, 1);
        grid.Children.Add(leftSide);
        grid.Children.Add(right);
        return Card(grid, new Thickness(12, 9));
    }

    private Control BuildLeft()
    {
        var add = new UniformGrid { Columns = 2 };
        foreach (var k in Enum.GetValues<WidgetKind>())
        {
            var kind = k;
            var b = new Button
            {
                Margin = new Thickness(2), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Badge(kind), new TextBlock { Text = k.ToString(), VerticalAlignment = VerticalAlignment.Center } } },
            };
            b.Click += async (_, _) => await AddWidgetAsync(kind);
            add.Children.Add(b);
        }

        var actions = new UniformGrid { Columns = 3, Margin = new Thickness(0, 8, 0, 0) };
        actions.Children.Add(SmallBtn("Up", "Bring forward", (_, _) => MoveSelected(+1)));
        actions.Children.Add(SmallBtn("Down", "Send backward", (_, _) => MoveSelected(-1)));
        actions.Children.Add(SmallBtn("Copy", "Duplicate", (_, _) => DuplicateSelected()));
        actions.Children.Add(SmallBtn("Group", "Group the layers selected in the list (Ctrl+click or Shift+click to select several). A group moves and resizes as one.", (_, _) => GroupSelected()));
        actions.Children.Add(SmallBtn("Ungroup", "Split the selected widget's group back into separate widgets", (_, _) => UngroupSelected()));
        var del = SmallBtn("Delete", "Delete (Del key)", (_, _) => DeleteSelected());
        del.Classes.Add("danger");
        actions.Children.Add(del);

        var dock = new DockPanel();
        var gallery = new Button
        {
            Content = "Widget gallery", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = Brush("#4F8CFF"), Foreground = Brushes.White, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 0, 2, 12),
        };
        ToolTip.SetTip(gallery, "Premade cards, gauges and graphs wired to your sensors");
        gallery.Click += (_, _) => OpenGallery();
        DockPanel.SetDock(gallery, Dock.Top); dock.Children.Add(gallery);
        var addCap = Caption("ADD WIDGET"); addCap.Margin = new Thickness(2, 0, 0, 6);
        DockPanel.SetDock(addCap, Dock.Top); dock.Children.Add(addCap);
        DockPanel.SetDock(add, Dock.Top); dock.Children.Add(add);
        var layerCap = Caption("LAYERS  ·  TOP IS FRONT"); layerCap.Margin = new Thickness(2, 14, 0, 6);
        DockPanel.SetDock(layerCap, Dock.Top); dock.Children.Add(layerCap);
        DockPanel.SetDock(actions, Dock.Bottom); dock.Children.Add(actions);
        dock.Children.Add(_layers);

        // On a short window the card keeps a usable height and the whole column scrolls, so the Add widget buttons stay reachable.
        const double minColumnHeight = 640;
        var card = Card(dock);
        var scroll = new ScrollViewer { Content = card, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SizeChanged += (_, e) => card.Height = Math.Max(minColumnHeight, e.NewSize.Height);
        return scroll;
    }

    private Control BuildCentre()
    {
        var screen = new Border { Child = _preview, CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = Brushes.Black };
        var bezel = new Border
        {
            Child = screen, Padding = new Thickness(14), CornerRadius = new CornerRadius(20),
            Background = Brush("#06070A"), BorderBrush = Brush("#2B3342"), BorderThickness = new Thickness(1),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 40, OffsetY = 14, Color = Color.Parse("#99000000") }),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _previewCaption.Foreground = Brush("#8C96A8");
        _previewCaption.FontSize = 12;
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Children = { bezel, _previewCaption } };
        _stage = new Border { Child = stack };   // its size (not the preview's) drives the auto-fit zoom

        var header = BuildPageHeader();
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(_stage);
        var card = Card(dock);
        card.Background = Brush("#10131A");
        return card;
    }

    /// <summary>Page tabs, the current page's settings, and the screen-size and zoom controls.</summary>
    private Control BuildPageHeader()
    {
        var add = Btn("+ Page", (_, _) => AddPage());
        var dup = Btn("Duplicate", (_, _) => DuplicatePage()); ToolTip.SetTip(dup, "Copy this page");
        var del = Btn("Delete page", (_, _) => DeletePage()); del.Classes.Add("danger");
        var pageRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var pageButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { add, dup, del } };
        DockPanel.SetDock(pageButtons, Dock.Right);
        pageRow.Children.Add(pageButtons);
        pageRow.Children.Add(_pageChips);

        _pageName.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty || _syncingPageBar) return;
            _theme.ActivePage.Name = _pageName.Text ?? "";
            RebuildPageChips();
        };
        _pageDuration.PropertyChanged += (_, e) =>
        {
            if (e.Property == NumericUpDown.ValueProperty && !_syncingPageBar && _pageDuration.Value is { } v) _theme.ActivePage.DurationSeconds = (double)v;
        };
        _autoRotate.IsCheckedChanged += (_, _) => { if (!_syncingPageBar) _theme.AutoRotate = _autoRotate.IsChecked == true; };

        _screenCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingPageBar || _screenCombo.SelectedIndex < 0 || _screenCombo.SelectedIndex >= ScreenPresets.Length) return;
            var p = ScreenPresets[_screenCombo.SelectedIndex];
            _theme.Width = p.W; _theme.Height = p.H;   // widgets keep their positions; the preview resizes on the next frame
            _status.Text = $"Screen set to {p.W} × {p.H}. Widgets keep their positions; drag or resize them to suit.";
        };
        _zoomCombo.SelectionChanged += (_, _) => _zoom = _zoomCombo.SelectedItem as string ?? "Fit";
        var virt = Btn("Virtual display", (_, _) => OpenVirtualDisplay());
        ToolTip.SetTip(virt, "Show the theme live in its own window (for demos without the LCD)");

        Control Cap(string text) { var t = new TextBlock { Text = text.ToUpperInvariant(), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) }; t.Classes.Add("caption"); return t; }
        var settings = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        void AddItem(params Control[] c) { var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 6) }; foreach (var x in c) p.Children.Add(x); settings.Children.Add(p); }
        AddItem(Cap("Page name"), _pageName);
        AddItem(Cap("Shown for (s)"), _pageDuration);
        AddItem(_autoRotate);
        _pinDisplay.IsChecked = _settings.PinDisplayToPage;
        _pinDisplay.IsCheckedChanged += (_, _) => { _settings.PinDisplayToPage = _pinDisplay.IsChecked == true; ApplyPins(); };
        ToolTip.SetTip(_pinDisplay, "Off: the display rotates through all pages while you edit. On: it stays on the page you are editing.");
        AddItem(_pinDisplay);
        AddItem(Cap("Screen"), _screenCombo);
        AddItem(Cap("Zoom"), _zoomCombo);
        AddItem(virt);

        return new StackPanel { Children = { pageRow, settings } };
    }

    // ---- pages --------------------------------------------------------------------------------------------------

    private void RebuildPageChips()
    {
        _pageChips.Children.Clear();
        for (int i = 0; i < _theme.Pages.Count; i++)
        {
            int index = i;
            var page = _theme.Pages[i];
            var chip = new ToggleButton
            {
                Content = $"{i + 1}  {page.Name}", IsChecked = i == _theme.ActivePageIndex, FontSize = 12, Padding = new Thickness(12, 5),
                Margin = new Thickness(0, 0, 6, 6), CornerRadius = new CornerRadius(999), MinHeight = 0,
            };
            chip.Click += (_, _) => SwitchPage(index);
            _pageChips.Children.Add(chip);
        }
    }

    /// <summary>Brings every page control (tabs, name, duration, rotation, screen size) in line with the theme.</summary>
    private void RefreshPageBar()
    {
        _syncingPageBar = true;
        try
        {
            RebuildPageChips();
            _pageName.Text = _theme.ActivePage.Name;
            _pageDuration.Value = (decimal)_theme.ActivePage.DurationSeconds;
            _pageDuration.IsEnabled = _autoRotate.IsEnabled = _theme.Pages.Count > 1;
            _autoRotate.IsChecked = _theme.AutoRotate;

            var items = ScreenPresets.Select(p => p.Label).ToList();
            int match = Array.FindIndex(ScreenPresets, p => p.W == _theme.Width && p.H == _theme.Height);
            if (match < 0) items.Add($"Custom · {_theme.Width} × {_theme.Height}");
            _screenCombo.ItemsSource = items;
            _screenCombo.SelectedIndex = match >= 0 ? match : items.Count - 1;
        }
        finally { _syncingPageBar = false; }
    }

    private void SwitchPage(int index)
    {
        _theme.ActivePageIndex = Math.Clamp(index, 0, _theme.Pages.Count - 1);
        _dragging = false; _handle = Handle.None;
        _selected = null;
        RefreshLayers();
        _props.Show(_theme, null);
        RefreshPageBar();
        // while editing, the display shows the page being edited instead of rotating
        ApplyPins();
    }

    private readonly CheckBox _pinDisplay = new() { Content = "Keep the display on this page while editing" };

    /// <summary>The page the display should stay on, or null to let the theme rotate through its pages.</summary>
    private int? DesiredPin => _pinDisplay.IsChecked == true ? _theme.ActivePageIndex : null;

    private void ApplyPins()
    {
        if (_pusher is { } pusher) pusher.PinnedPage = DesiredPin;   // read once: a Windows sleep event can clear it from another thread
        if (_liveToRunner) { var n = (DesiredPin ?? -1) + 1; Task.Run(() => RunnerClient.Send(ControlCommand.Page(n))); }   // PAGE 0 releases the pin
    }

    private void AddPage()
    {
        _theme.Pages.Add(new ThemePage { Name = $"Page {_theme.Pages.Count + 1}", DurationSeconds = 10 });
        SwitchPage(_theme.Pages.Count - 1);
        _status.Text = "Added a page. With two or more pages the display rotates through them.";
    }

    private void DuplicatePage()
    {
        var src = _theme.ActivePage;
        var copy = new ThemePage { Name = src.Name + " copy", DurationSeconds = src.DurationSeconds, BackgroundColor = src.BackgroundColor };
        foreach (var w in src.Widgets) { var c = w.Clone(); c.Name = w.Name; copy.Widgets.Add(c); }
        _theme.Pages.Insert(_theme.ActivePageIndex + 1, copy);
        SwitchPage(_theme.ActivePageIndex + 1);
    }

    private void DeletePage()
    {
        if (_theme.Pages.Count <= 1) { _status.Text = "A theme needs at least one page."; return; }
        var index = _theme.ActivePageIndex;
        _theme.Pages.RemoveAt(index);
        SwitchPage(Math.Min(index, _theme.Pages.Count - 1));
    }

    private void OpenVirtualDisplay()
    {
        if (_virtual is { IsVisible: true }) { _virtual.Activate(); return; }
        _virtual = new VirtualDisplayWindow(() => _theme, _hub);
        _virtual.Closed += (_, _) => _virtual = null;
        _virtual.Show(this);
    }

    private Control BuildRight()
    {
        var grid = new Grid { RowSpacing = 12, RowDefinitions = new RowDefinitions("2*,3*") };
        grid.RowDefinitions[0].MinHeight = 240;   // properties
        grid.RowDefinitions[1].MinHeight = 440;   // sensor search: chips take about 220 px, the rest is the list
        var propsCard = Card(_props, new Thickness(0)); propsCard.ClipToBounds = true;
        var sensorCard = Card(_sensors); Grid.SetRow(sensorCard, 1);
        grid.Children.Add(propsCard);
        grid.Children.Add(sensorCard);

        // On a short window the two cards keep a usable minimum height and the column scrolls, instead of squeezing the sensor list away.
        const double minColumnHeight = 700;
        var scroll = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SizeChanged += (_, e) => grid.Height = Math.Max(minColumnHeight, e.NewSize.Height);
        return scroll;
    }

    private Control BuildStatusBar()
    {
        Border Chip(Control dot, Control text) => new() { Classes = { "chip" }, Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { dot, text } } };

        _helperBtn.Click += (_, _) =>
        {
            _status.Text = SensorHub.LaunchHost() ? "Sensor helper starting (approve the Windows prompt)..." : "Could not start the sensor helper (not found, or the prompt was declined).";
        };
        _ffmpegBtn.Click += async (_, _) => await InstallFfmpegAsync();

        // the status text gets the leftover width (and is trimmed with an ellipsis); the buttons and chips wrap instead of overlapping
        var right = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var c in new Control[] { _ffmpegBtn, _helperBtn, StatusBtn("Setup check", (_, _) => OpenSetupCheck()), StatusBtn("Background service", async (_, _) => await new ServiceWindow().ShowDialog(this)), Chip(_displayDot, _displayText), Chip(_sensorDot, _sensorText) })
        {
            c.Margin = new Thickness(8, 2, 0, 2);
            right.Children.Add(c);
        }
        _status.TextTrimming = TextTrimming.CharacterEllipsis;
        var grid = new Grid { Margin = new Thickness(4, 0), ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(right, 1);
        grid.Children.Add(_status);
        grid.Children.Add(right);
        return grid;
    }

    // ---- small UI helpers ---------------------------------------------------------------------------------------

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    private static Border Card(Control child, Thickness? padding = null)
    {
        var b = new Border { Child = child };
        b.Classes.Add("card");
        if (padding is { } p) b.Padding = p;
        return b;
    }

    private static TextBlock Caption(string text) { var t = new TextBlock { Text = text }; t.Classes.Add("caption"); return t; }

    private static Control Badge(WidgetKind kind)
    {
        var (letter, color) = KindStyle[kind];
        var c = Color.Parse(color);
        return new Border
        {
            Width = 22, Height = 22, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B)),
            Child = new TextBlock { Text = letter, FontSize = 11, FontWeight = FontWeight.Bold, Foreground = new SolidColorBrush(c), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
    }

    private static Button StatusBtn(string text, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click)
    {
        var b = new Button { Content = text, FontSize = 12, Padding = new Thickness(11, 5) };
        b.Click += click;
        return b;
    }

    private static Button Btn(string text, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click)
    {
        var b = new Button { Content = text };
        b.Click += click;
        return b;
    }

    private static Button SmallBtn(string text, string tip, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click)
    {
        var b = new Button { Content = text, Margin = new Thickness(2), Padding = new Thickness(4, 6), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, FontSize = 12 };
        ToolTip.SetTip(b, tip);
        b.Click += click;
        return b;
    }

    private void WireEvents()
    {
        _layers.SelectionMode = SelectionMode.Multiple;   // Ctrl/Shift+click several layers, then Group
        _layers.SelectionChanged += (_, _) =>
        {
            if (_syncingLayers) return;
            if (_layers.Selection.SelectedIndexes.Count > 1)
            {
                _status.Text = $"{_layers.Selection.SelectedIndexes.Count} layers selected. Click Group to make them move and resize together.";
                return;
            }
            var idx = _layers.SelectedIndex;
            SelectWidget(idx >= 0 ? _theme.Widgets[_theme.Widgets.Count - 1 - idx] : null, fromList: true);
        };

        _preview.PointerPressed += OnPreviewPressed;
        _preview.PointerMoved += OnPreviewMoved;
        _preview.PointerReleased += (_, e) =>
        {
            _dragging = false; _handle = Handle.None; _groupScaler = null;
            e.Pointer.Capture(null);
            _commitTimer.Stop(); CommitNow();      // one undo step per drag or resize
        };

        KeyDown += OnKeyDown;

        _sensors.Picked += info =>
        {
            if (_selected is null || _selected.Kind is not (WidgetKind.Sensor or WidgetKind.Bar or WidgetKind.Gauge or WidgetKind.Graph))
            { _status.Text = "Select a Sensor, Bar, Gauge or Graph widget first, then double-click a sensor."; return; }
            _selected.SensorId = info.Id;
            if (_selected.Kind is WidgetKind.Sensor or WidgetKind.Gauge) _selected.Unit = info.Unit;
            if (_selected.Kind is WidgetKind.Bar or WidgetKind.Gauge) (_selected.Min, _selected.Max) = DefaultRange(info);
            if (_selected.Kind == WidgetKind.Graph) (_selected.Min, _selected.Max) = (0, 0);   // fit to the data
            if (_selected.Name is "Sensor" or "Bar" or "Gauge" or "Graph") _selected.Name = info.Name;
            RefreshLayers();
            _status.Text = $"Assigned: {info.Hardware} / {info.Name}";
        };

        _send.IsCheckedChanged += (_, _) => ToggleSend(_send.IsChecked == true);
    }

    private static (double, double) DefaultRange(SensorInfo s) => s.Type switch
    {
        "Fan" => (0, 3000),
        "Clock" => (0, 6000),
        "Voltage" => (0, 15),
        "Power" => (0, 300),
        _ => (0, 100),
    };

    // ---- theme management ---------------------------------------------------------------------------------------

    private AppTheme LoadInitialTheme()
    {
        // Glance.App.exe <theme.json | theme.glance> opens that file instead of the last theme (also how "Open with" would work)
        var fileArg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a));
        if (fileArg is not null)
        {
            try
            {
                var path = fileArg.EndsWith(ThemePackage.Extension, StringComparison.OrdinalIgnoreCase) ? ImportPackage(fileArg) : fileArg;
                _themePath = path;
                return AppTheme.Load(path);
            }
            catch { _themePath = null; /* fall through to the usual start-up theme */ }
        }

        if (_settings.LastTheme is { } p && File.Exists(p))
        {
            try { _themePath = p; return AppTheme.Load(p); } catch { _themePath = null; }
        }
        return AppTheme.CreateDefault(FindStarterBackground());
    }

    private static string? FindStarterBackground()
    {
        for (var d = AppContext.BaseDirectory; d is not null; d = Path.GetDirectoryName(d))
        {
            var p = Path.Combine(d, "Themes", "autumn (Aida).png");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private void SetTheme(AppTheme t, string? path)
    {
        _theme = t; _themePath = path;
        HookTheme();
        _commitTimer.Stop();
        _undo.Reset(_theme.ToSnapshot());
        UpdateUndoButtons();
        _renderer.ClearImageCache();
        RefreshLayers();
        SelectWidget(null);
        RefreshPageBar();
        ApplyPins();
        Title = "Glance - " + t.Name;
    }

    // ---- undo / redo --------------------------------------------------------------------------------------------

    private readonly HashSet<ThemePage> _hookedPages = new();

    private void HookTheme()
    {
        if (_hookedTheme is not null)
        {
            _hookedTheme.PropertyChanged -= OnModelChanged;
            _hookedTheme.Pages.CollectionChanged -= OnWidgetsChanged;
        }
        foreach (var p in _hookedPages) { p.PropertyChanged -= OnModelChanged; p.Widgets.CollectionChanged -= OnWidgetsChanged; }
        _hookedPages.Clear();
        foreach (var w in _hooked) w.PropertyChanged -= OnModelChanged;
        _hooked.Clear();
        _hookedTheme = _theme;
        _theme.PropertyChanged += OnModelChanged;
        _theme.Pages.CollectionChanged += OnWidgetsChanged;
        HookAll();
    }

    /// <summary>Watches every page and widget of the theme (not just the one being edited) so every change becomes an undo step.</summary>
    private void HookAll()
    {
        foreach (var p in _theme.Pages)
        {
            if (_hookedPages.Add(p)) { p.PropertyChanged += OnModelChanged; p.Widgets.CollectionChanged += OnWidgetsChanged; }
            foreach (var w in p.Widgets) if (_hooked.Add(w)) w.PropertyChanged += OnModelChanged;
        }
        foreach (var gone in _hookedPages.Where(p => !_theme.Pages.Contains(p)).ToList())
        { gone.PropertyChanged -= OnModelChanged; gone.Widgets.CollectionChanged -= OnWidgetsChanged; _hookedPages.Remove(gone); }
        var live = _theme.Pages.SelectMany(p => p.Widgets).ToHashSet();
        foreach (var gone in _hooked.Where(h => !live.Contains(h)).ToList()) { gone.PropertyChanged -= OnModelChanged; _hooked.Remove(gone); }
    }

    private void OnWidgetsChanged(object? s, NotifyCollectionChangedEventArgs e) { HookAll(); OnModelChanged(s, new PropertyChangedEventArgs(null)); }

    /// <summary>Any edit restarts a short timer; when it fires, the burst of changes becomes one undo step.</summary>
    private void OnModelChanged(object? s, PropertyChangedEventArgs e)
    {
        if (_restoring || _dragging || _handle != Handle.None) return;  // drags commit on release
        _commitTimer.Stop(); _commitTimer.Start();
    }

    private void CommitNow()
    {
        if (_restoring) return;
        if (_undo.Commit(_theme.ToSnapshot())) UpdateUndoButtons();
    }

    private void DoUndo() { _commitTimer.Stop(); CommitNow(); Restore(_undo.Undo()); }
    private void DoRedo() { _commitTimer.Stop(); Restore(_undo.Redo()); }

    private void Restore(string? snapshot)
    {
        if (snapshot is null) return;
        var selectedId = _selected?.Id;
        _restoring = true;
        try { _theme.ApplySnapshot(snapshot); HookAll(); }
        finally { _restoring = false; }
        // no cache clearing here: the renderer keys pictures by file and notices files that change on disk, so undo
        // neither decodes every picture again on the UI thread nor restarts videos
        _selected = _theme.Widgets.FirstOrDefault(w => w.Id == selectedId);
        RefreshLayers();
        _props.Show(_theme, _selected);
        RefreshPageBar();
        UpdateUndoButtons();
    }

    private void UpdateUndoButtons()
    {
        _undoBtn.IsEnabled = _undo.CanUndo;
        _redoBtn.IsEnabled = _undo.CanRedo;
    }

    private TemplatesWindow? _templates;

    /// <summary>"New" opens the starter themes. The current theme stays as it is until the user picks one.</summary>
    private void NewTheme()
    {
        if (_templates is { IsVisible: true }) { _templates.Activate(); return; }
        _templates = new TemplatesWindow(_hub, (template, options) =>
        {
            var theme = template.Build(_hub.Snapshot, options);
            SetTheme(theme, null);
            _renderer.SeedGraphHistory(_theme.ActivePage, _hub);
            _status.Text = $"New theme from \"{template.Name}\". Use Save to keep it.";
        });
        _templates.Closed += (_, _) => _templates = null;
        _templates.Show(this);
    }

    /// <summary>Shares the current theme as one .glance file with its images and videos inside.</summary>
    private async Task ExportAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export theme", SuggestedFileName = SafeName(_theme.Name) + ThemePackage.Extension, DefaultExtension = "glance",
            FileTypeChoices = new[] { new FilePickerFileType("Glance theme") { Patterns = new[] { "*.glance" } } },
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        try
        {
            await Task.Run(() => ThemePackage.Export(_theme, path));
            _status.Text = $"Exported to {path} ({new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB). Anyone can open it with Glance.";
        }
        catch (Exception ex) { _status.Text = "Export failed: " + ex.Message; }
    }

    /// <summary>Opens a .glance file by unpacking it into its own folder under the themes folder.</summary>
    private string ImportPackage(string package)
    {
        var baseName = SafeName(Path.GetFileNameWithoutExtension(package));
        var folder = Path.Combine(AppSettings.ThemesFolder, baseName);
        for (int n = 2; Directory.Exists(folder); n++) folder = Path.Combine(AppSettings.ThemesFolder, $"{baseName} {n}");
        try { return ThemePackage.Import(package, folder); }
        catch { try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { } throw; }   // do not leave a half-imported folder
    }

    private async Task OpenAsync()
    {
        Directory.CreateDirectory(AppSettings.ThemesFolder);
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open theme", AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(AppSettings.ThemesFolder),
            FileTypeFilter = new[] { new FilePickerFileType("Glance theme") { Patterns = new[] { "*.json", "*.glance" } } },
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        try
        {
            if (path.EndsWith(ThemePackage.Extension, StringComparison.OrdinalIgnoreCase)) path = ImportPackage(path);   // a shared theme: unpack it first
            SetTheme(AppTheme.Load(path), path); _settings.LastTheme = path; _status.Text = "Opened " + path;
        }
        catch (Exception ex) { _status.Text = "Could not open theme: " + ex.Message; }
    }

    private async Task SaveAsync(bool saveAs)
    {
        var path = _themePath;
        if (path is null || saveAs)
        {
            Directory.CreateDirectory(AppSettings.ThemesFolder);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save theme", SuggestedFileName = SafeName(_theme.Name) + ".json", DefaultExtension = "json",
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(AppSettings.ThemesFolder),
                FileTypeChoices = new[] { new FilePickerFileType("Theme") { Patterns = new[] { "*.json" } } },
            });
            if (file?.TryGetLocalPath() is not { } p) return;
            path = p;
        }
        try
        {
            var moved = _theme.Save(path);
            _undo.AfterSave(_theme.ToSnapshot(), moved);   // a plain save is not an undo step; a move to another folder clears the history
            UpdateUndoButtons();
            _themePath = path; _settings.LastTheme = path; _settings.Save();
            _status.Text = "Saved " + path + (moved ? " (undo history cleared: the theme now lives in a new folder)" : "");
        }
        catch (Exception ex) { _status.Text = "Save failed: " + ex.Message; }
    }

    private async Task ImportAidaAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import an AIDA64 LCD theme", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("AIDA64 LCD theme") { Patterns = new[] { "*.axlcd" } } },
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
        var temp = Path.Combine(Path.GetTempPath(), "Glance", "import-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            // 1) import into a throw-away folder and show it before touching the themes folder
            var sensors = _hub.Snapshot;
            var preview = Core.Import.AidaImporter.Import(path, temp, sensors);
            var ok = await new ImportPreviewWindow(Path.GetFileName(path), preview, _hub, _hub.IsElevated).ShowDialog<bool>(this);
            if (!ok) { _status.Text = "Import cancelled."; return; }

            // 2) confirmed: import again into the real themes folder
            var baseName = SafeName(Path.GetFileNameWithoutExtension(path));
            var folder = Path.Combine(AppSettings.ThemesFolder, baseName);
            for (int n = 2; Directory.Exists(folder); n++) folder = Path.Combine(AppSettings.ThemesFolder, $"{baseName} {n}");

            var res = Core.Import.AidaImporter.Import(path, folder, _hub.Snapshot);
            var themeFile = Path.Combine(folder, "theme.json");
            res.Theme.Save(themeFile);
            SetTheme(res.Theme, themeFile);
            _settings.LastTheme = themeFile; _settings.Save();
            _status.Text = $"Imported {Path.GetFileName(path)} - saved to {folder}";
        }
        catch (Exception ex) { _status.Text = "Import failed: " + ex.Message; }
        finally { try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { } }
    }
    private static string SafeName(string n) => string.Concat(n.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private async Task<string?> PickImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an image, GIF or video", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Images, GIFs and videos") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.mp4", "*.webm", "*.mkv", "*.mov", "*.avi", "*.m4v", "*.wmv" } } },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;   // a changed file is noticed by the renderer itself
    }

    private async Task<string?> PickFontAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a font file", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Fonts") { Patterns = new[] { "*.ttf", "*.otf", "*.ttc" } } },
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    // ---- widgets ------------------------------------------------------------------------------------------------

    private async Task AddWidgetAsync(WidgetKind kind)
    {
        var w = Widget.Create(kind, 30 + _theme.Widgets.Count % 8 * 10, 30 + _theme.Widgets.Count % 8 * 10);
        if (kind == WidgetKind.Image)
        {
            var path = await PickImageAsync();
            if (path is null) return;
            w.ImagePath = path; w.Name = Path.GetFileNameWithoutExtension(path);
            if (!Ffmpeg.IsVideo(path))
            {
                using var data = SKData.Create(path);
                using var codec = data is null ? null : SKCodec.Create(data);
                if (codec is not null)
                {
                    var s = Math.Min(1.0, Math.Min((double)_theme.Width / codec.Info.Width, (double)_theme.Height / codec.Info.Height));
                    w.Width = Math.Round(codec.Info.Width * s); w.Height = Math.Round(codec.Info.Height * s);
                }
            }
            else { w.X = 0; w.Y = 0; w.Width = _theme.Width; w.Height = _theme.Height; w.Fit = ImageFit.Cover; }
        }
        _theme.Widgets.Add(w);
        RefreshLayers();
        SelectWidget(w);
    }

    // ---- setup check -------------------------------------------------------------------------------------------

    private SetupWindow? _setup;

    private SetupContext CurrentSetupContext() => new(_hub.IsElevated, _hub.Snapshot.Count, Autostart.IsInstalled());

    private void OpenSetupCheck()
    {
        if (_setup is { IsVisible: true }) { _setup.Activate(); return; }
        _setup = new SetupWindow(CurrentSetupContext, () => Dispatcher.UIThread.Post(async () => await new ServiceWindow().ShowDialog(this)));
        _setup.Closed += (_, _) => _setup = null;
        _setup.Show(this);
    }

    /// <summary>On the very first run, open the setup check by itself if something required (the display) is not ready.</summary>
    private async Task MaybeShowSetupOnFirstRunAsync()
    {
        if (_settings.SetupCheckShown) return;
        await Task.Delay(2500);   // let sensors and the runner connection settle first
        var ctx = CurrentSetupContext();
        var results = await Task.Run(() => SetupChecks.Run(ctx));
        if (!SetupChecks.NeedsAttention(results)) return;
        _settings.SetupCheckShown = true; _settings.Save();
        OpenSetupCheck();
    }

    // ---- premade widgets ----------------------------------------------------------------------------------------

    private void OpenGallery()
    {
        if (_gallery is { IsVisible: true }) { _gallery.Activate(); return; }
        _gallery = new GalleryWindow(_hub, AddPreset);
        _gallery.Closed += (_, _) => _gallery = null;
        _gallery.Show(this);
    }

    /// <summary>Places a premade widget (a group of widgets bound to this PC's sensors) on the page being edited.</summary>
    private void AddPreset(WidgetPreset preset, PresetOptions options)
    {
        var result = preset.Build(_hub.Snapshot, options);
        int n = 1;
        var groups = _theme.Pages.SelectMany(p => p.Widgets).Select(w => w.Group).ToHashSet();
        while (groups.Contains($"{preset.Name} {n}")) n++;
        var group = $"{preset.Name} {n}";

        // centre it, then cascade a little so repeated adds do not stack exactly
        double cascade = (_presetsAdded++ % 6) * 14;
        double x0 = Math.Max(0, Math.Round((_theme.Width - result.Width) / 2 + cascade));
        double y0 = Math.Max(0, Math.Round((_theme.Height - result.Height) / 2 + cascade));
        foreach (var w in result.Widgets)
        {
            w.X += x0; w.Y += y0; w.Group = group;
            _theme.Widgets.Add(w);
        }
        _renderer.SeedGraphHistory(_theme.ActivePage, _hub);
        RefreshLayers();
        if (result.Widgets.Count > 0) SelectWidget(result.Widgets[0]);
        _status.Text = result.Missing.Count == 0
            ? $"Added \"{preset.Name}\". Drag any part to move it all (hold Alt to move one piece)."
            : $"Added \"{preset.Name}\", but no sensor was found for: {string.Join(", ", result.Missing)}. Assign one from the sensor list.";
    }

    /// <summary>Groups the layers selected in the list (Ctrl/Shift+click) so they move and resize as one.</summary>
    private void GroupSelected()
    {
        var picked = _layers.Selection.SelectedIndexes.Select(i => _theme.Widgets[_theme.Widgets.Count - 1 - i]).ToList();
        if (picked.Count < 2) { _status.Text = "Select two or more layers first (Ctrl+click or Shift+click in the layer list), then click Group."; return; }
        var name = GroupOps.NewGroupName(_theme.Pages.SelectMany(p => p.Widgets).Select(w => w.Group));
        foreach (var w in picked) w.Group = name;
        var keep = _selected is not null && picked.Contains(_selected) ? _selected : picked[0];
        RefreshLayers();
        SelectWidget(keep);
        _status.Text = $"Grouped {picked.Count} widgets as \"{name}\". Drag to move them together, drag the outline handles to resize them together.";
    }

    /// <summary>Breaks the selected widget's group (or the whole selection) back into separate widgets.</summary>
    private void UngroupSelected()
    {
        var targets = _layers.Selection.SelectedIndexes.Select(i => _theme.Widgets[_theme.Widgets.Count - 1 - i]).ToList();
        if (targets.Count == 0 && _selected is not null) targets.Add(_selected);
        var groups = targets.Select(w => w.Group).Where(g => !string.IsNullOrEmpty(g)).ToHashSet();
        if (groups.Count == 0) { _status.Text = "That widget is not in a group."; return; }
        var affected = _theme.Widgets.Where(w => groups.Contains(w.Group)).ToList();
        foreach (var w in affected) w.Group = "";
        var keep = _selected;
        RefreshLayers();
        SelectWidget(keep);
        _status.Text = $"Ungrouped {affected.Count} widgets. They now move and resize on their own.";
    }

    private void DeleteSelected()
    {
        if (_selected is null) return;
        var i = _theme.Widgets.IndexOf(_selected);
        _theme.Widgets.Remove(_selected);
        RefreshLayers();
        SelectWidget(_theme.Widgets.Count == 0 ? null : _theme.Widgets[Math.Min(i, _theme.Widgets.Count - 1)]);
    }

    private void DuplicateSelected()
    {
        if (_selected is null) return;
        var c = _selected.Clone(); c.X += 10; c.Y += 10;
        _theme.Widgets.Insert(_theme.Widgets.IndexOf(_selected) + 1, c);
        RefreshLayers();
        SelectWidget(c);
    }

    /// <summary>dir +1 = bring forward, -1 = send backward.</summary>
    private void MoveSelected(int dir)
    {
        if (_selected is null) return;
        var i = _theme.Widgets.IndexOf(_selected);
        var j = i + dir;
        if (j < 0 || j >= _theme.Widgets.Count) return;
        _theme.Widgets.Move(i, j);
        RefreshLayers();
    }

    private void RefreshLayers()
    {
        _syncingLayers = true;
        // shown front-to-back so the top of the list is the top of the picture
        _layers.ItemsSource = _theme.Widgets.Reverse().Select(w =>
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Opacity = w.Visible ? 1 : 0.4 };
            var badge = Badge(w.Kind); badge.Margin = new Thickness(0, 0, 10, 0);
            var name = new TextBlock { Text = w.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            var kind = new TextBlock { Text = w.Visible ? w.Kind.ToString() : "hidden", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#8C96A8") };
            Grid.SetColumn(kind, 2);
            row.Children.Add(badge); row.Children.Add(name); row.Children.Add(kind);
            var li = new ListBoxItem { Content = row };
            if (!string.IsNullOrEmpty(w.Group)) ToolTip.SetTip(li, "Group: " + w.Group + " (moves together; hold Alt to move one)");
            return li;
        }).ToList();
        _layers.SelectedIndex = _selected is null ? -1 : _theme.Widgets.Count - 1 - _theme.Widgets.IndexOf(_selected);
        _syncingLayers = false;
    }

    private void SelectWidget(Widget? w, bool fromList = false)
    {
        _selected = w;
        if (!fromList) RefreshLayers();
        _props.Show(_theme, w);
    }

    // ---- preview interaction ------------------------------------------------------------------------------------

    private Point ToTheme(Point p) => new(p.X / _scale, p.Y / _scale);

    /// <summary>The zoom that makes the theme fill the preview area (never smaller than 20% or bigger than 400%).</summary>
    private double DesiredScale()
    {
        if (_zoom != "Fit")
            return double.TryParse(_zoom.TrimEnd('%'), out var pct) ? pct / 100.0 : 1.0;
        var b = _stage.Bounds;
        if (b.Width < 100 || b.Height < 100) return _scale;
        return Math.Round(Math.Clamp(Math.Min((b.Width - 80) / _theme.Width, (b.Height - 120) / _theme.Height), 0.2, 4.0), 2);
    }

    private void OnPreviewPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_preview).Properties.IsLeftButtonPressed) return;
        Focus();
        var p = ToTheme(e.GetPosition(_preview));

        // resize handles of the selected widget take priority over everything underneath
        if (_selected is not null)
        {
            var (box, free, members) = SelectionBox();
            var h = Handles.HitTest(box, free, (float)p.X, (float)p.Y, ThemeRenderer.HandleSize, 3);
            if (h != Handle.None)
            {
                _handle = h; _startRect = box; _startFont = _selected.FontSize;
                _groupScaler = members.Count > 1 ? new GroupOps.Scaler(members, box) : null;
                e.Pointer.Capture(_preview);
                return;
            }
        }

        var hit = _theme.Widgets.Reverse().FirstOrDefault(w => w.Visible && w.LastBounds.Contains((float)p.X, (float)p.Y));
        if (hit is null) { SelectWidget(null); return; }
        if (!ReferenceEquals(hit, _selected)) SelectWidget(hit);
        _dragging = true;
        _dragOffset = new Point(p.X - hit.X, p.Y - hit.Y);
        // widgets of the same group (a premade widget) move together; hold Alt to move just this one
        var moveGroup = !e.KeyModifiers.HasFlag(KeyModifiers.Alt) && !string.IsNullOrEmpty(hit.Group);
        _dragMembers = (moveGroup ? _theme.Widgets.Where(w => w.Group == hit.Group) : new[] { hit }).Select(w => (w, w.X, w.Y)).ToList();
        e.Pointer.Capture(_preview);
    }

    private void OnPreviewMoved(object? sender, PointerEventArgs e)
    {
        var p = ToTheme(e.GetPosition(_preview));
        if (_handle != Handle.None && _selected is not null) { ResizeTo(p, e.KeyModifiers.HasFlag(KeyModifiers.Shift)); return; }
        if (!_dragging || _selected is null) { UpdateCursor(p); return; }
        double nx = Math.Round(p.X - _dragOffset.X), ny = Math.Round(p.Y - _dragOffset.Y);
        var head = _dragMembers.FirstOrDefault(m => ReferenceEquals(m.w, _selected));
        if (head.w is null) { _selected.X = nx; _selected.Y = ny; return; }
        double dx = nx - head.x, dy = ny - head.y;
        foreach (var m in _dragMembers) { m.w.X = m.x + dx; m.w.Y = m.y + dy; }
    }

    /// <summary>Moves the selected widget, and the rest of its group unless <paramref name="single"/> (Alt) is held.</summary>
    private void Nudge(double dx, double dy, bool single)
    {
        if (_selected is null) return;
        var members = !single && !string.IsNullOrEmpty(_selected.Group) ? _theme.Widgets.Where(w => w.Group == _selected.Group).ToList() : new List<Widget> { _selected };
        foreach (var w in members) { w.X += dx; w.Y += dy; }
    }

    private void UpdateCursor(Point p)
    {
        Handle h = Handle.None;
        if (_selected is not null)
        {
            var (box, free, _) = SelectionBox();
            h = Handles.HitTest(box, free, (float)p.X, (float)p.Y, ThemeRenderer.HandleSize, 3);
        }
        var type = h switch
        {
            Handle.TopLeft => StandardCursorType.TopLeftCorner,
            Handle.BottomRight => StandardCursorType.BottomRightCorner,
            Handle.TopRight => StandardCursorType.TopRightCorner,
            Handle.BottomLeft => StandardCursorType.BottomLeftCorner,
            Handle.Left or Handle.Right => StandardCursorType.SizeWestEast,
            Handle.Top or Handle.Bottom => StandardCursorType.SizeNorthSouth,
            _ => StandardCursorType.Arrow,
        };
        if (!_cursors.TryGetValue(type, out var c)) _cursors[type] = c = new Cursor(type);
        _preview.Cursor = c;
    }

    /// <summary>Bars, images and panels resize by edges; text widgets scale their font from the corners. Shift keeps the aspect ratio.</summary>
    /// <summary>The selected widget, and the box its resize handles sit on: the whole group's outline when it belongs to one.</summary>
    private (SKRect box, bool free, List<Widget> members) SelectionBox()
    {
        var members = GroupOps.Members(_theme.Widgets, _selected!);
        return members.Count > 1
            ? (GroupOps.Union(members), true, members)
            : (_selected!.LastBounds, Handles.IsFree(_selected), members);
    }

    private void ResizeTo(Point p, bool keepAspect)
    {
        var w = _selected!;
        double px = Math.Round(p.X), py = Math.Round(p.Y);
        var r = _startRect;
        bool left = _handle is Handle.TopLeft or Handle.Left or Handle.BottomLeft;
        bool top = _handle is Handle.TopLeft or Handle.Top or Handle.TopRight;

        if (_groupScaler is not null)
        {
            _groupScaler.Apply(GroupOps.DragRect(r, _handle, px, py, keepAspect, 8));
            return;
        }

        if (Handles.IsFree(w))
        {
            var d = GroupOps.DragRect(r, _handle, px, py, keepAspect);
            w.X = d.Left; w.Y = d.Top; w.Width = d.Width; w.Height = d.Height;
            return;
        }

        // text: the corner opposite the handle stays put and the font scales
        double ox = left ? r.Right : r.Left, oy = top ? r.Bottom : r.Top;
        double sw = Math.Max(r.Width, 1), sh = Math.Max(r.Height, 1);
        double scale = Math.Max(Math.Abs(px - ox) / sw, Math.Abs(py - oy) / sh);
        double font = Math.Clamp(Math.Round(_startFont * scale), 4, 400);
        double real = font / _startFont;
        double newW = sw * real, newH = sh * real;
        double newLeft = left ? ox - newW : ox, newTop = top ? oy - newH : oy;
        w.FontSize = font;
        w.Y = Math.Round(newTop);
        w.X = Math.Round(w.Align switch { TextAlign.Right => newLeft + newW, TextAlign.Center => newLeft + newW / 2, _ => newLeft });
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox or NumericUpDown or ComboBox) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (e.Key == Key.Z && !shift) { DoUndo(); e.Handled = true; return; }
            if (e.Key == Key.Y || (e.Key == Key.Z && shift)) { DoRedo(); e.Handled = true; return; }
        }
        if (_selected is null) return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left: Nudge(-step, 0, e.KeyModifiers.HasFlag(KeyModifiers.Alt)); break;
            case Key.Right: Nudge(step, 0, e.KeyModifiers.HasFlag(KeyModifiers.Alt)); break;
            case Key.Up: Nudge(0, -step, e.KeyModifiers.HasFlag(KeyModifiers.Alt)); break;
            case Key.Down: Nudge(0, step, e.KeyModifiers.HasFlag(KeyModifiers.Alt)); break;
            case Key.Delete: DeleteSelected(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---- rendering / display ------------------------------------------------------------------------------------

    private unsafe void RenderPreview()
    {
        try
        {
            bool resized = _bmp is null || _bmp.Width != _theme.Width || _bmp.Height != _theme.Height;
            if (resized)
            {
                _canvas?.Dispose(); _bmp?.Dispose();
                _bmp = new SKBitmap(new SKImageInfo(_theme.Width, _theme.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                _canvas = new SKCanvas(_bmp);
                _wb = new WriteableBitmap(new PixelSize(_theme.Width, _theme.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                _preview.Source = _wb;
            }
            var desired = DesiredScale();
            if (resized || Math.Abs(desired - _scale) > 0.004)
            {
                _scale = desired;
                _preview.Width = _theme.Width * _scale;
                _preview.Height = _theme.Height * _scale;
                _previewCaption.Text = $"{_theme.Width} × {_theme.Height} px  ·  {_scale * 100:0}%  ·  click to select  ·  drag to move  ·  arrow keys to nudge";
                if (_screenCombo.ItemsSource is not null && !_syncingPageBar && resized) RefreshPageBar();   // keeps the "Custom" label right
            }
            _renderer.Render(_canvas!, _theme, _hub, _selected);
            using var fb = _wb!.Lock();
            var src = (byte*)_bmp.GetPixels();
            var dst = (byte*)fb.Address;
            var rowBytes = Math.Min(_bmp.RowBytes, fb.RowBytes);
            for (int y = 0; y < _bmp.Height; y++)
                Buffer.MemoryCopy(src + (long)y * _bmp.RowBytes, dst + (long)y * fb.RowBytes, rowBytes, rowBytes);
            _preview.InvalidateVisual();
        }
        catch (InvalidOperationException) { /* collection changed mid-render; next tick */ }
    }

    private void SlowTick()
    {
        _sensors.Refresh(_hub.Snapshot);
        _sensorText.Text = _hub.Status;
        _sensorDot.Fill = _hub.IsElevated ? Brush("#34D399") : Brush("#FF9F43");
        _helperBtn.IsVisible = !_hub.IsElevated && SensorHub.FindHostExe() is not null;

        // video widgets need FFmpeg; offer a one-click install when one is present and it is missing
        bool needFfmpeg = Ffmpeg.Find() is null && _theme.Widgets.Any(w => w.Kind == WidgetKind.Image && Ffmpeg.IsVideo(w.ImagePath));
        _ffmpegBtn.IsVisible = needFfmpeg || _installingFfmpeg;
        _ffmpegBtn.IsEnabled = !_installingFfmpeg;
        _ffmpegBtn.Content = _installingFfmpeg ? "Installing FFmpeg..." : "Install FFmpeg for video";

        if (_pusher is { IsAlive: false }) RestartDeadPusher();
        if (_pusher is { } p)
        {
            _displayDot.Fill = p.LastError is not null ? Brush("#FF6B6B") : p.LastRenderError is not null ? Brush("#FF9F43") : Brush("#34D399");
            _displayText.Text = p.LastError is not null ? "Display error" : p.LastRenderError is not null ? "Theme error" : $"Display {p.Fps:0.0} fps";
            if (p.LastError is { } e2) _status.Text = "Display error: " + e2;
            else if (p.LastRenderError is { } re) _status.Text = "Could not draw the theme: " + re;   // USB is fine; reopening would not help
        }
        else if (_liveToRunner)
        {
            if (!RunnerClient.IsRunning) { _send.IsChecked = false; _status.Text = "The background runner stopped, so live sending ended."; }
            else { _displayDot.Fill = Brush("#34D399"); _displayText.Text = "Display via runner (live)"; }
        }
        else if (RunnerClient.IsRunning) { _displayDot.Fill = Brush("#4F8CFF"); _displayText.Text = "Runner owns display"; }
        else { _displayDot.Fill = Brushes.Gray; _displayText.Text = "Display idle"; }
    }

    private async Task InstallFfmpegAsync()
    {
        if (_installingFfmpeg) return;
        _installingFfmpeg = true;
        _status.Text = "Installing FFmpeg for all users via winget (Gyan.FFmpeg); Windows asks for permission. This can take a minute.";
        try
        {
            // machine scope: the elevated background runner only starts an ffmpeg.exe that normal users cannot change
            using var proc = Process.Start(SetupChecks.WingetInstallStartInfo(SetupChecks.FfmpegId, machineScope: true))!;
            await proc.WaitForExitAsync();
            Ffmpeg.Reset();
            _renderer.ClearImageCache();
            var forRunner = await Task.Run(Ffmpeg.FindForElevated); // reads folder permissions: keep it off the UI thread
            _status.Text = SetupChecks.FfmpegInstallOutcome(forRunner, Ffmpeg.Find(), proc.ExitCode);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == SetupChecks.UacCancelled) { _status.Text = "FFmpeg install cancelled: Windows did not get permission."; }
        catch (Exception ex) { _status.Text = "FFmpeg install failed: " + ex.Message; }
        finally { _installingFfmpeg = false; }
    }

    // ---- live streaming to the background runner (when it is active, it owns the display) ------------------------

    private bool _liveToRunner;
    private readonly LiveSendGate _liveGate = new();   // one send at a time, newest snapshot, retried until the runner says OK
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private void StartLive()
    {
        _liveToRunner = true; _liveGate.Reset();
        _liveTimer.Tick -= OnLiveTick; _liveTimer.Tick += OnLiveTick;
        _liveTimer.Start();
        var b = _settings.Brightness;
        var pg = (DesiredPin ?? -1) + 1;
        Task.Run(() => { RunnerClient.Send(ControlCommand.Resume); RunnerClient.Send(ControlCommand.Brightness(b)); RunnerClient.Send(ControlCommand.Page(pg)); });
        PushLive();
        _status.Text = LiveSendGate.ShowingText;
    }

    private void StopLive()
    {
        _liveTimer.Stop();
        if (!_liveToRunner) return;
        _liveToRunner = false;
        Task.Run(() => RunnerClient.Send(ControlCommand.LiveClear));
    }

    private void OnLiveTick(object? s, EventArgs e) => PushLive();

    /// <summary>Turns the display black right now, whoever is driving it.</summary>
    private void TurnDisplayOff()
    {
        bool runner = RunnerClient.IsRunning;
        StopLive();
        _display.Release(blank: true, DisplayPusher.StopTimeout);   // blanks the screen as it closes
        _send.IsChecked = false;
        if (runner)
        {
            Task.Run(() => RunnerClient.Send(ControlCommand.Pause));   // the runner blanks it and stops drawing
            _status.Text = "Display off (the background runner is paused). Click Send to display to turn it back on.";
        }
        else if (_display.StillClosing() is { } busy)
        {
            _status.Text = busy;          // the old loop blanks and closes it when its stuck transfer gives up
        }
        else
        {
            Task.Run(Ax206Display.TryBlank).ContinueWith(t => Dispatcher.UIThread.Post(() =>
                _status.Text = t.Result is { } err ? "Could not turn the display off: " + err : "Display off. Click Send to display to turn it back on."));
        }
    }

    /// <summary>Sends the current theme to the runner whenever it changed, or again after a failed send (see <see cref="LiveSendGate"/>).</summary>
    private void PushLive()
    {
        if (!_liveToRunner) return;
        string snap;
        try { snap = _theme.ToSnapshot(); } catch { return; }
        if (_liveGate.TryBegin(snap) is not { } send) return;
        var folder = _theme.BaseFolder;
        Task.Run(() => RunnerClient.SendLive(snap, folder)).ContinueWith(t =>
        {
            var reply = t.IsCompletedSuccessfully ? t.Result : null;
            Dispatcher.UIThread.Post(() =>
            {
                if (_liveGate.Complete(send, reply) is { } text && _liveToRunner) _status.Text = text;
            });
        });
    }

    /// <summary>
    /// The pusher's loop ended without being stopped (it should not, but it must never freeze the display silently): reopen it,
    /// through the same power and "still closing" checks as any other open. While Windows sleeps it stays closed and the wake-up
    /// path reopens it; after a session end it stays closed.
    /// </summary>
    private void RestartDeadPusher()
    {
        switch (_display.RestartIfDead(out var why, out var error))
        {
            case PusherSlot.RestartResult.Reopened:
                _status.Text = "The display stopped (" + why + "); reopened it.";
                break;
            case PusherSlot.RestartResult.NotNow:
                if (_display.Power.Suspended) _resumeAfterSleep = true;
                break;
            case PusherSlot.RestartResult.Failed:
                _send.IsChecked = false;   // runs ToggleSend(false), which sets its own status; ours goes after it
                _status.Text = "The display stopped (" + why + ") and could not be reopened: " + error;
                break;
        }
    }

    private DisplayPusher OpenPusher() => new(() => _theme, _hub, _settings.Brightness) { PinnedPage = DesiredPin };

    /// <summary>Opens the display; on failure unticks Send to display and says why.</summary>
    private bool StartSending()
    {
        if (_display.TryOpen(out var error)) { _status.Text = "Sending to the display..."; return true; }
        bool deviceProblem = _display.PowerReason is null && _display.StillClosing() is null;
        _send.IsChecked = false;   // runs ToggleSend(false), which sets its own status; ours goes after it
        _status.Text = error + (deviceProblem ? "  (No display? Use Virtual display to preview and demo your theme on screen.)" : "");
        return false;
    }

    private void ToggleSend(bool on)
    {
        _display.Release(blank: true, DisplayPusher.StopTimeout);
        StopLive();
        if (!on) { _status.Text = "Stopped sending."; return; }
        if (RunnerClient.IsRunning) { StartLive(); return; }
        StartSending();
    }

    // ---- shutdown, sign-out, sleep and wake while the editor itself drives the display ------------------------------

    private readonly DisplayPowerPolicy _power = new();
    private SessionPowerWatcher? _powerWatcher;
    private volatile bool _resumeAfterSleep;

    /// <summary>The blank started while the window closed. <see cref="Program"/> waits for it (bounded) before the process exits.</summary>
    internal static Task? PendingExitBlank { get; private set; }

    internal static void WaitForExitBlank()
    {
        try { PendingExitBlank?.Wait(PowerBlank.SessionEndBudget); } catch { /* best effort */ }
    }

    private void StartPowerWatcher()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { _powerWatcher = new SessionPowerWatcher(OnPowerEvent, log: m => Trace.WriteLine("Glance power: " + m)); }
        catch (Exception ex)
        {
            Trace.WriteLine("Glance power watcher failed: " + ex);
            _status.Text = "Could not watch for shutdown and sleep, so the display will not be turned off then: " + ex.Message;
        }
    }

    /// <summary>
    /// Runs on a watcher thread while Windows waits for the answer, so the blank happens here and never needs the UI thread.
    /// It is bounded (<see cref="PusherSlot.BlankForPowerEvent"/>). For sleep this is the only path; for a shutdown Avalonia
    /// usually closes the window first (see <c>Closing</c>), and this is the backup, for example when an installer closes the app.
    /// </summary>
    private void OnPowerEvent(PowerEvent e)
    {
        var action = _power.Handle(e);   // the policy has its own lock: from now on the slot refuses to open
        if (action is PowerAction.BlankAndStop or PowerAction.BlankAndHold)
        {
            var result = _display.BlankForPowerEvent(e, out bool hadOne);
            Trace.WriteLine($"Glance power: {e} -> {action}, blank {result}");
            if (action == PowerAction.BlankAndHold)
            {
                _resumeAfterSleep = hadOne;
                if (hadOne) Dispatcher.UIThread.Post(() => _status.Text = "Display off while Windows sleeps.");
            }
        }
        if (e == PowerEvent.SessionEnding)
            Dispatcher.UIThread.Post(() => { try { Close(); } catch { /* already closing */ } }); // an installer asked the app to close
        else if (action == PowerAction.Reopen && _resumeAfterSleep)
        {
            _resumeAfterSleep = false;
            Dispatcher.UIThread.Post(ResumeSendingAfterSleep);
        }
    }

    /// <summary>After wake, reopen the display if the editor was sending to it. USB can take a while to come back (hibernate, hubs).</summary>
    private async void ResumeSendingAfterSleep()
    {
        string? error = null;
        _status.Text = "Waiting for the display after wake...";
        foreach (var delay in ResumeRetry.Delays())
        {
            await Task.Delay(delay);
            if (_send.IsChecked != true || _pusher is not null || _liveToRunner || !_power.MayDrive) return; // the user (or Windows) moved on
            if (_display.TryOpen(out error)) { _status.Text = "Sending to the display..."; return; }
        }
        _send.IsChecked = false;   // runs ToggleSend(false), which sets its own status; ours goes after it
        _status.Text = "Could not reopen the display after waking up: " + error;
    }
}

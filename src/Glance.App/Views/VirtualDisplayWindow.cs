using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Glance.Core.Display;
using Glance.Core.Sensors;
using Glance.Core.Themes;
using SkiaSharp;
using AppTheme = Glance.Core.Themes.Theme;

namespace Glance.App.Views;

/// <summary>
/// A software stand-in for the LCD: shows the theme live (with its page rotation) at the theme's resolution, scaled to fit the window.
/// Useful for demonstrations without the hardware. Space or Right = next page, Left = previous, F11 = full screen, Esc = leave full screen.
/// </summary>
public sealed unsafe class VirtualDisplayWindow : Window
{
    private readonly Func<AppTheme> _theme;
    private readonly ISensorSource _sensors;
    private readonly ThemeRenderer _renderer = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly DispatcherTimer _timer = new();
    private SKBitmap? _bmp;
    private SKCanvas? _canvas;
    private WriteableBitmap? _wb;
    private int _page, _step;
    private long _since;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public VirtualDisplayWindow(Func<AppTheme> theme, ISensorSource sensors)
    {
        _theme = theme; _sensors = sensors;
        Title = "Virtual display";
        Width = 960; Height = 640; MinWidth = 320; MinHeight = 200;
        Background = Brushes.Black;
        Content = _image;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        KeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Space or Key.Right: _step++; break;
                case Key.Left: _step--; break;
                case Key.F11: WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen; break;
                case Key.Escape when WindowState == WindowState.FullScreen: WindowState = WindowState.Normal; break;
                default: return;
            }
            e.Handled = true;
        };
        DoubleTapped += (_, _) => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

        _timer.Interval = TimeSpan.FromMilliseconds(66);
        _timer.Tick += (_, _) => Render();
        _timer.Start();
        Closed += (_, _) => { _timer.Stop(); _renderer.Dispose(); _canvas?.Dispose(); _bmp?.Dispose(); };
        Render();
    }

    private void Render()
    {
        try
        {
            var theme = _theme();
            if (_bmp is null || _bmp.Width != theme.Width || _bmp.Height != theme.Height)
            {
                _canvas?.Dispose(); _bmp?.Dispose();
                _bmp = new SKBitmap(new SKImageInfo(theme.Width, theme.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
                _canvas = new SKCanvas(_bmp);
                _wb = new WriteableBitmap(new PixelSize(theme.Width, theme.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                _image.Source = _wb;
                _timer.Interval = TimeSpan.FromMilliseconds(theme.Width * theme.Height > 1_000_000 ? 100 : 66);   // big screens: fewer frames
            }

            _page = DisplayPusher.NextPageIndex(theme, _page, ref _since, _clock.ElapsedMilliseconds, -1, _step);
            _step = 0;
            var page = theme.Pages[Math.Min(_page, theme.Pages.Count - 1)];
            _renderer.Render(_canvas!, theme, _sensors, null, page);

            using var fb = _wb!.Lock();
            var src = (byte*)_bmp.GetPixels();
            var dst = (byte*)fb.Address;
            var rowBytes = Math.Min(_bmp.RowBytes, fb.RowBytes);
            for (int y = 0; y < _bmp.Height; y++)
                Buffer.MemoryCopy(src + (long)y * _bmp.RowBytes, dst + (long)y * fb.RowBytes, rowBytes, rowBytes);
            _image.InvalidateVisual();

            var pages = theme.Pages.Count > 1 ? $"  ·  page {_page + 1}/{theme.Pages.Count} ({page.Name})" : "";
            Title = $"Virtual display  ·  {theme.Name}  ·  {theme.Width} × {theme.Height}{pages}  ·  Space = next page, F11 = full screen";
        }
        catch (InvalidOperationException) { /* theme edited mid-frame; next tick */ }
    }
}

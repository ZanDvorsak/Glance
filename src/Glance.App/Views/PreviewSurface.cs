using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Glance.Core.Sensors;
using Glance.Core.Themes;
using SkiaSharp;

namespace Glance.App.Views;

/// <summary>A live, read-only rendering of a theme (same renderer as the display) scaled up for the screen.</summary>
public sealed unsafe class PreviewSurface : Image, IDisposable
{
    private readonly Theme _theme;
    private readonly ISensorSource _sensors;
    private readonly ThemeRenderer _renderer = new();
    private readonly SKBitmap _bmp;
    private readonly SKCanvas _canvas;
    private readonly WriteableBitmap _wb;
    private readonly DispatcherTimer _timer;

    public PreviewSurface(Theme theme, ISensorSource sensors, double scale)
    {
        _theme = theme; _sensors = sensors;
        _bmp = new SKBitmap(new SKImageInfo(theme.Width, theme.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        _canvas = new SKCanvas(_bmp);
        _wb = new WriteableBitmap(new PixelSize(theme.Width, theme.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        Source = _wb;
        Stretch = Stretch.Fill;
        Width = theme.Width * scale; Height = theme.Height * scale;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Render();
        _timer.Start();
        Render();
    }

    private void Render()
    {
        try
        {
            _renderer.Render(_canvas, _theme, _sensors);
            using var fb = _wb.Lock();
            var src = (byte*)_bmp.GetPixels();
            var dst = (byte*)fb.Address;
            var rowBytes = Math.Min(_bmp.RowBytes, fb.RowBytes);
            for (int y = 0; y < _bmp.Height; y++)
                Buffer.MemoryCopy(src + (long)y * _bmp.RowBytes, dst + (long)y * fb.RowBytes, rowBytes, rowBytes);
            InvalidateVisual();
        }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        _timer.Stop();
        _renderer.Dispose();
        _canvas.Dispose();
        _bmp.Dispose();
    }
}

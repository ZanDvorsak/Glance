using Glance.Core.Rendering;
using Glance.Core.Sensors;
using Glance.Core.Themes;
using Glance.Core.Usb;
using SkiaSharp;

namespace Glance.Core.Display;

/// <summary>Renders the current theme in the background and streams it to the physical display, rotating through its pages.</summary>
public sealed class DisplayPusher : IDisposable
{
    private readonly Ax206Display _lcd;
    private readonly Func<Theme> _theme;
    private readonly ISensorSource _sensors;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;
    private volatile int _brightness;
    private volatile int _pinned = -1;
    private int _step;
    private volatile int _pageIndex;

    public double Fps { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>The page currently on the display (0-based).</summary>
    public int CurrentPageIndex => _pageIndex;

    /// <summary>When set, the display stays on this page (used while editing); null lets the theme rotate normally.</summary>
    public int? PinnedPage { get => _pinned < 0 ? null : _pinned; set => _pinned = value ?? -1; }

    /// <summary>Opens the display; throws with a readable message if it is missing or busy.</summary>
    public DisplayPusher(Func<Theme> theme, ISensorSource sensors, int brightness)
    {
        _theme = theme; _sensors = sensors; _brightness = brightness;
        _lcd = Ax206Display.Open();
        _lcd.SetBrightness(brightness);
        _task = Task.Run(Loop);
    }

    public void SetBrightness(int level) => _brightness = Math.Clamp(level, 0, 7);

    /// <summary>Jump to the next page now and restart its timer.</summary>
    public void NextPage() => Interlocked.Increment(ref _step);

    /// <summary>Jump to the previous page now and restart its timer.</summary>
    public void PrevPage() => Interlocked.Decrement(ref _step);

    /// <summary>Which page to show at this moment, advancing the rotation timer. Pure logic, shared by the display and the virtual display.</summary>
    public static int NextPageIndex(Theme theme, int current, ref long since, long nowMs, int pinned, int step)
    {
        int n = theme.Pages.Count;
        if (n <= 1) { since = nowMs; return 0; }
        if (pinned >= 0) { since = nowMs; return Math.Min(pinned, n - 1); }
        if (current >= n) current = 0;
        if (step != 0) { since = nowMs; return ((current + step) % n + n) % n; }
        long dwell = (long)(Math.Max(1, theme.Pages[current].DurationSeconds) * 1000);
        if (theme.AutoRotate && nowMs - since >= dwell) { since = nowMs; return (current + 1) % n; }
        return current;
    }

    private void Loop()
    {
        using var renderer = new ThemeRenderer();
        using var bmp = new SKBitmap(new SKImageInfo(_lcd.Width, _lcd.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        var applied = _brightness;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int frames = 0; long mark = 0, pageSince = 0;
        while (!_cts.IsCancellationRequested)
        {
            var frameStart = sw.ElapsedMilliseconds;
            try
            {
                if (_brightness != applied) { applied = _brightness; _lcd.SetBrightness(applied); }
                var theme = _theme();
                _pageIndex = NextPageIndex(theme, _pageIndex, ref pageSince, frameStart, _pinned, Interlocked.Exchange(ref _step, 0));
                var page = theme.Pages[Math.Min(_pageIndex, theme.Pages.Count - 1)];

                // fit the theme into the panel keeping its proportions (black bars if the shapes differ)
                canvas.Clear(SKColors.Black);
                float s = Math.Min((float)_lcd.Width / theme.Width, (float)_lcd.Height / theme.Height);
                canvas.Save();
                canvas.Translate((_lcd.Width - theme.Width * s) / 2, (_lcd.Height - theme.Height * s) / 2);
                canvas.Scale(s, s);
                canvas.ClipRect(new SKRect(0, 0, theme.Width, theme.Height));
                renderer.Render(canvas, theme, _sensors, null, page);
                canvas.Restore();
                _lcd.Blit(0, 0, _lcd.Width, _lcd.Height, FrameConverter.ToRgb565Be(bmp));
                frames++;
                LastError = null;
            }
            catch (IOException ex) { LastError = ex.Message; Thread.Sleep(1000); }
            catch (InvalidOperationException) { /* theme edited mid-frame; try again */ }

            var now = sw.ElapsedMilliseconds;
            if (now - mark >= 2000) { Fps = frames * 1000.0 / (now - mark); frames = 0; mark = now; }
            var wait = 200 - (int)(sw.ElapsedMilliseconds - frameStart); // cap at ~5 fps
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    /// <summary>Stops sending and closes the display, turning it black (the default) so it does not keep a stale frame.</summary>
    public void Dispose() => Stop(blank: true);

    /// <param name="blank">False when the display is about to be reopened straight away (avoids a black flicker).</param>
    public void Stop(bool blank)
    {
        _cts.Cancel();
        try { _task.Wait(2000); } catch { }
        if (blank) _lcd.Blank();
        _lcd.Dispose();
    }
}

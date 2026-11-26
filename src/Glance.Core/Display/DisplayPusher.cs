using Glance.Core.Rendering;
using Glance.Core.Sensors;
using Glance.Core.Themes;
using Glance.Core.Usb;
using SkiaSharp;

namespace Glance.Core.Display;

/// <summary>
/// Renders the current theme in the background and streams it to the physical display, rotating through its pages.
/// While the loop runs, only the loop touches the device: <see cref="Stop"/> asks it to finish, and the loop itself blanks
/// (if asked) and closes the display on its way out, so the USB handle is never closed under a running transfer.
/// </summary>
public sealed class DisplayPusher : IDisposable
{
    private readonly IFrameDisplay _lcd;
    private readonly Func<Theme> _theme;
    private readonly ISensorSource _sensors;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;
    private volatile int _brightness;
    private volatile int _pinned = -1;
    private int _step;
    private volatile int _pageIndex;
    private volatile bool _blankOnStop;
    private volatile bool _faulted;
    private int _closed;

    private const long ForcedRefreshMs = 10_000;
    private const int FrameIntervalMs = 200;   // caps the loop at ~5 fps
    private const int ErrorBackoffMs = 1000;

    /// <summary>
    /// How long <see cref="Stop"/> waits for the loop to finish. Each USB transfer gives up after 3 s. A frame is a command,
    /// five 64 KiB chunks and a status read, but the first failed transfer ends the frame, so a stalled display costs about one
    /// timeout for the frame in flight plus up to two for blanking (black frame, backlight off). 6 transfers x 3 s covers that
    /// with room to spare. Normally the loop finishes in well under a second (the rest of the current frame plus the black frame).
    /// </summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(6 * 3);

    /// <summary>Frames actually sent to the display per second (frames that were identical to the last one are skipped).</summary>
    public double Fps { get; private set; }

    /// <summary>The last problem the loop hit (USB errors, or any unexpected error while drawing); null after a good frame.</summary>
    public string? LastError { get; private set; }

    /// <summary>False once the loop has ended: after <see cref="Stop"/>, or if it failed for good (<see cref="Faulted"/>). Owners restart a pusher that is not alive.</summary>
    public bool IsAlive => !_task.IsCompleted;

    /// <summary>The loop ended on its own because of an error (see <see cref="LastError"/>), without being stopped.</summary>
    public bool Faulted => _faulted;

    /// <summary>Unexpected (non-USB) errors caught by the loop so far. For tests and diagnostics.</summary>
    internal int UnexpectedErrors;

    /// <summary>The page currently on the display (0-based).</summary>
    public int CurrentPageIndex => _pageIndex;

    /// <summary>When set, the display stays on this page (used while editing); null lets the theme rotate normally.</summary>
    public int? PinnedPage { get => _pinned < 0 ? null : _pinned; set => _pinned = value ?? -1; }

    /// <summary>Opens the display; throws with a readable message if it is missing or busy.</summary>
    public DisplayPusher(Func<Theme> theme, ISensorSource sensors, int brightness)
        : this(theme, sensors, brightness, Ax206Display.Open()) { }

    /// <summary>Drives any <see cref="IFrameDisplay"/> (tests pass a fake). Takes ownership of <paramref name="lcd"/>, also when this throws.</summary>
    internal DisplayPusher(Func<Theme> theme, ISensorSource sensors, int brightness, IFrameDisplay lcd)
    {
        _theme = theme; _sensors = sensors; _brightness = brightness;
        _lcd = lcd;
        try { _lcd.SetBrightness(brightness); }
        catch { _lcd.Dispose(); throw; }   // otherwise the claimed USB interface stays busy until the process exits
        _task = Task.Factory.StartNew(Loop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void SetBrightness(int level) => _brightness = Math.Clamp(level, 0, 7);

    /// <summary>Jump to the next page now and restart its timer.</summary>
    public void NextPage() => Interlocked.Increment(ref _step);

    /// <summary>Jump to the previous page now and restart its timer.</summary>
    public void PrevPage() => Interlocked.Decrement(ref _step);

    /// <summary>Which page to show at this moment, advancing the rotation timer. Pure logic, shared by the display and the virtual display.</summary>
    public static int NextPageIndex(Theme theme, int current, ref long since, long nowMs, int pinned, int step)
        => NextPageIndex(theme.Pages, theme.AutoRotate, current, ref since, nowMs, pinned, step);

    /// <summary>The same rotation logic over a fixed list of pages (the loop passes its own copy so the editor cannot change it underneath).</summary>
    public static int NextPageIndex(IReadOnlyList<ThemePage> pages, bool autoRotate, int current, ref long since, long nowMs, int pinned, int step)
    {
        int n = pages.Count;
        if (n <= 1) { since = nowMs; return 0; }
        if (pinned >= 0) { since = nowMs; return Math.Min(pinned, n - 1); }
        if (current >= n || current < 0) current = 0;
        if (step != 0) { since = nowMs; return ((current + step) % n + n) % n; }
        long dwell = (long)(Math.Max(1, pages[current].DurationSeconds) * 1000);
        if (autoRotate && nowMs - since >= dwell) { since = nowMs; return (current + 1) % n; }
        return current;
    }

    /// <summary>
    /// A copy of the theme's pages that the UI thread cannot change while a frame is drawn. The editor edits the same
    /// theme object (undo/redo, deleting pages), so the live list can shrink between reading its count and indexing it.
    /// </summary>
    internal static ThemePage[] StablePages(Theme theme)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var copy = theme.Pages.ToArray();
                // a page removed between reading the count and copying leaves an empty slot at the end
                return Array.IndexOf(copy, null) < 0 ? copy : Array.FindAll(copy, p => p is not null);
            }
            catch (ArgumentException) when (attempt < 3) { /* the list grew while copying; try again */ }
        }
    }

    private void Loop()
    {
        try { Run(); }
        catch (Exception ex)
        {
            // everything inside a frame is caught in Run, so this is setup failing; report it so the owner restarts us
            _faulted = true;
            LastError = Describe(ex);
        }
        finally
        {
            // stopping: the loop owns the device, so it blanks and closes it here, after its last transfer.
            // Faulted without a stop: leave the device for Stop, which closes it once it sees the loop has ended.
            if (_cts.IsCancellationRequested) Close();
        }
    }

    private void Run()
    {
        var token = _cts.Token;
        int width = _lcd.Width, height = _lcd.Height;
        using var renderer = new ThemeRenderer();
        using var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        var applied = _brightness;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int frames = 0; long mark = 0, pageSince = 0, lastSentAt = 0;
        byte[]? lastSent = null;
        while (!token.IsCancellationRequested)
        {
            var frameStart = sw.ElapsedMilliseconds;
            int backoff = 0;
            try
            {
                var wanted = _brightness;
                if (wanted != applied) { _lcd.SetBrightness(wanted); applied = wanted; }
                var theme = _theme();
                var pages = StablePages(theme);
                if (pages.Length > 0)   // only for an instant while the editor swaps pages; keep the last picture
                {
                    _pageIndex = NextPageIndex(pages, theme.AutoRotate, _pageIndex, ref pageSince, frameStart, _pinned, Interlocked.Exchange(ref _step, 0));
                    var page = pages[Math.Clamp(_pageIndex, 0, pages.Length - 1)];

                    // fit the theme into the panel keeping its proportions (black bars if the shapes differ)
                    canvas.Clear(SKColors.Black);
                    float s = Math.Min((float)width / theme.Width, (float)height / theme.Height);
                    canvas.Save();
                    canvas.Translate((width - theme.Width * s) / 2, (height - theme.Height * s) / 2);
                    canvas.Scale(s, s);
                    canvas.ClipRect(new SKRect(0, 0, theme.Width, theme.Height));
                    try { renderer.Render(canvas, theme, _sensors, null, page); }
                    finally { canvas.Restore(); }

                    // A full frame takes ~400 ms over USB (this display only accepts whole frames), so do not send one
                    // when nothing changed. A refresh every few seconds keeps the picture honest if it was ever lost.
                    var rgb = FrameConverter.ToRgb565Be(bmp);
                    bool unchanged = lastSent is not null && rgb.AsSpan().SequenceEqual(lastSent);
                    if (!unchanged || frameStart - lastSentAt > ForcedRefreshMs)
                    {
                        _lcd.Blit(0, 0, width, height, rgb);
                        lastSent = rgb; lastSentAt = frameStart;
                        frames++;
                    }
                }
                LastError = null;
            }
            catch (IOException ex) { LastError = ex.Message; backoff = ErrorBackoffMs; }
            catch (InvalidOperationException ex) when (ex is not ObjectDisposedException) { /* theme edited mid-frame; try again */ }
            catch (Exception ex)
            {
                // anything else (a bad theme value, a renderer bug): report it and keep going rather than freezing the display
                Interlocked.Increment(ref UnexpectedErrors);
                LastError = Describe(ex);
                backoff = ErrorBackoffMs;
            }

            var now = sw.ElapsedMilliseconds;
            if (now - mark >= 2000) { Fps = frames * 1000.0 / (now - mark); frames = 0; mark = now; }
            var wait = Math.Max(backoff, FrameIntervalMs - (int)(sw.ElapsedMilliseconds - frameStart));
            if (wait > 0) token.WaitHandle.WaitOne(wait);   // wakes straight away on Stop
        }
    }

    private static string Describe(Exception ex) => ex is IOException ? ex.Message : $"{ex.Message} ({ex.GetType().Name})";

    /// <summary>Blanks (if asked) and closes the device, once. Only called by the loop on its way out, or by Stop after the loop has ended.</summary>
    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        if (_blankOnStop) { try { _lcd.Blank(); } catch { /* best effort */ } }
        try { _lcd.Dispose(); } catch { /* best effort */ }
    }

    /// <summary>Stops sending and closes the display, turning it black so it does not keep a stale frame.</summary>
    public void Dispose() => Stop(blank: true);

    /// <summary>
    /// Asks the loop to finish and waits for it (at most <see cref="StopTimeout"/>, normally well under a second). The loop
    /// blanks the display if <paramref name="blank"/> is set and then closes it. Safe to call more than once.
    /// </summary>
    /// <param name="blank">False when the display is about to be reopened straight away (avoids a black flicker).</param>
    /// <returns>
    /// True when the display is closed. False if a transfer was still stuck after the timeout: the display is then closed by the
    /// loop as soon as that transfer gives up, never underneath it.
    /// </returns>
    public bool Stop(bool blank) => Stop(blank, StopTimeout);

    internal bool Stop(bool blank, TimeSpan timeout)
    {
        if (blank) _blankOnStop = true;   // set before cancelling, so the loop sees it when it wakes
        _cts.Cancel();
        bool finished;
        try { finished = _task.Wait(timeout); }
        catch (AggregateException) { finished = true; }   // Loop catches everything, but be safe
        if (finished) Close();   // no-op when the loop already closed it; needed when the loop had faulted on its own
        return finished;
    }
}

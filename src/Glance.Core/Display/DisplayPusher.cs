using Glance.Core.Rendering;
using Glance.Core.Sensors;
using Glance.Core.Themes;
using Glance.Core.Usb;
using SkiaSharp;

namespace Glance.Core.Display;

/// <summary>
/// Renders the current theme in the background and streams it to the physical display, rotating through its pages.
/// While the loop runs, only the loop touches the device: <see cref="Stop(bool, TimeSpan)"/> asks it to finish, and the loop
/// itself blanks (if asked) and closes the display on its way out, so the USB handle is never closed under a running transfer.
/// Problems are reported in two places: <see cref="LastError"/> for the display (USB), which owners fix by reopening it, and
/// <see cref="LastRenderError"/> for the theme, which a new USB handle cannot fix.
/// </summary>
public sealed class DisplayPusher : IDisposable
{
    private readonly IFrameDisplay _lcd;
    private readonly Func<Theme> _theme;
    private readonly ISensorSource _sensors;
    private readonly PusherTiming _timing;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;
    private volatile int _brightness;
    private volatile int _pinned = -1;
    private int _step;
    private volatile int _pageIndex;
    private volatile bool _blankOnStop;
    private volatile bool _faulted;
    private volatile bool _deviceFailing;
    private int _closed;
    private int _renderErrors;

    private const long ForcedRefreshMs = 10_000;
    internal const string NoPagesMessage = "The theme has no pages to show.";

    /// <summary>
    /// The default bound for <see cref="Stop(bool)"/>. Each USB transfer gives up after 3 s. A frame is a command, five
    /// 64 KiB chunks and a status read, but the first failed transfer ends the frame, and a display whose last transfer failed
    /// is not blanked, so a stalled display costs about one timeout. A display that stalls only while being blanked costs up to
    /// three (the frame in flight, the black frame, the backlight). 6 transfers x 3 s covers that with room to spare.
    /// </summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(6 * 3);

    /// <summary>
    /// A bound for stops that must be quick, such as Windows going to sleep or shutting down. A working display blanks and
    /// closes in about 0.5-1 s (the rest of the current frame, a black frame, backlight off). A stalled one is not blanked; if a
    /// transfer is still stuck when this runs out, <see cref="Stop(bool, TimeSpan)"/> returns false and the loop closes the
    /// display when the transfer gives up.
    /// </summary>
    public static readonly TimeSpan QuickStopTimeout = TimeSpan.FromSeconds(2);

    /// <summary>True when the frames go to the Glance service, false when this process drives the USB display itself.</summary>
    public bool ViaService => _lcd is Service.RemoteFrameDisplay;

    /// <summary>Frames actually sent to the display per second (frames that were identical to the last one are skipped).</summary>
    public double Fps { get; private set; }

    /// <summary>The last USB problem (a failed or timed-out transfer); null after the next transfer that works. Owners reopen the display when this persists.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// The last problem drawing the theme (a bad value, a renderer error, a theme with no pages); null after the next good
    /// frame. The display keeps its last picture meanwhile. Reopening USB does not help, so owners only show it.
    /// </summary>
    public string? LastRenderError { get; private set; }

    /// <summary>False once the loop has ended: after a stop, or if it failed for good (<see cref="Faulted"/>). Owners restart a pusher that is not alive.</summary>
    public bool IsAlive => !_task.IsCompleted;

    /// <summary>The loop ended on its own because of an error (see <see cref="LastError"/>), without being stopped.</summary>
    public bool Faulted => _faulted;

    /// <summary>
    /// The display has been closed (blanked first if a stop asked for it). After a stop that returned false this becomes true
    /// later, when the stuck transfer gives up; do not open the display again before that, it is still claimed.
    /// </summary>
    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Errors caught while drawing the theme so far. For tests and diagnostics.</summary>
    internal int UnexpectedErrors => Volatile.Read(ref _renderErrors);

    /// <summary>The page currently on the display (0-based).</summary>
    public int CurrentPageIndex => _pageIndex;

    /// <summary>When set, the display stays on this page (used while editing); null lets the theme rotate normally.</summary>
    public int? PinnedPage { get => _pinned < 0 ? null : _pinned; set => _pinned = value ?? -1; }

    /// <summary>Opens the display, through the Glance service when it runs, else over USB directly; throws with a readable message if it is missing or busy.</summary>
    public DisplayPusher(Func<Theme> theme, ISensorSource sensors, int brightness)
        : this(theme, sensors, brightness, Service.DisplayConnector.Open()) { }

    /// <summary>Drives any <see cref="IFrameDisplay"/> (tests pass a fake and short timings). Takes ownership of <paramref name="lcd"/>, also when this throws.</summary>
    internal DisplayPusher(Func<Theme> theme, ISensorSource sensors, int brightness, IFrameDisplay lcd, PusherTiming? timing = null)
    {
        _theme = theme; _sensors = sensors; _brightness = brightness;
        _lcd = lcd; _timing = timing ?? PusherTiming.Default;
        try { _lcd.SetBrightness(brightness); }
        catch { _lcd.Dispose(); throw; }   // otherwise the claimed USB interface stays busy until the process exits
        _task = Task.Factory.StartNew(Loop, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void SetBrightness(int level) => _brightness = Math.Clamp(level, 0, 7);

    /// <summary>Jump to the next page now and restart its timer.</summary>
    public void NextPage() => Interlocked.Increment(ref _step);

    /// <summary>Jump to the previous page now and restart its timer.</summary>
    public void PrevPage() => Interlocked.Decrement(ref _step);

    /// <summary>
    /// The owners' restart rule (runner and editor): reopen at once when the loop has ended, and reopen when USB has kept
    /// failing for longer than <paramref name="grace"/>. Render errors are deliberately not an input: a new USB handle cannot
    /// fix a bad theme, and reopening for one every few seconds would only flood the log.
    /// </summary>
    /// <param name="failingSince">The owner's record of when USB started failing; updated here, cleared when healthy or reopening.</param>
    public static bool ShouldReopen(bool isAlive, string? usbError, ref DateTime? failingSince, DateTime nowUtc, TimeSpan grace)
    {
        if (!isAlive) { failingSince = null; return true; }
        if (usbError is null) { failingSince = null; return false; }
        failingSince ??= nowUtc;
        if (nowUtc - failingSince.Value <= grace) return false;
        failingSince = null;
        return true;
    }

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
    /// A copy of the theme's pages that the UI thread cannot change while this loop draws a frame. The editor edits the same
    /// theme object (undo/redo, deleting pages), so the live list can shrink between reading its count and indexing it.
    /// LIMIT: this protects only this loop. <see cref="Theme"/> itself is not thread-safe: <see cref="Theme.ApplySnapshot"/>
    /// still shrinks and replaces <c>Pages</c> while other threads may read it, so any other background reader of
    /// <c>theme.Pages</c> must copy it the same way (or be given an immutable snapshot, which is the proper fix, still to do).
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
            if (!_cts.IsCancellationRequested) _faulted = true;
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
        int frames = 0, invalidOps = 0; long mark = 0, pageSince = 0, lastSentAt = 0; long? noPagesSince = null;
        byte[]? lastSent = null;
        while (!token.IsCancellationRequested)
        {
            var frameStart = sw.ElapsedMilliseconds;
            int backoff = 0;

            // 1. Draw. Anything that fails here is the theme's problem, not the display's.
            byte[]? frame = null;
            try
            {
                var theme = _theme();
                var pages = StablePages(theme);
                if (pages.Length == 0)
                {
                    // an instant while the editor swaps pages is normal (keep the last picture); longer is a broken theme
                    noPagesSince ??= frameStart;
                    if (frameStart - noPagesSince.Value >= _timing.NoPagesReportMs) LastRenderError = NoPagesMessage;
                }
                else
                {
                    noPagesSince = null;
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
                    frame = FrameConverter.ToRgb565Be(bmp);
                    LastRenderError = null;
                    invalidOps = 0;
                }
            }
            catch (InvalidOperationException ex) when (ex is not ObjectDisposedException && ++invalidOps < _timing.InvalidOperationLimit)
            {
                /* the theme was edited mid-frame; try again. Repeating every frame falls through to the report below. */
            }
            catch (Exception ex)
            {
                // a bad theme value, a renderer bug, a sensor source that throws: report it and keep going
                Interlocked.Increment(ref _renderErrors);
                LastRenderError = Describe(ex);
                backoff = _timing.ErrorBackoffMs;
            }

            // 2. Send. Anything the device throws is a USB problem.
            try
            {
                var wanted = _brightness;
                if (wanted != applied) { _lcd.SetBrightness(wanted); applied = wanted; DeviceWorked(); }

                // A full frame takes ~400 ms over USB (this display only accepts whole frames), so do not send one
                // when nothing changed. A refresh every few seconds keeps the picture honest if it was ever lost.
                if (frame is not null && (lastSent is null || !frame.AsSpan().SequenceEqual(lastSent) || frameStart - lastSentAt > ForcedRefreshMs))
                {
                    _lcd.Blit(0, 0, width, height, frame);
                    lastSent = frame; lastSentAt = frameStart;
                    frames++;
                    DeviceWorked();
                }
            }
            catch (Exception ex)
            {
                LastError = Describe(ex);
                _deviceFailing = true;
                backoff = Math.Max(backoff, _timing.ErrorBackoffMs);
            }

            var now = sw.ElapsedMilliseconds;
            if (now - mark >= 2000) { Fps = frames * 1000.0 / (now - mark); frames = 0; mark = now; }
            var wait = Math.Max(backoff, _timing.FrameIntervalMs - (int)(sw.ElapsedMilliseconds - frameStart));
            if (wait > 0) token.WaitHandle.WaitOne(wait);   // wakes straight away on Stop
        }
    }

    private void DeviceWorked() { LastError = null; _deviceFailing = false; }

    private static string Describe(Exception ex) => ex is IOException ? ex.Message : $"{ex.Message} ({ex.GetType().Name})";

    /// <summary>Blanks (if asked) and closes the device, once. Only called by the loop on its way out, or by Stop after the loop has ended.</summary>
    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        // A display whose last transfer just failed will not take a black frame either; trying would cost a 3 s timeout
        // per transfer, which is what makes shutdown and sleep slow on a stalled display.
        if (_blankOnStop && !_deviceFailing) { try { _lcd.Blank(); } catch { /* best effort */ } }
        try { _lcd.Dispose(); } catch { /* best effort */ }
    }

    /// <summary>Stops sending and closes the display, turning it black so it does not keep a stale frame. Same as <c>Stop(blank: true)</c>.</summary>
    public void Dispose() => Stop(blank: true);

    /// <summary>Stops with the generous default bound, <see cref="StopTimeout"/>. See <see cref="Stop(bool, TimeSpan)"/>.</summary>
    public bool Stop(bool blank) => Stop(blank, StopTimeout);

    /// <summary>
    /// Asks the loop to finish and waits for it, at most <paramref name="timeout"/>. The loop finishes its current transfer,
    /// blanks the display if <paramref name="blank"/> is set (skipped when its last transfer failed: a stalled display cannot
    /// be blanked) and closes it. Normally this takes about 0.5-1 s with blanking. Safe to call more than once and from any
    /// thread except the loop's. Use <see cref="QuickStopTimeout"/> for sleep and shutdown.
    /// </summary>
    /// <param name="blank">False when the display is about to be reopened straight away (avoids a black flicker).</param>
    /// <returns>
    /// True when the display is closed. False if a transfer was still stuck when the timeout ran out: the display stays claimed
    /// until that transfer gives up (at most a few seconds), then the loop closes it, never underneath the transfer. Watch
    /// <see cref="IsClosed"/> before opening the display again.
    /// </returns>
    public bool Stop(bool blank, TimeSpan timeout)
    {
        if (blank) _blankOnStop = true;   // set before cancelling, so the loop sees it when it wakes
        try { _cts.Cancel(); } catch (ObjectDisposedException) { /* already stopped */ }
        bool finished;
        try { finished = _task.Wait(timeout); }
        catch (AggregateException) { finished = true; }   // Loop catches everything, but be safe
        if (!finished) return false;
        Close();   // no-op when the loop already closed it; needed when the loop had faulted on its own
        _cts.Dispose();
        return true;
    }
}

/// <summary>The loop's timings. Production uses <see cref="Default"/>; tests shorten them so they run fast and sample races often.</summary>
/// <param name="FrameIntervalMs">Minimum time per frame (200 ms caps the loop at ~5 fps; USB manages ~2.5 full frames/s anyway).</param>
/// <param name="ErrorBackoffMs">Pause after an error before trying again.</param>
/// <param name="NoPagesReportMs">How long the theme may have no pages before that is reported as an error.</param>
/// <param name="InvalidOperationLimit">InvalidOperationExceptions in a row (a theme edited mid-frame) tolerated silently before they are reported and backed off.</param>
internal sealed record PusherTiming(int FrameIntervalMs = 200, int ErrorBackoffMs = 1000, int NoPagesReportMs = 1000, int InvalidOperationLimit = 10)
{
    public static readonly PusherTiming Default = new();
}

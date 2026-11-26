using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Glance.Core.Control;
using Glance.Core.Display;
using Glance.Core.Sensors;
using Glance.Core.Service;
using Glance.Core.Settings;
using Glance.Core.Themes;


namespace Glance.Runner;

/// <summary>
/// The tray app (normal user rights, one per signed-in user). It draws the saved theme with this user's files and sends the
/// frames to the Glance service (or straight to the display when there is no service), reloads the theme when it changes,
/// and accepts commands from the editor over a pipe only this user can open. Sensors come from the service (or the basic local ones).
/// </summary>
internal sealed class RunnerContext : ApplicationContext
{
    /// <summary>The editor of this Windows session talks to this pipe (see <see cref="ServiceNames.TrayControlPipe"/>).</summary>
    private static readonly string ControlPipeName = ServiceNames.TrayControlPipeForThisSession();

    private readonly object _gate = new();
    private readonly Control _ui = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem = new("Starting...") { Enabled = false };
    private readonly ToolStripMenuItem _pauseItem = new("Pause display");
    private readonly SensorHub _sensors;
    private readonly ToolStripMenuItem _serviceItem = new("Glance service: checking...") { Enabled = false };
    private volatile string _serviceText = "checking...";
    private int _serviceCheckRunning;
    private readonly System.Windows.Forms.Timer _supervisor = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _reloadDebounce = new() { Interval = 700 };
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly DisplayPowerPolicy _power = new();
    private SessionPowerWatcher? _powerWatcher;
    private bool _quitting;
    private volatile bool _blankOwed;     // a power event found the display busy; blank it when the UI thread lets go

    private AppSettings _settings;
    private DateTime? _rereadSettingsAt;  // settings.json was locked when last read: read it again then (review L1)
    private Theme _theme = Theme.CreateDefault(null);
    private TrayControlServer? _control;    // the editor talks to us here
    private DisplayPusher? _pusher;
    private DisplayPusher? _closingPusher;  // stopped, but a stuck transfer still holds the device
    private string? _loggedRenderError;     // each distinct theme error is logged once, not every frame
    private DateTime? _failingSince;
    private string? _lastError;
    private bool _paused;
    private int _brightness;

    private Theme Current => _liveState.Live ?? _theme;

    public RunnerContext()
    {
        _ = _ui.Handle; // force handle creation so other threads can marshal onto the UI thread
        Log("Tray app starting as " + WindowsIdentity.GetCurrent().Name);


        _sensors = new SensorHub();   // the service's feed, or the basic sensors this user can read

        var read = AppSettings.Read(Log);
        _settings = read.Settings;
        NoteSettingsRead(read);
        _brightness = _settings.Brightness;
        _theme = ReadTheme(_settings.LastTheme);

        _pauseItem.Click += (_, _) => SetPaused(!_paused);
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Glance") { Enabled = false });
        menu.Items.Add(_statusItem);
        menu.Items.Add(_serviceItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add("Next page", null, (_, _) => { lock (_gate) _pusher?.NextPage(); });
        menu.Items.Add("Previous page", null, (_, _) => { lock (_gate) _pusher?.PrevPage(); });
        menu.Items.Add("Reload theme", null, (_, _) => { ClearStaleSleep(); Reload(); });
        menu.Items.Add("Open editor", null, (_, _) => OpenEditor());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Quit());
        _tray = new NotifyIcon { Icon = MakeIcon(), Text = "Glance", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenEditor();

        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); Reload(); };
        _supervisor.Tick += (_, _) => Supervise();
        _supervisor.Start();
        Supervise();

        WatchFiles();
        _control = StartControlServer();
        _powerWatcher = StartPowerWatcher();
        _supervisor.Tick += (_, _) => HealStuckSleep();
        _supervisor.Tick += (_, _) => { RereadLockedSettings(); CheckService(); };
        CheckService();
    }

    // ---- theme / settings ---------------------------------------------------------------------------------------

    /// <summary>Reads a theme file (a damaged one falls back to its backup); the starter layout when there is none or it fails.</summary>
    private static Theme ReadTheme(string? p)
    {
        try
        {
            if (p is null || !File.Exists(p)) { Log("No saved theme yet; showing the starter layout"); return Theme.CreateDefault(null); }
            var read = ThemeFile.Load(p, Log);   // logs when it had to use theme.json.bak
            if (!read.FromBackup) Log("Loaded theme " + p);
            return read.Theme;
        }
        catch (Exception ex) { Log("Theme load failed: " + ex.Message); return Theme.CreateDefault(null); }
    }

    /// <summary>A locked settings file is read again in 10 s instead of waiting for the next file change (review L1).</summary>
    private void NoteSettingsRead(SettingsRead read)
    {
        _rereadSettingsAt = read.State == SettingsFileState.Unreadable ? DateTime.UtcNow.AddSeconds(10) : null;
        if (_rereadSettingsAt is not null) Log("Settings could not be read; trying again in 10 s");
    }

    private void RereadLockedSettings()
    {
        if (_rereadSettingsAt is { } at && DateTime.UtcNow >= at) Reload();
    }

    /// <summary>Re-read settings and the theme, then restart the display so changed images are picked up.</summary>
    private void Reload()
    {
        // read the files before taking the lock: a busy file can take up to a second, and a power event must not wait behind it (review L3)
        var read = AppSettings.Read(Log);
        // a settings file that cannot be read right now keeps the settings already in use (never the defaults)
        var settings = read.State == SettingsFileState.Unreadable ? _settings : read.Settings;
        var theme = ReadTheme(settings.LastTheme);
        lock (_gate)
        {
            _settings = settings;
            _brightness = _settings.Brightness;
            _theme = theme;
            RewatchThemeFolder();
            DisposePusher(blank: false);
        }
        NoteSettingsRead(read);
        Supervise();
    }

    private void WatchFiles()
    {
        Directory.CreateDirectory(AppSettings.Folder);
        AddWatcher(AppSettings.Folder, includeSub: true);
        RewatchThemeFolder();
    }

    private string? _extraWatched;
    private void RewatchThemeFolder()
    {
        var dir = _settings.LastTheme is { } p ? Path.GetDirectoryName(Path.GetFullPath(p)) : null;
        if (dir is null || dir.StartsWith(AppSettings.Folder, StringComparison.OrdinalIgnoreCase) || dir == _extraWatched || !Directory.Exists(dir)) return;
        _extraWatched = dir;
        AddWatcher(dir, includeSub: false);
    }

    private void AddWatcher(string dir, bool includeSub)
    {
        try
        {
            var w = new FileSystemWatcher(dir, "*.json") { IncludeSubdirectories = includeSub, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size, EnableRaisingEvents = true };
            FileSystemEventHandler h = (_, _) => _ui.BeginInvoke(() => { _reloadDebounce.Stop(); _reloadDebounce.Start(); });
            w.Changed += h; w.Created += h; w.Renamed += (_, _) => h(null!, null!);
            _watchers.Add(w);
        }
        catch (Exception ex) { Log("Watch failed for " + dir + ": " + ex.Message); }
    }

    // ---- display ------------------------------------------------------------------------------------------------

    /// <summary>Opens the display when it is available, and reopens it if it keeps failing (unplugged, replugged, taken by another program).</summary>
    private void Supervise()
    {
        lock (_gate)
        {
            ExpireLive();   // M5: an editor that went away without LIVE_CLEAR does not leave its unsaved theme behind
            // checked before the dead-loop restart below: a dead pusher is never replaced while asleep or after a session end (M2)
            if (!_power.MayDrive) { PayOwedBlank(); DisposePusher(blank: true, DisplayPusher.QuickStopTimeout); SetStatus(_power.Ended ? "Display off: Windows is ending the session" : "Display off while Windows sleeps"); return; }
            if (_paused) { DisposePusher(); SetStatus("Display paused"); return; }
            if (_pusher is null)
            {
                if (_closingPusher is { IsClosed: false })
                    _lastError = "the display is finishing its last transfer";   // still claimed; opening now would fail with "busy"
                else
                {
                    _closingPusher = null;
                    try { _pusher = new DisplayPusher(() => Current, _sensors, _brightness) { PinnedPage = _liveState.PinnedPage }; _failingSince = null; _lastError = null; Log("Display opened"); }
                    catch (Exception ex) { _lastError = ex.Message; }
                    // Windows may have started a shutdown or sleep while it opened (the power event does not wait behind this lock)
                    if (_pusher is not null && !_power.MayDrive)
                    {
                        Log("Windows is ending the session or sleeping; closing the display just opened");
                        _blankOwed = false;
                        DisposePusher(blank: true, DisplayPusher.QuickStopTimeout);
                    }
                }
            }
            else
            {
                // reopen only for USB trouble or a dead loop; a theme that fails to draw is shown and logged once instead
                bool alive = _pusher.IsAlive;
                if (DisplayPusher.ShouldReopen(alive, _pusher.LastError, ref _failingSince, DateTime.UtcNow, TimeSpan.FromSeconds(6)))
                {
                    _lastError = _pusher.LastError ?? (_pusher.Faulted ? "the display loop failed" : "the display loop stopped");
                    Log((alive ? "Display kept failing; reopening: " : "Display loop stopped; reopening: ") + _lastError);
                    DisposePusher(blank: false);
                }
                else if (_pusher.LastRenderError is { } renderError && renderError != _loggedRenderError)
                {
                    Log("Could not draw the theme: " + renderError);
                    _loggedRenderError = renderError;
                }
            }

            SetStatus(_pusher is null
                ? "Waiting for the display: " + (_lastError ?? "not found")
                : _pusher.LastRenderError is { } drawError ? "Could not draw the theme: " + drawError
                : _liveState.Live is not null ? $"Live preview from the editor, {_pusher.Fps:0.0} fps" : $"Showing \"{_theme.Name}\"{(Current.Pages.Count > 1 ? $" (page {_pusher.CurrentPageIndex + 1}/{Current.Pages.Count})" : "")}, {_pusher.Fps:0.0} fps");
        }
    }

    /// <param name="blank">Turn the screen black as it closes. Not wanted when it is reopened immediately (reload, error recovery).</param>
    /// <param name="timeout">How long to wait for the loop; the default is <see cref="DisplayPusher.StopTimeout"/>.</param>
    private void DisposePusher(bool blank = true, TimeSpan? timeout = null)
    {
        if (_pusher is { } p && !p.Stop(blank, timeout ?? DisplayPusher.StopTimeout)) RememberClosing(p);
        _pusher = null; _failingSince = null;
    }

    /// <summary>A transfer is still stuck; the loop closes the display when it gives up. Supervise waits for that before reopening.</summary>
    private void RememberClosing(DisplayPusher p)
    {
        _closingPusher = p;
        Log("The display did not stop in time; it will close when its last transfer gives up");
    }

    // ---- shutdown, sign-out, sleep and wake ---------------------------------------------------------------------

    private SessionPowerWatcher? StartPowerWatcher()
    {
        try { return new SessionPowerWatcher(OnPowerEvent, "Turning off the Glance display", Log); }
        catch (Exception ex) { Log("Could not watch for shutdown and sleep: " + ex.Message); return null; }
    }

    /// <summary>
    /// Runs on a watcher thread while Windows waits for the answer: at a session end the process is killed soon after, so the
    /// display is blanked here, not later on the UI thread. It never waits on the UI thread (BeginInvoke only), and the blank is
    /// bounded: if the UI thread holds <see cref="_gate"/> for long (opening or reloading the display), it gives up and leaves
    /// the blank to the UI thread, which sees the policy and pays the owed blank when it lets go.
    /// </summary>
    private void OnPowerEvent(PowerEvent e)
    {
        var action = _power.Handle(e);       // the policy has its own lock: from now on Supervise will not open the display
        Log($"Windows {e}: {action}");      // before blanking, in case Windows ends the process during it
        if (action is PowerAction.BlankAndStop or PowerAction.BlankAndHold)
        {
            // the display loop's own bounded stop; if a transfer is stuck, Supervise waits for the loop to let go before reopening
            var result = PowerBlank.Run(_gate, TakePusher, PowerBlank.OwnerBudgetFor(e), RememberClosing);   // a reference write; Supervise reads it under _gate
            if (result == PowerBlank.Result.Busy) _blankOwed = true;
            Log($"Windows {e}: blank {result}");
        }
        if (e == PowerEvent.SessionEnding) _ui.BeginInvoke(Quit);   // always, even while asleep: also an installer closing the app (Restart Manager)
        else if (action is PowerAction.BlankAndHold or PowerAction.Reopen) _ui.BeginInvoke(Supervise); // status now; after wake it reopens (and keeps retrying)
    }

    /// <summary>Runs under <see cref="_gate"/>: detaches the pusher so it can be blanked outside the lock.</summary>
    private DisplayPusher? TakePusher()
    {
        var p = _pusher;
        _pusher = null; _failingSince = null;
        return p;
    }

    /// <summary>Under <see cref="_gate"/>: a power event found the display busy, so blank it now that the lock is free.</summary>
    private void PayOwedBlank()
    {
        if (!_blankOwed) return;
        _blankOwed = false;
        if (_pusher is not null) DisposePusher(blank: true, DisplayPusher.QuickStopTimeout);
        else if (_closingPusher is { IsClosed: false } closing)
        {
            closing.Stop(blank: true, TimeSpan.Zero);   // ask its loop to blank as well when it lets go (it may have been stopped without)
            Log("The display is still finishing its last transfer; its loop blanks it when it lets go");
        }
        else if (DisplayConnector.TryBlank() is { } err) Log("Could not blank the display after a power event: " + err);
    }

    /// <summary>Supervisor tick: if Windows never sent the wake-up notice, do not stay dark for good.</summary>
    private void HealStuckSleep()
    {
        if (_power.NoteAwakeTick() != PowerAction.Reopen) return;
        Log("No wake-up notice from Windows after 30 s awake; reopening the display");
        Supervise();
    }

    /// <summary>Supervisor tick: asks the Glance service how it is, off the UI thread (a missing service costs a 0.5 s connect timeout).</summary>
    private void CheckService()
    {
        if (Interlocked.Exchange(ref _serviceCheckRunning, 1) != 0) return;
        Task.Run(() =>
        {
            try
            {
                var status = DisplayConnector.ServiceStatus();
                if (status is not null) _ui.BeginInvoke(MoveToServiceIfOnUsb);
                _serviceText = status is { } s ? "running, " + s
                    : ServiceControl.Query() switch
                    {
                        ServiceState.NotInstalled => "not installed (basic sensors, display over USB)",
                        ServiceState.Running => "running, but not answering",
                        _ => "not running (basic sensors, display over USB)",
                    };
            }
            catch (Exception ex) { _serviceText = "unknown (" + ex.Message + ")"; }
            finally { Volatile.Write(ref _serviceCheckRunning, 0); }
            try { _ui.BeginInvoke(() => _serviceItem.Text = Trim("Glance service: " + _serviceText)); } catch { /* closing */ }
        });
    }

    private static string Trim(string text) => text.Length > 90 ? text[..90] + "..." : text;

    /// <summary>
    /// L3: the display was opened over USB because the service was not there yet (portable use, or the service started later).
    /// Once the service answers, hand the device to it, so shutdown blanking and AIDA64's release work as designed.
    /// </summary>
    private void MoveToServiceIfOnUsb()
    {
        lock (_gate)
        {
            if (_pusher is null || _pusher.ViaService) return;
            Log("The Glance service is running now; handing the display to it");
            DisposePusher(blank: false);
        }
        Supervise();
    }

    /// <summary>The user asked for the display (Resume, Reload): that also clears a sleep state Windows never ended.</summary>
    private void ClearStaleSleep()
    {
        if (_power.Handle(PowerEvent.Resume) == PowerAction.Reopen) Log("Cleared a sleep state that Windows never ended");
    }

    private void SetPaused(bool paused)
    {
        if (!paused) ClearStaleSleep();
        _paused = paused;
        _pauseItem.Text = paused ? "Resume display" : "Pause display";
        Supervise();
    }

    private void SetStatus(string text)
    {
        _statusItem.Text = text;
        _tray.Text = "Glance: " + (text.Length > 55 ? text[..55] : text); // NotifyIcon.Text is limited to 63 chars
    }

    // ---- control pipe (from the editor) -------------------------------------------------------------------------

    /// <summary>
    /// Under <see cref="_gate"/>: the editor's live theme and page pin. The live theme is leased to the connection that sent it
    /// (review M5), see <see cref="TrayLiveState"/>.
    /// </summary>
    private readonly TrayLiveState _liveState = new();

    private TrayControlServer StartControlServer() => new(ControlPipeName, Execute, OnControlClosed, Log);

    /// <summary>Under <see cref="_gate"/>: hands the page pin to the running display loop.</summary>
    private void ApplyPin()
    {
        if (_pusher is not null) _pusher.PinnedPage = _liveState.PinnedPage;
    }

    /// <summary>A control connection ended; if it held the live theme, show the saved theme again.</summary>
    private void OnControlClosed(long connection)
    {
        bool ended;
        lock (_gate) { ended = _liveState.ConnectionClosed(connection); if (ended) ApplyPin(); }
        if (ended) Log("The editor's live preview ended (its connection closed); showing the saved theme");
    }

    /// <summary>Supervisor tick, under <see cref="_gate"/>: a live theme nobody keeps alive any more ends.</summary>
    private void ExpireLive()
    {
        if (!_liveState.Expire()) return;
        ApplyPin();
        Log("The editor's live preview ended (no word from the editor); showing the saved theme");
    }

    private string Execute(string line, long connection)
    {
        // parsing and the wire format live in Glance.Core (ControlCommand) so the editor and tests share them
        if (!ControlCommand.TryParse(line, out var cmd, out var error)) return "ERR " + error;
        switch (cmd.Verb)
        {
            case ControlVerb.Status:
            {
                string pg;
                lock (_gate)
                {
                    _liveState.Handle(cmd, connection);   // counts as a sign of life from the editor
                    pg = _pusher is null ? "no display" : $"page {_pusher.CurrentPageIndex + 1}/{Current.Pages.Count}, pinned={(_pusher.PinnedPage is { } p ? (p + 1).ToString() : "no")}, autoRotate={Current.AutoRotate}, live={_liveState.Live is not null}";
                }
                return "OK " + _statusItem.Text + " | " + pg;
            }
            case ControlVerb.Reload: _ui.BeginInvoke(Reload); return "OK";
            case ControlVerb.Next: lock (_gate) _pusher?.NextPage(); return "OK";
            case ControlVerb.Prev: lock (_gate) _pusher?.PrevPage(); return "OK";
            case ControlVerb.Pause: _ui.BeginInvoke(() => SetPaused(true)); return "OK";
            case ControlVerb.Resume: _ui.BeginInvoke(() => SetPaused(false)); return "OK";
            case ControlVerb.Exit: _ui.BeginInvoke(Quit); return "OK";
            case ControlVerb.Brightness:
                lock (_gate) { _brightness = Math.Clamp(cmd.Number, 0, 7); _pusher?.SetBrightness(_brightness); }
                return "OK";
            case ControlVerb.Page:        // PAGE <n> pins the display to page n (1-based); PAGE 0 releases it
            case ControlVerb.Live:        // LIVE <base64 folder> <base64 theme json>: show the editor's unsaved theme
            case ControlVerb.LiveKeep:    // the editor is still there
            case ControlVerb.LiveClear:   // back to the saved theme, pin released
                lock (_gate)
                {
                    var reply = _liveState.Handle(cmd, connection) ?? "ERR unknown command";
                    ApplyPin();
                    return reply;
                }
            default: return "ERR unknown command";
        }
    }

    // ---- misc ---------------------------------------------------------------------------------------------------

    private void OpenEditor()
    {
        var exe = FindEditor();
        if (exe is null) { _tray.ShowBalloonTip(3000, "Glance", "Could not find Glance.App.exe next to the tray app.", ToolTipIcon.Warning); return; }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });   // same (normal) rights as this app
    }

    /// <summary>Next to the tray app; in a development build also in the repository's editor output, never elsewhere (L9).</summary>
    private static string? FindEditor() => DevPaths.FindSibling("Glance.App.exe", "Glance.App");

    private void Quit()
    {
        if (_quitting) return;   // a session end and the EXIT command can both ask
        _quitting = true;
        Log("Tray app exiting");
        _control?.Dispose();
        _supervisor.Stop();
        foreach (var w in _watchers) w.Dispose();
        lock (_gate)
        {
            _power.Handle(PowerEvent.Quit);   // nothing may reopen the display from here on
            PayOwedBlank();                   // a session end that found the display busy
            DisposePusher();                  // blanks it unless a session end already took and blanked it
        }
        _powerWatcher?.Dispose();
        _sensors.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }

    private static Icon MakeIcon()
    {
        try { if (Icon.ExtractAssociatedIcon(Application.ExecutablePath) is { } own) return own; } catch { /* fall back to a drawn icon */ }
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(Color.FromArgb(0x4F, 0x8C, 0xFF));
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(1, 1, 12, 12, 180, 90); path.AddArc(19, 1, 12, 12, 270, 90); path.AddArc(19, 19, 12, 12, 0, 90); path.AddArc(1, 19, 12, 12, 90, 90); path.CloseFigure();
            g.FillPath(fill, path);
            using var f = new Font("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Pixel);
            g.DrawString("G", f, Brushes.White, new RectangleF(0, 0, 32, 32), new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "runner.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* logging must never break the runner */ }
    }
}

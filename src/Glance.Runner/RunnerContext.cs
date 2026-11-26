using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Glance.Core.Control;
using Glance.Core.Display;
using Glance.Core.Sensors;
using Glance.Core.Settings;
using Glance.Core.Themes;

namespace Glance.Runner;

/// <summary>
/// Tray application that owns the display: reads sensors (elevated), serves them to the editor,
/// pushes the saved theme to the screen, reloads it when it changes, and accepts commands from the editor.
/// </summary>
internal sealed class RunnerContext : ApplicationContext
{
    public const string ControlPipeName = "Glance.Runner.Control";

    private readonly object _gate = new();
    private readonly Control _ui = new();
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _statusItem = new("Starting...") { Enabled = false };
    private readonly ToolStripMenuItem _pauseItem = new("Pause display");
    private readonly LocalSensorSource _sensors;
    private readonly SensorPipeServer _pipeServer = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Windows.Forms.Timer _supervisor = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _reloadDebounce = new() { Interval = 700 };
    private readonly List<FileSystemWatcher> _watchers = new();

    private AppSettings _settings = AppSettings.Load();
    private Theme _theme = Theme.CreateDefault(null);
    private Theme? _live;                 // unsaved theme streamed from the editor
    private DisplayPusher? _pusher;
    private DateTime? _failingSince;
    private string? _lastError;
    private bool _paused;
    private int? _pinPage;
    private int _brightness;

    private Theme Current => _live ?? _theme;

    public RunnerContext()
    {
        _ = _ui.Handle; // force handle creation so other threads can marshal onto the UI thread
        Log("Runner starting as " + WindowsIdentity.GetCurrent().Name);
        Glance.Core.Media.Ffmpeg.Rejected += Log; // say why a user-writable ffmpeg.exe is not started as administrator

        _sensors = new LocalSensorSource();
        _pipeServer.Start();
        _pipeServer.Publish(_sensors.HardwareSnapshot);
        _sensors.Updated += () => _pipeServer.Publish(_sensors.HardwareSnapshot);

        _brightness = _settings.Brightness;
        LoadTheme();

        _pauseItem.Click += (_, _) => SetPaused(!_paused);
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Glance runner") { Enabled = false });
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add("Next page", null, (_, _) => { lock (_gate) _pusher?.NextPage(); });
        menu.Items.Add("Previous page", null, (_, _) => { lock (_gate) _pusher?.PrevPage(); });
        menu.Items.Add("Reload theme", null, (_, _) => Reload());
        menu.Items.Add("Open editor", null, (_, _) => OpenEditor());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Quit());
        _tray = new NotifyIcon { Icon = MakeIcon(), Text = "Glance runner", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => OpenEditor();

        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); Reload(); };
        _supervisor.Tick += (_, _) => Supervise();
        _supervisor.Start();
        Supervise();

        WatchFiles();
        _ = Task.Run(ControlLoop);
    }

    // ---- theme / settings ---------------------------------------------------------------------------------------

    private void LoadTheme()
    {
        try
        {
            var p = _settings.LastTheme;
            _theme = p is not null && File.Exists(p) ? Theme.Load(p) : Theme.CreateDefault(null);
            Log(p is not null && File.Exists(p) ? "Loaded theme " + p : "No saved theme yet; showing the starter layout");
        }
        catch (Exception ex) { Log("Theme load failed: " + ex.Message); _theme = Theme.CreateDefault(null); }
    }

    /// <summary>Re-read settings and the theme, then restart the display so changed images are picked up.</summary>
    private void Reload()
    {
        lock (_gate)
        {
            _settings = AppSettings.Load();
            _brightness = _settings.Brightness;
            LoadTheme();
            RewatchThemeFolder();
            DisposePusher(blank: false);
        }
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
            if (_paused) { DisposePusher(); SetStatus("Display paused"); return; }
            if (_pusher is null)
            {
                try { _pusher = new DisplayPusher(() => Current, _sensors, _brightness) { PinnedPage = _pinPage }; _failingSince = null; _lastError = null; Log("Display opened"); }
                catch (Exception ex) { _lastError = ex.Message; }
            }
            else if (_pusher.LastError is not null)
            {
                _failingSince ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _failingSince > TimeSpan.FromSeconds(6)) { Log("Display kept failing; reopening: " + _pusher.LastError); DisposePusher(blank: false); }
            }
            else _failingSince = null;

            SetStatus(_pusher is null
                ? "Waiting for the display: " + (_lastError ?? "not found")
                : _live is not null ? $"Live preview from the editor, {_pusher.Fps:0.0} fps" : $"Showing \"{_theme.Name}\"{(Current.Pages.Count > 1 ? $" (page {_pusher.CurrentPageIndex + 1}/{Current.Pages.Count})" : "")}, {_pusher.Fps:0.0} fps");
        }
    }

    /// <param name="blank">Turn the screen black as it closes. Not wanted when it is reopened immediately (reload, error recovery).</param>
    private void DisposePusher(bool blank = true) { _pusher?.Stop(blank); _pusher = null; _failingSince = null; }

    private void SetPaused(bool paused)
    {
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

    private async Task ControlLoop()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var server = NamedPipeServerStreamAcl.Create(ControlPipeName, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                await server.WaitForConnectionAsync(_cts.Token);
                _ = Task.Run(() => Handle(server));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log("Control pipe error: " + ex.Message); try { await Task.Delay(1000, _cts.Token); } catch { return; } }
        }
    }

    private async Task Handle(NamedPipeServerStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            while (await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                string reply;
                try { reply = Execute(line.Trim()); } catch (Exception ex) { reply = "ERR " + ex.Message; }
                await writer.WriteLineAsync(reply);
            }
        }
        catch { /* client left */ }
        finally { pipe.Dispose(); }
    }

    private string Execute(string line)
    {
        // parsing and the wire format live in Glance.Core (ControlCommand) so the editor and tests share them
        if (!ControlCommand.TryParse(line, out var cmd, out var error)) return "ERR " + error;
        switch (cmd.Verb)
        {
            case ControlVerb.Status:
            {
                string pg;
                lock (_gate) pg = _pusher is null ? "no display" : $"page {_pusher.CurrentPageIndex + 1}/{Current.Pages.Count}, pinned={(_pusher.PinnedPage is { } p ? (p + 1).ToString() : "no")}, autoRotate={Current.AutoRotate}, live={_live is not null}";
                return "OK " + _statusItem.Text + " | " + pg;
            }
            case ControlVerb.Reload: _ui.BeginInvoke(Reload); return "OK";
            case ControlVerb.Next: lock (_gate) _pusher?.NextPage(); return "OK";
            case ControlVerb.Prev: lock (_gate) _pusher?.PrevPage(); return "OK";
            case ControlVerb.Page:   // PAGE <n> pins the display to page n (1-based); PAGE 0 releases it
                lock (_gate) { _pinPage = cmd.Number > 0 ? cmd.Number - 1 : null; if (_pusher is not null) _pusher.PinnedPage = _pinPage; }
                return "OK";
            case ControlVerb.Pause: _ui.BeginInvoke(() => SetPaused(true)); return "OK";
            case ControlVerb.Resume: _ui.BeginInvoke(() => SetPaused(false)); return "OK";
            case ControlVerb.Exit: _ui.BeginInvoke(Quit); return "OK";
            case ControlVerb.Brightness:
                lock (_gate) { _brightness = Math.Clamp(cmd.Number, 0, 7); _pusher?.SetBrightness(_brightness); }
                return "OK";
            case ControlVerb.Live:
            {
                // LIVE <base64 folder> <base64 theme json>: show the editor's unsaved theme until LIVE_CLEAR
                var t = new Theme { BaseFolder = cmd.Folder };
                t.ApplySnapshot(cmd.ThemeJson!);
                lock (_gate) _live = t;
                return "OK";
            }
            case ControlVerb.LiveClear: lock (_gate) { _live = null; _pinPage = null; if (_pusher is not null) _pusher.PinnedPage = null; } return "OK";
            default: return "ERR unknown command";
        }
    }

    // ---- misc ---------------------------------------------------------------------------------------------------

    private void OpenEditor()
    {
        var exe = FindEditor();
        if (exe is null) { _tray.ShowBalloonTip(3000, "Glance", "Could not find Glance.App.exe next to the runner.", ToolTipIcon.Warning); return; }
        // explorer starts it with the normal (non-elevated) user token, so the editor does not run as administrator
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = true });
    }

    private static string? FindEditor()
    {
        const string exe = "Glance.App.exe";
        var dir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe);
        for (var d = dir; d is not null; d = Path.GetDirectoryName(d))
        {
            var proj = Path.Combine(d, "Glance.App", "bin");
            if (!Directory.Exists(proj)) continue;
            return Directory.EnumerateFiles(proj, exe, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        return null;
    }

    private void Quit()
    {
        Log("Runner exiting");
        _cts.Cancel();
        _supervisor.Stop();
        foreach (var w in _watchers) w.Dispose();
        lock (_gate) DisposePusher();
        _pipeServer.Dispose();
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

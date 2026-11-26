using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Glance.App.Services;
using Glance.Core.Security;
using Glance.Core.Setup;

namespace Glance.App.Views;

/// <summary>Start, stop and auto-start the background runner that keeps the display updated without the editor.</summary>
public sealed class ServiceWindow : Window
{
    private readonly TextBlock _state = new() { FontSize = 16, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _detail = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#FF9F43")), IsVisible = false };
    private readonly Button _startStop = new();
    private readonly CheckBox _auto = new() { Content = "Start automatically when I sign in to Windows" };
    private readonly Ellipse_ _dot = new();
    private bool _busy, _syncing;
    private TrustVerdict? _trust; // null until the folder check has finished
    private readonly Button _fix = new() { IsVisible = false };
    private bool _fixInstall;

    private sealed class Ellipse_ : Border
    {
        public Ellipse_() { Width = 12; Height = 12; CornerRadius = new CornerRadius(6); VerticalAlignment = VerticalAlignment.Center; }
    }

    public ServiceWindow()
    {
        Title = "Background service";
        Width = 520; Height = 580; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _dot, _state } };
        var info = new TextBlock
        {
            Text = "The runner is a small tray app. It keeps your saved theme on the display and provides CPU and motherboard sensors, " +
                   "so the editor does not need to be open. It runs with administrator rights, which Windows needs for those sensors.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 4),
        };
        var autoNote = new TextBlock
        {
            Text = "Auto-start uses a Windows scheduled task set to run with the highest privileges, so there is no prompt at every sign-in. " +
                   "Creating or removing it asks for permission once. It is only available when Glance is installed in a folder " +
                   "that only administrators can change, such as Program Files.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.6, FontSize = 12,
        };

        _startStop.Click += async (_, _) => await ToggleRunning();
        _fix.Click += async (_, _) => await FixAutostart();
        _auto.IsCheckedChanged += async (_, _) => { if (!_syncing) await ToggleAutostart(); };
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        var stack = new StackPanel { Spacing = 14, Margin = new Thickness(22), Children = { header, _detail, info, _startStop, new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#232A37")) }, _auto, autoNote, _message, _fix, close } };
        Content = stack;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Sync();
        timer.Start();
        Closed += (_, _) => timer.Stop();
        Sync();
        _ = CheckAutostart();
    }

    /// <summary>
    /// Checks, off the UI thread, whether this copy may use auto-start (its folder must be admin-only) and what an existing
    /// task points at (M1). Shows any problem with a button that fixes it.
    /// </summary>
    private async Task CheckAutostart()
    {
        if (SensorHub.FindRunnerExe() is not { } exe) return;
        var (verdict, status) = await Task.Run(() => (Autostart.CheckRunner(exe), Autostart.Status(exe)));
        _trust = verdict;
        _fix.IsVisible = false;

        if (status.State is not (AutostartState.Ok or AutostartState.NotInstalled))
        {
            // An existing task that is unsafe, foreign or for another copy: repair it from a trusted copy, else turn it off.
            // A task for another, trusted copy is fine to leave alone when this copy could not replace it anyway.
            Say(status.Message!);
            _fixInstall = verdict.Trusted;
            _fix.Content = verdict.Trusted ? "Point auto-start at this copy" : "Turn auto-start off";
            _fix.IsVisible = verdict.Trusted || status.State != AutostartState.OtherCopy;
        }
        else if (!verdict.Trusted)
            Say(Autostart.UntrustedMessage(verdict) + (status.State == AutostartState.Ok ? " Auto-start is still turned on from before; turning it off is recommended." : ""));

        // H1: variables that make .NET load code into the elevated runner. The runner logs them too.
        if (status.State != AutostartState.NotInstalled && RunnerEnvironment.Warning(Environment.GetEnvironmentVariables()) is { } env)
            Say((_message.IsVisible ? _message.Text + "\n\n" : "") + env);
        Sync();
    }

    private async Task FixAutostart()
    {
        if (SensorHub.FindRunnerExe() is not { } exe) return;
        _busy = true; _message.IsVisible = false; _fix.IsVisible = false;
        try
        {
            var install = _fixInstall;
            var err = await Task.Run(() => install ? Autostart.Install(exe) : Autostart.Remove());
            if (err is not null) Say(err);
        }
        finally { _busy = false; Sync(); }
        if (!_message.IsVisible) await CheckAutostart();
    }

    private void Sync()
    {
        bool running = RunnerClient.IsRunning;
        _dot.Background = new SolidColorBrush(Color.Parse(running ? "#34D399" : "#6B7280"));
        _state.Text = running ? "Runner is running" : "Runner is not running";
        _detail.Text = running ? (RunnerClient.Send(ControlCommand.Status) is { } s && s.StartsWith("OK ") ? s[3..] : "Running.") : "The display is only updated while the editor is open and sending.";
        _startStop.Content = running ? "Stop the runner" : "Start the runner now";
        _startStop.IsEnabled = !_busy && (running || SensorHub.FindRunnerExe() is not null);

        _syncing = true;
        bool installed = Autostart.IsInstalled();
        try { _auto.IsChecked = installed; } finally { _syncing = false; }
        // An untrusted copy can still turn auto-start off, but not on.
        _auto.IsEnabled = AutostartTask.CheckboxEnabled(SensorHub.FindRunnerExe() is not null, _busy, installed, _trust?.Trusted);
        _fix.IsEnabled = !_busy;
    }

    private async Task ToggleRunning()
    {
        _busy = true; _message.IsVisible = false;
        try
        {
            if (RunnerClient.IsRunning) { RunnerClient.Send(ControlCommand.Exit); await Task.Delay(1200); }
            else if (!RunnerClient.Launch()) Say("Could not start the runner (not found, or the Windows prompt was declined).");
            else await Task.Delay(2500);
        }
        finally { _busy = false; Sync(); }
    }

    private async Task ToggleAutostart()
    {
        _busy = true; _message.IsVisible = false;
        var want = _auto.IsChecked == true;
        try
        {
            var exe = SensorHub.FindRunnerExe();
            var err = exe is null ? "Runner not found." : await Task.Run(() => want ? Autostart.Install(exe) : Autostart.Remove());
            if (err is not null) Say(err);
        }
        finally { _busy = false; Sync(); }
        if (!_message.IsVisible) await CheckAutostart();
    }

    private void Say(string text) { _message.Text = text; _message.IsVisible = true; }
}

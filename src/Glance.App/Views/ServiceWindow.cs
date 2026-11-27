using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Glance.App.Services;
using Glance.Core.Service;
using Glance.Core.Threading;

namespace Glance.App.Views;

/// <summary>
/// The Glance service (Windows service, owns the display and the sensors) and this session's tray app (draws your theme).
/// Starting the service needs no prompt; stopping it asks Windows for permission once. Everything is read off the UI thread.
/// </summary>
public sealed class ServiceWindow : Window
{
    private readonly TextBlock _state = new() { FontSize = 16, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _detail = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly Border _dot = Dot();
    private readonly Button _serviceButton = new();
    private readonly TextBlock _trayState = new() { FontWeight = FontWeight.SemiBold };
    private readonly Border _trayDot = Dot();
    private readonly Button _trayButton = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#FF9F43")), IsVisible = false };
    private readonly Button _removeTask = new() { Content = "Remove the old auto-start task", IsVisible = false };
    private ServiceState _service = ServiceState.Unknown;
    private bool _trayRunning, _busy, _refreshing;

    private static Border Dot() => new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center };

    public ServiceWindow()
    {
        Title = "Background service";
        Width = 540; Height = 600; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var info = new TextBlock
        {
            Text = "The Glance service runs in the background as part of Windows, also before anyone signs in. It owns the display " +
                   "and reads all hardware sensors, including CPU and motherboard. It never opens your themes or files.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75,
        };
        var trayInfo = new TextBlock
        {
            Text = "The tray app draws your saved theme with your own rights and sends it to the service, so the editor does not need " +
                   "to be open. It starts by itself when you sign in; to stop that, turn it off in Task Manager > Startup apps.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.6, FontSize = 12,
        };

        _serviceButton.Click += async (_, _) => await ToggleService();
        _trayButton.Click += async (_, _) => await ToggleTray();
        _removeTask.Click += async (_, _) => await RemoveOldTask();
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Spacing = 14, Margin = new Thickness(22),
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _dot, _state } },
                _detail, info, _serviceButton,
                new Border { Height = 1, Background = new SolidColorBrush(Color.Parse("#232A37")) },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _trayDot, _trayState } },
                trayInfo, _trayButton, _message, _removeTask, close,
            },
        };

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) => await Refresh();
        timer.Start();
        Closed += (_, _) => timer.Stop();
        Show(ServiceState.Unknown, null, null, false);   // "checking..." until Refresh has asked, off the UI thread
        TaskObserver.Observe(Refresh(), ex => Say("Could not read the service state: " + ex.Message));
        TaskObserver.Observe(CheckOldTask(), ex => Say("Could not look for the old sign-in task: " + ex.Message));
    }

    private async Task Refresh()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var (state, status, tray, trayExe) = await Task.Run(() =>
            {
                var s = ServiceControl.Query();
                return (s, s == ServiceState.Running ? DisplayConnector.ServiceStatus() : null, RunnerClient.IsRunning, AppPaths.FindTrayExe() is not null);
            });
            Show(state, status, tray, trayExe);
        }
        finally { _refreshing = false; }
    }

    private void Show(ServiceState service, string? status, bool? trayKnown, bool trayExeFound)
    {
        var trayRunning = trayKnown == true;
        _service = service; _trayRunning = trayRunning;
        _dot.Background = new SolidColorBrush(Color.Parse(service == ServiceState.Running ? "#34D399" : service == ServiceState.NotInstalled ? "#6B7280" : "#FF9F43"));
        _state.Text = service switch
        {
            ServiceState.Running => "Glance service is running",
            ServiceState.Starting => "Glance service is starting...",
            ServiceState.Stopping => "Glance service is stopping...",
            ServiceState.Stopped => "Glance service is stopped",
            ServiceState.NotInstalled => "Glance service is not installed",
            _ => "Glance service: checking...",
        };
        _detail.Text = service switch
        {
            ServiceState.Running => status is null ? "Running." : "Display: " + status,
            ServiceState.NotInstalled => "This copy of Glance was not installed with the installer (portable use). The tray app and the editor " +
                                         "drive the display directly, with the basic sensors only (no CPU temperatures, fans or voltages).",
            ServiceState.Stopped => "Only the basic sensors are available, and the tray app or the editor drive the display directly.",
            _ => "",
        };
        _serviceButton.Content = service is ServiceState.Running or ServiceState.Starting ? "Stop the service (Windows asks for permission)" : "Start the service";
        _serviceButton.IsVisible = service is not (ServiceState.NotInstalled or ServiceState.Unknown);
        _serviceButton.IsEnabled = !_busy && service is ServiceState.Running or ServiceState.Stopped;

        _trayDot.Background = new SolidColorBrush(Color.Parse(trayRunning ? "#34D399" : "#6B7280"));
        _trayState.Text = trayKnown is null ? "Tray app: checking..." : trayRunning ? "Tray app is running" : "Tray app is not running";
        _trayButton.Content = trayRunning ? "Stop the tray app" : "Start the tray app";
        _trayButton.IsEnabled = !_busy && trayKnown is not null && (trayRunning || trayExeFound);   // looked up in Refresh, off the UI thread
        _removeTask.IsEnabled = !_busy;
    }

    private async Task ToggleService()
    {
        if (!BeginBusy()) return;
        _message.IsVisible = false;
        try
        {
            string? error = _service == ServiceState.Running
                ? await Task.Run(() => Autostart.RunElevated(ServiceControl.StopElevatedStartInfo(), code => code == 0 ? null : $"sc.exe exited with code {code}."))
                : await Task.Run(() => ServiceControl.Start());
            if (error is not null) Say(error);
            await Task.Delay(1500);
        }
        finally { _busy = false; await Refresh(); }
    }

    private async Task ToggleTray()
    {
        if (!BeginBusy()) return;
        _message.IsVisible = false;
        try
        {
            if (_trayRunning) { await Task.Run(() => RunnerClient.Send(ControlCommand.Exit)); await Task.Delay(1200); }
            else if (!await Task.Run(RunnerClient.Launch)) Say("Could not start the tray app (Glance.Runner.exe was not found next to the editor).");
            else await Task.Delay(1500);
        }
        finally { _busy = false; await Refresh(); }
    }

    /// <summary>Glance 0.1.x started an elevated runner at sign-in through a scheduled task; offer to remove a leftover one.</summary>
    private async Task CheckOldTask()
    {
        if (!await Task.Run(Autostart.IsInstalled)) return;
        Say("The old \"Glance Runner\" sign-in task from Glance 0.1 is still set up. It starts the old runner with administrator " +
            "rights and is no longer needed: the Glance service replaces it. Remove it (Windows asks for permission).");
        _removeTask.IsVisible = true;
    }

    private async Task RemoveOldTask()
    {
        if (!BeginBusy()) return;
        try
        {
            var error = await Task.Run(Autostart.Remove);
            if (error is null) { _message.IsVisible = false; _removeTask.IsVisible = false; }
            else Say(error);
        }
        finally { _busy = false; await Refresh(); }
    }

    /// <summary>One action at a time: its buttons are disabled at once (not only at the next refresh), so a second click does nothing.</summary>
    private bool BeginBusy()
    {
        if (_busy) return false;
        _busy = true;
        _serviceButton.IsEnabled = _trayButton.IsEnabled = _removeTask.IsEnabled = false;
        return true;
    }

    private void Say(string text) { _message.Text = text; _message.IsVisible = true; }
}

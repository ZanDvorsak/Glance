using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Glance.Core.Setup;

namespace Glance.App.Views;

/// <summary>Shows what Glance needs (display driver, sensor driver, FFmpeg, runner) with a one-click fix where there is one. Rechecks by itself.</summary>
public sealed class SetupWindow : Window
{
    private readonly Func<SetupContext> _context;
    private readonly Action _openService;
    private readonly StackPanel _rows = new() { Spacing = 8 };
    private readonly TextBlock _summary = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.85, IsVisible = false };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _busyChecking, _installing;

    public SetupWindow(Func<SetupContext> context, Action openService)
    {
        _context = context; _openService = openService;
        Title = "Setup check";
        Width = 720; Height = 640; MinWidth = 520; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var title = new TextBlock { Text = "Setup check", FontSize = 20, FontWeight = FontWeight.Bold };
        var sub = new TextBlock
        {
            Text = "What Glance needs on this PC. This page updates by itself, so you can plug in the display or install a driver and watch it turn green.",
            Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12),
        };
        var recheck = new Button { Content = "Check again" };
        recheck.Click += async (_, _) => await RefreshAsync();
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { recheck, close } };

        var scroll = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var dock = new DockPanel { Margin = new Thickness(22) };
        DockPanel.SetDock(title, Dock.Top); DockPanel.SetDock(sub, Dock.Top); DockPanel.SetDock(_summary, Dock.Top); DockPanel.SetDock(_message, Dock.Bottom); DockPanel.SetDock(buttons, Dock.Bottom);
        _summary.Margin = new Thickness(0, 0, 0, 12); _message.Margin = new Thickness(0, 10, 0, 0);
        dock.Children.Add(title); dock.Children.Add(sub); dock.Children.Add(_summary); dock.Children.Add(buttons); dock.Children.Add(_message); dock.Children.Add(scroll);
        Content = dock;

        _timer.Tick += async (_, _) => { if (!_installing) await RefreshAsync(); };
        Opened += async (_, _) => { await RefreshAsync(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private async Task RefreshAsync()
    {
        if (_busyChecking) return;
        _busyChecking = true;
        try
        {
            var ctx = _context();
            var results = await Task.Run(() => SetupChecks.Run(ctx));   // spawns processes and touches USB, so keep it off the UI thread
            Render(results);
        }
        catch (Exception ex) { Say("Could not run the checks: " + ex.Message); }
        finally { _busyChecking = false; }
    }

    private void Render(List<CheckResult> results)
    {
        int problems = results.Count(r => r.Status == CheckStatus.Problem), warnings = results.Count(r => r.Status == CheckStatus.Warning);
        _summary.Text = problems > 0 ? $"{problems} thing{(problems == 1 ? "" : "s")} must be fixed before Glance can draw on the display."
                      : warnings > 0 ? $"Glance can run. {warnings} thing{(warnings == 1 ? "" : "s")} would improve it."
                      : "Everything Glance needs is in place.";
        _summary.Foreground = new SolidColorBrush(Color.Parse(problems > 0 ? "#FF6B6B" : warnings > 0 ? "#FF9F43" : "#34D399"));

        _rows.Children.Clear();
        foreach (var r in results) _rows.Children.Add(Row(r));
    }

    private Control Row(CheckResult r)
    {
        var color = r.Status switch { CheckStatus.Ok => "#34D399", CheckStatus.Warning => "#FF9F43", CheckStatus.Problem => "#FF6B6B", _ => "#6B7280" };
        var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.Parse(color)), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 12, 0) };
        var text = new StackPanel
        {
            Spacing = 3,
            Children =
            {
                new TextBlock { Text = r.Title, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = r.Detail, FontSize = 12, Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(dot);
        Grid.SetColumn(text, 1); grid.Children.Add(text);

        if (FixButton(r) is { } fix) { fix.Margin = new Thickness(12, 0, 0, 0); fix.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(fix, 2); grid.Children.Add(fix); }

        var card = new Border { Child = grid };
        card.Classes.Add("card");
        return card;
    }

    private Button? FixButton(CheckResult r)
    {
        if (r.Fix == FixKind.None || r.FixLabel is null) return null;

        // once Zadig is installed, the useful action is to start it
        if (r.Fix == FixKind.WingetInstall && r.FixArg == SetupChecks.ZadigId && FindZadig() is { } zadig)
        {
            var open = new Button { Content = "Open Zadig" };
            open.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(zadig) { UseShellExecute = true }); }   // Zadig asks for administrator rights itself
                catch (Exception ex) { Say("Could not start Zadig: " + ex.Message); }
            };
            return open;
        }

        var b = new Button { Content = r.FixLabel };
        b.Click += async (_, _) =>
        {
            switch (r.Fix)
            {
                case FixKind.OpenServiceDialog: _openService(); break;
                case FixKind.OpenUrl: Process.Start(new ProcessStartInfo(r.FixArg!) { UseShellExecute = true }); break;
                case FixKind.WingetInstall or FixKind.WingetInstallMachine: await InstallAsync(r, b); break;
            }
        };
        return b;
    }

    private async Task InstallAsync(CheckResult r, Button button)
    {
        if (_installing) return;
        _installing = true; button.IsEnabled = false; button.Content = "Installing...";
        Say($"Installing {r.FixArg} with winget. Windows may ask for permission. This can take a minute.");
        try
        {
            bool machine = r.Fix == FixKind.WingetInstallMachine;
            var code = await Task.Run(() =>
            {
                using var p = Process.Start(SetupChecks.WingetInstallStartInfo(r.FixArg!, machine))!;
                if (!machine) { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); }
                p.WaitForExit();
                return p.ExitCode;
            });
            Say(code == 0
                ? r.FixArg == SetupChecks.ZadigId
                    ? "Zadig is installed. Click \"Open Zadig\", then: Options > List All Devices, choose \"USB-Display\", pick libusb-win32 and click Install Driver."
                    : $"{r.FixArg} is installed."
                : r.FixArg == SetupChecks.FfmpegId && machine
                    ? $"The installer finished with code {code}. " + SetupChecks.FfmpegMachineInstallHelp
                    : $"The installer finished with code {code}. If it did not install, try the same command in a terminal: winget install --id {r.FixArg}");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == SetupChecks.UacCancelled) { Say("Install cancelled: Windows did not get permission."); }
        catch (Exception ex) { Say("Could not run winget: " + ex.Message); }
        finally { _installing = false; await RefreshAsync(); }
    }

    /// <summary>winget installs Zadig as a portable app; look for it in the places winget puts it.</summary>
    private static string? FindZadig()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (local is null) return null;
        var link = Path.Combine(local, "Microsoft", "WinGet", "Links", "zadig.exe");
        if (File.Exists(link)) return link;
        var pkgs = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(pkgs)) return null;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(pkgs, "*Zadig*"))
                if (Directory.EnumerateFiles(dir, "zadig*.exe", SearchOption.AllDirectories).FirstOrDefault() is { } exe) return exe;
        }
        catch { }
        return null;
    }

    private void Say(string text) { _message.Text = text; _message.IsVisible = true; }
}

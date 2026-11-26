using Glance.Core.Service;
using Glance.Core.Setup;

namespace Glance.Tests;

/// <summary>
/// What the setup check and the Background service window say after the move to a service: no "administrator rights" or
/// "background runner" any more, and FFmpeg is fine for the user alone, because nothing renders elevated.
/// </summary>
public class ServiceChecksTests
{
    [Theory]
    [InlineData(1, ServiceState.Stopped)]
    [InlineData(2, ServiceState.Starting)]
    [InlineData(3, ServiceState.Stopping)]
    [InlineData(4, ServiceState.Running)]
    [InlineData(5, ServiceState.Starting)]   // continue pending
    [InlineData(6, ServiceState.Stopping)]   // pause pending
    [InlineData(7, ServiceState.Stopped)]    // paused: it does not drive the display
    [InlineData(99, ServiceState.Unknown)]
    public void Service_states_are_read(int code, ServiceState expected) => Assert.Equal(expected, ServiceControl.StateFromCode(code));

    [Fact]
    public void A_running_service_with_the_tray_app_is_fine()
    {
        var r = ServiceChecks.DiagnoseService(ServiceState.Running, trayRunning: true);
        Assert.Equal(CheckStatus.Ok, r.Status);
        Assert.Equal("service", r.Id);
    }

    [Fact]
    public void A_running_service_without_the_tray_app_says_the_theme_is_not_shown()
    {
        var r = ServiceChecks.DiagnoseService(ServiceState.Running, trayRunning: false);
        Assert.Equal(CheckStatus.Info, r.Status);
        Assert.Contains("tray app", r.Detail);
        Assert.Equal(FixKind.OpenServiceDialog, r.Fix);
    }

    [Theory]
    [InlineData(ServiceState.Stopped)]
    [InlineData(ServiceState.Stopping)]
    public void A_stopped_service_is_a_warning_with_a_way_to_start_it(ServiceState state)
    {
        var r = ServiceChecks.DiagnoseService(state, trayRunning: true);
        Assert.Equal(CheckStatus.Warning, r.Status);
        Assert.Equal(FixKind.OpenServiceDialog, r.Fix);
    }

    [Fact]
    public void Without_the_service_the_installer_is_suggested()
    {
        var r = ServiceChecks.DiagnoseService(ServiceState.NotInstalled, trayRunning: false);
        Assert.Equal(CheckStatus.Info, r.Status);
        Assert.Contains("installer", r.Detail);
    }

    [Theory]
    [InlineData(ServiceState.Running, true)]
    [InlineData(ServiceState.Running, false)]
    [InlineData(ServiceState.Stopped, false)]
    [InlineData(ServiceState.NotInstalled, false)]
    [InlineData(ServiceState.Unknown, true)]
    public void Nothing_asks_for_administrator_rights_or_mentions_the_old_runner(ServiceState state, bool tray)
    {
        foreach (var r in new[] { ServiceChecks.DiagnoseService(state, tray), ServiceChecks.DiagnoseSensors(false, 12), ServiceChecks.DiagnoseSensors(true, 120) })
        {
            Assert.DoesNotContain("administrator rights", r.Detail);
            Assert.DoesNotContain("runner", r.Detail + r.Title, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Sensors_from_the_service_are_fine_and_without_it_the_service_window_is_offered()
    {
        var ok = ServiceChecks.DiagnoseSensors(true, 120);
        Assert.Equal(CheckStatus.Ok, ok.Status);
        Assert.Contains("Glance service", ok.Detail);

        var limited = ServiceChecks.DiagnoseSensors(false, 12);
        Assert.Equal(CheckStatus.Warning, limited.Status);
        Assert.Equal(FixKind.OpenServiceDialog, limited.Fix);
        Assert.Contains("12", limited.Detail);
    }

    [Fact]
    public void FFmpeg_installed_for_the_user_alone_is_enough_now()
    {
        var r = ServiceChecks.DiagnoseFfmpeg(@"C:\Users\me\AppData\Local\Microsoft\WinGet\Links\ffmpeg.exe");
        Assert.Equal(CheckStatus.Ok, r.Status);
        Assert.DoesNotContain("administrator", r.Detail);

        var missing = ServiceChecks.DiagnoseFfmpeg(null);
        Assert.Equal(FixKind.WingetInstall, missing.Fix);   // a per-user install: no Windows prompt
        Assert.Equal(SetupChecks.FfmpegId, missing.FixArg);
    }
}

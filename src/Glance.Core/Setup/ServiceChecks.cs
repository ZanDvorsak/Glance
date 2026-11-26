using Glance.Core.Service;

namespace Glance.Core.Setup;

/// <summary>The setup check's rows about the Glance service, the tray app, sensors and FFmpeg. Pure, for testing.</summary>
public static class ServiceChecks
{
    private const string Id = "service", Title = "Glance service";

    /// <summary>The service row.</summary>
    public static CheckResult DiagnoseService(ServiceState service, bool trayRunning) => service switch
    {
        ServiceState.Running when trayRunning => new(Id, Title, CheckStatus.Ok,
            "Running. The tray app shows your saved theme on the display, also while the editor is closed."),
        ServiceState.Running => new(Id, Title, CheckStatus.Info,
            "Running, but the tray app is not, so the display only shows your theme while the editor sends it. " +
            "Start the tray app (it also starts by itself when you sign in).",
            "Open Background service", FixKind.OpenServiceDialog),
        ServiceState.NotInstalled => new(Id, Title, CheckStatus.Info,
            "Not installed (portable copy). The tray app or the editor drives the display directly, with the basic sensors only. " +
            "Install Glance with the installer to get CPU temperatures, fans and voltages."),
        ServiceState.Unknown => new(Id, Title, CheckStatus.Info, "Its state could not be read."),
        _ => new(Id, Title, CheckStatus.Warning,
            "Installed but not running, so only the basic sensors are available and the display is driven without it. " +
            "Start it in the Background service window.",
            "Open Background service", FixKind.OpenServiceDialog),
    };

    /// <summary>The sensors row.</summary>
    public static CheckResult DiagnoseSensors(bool fromService, int count) => fromService
        ? new CheckResult("sensors", "Hardware sensors", CheckStatus.Ok, $"{count} sensors, including CPU and motherboard, from the Glance service.")
        : new CheckResult("sensors", "Hardware sensors", CheckStatus.Warning,
            $"Only {count} basic sensors are available because the Glance service is not running. " +
            "With the service, Glance also shows CPU temperatures, fans and voltages.",
            "Open Background service", FixKind.OpenServiceDialog);

    /// <summary>
    /// The FFmpeg row. Video is decoded by the user's own tray app or editor, never by the service, so what this process finds
    /// is what is used, and a per-user install (no Windows prompt) is enough.
    /// </summary>
    public static CheckResult DiagnoseFfmpeg(string? found) => found is not null
        ? new CheckResult("ffmpeg", "FFmpeg (video backgrounds)", CheckStatus.Ok, "Found. Video backgrounds are ready, in the editor and on the display.")
        : new CheckResult("ffmpeg", "FFmpeg (video backgrounds)", CheckStatus.Info,
            "Optional. Only needed for video backgrounds; images and GIFs work without it.",
            "Install FFmpeg", FixKind.WingetInstall, SetupChecks.FfmpegId);
}

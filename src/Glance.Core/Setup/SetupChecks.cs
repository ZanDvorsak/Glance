using System.Diagnostics;
using Glance.Core.Media;
using Glance.Core.Usb;
using Microsoft.Win32;

namespace Glance.Core.Setup;

public enum CheckStatus { Ok, Info, Warning, Problem }
/// <summary><see cref="WingetInstallMachine"/> installs for all users (machine scope, Windows asks for administrator rights).</summary>
public enum FixKind { None, OpenUrl, WingetInstall, OpenServiceDialog, WingetInstallMachine }

/// <summary>One line of the setup check: what was looked at, the verdict, and (when there is one) a one-click fix.</summary>
public sealed record CheckResult(string Id, string Title, CheckStatus Status, string Detail,
    string? FixLabel = null, FixKind Fix = FixKind.None, string? FixArg = null);

/// <summary>Facts only the running app knows.</summary>
public sealed record SetupContext(bool SensorsElevated, int SensorCount, bool AutostartInstalled);

/// <summary>Checks what Glance depends on and says what to do about anything missing. Reads only; changes nothing.</summary>
public static class SetupChecks
{
    public const string ZadigId = "akeo.ie.Zadig";
    public const string PawnIoId = "namazso.PawnIO";
    public const string FfmpegId = "Gyan.FFmpeg";
    private const string DriverHelp = "Then run Zadig: Options > List All Devices, choose \"USB-Display\", pick the libusb-win32 driver and click Install Driver. " +
                                      "(This replaces the driver AIDA64 uses for the display.)";

    public static List<CheckResult> Run(SetupContext ctx)
    {
        var list = new List<CheckResult>();

        bool runner = Process.GetProcessesByName("Glance.Runner").Length > 0;
        list.Add(CheckDisplay(runner));

        if (!runner && Process.GetProcessesByName("aida64").Length > 0)
            list.Add(new CheckResult("aida64", "AIDA64 is running", CheckStatus.Warning,
                "AIDA64 uses the same display. Close it completely (including its tray icon) so Glance can draw on it."));

        list.Add(CheckPawnIo());
        list.Add(ctx.SensorsElevated
            ? new CheckResult("sensors", "Hardware sensors", CheckStatus.Ok, $"{ctx.SensorCount} sensors, including CPU and motherboard, from the background runner.")
            : new CheckResult("sensors", "Hardware sensors", CheckStatus.Warning,
                $"Only {ctx.SensorCount} basic sensors are available because Glance is not running with administrator rights. " +
                "Start the background runner to get CPU temperatures, fans and voltages.",
                "Open Background service", FixKind.OpenServiceDialog));

        list.Add(runner
            ? new CheckResult("runner", "Background runner", CheckStatus.Ok,
                ctx.AutostartInstalled ? "Running, and it starts automatically when you sign in." : "Running. It keeps your theme on the display without the editor.")
            : new CheckResult("runner", "Background runner", CheckStatus.Info,
                "Not running. Without it the display only updates while the editor is open and sending. It also provides the CPU and motherboard sensors.",
                "Open Background service", FixKind.OpenServiceDialog));

        Ffmpeg.Reset();
        var ffmpeg = Ffmpeg.Find();
        list.Add(DiagnoseFfmpeg(ffmpeg, ffmpeg is null ? null : Ffmpeg.FindForElevated()));

        return list;
    }

    /// <summary>
    /// The FFmpeg row. <paramref name="found"/> is what this (normal) process would use; <paramref name="usableByRunner"/>
    /// is what the elevated runner would use, which must sit in a folder only administrators can change.
    /// </summary>
    public static CheckResult DiagnoseFfmpeg(string? found, string? usableByRunner)
    {
        const string id = "ffmpeg", title = "FFmpeg (video backgrounds)";
        if (usableByRunner is not null)
            return new CheckResult(id, title, CheckStatus.Ok, "Found. Video backgrounds are ready, in the editor and in the background runner.");
        if (found is not null)
            return new CheckResult(id, title, CheckStatus.Warning,
                $"Found at {found}, but that folder can be changed without administrator rights. The background runner runs as " +
                "administrator and will not start FFmpeg from there, so video backgrounds only play while the editor is sending. " +
                "Install FFmpeg for all users to fix this (Windows asks for permission).",
                "Install for all users", FixKind.WingetInstallMachine, FfmpegId);
        return new CheckResult(id, title, CheckStatus.Info,
            "Optional. Only needed for video backgrounds; images and GIFs work without it. It is installed for all users " +
            "(Windows asks for permission) so the background runner can use it too.",
            "Install FFmpeg", FixKind.WingetInstallMachine, FfmpegId);
    }

    /// <summary>
    /// How to start winget for a fix. A machine-scope install starts winget elevated (Windows shows a UAC prompt), so
    /// portable packages land under Program Files where only administrators can change them.
    /// </summary>
    public static ProcessStartInfo WingetInstallStartInfo(string packageId, bool machineScope)
    {
        var args = $"install --id {packageId} --exact --accept-package-agreements --accept-source-agreements --disable-interactivity";
        if (!machineScope)
            return new ProcessStartInfo("winget", args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // runas needs the shell, which rules out redirecting output; the exit code still comes back
        return new ProcessStartInfo("winget", args + " --scope machine")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
    }

    /// <summary>What to tell the user when a machine-scope FFmpeg install did not end with a usable copy.</summary>
    public const string FfmpegMachineInstallHelp =
        "If FFmpeg is already installed for your user only, winget may not add a second copy. Remove it first with " +
        "\"winget uninstall --id Gyan.FFmpeg --scope user\", then click Install again, or run " +
        "\"winget install --id Gyan.FFmpeg --scope machine\" in a terminal opened as administrator.";

    /// <summary>The editor's status line after the FFmpeg install button finished.</summary>
    /// <param name="usableByRunner">What the elevated runner would use (<c>Ffmpeg.FindForElevated()</c>).</param>
    /// <param name="usableByEditor">What the editor itself would use (<c>Ffmpeg.Find()</c>).</param>
    public static string FfmpegInstallOutcome(string? usableByRunner, string? usableByEditor, int exitCode) =>
        usableByEditor is not null ? "FFmpeg installed. Video backgrounds are ready."
            : $"FFmpeg install finished with code {exitCode}. " + FfmpegMachineInstallHelp;

    /// <summary>Win32 error code when the user says No to the UAC prompt.</summary>
    public const int UacCancelled = 1223;

    public static bool NeedsAttention(IEnumerable<CheckResult> results) => results.Any(r => r.Status == CheckStatus.Problem);

    // ---- display ------------------------------------------------------------------------------------------------

    private static CheckResult CheckDisplay(bool runnerRunning)
    {
        bool dllPresent = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "libusb0.dll"));
        bool found = false;
        string? openError = null;
        if (dllPresent)
        {
            try
            {
                found = Ax206Display.IsPresent();
                if (found && !runnerRunning) openError = Ax206Display.TryProbeOpen();
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException) { dllPresent = false; }
        }

        var (exists, service) = ReadDriverRegistration();
        return DiagnoseDisplay(dllPresent, found, runnerRunning, openError, exists, service);
    }

    /// <summary>Pure decision logic for the display row, separated so it can be tested without hardware.</summary>
    public static CheckResult DiagnoseDisplay(bool dllPresent, bool found, bool runnerRunning, string? openError, bool registered, string? driverService)
    {
        const string id = "display", title = "USB display (1908:0102)";
        if (!dllPresent)
            return new CheckResult(id, title, CheckStatus.Problem,
                "The libusb-win32 driver is not installed on this PC. Install it with Zadig. " + DriverHelp,
                "Install Zadig", FixKind.WingetInstall, ZadigId);

        if (found)
        {
            if (runnerRunning) return new CheckResult(id, title, CheckStatus.Ok, "Connected, and the Glance background runner is using it.");
            if (openError is null) return new CheckResult(id, title, CheckStatus.Ok, "Connected and ready (480 × 320).");
            return new CheckResult(id, title, CheckStatus.Warning,
                $"Connected, but it cannot be opened ({openError}). Another program is probably using it: close AIDA64 or any other LCD software.");
        }

        if (registered && !string.Equals(driverService, "libusb0", StringComparison.OrdinalIgnoreCase))
            return new CheckResult(id, title, CheckStatus.Problem,
                $"The display is using the \"{driverService ?? "unknown"}\" driver, but Glance needs libusb-win32. Install it with Zadig. " + DriverHelp,
                "Install Zadig", FixKind.WingetInstall, ZadigId);

        if (registered)
            return new CheckResult(id, title, CheckStatus.Problem,
                "The display has the right driver but is not connected right now. Plug in its USB cable, or try another USB port.");

        return new CheckResult(id, title, CheckStatus.Problem,
            "No display has been connected to this PC yet. Plug it in, and if Windows does not set it up for Glance, install the libusb-win32 driver with Zadig. " + DriverHelp,
            "Install Zadig", FixKind.WingetInstall, ZadigId);
    }

    /// <summary>Whether Windows has ever seen the display, and which driver service it is bound to (from the device enumeration registry).</summary>
    private static (bool registered, string? service) ReadDriverRegistration()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB\VID_1908&PID_0102");
            if (key is null) return (false, null);
            foreach (var instance in key.GetSubKeyNames())
            {
                using var k = key.OpenSubKey(instance);
                if (k?.GetValue("Service") is string s && s.Length > 0) return (true, s);
            }
            return (key.SubKeyCount > 0, null);
        }
        catch { return (false, null); }
    }

    // ---- sensor driver ------------------------------------------------------------------------------------------

    private static CheckResult CheckPawnIo()
    {
        bool installed;
        try { using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO"); installed = k is not null; }
        catch { installed = false; }

        return installed
            ? new CheckResult("pawnio", "Sensor driver (PawnIO)", CheckStatus.Ok, "Installed. CPU and motherboard sensors can be read.")
            : new CheckResult("pawnio", "Sensor driver (PawnIO)", CheckStatus.Warning,
                "CPU and motherboard temperatures, fans and voltages need the PawnIO driver. GPU, drive, memory and network sensors work without it.",
                "Install PawnIO", FixKind.WingetInstall, PawnIoId);
    }
}

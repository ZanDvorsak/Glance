using System.Diagnostics;
using Glance.Core.Media;
using Glance.Core.Usb;
using Microsoft.Win32;

namespace Glance.Core.Setup;

public enum CheckStatus { Ok, Info, Warning, Problem }
public enum FixKind { None, OpenUrl, WingetInstall, OpenServiceDialog }

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
        list.Add(ffmpeg is not null
            ? new CheckResult("ffmpeg", "FFmpeg (video backgrounds)", CheckStatus.Ok, "Found. Video backgrounds are ready.")
            : new CheckResult("ffmpeg", "FFmpeg (video backgrounds)", CheckStatus.Info,
                "Optional. Only needed for video backgrounds; images and GIFs work without it.",
                "Install FFmpeg", FixKind.WingetInstall, FfmpegId));

        return list;
    }

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

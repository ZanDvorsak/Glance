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
public sealed record SetupContext(bool SensorsFromService, int SensorCount, Glance.Core.Service.ServiceState Service, bool TrayRunning);

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

        // the service holds the display while the tray app (or the editor) draws; do not try to open it underneath them
        bool glanceUsesDisplay = ctx.Service == Glance.Core.Service.ServiceState.Running || ctx.TrayRunning;
        list.Add(CheckDisplay(glanceUsesDisplay));

        if (!glanceUsesDisplay && Process.GetProcessesByName("aida64").Length > 0)
            list.Add(new CheckResult("aida64", "AIDA64 is running", CheckStatus.Warning,
                "AIDA64 uses the same display. Close it completely (including its tray icon) so Glance can draw on it."));

        list.Add(CheckPawnIo());
        list.Add(ServiceChecks.DiagnoseSensors(ctx.SensorsFromService, ctx.SensorCount));
        list.Add(ServiceChecks.DiagnoseService(ctx.Service, ctx.TrayRunning));

        // video is decoded by the user's own tray app or editor (never by the service), so a per-user FFmpeg is enough
        Ffmpeg.Reset();
        list.Add(ServiceChecks.DiagnoseFfmpeg(Ffmpeg.Find()));

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
                $"Found at {found}, but that folder can be changed without administrator rights (FFmpeg is installed for your " +
                "user only). The background runner runs as administrator and only starts FFmpeg from a folder that only " +
                "administrators can change, so it will not play video backgrounds until FFmpeg is installed for all users. " +
                "Until then, videos play in the editor's preview, and on the display only while the editor drives it without " +
                "the background runner. Click Install for all users (Windows asks for permission).",
                "Install for all users", FixKind.WingetInstallMachine, FfmpegId);
        return new CheckResult(id, title, CheckStatus.Info,
            "Optional. Only needed for video backgrounds; images and GIFs work without it. It is installed for all users " +
            "(Windows asks for permission) so the background runner can use it too.",
            "Install FFmpeg", FixKind.WingetInstallMachine, FfmpegId);
    }

    /// <summary>
    /// How to start winget for a fix. A machine-scope install runs elevated (Windows shows a UAC prompt); see
    /// <see cref="MachineInstallScript"/> for what it does.
    /// </summary>
    public static ProcessStartInfo WingetInstallStartInfo(string packageId, bool machineScope)
    {
        if (!machineScope)
            return new ProcessStartInfo("winget", $"install --id {packageId} --exact --accept-package-agreements --accept-source-agreements --disable-interactivity")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // runas needs the shell, which rules out redirecting output; the script's exit code (winget's) still comes back
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(MachineInstallScript(packageId)));
        return new ProcessStartInfo(powershell, "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded)
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
    }

    /// <summary>Exit code of the install script when winget itself could not be found.</summary>
    public const int WingetNotFound = 9009;

    /// <summary>
    /// The elevated script behind "Install for all users". winget does not elevate itself for a machine-scope portable
    /// install, and it extracts the zip into the user's own %TEMP%\WinGet (whose permissions give that user Full Control)
    /// and then <i>moves</i> the folder to Program Files\WinGet\Packages, so the moved files keep the user's access and
    /// the runner rightly refuses them. The script therefore resets that package's folders to the permissions they inherit
    /// from Program Files and makes Administrators the owner. It runs even when winget reports an error, so clicking
    /// again repairs an earlier install. Nothing it runs comes from the user's PATH or module folders: winget is the
    /// packaged App Installer exe, icacls comes from System32, and modules load only from the system folder.
    /// </summary>
    public static string MachineInstallScript(string packageId)
    {
        if (packageId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
            throw new ArgumentException("Unexpected characters in a winget package id: " + packageId, nameof(packageId));
        return $$"""
            $ErrorActionPreference = 'Stop'
            $sys = [Environment]::GetFolderPath('System')
            $env:PSModulePath = [IO.Path]::Combine($sys, 'WindowsPowerShell\v1.0\Modules')
            try {
                $pkg = Get-AppxPackage -Name Microsoft.DesktopAppInstaller | Sort-Object Version -Descending | Select-Object -First 1
                $winget = Join-Path $pkg.InstallLocation 'winget.exe'
                if (-not (Test-Path -LiteralPath $winget)) { exit {{WingetNotFound}} }
            } catch { exit {{WingetNotFound}} }
            $ErrorActionPreference = 'Continue'
            & $winget install --id {{packageId}} --exact --scope machine --accept-package-agreements --accept-source-agreements --disable-interactivity
            $code = $LASTEXITCODE
            $icacls = Join-Path $sys 'icacls.exe'
            $root = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'WinGet\Packages'
            if (Test-Path -LiteralPath $root) {
                Get-ChildItem -LiteralPath $root -Directory -Filter '{{packageId}}_*' | ForEach-Object {
                    & $icacls $_.FullName /reset /T /C /Q | Out-Null
                    & $icacls $_.FullName /setowner *S-1-5-32-544 /T /C /Q | Out-Null
                }
            }
            exit $code
            """;
    }

    /// <summary>What to tell the user when the all-users FFmpeg install did not end with a copy the runner can use.</summary>
    public const string FfmpegMachineInstallHelp =
        "Video backgrounds will not play in the background runner until FFmpeg is installed for all users. Open Setup check " +
        "and click Install for all users again: it also repairs the permissions of an earlier all-users install. " +
        "The details are in runner.log.";

    /// <summary>The editor's status line after the FFmpeg install button finished.</summary>
    /// <param name="usableByRunner">What the elevated runner would use (<c>Ffmpeg.FindForElevated()</c>).</param>
    /// <param name="usableByEditor">What the editor itself would use (<c>Ffmpeg.Find()</c>).</param>
    public static string FfmpegInstallOutcome(string? usableByRunner, string? usableByEditor, int exitCode) =>
        usableByRunner is not null ? "FFmpeg is installed for all users. Video backgrounds are ready, also in the background runner."
        : exitCode == WingetNotFound ? "winget (App Installer) was not found, so FFmpeg could not be installed. " + FfmpegMachineInstallHelp
        : usableByEditor is not null ? $"The editor can use FFmpeg, but the background runner cannot use it yet (winget code {exitCode}). " + FfmpegMachineInstallHelp
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
            if (runnerRunning) return new CheckResult(id, title, CheckStatus.Ok, "Connected, and Glance (the service or the tray app) is using it.");
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

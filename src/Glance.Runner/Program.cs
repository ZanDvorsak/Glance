using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Glance.Core.Security;
using Glance.Core.Settings;
using Glance.Core.Setup;

namespace Glance.Runner;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The tray app runs with the signed-in user's normal rights (asInvoker); everything that needs administrator rights
        // (USB display ownership, CPU and motherboard sensors) is done by the Glance service. See docs/design/runner-service.md.
        // Glance 0.1's logon task starts this exe with highest privileges whatever its manifest says (review M3), so check first.
        var (type, elevated) = ReadElevation();
        bool relaunched;
        using (var marker = Mutex.TryOpenExisting(TrayStart.RelaunchMarker, out var m) ? m : null) relaunched = marker is not null;
        switch (TrayStart.Decide(type, elevated, relaunched))
        {
            case TrayStartAction.RelaunchUnelevated:
                Log("Started with administrator rights (probably by Glance 0.1's \"Glance Runner\" sign-in task); starting again with normal rights. " +
                    "Remove that task in the editor's Background service window.");
                if (RunnerEnvironment.Warning(Environment.GetEnvironmentVariables()) is { } warning) Log(warning);
                RelaunchUnelevated();
                return 0;
            case TrayStartAction.Refuse:
                Log("Still running with administrator rights after starting again with normal rights; the tray app does not run elevated, so it exits.");
                return 3;
        }

        // one tray app per sign-in session ("Local\" is per session)
        using var mutex = new Mutex(true, @"Local\Glance.Runner", out var first);
        if (!first) return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new RunnerContext());
        return 0;
    }

    /// <summary>
    /// explorer.exe (the user's shell, not elevated) starts the exe with the user's normal token. A named marker tells the new copy
    /// that it was relaunched, so it refuses instead of relaunching again if it is somehow still elevated.
    /// </summary>
    private static void RelaunchUnelevated()
    {
        using var marker = new Mutex(false, TrayStart.RelaunchMarker);
        try
        {
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            Process.Start(new ProcessStartInfo(explorer, $"\"{Environment.ProcessPath}\"") { UseShellExecute = false });
            Thread.Sleep(10000);   // keep the marker while the new copy starts
        }
        catch (Exception ex) { Log("Could not start again with normal rights: " + ex.Message); }
    }

    private static (TokenElevation Type, bool Elevated) ReadElevation()
    {
        using var me = WindowsIdentity.GetCurrent();
        var type = GetTokenInformation(me.Token, 18 /* TokenElevationType */, out int t, sizeof(int), out _) ? (TokenElevation)t : TokenElevation.Default;
        var elevated = GetTokenInformation(me.Token, 20 /* TokenElevation */, out int e, sizeof(int), out _) && e != 0;
        return (type, elevated);
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(Path.Combine(AppSettings.Folder, "runner.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* logging must never stop the tray app */ }
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);
}

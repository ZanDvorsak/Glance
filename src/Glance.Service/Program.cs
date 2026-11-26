using System.Runtime.InteropServices;
using System.Security.Principal;
using Glance.Core.Display;
using Glance.Core.Service;

namespace Glance.Service;

/// <summary>
/// Glance's Windows service. Installed by the installer (<c>sc.exe create GlanceService ...</c>) to run as LocalSystem, so its
/// environment comes from the Service Control Manager and never from a signed-in user (review H6/H1).
/// <para>
/// Debug builds also accept <c>--console</c>, which runs the same code in a console window until Ctrl+C. Release builds do not:
/// run from an elevated prompt, it would be a Glance .NET process elevated with the user's environment (review N2).
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        SetDefaultDllDirectories(LoadLibrarySearchSystem32 | LoadLibrarySearchApplicationDir);   // M4: never search PATH or the current folder as SYSTEM

#if DEBUG
        if (args.Contains("--console")) return RunInConsole();
#endif

        // The log is opened inside ServiceMain, once Windows has started this process as the service (L6).
        if (!NativeService.Run((log, logProblem) => new ServiceRuntime(log, logProblem)))
        {
            Console.Error.WriteLine("Glance.Service is a Windows service; the Glance installer sets it up.");
            return 2;
        }
        return 0;
    }

#if DEBUG
    private static int RunInConsole()
    {
        using var me = WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
        var log = ServiceLog.ShouldOpen(me.IsSystem, elevated) ? ServiceLog.OpenDefault() : null;
        if (log?.DisabledReason is { } why) Console.WriteLine(why);
        var runtime = new ServiceRuntime(m => { Console.WriteLine(m); log?.Write(m); }, log?.DisabledReason);
        using var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        runtime.Start();
        Console.WriteLine("Glance service running in the console (debug build). Ctrl+C stops it.");
        done.Wait();
        runtime.Stop(new ServiceSignal(ServiceSignalKind.Power, PowerEvent.Quit));
        return 0;
    }
#endif

    private const uint LoadLibrarySearchApplicationDir = 0x200, LoadLibrarySearchSystem32 = 0x800;

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);
}

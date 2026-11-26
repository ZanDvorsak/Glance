using Glance.Core.Display;
using Glance.Core.Service;

namespace Glance.Service;

/// <summary>
/// Glance's Windows service. Installed by the installer (<c>sc.exe create GlanceService ...</c>) to run as LocalSystem, so its
/// environment comes from the Service Control Manager and never from a signed-in user (review H6/H1).
/// <para>
/// <c>--console</c> runs the same code in a console window until Ctrl+C, for development. Run it from an elevated prompt:
/// clients only trust a display or sensor pipe served by SYSTEM or Administrators.
/// </para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var log = OperatingSystem.IsWindows() ? ServiceLog.OpenDefault() : null;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log?.Write("Unhandled error: " + e.ExceptionObject);

        if (args.Contains("--console"))
        {
            if (log?.DisabledReason is { } why) Console.WriteLine(why);
            return RunInConsole(m => { Console.WriteLine(m); log?.Write(m); }, log?.DisabledReason);
        }

        if (!NativeService.Run(() => new ServiceRuntime(m => log?.Write(m), log?.DisabledReason)))
        {
            Console.Error.WriteLine("Glance.Service is a Windows service; the Glance installer sets it up. Use --console to run it here for testing.");
            return 2;
        }
        return 0;
    }

    private static int RunInConsole(Action<string> log, string? logProblem)
    {
        var runtime = new ServiceRuntime(log, logProblem);
        using var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        runtime.Start();
        Console.WriteLine("Glance service running in the console. Ctrl+C stops it.");
        done.Wait();
        runtime.Stop(new ServiceSignal(ServiceSignalKind.Power, PowerEvent.Quit));
        return 0;
    }
}

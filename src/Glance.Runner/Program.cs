using Glance.Core.Security;
using Glance.Core.Setup;

namespace Glance.Runner;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The runner runs elevated with the user's environment (the autostart task passes it on). Some variables make
        // .NET load code before Main, which cannot be blocked from here, so at least make it visible.
        if (RunnerEnvironment.Warning(Environment.GetEnvironmentVariables()) is { } warning) RunnerLog.Write(warning);

        // Auto-start registration mode: started by the editor through a UAC prompt, does its job and exits.
        // It must not take the single-instance mutex, show a tray icon or touch the display.
        var request = AutostartTask.ParseArgs(args);
        if (request.Mode == AutostartMode.Invalid)
        {
            RunnerLog.Write($"Auto-start: refused the command line ({request.Error}).");
            return AutostartTask.Exit.BadArguments;
        }
        if (request.Mode != AutostartMode.None) return AutostartInstaller.Run(request);

        // one runner per login session
        using var mutex = new Mutex(true, @"Local\Glance.Runner", out var first);
        if (!first) return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new RunnerContext());
        return 0;
    }
}

using Glance.Core.Setup;

namespace Glance.Runner;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Auto-start registration mode: started by the editor through a UAC prompt, does its job and exits.
        // It must not take the single-instance mutex, show a tray icon or touch the display.
        var request = AutostartTask.ParseArgs(args);
        if (request.Mode == AutostartMode.Invalid) return AutostartTask.Exit.BadArguments;
        if (request.Mode != AutostartMode.None) return AutostartInstaller.Run(request);

        // one runner per login session
        using var mutex = new Mutex(true, @"Local\Glance.Runner", out var first);
        if (!first) return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new RunnerContext());
        return 0;
    }
}

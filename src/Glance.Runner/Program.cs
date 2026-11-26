namespace Glance.Runner;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // The tray app runs with the signed-in user's normal rights (asInvoker); everything that needs administrator rights
        // (USB display ownership, CPU and motherboard sensors) is done by the Glance service. See docs/design/runner-service.md.

        // one tray app per sign-in session ("Local\" is per session)
        using var mutex = new Mutex(true, @"Local\Glance.Runner", out var first);
        if (!first) return 0;

        ApplicationConfiguration.Initialize();
        Application.Run(new RunnerContext());
        return 0;
    }
}

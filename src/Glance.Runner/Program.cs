namespace Glance.Runner;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // one runner per login session
        using var mutex = new Mutex(true, @"Local\Glance.Runner", out var first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        Application.Run(new RunnerContext());
    }
}

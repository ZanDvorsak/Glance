using Avalonia;
using Avalonia.Threading;
using Glance.Core.Diagnostics;
using Glance.Core.Settings;

namespace Glance.App;

internal static class Program
{
    /// <summary>Where unhandled errors go: %AppData%\Glance\editor.log (next to the runner's runner.log).</summary>
    public static string CrashLogFile => Path.Combine(AppSettings.Folder, "editor.log");

    [STAThread]
    public static void Main(string[] args)
    {
        // Anything that would otherwise vanish or kill the editor silently is logged first.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog.Write(CrashLogFile, e.IsTerminating ? "Unhandled exception (editor closing)" : "Unhandled exception", e.ExceptionObject as Exception, DateTime.Now);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLog.Write(CrashLogFile, "Unobserved task exception", e.Exception, DateTime.Now);
            e.SetObserved();
        };
        // An exception in a UI event handler is logged and the editor keeps running, so one bad click or a bad value in a
        // theme does not throw away the user's unsaved work. The log is capped (CrashLog.MaxBytes) in case it repeats.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLog.Write(CrashLogFile, "Unhandled UI exception (editor kept running)", e.Exception, DateTime.Now);
            e.Handled = true;
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        MainWindow.WaitForExitBlank();   // the display is blanked off the UI thread while the window closes; let it finish (bounded)
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

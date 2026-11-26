using Avalonia;
using Avalonia.Threading;
using Glance.Core.Diagnostics;
using Glance.Core.Settings;

namespace Glance.App;

internal static class Program
{
    /// <summary>Where unhandled errors go: %AppData%\Glance\editor.log (next to the runner's runner.log).</summary>
    public static string CrashLogFile => Path.Combine(AppSettings.Folder, "editor.log");

    /// <summary>Raised on the UI thread when the user should hear about a caught UI error:
    /// <see cref="CrashVerdict.Notify"/> (first time) or <see cref="CrashVerdict.Storm"/> (it keeps repeating).</summary>
    public static event Action<CrashVerdict, Exception>? UiError;

    private static readonly CrashGuard Guard = new();

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
        // An exception in a UI event handler is caught so one bad click or a bad value in a theme does not throw away the
        // user's unsaved work. The user is told the first time (status bar). If the same error keeps repeating (a timer or
        // layout pass failing every frame), CrashGuard stops logging it and the editor asks the user to save and close.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            var now = DateTime.Now;
            var verdict = Guard.Note(e.Exception, now);
            switch (verdict)
            {
                case CrashVerdict.Notify:
                case CrashVerdict.Log:
                    CrashLog.Write(CrashLogFile, "Unhandled UI exception (editor kept running)", e.Exception, now);
                    break;
                case CrashVerdict.Storm:
                    CrashLog.Write(CrashLogFile, $"The same UI exception happened {CrashGuard.StormCount} times within {CrashGuard.Window.TotalSeconds:0} s; " +
                        "the user was asked to save and close, and further repeats are not logged", e.Exception, now);
                    break;
            }
            e.Handled = true;
            if (verdict is CrashVerdict.Notify or CrashVerdict.Storm) UiError?.Invoke(verdict, e.Exception);
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

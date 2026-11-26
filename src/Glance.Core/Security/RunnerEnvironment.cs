using System.Collections;

namespace Glance.Core.Security;

/// <summary>
/// Environment variables that make the .NET runtime load or connect to code chosen by whoever set them, before Main runs
/// (review finding H1 of the autostart change). The autostart task starts the elevated runner with the user's own
/// environment, which includes <c>HKCU\Environment</c>, so a normal program can set these without a prompt.
/// </summary>
/// <remarks>
/// <para>Measured with .NET 8.0 on Windows 11 (see docs/reviews/review-autostart-task.md, "Fixes after review"):</para>
/// <list type="bullet">
/// <item><c>DOTNET_STARTUP_HOOKS</c> is blocked by <c>StartupHookSupport=false</c> in the runner's runtimeconfig.json.</item>
/// <item><c>DOTNET_DiagnosticPorts</c> makes even a self-contained app connect to a pipe of the user's choice (through
/// which a profiler can be attached), and no runtimeconfig.json setting turns that off: the environment wins.</item>
/// <item><c>CORECLR_ENABLE_PROFILING</c>/<c>CORECLR_PROFILER_PATH</c> are read the same way (documented behaviour).</item>
/// </list>
/// <para>So the runner cannot block them itself; it can only report them. Only a clean environment (a service, or a
/// launcher that builds one) removes the risk.</para>
/// </remarks>
public static class RunnerEnvironment
{
    // Prefixes the CoreCLR configuration and profiler loader read (COMPlus_ is the older spelling of DOTNET_).
    private static readonly string[] RiskyPrefixes = { "CORECLR_", "COMPlus_", "DOTNET_", "COR_ENABLE_PROFILING", "COR_PROFILER" };

    // DOTNET_ variables only the SDK and CLI read; they do nothing in an app.
    private static readonly string[] HarmlessDotnet =
    {
        "DOTNET_CLI_", "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "DOTNET_GENERATE_ASPNET_CERTIFICATE",
        "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "DOTNET_WATCH", "DOTNET_INSTALL_",
    };

    /// <summary>The names of variables in <paramref name="environment"/> that change how the runtime starts or what it loads.</summary>
    public static IReadOnlyList<string> FindRiskyVariables(IDictionary environment)
    {
        var found = new List<string>();
        foreach (DictionaryEntry e in environment)
        {
            if (e.Key is not string name || string.IsNullOrEmpty(e.Value as string)) continue;
            if (!RiskyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (HarmlessDotnet.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) continue;
            found.Add(name);
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    /// <summary>The warning line the runner writes to runner.log, or null when nothing risky is set.</summary>
    public static string? Warning(IDictionary environment)
    {
        var risky = FindRiskyVariables(environment);
        return risky.Count == 0 ? null
            : "WARNING: these environment variables can make .NET load code into this elevated process before it starts: " +
              string.Join(", ", risky) + ". If you did not set them (Windows Settings > Environment variables), remove them and restart.";
    }
}

using Glance.Core.Service;

namespace Glance.App.Services;

/// <summary>Finds Glance's other programs next to the editor, or (development builds only) in the repository's build output.</summary>
public static class AppPaths
{
    public static string? FindExe(string exe, string projectFolder) => DevPaths.FindSibling(exe, projectFolder);

    /// <summary>The tray app (Glance.Runner.exe).</summary>
    public static string? FindTrayExe() => FindExe("Glance.Runner.exe", "Glance.Runner");
}

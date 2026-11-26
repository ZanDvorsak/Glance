namespace Glance.App.Services;

/// <summary>Finds Glance's other programs next to the editor, or in a sibling project's build output during development.</summary>
public static class AppPaths
{
    public static string? FindExe(string exe, string projectFolder)
    {
        var dir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe);
        for (var d = dir; d is not null; d = Path.GetDirectoryName(d))
        {
            var proj = Path.Combine(d, projectFolder, "bin");
            if (!Directory.Exists(proj)) continue;
            return Directory.EnumerateFiles(proj, exe, SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        return null;
    }

    /// <summary>The tray app (Glance.Runner.exe).</summary>
    public static string? FindTrayExe() => FindExe("Glance.Runner.exe", "Glance.Runner");
}

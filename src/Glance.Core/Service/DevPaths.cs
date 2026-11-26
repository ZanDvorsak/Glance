namespace Glance.Core.Service;

/// <summary>
/// Finds another Glance program next to this one, or, in a development build only, in a sibling project's build output inside
/// the same repository (review L9). An installed or unzipped copy never searches other folders: anyone can create folders such as
/// <c>C:\Glance.Runner\bin</c>, and the found program would run as the user who clicked.
/// </summary>
public static class DevPaths
{
    private const char Sep = '\\';

    /// <param name="exesBelow">The candidates named like the program below a folder, best first.</param>
    public static string? FindSibling(string baseDir, string exe, string projectFolder,
        Func<string, bool> fileExists, Func<string, bool> dirExists, Func<string, IEnumerable<string>> exesBelow)
    {
        if (fileExists(Path.Combine(baseDir, exe))) return Path.Combine(baseDir, exe);
        if (baseDir.IndexOf(Sep + "bin" + Sep, StringComparison.OrdinalIgnoreCase) < 0) return null;   // not a development build

        // the repository: the nearest folder above that holds Glance.sln; search no higher than that
        string? root = null;
        for (var d = Path.GetDirectoryName(baseDir.TrimEnd(Sep)); d is not null; d = Path.GetDirectoryName(d))
            if (fileExists(Path.Combine(d, "Glance.sln"))) { root = d; break; }
        if (root is null) return null;

        for (var d = baseDir.TrimEnd(Sep); d is not null && d.Length >= root.Length; d = Path.GetDirectoryName(d))
        {
            var proj = Path.Combine(d, projectFolder, "bin");
            if (dirExists(proj)) return exesBelow(proj).FirstOrDefault();
        }
        return null;
    }

    /// <summary>The same against the real file system, newest build first.</summary>
    public static string? FindSibling(string exe, string projectFolder) =>
        FindSibling(AppContext.BaseDirectory, exe, projectFolder, File.Exists, Directory.Exists,
            dir => Directory.EnumerateFiles(dir, exe, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc));
}

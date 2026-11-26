using System.IO.Compression;
using System.Text;

namespace Glance.Core.Themes;

/// <summary>
/// A single-file way to share a theme: a zip named *.glance holding theme.json and an assets folder with its images and videos.
/// Importing is defensive: only theme.json and files under assets/ are accepted, paths cannot escape the target folder,
/// and the size is capped, because the file may come from someone else.
/// </summary>
public static class ThemePackage
{
    public const string Extension = ".glance";
    public const string ThemeEntry = "theme.json";
    private const long MaxTotalBytes = 1L * 1024 * 1024 * 1024;   // 1 GB of extracted data
    private const int MaxEntries = 500;

    /// <summary>Writes the theme and every image/video it uses into <paramref name="destination"/>. Paths inside are made relative.</summary>
    public static void Export(Theme theme, string destination)
    {
        // work on a copy so the open theme keeps its own paths
        var copy = new Theme { BaseFolder = theme.BaseFolder };
        copy.ApplySnapshot(theme.ToSnapshot());

        if (File.Exists(destination)) File.Delete(destination);
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
        var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // source file -> entry name
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // adds a referenced file (image, video or font) to the package and returns its path inside it
        string Pack(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            var source = copy.ResolveAsset(path);
            if (source is null || !File.Exists(source)) return path;
            if (!used.TryGetValue(source, out var entry))
            {
                var baseName = SafeFileName(Path.GetFileName(source));
                var name = baseName;
                for (int n = 2; !names.Add(name); n++) name = $"{Path.GetFileNameWithoutExtension(baseName)}_{n}{Path.GetExtension(baseName)}";
                entry = "assets/" + name;
                used[source] = entry;
                zip.CreateEntryFromFile(source, entry, CompressionLevel.Optimal);
            }
            return entry;
        }
        foreach (var w in copy.Pages.SelectMany(p => p.Widgets))
        {
            w.ImagePath = Pack(w.ImagePath);
            w.FontFile = Pack(w.FontFile);
        }

        var json = zip.CreateEntry(ThemeEntry, CompressionLevel.Optimal);
        using var writer = new StreamWriter(json.Open(), new UTF8Encoding(false));
        writer.Write(copy.ToSnapshot());
    }

    /// <summary>Extracts a package into <paramref name="destFolder"/> and returns the path of its theme.json. Throws on anything suspicious.</summary>
    public static string Import(string package, string destFolder) => Import(package, destFolder, out _);

    /// <summary>
    /// Extracts a package into <paramref name="destFolder"/> and returns the path of its theme.json. Throws on anything
    /// suspicious in the archive itself. Widget paths inside theme.json that do not point into the package's own
    /// <c>assets/</c> folder (network, device or absolute paths, "..", anything else outside) are cleared, the cleaned
    /// theme is saved, and what was cleared is returned in <paramref name="removed"/> so the editor can tell the user.
    /// </summary>
    public static string Import(string package, string destFolder, out IReadOnlyList<RemovedAssetPath> removed)
    {
        using var zip = ZipFile.OpenRead(package);
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException("This theme file has too many files.");
        if (zip.GetEntry(ThemeEntry) is null) throw new InvalidDataException("This is not a Glance theme: it has no theme.json.");

        long total = 0;
        var root = Path.GetFullPath(destFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var plan = new List<(ZipArchiveEntry entry, string target)>();
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;   // folder entry
            var name = entry.FullName.Replace('\\', '/');
            if (name != ThemeEntry && !name.StartsWith("assets/", StringComparison.Ordinal))
                throw new InvalidDataException($"Unexpected file in theme: {name}");
            if (name.Contains("..") || Path.IsPathRooted(name) || name.Contains(':'))
                throw new InvalidDataException($"Unsafe path in theme: {name}");

            var target = Path.GetFullPath(Path.Combine(destFolder, name.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unsafe path in theme: {name}");

            total += entry.Length;   // declared size; also re-checked while copying, since it can lie
            if (total > MaxTotalBytes) throw new InvalidDataException("This theme file is too large.");
            plan.Add((entry, target));
        }

        Directory.CreateDirectory(destFolder);
        long written = 0;
        foreach (var (entry, target) in plan)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = File.Create(target);
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > MaxTotalBytes) throw new InvalidDataException("This theme file is too large.");
                output.Write(buffer, 0, read);
            }
        }

        var themeFile = Path.Combine(destFolder, ThemeEntry);
        var theme = Theme.Load(themeFile);   // fails early if the JSON is not a valid theme
        removed = RemoveForeignPaths(theme, destFolder);
        // Written directly, not with Theme.Save: every path left is relative and inside assets/, so there is nothing to
        // copy, and Save must not touch the paths just removed. The folder is new; a failed write fails the import.
        if (removed.Count > 0) File.WriteAllText(themeFile, theme.ToSnapshot(), new UTF8Encoding(false));
        return themeFile;
    }

    /// <summary>Clears every widget image/video and font path that does not stay inside <c>&lt;destFolder&gt;\assets\</c>.</summary>
    internal static IReadOnlyList<RemovedAssetPath> RemoveForeignPaths(Theme theme, string destFolder)
    {
        var removed = new List<RemovedAssetPath>();
        foreach (var page in theme.Pages)
            foreach (var w in page.Widgets)
            {
                if (WhyForeign(w.ImagePath, destFolder) is { } r1) { removed.Add(new(page.Name, w.Name, RemovedAssetPath.Picture, w.ImagePath, r1)); w.ImagePath = ""; }
                if (WhyForeign(w.FontFile, destFolder) is { } r2) { removed.Add(new(page.Name, w.Name, RemovedAssetPath.Font, w.FontFile, r2)); w.FontFile = ""; }
            }
        return removed;
    }

    /// <summary>Null when <paramref name="path"/> is empty or a relative path inside the package's assets folder; otherwise why not.</summary>
    internal static string? WhyForeign(string path, string destFolder)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Theme.IsNetworkOrDevicePath(path)) return "network or device path";
        if (Path.IsPathRooted(path)) return "absolute path";
        if (path.Contains(':')) return "drive or stream marker";
        if (path.Split('/', '\\').Any(s => s == "..")) return "contains \"..\"";
        try
        {
            var assets = Path.GetFullPath(Path.Combine(destFolder, "assets")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(destFolder, path));
            return full.StartsWith(assets, StringComparison.OrdinalIgnoreCase) ? null : "outside the theme's assets folder";
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return "not a valid path";   // e.g. a null character or 40,000 characters: drop this path, keep the theme
        }
    }

    private static string SafeFileName(string n) => string.Concat(n.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

/// <summary>A widget path that <see cref="ThemePackage.Import(string, string, out IReadOnlyList{RemovedAssetPath})"/> cleared.</summary>
/// <param name="Property">What the path was for: <see cref="Picture"/> or <see cref="Font"/>.</param>
public sealed record RemovedAssetPath(string Page, string Widget, string Property, string Path, string Reason)
{
    public const string Picture = "picture or video";
    public const string Font = "font";

    /// <summary>One line for the editor's report, worded like the dialog around it ("pick the pictures or fonts again").</summary>
    public override string ToString() => $"Page \"{Page}\", widget \"{Widget}\": {Property} {Path} ({Reason})";
}

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
    public static string Import(string package, string destFolder)
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
        Theme.Load(themeFile);   // fails early if the JSON is not a valid theme
        return themeFile;
    }

    private static string SafeFileName(string n) => string.Concat(n.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

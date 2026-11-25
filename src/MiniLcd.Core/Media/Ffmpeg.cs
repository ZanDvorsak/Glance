namespace MiniLcd.Core.Media;

public static class Ffmpeg
{
    public static readonly string[] VideoExtensions = { ".mp4", ".webm", ".mkv", ".mov", ".avi", ".m4v", ".wmv" };

    public static bool IsVideo(string? path) =>
        path is not null && VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static string? _cached;
    private static DateTime _checked;

    /// <summary>Finds ffmpeg.exe: next to the app, on PATH, or in the usual winget locations. Null if missing.</summary>
    public static string? Find()
    {
        if (_cached is not null && File.Exists(_cached)) return _cached;
        if (_cached is null && DateTime.UtcNow - _checked < TimeSpan.FromSeconds(5)) return null; // don't hammer the disk
        _checked = DateTime.UtcNow;
        return _cached = Search();
    }

    private static string? Search()
    {
        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"), Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe") };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim('"'), "ffmpeg.exe"));
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (local is not null)
        {
            candidates.Add(Path.Combine(local, "Microsoft", "WinGet", "Links", "ffmpeg.exe"));
            var pkgs = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(pkgs))
                foreach (var p in Directory.EnumerateDirectories(pkgs, "*FFmpeg*"))
                    try { candidates.AddRange(Directory.EnumerateFiles(p, "ffmpeg.exe", SearchOption.AllDirectories)); } catch { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Forget the cached lookup (e.g. right after installing).</summary>
    public static void Reset() { _cached = null; _checked = default; }
}

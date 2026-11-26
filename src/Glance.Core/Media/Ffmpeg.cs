namespace Glance.Core.Media;

public static class Ffmpeg
{
    public static readonly string[] VideoExtensions = { ".mp4", ".webm", ".mkv", ".mov", ".avi", ".m4v", ".wmv" };

    public static bool IsVideo(string? path) =>
        path is not null && VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static readonly object Gate = new();
    private static string? _cached;
    private static DateTime _checked;
    private static readonly HashSet<string> Reported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised (once per path and reason) when an elevated process skips an ffmpeg.exe because a normal user could
    /// replace it. The runner writes these to runner.log.
    /// </summary>
    public static event Action<string>? Rejected;

    /// <summary>Why the last search of an elevated process found nothing usable, or null.</summary>
    public static string? LastRejection { get; private set; }

    /// <summary>
    /// Finds ffmpeg.exe: next to the app, on PATH, or in the usual winget locations. Null if missing.
    /// In an elevated process (the runner) only a copy that non-admin users cannot change is returned, because starting
    /// a user-replaceable exe as administrator would hand admin rights to any program the user runs.
    /// </summary>
    public static string? Find()
    {
        lock (Gate)
        {
            if (_cached is not null && File.Exists(_cached)) return _cached;
            if (_cached is null && DateTime.UtcNow - _checked < TimeSpan.FromSeconds(5)) return null; // don't hammer the disk
            _checked = DateTime.UtcNow;
            LastRejection = null;
            return _cached = Search(Environment.IsPrivilegedProcess, Report);
        }
    }

    /// <summary>
    /// The copy an elevated process (the background runner) would use, or null. Lets the non-elevated editor tell the user
    /// when FFmpeg exists only in a folder the runner refuses. Not cached.
    /// </summary>
    public static string? FindForElevated() => Search(requireAdminOnly: true, onRejected: null);

    private static void Report(string path, string reason)
    {
        var message = $"FFmpeg at {path} is not used: {reason}. The runner runs as administrator, so it only starts FFmpeg " +
                      "from a folder that only administrators can change (install it for all users).";
        LastRejection = message;
        if (Reported.Add(path + "|" + reason))
            try { Rejected?.Invoke(message); } catch { /* logging must never break the search */ }
    }

    /// <summary>Every place ffmpeg.exe is looked for, in order (existing or not).</summary>
    public static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
        yield return Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string? c = null;
            try { c = Path.Combine(dir.Trim().Trim('"'), "ffmpeg.exe"); } catch { }
            if (c is not null) yield return c;
        }
        // winget: "--scope machine" puts portable packages under Program Files (only admins can change them),
        // the default user scope under %LOCALAPPDATA% (the user can). The Links entries are symlinks, so the real
        // files under Packages are listed as well.
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetEnvironmentVariable("LOCALAPPDATA") is { } l ? Path.Combine(l, "Microsoft") : null })
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return Path.Combine(root, "WinGet", "Links", "ffmpeg.exe");
            var pkgs = Path.Combine(root, "WinGet", "Packages");
            var found = new List<string>();
            try
            {
                if (Directory.Exists(pkgs))
                    foreach (var p in Directory.EnumerateDirectories(pkgs, "*FFmpeg*"))
                        try { found.AddRange(Directory.EnumerateFiles(p, "ffmpeg.exe", SearchOption.AllDirectories)); } catch { }
            }
            catch { }
            foreach (var f in found) yield return f;
        }
    }

    internal static string? Search(bool requireAdminOnly, Action<string, string>? onRejected)
    {
        foreach (var candidate in Candidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)) continue;
            if (!requireAdminOnly) return candidate;
            var verdict = FfmpegTrust.Check(candidate);
            if (verdict.Trusted) return candidate;
            onRejected?.Invoke(candidate, verdict.Reason);
        }
        return null;
    }

    /// <summary>Forget the cached lookup (e.g. right after installing).</summary>
    public static void Reset() { lock (Gate) { _cached = null; _checked = default; } }
}

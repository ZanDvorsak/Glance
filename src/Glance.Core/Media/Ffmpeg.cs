namespace Glance.Core.Media;

public static class Ffmpeg
{
    public static readonly string[] VideoExtensions = { ".mp4", ".webm", ".mkv", ".mov", ".avi", ".m4v", ".wmv" };

    public static bool IsVideo(string? path) =>
        path is not null && VideoExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>The finder the app uses: real elevation, real candidates, real file system and ACLs.</summary>
    internal static readonly FfmpegFinder Default = new(() => Environment.IsPrivilegedProcess, Candidates, File.Exists, FfmpegTrust.Check);

    /// <summary>
    /// Raised (once per path and reason) when an elevated process skips an ffmpeg.exe because a normal user could
    /// replace it. The runner writes these to runner.log.
    /// </summary>
    public static event Action<string>? Rejected { add => Default.Rejected += value; remove => Default.Rejected -= value; }

    /// <summary>Why the last search of an elevated process found nothing usable, or null.</summary>
    public static string? LastRejection => Default.LastRejection;

    /// <summary>
    /// Finds ffmpeg.exe: next to the app, on PATH, or in the usual winget locations. Null if missing.
    /// In an elevated process (the runner) only a copy that non-admin users cannot change is returned, because starting
    /// a user-replaceable exe as administrator would hand admin rights to any program the user runs.
    /// </summary>
    public static string? Find() => Default.Find();

    /// <summary>
    /// The copy an elevated process (the background runner) would use, or null. Lets the non-elevated editor tell the user
    /// when FFmpeg exists only in a folder the runner refuses. Not cached.
    /// </summary>
    public static string? FindForElevated() => Default.Search(requireAdminOnly: true).Path;

    /// <summary>Forget the cached lookup (e.g. right after installing).</summary>
    public static void Reset() => Default.Reset();

    internal static string RejectionMessage(string path, string reason) =>
        $"FFmpeg at {path} is not used: {reason}. The runner runs as administrator, so it only starts FFmpeg " +
        "from a folder that only administrators can change (install it for all users).";

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
            foreach (var f in WingetPackageFiles(Path.Combine(root, "WinGet", "Packages"))) yield return f;
        }
    }

    /// <summary>ffmpeg.exe files inside winget's <c>*FFmpeg*</c> package folders under <paramref name="packagesRoot"/>.</summary>
    internal static List<string> WingetPackageFiles(string packagesRoot)
    {
        var found = new List<string>();
        try
        {
            if (Directory.Exists(packagesRoot))
                foreach (var p in Directory.EnumerateDirectories(packagesRoot, "*FFmpeg*"))
                    try { found.AddRange(Directory.EnumerateFiles(p, "ffmpeg.exe", SearchOption.AllDirectories)); } catch { }
        }
        catch { }
        return found;
    }
}

/// <summary>The result of one search: the path to start (or null) and every candidate that was refused, with why.</summary>
public sealed record FfmpegSearchResult(string? Path, IReadOnlyList<(string Path, string Reason)> Rejections);

/// <summary>
/// The ffmpeg lookup with its inputs injected (elevation, candidates, file existence, trust check), so the switch that
/// turns the admin-only rule on can be tested. <see cref="Ffmpeg"/> holds the real one.
/// </summary>
internal sealed class FfmpegFinder
{
    private readonly Func<bool> _isElevated;
    private readonly Func<IEnumerable<string>> _candidates;
    private readonly Func<string, bool> _exists;
    private readonly Func<string, TrustVerdict> _check;
    private readonly object _gate = new();
    private readonly HashSet<string> _reported = new(StringComparer.OrdinalIgnoreCase);
    private string? _cached;
    private DateTime _checked;

    public FfmpegFinder(Func<bool> isElevated, Func<IEnumerable<string>> candidates, Func<string, bool> exists, Func<string, TrustVerdict> check)
    {
        _isElevated = isElevated; _candidates = candidates; _exists = exists; _check = check;
    }

    public event Action<string>? Rejected;

    public string? LastRejection { get; private set; }

    public string? Find()
    {
        lock (_gate)
        {
            if (_cached is not null && _exists(_cached)) return _cached;
            if (_cached is null && DateTime.UtcNow - _checked < TimeSpan.FromSeconds(5)) return null; // don't hammer the disk
            _checked = DateTime.UtcNow;
            LastRejection = null;
            var result = Search(_isElevated());
            foreach (var (path, reason) in result.Rejections)
            {
                var message = Ffmpeg.RejectionMessage(path, reason);
                LastRejection = message;
                if (_reported.Add(path + "|" + reason))
                    try { Rejected?.Invoke(message); } catch { /* logging must never break the search */ }
            }
            return _cached = result.Path;
        }
    }

    public FfmpegSearchResult Search(bool requireAdminOnly)
    {
        var rejections = new List<(string, string)>();
        foreach (var candidate in _candidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_exists(candidate)) continue;
            if (!requireAdminOnly) return new(candidate, rejections);
            var verdict = _check(candidate);
            if (verdict.Trusted) return new(candidate, rejections);
            rejections.Add((candidate, verdict.Reason));
        }
        return new(null, rejections);
    }

    public void Reset() { lock (_gate) { _cached = null; _checked = default; } }
}

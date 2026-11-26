namespace Glance.Core.Themes;

/// <summary>
/// Saves the editor's theme without holding the UI thread while pictures and videos are copied (review M7).
/// <list type="number">
/// <item><see cref="Begin"/> (UI thread): takes a detached copy of the theme, so nothing else reads the live one off the UI thread.</item>
/// <item><see cref="Run"/> (any thread): <see cref="Theme.Save"/> on the copy: copies the files and writes theme.json.</item>
/// <item><see cref="Apply"/> (UI thread): gives the live theme the new file paths and folder, as <see cref="Theme.Save"/>
/// would have. A path the user changed while the save ran is left as the user set it, and
/// <see cref="ChangedWhileSaving"/> says the file does not have those edits yet.</item>
/// </list>
/// </summary>
public sealed class ThemeSaveJob
{
    private readonly Theme _live;
    private readonly Theme _copy;
    private readonly List<Widget> _liveWidgets;
    private readonly List<(string Image, string Font)> _before;
    private bool? _moved;

    private ThemeSaveJob(Theme live, string path)
    {
        Path = path;
        _live = live;
        SavedFrom = live.ToSnapshot();
        _copy = new Theme { BaseFolder = live.BaseFolder, ActivePageIndex = live.ActivePageIndex };
        _copy.ApplySnapshot(SavedFrom);
        _liveWidgets = live.Pages.SelectMany(p => p.Widgets).ToList();
        _before = _liveWidgets.Select(w => (w.ImagePath, w.FontFile)).ToList();
        if (_copy.Pages.SelectMany(p => p.Widgets).Count() != _liveWidgets.Count)
            throw new InvalidOperationException("The theme changed while it was being copied for saving.");
    }

    /// <summary>The file being written.</summary>
    public string Path { get; }

    /// <summary>The live theme as it was when the save began.</summary>
    public string SavedFrom { get; }

    /// <summary>True once <see cref="Apply"/> found edits made after <see cref="Begin"/>; they are not in the file yet.</summary>
    public bool ChangedWhileSaving { get; private set; }

    /// <summary>After <see cref="Apply"/>: the theme moved to another folder (see <see cref="Theme.Save"/>).</summary>
    public bool Moved { get; private set; }

    /// <summary>The saved state (with the new paths), for the undo history's baseline. Valid after <see cref="Run"/>.</summary>
    public string SavedSnapshot => _copy.ToSnapshot();

    /// <summary>On the UI thread: copy what will be saved. Cheap (one JSON round trip).</summary>
    public static ThemeSaveJob Begin(Theme live, string path) => new(live, path);

    /// <summary>Off the UI thread: copies the files and writes the theme. Returns <see cref="Theme.Save"/>'s "moved" result.</summary>
    public bool Run()
    {
        _moved = _copy.Save(Path);
        return _moved.Value;
    }

    /// <summary>On the UI thread, after <see cref="Run"/> succeeded: the live theme takes the new paths and folder.</summary>
    /// <returns>True when the theme moved to another folder (see <see cref="Theme.Save"/>).</returns>
    public bool Apply()
    {
        if (_moved is not { } moved) throw new InvalidOperationException("Run did not finish.");
        var saved = _copy.Pages.SelectMany(p => p.Widgets).ToList();
        for (var i = 0; i < _liveWidgets.Count; i++)
        {
            var w = _liveWidgets[i];
            if (w.ImagePath == _before[i].Image) w.ImagePath = saved[i].ImagePath;
            if (w.FontFile == _before[i].Font) w.FontFile = saved[i].FontFile;
        }
        _live.BaseFolder = _copy.BaseFolder;
        ChangedWhileSaving = _live.ToSnapshot() != _copy.ToSnapshot();
        Moved = moved;
        return moved;
    }

    /// <summary>Begin on the caller's thread, run on the thread pool, apply back on the caller's (UI) thread.</summary>
    public static async Task<ThemeSaveJob> SaveAsync(Theme live, string path)
    {
        var job = Begin(live, path);
        await Task.Run(job.Run);
        job.Apply();   // the await came back to the caller's synchronization context
        return job;
    }
}

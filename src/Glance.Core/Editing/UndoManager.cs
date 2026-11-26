namespace Glance.Core.Editing;

/// <summary>Snapshot-based undo/redo. Snapshots are whole-theme JSON strings, which keeps every kind of edit undoable the same way.</summary>
public sealed class UndoManager
{
    private const int Limit = 200;
    private readonly List<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string _current;

    public UndoManager(string initial) => _current = initial;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Forget all history and start from a new baseline (new/open/import).</summary>
    public void Reset(string snapshot) { _undo.Clear(); _redo.Clear(); _current = snapshot; }

    /// <summary>Change the baseline without creating an undo step (e.g. save normalising image paths).</summary>
    public void Rebase(string snapshot) => _current = snapshot;

    /// <summary>
    /// Call after a save with the saved snapshot and whether the theme moved to another folder.
    /// A plain save only rebases (saving rewrites paths, which is not an undo step). After Save As to another folder
    /// the older snapshots hold paths relative to the old folder, which may not exist in the new one (a deleted
    /// widget's picture was never copied, or a file was renamed to avoid a clash), so the history is cleared.
    /// </summary>
    public void AfterSave(string snapshot, bool themeMoved)
    {
        if (themeMoved) Reset(snapshot); else Rebase(snapshot);
    }

    /// <summary>Records a new state. Returns true if it differed from the last one.</summary>
    public bool Commit(string snapshot)
    {
        if (snapshot == _current) return false;
        _undo.Add(_current);
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        _redo.Clear();
        _current = snapshot;
        return true;
    }

    public string? Undo()
    {
        if (_undo.Count == 0) return null;
        _redo.Push(_current);
        _current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return _current;
    }

    public string? Redo()
    {
        if (_redo.Count == 0) return null;
        _undo.Add(_current);
        _current = _redo.Pop();
        return _current;
    }
}

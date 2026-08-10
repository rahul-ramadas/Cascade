namespace Cascade.Core.Model;

/// <summary>
/// Undo/redo for structural filter edits.
///
/// Snapshots, not commands: the tree is small enough that cloning it is free, cloning keeps every id (so
/// the list's expansion state and the match cache's predicate chains survive a restore), and there is then
/// no way for an inverse operation to disagree with the operation it is undoing.
///
/// <see cref="Begin"/> may be called speculatively - before a dialog the user may cancel, or before a move
/// that may turn out to be illegal - because <see cref="Commit"/> keeps the snapshot only when the tree
/// really did change. Enabling and disabling filters is excluded by that same comparison (see
/// <see cref="FilterCollection.SameStructure"/>), which is what keeps toggling out of the history.
/// </summary>
public sealed class FilterHistory
{
    public const int MaxEntries = 100;

    private readonly List<Entry> _undo = new();
    private readonly List<Entry> _redo = new();
    private Entry? _pending;

    private sealed record Entry(string Label, List<Filter> Roots);

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>What <see cref="Undo"/> would take back, for the menu ("Undo Remove Filter").</summary>
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    public int Count => _undo.Count;

    /// <summary>The tree as it stood when <see cref="Begin"/> was called, or null when no edit is under way.
    /// Lets the caller ask what an edit actually changed before <see cref="Commit"/> disposes of it.</summary>
    public IReadOnlyList<Filter>? PendingRoots => _pending?.Roots;

    /// <summary>Records the state an edit is about to change.</summary>
    public void Begin(string label, FilterCollection filters) => _pending = new Entry(label, filters.CloneRoots());

    /// <summary>Keeps the pending snapshot if the tree actually changed, and drops it otherwise. Calling
    /// this with nothing pending - which every enable/disable change does - is a no-op.</summary>
    public void Commit(FilterCollection filters)
    {
        var pending = _pending;
        _pending = null;
        if (pending is null || FilterCollection.SameStructure(pending.Roots, filters.Roots)) return;
        Push(_undo, pending);
        _redo.Clear();
    }

    /// <summary>Throws away a pending snapshot: the edit did not happen.</summary>
    public void Abandon() => _pending = null;

    /// <summary>Restores the previous tree into <paramref name="filters"/>, returning what was undone.</summary>
    public string? Undo(FilterCollection filters) => Step(_undo, _redo, filters);

    public string? Redo(FilterCollection filters) => Step(_redo, _undo, filters);

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _pending = null;
    }

    private static string? Step(List<Entry> from, List<Entry> to, FilterCollection filters)
    {
        if (from.Count == 0) return null;
        var entry = from[^1];
        from.RemoveAt(from.Count - 1);
        Push(to, new Entry(entry.Label, filters.CloneRoots()));
        filters.ReplaceRoots(entry.Roots);
        return entry.Label;
    }

    private static void Push(List<Entry> stack, Entry entry)
    {
        stack.Add(entry);
        if (stack.Count > MaxEntries) stack.RemoveRange(0, stack.Count - MaxEntries);
    }
}

namespace Cascade.Core.Model;

/// <summary>The tree of filters plus collection-level state. Provides tree edits used by the UI
/// (add, remove, move for drag-and-drop reorder/nest) with depth and cycle validation.</summary>
public sealed class FilterCollection
{
    /// <summary>Maximum nesting levels (depths 0..MaxDepth-1).</summary>
    public const int MaxDepth = 8;

    public List<Filter> Roots { get; } = new();

    public bool ShowOnlyFilteredLines { get; set; }

    public IReadOnlyList<Filter> ChildrenOf(Filter? parent) => parent?.Children ?? (IReadOnlyList<Filter>)Roots;

    /// <summary>A deep copy of the whole tree that keeps every filter's id, so it can be put back and
    /// anything keyed on identity - the tree's expansion state, the match cache's predicate chains - still
    /// lines up. Used by undo/redo.</summary>
    public List<Filter> CloneRoots() => Roots.Select(f => f.Clone(newIds: false)).ToList();

    /// <summary>Replaces the whole tree (the other half of <see cref="CloneRoots"/>). Collection-level state
    /// such as <see cref="ShowOnlyFilteredLines"/> and the presets are deliberately left alone.</summary>
    public void ReplaceRoots(IEnumerable<Filter> roots)
    {
        Roots.Clear();
        foreach (var f in roots)
        {
            f.Parent = null;
            Roots.Add(f);
        }
    }

    /// <summary>Whether two trees agree on everything an edit can change.
    /// <see cref="Filter.Enabled"/> is deliberately excluded: turning a filter on and off is not an edit and
    /// must never land on the undo stack, so comparing without it means a path that forgets that rule still
    /// cannot record one.</summary>
    public static bool SameStructure(IReadOnlyList<Filter> a, IReadOnlyList<Filter> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            Filter x = a[i], y = b[i];
            if (!SamePredicate(x, y) || x.Description != y.Description) return false;
            if (x.Style.Foreground != y.Style.Foreground || x.Style.Background != y.Style.Background ||
                x.Style.Bold != y.Style.Bold || x.Style.Italic != y.Style.Italic ||
                x.Style.Underline != y.Style.Underline) return false;
            if (!SameStructure(x.Children, y.Children)) return false;
        }
        return true;
    }

    /// <summary>Whether two trees would filter identically: the same filters in the same places, each with
    /// the same predicate, kind and enabled state.
    ///
    /// Style and description are excluded because nothing in evaluation reads them - the view resolves a
    /// line's colour from the live filter every time it paints. That is what lets an edit that changed only
    /// how filters look skip the whole filtering pipeline instead of restarting a pass over the file.</summary>
    public static bool SameMatching(IReadOnlyList<Filter> a, IReadOnlyList<Filter> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            Filter x = a[i], y = b[i];
            if (!SamePredicate(x, y) || x.Enabled != y.Enabled) return false;
            if (!SameMatching(x.Children, y.Children)) return false;
        }
        return true;
    }

    /// <summary>Everything about one filter that decides which lines it claims. Shared so that a field added
    /// to <see cref="FilterMatch"/> later cannot be remembered by one of the two comparisons and forgotten
    /// by the other.</summary>
    private static bool SamePredicate(Filter x, Filter y) =>
        x.Id == y.Id && x.Kind == y.Kind &&
        x.Match.Type == y.Match.Type && x.Match.Text == y.Match.Text &&
        x.Match.CaseSensitive == y.Match.CaseSensitive && x.Match.Regex == y.Match.Regex &&
        x.Match.MarkerIndex == y.Match.MarkerIndex;

    public void Add(Filter filter, Filter? parent = null, int index = -1)
    {
        filter.Parent = parent;
        var list = parent?.Children ?? Roots;
        if (index < 0 || index > list.Count) list.Add(filter);
        else list.Insert(index, filter);
    }

    public void Remove(Filter filter)
    {
        var list = filter.Parent?.Children ?? Roots;
        list.Remove(filter);
        filter.Parent = null;
    }

    // ---- operating on several filters at once ----

    /// <summary>The filters in <paramref name="chosen"/> that no other member already carries: a filter
    /// whose ancestor is also chosen is dropped, because every structural operation - remove, move,
    /// duplicate - takes a filter's whole subtree with it, so naming both would do the work twice.
    /// The order they were given in is kept, which is the list order the caller reads them off the tree in.</summary>
    public static List<Filter> SelectionRoots(IEnumerable<Filter> chosen)
    {
        var all = chosen as IReadOnlyCollection<Filter> ?? chosen.ToList();
        var set = new HashSet<Filter>(all);   // Filter does not override Equals, so this is identity
        return all.Where(f => !HasChosenAncestor(f, set)).ToList();
    }

    private static bool HasChosenAncestor(Filter f, HashSet<Filter> chosen)
    {
        for (var p = f.Parent; p is not null; p = p.Parent)
            if (chosen.Contains(p)) return true;
        return false;
    }

    /// <summary>Removes every filter in <paramref name="chosen"/>, subtrees and all. One call so the caller
    /// makes one undo entry rather than one per filter.</summary>
    public void RemoveMany(IEnumerable<Filter> chosen)
    {
        foreach (var f in SelectionRoots(chosen)) Remove(f);
    }

    /// <summary>Moves every filter in <paramref name="chosen"/> under <paramref name="newParent"/>, landing
    /// them consecutively from <paramref name="index"/> in the order given.
    ///
    /// All or nothing: if any one of them could not be moved there - a cycle, or a subtree too tall for the
    /// depth limit - none of them are, so a refused drop leaves the tree exactly as it was rather than half
    /// rearranged.</summary>
    public bool MoveMany(IEnumerable<Filter> chosen, Filter? newParent, int index)
    {
        var roots = SelectionRoots(chosen);
        if (roots.Count == 0) return false;
        if (roots.Any(f => !CanMove(f, newParent))) return false;

        // Dropping into a filter that is itself being moved would take the whole group with it.
        if (newParent is not null && roots.Any(f => ReferenceEquals(f, newParent) || f.IsAncestorOf(newParent))) return false;

        var list = newParent?.Children ?? Roots;
        int at = Math.Clamp(index, 0, list.Count);
        bool moved = false;
        foreach (var f in roots)
        {
            // Move reads its index in the list as it stands before the filter is taken out of it, so the
            // slot just after the one that landed last is the right thing to ask for either way.
            if (!Move(f, newParent, at)) continue;
            at = list.IndexOf(f) + 1;
            moved = true;
        }
        return moved;
    }

    /// <summary>True if <paramref name="filter"/> may be moved under <paramref name="newParent"/>
    /// without creating a cycle or exceeding <see cref="MaxDepth"/>.</summary>
    public static bool CanMove(Filter filter, Filter? newParent)
    {
        if (newParent is null) return true;
        if (ReferenceEquals(filter, newParent)) return false;
        if (filter.IsAncestorOf(newParent)) return false; // would create a cycle
        int newDepth = newParent.Depth + 1;
        int deepest = newDepth + SubtreeHeight(filter);
        return deepest <= MaxDepth - 1;
    }

    /// <summary>Moves <paramref name="filter"/> to be a child of <paramref name="newParent"/>
    /// (or a root when null) at <paramref name="index"/>. Returns false if the move is invalid.</summary>
    public bool Move(Filter filter, Filter? newParent, int index)
    {
        if (!CanMove(filter, newParent)) return false;

        var oldList = filter.Parent?.Children ?? Roots;
        int oldIndex = oldList.IndexOf(filter);

        // Adjust target index if moving within the same list to the right of the old position.
        var newList = newParent?.Children ?? Roots;
        if (ReferenceEquals(oldList, newList) && oldIndex >= 0 && index > oldIndex) index--;

        oldList.RemoveAt(oldIndex);
        filter.Parent = newParent;
        if (index < 0 || index > newList.Count) newList.Add(filter);
        else newList.Insert(index, filter);
        return true;
    }

    /// <summary>Moves <paramref name="filter"/> one slot towards the start (-1) or end (+1) of its own
    /// sibling list. Returns false when it is already at that end.</summary>
    public bool Reorder(Filter filter, int delta)
    {
        var list = filter.Parent?.Children ?? Roots;
        int from = list.IndexOf(filter);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= list.Count) return false;
        list.RemoveAt(from);
        list.Insert(to, filter);
        return true;
    }

    /// <summary>Nests <paramref name="filter"/> under the sibling directly above it, as its last child.
    /// Returns false when nothing sits above it at this level, or the extra level would exceed
    /// <see cref="MaxDepth"/>.</summary>
    public bool Indent(Filter filter)
    {
        var list = filter.Parent?.Children ?? Roots;
        int i = list.IndexOf(filter);
        if (i <= 0) return false;
        var newParent = list[i - 1];
        return Move(filter, newParent, newParent.Children.Count);
    }

    /// <summary>Moves <paramref name="filter"/> out one level, placing it directly after its former parent.
    /// Returns false when it is already at the top level.</summary>
    public bool Outdent(Filter filter)
    {
        var parent = filter.Parent;
        if (parent is null) return false;
        var above = parent.Parent?.Children ?? Roots;
        return Move(filter, parent.Parent, above.IndexOf(parent) + 1);
    }

    public IEnumerable<Filter> EnumerateDepthFirst()
    {
        foreach (var root in Roots)
            foreach (var f in Walk(root))
                yield return f;
    }

    // ---- presets ----

    /// <summary>Named sets of filters to switch on together. Saved with the filters, since that is what
    /// they refer to.</summary>
    public List<FilterPreset> Presets { get; } = new();

    public Filter? FindById(string id) => EnumerateDepthFirst().FirstOrDefault(f => f.Id == id);

    /// <summary>How many of a preset's filters no longer exist, so the list can say so.</summary>
    public int MissingCount(FilterPreset preset) => preset.FilterIds.Count(id => FindById(id) is null);

    /// <summary>Whether a preset is currently in effect: every filter it names that still exists is
    /// enabled. Derived rather than stored, so ticking a filter by hand lights the preset up (or clears it)
    /// exactly as applying it would. A preset naming nothing that exists is never active.</summary>
    public bool IsPresetActive(FilterPreset preset)
    {
        bool any = false;
        foreach (var id in preset.FilterIds)
        {
            if (FindById(id) is not { } f) continue;
            if (!f.Enabled) return false;
            any = true;
        }
        return any;
    }

    /// <summary>A preset capturing exactly what is enabled right now. Ancestors are not added: a parent's
    /// pattern constrains its children whether or not the parent is enabled, so "parent off, children on"
    /// is a real arrangement and must be reproduced faithfully.</summary>
    public FilterPreset CaptureEnabled(string name)
        => new(name, EnumerateDepthFirst().Where(f => f.Enabled).Select(f => f.Id));

    /// <summary>Switches every filter a preset names on, or off, and leaves every other filter exactly as
    /// it was. A preset says which filters belong to it and nothing whatever about the rest, so putting one
    /// in or out of effect must not disturb a filter the user turned on by hand - nor one that belongs to
    /// another preset and is only being shared.
    ///
    /// Returns whether that changed anything, so a tick that lands on filters already in that state costs
    /// nothing - re-running a pass over a multi-gigabyte file to arrive back where it started is a visible
    /// flicker of the progress bar and a lot of work for no answer.</summary>
    public bool SetPresetEnabled(FilterPreset preset, bool on)
    {
        var ids = new HashSet<string>(preset.FilterIds, StringComparer.Ordinal);
        bool changed = false;
        foreach (var f in EnumerateDepthFirst())
        {
            if (!ids.Contains(f.Id) || f.Enabled == on) continue;
            f.Enabled = on;
            changed = true;
        }
        return changed;
    }

    /// <summary>Switches on exactly the union of the given presets, and everything else off - so a single
    /// preset means "just this and nothing else". That is what <i>Apply Only This Preset</i> is for; ticking
    /// and unticking go through <see cref="SetPresetEnabled"/>, which leaves filters outside the preset
    /// alone.
    ///
    /// Returns whether that changed anything, for the same reason as above.</summary>
    public bool ApplyPresets(IEnumerable<FilterPreset> presets)
    {
        var wanted = new HashSet<string>(presets.SelectMany(p => p.FilterIds), StringComparer.Ordinal);
        bool changed = false;
        foreach (var f in EnumerateDepthFirst())
        {
            bool on = wanted.Contains(f.Id);
            if (f.Enabled == on) continue;
            f.Enabled = on;
            changed = true;
        }
        return changed;
    }

    private static IEnumerable<Filter> Walk(Filter f)
    {
        yield return f;
        foreach (var child in f.Children)
            foreach (var d in Walk(child))
                yield return d;
    }

    private static int SubtreeHeight(Filter f)
    {
        int h = 0;
        foreach (var c in f.Children) h = Math.Max(h, 1 + SubtreeHeight(c));
        return h;
    }

    public int EnabledCount => EnumerateDepthFirst().Count(f => f.Enabled);
}

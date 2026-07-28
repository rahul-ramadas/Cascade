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

    /// <summary>True if <paramref name="filter"/> may be moved under <paramref name="newParent"/>
    /// without creating a cycle or exceeding <see cref="MaxDepth"/>.</summary>
    public bool CanMove(Filter filter, Filter? newParent)
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

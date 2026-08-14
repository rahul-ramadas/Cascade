using Cascade.Core.Markers;

namespace Cascade.Core.Filtering;

/// <summary>
/// The filters that answer for a line, fixed at the moment this was taken. <b>Take one and hold it for a
/// whole frame</b>, rather than asking the document per row.
/// <para>Which filters answer depends on whether a filtering pass is still running, and that changes the
/// instant the pass finishes. A frame asks the visible set for its rows once, so the rows it is about to
/// paint belong to the filters that were in force then; if it then read the rule again per row, a pass
/// ending half way down the screen would leave the rest of the rows answered by filters that do not show
/// them - which is to say drawn as plain unfiltered text, in the middle of a coloured screen.</para>
/// </summary>
public readonly struct LineColouring
{
    private readonly FilterSnapshot _filters;
    private readonly FilterSnapshot[]? _previous;
    private readonly MarkerStore? _markers;

    internal LineColouring(FilterSnapshot filters, FilterSnapshot[]? previous, MarkerStore? markers)
    {
        _filters = filters;
        _previous = previous;
        _markers = markers;
    }

    /// <summary>The filters in force.</summary>
    internal FilterSnapshot Filters => _filters;

    /// <summary>The filters the visible set may still be showing rows from, newest first, or <c>null</c>
    /// when it has caught up with <see cref="Filters"/> and so nothing being shown can need them.</summary>
    internal FilterSnapshot[]? Previous => _previous;

    /// <summary>Whether a line is shown, and which filter gives it its colour.</summary>
    public LineEval Evaluate(ReadOnlySpan<char> text, long line)
    {
        var eval = _filters.Evaluate(text, line, _markers);
        if (eval.Shown || _previous is null) return eval;
        foreach (var was in _previous)
        {
            var older = was.Evaluate(text, line, _markers);
            if (older.Shown) return older;
        }
        return eval;
    }
}

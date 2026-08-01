using Cascade.Core.Find;

namespace Cascade.Core.Filtering;

/// <summary>
/// The ordered set of line numbers currently visible (passing the filters). Two shapes:
/// an <b>identity</b> view (no filtering — row N maps to line N) or an <b>explicit</b> view backed by a
/// <see cref="VisibleLineSet"/> that the streaming filter updates in place as it re-evaluates each line.
/// </summary>
public sealed class FilteredView
{
    private readonly VisibleLineSet? _set;
    private readonly Func<long>? _identityCount;

    private FilteredView(VisibleLineSet? set, Func<long>? identityCount)
    {
        _set = set;
        _identityCount = identityCount;
    }

    public static FilteredView CreateExplicit(VisibleLineSet set) => new(set, null);

    public static FilteredView CreateIdentity(Func<long> count) => new(null, count);

    public bool IsIdentity => _set is null;

    /// <summary>Number of visible rows (grows while filtering/indexing stream).</summary>
    public long Count => IsIdentity ? _identityCount!() : _set!.Count;

    /// <summary>File lines this view has meaningful results for; beyond it nothing has been evaluated yet.</summary>
    public long KnownLines => IsIdentity ? _identityCount!() : _set!.KnownLines;

    /// <summary>Maps a visible row to its original file line number.</summary>
    public long LineAt(long row) => IsIdentity ? row : _set!.LineAt(row);

    /// <summary>Maps a file line number to its visible row, or -1 if that line is not visible.</summary>
    public long RowForLine(long line)
        => IsIdentity ? (line >= 0 && line < Count ? line : -1) : _set!.RowForLine(line);

    /// <summary>True when this view is currently showing <paramref name="line"/>.</summary>
    public bool IsVisible(long line)
        => IsIdentity ? line >= 0 && line < Count : _set!.IsVisible(line);

    /// <summary>Reads visibility 64 lines to a word. Null when everything is visible, which lets a caller
    /// skip the intersection entirely rather than ask about lines that cannot be hidden.</summary>
    public VisibleWordReader? VisibleWords => IsIdentity ? null : _set!.CopyVisibleWords;

    /// <summary>How many visible lines fall in <c>[from, toExclusive)</c>.</summary>
    public long CountInRange(long from, long toExclusive)
        => IsIdentity ? Math.Max(0, Math.Min(toExclusive, Count) - Math.Max(0, from))
                      : _set!.CountInRange(from, toExclusive);

    /// <summary>Row of the nearest visible line at or after <paramref name="line"/> (for preserving
    /// the current position when toggling filtered mode). Returns Count if none.</summary>
    public long RowAtOrAfterLine(long line)
        => IsIdentity ? Math.Clamp(line, 0, Count) : _set!.RowAtOrAfterLine(line);

    /// <summary>Resolves one whole screen against a single consistent snapshot, anchoring
    /// <paramref name="anchorLine"/> at <paramref name="anchorOffset"/> rows from the top. Returns the first row.</summary>
    public long ResolveWindow(long anchorLine, int anchorOffset, Span<long> lines, out int count)
    {
        if (!IsIdentity) return _set!.ResolveWindow(anchorLine, anchorOffset, lines, out count);
        long first = Math.Clamp(anchorLine - anchorOffset, 0, Math.Max(0, Count - lines.Length));
        count = FillIdentity(first, lines);
        return first;
    }

    /// <summary>Fills <paramref name="lines"/> with the file lines shown from <paramref name="firstRow"/> on,
    /// resolved against a single snapshot. Returns how many were filled.</summary>
    public int LinesForRows(long firstRow, Span<long> lines)
        => IsIdentity ? FillIdentity(Math.Max(0, firstRow), lines) : _set!.LinesForRows(firstRow, lines);

    private int FillIdentity(long firstRow, Span<long> lines)
    {
        int count = (int)Math.Clamp(Count - firstRow, 0, lines.Length);
        for (int i = 0; i < count; i++) lines[i] = firstRow + i;
        return count;
    }
}

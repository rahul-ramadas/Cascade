namespace Cascade.Core.Filtering;

/// <summary>
/// The ordered set of line numbers currently visible (passing the filters). Two shapes:
/// an <b>identity</b> view (no filtering — row N maps to line N) or an <b>explicit</b> view that
/// grows as the streaming filter appends matching line numbers in order.
/// </summary>
public sealed class FilteredView
{
    private readonly PagedLongList? _lines;
    private readonly Func<long>? _identityCount;

    private FilteredView(PagedLongList? lines, Func<long>? identityCount)
    {
        _lines = lines;
        _identityCount = identityCount;
    }

    public static FilteredView CreateExplicit() => new(new PagedLongList(), null);

    public static FilteredView CreateIdentity(Func<long> count) => new(null, count);

    public bool IsIdentity => _lines is null;

    /// <summary>Number of visible rows (grows while filtering/indexing stream).</summary>
    public long Count => IsIdentity ? _identityCount!() : _lines!.Count;

    /// <summary>Maps a visible row to its original file line number.</summary>
    public long LineAt(long row) => IsIdentity ? row : _lines!.Get(row);

    internal void Append(long line) => _lines!.Add(line);

    /// <summary>Maps a file line number to its visible row, or -1 if that line is not visible.
    /// Uses a binary search over the (sorted, ascending) explicit list.</summary>
    public long RowForLine(long line)
    {
        if (IsIdentity)
            return line >= 0 && line < Count ? line : -1;

        long lo = 0, hi = _lines!.Count - 1;
        while (lo <= hi)
        {
            long mid = (lo + hi) >> 1;
            long v = _lines.Get(mid);
            if (v == line) return mid;
            if (v < line) lo = mid + 1;
            else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>Row of the nearest visible line at or after <paramref name="line"/> (for preserving
    /// the current position when toggling filtered mode). Returns Count if none.</summary>
    public long RowAtOrAfterLine(long line)
    {
        if (IsIdentity) return Math.Clamp(line, 0, Count);
        long lo = 0, hi = _lines!.Count;
        while (lo < hi)
        {
            long mid = (lo + hi) >> 1;
            if (_lines.Get(mid) < line) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}

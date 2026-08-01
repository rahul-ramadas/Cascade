namespace Cascade.App;

/// <summary>Row selection stored as normalized, disjoint, ascending inclusive ranges so that even
/// "select all" over millions of rows is O(1) memory. Rows are display rows (selection is cleared
/// when the view changes).</summary>
public sealed class RowSelection
{
    private readonly List<(long A, long B)> _ranges = new();

    public long Anchor { get; private set; } = -1;

    /// <summary>Bumped by every change. Anything that summarises the selection keys its cache on this rather
    /// than walking the ranges to find out whether they moved.</summary>
    public int Version { get; private set; }

    public void Clear() { _ranges.Clear(); Anchor = -1; Version++; }

    public long Count
    {
        get { long n = 0; foreach (var (a, b) in _ranges) n += b - a + 1; return n; }
    }

    public bool Contains(long row)
    {
        foreach (var (a, b) in _ranges)
            if (row >= a && row <= b) return true;
        return false;
    }

    /// <summary>Whether anything in <c>[from, toExclusive)</c> is selected. A summary that stands for many
    /// rows in one pixel has to ask about the whole span, not the one row it happens to name.</summary>
    public bool IntersectsRange(long from, long toExclusive)
    {
        foreach (var (a, b) in _ranges)
            if (a < toExclusive && b >= from) return true;
        return false;
    }

    public void SetSingle(long row)
    {
        _ranges.Clear();
        if (row >= 0) _ranges.Add((row, row));
        Anchor = row;
        Version++;
    }

    public void SetRange(long a, long b)
    {
        if (a < 0 || b < 0) return;
        _ranges.Clear();
        _ranges.Add((Math.Min(a, b), Math.Max(a, b)));
        Anchor = a;
        Version++;
    }

    public void SelectAll(long count)
    {
        _ranges.Clear();
        if (count > 0) _ranges.Add((0, count - 1));
        Anchor = 0;
        Version++;
    }

    public void ToggleSingle(long row)
    {
        if (row < 0) return;
        Anchor = row;
        if (Contains(row)) Remove(row);
        else Add(row);
        Version++;
    }

    private void Add(long row)
    {
        _ranges.Add((row, row));
        Normalize();
    }

    private void Remove(long row)
    {
        var result = new List<(long, long)>();
        foreach (var (a, b) in _ranges)
        {
            if (row < a || row > b) { result.Add((a, b)); continue; }
            if (a < row) result.Add((a, row - 1));
            if (row < b) result.Add((row + 1, b));
        }
        _ranges.Clear();
        _ranges.AddRange(result);
    }

    private void Normalize()
    {
        if (_ranges.Count < 2) return;
        _ranges.Sort((x, y) => x.A.CompareTo(y.A));
        var merged = new List<(long A, long B)> { _ranges[0] };
        for (int i = 1; i < _ranges.Count; i++)
        {
            var last = merged[^1];
            var cur = _ranges[i];
            if (cur.A <= last.B + 1) merged[^1] = (last.A, Math.Max(last.B, cur.B));
            else merged.Add(cur);
        }
        _ranges.Clear();
        _ranges.AddRange(merged);
    }

    public IEnumerable<long> Rows(long cap)
    {
        long n = 0;
        foreach (var (a, b) in _ranges)
            for (long r = a; r <= b; r++)
            {
                if (n++ >= cap) yield break;
                yield return r;
            }
    }
}

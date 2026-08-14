namespace Cascade.App;

/// <summary>
/// What is selected, held as normalized, disjoint, ascending inclusive ranges of <b>file lines</b> - so
/// even "select all" over millions of lines is O(1) memory.
/// <para>Lines, never display rows. A row index says nothing about which text it stands for: the filtering
/// pass adds and drops lines above the viewport, so the row a line sits on changes whenever the filters do
/// (and continuously while a pass streams). Keyed by line, the highlight stays on the text it was put on
/// whatever happens to the view, and a line the filters have hidden simply is not drawn - it comes back
/// where it was if the filter goes.</para>
/// <para>A range is a stretch of the log, so which of the lines in it are on screen is a question for the
/// view: see <see cref="LineGridControl.SelectedCount"/> and the enumeration beside it.</para>
/// </summary>
public sealed class LineSelection
{
    private readonly List<(long A, long B)> _ranges = new();

    public long Anchor { get; private set; } = -1;

    /// <summary>Bumped by every change. Anything that summarises the selection keys its cache on this rather
    /// than walking the ranges to find out whether they moved.</summary>
    public int Version { get; private set; }

    /// <summary>The stretches of the log that are selected, whether or not the view is showing them.</summary>
    public IReadOnlyList<(long A, long B)> Ranges => _ranges;

    public bool IsEmpty => _ranges.Count == 0;

    public void Clear() { _ranges.Clear(); Anchor = -1; Version++; }

    /// <summary>How many lines are spanned, hidden ones included. The count that is shown to the reader is
    /// of the lines the view can actually show, which only the view can answer.</summary>
    public long LineCount
    {
        get { long n = 0; foreach (var (a, b) in _ranges) n += b - a + 1; return n; }
    }

    public bool Contains(long line)
    {
        foreach (var (a, b) in _ranges)
            if (line >= a && line <= b) return true;
        return false;
    }

    public void SetSingle(long line)
    {
        _ranges.Clear();
        if (line >= 0) _ranges.Add((line, line));
        Anchor = line;
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

    public void SelectAll(long lineCount)
    {
        _ranges.Clear();
        if (lineCount > 0) _ranges.Add((0, lineCount - 1));
        Anchor = 0;
        Version++;
    }

    public void ToggleSingle(long line)
    {
        if (line < 0) return;
        Anchor = line;
        if (Contains(line)) Remove(line);
        else Add(line);
        Version++;
    }

    private void Add(long line)
    {
        _ranges.Add((line, line));
        Normalize();
    }

    private void Remove(long line)
    {
        var result = new List<(long, long)>();
        foreach (var (a, b) in _ranges)
        {
            if (line < a || line > b) { result.Add((a, b)); continue; }
            if (a < line) result.Add((a, line - 1));
            if (line < b) result.Add((line + 1, b));
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
}

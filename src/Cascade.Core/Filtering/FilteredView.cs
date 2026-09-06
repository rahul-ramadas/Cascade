using Cascade.Core.Find;

namespace Cascade.Core.Filtering;

/// <summary>
/// The ordered set of line numbers currently visible (passing the filters). Three shapes:
/// an <b>identity</b> view (no filtering — row N maps to line N), an <b>explicit</b> view backed by a
/// <see cref="VisibleLineSet"/> that the streaming filter updates in place as it re-evaluates each line, or a
/// <b>cropped</b> view, which is either of those restricted to one stretch of the file.
/// <para>
/// A crop is a row offset and a count, worked out from the same rank index everything else uses, so it costs
/// nothing to apply and nothing to move: the file is not re-read, re-filtered or copied, and a crop of ten
/// lines costs exactly what a crop of ten million does.
/// </para>
/// </summary>
public sealed class FilteredView
{
    private readonly VisibleLineSet? _set;
    private readonly Func<long>? _identityCount;
    private readonly long _lo, _hiExclusive;   // file lines the crop admits; the whole file when uncropped

    private FilteredView(VisibleLineSet? set, Func<long>? identityCount, long lo, long hiExclusive)
    {
        _set = set;
        _identityCount = identityCount;
        _lo = lo;
        _hiExclusive = hiExclusive;
    }

    public static FilteredView CreateExplicit(VisibleLineSet set) => new(set, null, 0, long.MaxValue);

    public static FilteredView CreateIdentity(Func<long> count) => new(null, count, 0, long.MaxValue);

    public bool IsIdentity => _set is null;

    /// <summary>True when this view shows only part of the file.</summary>
    public bool IsCropped => _lo > 0 || _hiExclusive != long.MaxValue;

    /// <summary>The first file line this view admits, and one past the last.</summary>
    public long CropFrom => _lo;

    public long CropToExclusive => _hiExclusive;

    /// <summary>The same view restricted to the file lines in <c>[lo, hiExclusive)</c>. A crop names an
    /// absolute stretch of the file, so this replaces whatever crop was in force rather than narrowing it
    /// again.</summary>
    public FilteredView Cropped(long lo, long hiExclusive)
    {
        lo = Math.Max(0, lo);
        if (lo <= 0 && hiExclusive >= long.MaxValue) return new(_set, _identityCount, 0, long.MaxValue);
        return new(_set, _identityCount, lo, Math.Max(lo, hiExclusive));
    }

    /// <summary>How many rows the underlying view has, crop or no crop - what a crop is a fraction <i>of</i>.
    /// The one number a cropped view must still be able to give, since the reader is told how much of the file
    /// is being kept out of sight.</summary>
    public long UncroppedCount => IsIdentity ? _identityCount!() : _set!.Count;

    /// <summary>Number of visible rows (grows while filtering/indexing stream).</summary>
    public long Count => IsCropped ? CountInRange(_lo, _hiExclusive) : UncroppedCount;

    /// <summary>File lines this view has meaningful results for; beyond it nothing has been evaluated yet.</summary>
    public long KnownLines => IsIdentity ? _identityCount!() : _set!.KnownLines;

    /// <summary>Rows of the underlying view lying before the crop - what row 0 stands at.</summary>
    private long RowOffset => IsCropped ? RawRowAtOrAfter(_lo) : 0;

    private long RawRowAtOrAfter(long line)
        => IsIdentity ? Math.Clamp(line, 0, _identityCount!()) : _set!.RowAtOrAfterLine(line);

    /// <summary>Maps a visible row to its original file line number. The number is the file's own either way:
    /// a crop moves which rows exist, never what a line is called.</summary>
    public long LineAt(long row)
    {
        if (!IsCropped) return IsIdentity ? row : _set!.LineAt(row);
        long line = IsIdentity ? _lo + Math.Max(0, row) : _set!.LineAt(RowOffset + Math.Max(0, row));
        return Math.Clamp(line, _lo, Math.Max(_lo, _hiExclusive - 1));
    }

    /// <summary>Maps a file line number to its visible row, or -1 if that line is not visible.</summary>
    public long RowForLine(long line)
    {
        if (!InCrop(line)) return -1;
        long row = IsIdentity ? (line < UncroppedCount ? line : -1) : _set!.RowForLine(line);
        return row < 0 ? -1 : row - RowOffset;
    }

    /// <summary>True when this view is currently showing <paramref name="line"/>.</summary>
    public bool IsVisible(long line)
        => InCrop(line) && (IsIdentity ? line < UncroppedCount : _set!.IsVisible(line));

    private bool InCrop(long line) => line >= _lo && line < _hiExclusive;

    /// <summary>Reads visibility 64 lines to a word. Null when everything is visible, which lets a caller
    /// skip the intersection entirely rather than ask about lines that cannot be hidden.
    /// <para>A crop hides lines as surely as a filter does, so a cropped identity view has words to offer
    /// where an uncropped one has none - which is what makes a find tally count only what the crop shows.</para></summary>
    public VisibleWordReader? VisibleWords
    {
        get
        {
            if (!IsCropped) return IsIdentity ? null : _set!.CopyVisibleWords;
            VisibleWordReader? inner = IsIdentity ? null : _set!.CopyVisibleWords;
            return (fromWord, words) =>
            {
                if (inner is null) words.Fill(ulong.MaxValue);
                else inner(fromWord, words);
                for (int i = 0; i < words.Length; i++) words[i] &= CropMask(fromWord + i);
            };
        }
    }

    /// <summary>Which of a word's 64 lines the crop admits.</summary>
    private ulong CropMask(long word)
    {
        long first = word * 64;
        if (first + 64 <= _lo || (_hiExclusive != long.MaxValue && first >= _hiExclusive)) return 0;
        ulong mask = ulong.MaxValue;
        if (_lo > first) mask &= ulong.MaxValue << (int)(_lo - first);
        if (_hiExclusive < first + 64) mask &= ulong.MaxValue >> (int)(first + 64 - _hiExclusive);
        return mask;
    }

    /// <summary>How many visible lines fall in <c>[from, toExclusive)</c>, counting only what the crop shows.</summary>
    public long CountInRange(long from, long toExclusive)
    {
        from = Math.Max(from, _lo);
        toExclusive = Math.Min(toExclusive, _hiExclusive);
        if (toExclusive <= from) return 0;
        return IsIdentity ? Math.Max(0, Math.Min(toExclusive, UncroppedCount) - Math.Max(0, from))
                          : _set!.CountInRange(from, toExclusive);
    }

    /// <summary>Row of the nearest visible line at or after <paramref name="line"/> (for preserving
    /// the current position when toggling filtered mode). Returns Count if none.</summary>
    public long RowAtOrAfterLine(long line)
    {
        if (!IsCropped)
            return IsIdentity ? Math.Clamp(line, 0, UncroppedCount) : _set!.RowAtOrAfterLine(line);
        return Math.Clamp(RawRowAtOrAfter(Math.Clamp(line, _lo, _hiExclusive)) - RowOffset, 0, Count);
    }

    /// <summary>Resolves one whole screen against a single consistent snapshot, anchoring
    /// <paramref name="anchorLine"/> at <paramref name="anchorOffset"/> rows from the top. Returns the first row.</summary>
    public long ResolveWindow(long anchorLine, int anchorOffset, Span<long> lines, out int count)
    {
        if (!IsIdentity)
            return IsCropped
                ? _set!.ResolveWindow(anchorLine, anchorOffset, lines, out count, _lo, _hiExclusive)
                : _set!.ResolveWindow(anchorLine, anchorOffset, lines, out count);

        long rows = Count;
        long anchorRow = Math.Clamp(anchorLine, _lo, _lo + rows) - _lo;
        long first = Math.Clamp(anchorRow - anchorOffset, 0, Math.Max(0, rows - lines.Length));
        count = FillIdentity(first, lines);
        return first;
    }

    /// <summary>Fills <paramref name="lines"/> with the file lines shown from <paramref name="firstRow"/> on,
    /// resolved against a single snapshot. Returns how many were filled.</summary>
    public int LinesForRows(long firstRow, Span<long> lines)
    {
        if (IsIdentity) return FillIdentity(Math.Max(0, firstRow), lines);
        return IsCropped
            ? _set!.LinesForRows(firstRow, lines, _lo, _hiExclusive)
            : _set!.LinesForRows(Math.Max(0, firstRow), lines);
    }

    private int FillIdentity(long firstRow, Span<long> lines)
    {
        int count = (int)Math.Clamp(Count - firstRow, 0, lines.Length);
        for (int i = 0; i < count; i++) lines[i] = _lo + firstRow + i;
        return count;
    }
}

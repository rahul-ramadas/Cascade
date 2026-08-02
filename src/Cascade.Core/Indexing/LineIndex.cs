namespace Cascade.Core.Indexing;

/// <summary>
/// A growable, append-only mapping of line number → start byte offset. Stored as fixed-size pages
/// so growth never reallocates existing data, allowing lock-free reads while a background thread
/// appends.
///
/// Offsets only ever climb, so a page records its first offset once and stores the rest as 32-bit
/// distances from it. That is 4 bytes per line instead of 8 — on a multi-gigabyte log the index is
/// the largest thing the process holds, and the scan that drives filtering reads two offsets for
/// every line, so the narrower page also halves what that pulls through the cache. A page whose
/// lines span more than 4 GB cannot be written that way and widens the index back to plain 64-bit
/// offsets; that needs lines averaging 64 KB, which no ordinary log has.
///
/// Concurrency contract: a single writer calls <see cref="Add"/>. Any number of readers may call
/// <see cref="Get"/>/<see cref="Count"/>; a reader must observe <see cref="Count"/> &gt; i (via the
/// same or a happens-before-related read) before calling <c>Get(i)</c>.
/// </summary>
public sealed class LineIndex
{
    private const int PageBits = 16;
    private const int PageSize = 1 << PageBits;   // 65,536 offsets (256 KB) per page
    private const int PageMask = PageSize - 1;

    private uint[][] _pages;      // distance from the page's own base
    private long[] _bases;        // first offset on each page
    private long[][]? _wide;      // set only after an overflow, and then it is the only truth
    private int _pageCount;
    private long _count;
    private readonly object _growLock = new();

    public LineIndex()
    {
        _pages = new uint[4][];
        _bases = new long[4];
        _pages[0] = new uint[PageSize];
        _pageCount = 1;
    }

    /// <summary>Number of line starts recorded so far (grows during streaming indexing).</summary>
    public long Count => Volatile.Read(ref _count);

    /// <summary>Appends the next line's start offset. Single-writer only, and offsets must not decrease.</summary>
    public void Add(long offset)
    {
        long i = _count;
        int page = (int)(i >> PageBits);
        int slot = (int)(i & PageMask);
        if (page >= _pageCount) EnsurePage(page, offset);

        long[][]? wide = _wide;
        if (wide is null)
        {
            long delta = offset - _bases[page];
            if ((ulong)delta <= uint.MaxValue) _pages[page][slot] = (uint)delta;
            else { Widen(); _wide![page][slot] = offset; }
        }
        else wide[page][slot] = offset;

        Volatile.Write(ref _count, i + 1);
    }

    /// <summary>Start byte offset of line <paramref name="i"/> (must satisfy 0 ≤ i &lt; observed Count).</summary>
    public long Get(long i)
    {
        int page = (int)(i >> PageBits);
        int slot = (int)(i & PageMask);
        long[][]? wide = Volatile.Read(ref _wide);
        if (wide is not null) return wide[page][slot];
        return Volatile.Read(ref _bases)[page] + Volatile.Read(ref _pages)[page][slot];
    }

    /// <summary>Byte range of line <paramref name="i"/>: where it starts, and where the next line starts
    /// (or <paramref name="fileLength"/> for the last one). Every caller wants both, and taking them
    /// together resolves the page once instead of twice — which matters because the filter scan calls this
    /// for every line in the file.</summary>
    public void GetRange(long i, long fileLength, out long start, out long end)
    {
        long count = Volatile.Read(ref _count);   // acquire: publishes the pages written before it
        int page = (int)(i >> PageBits);
        int slot = (int)(i & PageMask);
        bool hasNext = i + 1 < count;

        if (_wide is long[][] wide)
        {
            long[] p = wide[page];
            start = p[slot];
            end = !hasNext ? fileLength
                : slot + 1 < PageSize ? p[slot + 1]
                : wide[page + 1][0];
            return;
        }

        uint[] deltas = _pages[page];
        long b = _bases[page];
        start = b + deltas[slot];
        end = !hasNext ? fileLength
            : slot + 1 < PageSize ? b + deltas[slot + 1]
            : _bases[page + 1] + _pages[page + 1][0];
    }

    private void EnsurePage(int page, long firstOffset)
    {
        lock (_growLock)
        {
            if (page < _pageCount) return;
            if (page >= _pages.Length)
            {
                int newLen = _pages.Length * 2;
                while (page >= newLen) newLen *= 2;

                var grownBases = new long[newLen];
                Array.Copy(_bases, grownBases, _pageCount);
                _bases = grownBases;              // only read after the page slot below is published

                if (_wide is long[][] old)
                {
                    var grownWide = new long[newLen][];
                    Array.Copy(old, grownWide, _pageCount);
                    Volatile.Write(ref _wide, grownWide);
                }
                var grown = new uint[newLen][];
                Array.Copy(_pages, grown, _pageCount);
                Volatile.Write(ref _pages, grown); // publish larger outer array before its slots are used
            }

            _bases[page] = firstOffset;
            if (_wide is long[][] w) Volatile.Write(ref w[page], new long[PageSize]);
            else Volatile.Write(ref _pages[page], new uint[PageSize]);
            _pageCount = page + 1;
        }
    }

    /// <summary>Falls back to 64-bit offsets when a page turns out to span more than 4 GB.</summary>
    private void Widen()
    {
        lock (_growLock)
        {
            if (_wide is not null) return;
            var wide = new long[_pages.Length][];
            for (int p = 0; p < _pageCount; p++)
            {
                uint[] src = _pages[p];
                var dst = new long[PageSize];
                long b = _bases[p];
                for (int k = 0; k < PageSize; k++) dst[k] = b + src[k];
                wide[p] = dst;
            }
            Volatile.Write(ref _wide, wide);
        }
    }
}

namespace Cascade.Core.Indexing;

/// <summary>
/// A growable, append-only mapping of line number → start byte offset. Stored as fixed-size pages
/// so growth never reallocates existing data, allowing lock-free reads while a background thread
/// appends. Costs 8 bytes per line and is independent of the file's byte size.
///
/// Concurrency contract: a single writer calls <see cref="Add"/>. Any number of readers may call
/// <see cref="Get"/>/<see cref="Count"/>; a reader must observe <see cref="Count"/> &gt; i (via the
/// same or a happens-before-related read) before calling <c>Get(i)</c>.
/// </summary>
public sealed class LineIndex
{
    private const int PageBits = 16;
    private const int PageSize = 1 << PageBits;   // 65,536 offsets (512 KB) per page
    private const int PageMask = PageSize - 1;

    private long[][] _pages;
    private int _pageCount;
    private long _count;
    private readonly object _growLock = new();

    public LineIndex()
    {
        _pages = new long[4][];
        _pages[0] = new long[PageSize];
        _pageCount = 1;
    }

    /// <summary>Number of line starts recorded so far (grows during streaming indexing).</summary>
    public long Count => Volatile.Read(ref _count);

    /// <summary>Appends the next line's start offset. Single-writer only.</summary>
    public void Add(long offset)
    {
        long i = _count;
        int page = (int)(i >> PageBits);
        if (page >= _pageCount) EnsurePage(page);
        _pages[page][(int)(i & PageMask)] = offset;
        Volatile.Write(ref _count, i + 1);
    }

    private void EnsurePage(int page)
    {
        lock (_growLock)
        {
            if (page < _pageCount) return;
            if (page >= _pages.Length)
            {
                int newLen = _pages.Length * 2;
                while (page >= newLen) newLen *= 2;
                var grown = new long[newLen][];
                Array.Copy(_pages, grown, _pageCount);
                Volatile.Write(ref _pages, grown);   // publish larger outer array before its slots are used
            }
            Volatile.Write(ref _pages[page], new long[PageSize]); // publish the page before Count exposes it
            _pageCount = page + 1;
        }
    }

    /// <summary>Start byte offset of line <paramref name="i"/> (must satisfy 0 ≤ i &lt; observed Count).</summary>
    public long Get(long i)
    {
        long[][] pages = Volatile.Read(ref _pages);
        return pages[(int)(i >> PageBits)][(int)(i & PageMask)];
    }
}

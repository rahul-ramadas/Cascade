namespace Cascade.Core.Filtering;

/// <summary>Append-only, paged list of longs supporting lock-free reads during single-writer
/// appends (same contract as <see cref="Indexing.LineIndex"/>). Used to hold visible line numbers.</summary>
public sealed class PagedLongList
{
    private const int PageBits = 16;
    private const int PageSize = 1 << PageBits;
    private const int PageMask = PageSize - 1;

    private long[][] _pages;
    private int _pageCount;
    private long _count;
    private readonly object _growLock = new();

    public PagedLongList()
    {
        _pages = new long[4][];
        _pages[0] = new long[PageSize];
        _pageCount = 1;
    }

    public long Count => Volatile.Read(ref _count);

    public void Add(long value)
    {
        long i = _count;
        int page = (int)(i >> PageBits);
        if (page >= _pageCount) EnsurePage(page);
        _pages[page][(int)(i & PageMask)] = value;
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
                Volatile.Write(ref _pages, grown);
            }
            Volatile.Write(ref _pages[page], new long[PageSize]);
            _pageCount = page + 1;
        }
    }

    public long Get(long i)
    {
        long[][] pages = Volatile.Read(ref _pages);
        return pages[(int)(i >> PageBits)][(int)(i & PageMask)];
    }
}

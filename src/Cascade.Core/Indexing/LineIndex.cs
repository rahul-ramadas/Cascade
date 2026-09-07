namespace Cascade.Core.Indexing;

/// <summary>
/// A growable, append-only mapping of line number → start byte offset. Stored as fixed-size pages
/// so growth never reallocates existing data, allowing lock-free reads while a background thread
/// appends.
///
/// Offsets only ever climb, so nothing here stores one whole. A page of 65,536 lines records its own
/// first offset as a <c>long</c>; inside it, each block of 32 lines records its first offset as a 32-bit
/// distance from the page's; and each line records a 16-bit distance from its block's. That is
/// <b>2.125 bytes per line</b>. On a multi-gigabyte log the index is the largest thing the process holds
/// by a wide margin — MEASURED at 265 MB of a 319 MB managed heap on a 15.8 GB log — and the scan that
/// drives filtering reads two offsets for every line, so the narrower page also shrinks what that pulls
/// through the cache.
///
/// A block whose 32 lines span 64 KB or more cannot be written that way, and that page alone falls back
/// to 32-bit distances: 4 bytes a line, which is what every page cost before, so the worst case is merely
/// the old case rather than a doubling. MEASURED over 139 million lines of two real 14-16 GB traces:
/// <b>not one block of 32 overflowed</b>. At 128 lines to a block they begin to, which is why it is 32.
/// A page whose lines span more than 4 GB cannot be written either way and widens the whole index back to
/// plain 64-bit offsets; that needs lines averaging 64 KB, which no ordinary log has.
///
/// Concurrency contract: a single writer calls <see cref="Add"/>. Any number of readers may call
/// <see cref="Get"/>/<see cref="Count"/>; a reader must observe <see cref="Count"/> &gt; i (via the
/// same or a happens-before-related read) before calling <c>Get(i)</c>. Every store a line needs is made
/// before the release write of <see cref="Count"/> that publishes it.
/// </summary>
public sealed class LineIndex
{
    private const int PageBits = 16;
    private const int PageSize = 1 << PageBits;   // 65,536 offsets per page
    private const int PageMask = PageSize - 1;
    private const int BlockBits = 5;
    private const int BlockSize = 1 << BlockBits; // 32 lines share a base
    private const int BlockMask = BlockSize - 1;
    private const int BlocksPerPage = PageSize >> BlockBits;

    private ushort[][] _pages;    // distance from the line's own block base
    private uint[][] _blocks;     // each block's first offset, as a distance from the page's
    private uint[][]? _widePages; // per page: distance from the page base, once a block on it overflowed
    /// <summary>Whether any page has fallen back at all. <see cref="GetRange"/> runs once per line of the
    /// file, so the ordinary path tests one field rather than loading a slot out of an array of nulls.</summary>
    private bool _anyWidePage;
    private long[] _bases;        // first offset on each page
    private long[][]? _wide;      // set only after a page overflows 4 GB, and then it is the only truth
    private int _pageCount;
    private long _count;
    private readonly object _growLock = new();

    public LineIndex()
    {
        _pages = new ushort[4][];
        _blocks = new uint[4][];
        _bases = new long[4];
        _pages[0] = new ushort[PageSize];
        _blocks[0] = new uint[BlocksPerPage];
        _pageCount = 1;
    }

    /// <summary>Number of line starts recorded so far (grows during streaming indexing).</summary>
    public long Count => Volatile.Read(ref _count);

    /// <summary>Bytes of index held per line. Nothing else can notice the shape quietly going back to four
    /// bytes a line: every answer would still be right, and only a heap dump would show it.</summary>
    internal double BytesPerLineForTesting
    {
        get
        {
            if (_wide is not null) return 8;
            double narrow = PageSize * 2 + BlocksPerPage * 4;
            double total = 0;
            for (int p = 0; p < _pageCount; p++)
                total += narrow + (_widePages?[p] is not null ? PageSize * 4 : 0);
            return total / (_pageCount * (double)PageSize);
        }
    }

    /// <summary>How many pages had to fall back to 32-bit distances. Zero on every ordinary log.</summary>
    internal int WidePageCountForTesting
    {
        get
        {
            if (_widePages is not uint[][] w) return 0;
            int n = 0;
            for (int p = 0; p < _pageCount; p++) if (w[p] is not null) n++;
            return n;
        }
    }

    /// <summary>Appends the next line's start offset. Single-writer only, and offsets must not decrease.</summary>
    public void Add(long offset)
    {
        long i = _count;
        int page = (int)(i >> PageBits);
        int slot = (int)(i & PageMask);
        if (page >= _pageCount) EnsurePage(page, offset);

        long[][]? wide = _wide;
        if (wide is not null)
        {
            wide[page][slot] = offset;
            Volatile.Write(ref _count, i + 1);
            return;
        }

        long delta = offset - _bases[page];
        if ((ulong)delta > uint.MaxValue)
        {
            Widen();
            _wide![page][slot] = offset;
            Volatile.Write(ref _count, i + 1);
            return;
        }

        int block = slot >> BlockBits;
        if ((slot & BlockMask) == 0) _blocks[page][block] = (uint)delta;

        uint[]? widePage = _anyWidePage ? _widePages![page] : null;
        if (widePage is not null) widePage[slot] = (uint)delta;
        else
        {
            long inBlock = delta - _blocks[page][block];
            if (inBlock > ushort.MaxValue) WidenPage(page)[slot] = (uint)delta;
            else _pages[page][slot] = (ushort)inBlock;
        }

        Volatile.Write(ref _count, i + 1);
    }

    /// <summary>Start byte offset of line <paramref name="i"/> (must satisfy 0 ≤ i &lt; observed Count).</summary>
    public long Get(long i)
    {
        int page = (int)(i >> PageBits);
        int slot = (int)(i & PageMask);
        long[][]? wide = Volatile.Read(ref _wide);
        if (wide is not null) return wide[page][slot];

        long b = Volatile.Read(ref _bases)[page];
        uint[]? widePage = Volatile.Read(ref _anyWidePage) ? Volatile.Read(ref _widePages)![page] : null;
        if (widePage is not null) return b + widePage[slot];
        return b + Volatile.Read(ref _blocks)[page][slot >> BlockBits] + Volatile.Read(ref _pages)[page][slot];
    }

    /// <summary>Byte range of line <paramref name="i"/>: where it starts, and where the next line starts
    /// (or <paramref name="fileLength"/> for the last one). Every caller wants both, and taking them
    /// together resolves the page, the block and the base once instead of twice — which matters because the
    /// filter scan calls this for every line in the file.</summary>
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

        long b = _bases[page];
        if (_anyWidePage && _widePages![page] is uint[] widePage)
        {
            start = b + widePage[slot];
            end = !hasNext ? fileLength
                : slot + 1 < PageSize ? b + widePage[slot + 1]
                : NextPageStart(page);
            return;
        }

        ushort[] deltas = _pages[page];
        uint[] blocks = _blocks[page];
        long blockBase = b + blocks[slot >> BlockBits];
        start = blockBase + deltas[slot];

        if (!hasNext) { end = fileLength; return; }
        int next = slot + 1;
        if (next >= PageSize) { end = NextPageStart(page); return; }
        // Thirty-one lines out of thirty-two share the block whose base is already in hand.
        end = (next & BlockMask) == 0
            ? b + blocks[next >> BlockBits] + deltas[next]
            : blockBase + deltas[next];
    }

    /// <summary>Where the page after <paramref name="page"/> begins, whatever shape that page is in.</summary>
    private long NextPageStart(int page)
    {
        long b = _bases[page + 1];
        if (_anyWidePage && _widePages![page + 1] is uint[] w) return b + w[0];
        return b + _blocks[page + 1][0] + _pages[page + 1][0];
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
                if (_widePages is uint[][] oldWide)
                {
                    var grownWidePages = new uint[newLen][];
                    Array.Copy(oldWide, grownWidePages, _pageCount);
                    Volatile.Write(ref _widePages, grownWidePages);
                }
                var grownBlocks = new uint[newLen][];
                Array.Copy(_blocks, grownBlocks, _pageCount);
                Volatile.Write(ref _blocks, grownBlocks);

                var grown = new ushort[newLen][];
                Array.Copy(_pages, grown, _pageCount);
                Volatile.Write(ref _pages, grown); // publish larger outer array before its slots are used
            }

            _bases[page] = firstOffset;
            if (_wide is long[][] w) Volatile.Write(ref w[page], new long[PageSize]);
            else
            {
                Volatile.Write(ref _blocks[page], new uint[BlocksPerPage]);
                Volatile.Write(ref _pages[page], new ushort[PageSize]);
            }
            _pageCount = page + 1;
        }
    }

    /// <summary>Falls back to 32-bit distances for ONE page, when a block of 32 lines on it turns out to
    /// span 64 KB or more. That page then costs what every page cost before this shape existed, and the
    /// rest of the index is untouched — one enormous line must not double the whole thing.</summary>
    private uint[] WidenPage(int page)
    {
        lock (_growLock)
        {
            if (_widePages is null) Volatile.Write(ref _widePages, new uint[_pages.Length][]);
            else if (_widePages.Length < _pages.Length)
            {
                var grown = new uint[_pages.Length][];
                Array.Copy(_widePages, grown, _widePages.Length);
                Volatile.Write(ref _widePages, grown);
            }
            if (_widePages![page] is uint[] already) return already;

            // Everything already written to this page, re-expressed as a distance from the page's base.
            var wide = new uint[PageSize];
            ushort[] deltas = _pages[page];
            uint[] blocks = _blocks[page];
            long written = Volatile.Read(ref _count) - ((long)page << PageBits);
            int upTo = (int)Math.Clamp(written, 0, PageSize);
            for (int s = 0; s < upTo; s++) wide[s] = blocks[s >> BlockBits] + deltas[s];
            Volatile.Write(ref _widePages[page], wide);
            Volatile.Write(ref _anyWidePage, true);   // published last: a reader must see the page first
            return wide;
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
                var dst = new long[PageSize];
                long b = _bases[p];
                if (_anyWidePage && _widePages![p] is uint[] w)
                    for (int k = 0; k < PageSize; k++) dst[k] = b + w[k];
                else
                {
                    ushort[] deltas = _pages[p];
                    uint[] blocks = _blocks[p];
                    for (int k = 0; k < PageSize; k++) dst[k] = b + blocks[k >> BlockBits] + deltas[k];
                }
                wide[p] = dst;
            }
            Volatile.Write(ref _wide, wide);
        }
    }
}

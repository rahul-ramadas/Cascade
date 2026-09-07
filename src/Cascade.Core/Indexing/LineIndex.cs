namespace Cascade.Core.Indexing;

/// <summary>
/// A growable, append-only mapping of line number → start byte offset. Stored as fixed-size pages
/// so growth never reallocates existing data, allowing lock-free reads while a background thread
/// appends.
///
/// Offsets only ever climb, so nothing here stores one whole. A page of 65,536 lines records its own
/// first offset as a <c>long</c>; inside it, each block of 16 lines records its first offset as a 32-bit
/// distance from the page's; and each line records a 16-bit distance from its block's. That is
/// <b>2.25 bytes per line</b>. On a multi-gigabyte log the index is the largest thing the process holds
/// by a wide margin — MEASURED at 265 MB of a 319 MB managed heap on a 15.8 GB log — and the scan that
/// drives filtering reads two offsets for every line, so the narrower page also shrinks what that pulls
/// through the cache.
///
/// A block whose 16 lines span 64 KB or more cannot be written that way, and that page alone falls back
/// to 32-bit distances: 4 bytes a line, which is EXACTLY what every page cost before, so the worst case is
/// the old case and not a regression. That equality is load-bearing and it is not free — the narrow pair
/// has to be RELEASED when a page falls back rather than merely abandoned, or a file of long lines would
/// hold 2 + 4 bytes a line and this whole shape would be a loss on it. See <see cref="WidenPage"/>.
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
    /// <summary>How many lines share a block base, and the number the whole shape turns on. A block that
    /// spans 64 KB cannot be held as 16-bit distances, so this is really a bet about line length: 16 lines
    /// covers any log averaging under 4 KB a line, which includes the structured JSON ones that a plain
    /// trace format does not prepare you for. MEASURED on four real logs - three ETW traces of 14-16 GB
    /// (195-239 bytes a line) and a 1.7 GB JSON-style log (2,421 bytes a line):
    /// <list type="bullet">
    /// <item>32 lines a block: 2.125 B/line on the traces, but EVERY page of the JSON log widens, so it
    /// saves nothing there at all</item>
    /// <item>16 lines a block: 2.250 B/line on the traces, and 2.250 on the JSON log too - no page widens</item>
    /// <item>8 lines a block: 2.500 B/line everywhere, buying only the 4-8 KB range</item>
    /// </list>
    /// So 16 gives up three points on the traces to keep the saving on a whole category of log. Guessing
    /// too big is only ever a lost saving, never a regression - a widened page costs exactly the four bytes
    /// a line this shape replaced - but a saving lost on every JSON log is not worth 0.125 bytes.</summary>
    private const int BlockBits = 4;
    private const int BlockSize = 1 << BlockBits;
    private const int BlockMask = BlockSize - 1;
    private const int BlocksPerPage = PageSize >> BlockBits;

    /// <summary>Lets a test step over block boundaries without writing the number down twice.</summary>
    internal const int BlockLinesForTesting = BlockSize;
    /// <summary>What a page costs per line while it is still narrow, for a test to hold the shape to.</summary>
    internal const double NarrowBytesPerLineForTesting = 2.0 + 4.0 / BlockSize;

    private ushort[]?[] _pages;   // distance from the line's own block base; null once the page has widened
    private uint[]?[] _blocks;    // each block's first offset, as a distance from the page's; null likewise
    private uint[][]? _widePages; // per page: distance from the page base, once a block on it overflowed
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
            double total = 0;
            for (int p = 0; p < _pageCount; p++)
                total += _pages[p] is null ? PageSize * 4 : PageSize * 2 + BlocksPerPage * 4;
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

        if (_pages[page] is not ushort[] deltas)
        {
            // The page has already fallen back; its narrow pair is gone and only the wide one is written.
            _widePages![page][slot] = (uint)delta;
        }
        else
        {
            uint[] blocks = _blocks[page]!;
            int block = slot >> BlockBits;
            if ((slot & BlockMask) == 0) blocks[block] = (uint)delta;
            long inBlock = delta - blocks[block];
            if (inBlock > ushort.MaxValue) WidenPage(page)[slot] = (uint)delta;
            else deltas[slot] = (ushort)inBlock;
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
        // The narrow pair is released when a page falls back, and released only AFTER the wide page is
        // published - so finding either half gone is proof the wide one is there.
        ushort[]? deltas = Volatile.Read(ref Volatile.Read(ref _pages)[page]);
        uint[]? blocks = Volatile.Read(ref Volatile.Read(ref _blocks)[page]);
        if (deltas is null || blocks is null) return b + Volatile.Read(ref _widePages)![page][slot];
        return b + blocks[slot >> BlockBits] + deltas[slot];
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
        // BOTH halves of the narrow pair are tested, not just one. A page can fall back between these two
        // reads, and the writer releases the pair one reference at a time, so a reader can genuinely see a
        // live deltas array beside a released blocks array. Either being gone proves the wide page is
        // already published (it is written before either null), so either is enough to send us there.
        // Testing only one of them is a null dereference no test will reproduce and every long run will hit.
        ushort[]? deltas = Volatile.Read(ref _pages[page]);
        uint[]? blocks = Volatile.Read(ref _blocks[page]);
        if (deltas is null || blocks is null)
        {
            uint[] widePage = _widePages![page];
            start = b + widePage[slot];
            end = !hasNext ? fileLength
                : slot + 1 < PageSize ? b + widePage[slot + 1]
                : NextPageStart(page);
            return;
        }

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
        ushort[]? deltas = Volatile.Read(ref _pages[page + 1]);
        uint[]? blocks = Volatile.Read(ref _blocks[page + 1]);
        if (deltas is null || blocks is null) return b + _widePages![page + 1][0];
        return b + blocks[0] + deltas[0];
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
                var grownBlocks = new uint[]?[newLen];
                Array.Copy(_blocks, grownBlocks, _pageCount);
                Volatile.Write(ref _blocks, grownBlocks);

                var grown = new ushort[]?[newLen];
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
    /// span 64 KB or more — which is any file whose lines average 2 KB, so not a rarity worth being casual
    /// about. That page then costs EXACTLY what every page cost before this shape existed: the narrow pair
    /// is released, not merely abandoned. Left in place they would have made such a file 2 + 4 bytes a line,
    /// half as much again as the four it fell back from, and the change would have been a loss on every log
    /// that carries JSON.</summary>
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
            ushort[] deltas = _pages[page]!;
            uint[] blocks = _blocks[page]!;
            long written = Volatile.Read(ref _count) - ((long)page << PageBits);
            int upTo = (int)Math.Clamp(written, 0, PageSize);
            for (int s = 0; s < upTo; s++) wide[s] = blocks[s >> BlockBits] + deltas[s];

            // Publish the wide page BEFORE letting go of the narrow pair, so a reader that finds either half
            // of the pair gone is certain to find the wide one there. A reader still holding the old narrow
            // arrays reads correct answers from them: nothing rewrites what they already hold, and a line
            // written after this point cannot be read until Count publishes it, by which time these nulls
            // are visible too.
            Volatile.Write(ref _widePages[page], wide);
            Volatile.Write(ref _pages[page], null);
            Volatile.Write(ref _blocks[page], null);
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
                if (_pages[p] is ushort[] deltas && _blocks[p] is uint[] blocks)
                    for (int k = 0; k < PageSize; k++) dst[k] = b + blocks[k >> BlockBits] + deltas[k];
                else
                {
                    uint[] w = _widePages![p];
                    for (int k = 0; k < PageSize; k++) dst[k] = b + w[k];
                }
                wide[p] = dst;
            }
            Volatile.Write(ref _wide, wide);
        }
    }
}

using System.Numerics;

namespace Cascade.Core.Filtering;

/// <summary>
/// The set of file lines currently visible (passing the filters), stored as <b>one bit per file line</b> so a
/// filter pass can update it <b>in place</b> — keeping, dropping or adding individual lines — instead of
/// rebuilding it from scratch. The view therefore stays complete and coherent at every instant (new results
/// above the scan frontier, the previous pass's results below it), which lets the UI hold the user's position
/// steady while filtering streams instead of waiting for the scan to reach them.
/// <para>
/// Costs <c>lines / 8</c> bytes plus a small rank index no matter how many lines match — far less than a list
/// of matched line numbers (~4 MB vs ~156 MB for a 33M-line file with 19M matches), and nothing is allocated
/// when the filters change.
/// </para>
/// <para>
/// Single writer (the filter worker thread), many lock-free readers (the UI). Readers take one immutable
/// index snapshot and stay self-consistent within it; bits the writer flips afterwards can only shift a row
/// by less than one 4,096-line block, which the next <see cref="Publish"/> corrects.
/// </para>
/// </summary>
public sealed class VisibleLineSet
{
    private const int WordBits = 64;
    private const int BlockWords = 64;                  // rank block = 4,096 lines
    private const int BlockLines = BlockWords * WordBits;
    private const int PageWords = 1 << 16;              // 512 KB page = 4,194,304 lines

    /// <summary>Immutable rank index published for readers: visible-line counts before each block.</summary>
    private sealed class Index
    {
        public static readonly Index Empty = new(new long[1], 0, 0);
        public readonly long[] Cumulative; // Cumulative[b] = visible lines before block b; last entry = Total
        public readonly long Total;
        public readonly long Lines;

        public Index(long[] cumulative, long total, long lines)
        {
            Cumulative = cumulative;
            Total = total;
            Lines = lines;
        }
    }

    private long[][] _pages = new long[4][];
    private int _pageCount;
    private long _lines;                    // writer-only: file lines covered
    private int _blocks;                    // writer-only: rank blocks covering _lines
    private long[] _cum = new long[1];      // writer-only running cumulative (_blocks + 1 entries)
    private Index _index = Index.Empty;

    /// <summary>Number of visible lines in the last published snapshot.</summary>
    public long Count => Volatile.Read(ref _index).Total;

    /// <summary>File lines this set has meaningful visibility for; beyond it nothing has been evaluated yet.</summary>
    public long KnownLines => Volatile.Read(ref _index).Lines;

    // ---- writer ----

    /// <summary>Makes room for <paramref name="lines"/> file lines. New lines start hidden.</summary>
    public void EnsureLines(long lines)
    {
        if (lines <= _lines) return;

        long words = (lines + WordBits - 1) / WordBits;
        int pagesNeeded = (int)((words + PageWords - 1) / PageWords);
        if (pagesNeeded > _pages.Length)
        {
            int len = Math.Max(4, _pages.Length);
            while (len < pagesNeeded) len *= 2;
            var grown = new long[len][];
            Array.Copy(_pages, grown, _pageCount);
            Volatile.Write(ref _pages, grown);
        }
        for (int p = _pageCount; p < pagesNeeded; p++) Volatile.Write(ref _pages[p], new long[PageWords]);
        if (pagesNeeded > _pageCount) _pageCount = pagesNeeded;

        int blocks = (int)((lines + BlockLines - 1) / BlockLines);
        if (blocks > _blocks)
        {
            long total = _cum[_blocks];
            Array.Resize(ref _cum, blocks + 1);
            for (int b = _blocks + 1; b <= blocks; b++) _cum[b] = total; // new lines are hidden
            _blocks = blocks;
        }
        _lines = lines;
    }

    /// <summary>Applies one block of freshly evaluated results, updating each line <b>in place</b>: lines that
    /// still match keep their place, lines that stopped matching are dropped, new matches are added.</summary>
    public void ApplyRange(long start, ReadOnlySpan<bool> visible)
    {
        if (start < 0 || visible.Length == 0) return;
        long end = start + visible.Length;
        EnsureLines(end);

        // Build whole 64-line words at a time rather than touching one bit per line; only the ragged ends
        // need a read-modify-write.
        int i = 0;
        long line = start;

        int head = (int)(line % WordBits);
        if (head != 0)
        {
            int n = Math.Min(WordBits - head, visible.Length);
            MergeBits(line / WordBits, head, visible[..n]);
            i = n;
            line += n;
        }

        for (; i + WordBits <= visible.Length; i += WordBits, line += WordBits)
        {
            ulong w = 0;
            ReadOnlySpan<bool> chunk = visible.Slice(i, WordBits);
            for (int k = 0; k < WordBits; k++) if (chunk[k]) w |= 1UL << k;
            SetWord(line / WordBits, (long)w);
        }

        if (i < visible.Length) MergeBits(line / WordBits, 0, visible[i..]);

        Recount(start, end);
    }

    /// <summary>Read-modify-writes <paramref name="bits"/> into one word starting at <paramref name="offset"/>.</summary>
    private void MergeBits(long word, int offset, ReadOnlySpan<bool> bits)
    {
        ulong set = 0, touched = 0;
        for (int k = 0; k < bits.Length; k++)
        {
            ulong mask = 1UL << (offset + k);
            touched |= mask;
            if (bits[k]) set |= mask;
        }
        SetWord(word, (long)(((ulong)WordAt(word) & ~touched) | set));
    }

    /// <summary>Marks every line in <c>[0, lines)</c> visible and hides the rest — the state the view is in
    /// when no filters are active. Used to seed an in-place pass so its first frame still shows the user's
    /// current lines instead of an empty view.</summary>
    public void FillVisible(long lines)
    {
        EnsureLines(lines);
        long full = lines / WordBits;
        int tail = (int)(lines % WordBits);
        long totalWords = (_lines + WordBits - 1) / WordBits;

        for (long w = 0; w < full; w++) SetWord(w, -1L);
        if (tail > 0) SetWord(full, (long)((1UL << tail) - 1));
        for (long w = full + (tail > 0 ? 1 : 0); w < totalWords; w++) SetWord(w, 0L);

        RecountAll();
    }

    /// <summary>Publishes an immutable snapshot of the rank index for lock-free readers.</summary>
    public void Publish()
    {
        var cum = new long[_blocks + 1];
        Array.Copy(_cum, cum, _blocks + 1);
        Volatile.Write(ref _index, new Index(cum, _cum[_blocks], _lines));
    }

    private void SetWord(long word, long value) => _pages[(int)(word / PageWords)][(int)(word % PageWords)] = value;

    private long WordAt(long word) => _pages[(int)(word / PageWords)][(int)(word % PageWords)];

    /// <summary>Recomputes the running cumulative counts for the blocks touched by <c>[from, to)</c> and
    /// shifts every later block by the resulting delta — O(blocks), not O(lines).</summary>
    private void Recount(long from, long to)
    {
        int firstBlock = (int)(from / BlockLines);
        int lastBlock = (int)((to - 1) / BlockLines);
        long before = _cum[lastBlock + 1];
        for (int b = firstBlock; b <= lastBlock; b++) _cum[b + 1] = _cum[b] + PopCountBlock(b);
        long delta = _cum[lastBlock + 1] - before;
        if (delta != 0)
            for (int b = lastBlock + 2; b <= _blocks; b++) _cum[b] += delta;
    }

    private void RecountAll()
    {
        for (int b = 0; b < _blocks; b++) _cum[b + 1] = _cum[b] + PopCountBlock(b);
    }

    private long PopCountBlock(int block)
    {
        long w0 = (long)block * BlockWords;
        long w1 = Math.Min(w0 + BlockWords, (_lines + WordBits - 1) / WordBits);
        long n = 0;
        for (long w = w0; w < w1; w++) n += BitOperations.PopCount((ulong)WordAt(w));
        return n;
    }

    // ---- lock-free readers ----

    /// <summary>Row currently showing <paramref name="line"/>, or -1 when that line is not visible.</summary>
    public long RowForLine(long line)
    {
        var idx = Volatile.Read(ref _index);
        if (line < 0 || line >= idx.Lines) return -1;
        long[][] pages = Volatile.Read(ref _pages);
        long word = line / WordBits;
        if ((GetWord(pages, word) & (1L << (int)(line % WordBits))) == 0) return -1;
        return RankBefore(idx, pages, line);
    }

    /// <summary>Row of the nearest visible line at or after <paramref name="line"/>; <see cref="Count"/> if none.</summary>
    public long RowAtOrAfterLine(long line)
    {
        var idx = Volatile.Read(ref _index);
        if (line <= 0) return 0;
        if (line >= idx.Lines) return idx.Total;
        return RankBefore(idx, Volatile.Read(ref _pages), line);
    }

    /// <summary>File line shown at <paramref name="row"/> (clamped into the current set).</summary>
    public long LineAt(long row)
    {
        var idx = Volatile.Read(ref _index);
        if (idx.Total <= 0) return 0;
        return SelectLine(idx, Volatile.Read(ref _pages), Math.Clamp(row, 0, idx.Total - 1));
    }

    /// <summary>Resolves one whole screen against a <b>single</b> index snapshot: puts <paramref name="anchorLine"/>
    /// (or the next visible line) <paramref name="anchorOffset"/> rows from the top and fills
    /// <paramref name="lines"/> with the file lines to paint. Because the pass keeps adding and dropping lines
    /// while the UI paints, resolving row by row would mix two states within one frame; doing it in one shot
    /// makes every frame internally consistent and exactly anchored. Returns the first row.</summary>
    public long ResolveWindow(long anchorLine, int anchorOffset, Span<long> lines, out int count)
    {
        var idx = Volatile.Read(ref _index);
        long[][] pages = Volatile.Read(ref _pages);
        long anchorRow = anchorLine <= 0 ? 0
            : anchorLine >= idx.Lines ? idx.Total
            : RankBefore(idx, pages, anchorLine);
        long first = Math.Clamp(anchorRow - anchorOffset, 0, Math.Max(0, idx.Total - lines.Length));
        count = Fill(idx, pages, first, lines);
        return first;
    }

    /// <summary>Fills <paramref name="lines"/> with the file lines shown at rows starting at
    /// <paramref name="firstRow"/>, all resolved against a single snapshot. Returns how many were filled.</summary>
    public int LinesForRows(long firstRow, Span<long> lines)
        => Fill(Volatile.Read(ref _index), Volatile.Read(ref _pages), firstRow, lines);

    /// <summary>Walks set bits forward from <paramref name="firstRow"/> — one select, then a linear scan, so a
    /// whole screen costs about as much as a single lookup.</summary>
    private static int Fill(Index idx, long[][] pages, long firstRow, Span<long> lines)
    {
        if (idx.Total <= 0 || lines.Length == 0) return 0;
        firstRow = Math.Max(0, firstRow);
        if (firstRow >= idx.Total) return 0;

        long line = SelectLine(idx, pages, firstRow);
        int n = 0;
        lines[n++] = line;

        long word = line / WordBits;
        int bit = (int)(line % WordBits);
        ulong w = (ulong)GetWord(pages, word) & ~((1UL << bit) | ((1UL << bit) - 1)); // drop bits 0..bit
        long lastWord = (idx.Lines + WordBits - 1) / WordBits;

        while (n < lines.Length)
        {
            while (w == 0)
            {
                if (++word >= lastWord) return n;
                w = (ulong)GetWord(pages, word);
            }
            int next = BitOperations.TrailingZeroCount(w);
            lines[n++] = word * WordBits + next;
            w &= w - 1;
        }
        return n;
    }

    /// <summary>File line at <paramref name="row"/> within the given snapshot (row must be in range).</summary>
    private static long SelectLine(Index idx, long[][] pages, long row)
    {
        // Largest block whose preceding count is still <= row.
        int lo = 0, hi = idx.Cumulative.Length - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) >> 1;
            if (idx.Cumulative[mid] <= row) lo = mid; else hi = mid - 1;
        }

        long remaining = row - idx.Cumulative[lo];
        long lastWord = (idx.Lines + WordBits - 1) / WordBits;
        for (long w = (long)lo * BlockWords; w < lastWord; w++)
        {
            ulong word = (ulong)GetWord(pages, w);
            int set = BitOperations.PopCount(word);
            if (remaining < set)
            {
                while (word != 0)
                {
                    int bit = BitOperations.TrailingZeroCount(word);
                    if (remaining == 0) return w * WordBits + bit;
                    remaining--;
                    word &= word - 1;
                }
            }
            remaining -= set;
        }
        return Math.Max(0, idx.Lines - 1); // defensive: bits changed under this snapshot
    }

    /// <summary>Visible lines strictly before <paramref name="line"/>.</summary>
    private static long RankBefore(Index idx, long[][] pages, long line)
    {
        int block = (int)(line / BlockLines);
        if (block >= idx.Cumulative.Length) return idx.Total;
        long rank = idx.Cumulative[block];
        long wEnd = line / WordBits;
        for (long w = (long)block * BlockWords; w < wEnd; w++) rank += BitOperations.PopCount((ulong)GetWord(pages, w));
        int bits = (int)(line % WordBits);
        if (bits > 0) rank += BitOperations.PopCount((ulong)GetWord(pages, wEnd) & ((1UL << bits) - 1));
        return rank;
    }

    private static long GetWord(long[][] pages, long word)
    {
        int p = (int)(word / PageWords);
        if (p < 0 || p >= pages.Length) return 0;
        long[]? page = Volatile.Read(ref pages[p]);
        return page is null ? 0 : page[(int)(word % PageWords)];
    }
}

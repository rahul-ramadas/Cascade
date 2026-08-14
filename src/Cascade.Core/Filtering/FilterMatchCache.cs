using System.Numerics;

namespace Cascade.Core.Filtering;

/// <summary>
/// Remembers, per filter, which lines it matched, so enabling/disabling/removing filters does not re-scan the
/// file. What is cached is the filter's <b>deep match</b> (its own predicate AND every ancestor's), because
/// that is exactly the part that does not depend on which filters are enabled — so a toggle becomes a bitwise
/// combine of cached sets instead of a full pass.
/// <para>
/// Storage is chosen per filter: a bit per line where that is smaller, otherwise a sorted list of matching
/// lines. Real filter sets are extremely sparse (most filters match a tiny fraction of a log, many match
/// nothing at all), so this is dramatically cheaper than a bitmap for every filter — measured at 21 MB rather
/// than 692 MB for a 175-filter set over 33M lines.
/// </para>
/// </summary>
public sealed class FilterMatchCache
{
    private readonly Dictionary<string, MatchSet> _sets = new(StringComparer.Ordinal);
    // Written by the filter worker, read by per-filter find on its own background thread.
    private readonly object _sync = new();

    public long UsedBytes { get { lock (_sync) return _usedBytes; } }
    public int Count { get { lock (_sync) return _sets.Count; } }

    private long _usedBytes;

    /// <summary>The lines a filter matched. <see cref="Combine"/> reads the storage directly, so it can walk
    /// a dense set a word at a time and scatter a sparse one bit by bit rather than treating both alike.</summary>
    public sealed class MatchSet
    {
        internal readonly ulong[]? _dense;   // one bit per line
        internal readonly uint[]? _sparse;   // sorted matching line numbers
        internal readonly int _sparseCount;

        internal MatchSet(ulong[]? dense, uint[]? sparse, int sparseCount, long covered, long matches, long bytes)
        {
            _dense = dense;
            _sparse = sparse;
            _sparseCount = sparseCount;
            Covered = covered;
            Matches = matches;
            Bytes = bytes;
        }

        /// <summary>Lines <c>[0, Covered)</c> have been evaluated against this filter.</summary>
        public long Covered { get; }
        public long Matches { get; }
        public long Bytes { get; }

        public bool Contains(long line)
        {
            if (line < 0 || line >= Covered) return false;
            if (_dense is not null) return (_dense[line >> 6] & (1UL << (int)(line & 63))) != 0;
            int lo = 0, hi = _sparseCount - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                uint v = _sparse![mid];
                if (v == line) return true;
                if (v < line) lo = mid + 1; else hi = mid - 1;
            }
            return false;
        }

        /// <summary>The first matching line at or after <paramref name="from"/>, or -1. This is what makes
        /// "find the filter's next match" a bit scan instead of a re-read of the file.</summary>
        public long Next(long from)
        {
            if (from < 0) from = 0;
            if (from >= Covered) return -1;

            if (_dense is ulong[] dense)
            {
                long w = from >> 6;
                ulong word = dense[w] & (ulong.MaxValue << (int)(from & 63));
                while (true)
                {
                    if (word != 0)
                    {
                        long line = (w << 6) + BitOperations.TrailingZeroCount(word);
                        return line < Covered ? line : -1;
                    }
                    if (++w >= dense.LongLength) return -1;
                    word = dense[w];
                }
            }

            int lo = 0, hi = _sparseCount - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_sparse![mid] >= from) { found = mid; hi = mid - 1; } else lo = mid + 1;
            }
            return found < 0 ? -1 : _sparse![found];
        }

        /// <summary>The last matching line at or before <paramref name="from"/>, or -1.</summary>
        public long Previous(long from)
        {
            if (from >= Covered) from = Covered - 1;
            if (from < 0) return -1;

            if (_dense is ulong[] dense)
            {
                long w = Math.Min(from >> 6, dense.LongLength - 1);
                int bit = (int)(from & 63);
                ulong word = dense[w] & (bit == 63 ? ulong.MaxValue : (1UL << (bit + 1)) - 1);
                while (true)
                {
                    if (word != 0) return (w << 6) + (63 - BitOperations.LeadingZeroCount(word));
                    if (--w < 0) return -1;
                    word = dense[w];
                }
            }

            int lo = 0, hi = _sparseCount - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_sparse![mid] <= from) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return found < 0 ? -1 : _sparse![found];
        }

        /// <summary>How many of this filter's lines fall in <c>[from, toExclusive)</c>. Used to summarise a
        /// whole band of the file in one pixel, so it must not walk the lines one at a time.</summary>
        public long CountInRange(long from, long toExclusive)
        {
            if (from < 0) from = 0;
            if (toExclusive > Covered) toExclusive = Covered;
            if (toExclusive <= from) return 0;

            if (_dense is ulong[] dense)
            {
                long first = from >> 6, last = (toExclusive - 1) >> 6;
                ulong head = ulong.MaxValue << (int)(from & 63);
                int lastBit = (int)((toExclusive - 1) & 63);
                ulong tail = lastBit == 63 ? ulong.MaxValue : (1UL << (lastBit + 1)) - 1;
                if (first == last) return BitOperations.PopCount(dense[first] & head & tail);

                long n = BitOperations.PopCount(dense[first] & head);
                for (long w = first + 1; w < last; w++) n += BitOperations.PopCount(dense[w]);
                return n + BitOperations.PopCount(dense[last] & tail);
            }

            return LowerBound(toExclusive) - LowerBound(from);
        }

        /// <summary>Index of the first sparse entry at or after <paramref name="line"/>.</summary>
        private int LowerBound(long line)
        {
            int lo = 0, hi = _sparseCount;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                if (_sparse![mid] < line) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
    }

    /// <summary>Returns the cached set for <paramref name="key"/> if it covers at least
    /// <paramref name="requiredLines"/> lines. <see cref="MatchSet"/> is immutable, so the result stays
    /// valid outside the lock.</summary>
    public bool TryGet(string key, long requiredLines, out MatchSet set)
    {
        lock (_sync) return _sets.TryGetValue(key, out set!) && set.Covered >= requiredLines;
    }

    public void Clear()
    {
        lock (_sync)
        {
            _sets.Clear();
            _usedBytes = 0;
        }
    }

    /// <summary>Forgets every result that does not belong to one of <paramref name="live"/>. A key describes
    /// a filter's whole predicate chain, so deleting or editing a filter strands its results permanently -
    /// nothing can ever ask for them again. Without this the cache only grows.</summary>
    public void RetainOnly(IReadOnlyCollection<string> live)
    {
        lock (_sync)
        {
            if (_sets.Count == 0) return;
            var dead = _sets.Keys.Where(k => !live.Contains(k)).ToList();
            foreach (string key in dead)
            {
                _usedBytes -= _sets[key].Bytes;
                _sets.Remove(key);
            }
        }
    }

    internal void Store(string key, MatchSet set)
    {
        lock (_sync)
        {
            if (_sets.TryGetValue(key, out var old)) _usedBytes -= old.Bytes;
            _sets[key] = set;
            _usedBytes += set.Bytes;
        }
    }

    /// <summary>Accumulates one filter's matching lines during a pass, switching from a sorted list to a bit
    /// per line once that becomes the cheaper representation.</summary>
    public sealed class SetBuilder
    {
        private long _lines;
        private uint[] _sparse = new uint[64];
        private int _sparseCount;
        private ulong[]? _dense;
        private long _matches;

        /// <param name="lines">Lines indexed so far. Filtering normally starts while the file is still being
        /// indexed, so this is only a lower bound; the builder resizes itself as later words arrive.</param>
        public SetBuilder(long lines) => _lines = lines;

        public long Matches => _matches;

        /// <summary>Adds a 64-line word of results; <paramref name="wordIndex"/> must ascend.</summary>
        public void AddWord(long wordIndex, ulong word)
        {
            if (word == 0) return;
            _matches += BitOperations.PopCount(word);

            long seen = (wordIndex + 1) << 6;
            if (seen > _lines) _lines = seen;

            // A bit per line costs lines/8 bytes; a sorted uint list costs 4 bytes per match. Switch once the
            // list would exceed the bitmap.
            if (_dense is null && _matches > Math.Max(1024, _lines / 32)) SwitchToDense();

            if (_dense is not null)
            {
                if (wordIndex >= _dense.LongLength) GrowDense(wordIndex + 1);
                _dense[wordIndex] |= word;
                return;
            }

            long first = wordIndex << 6;
            while (word != 0)
            {
                int bit = BitOperations.TrailingZeroCount(word);
                if (_sparseCount == _sparse.Length) Array.Resize(ref _sparse, _sparse.Length * 2);
                _sparse[_sparseCount++] = (uint)(first + bit);
                word &= word - 1;
            }
        }

        private void GrowDense(long words)
        {
            var bigger = new ulong[Math.Max(words, _dense!.LongLength * 2)];
            _dense.CopyTo(bigger, 0);
            _dense = bigger;
        }

        private void SwitchToDense()
        {
            _dense = new ulong[Math.Max(1, (_lines + 63) / 64)];
            for (int i = 0; i < _sparseCount; i++)
            {
                long line = _sparse[i];
                _dense[line >> 6] |= 1UL << (int)(line & 63);
            }
            _sparse = Array.Empty<uint>();
            _sparseCount = 0;
        }

        public MatchSet Build(long covered)
        {
            if (_dense is not null)
            {
                // Trim the growth headroom (always past the end of the file) so the stored set costs exactly
                // what it needs.
                long words = (covered + 63) / 64;
                var dense = _dense;
                if (dense.LongLength != words)
                {
                    var trimmed = new ulong[words];
                    Array.Copy(dense, trimmed, Math.Min(words, dense.LongLength));
                    dense = trimmed;
                }
                return new MatchSet(dense, null, 0, covered, _matches, dense.LongLength * 8);
            }
            var exact = new uint[_sparseCount];
            Array.Copy(_sparse, exact, _sparseCount);
            return new MatchSet(null, exact, _sparseCount, covered, _matches, exact.LongLength * 4);
        }

        /// <summary>The first matching line in <c>[from, covered)</c>, or -1. Half-built results are worth
        /// reading: a find can take its answer from the pass that is running rather than starting one of its
        /// own. Only the caller knows how far that pass has swept, so the extent is passed in.</summary>
        public long Next(long from, long covered)
        {
            if (from < 0) from = 0;
            if (from >= covered) return -1;

            if (_dense is ulong[] dense)
            {
                long w = from >> 6;
                if (w >= dense.LongLength) return -1;   // nothing has matched this far up yet
                ulong word = dense[w] & (ulong.MaxValue << (int)(from & 63));
                while (true)
                {
                    if (word != 0)
                    {
                        long line = (w << 6) + BitOperations.TrailingZeroCount(word);
                        return line < covered ? line : -1;
                    }
                    if (++w >= dense.LongLength) return -1;
                    word = dense[w];
                }
            }

            int lo = 0, hi = _sparseCount - 1, found = -1;   // words arrive in order, so this list ascends
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_sparse[mid] >= from) { found = mid; hi = mid - 1; } else lo = mid + 1;
            }
            if (found < 0) return -1;
            long hit = _sparse[found];
            return hit < covered ? hit : -1;
        }

        /// <summary>The last matching line at or before <paramref name="from"/> within <c>[0, covered)</c>,
        /// or -1.</summary>
        public long Previous(long from, long covered)
        {
            if (from >= covered) from = covered - 1;
            if (from < 0) return -1;

            if (_dense is ulong[] dense)
            {
                long w = from >> 6;
                ulong word;
                if (w >= dense.LongLength) { w = dense.LongLength - 1; word = w < 0 ? 0 : dense[w]; }
                else
                {
                    int bit = (int)(from & 63);
                    word = dense[w] & (bit == 63 ? ulong.MaxValue : (1UL << (bit + 1)) - 1);
                }
                while (w >= 0)
                {
                    if (word != 0) return (w << 6) + (63 - BitOperations.LeadingZeroCount(word));
                    if (--w < 0) return -1;
                    word = dense[w];
                }
                return -1;
            }

            int lo = 0, hi = _sparseCount - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_sparse[mid] <= from) { found = mid; lo = mid + 1; } else hi = mid - 1;
            }
            return found < 0 ? -1 : _sparse[found];
        }
    }

    /// <summary>
    /// Rebuilds the visible-line words purely from cached sets: a line is shown when some enabled include
    /// deep-matches it and no enabled exclude does. This is what makes a filter toggle a memory-bandwidth
    /// operation instead of a re-scan.
    /// </summary>
    public static void Combine(IReadOnlyList<MatchSet> includes, IReadOnlyList<MatchSet> excludes,
        bool hasEnabledInclude, long lines, ulong[] shown)
    {
        int words = (int)((lines + 63) / 64);
        var span = shown.AsSpan(0, words);

        // Sets differ enormously in shape and it is worth treating them differently: in a real filter file
        // most match nothing at all, most of the rest match a fraction of a percent, and only a handful are
        // dense enough to be worth walking a word at a time. Asking every set for every word costs
        // O(lines x filters) whatever they hold; splitting by shape costs O(lines x dense sets + sparse bits).
        if (!hasEnabledInclude) span.Fill(ulong.MaxValue);   // no include filters: everything qualifies
        else
        {
            span.Clear();
            foreach (var set in includes) Or(span, set, lines);
        }
        foreach (var set in excludes) AndNot(span, set, lines);

        // Clear any bits past the end of the file in the final word.
        int tail = (int)(lines & 63);
        if (tail != 0 && words > 0) span[words - 1] &= (1UL << tail) - 1;
    }

    private static void Or(Span<ulong> shown, MatchSet set, long lines)
    {
        if (set._dense is ulong[] dense)
        {
            int n = Math.Min(shown.Length, dense.Length);
            for (int w = 0; w < n; w++) shown[w] |= dense[w];
            return;
        }
        if (set._sparse is not uint[] sparse) return;
        for (int i = 0; i < set._sparseCount; i++)
        {
            uint line = sparse[i];
            if (line >= lines) return;   // ascending, and a set may cover more of the file than was asked for
            shown[(int)(line >> 6)] |= 1UL << (int)(line & 63);
        }
    }

    private static void AndNot(Span<ulong> shown, MatchSet set, long lines)
    {
        if (set._dense is ulong[] dense)
        {
            int n = Math.Min(shown.Length, dense.Length);
            for (int w = 0; w < n; w++) shown[w] &= ~dense[w];
            return;
        }
        if (set._sparse is not uint[] sparse) return;
        for (int i = 0; i < set._sparseCount; i++)
        {
            uint line = sparse[i];
            if (line >= lines) return;
            shown[(int)(line >> 6)] &= ~(1UL << (int)(line & 63));
        }
    }
}

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
    /// <summary>Cap on total cached bytes. Beyond this the cache stops taking new entries rather than growing
    /// without bound; filtering still works, it just re-scans.</summary>
    public const long DefaultBudgetBytes = 256L * 1024 * 1024;

    private readonly Dictionary<string, MatchSet> _sets = new(StringComparer.Ordinal);
    private readonly long _budgetBytes;

    public FilterMatchCache(long budgetBytes = DefaultBudgetBytes) => _budgetBytes = budgetBytes;

    public long UsedBytes { get; private set; }
    public int Count => _sets.Count;

    /// <summary>The lines a filter matched. Read sequentially by <see cref="Combine"/> via a word cursor.</summary>
    public sealed class MatchSet
    {
        private readonly ulong[]? _dense;   // one bit per line
        private readonly uint[]? _sparse;   // sorted matching line numbers
        private readonly int _sparseCount;

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

        /// <summary>Walks the set 64 lines at a time. Words must be requested in ascending order.</summary>
        internal struct Cursor
        {
            private readonly MatchSet _set;
            private int _at;

            public Cursor(MatchSet set) { _set = set; _at = 0; }

            public ulong Word(long wordIndex)
            {
                if (_set._dense is ulong[] dense)
                    return (ulong)wordIndex < (ulong)dense.LongLength ? dense[wordIndex] : 0UL;

                uint[] sparse = _set._sparse!;
                long first = wordIndex << 6, end = first + 64;
                ulong word = 0;
                while (_at < _set._sparseCount && sparse[_at] < first) _at++;   // ascending access only
                for (int i = _at; i < _set._sparseCount && sparse[i] < end; i++) word |= 1UL << (int)(sparse[i] - first);
                return word;
            }
        }
    }

    /// <summary>Returns the cached set for <paramref name="key"/> if it covers at least
    /// <paramref name="requiredLines"/> lines.</summary>
    public bool TryGet(string key, long requiredLines, out MatchSet set)
        => _sets.TryGetValue(key, out set!) && set.Covered >= requiredLines;

    public void Clear()
    {
        _sets.Clear();
        UsedBytes = 0;
    }

    internal void Store(string key, MatchSet set)
    {
        if (_sets.TryGetValue(key, out var old)) UsedBytes -= old.Bytes;
        else if (UsedBytes + set.Bytes > _budgetBytes) return;   // over budget: simply do not cache
        _sets[key] = set;
        UsedBytes += set.Bytes;
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
        var incCursors = new MatchSet.Cursor[includes.Count];
        for (int i = 0; i < includes.Count; i++) incCursors[i] = new MatchSet.Cursor(includes[i]);
        var excCursors = new MatchSet.Cursor[excludes.Count];
        for (int i = 0; i < excludes.Count; i++) excCursors[i] = new MatchSet.Cursor(excludes[i]);

        for (int w = 0; w < words; w++)
        {
            ulong included = 0;
            for (int i = 0; i < incCursors.Length; i++) included |= incCursors[i].Word(w);
            if (!hasEnabledInclude) included = ulong.MaxValue;   // no include filters: everything qualifies

            ulong excluded = 0;
            for (int i = 0; i < excCursors.Length; i++) excluded |= excCursors[i].Word(w);

            shown[w] = included & ~excluded;
        }

        // Clear any bits past the end of the file in the final word.
        int tail = (int)(lines & 63);
        if (tail != 0 && words > 0) shown[words - 1] &= (1UL << tail) - 1;
    }
}

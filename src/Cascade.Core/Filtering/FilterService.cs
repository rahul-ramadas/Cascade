using System.Buffers;
using System.Text;
using Cascade.Core.Indexing;
using Cascade.Core.IO;
using Cascade.Core.Markers;
using Cascade.Core.Model;

namespace Cascade.Core.Filtering;

/// <summary>
/// Runs filtering on a dedicated background thread and <b>streams</b> results: matching line numbers
/// are appended to a <see cref="FilteredView"/> in order as they are found, so the UI shows passing
/// lines immediately while the rest of the file is still being evaluated. Each generation (started by
/// <see cref="Restart"/> when filters change) processes lines in ascending blocks, parallelized across
/// cores within a block to keep results ordered. Picks up newly indexed lines as they arrive.
/// </summary>
public sealed class FilterService : IDisposable
{
    public sealed class Generation
    {
        public FilterSnapshot Snapshot { get; }
        public FilteredView View { get; }
        public long Id { get; }
        public long[] Counts { get; }
        public readonly object CountsSync = new();

        public Generation(FilterSnapshot snapshot, FilteredView view, long id)
        {
            Snapshot = snapshot;
            View = view;
            Id = id;
            Counts = new long[snapshot.FilterCount];
        }

        /// <summary>Seed the shared visible set to "everything visible" before sweeping (set when the view
        /// was unfiltered, so the first filtered frame still shows the user's current lines).</summary>
        internal bool SeedAllVisible;
        internal bool Seeded;

        /// <summary>Per-filter accumulators (indexed by filter index) filled while this pass runs, so its work
        /// is remembered and later enable/disable changes need no pass at all. Null when not caching.</summary>
        internal FilterMatchCache.SetBuilder?[]? CacheBuild;
        internal List<FilterSnapshot.CacheableFilter>? CacheFilters;
        internal bool CacheStored;
    }

    private sealed class Worker
    {
        public LineReader Reader = null!;
        public long[] Counts = null!;
        public FilterSnapshot.MatchContext Context = null!;
        public ulong[] Deep = Array.Empty<ulong>();
    }

    private const int Block = 1 << 15; // 32,768 lines per ordered block

    private readonly MemoryMappedTextSource _src;
    private readonly LineIndex _index;
    private readonly long _fileLength;
    private readonly MarkerStore _markers;
    private readonly Encoding _encoding;
    private readonly Func<long> _completedCount;
    private readonly Func<bool> _indexComplete;

    private readonly object _lock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    // One visible-line set per file, reused by every generation: a filter change re-evaluates lines and
    // updates it in place (keep / drop / add) rather than rebuilding it, so the view is never empty.
    private readonly VisibleLineSet _visible = new();
    // Remembers which lines each filter matched, so toggling filters recombines cached sets instead of
    // re-reading the file.
    private readonly FilterMatchCache _cache = new();
    private Generation? _current;
    private long _processed;
    private long _genId;
    private CancellationTokenSource _cts = new();
    private volatile bool _disposed;

    /// <summary>Raised (on the worker thread) when the visible count for the current generation grows.</summary>
    public event Action<Generation>? Progress;

    public FilterService(MemoryMappedTextSource src, LineIndex index, long fileLength, MarkerStore markers,
        Encoding encoding, Func<long> completedCount, Func<bool> indexComplete)
    {
        _src = src;
        _index = index;
        _fileLength = fileLength;
        _markers = markers;
        _encoding = encoding;
        _completedCount = completedCount;
        _indexComplete = indexComplete;
        _thread = new Thread(Loop) { IsBackground = true, Name = "Cascade.Filter" };
        _thread.Start();
    }

    /// <summary>Starts a fresh generation for a changed filter set, cancelling any in-flight run. The visible
    /// set is <b>reused</b>, so the re-evaluation updates it line by line instead of clearing it;
    /// <paramref name="seedAllVisible"/> marks every line visible first (the previous view was unfiltered).</summary>
    public Generation Restart(FilterSnapshot snapshot, bool seedAllVisible = false)
    {
        Generation gen;
        lock (_lock)
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            gen = new Generation(snapshot, FilteredView.CreateExplicit(_visible), ++_genId) { SeedAllVisible = seedAllVisible };
            _current = gen;
            _processed = 0;
        }
        _wake.Set();
        return gen;
    }

    /// <summary>Throws away everything cached for filters that <paramref name="snapshot"/> no longer
    /// contains. The key is the whole predicate chain, so a filter that has been deleted or edited can never
    /// be asked about again; disabled filters keep their results, which is what makes switching one back on
    /// instant. Called on every filter change - including the one that leaves none.</summary>
    public void RetainCachedResults(FilterSnapshot snapshot) => _cache.RetainOnly(snapshot.CacheKeys());

    /// <summary>Bytes currently held by the per-filter match cache.</summary>
    public long CacheBytes => _cache.UsedBytes;

    /// <summary>How many filters currently have results cached.</summary>
    public int CacheCount => _cache.Count;

    /// <summary>How many filter changes were served entirely from cached results, with no pass over the file.</summary>
    public long CacheHits { get; private set; }

    /// <summary>The lines <paramref name="filter"/> matched during the last full pass, when those results
    /// still cover the whole file. Answering "where is the next match" from this is a bit scan rather than a
    /// re-read of the file. False when nothing usable is cached for it.</summary>
    public bool TryGetMatchSet(FilterSnapshot snapshot, Filter filter, out FilterMatchCache.MatchSet set)
    {
        set = null!;
        if (!_indexComplete()) return false;
        long lines = _completedCount();
        if (lines <= 0) return false;
        if (!snapshot.TryGetCacheKey(filter, out string key)) return false;
        return _cache.TryGet(key, lines, out set);
    }

    /// <summary>Cancels the current generation (if any) and goes idle. Use when there are no enabled
    /// filters so <see cref="IsIdle"/> reports true at once and the UI can clear its "busy" state and
    /// progress bar immediately, instead of leaving an orphaned pass running to completion.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            _current = null;
            _processed = 0;
        }
        _wake.Set();
    }

    /// <summary>Signals that more indexed lines are available (or indexing completed).</summary>
    public void Notify() => _wake.Set();

    /// <summary>True when there is no current generation, or the current generation has processed all
    /// completed lines and indexing has finished (used by tests and the self-test harness).</summary>
    public bool IsIdle
    {
        get
        {
            lock (_lock)
            {
                if (_current is null) return true;
                return _indexComplete() && _processed >= _completedCount();
            }
        }
    }

    /// <summary>Number of lines the current generation has finished evaluating (for progress display).</summary>
    public long ProcessedLineCount
    {
        get { lock (_lock) return _current is null ? 0 : _processed; }
    }

    private void Loop()
    {
        while (!_disposed)
        {
            _wake.WaitOne();
            if (_disposed) break;

            Generation? gen;
            CancellationToken ct;
            lock (_lock) { gen = _current; ct = _cts.Token; }
            if (gen is null) continue;

            try { ProcessAvailable(gen, ct); }
            catch (OperationCanceledException) { /* superseded generation */ }
        }
    }

    private void ProcessAvailable(Generation gen, CancellationToken ct)
    {
        if (!gen.Seeded)
        {
            gen.Seeded = true;
            // Start from what the user is currently looking at so the view morphs in place instead of
            // blanking: every line when no filters were active, otherwise the previous pass's results
            // (already held in the shared set).
            if (gen.SeedAllVisible) _visible.FillVisible(_completedCount());
            _visible.Publish();

            // If every participating filter's results are already cached for the whole file, the new visible
            // set is just a bitwise combine of them - no reading, decoding or matching at all.
            if (TryApplyFromCache(gen))
            {
                lock (_lock)
                {
                    if (!ReferenceEquals(gen, _current)) return;
                    _processed = _completedCount();
                }
                Progress?.Invoke(gen);
                return;
            }
            StartCacheBuild(gen);
        }

        while (!ct.IsCancellationRequested)
        {
            long from;
            lock (_lock)
            {
                if (!ReferenceEquals(gen, _current)) return;
                from = _processed;
            }

            long to = _completedCount();

            // A cache build records whole 64-line words, and ProcessBlock assumes each block starts on a word
            // boundary. Indexing hands us an arbitrary number of lines, so while it is still running, stop at
            // the last whole word and pick the remainder up next time.
            if (gen.CacheBuild is not null && !_indexComplete()) to &= ~63L;

            if (from >= to)
            {
                StoreCacheIfComplete(gen, from);
                return; // caught up; wait for next Notify
            }

            // Process in ordered blocks, publishing progress after each so the UI can show both the
            // streaming matches and a live "lines analyzed" count that climbs smoothly to the total.
            for (long start = from; start < to && !ct.IsCancellationRequested; start += Block)
            {
                int len = (int)Math.Min(Block, to - start);
                ProcessBlock(gen, start, len, ct);

                lock (_lock)
                {
                    if (!ReferenceEquals(gen, _current)) return;
                    _processed = start + len;
                }
                Progress?.Invoke(gen);
            }
        }
    }

    /// <summary>Rebuilds the visible set purely from cached per-filter results. Only possible once indexing is
    /// finished and every participating filter has a cached set covering the whole file.</summary>
    private bool TryApplyFromCache(Generation gen)
    {
        if (!_indexComplete()) return false;
        long lines = _completedCount();
        if (lines <= 0) return false;
        if (!gen.Snapshot.TryGetCacheableFilters(out var filters) || filters.Count == 0) return false;

        var includes = new List<FilterMatchCache.MatchSet>();
        var excludes = new List<FilterMatchCache.MatchSet>();
        var counts = new long[gen.Counts.Length];
        foreach (var filter in filters)
        {
            if (!_cache.TryGet(filter.Key, lines, out var set)) return false;
            if (!filter.Enabled) continue;                 // counts only track enabled filters
            counts[filter.Index] = set.Matches;
            (filter.IsExclude ? excludes : includes).Add(set);
        }

        var shown = new ulong[(lines + 63) / 64];
        FilterMatchCache.Combine(includes, excludes, gen.Snapshot.HasEnabledInclude, lines, shown);
        _visible.ReplaceAll(shown, lines);
        _visible.Publish();
        lock (gen.CountsSync) Array.Copy(counts, gen.Counts, counts.Length);
        gen.CacheStored = true;   // nothing new to remember
        CacheHits++;
        return true;
    }

    /// <summary>Prepares accumulators so this pass also remembers each filter's results for next time.
    /// Indexing need not be finished: opening a file with filters already applied starts evaluation at 0%
    /// indexed, and that pass is the one most worth remembering. The pass must start at line 0 though - a set
    /// missing the head of the file could never be reused - and only complete results are ever stored.</summary>
    private void StartCacheBuild(Generation gen)
    {
        lock (_lock) { if (_processed != 0) return; }
        if (!gen.Snapshot.TryGetCacheableFilters(out var filters) || filters.Count == 0) return;

        var builders = new FilterMatchCache.SetBuilder?[gen.Counts.Length];
        long lines = _completedCount();
        foreach (var filter in filters) builders[filter.Index] = new FilterMatchCache.SetBuilder(lines);
        gen.CacheBuild = builders;
        gen.CacheFilters = filters;
    }

    /// <summary>Stores the accumulated results once the pass has covered the whole file.</summary>
    private void StoreCacheIfComplete(Generation gen, long processed)
    {
        if (gen.CacheStored || gen.CacheBuild is null || gen.CacheFilters is null) return;
        if (!_indexComplete() || processed < _completedCount()) return;

        gen.CacheStored = true;
        foreach (var filter in gen.CacheFilters)
        {
            var builder = gen.CacheBuild[filter.Index];
            if (builder is not null) _cache.Store(filter.Key, builder.Build(processed));
        }
        gen.CacheBuild = null;
        gen.CacheFilters = null;
    }

    private void ProcessBlock(Generation gen, long start, int len, CancellationToken ct)
    {
        bool[] shown = ArrayPool<bool>.Shared.Rent(len);
        try
        {
            long[] blockCounts = ScanBlock(gen.Snapshot, start, len, shown, gen.CacheBuild, gen.CacheFilters, ct);

            // Update the visible set IN PLACE for this block: lines that still match keep their place, lines
            // that stopped matching are dropped, new matches are added. Then publish the refreshed rank index
            // so readers see a complete, coherent view of the whole file at every instant.
            _visible.ApplyRange(start, shown.AsSpan(0, len));
            _visible.Publish();

            lock (gen.CountsSync)
                for (int i = 0; i < blockCounts.Length; i++) gen.Counts[i] += blockCounts[i];
        }
        finally { ArrayPool<bool>.Shared.Return(shown); }
    }

    /// <summary>Evaluates one block of lines in parallel: fills <paramref name="shown"/> (when asked), feeds
    /// each filter's matching lines to <paramref name="builders"/> (when caching) and returns the per-filter
    /// match counts. Shared by the live filtering pass and by <see cref="PrimeCache"/>, so a find computes
    /// results exactly the same way enabling the filter would.</summary>
    private long[] ScanBlock(FilterSnapshot snapshot, long start, int len, bool[]? shown,
        FilterMatchCache.SetBuilder?[]? builders, List<FilterSnapshot.CacheableFilter>? cacheFilters,
        CancellationToken ct)
    {
        long[] blockCounts = new long[snapshot.FilterCount];
        object mergeLock = new();

        // When caching, each 64-line group records which filters deep-matched its lines. Groups own whole
        // words, so they can be written in parallel without any synchronisation.
        bool caching = builders is not null && cacheFilters is not null;
        int deepWords = caching ? snapshot.DeepMatchWords : 0;   // words in one line's bitset
        int filterCount = snapshot.FilterCount;
        int groups = (len + 63) / 64;
        int deepLength = caching ? filterCount * groups : 0;     // one word per (filter, group)
        ulong[]? deepBits = caching ? ArrayPool<ulong>.Shared.Rent(deepLength) : null;
        if (deepBits is not null) Array.Clear(deepBits, 0, deepLength);

        try
        {
            var options = new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };

            Parallel.For(0, groups, options,
                () => new Worker
                {
                    Reader = new LineReader(_src, _encoding),
                    Counts = new long[snapshot.FilterCount],
                    Context = snapshot.GetThreadContext(),
                    Deep = deepWords == 0 ? Array.Empty<ulong>() : new ulong[deepWords]
                },
                (g, _, w) =>
                {
                    int from = g * 64, until = Math.Min(from + 64, len);
                    for (int k = from; k < until; k++)
                    {
                        long line = start + k;
                        _index.GetRange(line, _fileLength, out long s, out long e);
                        var span = w.Reader.GetChars(s, e);

                        if (deepBits is null)
                        {
                            bool hit = snapshot.Evaluate(span, line, _markers, w.Counts, w.Context).Shown;
                            if (shown is not null) shown[k] = hit;
                            continue;
                        }

                        Array.Clear(w.Deep);
                        bool visible = snapshot.Evaluate(span, line, _markers, w.Counts, w.Context, w.Deep).Shown;
                        if (shown is not null) shown[k] = visible;

                        // Transpose this line's deep matches into per-filter words. Only set bits are visited,
                        // and a line matches very few filters, so this stays cheap.
                        int bitInGroup = k - from;
                        for (int word = 0; word < deepWords; word++)
                        {
                            ulong bits = w.Deep[word];
                            while (bits != 0)
                            {
                                int filter = (word << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits);
                                deepBits[filter * groups + g] |= 1UL << bitInGroup;
                                bits &= bits - 1;
                            }
                        }
                    }
                    return w;
                },
                w => { lock (mergeLock) for (int i = 0; i < blockCounts.Length; i++) blockCounts[i] += w.Counts[i]; });

            // Hand this block's per-filter results to the sets being built.
            if (deepBits is not null && builders is not null && cacheFilters is not null)
            {
                long firstWord = start / 64;   // blocks are word-aligned
                foreach (var filter in cacheFilters)
                {
                    var builder = builders[filter.Index];
                    if (builder is null) continue;
                    int baseIndex = filter.Index * groups;
                    for (int g = 0; g < groups; g++)
                    {
                        ulong word = deepBits[baseIndex + g];
                        if (word != 0) builder.AddWord(firstWord + g, word);
                    }
                }
            }

            return blockCounts;
        }
        finally
        {
            if (deepBits is not null) ArrayPool<ulong>.Shared.Return(deepBits);
        }
    }

    /// <summary>Computes and stores every cacheable filter's matching lines for <paramref name="snapshot"/>
    /// using the same parallel, automaton-driven scan a filter change uses - but without touching the
    /// visible view. This is what makes "find this filter's next match" cost the same as switching the
    /// filter on once, and nothing at all after that.</summary>
    public void PrimeCache(FilterSnapshot snapshot, CancellationToken ct, Action<double>? onProgress = null)
    {
        if (!_indexComplete()) return;                       // partial coverage is never stored
        long lines = _completedCount();
        if (lines <= 0) return;
        if (!snapshot.TryGetCacheableFilters(out var filters) || filters.Count == 0) return;

        var builders = new FilterMatchCache.SetBuilder?[snapshot.FilterCount];
        foreach (var f in filters) builders[f.Index] = new FilterMatchCache.SetBuilder(lines);

        for (long start = 0; start < lines; start += Block)
        {
            ct.ThrowIfCancellationRequested();
            int len = (int)Math.Min(Block, lines - start);
            ScanBlock(snapshot, start, len, null, builders, filters, ct);
            onProgress?.Invoke((start + len) / (double)lines);
        }

        foreach (var f in filters)
            if (builders[f.Index] is { } builder) _cache.Store(f.Key, builder.Build(lines));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock) _cts.Cancel();
        _wake.Set();
        _thread.Join(2000);
        _wake.Dispose();
        _cts.Dispose();
    }
}

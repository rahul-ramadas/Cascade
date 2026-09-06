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
        /// is remembered and later enable/disable changes need no pass at all. A find reads these as they fill,
        /// under the service's lock, so it need not scan the file itself. Null when not caching.</summary>
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

    /// <summary>What the pass that is running now can say about a filter's next match.</summary>
    public enum PassAnswer
    {
        /// <summary>No pass is recording that filter; ask somewhere else.</summary>
        Unavailable,
        /// <summary>The line is the true answer - the sweep has covered everything between it and the start.</summary>
        Found,
        /// <summary>There is no match in that direction, and the sweep has looked everywhere it could be.</summary>
        Exhausted,
        /// <summary>The sweep has not reached the answer yet.</summary>
        NotYet,
    }

    private readonly MemoryMappedTextSource _src;
    private readonly LineIndex _index;
    private readonly long _fileLength;
    private readonly MarkerStore _markers;
    private readonly Encoding _encoding;
    private readonly Func<long> _completedCount;
    private readonly Func<bool> _indexComplete;

    private readonly object _lock = new();
    private readonly Thread _thread;
    // Woken through the lock rather than an event handle: the handle would have to be disposed, and
    // disposing it is exactly what cannot be done safely while a pass may still be running.
    private bool _woken;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // One visible-line set per file, reused by every generation: a filter change re-evaluates lines and
    // updates it in place (keep / drop / add) rather than rebuilding it, so the view is never empty.
    private readonly VisibleLineSet _visible = new();
    // Remembers which lines each filter matched, so toggling filters recombines cached sets instead of
    // re-reading the file.
    private readonly FilterMatchCache _cache = new();
    private Generation? _current;
    private long _processed;
    private long _genId;
    private long _linesScanned;
    private CancellationTokenSource _cts = new();
    private volatile bool _disposed;

    /// <summary>Raised (on the worker thread) when the visible count for the current generation grows.</summary>
    public event Action<Generation>? Progress;

    /// <summary>Completes once the worker has really stopped reading the file. Whoever owns the mapping must
    /// wait for this before freeing it - a pass can be deep inside work that does not answer cancellation
    /// (a regular expression is the usual one), and a wait that gave up would free memory still being read
    /// through a raw pointer.</summary>
    public Task Stopped => _stopped.Task;

    /// <summary>What went wrong the last time a pass failed, or null. A pass runs on a background thread,
    /// where an escaping exception ends the process, so a failure is recorded rather than thrown.</summary>
    public Exception? LastFailure { get; private set; }

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
        // The accumulators are prepared here rather than on the worker so that a find arriving in the same
        // breath as the filter change already has something to read, instead of concluding that nothing is
        // working this filter out and starting a second pass of its own.
        //
        // Per filter, not all-or-nothing: whether one filter's results can be named has no bearing on
        // another's, and treating it as a single decision meant one unnameable chain stopped the whole tree
        // being remembered - turning every later filter change into a fresh pass over the file.
        FilterMatchCache.SetBuilder?[]? builders = null;
        List<FilterSnapshot.CacheableFilter>? cacheFilters = null;
        var cacheable = snapshot.CacheableFilters();
        if (cacheable.Count > 0)
        {
            long lines = _completedCount();
            builders = new FilterMatchCache.SetBuilder?[snapshot.FilterCount];
            foreach (var f in cacheable) builders[f.Index] = new FilterMatchCache.SetBuilder(lines);
            cacheFilters = cacheable;
        }

        Generation gen;
        lock (_lock)
        {
            gen = new Generation(snapshot, FilteredView.CreateExplicit(_visible), ++_genId)
            {
                SeedAllVisible = seedAllVisible,
                CacheBuild = builders,
                CacheFilters = cacheFilters,
            };
            if (_disposed) return gen;   // torn down: nothing will ever run it
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            _current = gen;
            _processed = 0;
            _woken = true;
            Monitor.PulseAll(_lock);
        }
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

    /// <summary>Lines evaluated against the filters since this file was opened, by any caller. A find that
    /// has to work results out for itself adds to this, so it says whether one is duplicating a pass.</summary>
    public long LinesScanned => Interlocked.Read(ref _linesScanned);

    /// <summary>Test seam: runs on the filter worker after each block, so a test can hold a pass at a known
    /// frontier and exercise what happens while one is still in flight.</summary>
    internal Action<long>? AfterBlockForTesting;

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
        // The key names the marks this filter's chain rested on when the snapshot was built. Marks move
        // without a new snapshot whenever no marker filter is switched on - nothing on screen would change -
        // so a key can outlive what it names, and answering from it would be the very staleness that keeping
        // marker results out of the cache used to prevent.
        if (snapshot.ChainMarksMoved(filter, _markers)) return false;
        return _cache.TryGet(key, lines, out set);
    }

    /// <summary>Where <paramref name="filter"/>'s next match lies, according to the pass that is running now.
    /// A pass already works out which lines every filter it evaluates matches; until it finishes those results
    /// are only half-built, but the part it has swept is final - so a find can read them instead of starting a
    /// second whole-file scan alongside it. The sweep runs upwards from line 0, which is why a backward search
    /// cannot be answered until it has passed the line the search started from: a later match could still turn
    /// up before it.</summary>
    public PassAnswer AskCurrentPass(FilterSnapshot snapshot, Filter filter, long from, bool forward,
        long lines, out long hit, out long covered)
    {
        hit = -1;
        covered = 0;
        if (lines <= 0) return PassAnswer.Exhausted;
        if (!snapshot.TryGetIndex(filter, out int index)) return PassAnswer.Unavailable;

        lock (_lock)
        {
            var gen = _current;
            if (gen is null || !ReferenceEquals(gen.Snapshot, snapshot)) return PassAnswer.Unavailable;
            var builders = gen.CacheBuild;
            if (builders is null || index >= builders.Length || builders[index] is not { } builder)
                return PassAnswer.Unavailable;

            covered = Math.Min(_processed, lines);
            if (forward)
            {
                if (from >= lines) return PassAnswer.Exhausted;
                hit = builder.Next(Math.Max(0, from), covered);
                if (hit >= 0) return PassAnswer.Found;
                return covered >= lines ? PassAnswer.Exhausted : PassAnswer.NotYet;
            }

            if (from < 0) return PassAnswer.Exhausted;
            if (covered <= from) return PassAnswer.NotYet;
            hit = builder.Previous(Math.Min(from, lines - 1), covered);
            return hit >= 0 ? PassAnswer.Found : PassAnswer.Exhausted;
        }
    }

    /// <summary>Blocks until the current pass moves on, is replaced, or <paramref name="ct"/> is cancelled.
    /// The timeout is only a backstop - every change pulses.</summary>
    public void WaitForPassProgress(CancellationToken ct)
    {
        using var registration = ct.Register(static state =>
        {
            var self = (FilterService)state!;
            lock (self._lock) Monitor.PulseAll(self._lock);
        }, this);
        lock (_lock) Monitor.Wait(_lock, 100);
    }

    /// <summary>Cancels the current generation (if any) and goes idle. Use when there are no enabled
    /// filters so <see cref="IsIdle"/> reports true at once and the UI can clear its "busy" state and
    /// progress bar immediately, instead of leaving an orphaned pass running to completion.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            _current = null;
            _processed = 0;
            _woken = true;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>Signals that more indexed lines are available (or indexing completed).</summary>
    public void Notify()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _woken = true;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>True when there is no current generation, or the current generation has processed all
    /// completed lines, indexing has finished, and what it worked out has been recorded (used by tests and
    /// the self-test harness).</summary>
    public bool IsIdle
    {
        get
        {
            lock (_lock)
            {
                if (_current is null) return true;
                // A pass is not finished until its per-filter results are stored: the sweep sets _processed
                // to the total and only then hands them over, and a filter change arriving in that window
                // would miss the cache and pay for a whole pass again. CacheBuild is nulled by the store,
                // and is null from the outset for a pass that records nothing.
                return _indexComplete() && _processed >= _completedCount() && _current.CacheBuild is null;
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
        try
        {
            while (true)
            {
                Generation? gen;
                CancellationToken ct;
                lock (_lock)
                {
                    while (!_disposed && !_woken) Monitor.Wait(_lock);
                    if (_disposed) return;
                    _woken = false;
                    gen = _current;
                    ct = _cts.Token;
                }
                if (gen is null) continue;

                try { ProcessAvailable(gen, ct); }
                catch (OperationCanceledException) { /* superseded generation */ }
                catch (Exception ex) { Abandon(gen, ex); }
            }
        }
        finally
        {
            lock (_lock) _cts.Dispose();
            _stopped.TrySetResult();
        }
    }

    /// <summary>Gives up a pass that failed, leaving the view exactly as it stands. Letting the exception
    /// escape would end the process: this runs on a background thread.</summary>
    private void Abandon(Generation gen, Exception failure)
    {
        lock (_lock)
        {
            LastFailure = failure;
            if (ReferenceEquals(gen, _current)) { _current = null; _processed = 0; }
            Monitor.PulseAll(_lock);
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
                    Monitor.PulseAll(_lock);
                }
                Progress?.Invoke(gen);
                return;
            }
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
                    Monitor.PulseAll(_lock);
                }
                Progress?.Invoke(gen);
                AfterBlockForTesting?.Invoke(start + len);
            }
        }
    }

    /// <summary>Test seam: never rebuild the view from cached results, so every filter change really sweeps
    /// the file. Tests that watch a pass in flight - what a half-rewritten view draws, which filters explain
    /// a stretch it has not reached - need a change that sweeps, and enabling a filter is exactly the change
    /// the cache answers instantly. Before marker results could be remembered, those tests parked an unused
    /// marker filter in the list to spoil the cache; saying so outright is honest, and does not break the
    /// moment a marker filter becomes cacheable.</summary>
    public bool SkipCacheForTesting { get; set; }

    /// <summary>Rebuilds the visible set purely from cached per-filter results. Only possible once indexing is
    /// finished and every participating filter has a cached set covering the whole file.</summary>
    private bool TryApplyFromCache(Generation gen)
    {
        if (SkipCacheForTesting) return false;
        if (!_indexComplete()) return false;
        long lines = _completedCount();
        if (lines <= 0) return false;
        // A marker predicate's answer is the marks themselves, which are already in hand - so those sets are
        // made here rather than found, and the combine below then has every term it needs.
        SeedMarkerTailedSets(gen.Snapshot, lines);
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
        lock (_lock)
        {
            gen.CacheStored = true;   // nothing new to remember
            gen.CacheBuild = null;    // and nothing for a find to read - the cache has it all
            gen.CacheFilters = null;
        }
        CacheHits++;
        return true;
    }

    /// <summary>Works out and stores the results of every marker-tailed filter whose chain above it is already
    /// known, <b>without reading the file at all</b>.
    ///
    /// <para>A marker predicate is the one predicate whose answer the app is already holding: the marks are in
    /// the store, so "which lines carry mark 3" needs no scan, no decode and no matching. A filter ending in
    /// one therefore matches exactly its parent's matches that also carry the mark - an intersection of two
    /// sets both in memory. Marking a line and watching a marker filter's results follow costs a walk of the
    /// marked lines, which are hand-picked and so few, rather than a pass over millions.</para>
    ///
    /// <para>Taken in index order, which is the order the tree is drawn, so a chain of markers seeds each of
    /// its own prefixes before it needs them.</para></summary>
    private void SeedMarkerTailedSets(FilterSnapshot snapshot, long lines)
    {
        var tailed = snapshot.MarkerTailedFilters();
        if (tailed.Count == 0) return;

        // The key names the marks a result belongs to, so a result must only be stored under it while those
        // are still the marks. Marking happens on the UI thread and this runs on the worker, so the two can
        // overlap: were the check left out, a set worked out from marks that had already moved on could be
        // filed under the key of the marks it was asked about. Nothing would read it - a version never comes
        // round again - but the pass in flight would briefly show results that belong to no state at all.
        int version = _markers.Version;
        var marks = _markers.Snapshot();

        foreach (var filter in tailed)
        {
            if (_cache.TryGet(filter.Key, lines, out _)) continue;

            // A root marker filter is narrowing nothing, so every marked line qualifies. A nested one can only
            // be built when its prefix is known - otherwise it is left to the pass, which computes the whole
            // chain anyway.
            FilterMatchCache.MatchSet? prefix = null;
            if (filter.HasParent)
            {
                if (!_cache.TryGet(filter.ParentKey, lines, out var found)) continue;
                prefix = found;
            }

            int bit = 1 << filter.MarkerIndex;
            var builder = new FilterMatchCache.SetBuilder(lines);
            // The marks arrive in line order, so whole 64-line words can be gathered and handed over in the
            // ascending order the builder requires.
            long currentWord = -1;
            ulong word = 0;
            foreach (var (line, mask) in marks)
            {
                if ((mask & bit) == 0 || line < 0 || line >= lines) continue;
                if (prefix is not null && !prefix.Contains(line)) continue;

                long w = line >> 6;
                if (w != currentWord)
                {
                    if (word != 0) builder.AddWord(currentWord, word);
                    currentWord = w;
                    word = 0;
                }
                word |= 1UL << (int)(line & 63);
            }
            if (word != 0) builder.AddWord(currentWord, word);

            // The marks moved while this was being worked out, so it describes neither the state the key
            // names nor reliably the new one. Drop it: the change that moved them restarts the pass anyway.
            if (_markers.Version != version) return;
            _cache.Store(filter.Key, builder.Build(lines));
        }
    }

    /// <summary>Stores the accumulated results once the pass has covered the whole file.</summary>
    private void StoreCacheIfComplete(Generation gen, long processed)
    {
        if (gen.CacheStored || gen.CacheBuild is null || gen.CacheFilters is null) return;
        if (!_indexComplete() || processed < _completedCount()) return;

        gen.CacheStored = true;
        lock (_lock)
        {
            foreach (var filter in gen.CacheFilters)
            {
                var builder = gen.CacheBuild[filter.Index];
                if (builder is not null) _cache.Store(filter.Key, builder.Build(processed));
            }
            gen.CacheBuild = null;
            gen.CacheFilters = null;
            Monitor.PulseAll(_lock);   // a find reading this pass must go and look in the cache instead
        }
    }

    private void ProcessBlock(Generation gen, long start, int len, CancellationToken ct)
    {
        bool[] shown = ArrayPool<bool>.Shared.Rent(len);
        try
        {
            long[] blockCounts = ScanBlock(gen.Snapshot, start, len, shown, gen.CacheBuild, gen.CacheFilters,
                                           shareBuilders: true, ct);

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
    /// results exactly the same way enabling the filter would. <paramref name="shareBuilders"/> publishes the
    /// results under the lock, which the pass needs because a find may be reading them as they are written.</summary>
    private long[] ScanBlock(FilterSnapshot snapshot, long start, int len, bool[]? shown,
        FilterMatchCache.SetBuilder?[]? builders, List<FilterSnapshot.CacheableFilter>? cacheFilters,
        bool shareBuilders, CancellationToken ct)
    {
        long[] blockCounts = new long[snapshot.FilterCount];
        object mergeLock = new();
        Interlocked.Add(ref _linesScanned, len);

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
                if (shareBuilders) lock (_lock) Feed();
                else Feed();

                void Feed()
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
    /// visible view. Only reached when no pass is working the filter out already, so it runs at full width:
    /// narrowing it was measured to leave a concurrent pass no faster (the scan is bound by memory bandwidth,
    /// not by cores) while making the search itself markedly slower.</summary>
    public void PrimeCache(FilterSnapshot snapshot, CancellationToken ct, Action<double>? onProgress = null)
    {
        if (!_indexComplete()) return;                       // partial coverage is never stored
        long lines = _completedCount();
        if (lines <= 0) return;

        // A chain ending in a marker is answerable from the marks alone, so take what can be had for nothing
        // before deciding there is a file to read.
        SeedMarkerTailedSets(snapshot, lines);

        var cacheable = snapshot.CacheableFilters();
        if (cacheable.Count == 0) return;

        // Whatever is already known - just seeded, or left behind by an earlier pass - needs neither a
        // builder nor a scan. When that accounts for all of them there is nothing to read at all.
        var filters = cacheable.FindAll(f => !_cache.TryGet(f.Key, lines, out _));
        if (filters.Count == 0) return;

        var builders = new FilterMatchCache.SetBuilder?[snapshot.FilterCount];
        foreach (var f in filters) builders[f.Index] = new FilterMatchCache.SetBuilder(lines);

        for (long start = 0; start < lines; start += Block)
        {
            ct.ThrowIfCancellationRequested();
            int len = (int)Math.Min(Block, lines - start);
            ScanBlock(snapshot, start, len, null, builders, filters, shareBuilders: false, ct);
            onProgress?.Invoke((start + len) / (double)lines);
        }

        foreach (var f in filters)
            if (builders[f.Index] is { } builder) _cache.Store(f.Key, builder.Build(lines));
    }

    /// <summary>Asks the worker to stop. It does <b>not</b> wait: wait on <see cref="Stopped"/> instead,
    /// and only free the file once that has completed.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            Monitor.PulseAll(_lock);
        }
    }
}

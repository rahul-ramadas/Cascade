using System.Text;
using Cascade.Core.Columns;
using Cascade.Core.Filtering;
using Cascade.Core.Find;
using Cascade.Core.Indexing;
using Cascade.Core.IO;
using Cascade.Core.Markers;
using Cascade.Core.Model;
using Cascade.Core.Text;

namespace Cascade.Core.Document;

/// <summary>
/// The integration hub the UI binds to. Owns the memory-mapped source, the streaming line index, the
/// marker store, the filter tree, the column spec, and the streaming filter service, and exposes a
/// simple row-oriented view (respecting dim vs. filtered mode). All heavy work happens on background
/// threads; <see cref="Updated"/> fires (possibly off the UI thread) whenever counts change.
/// </summary>
public sealed class CascadeDocument : IDisposable
{
    private MemoryMappedTextSource _src = null!;
    private LineIndex _index = null!;
    private LineIndexer _indexer = null!;
    private LineReader _uiReader = null!;
    private FilterService _filterService = null!;
    private FilteredView _identityView = null!;
    private CancellationTokenSource _indexCts = new();
    private Task _indexTask = Task.CompletedTask;
    private DetectedEncoding _enc;
    private FilterService.Generation? _generation;
    private CancellationTokenSource? _findCts;
    private Task<long>? _findTask;
    private FindSearch? _search;
    private ThreadLocal<FindEngine.FindMatcher>? _searchMatchers;
    private ThreadLocal<LineReader>? _searchReaders;

    public CascadeDocument()
    {
        _identityView = FilteredView.CreateIdentity(() => CompletedLineCount);
    }

    public string FilePath { get; private set; } = "";
    public long FileLength { get; private set; }
    public Encoding Encoding => _enc.Encoding;

    public MarkerStore Markers { get; } = new();
    public FilterCollection Filters { get; private set; } = new();
    public ColumnSpec Columns { get; private set; } = new();
    public FilterSnapshot CurrentSnapshot { get; private set; } = FilterSnapshot.Build(new FilterCollection());

    /// <summary>Fires when indexing or filtering makes progress (may be raised on a background thread).</summary>
    public event Action? Updated;

    public bool IsIndexComplete => _indexer?.IsComplete ?? false;

    /// <summary>Number of fully-known lines (all but the last while still streaming).</summary>
    public long CompletedLineCount
    {
        get
        {
            long c = _index?.Count ?? 0;
            if (IsIndexComplete) return c;
            return c > 0 ? c - 1 : 0;
        }
    }

    private FilteredView MatchView =>
        (CurrentSnapshot.HasAnyEnabled && _generation is not null) ? _generation.View : _identityView;

    public bool FilteredMode => Filters.ShowOnlyFilteredLines;

    /// <summary>Rows currently displayed (matched lines in filtered mode, all lines in dim mode).</summary>
    public long RowCount => FilteredMode ? MatchView.Count : CompletedLineCount;

    public long RowToLine(long row) => FilteredMode ? MatchView.LineAt(row) : row;

    /// <summary>Maps a file line to its current display row, or -1 if not currently visible.</summary>
    public long RowForLine(long line)
        => FilteredMode ? MatchView.RowForLine(line) : (line >= 0 && line < CompletedLineCount ? line : -1);

    /// <summary>Row of the nearest visible line at or after <paramref name="line"/> (never negative).</summary>
    public long RowAtOrAfterLine(long line)
        => FilteredMode ? MatchView.RowAtOrAfterLine(line) : Math.Clamp(line, 0, Math.Max(0, CompletedLineCount));

    /// <summary>True when the view can actually show <paramref name="line"/>. In dim mode that is every
    /// line; in filtered mode a line can match one filter and still be hidden by an exclude.</summary>
    public bool IsLineVisible(long line)
        => FilteredMode ? MatchView.IsVisible(line) : line >= 0 && line < CompletedLineCount;

    /// <summary>Resolves one whole screen of rows against a <b>single</b> consistent snapshot of the visible
    /// set, anchoring <paramref name="anchorLine"/> at <paramref name="anchorOffset"/> rows from the top and
    /// filling <paramref name="lines"/> with the file lines to paint. A streaming pass keeps adding and
    /// dropping lines while the UI paints, so resolving row by row would mix two states inside one frame.
    /// Returns the first row.</summary>
    public long ResolveWindow(long anchorLine, int anchorOffset, Span<long> lines, out int count)
    {
        if (FilteredMode) return MatchView.ResolveWindow(anchorLine, anchorOffset, lines, out count);
        long first = Math.Clamp(anchorLine - anchorOffset, 0, Math.Max(0, CompletedLineCount - lines.Length));
        count = FillLines(first, lines);
        return first;
    }

    /// <summary>Fills <paramref name="lines"/> with the file lines shown from <paramref name="firstRow"/> on,
    /// resolved against a single snapshot. Returns how many were filled.</summary>
    public int LinesForRows(long firstRow, Span<long> lines)
        => FilteredMode ? MatchView.LinesForRows(firstRow, lines) : FillLines(Math.Max(0, firstRow), lines);

    private int FillLines(long firstRow, Span<long> lines)
    {
        int count = (int)Math.Clamp(CompletedLineCount - firstRow, 0, lines.Length);
        for (int i = 0; i < count; i++) lines[i] = firstRow + i;
        return count;
    }

    /// <summary>Number of lines matching the filters (the status-bar "Fil" count).</summary>
    public long MatchedLineCount => MatchView.Count;

    /// <summary>Bumped every time the filters are re-applied. Anything that summarises the whole file can
    /// key its cache on this instead of recomputing per paint.</summary>
    public int FilterGeneration { get; private set; }

    /// <summary>How many matching lines fall in <c>[from, toExclusive)</c>. Two rank lookups, so summarising
    /// the whole file a band at a time costs the same as summarising one line.</summary>
    public long MatchedLinesInRange(long from, long toExclusive) => MatchView.CountInRange(from, toExclusive);

    /// <summary>The cached set of lines deep-matching <paramref name="filter"/>, when there is one. Only a
    /// summary of the whole file needs this; everything else asks about one line at a time.</summary>
    public FilterMatchCache.MatchSet? MatchSetFor(Filter filter)
        => _filterService is not null && _generation is not null &&
           _filterService.TryGetMatchSet(_generation.Snapshot, filter, out var set) ? set : null;

    /// <summary>Lines the active filter generation has finished analyzing (0 when no filters run).</summary>
    public long FilterProcessedLineCount => _generation is not null ? _filterService.ProcessedLineCount : 0;

    /// <summary>Filter changes served entirely from cached per-filter results, with no pass over the file.</summary>
    public long FilterCacheHits => _filterService?.CacheHits ?? 0;

    /// <summary>Bytes held by the per-filter match cache.</summary>
    public long FilterCacheBytes => _filterService?.CacheBytes ?? 0;

    /// <summary>How many filters currently have results cached.</summary>
    public int FilterCacheCount => _filterService?.CacheCount ?? 0;

    /// <summary>File lines below this value are fully resolved in the current view: every visible line before
    /// it has been discovered, so <see cref="RowForLine"/> is authoritative for them. Because a filter pass
    /// updates the visible set in place, this is the whole file as soon as one pass has covered it — only the
    /// very first pass over a file being indexed reports a smaller extent. Lets the view tell "not evaluated
    /// yet" apart from "filtered out", so it can hold still instead of chasing the scan frontier.</summary>
    public long ViewKnownThroughLine => (FilteredMode && _generation is not null)
        ? Math.Min(_generation.View.KnownLines, CompletedLineCount)
        : CompletedLineCount;

    public bool IsBusy => _src is not null && (!IsIndexComplete || !(_filterService?.IsIdle ?? true));

    public void Open(string path, Encoding? forcedEncoding = null)
    {
        DisposeCurrent();

        FilePath = path;
        _src = new MemoryMappedTextSource(path);
        FileLength = _src.Length;

        int prefixLen = (int)Math.Min(64, _src.Length);
        byte[] prefix = prefixLen > 0 ? _src.Slice(0, prefixLen).ToArray() : Array.Empty<byte>();
        _enc = forcedEncoding is not null
            ? EncodingDetector.ForEncoding(forcedEncoding, prefix)
            : EncodingDetector.Detect(prefix);

        _index = new LineIndex();
        _indexer = new LineIndexer(_src, _index, _enc.PreambleLength, _enc.UnitSize, _enc.BigEndian);
        _uiReader = new LineReader(_src, _enc.Encoding);
        _identityView = FilteredView.CreateIdentity(() => CompletedLineCount);
        _filterService = new FilterService(_src, _index, _src.Length, Markers, _enc.Encoding,
            () => CompletedLineCount, () => IsIndexComplete);
        _filterService.Progress += _ => Updated?.Invoke();

        ApplyFilters();

        _indexCts = new CancellationTokenSource();
        var ct = _indexCts.Token;
        _indexTask = Task.Run(() => _indexer.Run(_ =>
        {
            _filterService.Notify();
            Updated?.Invoke();
        }, ct), ct);
    }

    /// <summary>Rebuilds the filter snapshot and (re)starts streaming evaluation. Call after any edit
    /// to the filter tree, its enabled states, or the filtered/dim mode.</summary>
    public void ApplyFilters()
    {
        CurrentSnapshot = FilterSnapshot.Build(Filters);
        FilterGeneration++;
        if (_filterService is null)
        {
            // No file open yet (e.g. filters auto-loaded at startup). Keep the snapshot so the filters
            // take effect as soon as a file is opened; there is nothing to evaluate against right now.
            _generation = null;
            Updated?.Invoke();
            return;
        }
        if (CurrentSnapshot.HasAnyEnabled)
        {
            // The pass reuses (and updates in place) the existing visible set. When no filters were active the
            // view was showing every line, so seed it that way and let the sweep drop what no longer matches.
            _generation = _filterService.Restart(CurrentSnapshot, seedAllVisible: _generation is null);
        }
        else
        {
            // No enabled filters: cancel any in-flight pass so IsBusy / IsFilterIdle clear immediately,
            // rather than leaving an orphaned run that freezes the progress bar until it finishes.
            _filterService.Stop();
            _generation = null;
        }
        _filterService.Notify();
        Updated?.Invoke();
    }

    public void SetFilters(FilterCollection filters)
    {
        Filters = filters;
        ApplyFilters();
    }

    public string GetLineText(long line)
    {
        if (line < 0 || line >= _index.Count) return "";
        long s = _index.Get(line);
        long e = (line + 1 < _index.Count) ? _index.Get(line + 1) : _src.Length;
        return _uiReader.GetString(s, e);
    }

    public bool IsLineTruncated(long line)
    {
        if (line < 0 || line >= _index.Count) return false;
        long s = _index.Get(line);
        long e = (line + 1 < _index.Count) ? _index.Get(line + 1) : _src.Length;
        return _uiReader.IsTruncated(s, e);
    }

    /// <summary>Evaluates a decoded line against the current filters (for coloring visible rows).</summary>
    public LineEval EvaluateText(ReadOnlySpan<char> text, long line) => CurrentSnapshot.Evaluate(text, line, Markers);

    /// <summary>Lines currently matching (deep-match) <paramref name="filter"/>, or -1 if unknown
    /// (no active filtering generation). The value grows while filtering streams, final when idle.</summary>
    public long MatchCountFor(Filter filter)
    {
        var gen = _generation;
        if (gen is null || !gen.Snapshot.TryGetIndex(filter, out int idx) || idx >= gen.Counts.Length) return -1;
        lock (gen.CountsSync) return gen.Counts[idx];
    }

    public long FindLine(FindQuery query, long startLine, bool forward, CancellationToken ct, Action<double>? onProgress = null)
    {
        if (_src is null) return -1;
        var reader = new LineReader(_src, _enc.Encoding);

        // In dim mode every line is visible, so search the whole file.
        if (!FilteredMode)
            return FindEngine.Find(reader, _index, _src.Length, CompletedLineCount, query, startLine, forward, ct, onProgress);

        // In filtered mode, search ONLY the visible (matched) lines, so the hit is always a line the
        // user can see — otherwise a match on a hidden line would snap the highlight to a different,
        // non-matching visible line.
        var view = MatchView;
        long rows = view.Count;
        if (rows <= 0) return -1;

        long startRow;
        if (forward)
        {
            startRow = view.RowAtOrAfterLine(startLine);
            if (startRow >= rows) return -1; // nothing visible at/after the start point
        }
        else
        {
            long r = view.RowAtOrAfterLine(startLine);
            if (r >= rows || view.LineAt(r) > startLine) r--; // step back to the row at/before startLine
            if (r < 0) return -1;
            startRow = r;
        }
        return FindEngine.FindInRows(reader, _index, _src.Length, rows, view.LineAt, query, startRow, forward, ct, onProgress);
    }

    /// <summary>True while a background find is still running.</summary>
    public bool IsFindRunning => _findTask is { IsCompleted: false };

    /// <summary>Cancels a background find in progress (if any).</summary>
    public void CancelFind() => _findCts?.Cancel();

    /// <summary>Runs the search under the shared find cancellation, so <see cref="IsFindRunning"/> and
    /// <see cref="CancelFind"/> cover it too.</summary>
    public Task<long> FindNextAsync(FindQuery query, long fromLine, bool forward)
    {
        _findCts?.Cancel();
        var cts = new CancellationTokenSource();
        _findCts = cts;
        var task = FindNextAsync(query, fromLine, forward, cts.Token);
        _findTask = task;
        return task;
    }

    /// <summary>How much of the file the current search term has been swept for, 0..1.</summary>
    public double FindProgress => _search?.Progress ?? 1;

    /// <summary>How much of the direction a search is going has been swept, 0..1 - the honest measure of how
    /// far along that search is, since it never waits on the opposite direction.</summary>
    public double FindProgressFor(bool forward) => _search?.ProgressFor(forward) ?? 1;

    /// <summary>True once every line has been examined for the current term.</summary>
    public bool FindComplete => _search?.Complete ?? true;

    /// <summary>The next line matching <paramref name="query"/> from <paramref name="fromLine"/> in the given
    /// direction, or -1 once there are none left. The term is swept for once, in the background, and kept
    /// until the term changes - so asking again costs nothing and no line is ever examined twice.
    ///
    /// Waits when the sweep has not reached the answer yet, which is why -1 always means "no more" rather
    /// than "not found yet".</summary>
    public async Task<long> FindNextAsync(FindQuery query, long fromLine, bool forward, CancellationToken ct)
    {
        if (_src is null) return -1;

        // Until the whole file is indexed there is no fixed set of lines to sweep, so fall back to the plain
        // scan - reading a file while it indexes is the point of the thing.
        if (!IsIndexComplete)
            return await Task.Run(() => FindLine(query, fromLine, forward, ct), ct).ConfigureAwait(false);

        var search = SearchFor(query, fromLine);
        if (search is null) return -1;      // an empty term, or a regex that will not parse
        return await search.NextAsync(fromLine, forward, IsLineVisible, ct).ConfigureAwait(false);
    }

    /// <summary>The search for a term, started if this is the first time it has been asked for. Replacing the
    /// term throws the old one away, which is what keeps one file's worth of results in memory at most.</summary>
    private FindSearch? SearchFor(FindQuery query, long startLine)
    {
        if (_search is { } current && current.Query == query) return current;

        DropSearch();
        if (FindEngine.CompileQuery(query) is null) return null;

        var src = _src!;
        var index = _index;
        var encoding = _enc.Encoding;
        long length = src.Length;
        // One matcher and one reader per thread: a Regex shared across threads hands out a single cached
        // runner, so all but one caller would allocate a fresh one on every line.
        var matchers = new ThreadLocal<FindEngine.FindMatcher>(() => FindEngine.CompileQuery(query)!);
        var readers = new ThreadLocal<LineReader>(() => new LineReader(src, encoding));

        void ScanRange(long from, long count, List<long> hits, CancellationToken ct)
        {
            var matcher = matchers.Value!;
            var reader = readers.Value!;
            long end = from + count;
            for (long line = from; line < end; line++)
            {
                if ((line & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();
                long s = index.Get(line);
                long e = (line + 1 < index.Count) ? index.Get(line + 1) : length;
                if (matcher.Matches(reader.GetChars(s, e))) hits.Add(line);
            }
        }

        _searchMatchers = matchers;
        _searchReaders = readers;
        _search = new FindSearch(query, CompletedLineCount, startLine, ScanRange);
        _search.Start();
        return _search;
    }

    private void DropSearch()
    {
        _search?.Dispose();          // stops the sweep before anything it reads can be freed
        _search = null;
        _searchMatchers?.Dispose();
        _searchMatchers = null;
        _searchReaders?.Dispose();
        _searchReaders = null;
    }

    /// <summary>Finds the next/previous file line (from <paramref name="startLine"/>, exclusive of it via
    /// the caller's +/-1) that deep-matches <paramref name="filter"/>, or -1 if none. Scans decoded
    /// lines directly, so it works regardless of the filtered/dim view or whether the filter is enabled.</summary>
    public long FindLineMatchingFilter(Filter filter, long startLine, bool forward, CancellationToken ct,
        Action<double>? onProgress = null)
    {
        if (_src is null) return -1;
        var snapshot = CurrentSnapshot;
        if (!snapshot.TryGetIndex(filter, out _)) return -1; // filter not in the current snapshot
        long count = CompletedLineCount;
        if (count <= 0) return -1;
        // A superseded or cancelled find must not come back with an answer, even a cached one.
        ct.ThrowIfCancellationRequested();
        long from = forward ? Math.Max(0, startLine) : Math.Min(startLine, count - 1);

        if (_filterService is not null)
        {
            // The filtering pass already records which lines each filter matched, so when that is still
            // valid the answer is a bit scan - no reading, decoding or matching at all.
            if (_filterService.TryGetMatchSet(snapshot, filter, out var set))
                return VisibleMatch(set, from, forward, count);

            // Nothing cached for it yet (typically because the filter is switched off). Compute it exactly
            // as switching it on would - the same automaton, the same parallel block scan - and remember the
            // result, so this costs one pass once and nothing on every later find.
            var findSnapshot = FilterSnapshot.Build(Filters, forceEnabled: filter);
            _filterService.PrimeCache(findSnapshot, ct, onProgress);
            if (_filterService.TryGetMatchSet(findSnapshot, filter, out var primed))
                return VisibleMatch(primed, from, forward, count);
        }

        // Fallback for the cases the cache cannot serve: marker filters, a file still being indexed, or a
        // cache already at its memory budget.
        const int ProgressEvery = 64 * 1024;
        var reader = new LineReader(_src, _enc.Encoding);
        if (forward)
        {
            long span = Math.Max(1, count - from);
            for (long l = from; l < count; l++)
            {
                ct.ThrowIfCancellationRequested();
                if (DeepMatchesLine(reader, snapshot, filter, l) && IsLineVisible(l)) return l;
                if (onProgress is not null && (l - from) % ProgressEvery == 0) onProgress((l - from) / (double)span);
            }
        }
        else
        {
            long span = Math.Max(1, from + 1);
            for (long l = from; l >= 0; l--)
            {
                ct.ThrowIfCancellationRequested();
                if (DeepMatchesLine(reader, snapshot, filter, l) && IsLineVisible(l)) return l;
                if (onProgress is not null && (from - l) % ProgressEvery == 0) onProgress((from - l) / (double)span);
            }
        }
        return -1;
    }

    /// <summary>Steps through cached matches from <paramref name="from"/>, skipping any the view cannot show.
    /// A line can match one filter and still be hidden by an exclude; navigating there would strand the caret
    /// on a neighbouring, non-matching line, and because the next search starts from the caret it would then
    /// return that same hidden line forever.</summary>
    private long VisibleMatch(FilterMatchCache.MatchSet set, long from, bool forward, long count)
    {
        long at = from;
        while (at >= 0 && at < count)
        {
            long hit = forward ? set.Next(at) : set.Previous(at);
            if (hit < 0) return -1;
            if (IsLineVisible(hit)) return hit;
            at = forward ? hit + 1 : hit - 1;
        }
        return -1;
    }

    /// <summary>Runs <see cref="FindLineMatchingFilter"/> on a background thread so the window stays
    /// responsive, cancelling any find already in flight. Shares its cancellation with the text find, so
    /// <see cref="IsFindRunning"/> and <see cref="CancelFind"/> cover both.</summary>
    public Task<long> FindLineMatchingFilterAsync(Filter filter, long startLine, bool forward,
        IProgress<double>? progress = null)
    {
        _findCts?.Cancel();
        var cts = new CancellationTokenSource();
        _findCts = cts;
        Action<double>? onProgress = progress is null ? null : progress.Report;
        var task = Task.Run(() => FindLineMatchingFilter(filter, startLine, forward, cts.Token, onProgress), cts.Token);
        _findTask = task;
        return task;
    }

    private bool DeepMatchesLine(LineReader reader, FilterSnapshot snapshot, Filter filter, long line)
    {
        long s = _index.Get(line);
        long e = (line + 1 < _index.Count) ? _index.Get(line + 1) : _src.Length;
        return snapshot.DeepMatches(reader.GetChars(s, e), line, Markers, filter);
    }

    // ---- test / self-test helpers ----
    public void WaitForIndex() => _indexTask.Wait();
    public bool IsFilterIdle => _filterService?.IsIdle ?? true;

    private void DisposeCurrent()
    {
        // Stop any background find first so it cannot read the memory-mapped file after we free it.
        try { _findCts?.Cancel(); } catch { /* ignore */ }
        try { _findTask?.Wait(2000); } catch { /* ignore */ }
        _findTask = null;
        try { DropSearch(); } catch { /* ignore */ }
        try { _indexCts.Cancel(); } catch { /* ignore */ }
        try { _indexTask.Wait(1000); } catch { /* ignore */ }
        _filterService?.Dispose();
        _src?.Dispose();
    }

    public void Dispose()
    {
        DisposeCurrent();
        _indexCts.Dispose();
    }
}

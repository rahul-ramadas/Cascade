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
    // Readers that have been superseded - a search whose term was replaced, a find that a newer one took
    // over from - but which may still be inside a scan of the file that is open. They are no longer
    // reachable through the fields above, so the release has to be told about them separately or it would
    // free the mapping out from under them.
    private Task _retired = Task.CompletedTask;

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

    /// <summary>The filters the visible set may still be showing rows from, newest first, and empty until
    /// they have changed once. Usually one: the filters in force before the running pass started. A second
    /// appears when the filters are changed AGAIN before a pass can finish - the superseded pass leaves the
    /// stretch it had already rewritten behind it, and only the filters it was running explain those rows.
    /// Past <see cref="MaxRememberedViews"/> the oldest stretch is answered by the filters in force, which
    /// is what every stretch got before any of this existed.</summary>
    private FilterSnapshot[] _viewSnapshots = [];

    private const int MaxRememberedViews = 2;

    /// <summary>Fires when indexing or filtering makes progress (may be raised on a background thread).</summary>
    public event Action? Updated;

    public bool IsIndexComplete => _indexer?.IsComplete ?? false;

    /// <summary>How far indexing has got, as a fraction of the file. Measured in bytes because the number
    /// of lines is only known once the scan finishes, whereas the file's size is known before it starts.</summary>
    public double IndexedFraction
    {
        get
        {
            if (_indexer is null || FileLength <= 0) return 0;
            if (IsIndexComplete) return 1;
            return Math.Clamp(_indexer.ProcessedByteCount / (double)FileLength, 0, 1);
        }
    }

    /// <summary>Number of fully-known lines (all but the last while still streaming).</summary>
    public long CompletedLineCount => _index is null || _indexer is null ? 0 : CompletedLinesOf(_index, _indexer);

    /// <summary>The same, of one particular file's index. A retired pass must keep asking about the file it
    /// was started for, never about whatever is open now.</summary>
    private static long CompletedLinesOf(LineIndex index, LineIndexer indexer)
    {
        long count = index.Count;
        if (indexer.IsComplete) return count;
        return count > 0 ? count - 1 : 0;
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

    /// <summary>The first line at or after <paramref name="line"/> that the filters match, or -1 when there
    /// is none. A rank lookup and a bit scan, so skipping a million unmatched lines costs the same as
    /// skipping one - which is what lets a summary walk past the stretches with nothing in them.</summary>
    public long NextMatchedLine(long line)
    {
        if (line < 0) line = 0;
        var view = MatchView;
        long row = view.RowAtOrAfterLine(line);
        return row >= view.Count ? -1 : view.LineAt(row);
    }

    /// <summary>The last line at or before <paramref name="line"/> that the filters match, or -1.</summary>
    public long PrevMatchedLine(long line)
    {
        if (line < 0) return -1;
        var view = MatchView;
        long row = view.RowAtOrAfterLine(line + 1);
        return row <= 0 ? -1 : view.LineAt(row - 1);
    }

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

    /// <summary>Reads which lines the filters match, 64 to a word, or null when every line does. A summary
    /// that has to know where the matches are wants this rather than a lookup a line at a time: one read
    /// covers thousands of lines, where <see cref="NextMatchedLine"/> is a rank and a select apiece.</summary>
    public VisibleWordReader? MatchedWords => MatchView.VisibleWords;

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

    /// <summary>Lines the filter engine has evaluated since this file was opened. A find that has to work a
    /// filter's matches out for itself adds to this, so it says whether one is duplicating a running pass.</summary>
    public long FilterLinesScanned => _filterService?.LinesScanned ?? 0;

    /// <summary>Test seam: runs on the filter worker after each block, so a test can hold a pass at a known
    /// frontier and exercise what happens while one is still in flight. Public because the app's own
    /// self-test holds a pass to check what the view draws while one is running. Survives <see cref="Open"/>.</summary>
    public Action<long>? FilterCheckpointForTesting
    {
        get => _filterCheckpoint;
        set
        {
            _filterCheckpoint = value;
            if (_filterService is not null) _filterService.AfterBlockForTesting = value;
        }
    }

    private Action<long>? _filterCheckpoint;

    /// <summary>Test seam: runs on a find sweep before each block, so a test can hold a search at a known
    /// point and exercise what happens while one is still reading the file. The sibling of
    /// <see cref="FilterCheckpointForTesting"/> for the search path, and the only way to hold a sweep
    /// deterministically - a pattern that merely takes a long time is at the mercy of whatever the regular
    /// expression engine of the day optimises away. Read live, so clearing it lets the next search run
    /// freely. Once per BLOCK, not per line: never in the way of the scan itself.</summary>
    public Action<long>? FindCheckpointForTesting { get; set; }

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
        DisposeCurrent(releaseAsync: true);

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
        var index = _index;
        var indexer = _indexer;
        _filterService = new FilterService(_src, _index, _src.Length, Markers, _enc.Encoding,
            () => CompletedLinesOf(index, indexer), () => indexer.IsComplete);
        _filterService.Progress += _ => Updated?.Invoke();
        _filterService.AfterBlockForTesting = _filterCheckpoint;

        ApplyFilters();

        _indexCts = new CancellationTokenSource();
        var ct = _indexCts.Token;
        var service = _filterService;
        _indexTask = Task.Run(() => indexer.Run(_ =>
        {
            service.Notify();
            Updated?.Invoke();
        }, ct), ct);
    }

    /// <summary>Rebuilds the filter snapshot and (re)starts streaming evaluation. Call after any edit
    /// to the filter tree, its enabled states, or the filtered/dim mode.</summary>
    public void ApplyFilters()
    {
        // The visible set is REUSED by the next pass and rewritten line by line, so for as long as that pass
        // runs the view is still showing rows the OLD filters put there. Remembering those filters is what
        // lets such a row be drawn as it was until the view really drops it - see ColouringSnapshot. A pass
        // that ran to the end left the whole view reflecting the filters it was running; one that was cut
        // short left only the stretch it had reached, so what came before it has to be kept as well.
        if (IsFilterIdle) _viewSnapshots = [CurrentSnapshot];
        else if (FilterProcessedLineCount > 0)
            _viewSnapshots = [CurrentSnapshot, .. _viewSnapshots.Take(MaxRememberedViews - 1)];
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
        // A filter that has just been deleted or edited can never be asked about again, so its results are
        // dropped here rather than being kept for the life of the file. It has to happen on the way through
        // every filter change, not inside Restart: removing the last filter takes the Stop path below, which
        // is exactly the change that makes the most cached results dead.
        _filterService.RetainCachedResults(CurrentSnapshot);
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
        _index.GetRange(line, _src.Length, out long s, out long e);
        return _uiReader.GetString(s, e);
    }

    public bool IsLineTruncated(long line)
    {
        if (line < 0 || line >= _index.Count) return false;
        _index.GetRange(line, _src.Length, out long s, out long e);
        return LineReader.IsTruncated(s, e);
    }

    /// <summary>Writes every row on show to a file, off the calling thread. Exporting a filtered view of a
    /// large log is minutes of reading and writing, and doing it in hand with the window meant the window
    /// stopped answering for all of it.
    /// <para>What to write is settled HERE, on the caller's thread: the file, its index, its encoding, the
    /// row set and how many rows there are. A retired pass must keep asking about the file it was started
    /// for, so none of it is read from a field again once the writing begins - reopening while this runs
    /// would otherwise have it reading the new file through the old index. The row set itself is read the
    /// same way a background find reads it, which is safe by its own design.</para>
    /// <para>Registered as a reader before returning, so the mapping cannot be freed under it.</para></summary>
    public Task SaveRowsAsync(string path, IProgress<double>? progress, CancellationToken token)
    {
        var src = _src;
        var index = _index;
        var encoding = _enc.Encoding;
        var view = MatchView;
        bool filtered = FilteredMode;
        long rows = RowCount;

        var task = Task.Run(() => AtomicFile.Write(path, writer =>
        {
            var reader = new LineReader(src, encoding);
            for (long r = 0; r < rows; r++)
            {
                // Often enough that Esc feels immediate, rarely enough to cost nothing against the read
                // and the write it sits between.
                if ((r & 0xFFF) == 0)
                {
                    token.ThrowIfCancellationRequested();
                    progress?.Report((double)r / rows);
                }
                long line = filtered ? view.LineAt(r) : r;
                if (line < 0 || line >= index.Count) continue;
                index.GetRange(line, src.Length, out long s, out long e);
                writer.WriteLine(reader.GetString(s, e));
            }
            token.ThrowIfCancellationRequested();
        }), token);          // written as UTF-8, as it always has been - the source encoding decodes only
        Retire(task);
        return task;
    }

    /// <summary>
    /// The filters to draw with, fixed at the moment this is called: take one and use it for a whole frame
    /// (see <see cref="LineColouring"/>), never one per row.
    /// <para>While a pass is running the view and the filters disagree on purpose: the visible set is
    /// rewritten in place, so it still lists rows the OLD filters matched until the sweep reaches them.
    /// Asked about such a row the new filters answer "not shown", which has no colour - and the row is
    /// painted as plain unfiltered text for the frame or two before it disappears, which reads as a white
    /// flash. It is answered by the filters that put it on screen instead, so it simply keeps the
    /// appearance it had until the view drops it. Once the pass settles nothing on screen can be unshown,
    /// so this cannot affect a settled view - and in dim mode nothing is hidden at all, so the change of
    /// colour is the point and is left immediate.</para>
    /// </summary>
    public LineColouring ColouringSnapshot()
        => new(CurrentSnapshot, !FilteredMode || IsFilterIdle ? null : _viewSnapshots, Markers);

    /// <summary>
    /// Lets go of the filters a settled view can no longer be asked about. Each one carries its own
    /// matching automaton - about 400 KB for a filter file of a couple of hundred - and otherwise sits
    /// there until the next filter change, which may never come.
    /// <para>It cannot change what is drawn, whenever it is called. A finished pass has swept the whole
    /// file, so every row reflects the filters in force; what this leaves behind is exactly what the next
    /// filter change would leave, so consulting it could only ever repeat what those filters already said.
    /// While a pass is still running it does nothing at all, because the stretches it has not reached yet
    /// need the older filters to keep their colour.</para>
    /// </summary>
    public void DropRememberedViews()
    {
        if (_viewSnapshots is [var only] && ReferenceEquals(only, CurrentSnapshot)) return;
        if (!IsFilterIdle) return;
        _viewSnapshots = [CurrentSnapshot];
    }

    /// <summary>How many sets of filters the view might still be showing rows from.</summary>
    public int RememberedViewCountForTesting => _viewSnapshots.Length;

    /// <summary>Whether any of them is other than the filters in force - i.e. whether anything is being
    /// held that a settled view has no use for.</summary>
    public bool HoldsOldFiltersForTesting => _viewSnapshots.Any(s => !ReferenceEquals(s, CurrentSnapshot));

    /// <summary>
    /// The filter each of the first <paramref name="count"/> <paramref name="lines"/> takes its colour
    /// from - <c>null</c> where nothing colours it - written into <paramref name="into"/>. A line given as
    /// -1 is skipped and answered <c>null</c>, so a caller can ask about the ones it does not already know.
    /// <para>Exactly what a <see cref="LineColouring"/> answers one line at a time, but <b>across every
    /// core</b>. That matters because the caller is the minimap, which stands for tens of thousands of rows
    /// at once: running the filters over that many lines is a fifth of a second on a single thread, and it
    /// is asked for again on every scroll.</para>
    /// </summary>
    public void ColouringFilters(long[] lines, int count, Filter?[] into)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(into);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Math.Min(lines.Length, into.Length));
        if (count <= 0) return;
        Array.Clear(into, 0, count);
        if (_src is null || _index is null) return;

        // The same object the text view paints a frame with, so the map cannot come to a different answer
        // than the row it stands for while a pass is catching up with the view.
        var colouring = ColouringSnapshot();
        var snapshot = colouring.Filters;
        var markers = Markers;
        var src = _src;
        var index = _index;
        var encoding = _enc.Encoding;
        long length = src.Length;
        long known = index.Count;
        var views = colouring.Previous;

        // Below this the fork and join cost more than the work does.
        const int WorthSharingOut = 512;
        const int MostColouringWorkers = 8;
        if (count < WorthSharingOut)
        {
            var reader = new LineReader(src, encoding);
            var context = snapshot.GetThreadContext();
            for (int i = 0; i < count; i++) One(i, reader, context);
            return;
        }

        // One reader and one match context per partition, never one per line: both carry scratch buffers
        // that would otherwise be reallocated - or, worse, shared between threads.
        //
        // As many threads as there is work for, and no more. Running the filters over a line is about a
        // third of a microsecond, so handing a mapful to all 32 cores of a big machine gives each of them a
        // few dozen microseconds of work in return for waking up, spinning and handing back - measured, on
        // the five and a half thousand rows a mouse report of a drag uncovers, at 8.1 ms of processor time
        // against 2.1 ms of actual work. A thread per thousand lines, up to eight, costs 3.5 ms for the same
        // rows and finishes within a fifth of a millisecond of the greedy version.
        var share = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(count / 1024, 1, Math.Min(MostColouringWorkers, Environment.ProcessorCount))
        };
        Parallel.For(0, count, share, () => new LineReader(src, encoding), (i, _, reader) =>
        {
            One(i, reader, snapshot.GetThreadContext());
            return reader;
        }, _ => { });

        void One(int i, LineReader reader, FilterSnapshot.MatchContext context)
        {
            long line = lines[i];
            if (line < 0 || line >= known) return;
            index.GetRange(line, length, out long s, out long e);
            var text = reader.GetChars(s, e);
            var eval = snapshot.Evaluate(text, line, markers, null, context);
            if (!eval.Shown && views is not null)
                foreach (var was in views)
                {
                    var older = was.Evaluate(text, line, markers, null, was.GetThreadContext());
                    if (older.Shown) { eval = older; break; }
                }
            into[i] = eval.ColorFilter;
        }
    }

    /// <summary>Every filter that deep-matches a line, switched-off ones included, in document order.
    /// For explaining a line to the user; not on any hot path.</summary>
    public List<Filter> FiltersMatching(long line)
    {
        var snapshot = CurrentSnapshot;
        var result = new List<Filter>();
        if (snapshot.NodeCount == 0) return result;

        var bits = new ulong[(snapshot.NodeCount + 63) / 64];
        snapshot.MatchingFilters(GetLineText(line), line, Markers, bits);
        for (int i = 0; i < snapshot.NodeCount; i++)
            if ((bits[i >> 6] & (1UL << (i & 63))) != 0) result.Add(snapshot.FilterAt(i));
        return result;
    }

    /// <summary>Lines currently matching (deep-match) <paramref name="filter"/>, or -1 if unknown
    /// (no active filtering generation). The value grows while filtering streams, final when idle.
    /// A disabled filter counts nothing, as it contributes nothing to the view.
    /// <para>An enabled filter's count is a property of its predicate chain alone - it does not depend on
    /// which filters are enabled - so a result already worked out for the whole file is its answer and is
    /// used as it stands. Without that, every filter's count would restart from zero on every filter change,
    /// because a fresh pass owns fresh accumulators: the whole list would read 0 for as long as the change
    /// took to apply, even though nothing about those filters had changed.</para></summary>
    public long MatchCountFor(Filter filter)
    {
        var gen = _generation;
        if (gen is null || !gen.Snapshot.TryGetIndex(filter, out int idx) || idx >= gen.Counts.Length) return -1;
        if (filter.Enabled && _filterService is not null
            && _filterService.TryGetMatchSet(gen.Snapshot, filter, out var known))
            return known.Matches;
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
        Retire(_findTask);
        var cts = new CancellationTokenSource();
        _findCts = cts;
        var task = FindNextAsync(query, fromLine, forward, cts.Token);
        _findTask = task;
        return task;
    }

    /// <summary>Remembers a reader that has been superseded, so the file it is reading is not freed until it
    /// has really stopped. Cancelling it is not enough: it can be inside work that never looks at the token.
    /// One that has already finished is dropped rather than chained, so a long session of searches cannot
    /// build up a chain of tasks that keeps every earlier one alive.</summary>
    private void Retire(Task? reader)
    {
        if (reader is null || reader.IsCompleted) return;
        var settled = Settled(reader);
        _retired = _retired.IsCompleted ? settled : Task.WhenAll(_retired, settled);
    }

    /// <summary>How much of the file the current search term has been swept for, 0..1.</summary>
    public double FindProgress => _search?.Progress ?? 1;

    /// <summary>How much of the direction a search is going has been swept, 0..1 - the honest measure of how
    /// far along that search is, since it never waits on the opposite direction.</summary>
    public double FindProgressFor(bool forward) => _search?.ProgressFor(forward) ?? 1;

    /// <summary>True once every line has been examined for the current term.</summary>
    public bool FindComplete => _search?.Complete ?? true;

    /// <summary>How much the current term matches, split by what the view is showing. Null when no term is
    /// live.</summary>
    public FindTally? FindTally(long currentLine)
        => _search?.Count(FilteredMode ? MatchView.VisibleWords : null, currentLine);

    /// <summary>Lines the current find term has been found on so far, or 0 when nothing is being looked for.
    /// A summary of the whole file keys its cache on this, so that a sweep filling in matches behind it is
    /// noticed without polling the bitmap.</summary>
    public long FindHitCount => _search?.Found ?? 0;

    /// <summary>How many of the find term's lines fall in <c>[from, toExclusive)</c>.</summary>
    public long FindHitsInRange(long from, long toExclusive) => _search?.HitsInRange(from, toExclusive) ?? 0;

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

    private const int ScanGroupLines = 64;
    private const long ParallelScanThreshold = 16 * 1024;

    /// <summary>Threads per sweep direction. Both directions run at once, so giving each of them every core
    /// would have the two fighting for the same ones.</summary>
    private static readonly int FindParallelism = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>The search for a term, started if this is the first time it has been asked for. Replacing the
    /// term throws the old one away, which is what keeps one file's worth of results in memory at most.</summary>
    private FindSearch? SearchFor(FindQuery query, long startLine)    {
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

        void ScanRange(long from, long count, List<FindHit> hits, CancellationToken ct)
        {
            FindCheckpointForTesting?.Invoke(from);

            // Below this a pass is over before threads could be handed out, and the first block of a sweep
            // is deliberately small so the first result lands at once - that latency must not be traded away
            // for throughput that only matters much later.
            if (count < ParallelScanThreshold) { ScanSequential(from, count, hits, ct); return; }

            // Groups of 64 lines, as the filter pass uses: enough work per group to be worth a thread, small
            // enough that a cancelled search stops promptly.
            int groups = (int)((count + ScanGroupLines - 1) / ScanGroupLines);
            var perThread = new List<List<FindHit>>();
            var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = FindParallelism };
            Parallel.For(0, groups, options,
                () => new List<FindHit>(),
                (g, _, local) =>
                {
                    long start = from + (long)g * ScanGroupLines;
                    ScanSequential(start, Math.Min(ScanGroupLines, from + count - start), local, ct);
                    return local;
                },
                local => { lock (perThread) perThread.Add(local); });

            // Order does not matter: these go into a bitset, and coverage only advances once the whole
            // block is done, so nothing can observe a half-finished range.
            foreach (var list in perThread) hits.AddRange(list);

            void ScanSequential(long start, long lines, List<FindHit> into, CancellationToken token)
            {
                var matcher = matchers.Value!;
                var reader = readers.Value!;
                long end = start + lines;
                for (long line = start; line < end; line++)
                {
                    if ((line & 0x3FFF) == 0) token.ThrowIfCancellationRequested();
                    index.GetRange(line, length, out long s, out long e);
                    int occurrences = matcher.CountIn(reader.GetChars(s, e));
                    if (occurrences > 0) into.Add(new FindHit(line, occurrences));
                }
            }
        }

        _searchMatchers = matchers;
        _searchReaders = readers;
        _search = new FindSearch(query, CompletedLineCount, startLine, ScanRange);
        _search.Start();
        return _search;
    }

    /// <summary>Releases the current search: nothing is looking for that term any more, so its sweep and
    /// the per-thread readers behind it can go. The readers are let go only once the sweep has really
    /// stopped - it reads the file through them - and waiting for that here would freeze the window,
    /// since this runs when the reader presses Escape.</summary>
    public void DropSearch()
    {
        var search = _search;
        var matchers = _searchMatchers;
        var readers = _searchReaders;
        _search = null;
        _searchMatchers = null;
        _searchReaders = null;
        if (search is null)
        {
            matchers?.Dispose();
            readers?.Dispose();
            return;
        }
        search.Dispose();
        Retire(search.Stopped);
        _ = search.Stopped.ContinueWith(_ =>
        {
            matchers?.Dispose();
            readers?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
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

            // A pass may be running right now and already working this filter out. Reading its half-built
            // results costs nothing and waits only until its sweep reaches the answer; scanning the file
            // again alongside it would double the work the machine is doing and finish no sooner.
            if (TryFindInRunningPass(snapshot, filter, from, forward, count, onProgress, ct, out long streamed))
                return streamed;

            // That pass may have finished while we were reading it, which puts its results in the cache.
            if (_filterService.TryGetMatchSet(snapshot, filter, out set))
                return VisibleMatch(set, from, forward, count);

            // Nothing to go on - typically the filter is switched off, so no pass evaluates it. Compute it
            // exactly as switching it on would, over its own chain of predicates and nothing else, and
            // remember the result: this costs one pass once and nothing on every later find.
            var findSnapshot = FilterSnapshot.BuildForChain(Filters, filter);
            _filterService.PrimeCache(findSnapshot, ct, onProgress);
            if (_filterService.TryGetMatchSet(findSnapshot, filter, out var primed))
                return VisibleMatch(primed, from, forward, count);
        }

        // Fallback for the one case the cache cannot serve: a marker somewhere in the filter's own chain,
        // whose results change independently of the filters and so must never be reused. A file still being
        // indexed comes here too, since a set missing the tail of the file could not be stored.
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

    /// <summary>Answers from the filtering pass that is running, waiting for its sweep to reach the answer.
    /// False when no pass is working this filter out, which leaves the caller to compute it. The sweep runs
    /// upwards, so a forward search is answered the moment it crosses the first match below the caret, while
    /// a backward one has to wait for it to pass the caret - either way, never for the whole pass.</summary>
    private bool TryFindInRunningPass(FilterSnapshot snapshot, Filter filter, long from, bool forward,
        long count, Action<double>? onProgress, CancellationToken ct, out long line)
    {
        line = -1;
        long at = from;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var answer = _filterService!.AskCurrentPass(snapshot, filter, at, forward, count,
                                                        out long hit, out long covered);
            switch (answer)
            {
                case FilterService.PassAnswer.Unavailable:
                    return false;

                case FilterService.PassAnswer.Exhausted:
                    return true;

                case FilterService.PassAnswer.Found:
                    if (IsLineVisible(hit)) { line = hit; return true; }
                    at = forward ? hit + 1 : hit - 1;
                    if (at < 0 || at >= count) return true;
                    break;

                default:
                    // Nothing below the frontier matched, so the next look can start there: the search origin
                    // only ever moves the way the search is going, which keeps the total scanning linear.
                    if (forward) at = Math.Max(at, covered);
                    onProgress?.Invoke(forward
                        ? (covered - from) / (double)Math.Max(1, count - from)
                        : covered / (double)Math.Max(1, from + 1));
                    _filterService.WaitForPassProgress(ct);
                    break;
            }
        }
    }

    /// <summary>Runs <see cref="FindLineMatchingFilter"/> on a background thread so the window stays
    /// responsive, cancelling any find already in flight. Shares its cancellation with the text find, so
    /// <see cref="IsFindRunning"/> and <see cref="CancelFind"/> cover both.</summary>
    public Task<long> FindLineMatchingFilterAsync(Filter filter, long startLine, bool forward,
        IProgress<double>? progress = null)
    {
        _findCts?.Cancel();
        Retire(_findTask);
        var cts = new CancellationTokenSource();
        _findCts = cts;
        Action<double>? onProgress = progress is null ? null : progress.Report;
        var task = Task.Run(() => FindLineMatchingFilter(filter, startLine, forward, cts.Token, onProgress), cts.Token);
        _findTask = task;
        return task;
    }

    private bool DeepMatchesLine(LineReader reader, FilterSnapshot snapshot, Filter filter, long line)
    {
        _index.GetRange(line, _src.Length, out long s, out long e);
        return snapshot.DeepMatches(reader.GetChars(s, e), line, Markers, filter);
    }

    // ---- test / self-test helpers ----
    public void WaitForIndex() => _indexTask.Wait();
    public bool IsFilterIdle => _filterService?.IsIdle ?? true;

    /// <summary>Whether the pass now running was told to start from "every line visible" - true only when
    /// the view it is replacing was unfiltered, which includes a file that has just been opened.</summary>
    internal bool CurrentPassSeededFromEverything => _generation?.SeedAllVisible ?? false;

    /// <summary>Completes once the file let go of by the last <see cref="Open"/> has really been unmapped.
    /// Only tests need to wait for it; the app never does.</summary>
    internal Task ReleasePending { get; private set; } = Task.CompletedTask;

    /// <summary>Test seam: runs just before the file is let go, so a test can hold the release open and
    /// prove the window was not waiting for it - or count whether it happened at all. Per document, not
    /// shared: the test suite runs several classes at once and a static hook would stall whatever else
    /// happened to be opening a file.</summary>
    internal Action? ReleaseDelayForTesting;

    private void DisposeCurrent(bool releaseAsync = false)
    {
        // Everything that reads the file is asked to stop here, and NOTHING is waited for with a deadline.
        // The mapping is handed out as a raw pointer, so freeing it while a reader is still inside a scan
        // is an access violation - and a reader can be deep in work that does not answer cancellation at
        // all (a regular expression is the usual one). So the release waits on the readers themselves.
        try { _findCts?.Cancel(); } catch { /* ignore */ }
        try { _indexCts.Cancel(); } catch { /* ignore */ }

        var search = _search;
        var matchers = _searchMatchers;
        var readers = _searchReaders;
        _search = null;
        _searchMatchers = null;
        _searchReaders = null;
        try { search?.Dispose(); } catch { /* ignore */ }

        var service = _filterService;
        _filterService = null!;
        try { service?.Dispose(); } catch { /* ignore */ }

        var findTask = _findTask;
        _findTask = null;
        var indexTask = _indexTask;
        _indexTask = Task.CompletedTask;
        // The generation belongs to the file being let go: the next one must not inherit it, or the first
        // pass over the new file would think it was replacing a view that is already filtered.
        _generation = null;

        var source = _src;
        _src = null!;

        var stopped = Task.WhenAll(service?.Stopped ?? Task.CompletedTask,
                                   search?.Stopped ?? Task.CompletedTask,
                                   _retired,
                                   Settled(findTask), Settled(indexTask));
        _retired = Task.CompletedTask;

        void Release()
        {
            ReleaseDelayForTesting?.Invoke();
            // Only now: these are what the sweeps read the file through.
            matchers?.Dispose();
            readers?.Dispose();
            source?.Dispose();
        }

        if (!releaseAsync)
        {
            // Closing: the window is already down. Give the readers a moment, but never free the mapping
            // because the wait ran out - a process on its way out can leave that to the kernel.
            ReleasePending = stopped;
            try { if (stopped.Wait(ReleaseWaitMs)) Release(); } catch { /* leave it to the kernel */ }
            return;
        }

        // Unmapping a large log makes the kernel hand back every resident page - MEASURED at 973 ms for a
        // 15.8 GB file. So the wait goes to a worker: opening another file is the one time this happens
        // with a window still on screen.
        ReleasePending = stopped.ContinueWith(_ => Release(), CancellationToken.None,
                                              TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>How long closing waits for the readers before leaving the mapping to the kernel.</summary>
    internal int ReleaseWaitMs = 5000;

    /// <summary>A task that completes when <paramref name="task"/> does, however it ends. Waiting on the
    /// originals directly would re-throw the cancellation they were just asked for.</summary>
    private static Task Settled(Task? task)
        => task is null ? Task.CompletedTask
                        : task.ContinueWith(static _ => { }, CancellationToken.None,
                                            TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>True once the file has been let go. Releasing the mapping of a large log is slow, so what
    /// matters is that this happens with the window already down - see MainForm.Dispose.</summary>
    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        // Deliberately synchronous: the window is already down by the time this runs, and the process
        // cannot finish exiting until the address space is torn down anyway.
        try { ReleasePending.Wait(5000); } catch { /* a release that will not finish must not block exit */ }
        DisposeCurrent();
        _indexCts.Dispose();
        IsDisposed = true;
    }
}

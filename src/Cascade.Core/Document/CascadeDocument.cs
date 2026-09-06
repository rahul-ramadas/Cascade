using System.Text;
using Cascade.Core.Columns;
using Cascade.Core.Filtering;
using Cascade.Core.Find;
using Cascade.Core.Indexing;
using Cascade.Core.IO;
using Cascade.Core.Markers;
using Cascade.Core.Model;
using Cascade.Core.Text;
using Cascade.Core.Timing;

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

    /// <summary>The stretch of the file the reader has cropped to, or null for the whole of it. Purely a
    /// matter of what is shown: filtering still reads and remembers every line, and every count the crop
    /// reports is taken from those same whole-file results by two rank lookups.</summary>
    public (long From, long ToExclusive)? Crop { get; private set; }

    /// <summary>Shows only the file lines in <c>[from, toExclusive)</c>. Clamped to the file, and ignored
    /// when that leaves nothing.</summary>
    public bool SetCrop(long from, long toExclusive)
    {
        from = Math.Max(0, from);
        if (toExclusive <= from) return false;
        if (Crop is { } now && now.From == from && now.ToExclusive == toExclusive) return true;
        Crop = (from, toExclusive);
        InvalidateElapsedOrigin();
        Updated?.Invoke();
        return true;
    }

    public void ClearCrop()
    {
        if (Crop is null) return;
        Crop = null;
        InvalidateElapsedOrigin();
        Updated?.Invoke();
    }

    /// <summary>The start of the log is the start of the crop, so moving one moves the other.</summary>
    private void InvalidateElapsedOrigin()
    {
        _firstTimed = long.MinValue;
        _firstTimedFrom = long.MinValue;
        _originTicksFor = long.MinValue;
    }

    /// <summary>The rows on show: the filters' verdict, or every line in dim mode, narrowed to the crop. The
    /// one place the two choices meet, so nothing downstream has to remember to apply either.</summary>
    private FilteredView DisplayView => Cropped(FilteredMode ? MatchView : _identityView, ref _displayCrop);

    /// <summary>What the filters match, narrowed to the crop. Everything the reader is told about matches -
    /// counts, the map, where the next one is - has to stop at the crop's edge, or the file it appears to be
    /// reading would have more in it than it shows.</summary>
    private FilteredView CroppedMatchView => Cropped(MatchView, ref _matchCrop);

    /// <summary>A cropped view is a wrapper worth making once. It is asked for per row of every frame, and
    /// the map asks per pixel of a rebuild, so making one each time would put thousands of throwaway objects
    /// through gen0 for a picture that has not changed. Kept until the crop moves or the view beneath it is
    /// replaced, and swapped as one reference so a reader can never pair a base with another's crop.</summary>
    private sealed record CropOf(FilteredView Base, long From, long ToExclusive, FilteredView View);

    private CropOf? _displayCrop, _matchCrop;

    private FilteredView Cropped(FilteredView view, ref CropOf? cache)
    {
        if (Crop is not { } crop) return view;
        var held = cache;
        if (held is not null && ReferenceEquals(held.Base, view)
            && held.From == crop.From && held.ToExclusive == crop.ToExclusive)
            return held.View;
        var made = view.Cropped(crop.From, crop.ToExclusive);
        cache = new CropOf(view, crop.From, crop.ToExclusive, made);
        return made;
    }

    /// <summary>Rows currently displayed (matched lines in filtered mode, all lines in dim mode).</summary>
    public long RowCount => DisplayView.Count;

    public long RowToLine(long row) => DisplayView.LineAt(row);

    /// <summary>Maps a file line to its current display row, or -1 if not currently visible.</summary>
    public long RowForLine(long line) => DisplayView.RowForLine(line);

    /// <summary>Row of the nearest visible line at or after <paramref name="line"/> (never negative).</summary>
    public long RowAtOrAfterLine(long line) => DisplayView.RowAtOrAfterLine(line);

    /// <summary>True when the view can actually show <paramref name="line"/>. In dim mode that is every
    /// line the crop admits; in filtered mode a line can match one filter and still be hidden by an exclude.</summary>
    public bool IsLineVisible(long line) => DisplayView.IsVisible(line);

    /// <summary>The first line at or after <paramref name="line"/> that the filters match, or -1 when there
    /// is none. A rank lookup and a bit scan, so skipping a million unmatched lines costs the same as
    /// skipping one - which is what lets a summary walk past the stretches with nothing in them.</summary>
    public long NextMatchedLine(long line)
    {
        if (line < 0) line = 0;
        var view = CroppedMatchView;
        long row = view.RowAtOrAfterLine(line);
        return row >= view.Count ? -1 : view.LineAt(row);
    }

    /// <summary>The last line at or before <paramref name="line"/> that the filters match, or -1.</summary>
    public long PrevMatchedLine(long line)
    {
        if (line < 0) return -1;
        var view = CroppedMatchView;
        long row = view.RowAtOrAfterLine(line + 1);
        return row <= 0 ? -1 : view.LineAt(row - 1);
    }

    /// <summary>Resolves one whole screen of rows against a <b>single</b> consistent snapshot of the visible
    /// set, anchoring <paramref name="anchorLine"/> at <paramref name="anchorOffset"/> rows from the top and
    /// filling <paramref name="lines"/> with the file lines to paint. A streaming pass keeps adding and
    /// dropping lines while the UI paints, so resolving row by row would mix two states inside one frame.
    /// Returns the first row.</summary>
    public long ResolveWindow(long anchorLine, int anchorOffset, Span<long> lines, out int count)
        => DisplayView.ResolveWindow(anchorLine, anchorOffset, lines, out count);

    /// <summary>Fills <paramref name="lines"/> with the file lines shown from <paramref name="firstRow"/> on,
    /// resolved against a single snapshot. Returns how many were filled.</summary>
    public int LinesForRows(long firstRow, Span<long> lines) => DisplayView.LinesForRows(firstRow, lines);

    /// <summary>Number of lines matching the filters (the status-bar "Fil" count), within the crop.</summary>
    public long MatchedLineCount => CroppedMatchView.Count;

    /// <summary>Lines the file has, as far as the view is concerned: the crop's own length, so everything
    /// reads as though the file were only that long.</summary>
    public long DisplayLineCount
        => Crop is { } crop ? Math.Max(0, Math.Min(crop.ToExclusive, CompletedLineCount) - crop.From)
                            : CompletedLineCount;

    /// <summary>The first line the view admits, and the last. The file's own numbers, which is what the
    /// reader still sees beside every row.</summary>
    public long FirstDisplayLine => Crop?.From ?? 0;

    public long LastDisplayLine
        => Crop is { } crop ? Math.Min(crop.ToExclusive, CompletedLineCount) - 1 : CompletedLineCount - 1;

    /// <summary>Bumped every time the filters are re-applied. Anything that summarises the whole file can
    /// key its cache on this instead of recomputing per paint.</summary>
    public int FilterGeneration { get; private set; }

    /// <summary>How many matching lines fall in <c>[from, toExclusive)</c>. Two rank lookups, so summarising
    /// the whole file a band at a time costs the same as summarising one line.</summary>
    public long MatchedLinesInRange(long from, long toExclusive)
        => CroppedMatchView.CountInRange(from, toExclusive);

    /// <summary>Reads which lines the filters match, 64 to a word, or null when every line does. A summary
    /// that has to know where the matches are wants this rather than a lookup a line at a time: one read
    /// covers thousands of lines, where <see cref="NextMatchedLine"/> is a rank and a select apiece.</summary>
    public VisibleWordReader? MatchedWords => CroppedMatchView.VisibleWords;

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

    /// <summary>Test seam: <inheritdoc cref="FilterService.SkipCacheForTesting"/> Survives <see cref="Open"/>.</summary>
    public bool SkipFilterCacheForTesting
    {
        get => _skipFilterCache;
        set
        {
            _skipFilterCache = value;
            if (_filterService is not null) _filterService.SkipCacheForTesting = value;
        }
    }

    private bool _skipFilterCache;

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
        // A crop names lines in the file it was set on. Re-reading the SAME file is still that file - F5 after
        // it has grown, or reading it again as another encoding - so the crop stays and is simply clamped to
        // whatever the file now holds. Another file is another set of lines entirely, and it goes.
        if (!string.Equals(FilePath, path, StringComparison.OrdinalIgnoreCase)) Crop = null;
        InvalidateElapsedOrigin();

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
        _filterService.SkipCacheForTesting = _skipFilterCache;

        ApplyFilters();

        _indexCts = new CancellationTokenSource();
        var ct = _indexCts.Token;
        var service = _filterService;
        // A thread of its own rather than the pool: the scan blocks on the file for as long as the file
        // takes, and the pool is where the filter pass runs its per-block Parallel.For at full width.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _indexTask = done.Task;
        new Thread(() =>
        {
            try
            {
                indexer.Run(_ =>
                {
                    service.Notify();
                    Updated?.Invoke();
                }, ct);
                done.SetResult();
            }
            catch (OperationCanceledException) { done.SetCanceled(ct); }
            catch (Exception ex) { done.SetException(ex); }
        })
        { IsBackground = true, Name = "Cascade.Index" }.Start();
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
        CurrentSnapshot = FilterSnapshot.Build(Filters, Markers);
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

    // ---- times ----

    private LogClock? _clock;
    private LineTemplate? _clockTemplate;
    private string _clockFormat = "\u0000";
    private int _clockPart = int.MinValue;
    private bool _clockSettled;
    private long _detectedWith;

    /// <summary>How many lines are read to propose a clock, and the fewest worth judging one by. A file this
    /// long into its own index is available within a moment of opening even when the whole of it is hours
    /// away, and it is far more of the log than a banner at the top of it can spoil.</summary>
    private const int DetectFrom = 500, DetectAtLeast = 100;

    /// <summary>How far back the search for a line to measure from will walk over lines carrying no time.
    /// A capped walk, because a row must cost a bounded amount to draw however much of the log is stack
    /// traces.</summary>
    private const int WalkBack = 64;

    /// <summary>How the timestamps in this log are read, or null when nobody has said and none could be
    /// found. What the reader named in the field template wins; failing that, what could be detected.
    ///
    /// <para>Read on the UI thread, once per row per frame, so the steady state allocates nothing: the
    /// template is compared by reference (a new one is built whenever its text changes) and the rest is two
    /// comparisons.</para>
    ///
    /// <para>Detection runs at most twice per file and is worked out again rather than saved, so opening a
    /// log can never write to the reader's filter set.</para></summary>
    public LogClock? Clock
    {
        get
        {
            if (Columns.HasTime)
            {
                var template = Columns.Compiled;
                if (_clockPart != Columns.TimePart || !ReferenceEquals(_clockTemplate, template)
                    || !string.Equals(_clockFormat, Columns.TimeFormat, StringComparison.Ordinal))
                {
                    _clockPart = Columns.TimePart;
                    _clockTemplate = template;
                    _clockFormat = Columns.TimeFormat;
                    _clock = LogClock.From(Columns);
                    _clockSettled = true;
                    _detectedWith = 0;
                    ForgetTimesRead();
                }
                return _clock;
            }

            if (_clockPart != int.MinValue)
            {
                _clockPart = int.MinValue;
                _clockTemplate = null;
                _clockFormat = "";
                _clock = null;
                _clockSettled = false;
                _detectedWith = 0;
                ForgetTimesRead();
            }
            if (!_clockSettled) Detect();
            return _clock;
        }
    }

    /// <summary>Proposes a clock from the head of the file. Tried as soon as there is enough of the index to
    /// be worth reading, and again only when there is FOUR TIMES as much to go on - a file part way through
    /// a long index would otherwise be re-read on every access, and this is asked once a row per frame.
    /// </summary>
    private void Detect()
    {
        long lines = CompletedLineCount;
        bool complete = IsIndexComplete;
        if (lines < DetectAtLeast && !(complete && lines > 0)) return;
        if (_detectedWith > 0 && lines < _detectedWith * 4 && !complete) return;

        int take = (int)Math.Min(DetectFrom, lines);
        var sample = new List<string>(take);
        for (int i = 0; i < take; i++) sample.Add(GetLineText(i));
        _clock = ClockDetector.Detect(sample);
        _detectedWith = take;
        _clockSettled = take >= DetectFrom || complete;
        ForgetTimesRead();
    }

    /// <summary>Everything the log was READ for rather than counted from, which a new clock invalidates:
    /// naming another field in the field settings turns the same lines into different moments, and these
    /// were all remembered against the line count, which a change of field does not move.</summary>
    private void ForgetTimesRead()
    {
        _widestFor = -1;
        _widestSeconds = ElapsedText.DefaultWidestSeconds;
        _originTicksFor = long.MinValue;
        _firstTimed = long.MinValue;
    }

    /// <summary>Whether the reader named the time field rather than it being guessed at.</summary>
    public bool TimeFieldIsSet => Columns.HasTime && Clock is not null;

    /// <summary>The moment one line was written, or null when it carries no readable stamp.</summary>
    public long? TimeOf(long line) => TimeOf(line, null);

    private long? TimeOf(long line, string? text)
    {
        var clock = Clock;
        if (clock is null || line < 0 || line >= _index.Count) return null;
        return clock.TryRead(text ?? GetLineText(line), out long ticks) ? ticks : null;
    }

    /// <summary>How long after the previous line ON SHOW this one was written.
    ///
    /// <para>"On show" is the whole point of it: with filters hiding the noise between them, this is the
    /// time between one interesting line and the next, which is a latency profile of whatever the filters
    /// select and is not obtainable any other way. Reading it is helped by the line numbers beside it,
    /// which visibly skip.</para>
    ///
    /// <para>Lines carrying no time are stepped over rather than breaking the chain, up to a limit.</para>
    /// </summary>
    public bool TryElapsedBefore(long line, out long elapsed) => TryElapsedBefore(line, null, out elapsed);

    /// <summary><inheritdoc cref="TryElapsedBefore(long, out long)"/></summary>
    /// <param name="text">The line's text where the caller already has it - the paint decodes every row it
    /// draws, and reading it a second time would double what a frame costs.</param>
    public bool TryElapsedBefore(long line, string? text, out long elapsed)
    {
        elapsed = 0;
        var clock = Clock;
        if (clock is null) return false;
        if (TimeOf(line, text) is not { } now) return false;

        long row = RowForLine(line);
        if (row <= 0) return false;
        for (int back = 1; back <= WalkBack && row - back >= 0; back++)
        {
            if (TimeOf(RowToLine(row - back)) is not { } then) continue;
            elapsed = ClockMath.Elapsed(then, now, clock.Format.WrapsAtMidnight);
            return true;
        }
        return false;
    }

    /// <summary>The line every measurement is taken from while the origin is
    /// <see cref="ElapsedOrigin.Reference"/>, or -1 for none. Belongs to the reading rather than to the log
    /// or the filters, so it is never saved: a line number means nothing against a different file.</summary>
    public long ReferenceLine { get; private set; } = -1;

    /// <summary>Whether a line can be measured from - it has to carry a time of its own, or everything
    /// measured against it would be nothing at all.</summary>
    public bool TrySetReference(long line)
    {
        if (TimeOf(line) is null) return false;
        ReferenceLine = line;
        _originTicksFor = long.MinValue;
        return true;
    }

    public void ClearReference()
    {
        ReferenceLine = -1;
        _originTicksFor = long.MinValue;
    }

    /// <summary>What an asked-for origin really comes out as. The choice is a preference and is kept across
    /// sessions; the reference LINE is not, so "from the reference" with none named reads as the previous
    /// line rather than as an empty column.</summary>
    public ElapsedOrigin Resolve(ElapsedOrigin wanted)
        => wanted == ElapsedOrigin.Reference && ReferenceLine < 0 ? ElapsedOrigin.PreviousShown : wanted;

    private long _originTicksFor = long.MinValue, _originTicks;

    /// <summary>The moment a fixed origin stands at, worked out once rather than per row: this is asked for
    /// every line of every frame, and the answer only moves when the reference or the clock does.</summary>
    private bool TryOriginTicks(ElapsedOrigin origin, out long ticks)
    {
        // Asked BEFORE the cache is consulted: reading it is what notices the reader naming another field,
        // and on the reference path nothing else here would touch it.
        if (Clock is null) { ticks = 0; return false; }

        long want = origin == ElapsedOrigin.Reference ? ReferenceLine : FirstTimedLine();
        ticks = _originTicks;
        if (want < 0) return false;
        if (_originTicksFor == want) return true;

        if (TimeOf(want) is not { } found) return false;
        _originTicksFor = want;
        ticks = _originTicks = found;
        return true;
    }

    private long _firstTimed = long.MinValue;
    private long _firstTimedFrom = long.MinValue;

    /// <summary>The first line of the log carrying a time, within the same capped walk everything else
    /// uses - a banner at the top of a file is not a reason to give up on the whole of it.
    /// <para>Of the crop, when there is one: "measured from the start" has to mean the start of the file the
    /// reader appears to be looking at, or every row in a cropped view would read as an offset from a line
    /// the view does not admit.</para></summary>
    private long FirstTimedLine()
    {
        long from = Crop?.From ?? 0;
        if (_firstTimed != long.MinValue && _firstTimedFrom == from) return _firstTimed;
        for (long line = from; line <= from + WalkBack && line < _index.Count; line++)
            if (TimeOf(line) is not null)
            {
                _firstTimedFrom = from;
                return _firstTimed = line;
            }
        return -1;   // not remembered: more of the file may yet be indexed
    }

    /// <summary>How long after <paramref name="origin"/> this line was written. Backwards reads as a
    /// negative, which for a line above the reference is simply what it is.</summary>
    public bool TryElapsedFrom(long line, ElapsedOrigin origin, out long elapsed)
        => TryElapsedFrom(line, origin, null, out elapsed);

    /// <summary><inheritdoc cref="TryElapsedFrom(long, ElapsedOrigin, out long)"/></summary>
    /// <param name="text">The line's text where the caller already has it.</param>
    public bool TryElapsedFrom(long line, ElapsedOrigin origin, string? text, out long elapsed)
    {
        if (origin == ElapsedOrigin.PreviousShown) return TryElapsedBefore(line, text, out elapsed);

        elapsed = 0;
        var clock = Clock;
        if (clock is null) return false;
        if (!TryOriginTicks(origin, out long from)) return false;
        if (TimeOf(line, text) is not { } now) return false;
        elapsed = ClockMath.Elapsed(from, now, clock.Format.WrapsAtMidnight);
        return true;
    }

    private long _widestFor = -1, _widestSeconds = ElapsedText.DefaultWidestSeconds;
    private bool _widestComplete;

    /// <summary>The largest figure the elapsed column could be asked to draw, in whole seconds: no two
    /// lines in the file are further apart than its own span, whatever it is measured from. The column is
    /// sized from this so that it is the same width whichever origin is chosen - a column that changed
    /// width when the origin did would slide the log sideways under the reader who changed it.
    ///
    /// <para>Read from the ends of the FILE and not of the view, or filtering something out would resize
    /// the margin. It grows with the file while that is still being indexed, exactly as the line-number
    /// column does, and is worked out again only when there is twice as much to go on - this is asked once
    /// a frame.</para></summary>
    public long WidestElapsedSeconds()
    {
        var clock = Clock;
        if (clock is null) return ElapsedText.DefaultWidestSeconds;

        long lines = CompletedLineCount;
        bool complete = IsIndexComplete;
        if (_widestFor >= 0 && lines < _widestFor * 2 && complete == _widestComplete) return _widestSeconds;
        _widestFor = lines;
        _widestComplete = complete;

        _widestSeconds = ElapsedText.DefaultWidestSeconds;
        if (lines <= 0) return _widestSeconds;
        if (LineTimeFrom(0, +1, lines) is not { } first) return _widestSeconds;
        if (LineTimeFrom(lines - 1, -1, lines) is not { } last) return _widestSeconds;

        long seconds = Math.Abs(ClockMath.Elapsed(first, last, clock.Format.WrapsAtMidnight))
                     / TimeSpan.TicksPerSecond;
        // Rounded UP to all-nines rather than taken as it is: a log has to grow tenfold before the column
        // changes width, so indexing one does not widen the margin under the reader every few seconds, and
        // a stretch running slightly past the ends that were sampled still has somewhere to be drawn.
        long room = 9;
        while (room < seconds && room < long.MaxValue / 10) room = room * 10 + 9;
        return _widestSeconds = room;
    }

    /// <summary>The first time found walking from one end of the file, over the same capped run of lines
    /// carrying none that everything else here steps across.</summary>
    private long? LineTimeFrom(long start, int step, long lines)
    {
        for (int n = 0; n <= WalkBack; n++)
        {
            long line = start + (long)n * step;
            if (line < 0 || line >= lines) return null;
            if (TimeOf(line) is { } found) return found;
        }
        return null;
    }

    /// <summary>How much time one stretch of the log covers, from the first line of it to the last.
    ///
    /// <para>Either end may be a line carrying no time - a stack trace caught by the end of a drag is the
    /// ordinary case - so the ends are walked INWARD to the nearest line that has one. The stretch measured
    /// is then a little shorter than the stretch selected, which is the honest answer and the only one
    /// available.</para></summary>
    public bool TrySpan(long firstLine, long lastLine, out long span)
    {
        span = 0;
        var clock = Clock;
        if (clock is null) return false;

        long fromRow = RowAtOrAfterLine(Math.Min(firstLine, lastLine));
        long toRow = RowAtOrAfterLine(Math.Max(firstLine, lastLine) + 1) - 1;
        if (fromRow < 0 || toRow < fromRow) return false;

        if (!WalkForTime(fromRow, toRow, +1, out long from)) return false;
        if (!WalkForTime(toRow, fromRow, -1, out long to)) return false;
        span = ClockMath.Elapsed(from, to, clock.Format.WrapsAtMidnight);
        return true;
    }

    /// <summary>The first line from <paramref name="row"/> towards <paramref name="stopRow"/> that carries a
    /// time, within the same capped walk the elapsed column uses.</summary>
    private bool WalkForTime(long row, long stopRow, int step, out long ticks)
    {
        ticks = 0;
        for (int n = 0; n <= WalkBack; n++)
        {
            long at = row + (long)n * step;
            if (step > 0 ? at > stopRow : at < stopRow) return false;
            if (TimeOf(RowToLine(at)) is not { } found) continue;
            ticks = found;
            return true;
        }
        return false;
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
        // The rows on show, crop and all: what this writes out has to be what the reader is looking at.
        var view = DisplayView;
        long rows = view.Count;

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
                long line = view.LineAt(r);
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
    public long MatchCountFor(Filter filter) => MatchCountFor(filter, out _);

    /// <summary>As <see cref="MatchCountFor(Filter)"/>, and tells whether the number is settled.
    /// <para>Finality is per filter, not per pass. A pass being in flight says nothing about a filter whose
    /// whole-file result is already known: that answer came from the cache and the running pass cannot move
    /// it. Only a filter this pass is actually working out - one added or edited, or one whose chain is not
    /// cacheable - has a count still climbing. Without the distinction a new filter would put every
    /// already-settled count in the list back to "still counting" for as long as it took to apply.</para>
    /// <para>Unsettled is the safe answer, so where nothing is known (-1) this follows the pass.</para></summary>
    public long MatchCountFor(Filter filter, out bool final)
    {
        var gen = _generation;
        if (gen is null || !gen.Snapshot.TryGetIndex(filter, out int idx) || idx >= gen.Counts.Length)
        {
            final = IsFilterIdle;
            return -1;
        }
        if (filter.Enabled && _filterService is not null
            && _filterService.TryGetMatchSet(gen.Snapshot, filter, out var known))
        {
            final = true;
            return Crop is { } crop ? known.CountInRange(crop.From, crop.ToExclusive) : known.Matches;
        }

        // Cropped, with nothing remembered for this filter yet - which is only so while the file is still
        // being indexed, since a set is stored the moment it covers the whole of it. The pass accumulates one
        // number for the whole file and cannot say how much of it fell inside the crop, and a whole-file count
        // shown against a cropped view would be a plain lie. "Still counting" is the honest answer, and it is
        // the one already drawn for a number that has not settled.
        if (Crop is not null)
        {
            final = false;
            return -1;
        }

        // Read before the count, never after: an idle seen first can only mean the count read next is at
        // least as settled, whereas the other order could pair "idle now" with a count taken mid-climb.
        bool idle = IsFilterIdle;
        lock (gen.CountsSync)
        {
            final = idle;
            return gen.Counts[idx];
        }
    }

    /// <summary>What <paramref name="filter"/> matches in the whole file, whatever the crop is showing. The
    /// count beside a filter is of the crop; this is what that is a part of, which is the comparison worth
    /// having when a crop is on. -1 when it is not known.</summary>
    public long WholeFileMatchCountFor(Filter filter)
    {
        var gen = _generation;
        if (gen is null || !gen.Snapshot.TryGetIndex(filter, out _)) return -1;
        return filter.Enabled && _filterService is not null
               && _filterService.TryGetMatchSet(gen.Snapshot, filter, out var known) ? known.Matches : -1;
    }

    public long FindLine(FindQuery query, long startLine, bool forward, CancellationToken ct, Action<double>? onProgress = null)
    {
        if (_src is null) return -1;
        var reader = new LineReader(_src, _enc.Encoding);

        // In dim mode with the whole file on show every line is visible, so search it end to end.
        if (!FilteredMode && Crop is null)
            return FindEngine.Find(reader, _index, _src.Length, CompletedLineCount, query, startLine, forward, ct, onProgress);

        // Otherwise search ONLY the rows on show, so the hit is always a line the user can see — a match on
        // a hidden line, or on one the crop keeps out of sight, would snap the highlight to a different,
        // non-matching visible line.
        var view = DisplayView;
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
    /// live. A crop hides lines as surely as a filter does, so it too has words to intersect with - which is
    /// what stops a tally counting hits in a stretch the reader has put out of sight.</summary>
    public FindTally? FindTally(long currentLine)
        => _search?.Count(DisplayView.VisibleWords, currentLine);

    /// <summary>Lines the current find term has been found on so far, or 0 when nothing is being looked for.
    /// A summary of the whole file keys its cache on this, so that a sweep filling in matches behind it is
    /// noticed without polling the bitmap.</summary>
    public long FindHitCount => _search?.Found ?? 0;

    /// <summary>How many of the find term's lines fall in <c>[from, toExclusive)</c>, within the crop.</summary>
    public long FindHitsInRange(long from, long toExclusive)
    {
        if (Crop is { } crop)
        {
            from = Math.Max(from, crop.From);
            toExclusive = Math.Min(toExclusive, crop.ToExclusive);
        }
        return toExclusive <= from ? 0 : _search?.HitsInRange(from, toExclusive) ?? 0;
    }

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
            var findSnapshot = FilterSnapshot.BuildForChain(Filters, filter, Markers);
            _filterService.PrimeCache(findSnapshot, ct, onProgress);
            if (_filterService.TryGetMatchSet(findSnapshot, filter, out var primed))
                return VisibleMatch(primed, from, forward, count);
        }

        // Fallback for the one case the cache cannot serve: a file still being indexed, since a set missing
        // the tail of the file could not be stored.
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
        // A detected clock belongs to the file it was read from, so the next one starts over. A clock the
        // reader NAMED is in the filter set and stays; the comparison in the property rebuilds it either way.
        _clockPart = int.MinValue;
        _clockTemplate = null;
        _clockFormat = "\u0000";
        _clock = null;
        _clockSettled = false;
        _detectedWith = 0;
        // The reference is a line number, which means nothing once a different file is open behind it.
        ReferenceLine = -1;
        _originTicksFor = long.MinValue;
        _firstTimed = long.MinValue;

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

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

    /// <summary>Number of lines matching the filters (the status-bar "Fil" count).</summary>
    public long MatchedLineCount => MatchView.Count;

    /// <summary>Lines the active filter generation has finished analyzing (0 when no filters run).</summary>
    public long FilterProcessedLineCount => _generation is not null ? _filterService.ProcessedLineCount : 0;

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
        _generation = CurrentSnapshot.HasAnyEnabled ? _filterService.Restart(CurrentSnapshot) : null;
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

    public long FindLine(FindQuery query, long startLine, bool forward, CancellationToken ct)
    {
        var reader = new LineReader(_src, _enc.Encoding);
        return FindEngine.Find(reader, _index, _src.Length, CompletedLineCount, query, startLine, forward, ct);
    }

    /// <summary>Finds the next/previous file line (from <paramref name="startLine"/>, exclusive of it via
    /// the caller's +/-1) that deep-matches <paramref name="filter"/>, or -1 if none. Scans decoded
    /// lines directly, so it works regardless of the filtered/dim view or whether the filter is enabled.</summary>
    public long FindLineMatchingFilter(Filter filter, long startLine, bool forward, CancellationToken ct)
    {
        if (_src is null) return -1;
        var snapshot = CurrentSnapshot;
        if (!snapshot.TryGetIndex(filter, out _)) return -1; // filter not in the current snapshot
        long count = CompletedLineCount;
        if (count <= 0) return -1;

        var reader = new LineReader(_src, _enc.Encoding);
        if (forward)
        {
            for (long l = Math.Max(0, startLine); l < count; l++)
            {
                ct.ThrowIfCancellationRequested();
                if (DeepMatchesLine(reader, snapshot, filter, l)) return l;
            }
        }
        else
        {
            for (long l = Math.Min(startLine, count - 1); l >= 0; l--)
            {
                ct.ThrowIfCancellationRequested();
                if (DeepMatchesLine(reader, snapshot, filter, l)) return l;
            }
        }
        return -1;
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

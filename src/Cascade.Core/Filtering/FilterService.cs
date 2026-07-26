using System.Buffers;
using System.Text;
using Cascade.Core.Indexing;
using Cascade.Core.IO;
using Cascade.Core.Markers;

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
    }

    private sealed class Worker
    {
        public LineReader Reader = null!;
        public long[] Counts = null!;
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

    /// <summary>Starts a fresh generation for a changed filter set, cancelling any in-flight run.</summary>
    public Generation Restart(FilterSnapshot snapshot)
    {
        Generation gen;
        lock (_lock)
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
            gen = new Generation(snapshot, FilteredView.CreateExplicit(), ++_genId);
            _current = gen;
            _processed = 0;
        }
        _wake.Set();
        return gen;
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
        while (!ct.IsCancellationRequested)
        {
            long from;
            lock (_lock)
            {
                if (!ReferenceEquals(gen, _current)) return;
                from = _processed;
            }

            long to = _completedCount();
            if (from >= to) return; // caught up; wait for next Notify

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

    private void ProcessBlock(Generation gen, long start, int len, CancellationToken ct)
    {
        bool[] shown = ArrayPool<bool>.Shared.Rent(len);
        long[] blockCounts = new long[gen.Counts.Length];
        object mergeLock = new();
        try
        {
            var options = new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };

            Parallel.For(0, len, options,
                () => new Worker { Reader = new LineReader(_src, _encoding), Counts = new long[gen.Counts.Length] },
                (k, _, w) =>
                {
                    long line = start + k;
                    long s = _index.Get(line);
                    long e = (line + 1 < _index.Count) ? _index.Get(line + 1) : _fileLength;
                    var span = w.Reader.GetChars(s, e);
                    shown[k] = gen.Snapshot.Evaluate(span, line, _markers, w.Counts).Shown;
                    return w;
                },
                w => { lock (mergeLock) for (int i = 0; i < blockCounts.Length; i++) blockCounts[i] += w.Counts[i]; });

            for (int k = 0; k < len; k++)
                if (shown[k]) gen.View.Append(start + k);

            lock (gen.CountsSync)
                for (int i = 0; i < blockCounts.Length; i++) gen.Counts[i] += blockCounts[i];
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(shown);
        }
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

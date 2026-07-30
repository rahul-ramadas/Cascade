namespace Cascade.Core.Find;

/// <summary>Examines lines <paramref name="from"/> to <paramref name="from"/> + <paramref name="count"/> and
/// appends every one that matches to <paramref name="hits"/>. Whether that reads a file, and whether it uses
/// one thread or all of them, is the caller's business - a search only cares about the answers.</summary>
public delegate void FindRangeScanner(long from, long count, List<long> hits, CancellationToken ct);

/// <summary>Every line one search term matches, gathered once and kept until the term changes.
///
/// The sweep starts at the line the user was on and grows outwards in both directions, so the first result
/// is available almost at once and Find Previous is answerable just as early as Find Next. Asking for a
/// match that has not been reached yet waits for the sweep rather than starting a second one, so no line is
/// ever examined twice for the same term.
///
/// Hidden lines are examined too. Which results the user can actually be sent to is decided when the
/// question is asked, so changing the filters never invalidates what has been gathered.</summary>
public sealed class FindSearch : IDisposable
{
    private const long FirstBlockLines = 8 * 1024;      // small, so the first result lands almost at once
    private const long MaxBlockLines = 256 * 1024;

    private readonly object _sync = new();
    private readonly LineBitSet _hits;
    private readonly FindRangeScanner _scanner;
    private readonly long _lines;
    private readonly long _start;
    private readonly CancellationTokenSource _cts = new();

    private long _lo, _hi;              // lines [_lo, _hi) have been examined
    private long _found;
    private bool _stopped;
    private Exception? _failure;
    private Task[] _sweeps = Array.Empty<Task>();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FindSearch(FindQuery query, long lines, long startLine, FindRangeScanner scanner)
    {
        Query = query;
        _lines = Math.Max(0, lines);
        _start = Math.Clamp(startLine, 0, Math.Max(0, _lines - 1));
        _scanner = scanner;
        _hits = new LineBitSet(_lines);
        _lo = _hi = _start;
    }

    public FindQuery Query { get; }

    /// <summary>How much of the file has been examined, 0..1.</summary>
    public double Progress
    {
        get { lock (_sync) return _lines <= 0 ? 1 : Math.Clamp((double)(_hi - _lo) / _lines, 0, 1); }
    }

    /// <summary>How much of ONE direction has been examined, 0..1. A search only ever waits on the way it is
    /// going, so this is what its progress bar must show: whole-file coverage stops short of 100% whenever
    /// the other side has less ground to cover, which is most of the time.</summary>
    public double ProgressFor(bool forward)
    {
        lock (_sync)
        {
            long span = forward ? _lines - _start : _start;
            long done = forward ? _hi - _start : _start - _lo;
            return span <= 0 ? 1 : Math.Clamp((double)done / span, 0, 1);
        }
    }

    public bool Complete { get { lock (_sync) return _lo <= 0 && _hi >= _lines; } }

    /// <summary>Matches found so far. Only meaningful once <see cref="Complete"/>.</summary>
    public long Found { get { lock (_sync) return _found; } }

    public void Start()
    {
        if (_lines <= 0) { lock (_sync) { _lo = 0; _hi = 0; Pulse(); } return; }
        _sweeps = new[]
        {
            Task.Run(() => Sweep(forward: true, _cts.Token)),
            Task.Run(() => Sweep(forward: false, _cts.Token)),
        };
    }

    /// <summary>The next match in the given direction from <paramref name="from"/> (inclusive) that
    /// <paramref name="visible"/> accepts, or -1 once that direction is exhausted. Waits for the sweep when
    /// the answer is not known yet; -1 is only ever returned once that direction has been fully examined, so
    /// "no more matches" is never reported early.</summary>
    public async Task<long> NextAsync(long from, bool forward, Func<long, bool>? visible, CancellationToken ct)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_stopped, this);
                if (_failure is not null) throw new InvalidOperationException("The search could not be completed.", _failure);
                if (TryAnswer(from, forward, visible, out long line)) return line;
                changed = _changed.Task;
            }
            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the answer follows from what has been examined so far. Called under the lock.</summary>
    private bool TryAnswer(long from, bool forward, Func<long, bool>? visible, out long line)
    {
        line = -1;
        bool Shown(long l) => visible is null || visible(l);

        if (forward)
        {
            // Below the examined range is still unknown, and could hold the answer - unless the sweep has
            // already reached the start of the file, in which case there is no unknown region left.
            if (from < _lo && _lo > 0) return false;
            for (long at = Math.Max(from, _lo); ;)
            {
                long hit = _hits.Next(at);
                if (hit < 0 || hit >= _hi) break;
                if (Shown(hit)) { line = hit; return true; }
                at = hit + 1;
            }
            return _hi >= _lines;   // nothing left to examine, so there is genuinely nothing more
        }

        if (from >= _hi && _hi < _lines) return false;
        for (long at = Math.Min(from, _hi - 1); at >= _lo;)
        {
            long hit = _hits.Previous(at);
            if (hit < 0 || hit < _lo) break;
            if (Shown(hit)) { line = hit; return true; }
            at = hit - 1;
        }
        return _lo <= 0;
    }

    private void Sweep(bool forward, CancellationToken ct)
    {
        var hits = new List<long>();
        long edge = _start;
        long block = FirstBlockLines;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                long from, count;
                if (forward)
                {
                    if (edge >= _lines) break;
                    from = edge;
                    count = Math.Min(block, _lines - edge);
                }
                else
                {
                    if (edge <= 0) break;
                    count = Math.Min(block, edge);
                    from = edge - count;
                }

                hits.Clear();
                _scanner(from, count, hits, ct);

                lock (_sync)
                {
                    foreach (long h in hits) { if (!_hits.Contains(h)) _found++; _hits.Add(h); }
                    if (forward) _hi = from + count; else _lo = from;
                    Pulse();
                }

                edge = forward ? from + count : from;
                block = Math.Min(MaxBlockLines, block * 2);
            }
        }
        catch (OperationCanceledException) { /* the term moved on, or the file closed */ }
        catch (Exception ex)
        {
            lock (_sync) { _failure ??= ex; Pulse(); }
        }
    }

    /// <summary>Wakes everything waiting on the sweep. Called under the lock, so a waiter that took the task
    /// before releasing it cannot miss the change that happened in between.</summary>
    private void Pulse()
    {
        var stale = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stale.TrySetResult();
    }

    /// <summary>Test seam: waits for the whole file to have been examined.</summary>
    internal bool WaitForCompletion(int timeoutMs = 30000)
        => _sweeps.Length == 0 || Task.WaitAll(_sweeps, timeoutMs);

    public void Dispose()
    {
        lock (_sync)
        {
            if (_stopped) return;
            _stopped = true;
            Pulse();          // anything still waiting is for a term nobody wants any more
        }
        try { _cts.Cancel(); } catch { /* ignore */ }
        // The scanner reads the file, so it has to have stopped before the caller frees it.
        try { Task.WaitAll(_sweeps, 5000); } catch { /* ignore */ }
        _cts.Dispose();
    }
}

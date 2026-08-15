using System.Numerics;

namespace Cascade.Core.Find;

/// <summary>A line that matched, and how many times it did.</summary>
public readonly record struct FindHit(long Line, int Occurrences);

/// <summary>Examines lines <paramref name="from"/> to <paramref name="from"/> + <paramref name="count"/> and
/// appends every one that matches to <paramref name="hits"/>. Whether that reads a file, and whether it uses
/// one thread or all of them, is the caller's business - a search only cares about the answers.</summary>
public delegate void FindRangeScanner(long from, long count, List<FindHit> hits, CancellationToken ct);

/// <summary>Fills <paramref name="words"/> with the visibility of the lines starting at
/// <paramref name="fromWord"/> * 64, one bit per line.</summary>
public delegate void VisibleWordReader(long fromWord, Span<ulong> words);

/// <summary>How much a term matches, split by what the view is currently showing.</summary>
/// <param name="Position">Which visible match the caret is on, 1-based, or 0 when it is not on one.</param>
/// <param name="Approximate"><see cref="VisibleOccurrences"/> is a floor. <see cref="Occurrences"/> is
/// always exact - what a cap can cost is the record of WHICH lines matched more than once, and that is
/// only needed to split the total between shown and hidden lines.</param>
public readonly record struct FindTally(long Position, long VisibleLines, long HiddenLines,
                                        long VisibleOccurrences, long Occurrences, bool Complete, bool Approximate);

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
    private long _occurrences;
    // Only lines matching MORE than once are recorded: almost every hit line matches exactly once, so this
    // stays tiny in practice, and the cap keeps a pathological term ("e" in prose) from eating memory.
    private readonly Dictionary<long, int> _extras = new();
    private bool _extrasCapped;
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

    /// <summary>Completes once both sweeps have really stopped reading the file. Whoever owns the mapping
    /// must wait for this before freeing it: a sweep can be inside a scan that does not answer cancellation
    /// promptly, and a wait that gave up would free memory still being read. Valid from construction, so an
    /// observer that sees the search at all cannot mistake it for one that has finished.</summary>
    public Task Stopped => _sweepsDone.Task;

    private readonly TaskCompletionSource _sweepsDone = new(TaskCreationOptions.RunContinuationsAsynchronously);

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

    /// <summary>How many matching lines fall in <c>[from, toExclusive)</c>. Whole words at a time, so
    /// summarising the file band by band costs one pass over the bitmap, not one per band.</summary>
    public long HitsInRange(long from, long toExclusive)
    {
        lock (_sync) return _hits.CountInRange(from, toExclusive);
    }

    private const int MaxExtraLines = 2_000_000;

    /// <summary>Words of visibility to read at a time. Big enough that the call is amortised away, small
    /// enough to sit on the stack.</summary>
    private const int VisibilityChunk = 512;

    /// <summary>How much has been found, split by whether the view is currently showing it.
    ///
    /// <paramref name="visible"/> reads the visibility of 64 lines per word, or is null when nothing is
    /// hidden. Both are answered a machine word at a time: asking line by line meant twenty million
    /// callbacks on a common term, which was 160 ms of frozen window every time the caret moved.</summary>
    public FindTally Count(VisibleWordReader? visible, long currentLine)
    {
        lock (_sync)
        {
            bool complete = _lo <= 0 && _hi >= _lines;

            // Nothing hidden: the totals are the ones already kept as the sweep runs, so the only thing left
            // to work out is where the caret sits among them.
            if (visible is null)
            {
                long at = _hits.Contains(currentLine) ? _hits.CountUpTo(currentLine) : 0;
                return new FindTally(at, _found, 0, _occurrences, _occurrences, complete, false);
            }

            long visibleLines = 0, hiddenLines = 0, position = 0;
            long caretWord = currentLine < 0 ? -1 : currentLine >> 6;
            bool onVisibleHit = false;
            Span<ulong> shownWords = stackalloc ulong[VisibilityChunk];

            for (long start = 0; start < _hits.WordCount; start += VisibilityChunk)
            {
                int n = (int)Math.Min(VisibilityChunk, _hits.WordCount - start);
                visible(start, shownWords[..n]);
                for (int i = 0; i < n; i++)
                {
                    ulong hit = _hits.Word(start + i);
                    if (hit == 0) continue;
                    ulong shown = hit & shownWords[i];
                    visibleLines += BitOperations.PopCount(shown);
                    hiddenLines += BitOperations.PopCount(hit & ~shown);

                    long w = start + i;
                    if (w < caretWord) position += BitOperations.PopCount(shown);
                    else if (w == caretWord)
                    {
                        int bit = (int)(currentLine & 63);
                        ulong upTo = bit == 63 ? ulong.MaxValue : (1UL << (bit + 1)) - 1;
                        position += BitOperations.PopCount(shown & upTo);
                        onVisibleHit = (shown & (1UL << bit)) != 0;
                    }
                }
            }

            return new FindTally(onVisibleHit ? position : 0, visibleLines, hiddenLines,
                                 VisibleOccurrences(visible, visibleLines, hiddenLines), _occurrences,
                                 // With nothing hidden every occurrence is shown, so the split is exact
                                 // however little of the per-line record survived the cap.
                                 complete, _extrasCapped && hiddenLines > 0);
        }
    }

    /// <summary>Occurrences on the lines the view is showing.
    ///
    /// Three ways to the same number, and which is cheapest depends entirely on the shape of the data: the
    /// lines shown, the lines hidden, and the lines that matched more than once can each be the small one.
    /// Filtering a 33M-line trace down to a screenful leaves 60 shown against two million recorded, so
    /// reading the recorded list would be 20 ms of frozen window for an answer that 60 lookups give.</summary>
    private long VisibleOccurrences(VisibleWordReader visible, long visibleLines, long hiddenLines)
    {
        if (hiddenLines == 0) return _occurrences;      // nothing kept back, so all of them are shown
        if (_extras.Count == 0) return visibleLines;    // one occurrence each, so lines and hits agree

        // Counting up from the shown lines is the only way that stays a floor once the record is capped;
        // subtracting the hidden ones would credit the shown side with occurrences nobody counted.
        bool byShown = _extrasCapped || visibleLines <= hiddenLines;
        long side = byShown ? visibleLines : hiddenLines;
        if (side <= _extras.Count)
        {
            long counted = 0;
            Span<ulong> words = stackalloc ulong[VisibilityChunk];
            for (long start = 0; start < _hits.WordCount; start += VisibilityChunk)
            {
                int n = (int)Math.Min(VisibilityChunk, _hits.WordCount - start);
                visible(start, words[..n]);
                for (int i = 0; i < n; i++)
                {
                    ulong hit = _hits.Word(start + i);
                    if (hit == 0) continue;
                    ulong walk = byShown ? hit & words[i] : hit & ~words[i];
                    while (walk != 0)
                    {
                        long line = ((start + i) << 6) + BitOperations.TrailingZeroCount(walk);
                        if (_extras.TryGetValue(line, out int extra)) counted += extra;
                        walk &= walk - 1;
                    }
                }
            }
            return byShown ? visibleLines + counted : _occurrences - hiddenLines - counted;
        }

        long total = visibleLines;
        Span<ulong> word = stackalloc ulong[1];
        long cached = -1;
        foreach (var (line, extra) in _extras)
        {
            long w = line >> 6;
            if (w != cached) { visible(w, word); cached = w; }
            if ((word[0] & (1UL << (int)(line & 63))) != 0) total += extra;
        }
        return total;
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_stopped || _lines <= 0)
            {
                if (_lines <= 0) { _lo = 0; _hi = 0; Pulse(); }
                _sweepsDone.TrySetResult();
                return;
            }
        }
        _sweeps = new[]
        {
            Task.Run(() => Sweep(forward: true, _cts.Token)),
            Task.Run(() => Sweep(forward: false, _cts.Token)),
        };
        _ = Task.WhenAll(_sweeps).ContinueWith(_ => _sweepsDone.TrySetResult(), CancellationToken.None,
                                               TaskContinuationOptions.None, TaskScheduler.Default);
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
        var hits = new List<FindHit>();
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
                    foreach (var h in hits)
                    {
                        if (_hits.Contains(h.Line)) continue;
                        _hits.Add(h.Line);
                        _found++;
                        int occ = Math.Max(1, h.Occurrences);
                        _occurrences += occ;
                        if (occ > 1)
                        {
                            if (_extras.Count < MaxExtraLines) _extras[h.Line] = occ - 1;
                            else _extrasCapped = true;
                        }
                    }
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

    /// <summary>Same, for tests outside this assembly.</summary>
    public bool WaitForCompletionForTesting(int timeoutMs = 30000) => WaitForCompletion(timeoutMs);

    /// <summary>Asks the sweeps to stop. It does <b>not</b> wait for them: wait on <see cref="Stopped"/>
    /// instead, and only free the file once that has completed.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_stopped) return;
            _stopped = true;
            Pulse();          // anything still waiting is for a term nobody wants any more
        }
        try { _cts.Cancel(); } catch { /* ignore */ }
        // A search that never ran has no sweep to wait for; one that did completes through Start's
        // continuation, and only then is the source nothing is using any more.
        if (_sweeps.Length == 0) _sweepsDone.TrySetResult();
        _ = Stopped.ContinueWith(static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                                 _cts, CancellationToken.None,
                                 TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

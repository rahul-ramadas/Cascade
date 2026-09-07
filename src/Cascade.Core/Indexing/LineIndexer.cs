using System.Runtime.InteropServices;
using Cascade.Core.IO;

namespace Cascade.Core.Indexing;

/// <summary>Progress notification raised while indexing streams. <see cref="LineCount"/> is the
/// number of line starts known so far; <see cref="Completed"/> is true once EOF is reached.</summary>
public readonly record struct IndexProgress(long LineCount, bool Completed);

/// <summary>
/// Scans a <see cref="MemoryMappedTextSource"/> for line starts and streams them into a
/// <see cref="LineIndex"/>. Newline detection uses SIMD <c>IndexOf</c> for single-byte encodings and
/// a correct per-code-unit scan for UTF-16/32. Lines are split on <c>\n</c>; a preceding <c>\r</c>
/// is trimmed at decode time. A trailing newline at EOF does not create an empty final line.
/// </summary>
public sealed class LineIndexer
{
    private const int ChunkSize = 4 * 1024 * 1024;

    // Asked for in whole ranges on a thread of its own: one range reaching this far ahead lets the OS keep
    // several reads in flight, where the scan's own faults can only ever have one.
    private const long PrefetchChunk = 32L * 1024 * 1024;
    private const long PrefetchLead = 64L * 1024 * 1024;

    /// <summary>How much of the file one page-toucher claims before handing on to the next. Large enough
    /// that two of them never work the same page, small enough that they finish a band well inside the
    /// window they are allowed to work in.</summary>
    private const long TouchBand = 2L * 1024 * 1024;

    private readonly MemoryMappedTextSource _src;
    private readonly int _preamble;
    private readonly int _unit;
    private readonly bool _bigEndian;

    public LineIndex Index { get; }
    public bool IsComplete { get; private set; }

    /// <summary>Bytes scanned so far. The line count is not knowable until the end, but the file's size is
    /// known from the outset, so this is what makes indexing progress an actual fraction rather than a
    /// barber's pole. Safe to read from any thread.</summary>
    public long ProcessedByteCount => Volatile.Read(ref _processed);
    private long _processed;

    /// <summary>Bands touched in front of what the read-ahead had asked for. Must stay zero: past that line
    /// a page nobody has asked for is a fault that goes to the disk on its own, and several of those at once
    /// is what turns one large read into a scatter of small ones. Nothing else can catch the bound being
    /// dropped, because dropping it changes no answer and on a warm machine no timing either.</summary>
    internal long TouchedAheadOfReadAhead => Volatile.Read(ref _touchedAhead);
    private long _touchedAhead;

    public LineIndexer(MemoryMappedTextSource src, LineIndex index, int preamble, int unit, bool bigEndian)
    {
        _src = src;
        Index = index;
        _preamble = preamble;
        _unit = unit;
        _bigEndian = bigEndian;
    }

    /// <summary>Runs the scan to completion (call on a background thread). <paramref name="onProgress"/>
    /// is invoked after each chunk and once more at completion; keep it cheap.</summary>
    public void Run(Action<IndexProgress>? onProgress, CancellationToken ct)
    {
        long length = _src.Length;
        if (length <= _preamble)
        {
            IsComplete = true;
            Volatile.Write(ref _processed, length);
            onProgress?.Invoke(new IndexProgress(0, true));
            return;
        }

        Index.Add(_preamble); // first line starts right after any BOM

        using (StartReadAhead(length, ct))
        {
            if (_unit == 1) ScanSingleByte(length, onProgress, ct);
            else ScanCodeUnits(length, onProgress, ct);
        }

        IsComplete = true;
        Volatile.Write(ref _processed, length);
        onProgress?.Invoke(new IndexProgress(Index.Count, true));
    }

    private void ScanSingleByte(long length, Action<IndexProgress>? onProgress, CancellationToken ct)
    {
        long pos = _preamble;
        while (pos < length)
        {
            ct.ThrowIfCancellationRequested();
            int chunk = (int)Math.Min(ChunkSize, length - pos);
            ReadOnlySpan<byte> span = _src.Slice(pos, chunk);

            int searchStart = 0;
            while (searchStart < chunk)
            {
                int idx = span.Slice(searchStart).IndexOf((byte)0x0A);
                if (idx < 0) break;
                long p = pos + searchStart + idx;
                long next = p + 1;
                if (next < length) Index.Add(next);
                searchStart += idx + 1;
            }

            pos += chunk;
            Volatile.Write(ref _processed, pos);
            onProgress?.Invoke(new IndexProgress(Index.Count, false));
        }
    }

    private void ScanCodeUnits(long length, Action<IndexProgress>? onProgress, CancellationToken ct)
    {
        long pos = _preamble;
        while (pos < length)
        {
            ct.ThrowIfCancellationRequested();
            int chunk = (int)Math.Min(ChunkSize, length - pos);
            chunk -= chunk % _unit;
            if (chunk == 0) break;
            ReadOnlySpan<byte> span = _src.Slice(pos, chunk);

            // Searched as whole code units, so a 0x0A byte inside another character is never mistaken for a
            // newline. The value to look for is the newline as it reads out of memory, which is what makes
            // the big-endian case a different constant rather than a different loop.
            if (_unit == 2)
                ScanUnits(MemoryMarshal.Cast<byte, char>(span), pos, length, _bigEndian ? '\u0A00' : '\u000A');
            else
                ScanUnits(MemoryMarshal.Cast<byte, uint>(span), pos, length, _bigEndian ? 0x0A000000u : 0x0000000Au);

            pos += chunk;
            Volatile.Write(ref _processed, pos);
            onProgress?.Invoke(new IndexProgress(Index.Count, false));
        }
    }

    /// <summary>Keeps the OS reading ahead of the scan, on its own thread, and the pages it has asked for
    /// mapped into this process on a few more. These are two different jobs, and the file needs both.
    ///
    /// <para>The scan takes ONE PAGE FAULT PER PAGE whatever is done here - MEASURED at 534,941 faults over
    /// a 2 GB log with no read-ahead, 535,008 with it, and 535,107 with the touchers as well. Neither
    /// mechanism removes a single fault. What they change is what a fault COSTS and how many can be
    /// outstanding at once.</para>
    ///
    /// <para><c>PrefetchVirtualMemory</c> answers the first: it turns the disk behind those faults into a
    /// few large sequential reads instead of one 4 KB read at a time. Issuing it from the scanning thread
    /// does not work - it does not return until the reads are under way, so it stalls the very scan it is
    /// meant to feed (MEASURED cold on a 19.3 GB log: 12.1 s demand-paged, 7.6 s asked for inline, 5.0 s
    /// from a thread of its own).</para>
    ///
    /// <para>The touchers answer the second: a scan walking the mapping can only ever have one fault in
    /// flight, because it stops at each one. Reading a byte per page on several threads takes the same
    /// faults eight at a time. That is worth as much again as the read-ahead, and it is the whole of the
    /// cost on a file already in the cache, where no fault waits on a disk at all.</para>
    ///
    /// <para>MEASURED on a 2 GB log, the four combinations - the reason both are here and the reason the
    /// touchers are bounded:</para>
    /// <list type="bullet">
    /// <item>cold, neither: 1,046 ms</item>
    /// <item>cold, read-ahead only: 569 ms</item>
    /// <item>cold, read-ahead and touchers: 316 ms</item>
    /// <item>cold, TOUCHERS WITHOUT THE READ-AHEAD: 1,155 ms - WORSE THAN DOING NOTHING</item>
    /// <item>warm, read-ahead only: 537 ms; warm, both: 215 ms</item>
    /// </list>
    /// <para>That last cold figure is why <see cref="ReadAhead"/> holds the touchers behind the prefetch
    /// frontier rather than merely behind the scan. Ahead of it they fault pages nobody has asked for, one
    /// scattered small read each, and they undo the read-ahead entirely.</para>
    ///
    /// <para>Splitting the SCAN across eight threads instead measured the same (142 ms against 146 ms warm),
    /// so this gets what that would while leaving the index single-writer, in order, and streaming exactly
    /// as it was.</para>
    /// <para>Disposing joins every thread, so the read-ahead can never outlive the scan and therefore never
    /// outlives the mapping it reads through - the release waits on the indexing task.</para></summary>
    private ReadAhead? StartReadAhead(long length, CancellationToken ct)
        => length < PrefetchChunk ? null : new ReadAhead(this, length, ct);

    private sealed class ReadAhead : IDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly List<Thread> _threads = new();

        /// <summary>How far <see cref="MemoryMappedTextSource.Prefetch"/> has been asked to reach. The
        /// touchers stay behind it: in front of that line a page nobody has asked for is a fault that goes
        /// to the disk on its own, and several at once is exactly what collapses one 32 MB read into a
        /// scatter of small ones. MEASURED cold on a 2 GB log: 316 ms with this bound, and 1,155 ms with
        /// the touchers let loose without a read-ahead at all - WORSE than the 1,046 ms of doing neither.
        /// Behind it the page is resident or already on its way, so the fault costs no extra I/O.</summary>
        private long _prefetchedThrough;

        public ReadAhead(LineIndexer owner, long length, CancellationToken ct)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var stop = _stop.Token;

            Start("Cascade.ReadAhead", () =>
            {
                long at = owner._preamble;
                while (at < length && !stop.IsCancellationRequested)
                {
                    while (at - owner.ProcessedByteCount > PrefetchLead && !stop.IsCancellationRequested)
                        Thread.Sleep(1);
                    if (stop.IsCancellationRequested) return;
                    long take = Math.Min(PrefetchChunk, length - at);
                    owner._src.Prefetch(at, take);
                    at += take;
                    Volatile.Write(ref _prefetchedThrough, at);
                }
                // Nothing left to ask for: let the touchers run to the end rather than wait on a line that
                // will never move again.
                Volatile.Write(ref _prefetchedThrough, long.MaxValue);
            });

            // Eight was the knee on a 24-core machine (4 threads 166 ms, 8 threads 146 ms, 16 threads
            // 153 ms); half the cores keeps that shape on smaller machines without oversubscribing them.
            int touchers = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
            for (int i = 0; i < touchers; i++)
            {
                int me = i, n = touchers;
                Start("Cascade.Touch", () =>
                {
                    long sink = 0;
                    long bands = (length + TouchBand - 1) / TouchBand;
                    for (long b = me; b < bands && !stop.IsCancellationRequested; b += n)
                    {
                        long at = b * TouchBand;
                        long take = Math.Min(TouchBand, length - at);
                        // Wholly behind the read-ahead, and no further in front of the scan than the
                        // read-ahead is allowed to be. A toucher that ran past the read-ahead would fault a
                        // page nobody had asked for, on its own, which is what turns one large read into a
                        // scatter of small ones; one that ran past the scan would make pages resident long
                        // before they are wanted, and on a file larger than memory evict its own head.
                        while (!stop.IsCancellationRequested &&
                               (at + take > Volatile.Read(ref _prefetchedThrough) ||
                                at - owner.ProcessedByteCount > PrefetchLead))
                            Thread.Sleep(0);
                        if (stop.IsCancellationRequested) break;
                        if (at + take > Volatile.Read(ref _prefetchedThrough))
                            Interlocked.Increment(ref owner._touchedAhead);
                        sink += owner._src.Touch(at, take);
                    }
                    Volatile.Write(ref Touched, sink);
                });
            }
        }

        /// <summary>Kept only so that reading the pages cannot be optimised away.</summary>
        internal static long Touched;

        private void Start(string name, Action work)
        {
            // Reading ahead is only a hint, and these are background threads - an exception escaping one
            // would end the process over work whose failure costs nothing but speed.
            var t = new Thread(() => { try { work(); } catch { /* the scan pages the file in for itself */ } })
            { IsBackground = true, Name = name };
            _threads.Add(t);
            t.Start();
        }

        public void Dispose()
        {
            _stop.Cancel();
            foreach (var t in _threads) t.Join();
            _stop.Dispose();
        }
    }

    /// <summary>Records a line start after every newline in one chunk of code units. Vectorised through
    /// <see cref="MemoryExtensions.IndexOf{T}(ReadOnlySpan{T}, T)"/>: walking a unit at a time was MEASURED
    /// at four times slower over the same bytes, which is why a UTF-16 log indexed far slower than a UTF-8
    /// one of the same size.</summary>
    private void ScanUnits<T>(ReadOnlySpan<T> units, long chunkStart, long length, T newline)
        where T : struct, IEquatable<T>
    {
        for (int at = 0; at < units.Length;)
        {
            int hit = units[at..].IndexOf(newline);
            if (hit < 0) return;
            at += hit + 1;
            long next = chunkStart + (long)at * _unit;
            if (next < length) Index.Add(next);
        }
    }
}

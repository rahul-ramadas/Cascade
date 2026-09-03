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

    /// <summary>Keeps the OS reading ahead of the scan, on its own thread. Demand paging alone leaves one
    /// fault outstanding at a time, so throughput is that fault's latency however idle the disk is. Issuing
    /// the read-ahead from the scanning thread does not work - <c>PrefetchVirtualMemory</c> does not return
    /// until the reads are under way, so it stalls the very scan it is meant to feed. MEASURED cold on a
    /// 19.3 GB log: 12.1 s demand-paged, 7.6 s asked for inline, 5.0 s from here.
    /// <para>Disposing joins the thread, so the read-ahead can never outlive the scan and therefore never
    /// outlives the mapping it reads through - the release waits on the indexing task.</para></summary>
    private ReadAhead? StartReadAhead(long length, CancellationToken ct)
        => length < PrefetchChunk ? null : new ReadAhead(this, length, ct);

    private sealed class ReadAhead : IDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly Thread _thread;

        public ReadAhead(LineIndexer owner, long length, CancellationToken ct)
        {
            _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var stop = _stop.Token;
            _thread = new Thread(() =>
            {
                // Reading ahead is only a hint, and this is a background thread - an exception escaping
                // here would end the process over work whose failure costs nothing but speed.
                try
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
                    }
                }
                catch { /* the scan pages the file in for itself either way */ }
            })
            { IsBackground = true, Name = "Cascade.ReadAhead" };
            _thread.Start();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _thread.Join();
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

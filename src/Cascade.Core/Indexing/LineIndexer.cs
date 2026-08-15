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

        if (_unit == 1) ScanSingleByte(length, onProgress, ct);
        else ScanCodeUnits(length, onProgress, ct);

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

using System.Numerics;
using Cascade.Core.Find;

namespace Cascade.Core.Filtering;

public readonly record struct FilterNavigationTally(long Position, long Total, bool Complete);

internal sealed class FilterNavigationIndex
{
    private const int BlockWords = 64;
    private readonly FilterMatchCache.MatchSet _matches;
    private readonly VisibleWordReader? _visible;
    private readonly long[]? _prefix;

    internal FilterNavigationIndex(FilterMatchCache.MatchSet matches, VisibleWordReader? visible = null,
                                   CancellationToken cancellationToken = default)
    {
        _matches = matches;
        _visible = visible;
        cancellationToken.ThrowIfCancellationRequested();
        if (matches._dense is null && visible is null) return;

        int length = matches._dense?.Length ?? matches._sparseCount;
        int blocks = (length + BlockWords - 1) / BlockWords;
        _prefix = new long[blocks + 1];
        long count = 0;
        for (int block = 0; block < blocks; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = block * BlockWords;
            int end = Math.Min(start + BlockWords, length);
            count += matches._dense is not null ? CountDense(start, end) : CountSparse(start, end);
            _prefix[block + 1] = count;
        }
    }

    internal long Bytes => (_prefix?.LongLength ?? 0) * sizeof(long);
    internal long Count => _prefix is null ? _matches.Matches : _prefix[^1];

    internal long Find(long from, bool forward, long cropFrom, long cropTo)
    {
        if (cropTo <= cropFrom) return -1;
        from = forward ? Math.Max(from, cropFrom) : Math.Min(from, cropTo - 1);
        if (from < cropFrom || from >= cropTo) return -1;
        long row = forward ? CountBefore(from) : CountBefore(from + 1) - 1;
        long line = LineAt(row);
        return line >= cropFrom && line < cropTo ? line : -1;
    }

    private long LineAt(long row)
    {
        if (row < 0 || row >= Count) return -1;
        if (_prefix is null) return _matches._sparse![row];
        int low = 0, high = _prefix.Length - 2;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_prefix[middle + 1] <= row) low = middle + 1;
            else high = middle;
        }
        row -= _prefix[low];
        int start = low * BlockWords;
        if (_matches._dense is not { } dense)
        {
            int end = Math.Min(start + BlockWords, _matches._sparseCount);
            for (int entry = start; entry < end; entry++)
            {
                long line = _matches._sparse![entry];
                if (Contains(line) && row-- == 0) return line;
            }
            return -1;
        }

        int limit = Math.Min(start + BlockWords, dense.Length);
        Span<ulong> visible = stackalloc ulong[BlockWords];
        if (_visible is not null) _visible(start, visible[..(limit - start)]);
        for (int word = start; word < limit; word++)
        {
            ulong bits = dense[word];
            if (_visible is not null) bits &= visible[word - start];
            int count = BitOperations.PopCount(bits);
            if (row >= count) { row -= count; continue; }
            while (row-- > 0) bits &= bits - 1;
            return ((long)word << 6) + BitOperations.TrailingZeroCount(bits);
        }
        return -1;
    }

    internal bool Contains(long line)
    {
        if (!_matches.Contains(line)) return false;
        if (_visible is null) return true;
        Span<ulong> word = stackalloc ulong[1];
        _visible(line >> 6, word);
        return (word[0] & (1UL << (int)(line & 63))) != 0;
    }

    internal long CountBefore(long line)
    {
        if (line <= 0) return 0;
        if (line >= _matches.Covered) return Count;
        if (_matches._dense is null)
        {
            int end = _matches.LowerBound(line);
            if (_prefix is null) return end;
            int sparseBlock = end / BlockWords;
            return _prefix[sparseBlock] + CountSparse(sparseBlock * BlockWords, end);
        }

        int endWord = (int)(line >> 6);
        int block = endWord / BlockWords;
        int tail = (int)(line & 63);
        return _prefix![block] + (tail == 0
            ? CountDense(block * BlockWords, endWord)
            : CountDense(block * BlockWords, endWord + 1, (1UL << tail) - 1));
    }

    private long CountDense(int start, int end, ulong lastMask = ulong.MaxValue)
    {
        if (end <= start) return 0;
        Span<ulong> visible = stackalloc ulong[BlockWords];
        if (_visible is not null) _visible(start, visible[..(end - start)]);
        var dense = _matches._dense!;
        long count = 0;
        for (int word = start; word < end; word++)
        {
            ulong bits = dense[word];
            if (_visible is not null) bits &= visible[word - start];
            if (word == end - 1) bits &= lastMask;
            count += BitOperations.PopCount(bits);
        }
        return count;
    }

    private long CountSparse(int start, int end)
    {
        Span<ulong> visible = stackalloc ulong[1];
        long previousWord = -1, count = 0;
        for (int entry = start; entry < end; entry++)
        {
            long line = _matches._sparse![entry];
            long word = line >> 6;
            if (word != previousWord)
            {
                _visible!(word, visible);
                previousWord = word;
            }
            if ((visible[0] & (1UL << (int)(line & 63))) != 0) count++;
        }
        return count;
    }
}
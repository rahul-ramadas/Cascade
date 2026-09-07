using System.Numerics;

namespace Cascade.Core.Find;

/// <summary>One bit per line, with the two scans a search needs: the next set bit at or after a line, and
/// the last one at or before it. A search fills this from both ends at once, which is why it is a bitmap and
/// not a sorted list - a list would have to insert at the front for every match the backward sweep finds.</summary>
internal sealed class LineBitSet
{
    private readonly ulong[] _words;
    private readonly long _lines;

    public LineBitSet(long lines)
    {
        _lines = Math.Max(0, lines);
        _words = new ulong[checked((int)((_lines + 63) >> 6))];
    }

    public long Lines => _lines;

    public void Add(long line)
    {
        if (line < 0 || line >= _lines) return;
        _words[line >> 6] |= 1UL << (int)(line & 63);
    }

    public bool Contains(long line)
        => line >= 0 && line < _lines && (_words[line >> 6] & (1UL << (int)(line & 63))) != 0;

    public ulong Word(long index) => _words[index];

    /// <summary>Folds a block's worth of bits in, and answers how many lines that newly set. Merging a
    /// whole block a word at a time rather than a line at a time is what keeps a common term affordable:
    /// the work is then the size of the FILE, not the number of matches in it.</summary>
    public long Or(long firstWord, ReadOnlySpan<ulong> words)
    {
        long added = 0;
        for (int i = 0; i < words.Length; i++)
        {
            ulong bits = words[i];
            if (bits == 0) continue;
            long w = firstWord + i;
            if (w < 0 || w >= _words.LongLength) continue;
            ulong was = _words[w];
            ulong now = was | bits;
            if (now == was) continue;
            _words[w] = now;
            added += BitOperations.PopCount(now) - BitOperations.PopCount(was);
        }
        return added;
    }

    /// <summary>How many lines in <c>[from, toExclusive)</c> are set. Only the words in the range are
    /// touched, so summarising the file band by band costs one pass over the bitmap in total rather than one
    /// pass per band.</summary>
    public long CountInRange(long from, long toExclusive)
    {
        if (from < 0) from = 0;
        if (toExclusive > _lines) toExclusive = _lines;
        if (toExclusive <= from) return 0;

        long first = from >> 6, last = (toExclusive - 1) >> 6;
        ulong head = ulong.MaxValue << (int)(from & 63);
        int lastBit = (int)((toExclusive - 1) & 63);
        ulong tail = lastBit == 63 ? ulong.MaxValue : (1UL << (lastBit + 1)) - 1;
        if (first == last) return BitOperations.PopCount(_words[first] & head & tail);

        long n = BitOperations.PopCount(_words[first] & head);
        for (long w = first + 1; w < last; w++) n += BitOperations.PopCount(_words[w]);
        return n + BitOperations.PopCount(_words[last] & tail);
    }

    /// <summary>The first set line at or after <paramref name="from"/>, or -1.</summary>
    public long Next(long from)
    {
        if (_lines <= 0) return -1;
        if (from < 0) from = 0;
        if (from >= _lines) return -1;

        long w = from >> 6;
        ulong word = _words[w] & (ulong.MaxValue << (int)(from & 63));
        while (true)
        {
            if (word != 0)
            {
                long hit = (w << 6) + BitOperations.TrailingZeroCount(word);
                return hit < _lines ? hit : -1;
            }
            if (++w >= _words.LongLength) return -1;
            word = _words[w];
        }
    }

    /// <summary>The last set line at or before <paramref name="from"/>, or -1.</summary>
    public long Previous(long from)
    {
        if (_lines <= 0) return -1;
        if (from >= _lines) from = _lines - 1;
        if (from < 0) return -1;

        long w = from >> 6;
        int bit = (int)(from & 63);
        ulong mask = bit == 63 ? ulong.MaxValue : (1UL << (bit + 1)) - 1;
        ulong word = _words[w] & mask;
        while (true)
        {
            if (word != 0) return (w << 6) + (63 - BitOperations.LeadingZeroCount(word));
            if (--w < 0) return -1;
            word = _words[w];
        }
    }
}

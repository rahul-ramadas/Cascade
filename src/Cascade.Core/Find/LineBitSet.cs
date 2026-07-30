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

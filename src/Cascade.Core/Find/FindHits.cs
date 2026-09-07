using System.Numerics;

namespace Cascade.Core.Find;

/// <summary>
/// Where a scanner puts what it found in one block of lines.
///
/// <para>It takes bits, not a list of matches. A list costs the number of MATCHES, and merging it into the
/// search's bitmap costs that again on one thread: MEASURED on a 15.8 GB log, a term on 36 M of its 66 M
/// lines allocated 1.9 GB of match records per sweep and left the machine 5.8 cores busy of 32, because
/// every one of those matches went into the bitmap one at a time under the lock. A block of bits costs the
/// number of LINES divided by 64, whatever the term matches, and merges as whole words.</para>
///
/// <para>Threads write into it directly and it is reused between blocks, so a whole sweep allocates
/// nothing.</para>
/// </summary>
public sealed class FindHits
{
    private ulong[] _words = [];
    private readonly object _extras = new();

    /// <summary>The file word index <see cref="Words"/> starts at.</summary>
    internal long FirstWord { get; private set; }

    /// <summary>Bits for the block, one per line, from <see cref="FirstWord"/> * 64.</summary>
    internal ReadOnlySpan<ulong> Words => _words.AsSpan(0, WordCount);
    internal int WordCount { get; private set; }

    /// <summary>Occurrences across the whole block. Lines are counted from the bits; this is the extra
    /// question of how many times each of them matched.</summary>
    internal long Occurrences;

    /// <summary>True when some line's count was given up on part way, so the total is a floor.</summary>
    internal bool Capped;

    /// <summary>Lines that matched more than once, and by how many. Only these, because almost every
    /// matching line matches exactly once and the exceptions are what the split between shown and hidden
    /// hits is worked out from.</summary>
    internal readonly List<(long Line, int Extra)> ExtraLines = [];

    /// <summary>Whether the search still has room for lines that matched more than once. Once its record is
    /// full every further one is thrown away, so gathering them is pure cost: MEASURED on a term matching
    /// 36 M lines of a 15.8 GB log, collecting them past that point was 364 MB of allocation for nothing.
    /// </summary>
    public bool WantExtras { get; internal set; } = true;

    /// <summary>How many threads this block may be spread over. The two sweep directions run at once, so
    /// each of them is allowed half the machine while both have work and all of it once the other has run
    /// out of file. Letting both ask for everything is measurably worse - a 855 MB log swept from its middle
    /// took 28 ms that way against 14 ms sharing, on twice the CPU, because the pool ends up with twice as
    /// many threads as there are cores.</summary>
    public int Threads { get; internal set; } = Environment.ProcessorCount;

    internal void Reset(long from, long count, bool wantExtras, int threads)
    {
        FirstWord = from >> 6;
        long lastWord = count <= 0 ? FirstWord : (from + count - 1) >> 6;
        WordCount = checked((int)(lastWord - FirstWord + 1));
        if (_words.Length < WordCount) _words = new ulong[Math.Max(WordCount, _words.Length * 2)];
        else Array.Clear(_words, 0, WordCount);
        Occurrences = 0;
        Capped = false;
        WantExtras = wantExtras;
        Threads = Math.Max(1, threads);
        ExtraLines.Clear();
    }

    /// <summary>Records one matching line. For a scanner with nothing to gain from working in words -
    /// a test, or a range too small to have been handed out to threads.</summary>
    public void Add(long line, int occurrences = 1, bool capped = false)
    {
        int occ = Math.Max(1, occurrences);
        long word = (line >> 6) - FirstWord;
        if (word < 0 || word >= WordCount) return;
        _words[word] |= 1UL << (int)(line & 63);
        Occurrences += occ;
        if (capped) Capped = true;
        if (occ > 1 && WantExtras) ExtraLines.Add((line, occ - 1));
    }

    /// <summary>Records a whole word of lines at once, from a thread that owns none of the rest of the
    /// block. Words are OR-ed in atomically because a group of lines need not begin on a word boundary -
    /// a file of megabyte-long lines gets one line to a group, so sixty-four of them share a word.</summary>
    public void AddWord(long fileWord, ulong bits)
    {
        long word = fileWord - FirstWord;
        if (bits == 0 || word < 0 || word >= WordCount) return;
        Interlocked.Or(ref _words[word], bits);
    }

    /// <summary>What one thread found besides the bits: how many times its lines matched, and which of them
    /// matched more than once.</summary>
    public void AddCounts(long occurrences, bool capped, List<(long Line, int Extra)>? extraLines)
    {
        if (occurrences != 0) Interlocked.Add(ref Occurrences, occurrences);
        if (capped) Capped = true;
        if (extraLines is not { Count: > 0 }) return;
        lock (_extras) ExtraLines.AddRange(extraLines);
    }

    /// <summary>Lines set in the block. Only for callers that have no bitmap of their own to fold this
    /// into - the search itself counts them as it merges.</summary>
    internal long Count()
    {
        long n = 0;
        for (int i = 0; i < WordCount; i++) n += BitOperations.PopCount(_words[i]);
        return n;
    }
}

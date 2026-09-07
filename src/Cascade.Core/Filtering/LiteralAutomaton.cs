using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Cascade.Core.Filtering;

/// <summary>
/// An Aho-Corasick automaton that reports, in a <b>single pass</b> over a line, which of many literal patterns
/// occur in it. Filtering used to scan every line once per filter (175 scans per line); this scans once for all
/// of them, which is where almost all of the filtering time went.
/// <para>
/// Case-insensitive matching is exact rather than approximate: the character table maps every char whose
/// <see cref="char.ToUpperInvariant(char)"/> appears in a pattern, which is precisely the comparison
/// <see cref="StringComparison.OrdinalIgnoreCase"/> performs — so no folding is needed while matching.
/// </para>
/// </summary>
internal sealed class LiteralAutomaton
{
    private readonly byte[] _alpha;   // char -> symbol (0 = occurs in no pattern)
    private readonly int[]? _trans;   // (node << _shift) + symbol -> node, when there are too many nodes for 16 bits
    private readonly ushort[]? _trans16; // the same table narrowed; halves the traffic of the hot loop
    private readonly ulong[] _mask;   // node * _words -> bitset of patterns matched at this node
    private readonly bool[] _hasOut;  // most nodes end no pattern; skip the mask merge for those
    /// <summary>Log2 of the row stride. A row is padded to a power of two so that finding it is a shift
    /// rather than a multiply — and that shift sits on the loop's dependency chain, because the next node
    /// cannot be looked up until this one is known. MEASURED over 237 M characters of real trace on 32
    /// threads: 1.22x with 166 patterns, 1.30x with 48. The padding costs at most twice the table (307 KB
    /// -> 457 KB for 166 patterns), which is nothing beside a line index of hundreds of megabytes.</summary>
    private readonly int _shift;

    /// <summary>Every character that moves the walk off the root, or null when there are too many of them to
    /// be worth searching for. While the walk is at the root a character with no root transition leaves it
    /// there and can end no pattern, so a whole run of them can be skipped with one vectorized search
    /// instead of one table lookup each. MEASURED: 3.07x with 5 filters (2 such characters), 1.40x with 16
    /// (6 characters) — and a LOSS from 10 characters up, where the search stops being a few SIMD compares.
    /// Ordinary filter sets sit at the fast end: a handful of filters is what people usually have on.</summary>
    private readonly SearchValues<char>? _rootMovers;

    /// <summary>How many distinct root-entering characters are still worth a vectorized search. Above this
    /// <see cref="SearchValues{T}"/> falls back to a bitmap probe and the skip costs more than it saves;
    /// measured crossover is between 6 and 10, so the boundary is put where both sides were measured.</summary>
    private const int RootSkipLimit = 8;

    /// <summary>Number of 64-bit words a hit bitset needs.</summary>
    public int Words { get; }

    /// <summary>Whether the vectorized root skip is in force. Both walks answer the same, so no test would
    /// fail if the skip silently stopped being taken - it would just quietly cost the common filter sets
    /// their speed. This is what lets a test say which one it exercised.</summary>
    internal bool SkipsAtRootForTesting => _rootMovers is not null;

    /// <summary>Entries in the transition table, padding included. Lets a test hold the padding to the
    /// size it is meant to be rather than trusting the arithmetic.</summary>
    internal int TableEntriesForTesting => _trans16?.Length ?? _trans!.Length;

    /// <summary>Canonical form of each character under <see cref="StringComparison.OrdinalIgnoreCase"/>, built
    /// by asking .NET itself rather than assuming <see cref="char.ToUpperInvariant(char)"/>: ordinal casing
    /// deliberately does <b>not</b> fold some characters that invariant uppercasing does (the Turkish dotless
    /// ı onto I, for one), and the automaton must reproduce the comparison it replaces exactly.</summary>
    private static readonly char[] OrdinalUpper = BuildOrdinalUpperTable();

    private static char[] BuildOrdinalUpperTable()
    {
        var map = new char[char.MaxValue + 1];
        Span<char> from = stackalloc char[1];
        Span<char> to = stackalloc char[1];
        for (int i = 0; i <= char.MaxValue; i++)
        {
            char c = (char)i, upper = char.ToUpperInvariant(c);
            from[0] = c;
            to[0] = upper;
            map[i] = upper != c && from.Equals(to, StringComparison.OrdinalIgnoreCase) ? upper : c;
        }
        return map;
    }

    private LiteralAutomaton(byte[] alpha, int[] trans, ulong[] mask, bool[] hasOut, int shift, int words,
                             int nodes, SearchValues<char>? rootMovers)
    {
        _alpha = alpha;
        _mask = mask;
        _hasOut = hasOut;
        _shift = shift;
        Words = words;
        _rootMovers = rootMovers;
        // The transition table is walked randomly for every character, so its size drives cache behaviour.
        // Only one width is kept: holding both cost half a megabyte of the larger one for nothing.
        if (nodes <= ushort.MaxValue)
        {
            var narrow = new ushort[trans.Length];
            for (int i = 0; i < trans.Length; i++) narrow[i] = (ushort)trans[i];
            _trans16 = narrow;
        }
        else _trans = trans;
    }

    /// <summary>Builds an automaton for <paramref name="patterns"/>, or returns null when they use more than
    /// 254 distinct characters (rare; the caller then falls back to scanning per pattern).</summary>
    public static LiteralAutomaton? TryBuild(IReadOnlyList<string> patterns, bool ignoreCase)
    {
        if (patterns.Count == 0) return null;

        // Assign a compact symbol to each distinct pattern character.
        var symbolOf = new Dictionary<char, byte>();
        foreach (string p in patterns)
            foreach (char c in p)
            {
                char key = ignoreCase ? OrdinalUpper[c] : c;
                if (!symbolOf.ContainsKey(key))
                {
                    if (symbolOf.Count >= 254) return null;
                    symbolOf[key] = (byte)(symbolOf.Count + 1);
                }
            }
        int alphabetSize = symbolOf.Count + 1;

        // Character table. For ignore-case this maps every character that shares a pattern character's
        // canonical form, so matching needs no per-character folding and still equals OrdinalIgnoreCase.
        var alpha = new byte[char.MaxValue + 1];
        for (int c = 0; c <= char.MaxValue; c++)
        {
            char key = ignoreCase ? OrdinalUpper[c] : (char)c;
            if (symbolOf.TryGetValue(key, out byte sym)) alpha[c] = sym;
        }

        // Trie.
        int words = (patterns.Count + 63) / 64;
        var children = new List<int[]> { new int[alphabetSize] };
        var ends = new List<ulong[]> { new ulong[words] };
        for (int p = 0; p < patterns.Count; p++)
        {
            int node = 0;
            foreach (char c in patterns[p])
            {
                int sym = alpha[c];
                if (children[node][sym] == 0)
                {
                    children.Add(new int[alphabetSize]);
                    ends.Add(new ulong[words]);
                    children[node][sym] = children.Count - 1;
                }
                node = children[node][sym];
            }
            ends[node][p >> 6] |= 1UL << (p & 63);
        }

        // Breadth-first pass: turn the trie into a full automaton (goto for every symbol) and fold each
        // node's output with its failure link's, so a match needs no failure-chain walk.
        //
        // Rows are padded to a power of two. Nothing is stored in the padding; it exists so that the row of
        // a node is (node << shift), which the hot loop can do with a shift on its dependency chain instead
        // of a multiply. Column 0 is never written, which is what lets the loop read it unconditionally: a
        // character in no pattern lands there and reads back the root, exactly as failing all the way would.
        int n = children.Count;
        int shift = BitOperations.Log2((uint)Math.Max(1, alphabetSize - 1)) + 1;
        int stride = 1 << shift;
        var trans = new int[n * stride];
        var mask = new ulong[n * words];
        var hasOut = new bool[n];
        var fail = new int[n];
        var queue = new Queue<int>();

        for (int sym = 1; sym < alphabetSize; sym++)
        {
            int t = children[0][sym];
            trans[sym] = t;
            if (t != 0) queue.Enqueue(t);
        }
        while (queue.Count > 0)
        {
            int node = queue.Dequeue();
            for (int w = 0; w < words; w++)
            {
                ulong m = ends[node][w] | mask[fail[node] * words + w];
                mask[node * words + w] = m;
                if (m != 0) hasOut[node] = true;
            }
            for (int sym = 1; sym < alphabetSize; sym++)
            {
                int t = children[node][sym];
                if (t != 0)
                {
                    fail[t] = trans[(fail[node] << shift) + sym];
                    trans[(node << shift) + sym] = t;
                    queue.Enqueue(t);
                }
                else trans[(node << shift) + sym] = trans[(fail[node] << shift) + sym];
            }
        }

        // Characters the root has a transition on. Any other character leaves the walk where it is when that
        // is the root, so runs of them are skipped rather than stepped through - but only while there are
        // few enough of them for the search to stay a couple of SIMD compares.
        SearchValues<char>? rootMovers = null;
        var movers = new List<char>();
        for (int c = 0; c <= char.MaxValue; c++)
        {
            int sym = alpha[c];
            if (sym != 0 && trans[sym] != 0)
            {
                movers.Add((char)c);
                if (movers.Count > RootSkipLimit) break;
            }
        }
        if (movers.Count <= RootSkipLimit) rootMovers = SearchValues.Create(CollectionsMarshal.AsSpan(movers));

        return new LiteralAutomaton(alpha, trans, mask, hasOut, shift, words, n, rootMovers);
    }

    /// <summary>ORs into <paramref name="hits"/> the bit of every pattern occurring in <paramref name="line"/>.</summary>
    public void Match(ReadOnlySpan<char> line, Span<ulong> hits)
    {
        if (_trans16 is ushort[] trans16) Walk(trans16, line, hits);
        else Walk(_trans!, line, hits);
    }

    /// <summary>The walk, written once over whichever width the table is. This is the hottest loop in the
    /// program - it runs once per character of the file, so on a 15 GB log that is fifteen thousand million
    /// times - and it is bound by the latency of its own dependency chain: the next node cannot be looked
    /// up until this one is known. Everything here is about keeping that chain short.</summary>
    private void Walk<T>(T[] trans, ReadOnlySpan<char> line, Span<ulong> hits) where T : IBinaryInteger<T>
    {
        byte[] alpha = _alpha;
        ulong[] mask = _mask;
        bool[] hasOut = _hasOut;
        int sh = _shift, w = Words;
        int node = 0, i = 0, len = line.Length;

        if (_rootMovers is SearchValues<char> movers)
        {
            while (i < len)
            {
                // At the root, and only there, a character the root has no transition on cannot start
                // anything: it leaves the walk at the root and ends no pattern. So look for the next
                // character that can, rather than reading the table for each one that cannot.
                if (node == 0)
                {
                    int skip = line[i..].IndexOfAny(movers);
                    if (skip < 0) return;
                    i += skip;
                }
                node = int.CreateTruncating(trans[(node << sh) + alpha[line[i]]]);
                if (hasOut[node])
                    for (int k = 0; k < w; k++) hits[k] |= mask[node * w + k];
                i++;
            }
            return;
        }

        for (; i < len; i++)
        {
            node = int.CreateTruncating(trans[(node << sh) + alpha[line[i]]]);
            if (hasOut[node])
                for (int k = 0; k < w; k++) hits[k] |= mask[node * w + k];
        }
    }
}

/// <summary>
/// Rewrites the very common "literal <c>.+</c> literal" log-filter regex into a sequence of plain substring
/// searches. Those run vectorized, need no <see cref="Regex"/> object at all (so they never contend on its
/// internal runner cache), and are exactly equivalent for a match test.
/// </summary>
internal static class RegexLiteralRewriter
{
    /// <summary>True if <paramref name="pattern"/> is literals separated only by <c>.+</c> / <c>.+?</c>, in
    /// which case <paramref name="parts"/> receives those literals in order.</summary>
    public static bool TryRewrite(string pattern, out string[] parts)
    {
        parts = Array.Empty<string>();
        if (pattern.Length == 0) return false;

        var list = new List<string>();
        var current = new System.Text.StringBuilder();
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\')
            {
                // Only punctuation escapes are literal; \d, \w, \s, \b... are classes or anchors.
                if (i + 1 >= pattern.Length) return false;
                char escaped = pattern[++i];
                if (char.IsLetterOrDigit(escaped)) return false;
                current.Append(escaped);
            }
            else if (c == '.' && i + 1 < pattern.Length && pattern[i + 1] == '+')
            {
                i++;
                if (i + 1 < pattern.Length && pattern[i + 1] == '?') i++; // lazy: same set of lines matches
                list.Add(current.ToString());
                current.Clear();
            }
            else if (c is '[' or ']' or '(' or ')' or '|' or '^' or '$' or '*' or '+' or '?' or '{' or '}' or '.')
                return false; // any other metacharacter: keep the real regex engine
            else current.Append(c);
        }
        list.Add(current.ToString());

        // Needs at least one separator, and every literal must be non-empty (an empty one would match anywhere).
        if (list.Count < 2) return false;
        foreach (string s in list) if (s.Length == 0) return false;

        parts = list.ToArray();
        return true;
    }

    /// <summary>True if each literal occurs in order with at least one character between them — exactly what
    /// <c>L0.+L1.+L2</c> tests. Searching earliest-first is optimal: an earlier hit can only leave more room
    /// for the rest.</summary>
    public static bool Matches(ReadOnlySpan<char> line, string[] parts, StringComparison comparison)
    {
        int at = 0;
        for (int p = 0; p < parts.Length; p++)
        {
            if (at > line.Length) return false;
            int found = line[at..].IndexOf(parts[p], comparison);
            if (found < 0) return false;
            at += found + parts[p].Length;
            if (p + 1 < parts.Length) at++; // ".+" requires at least one character
        }
        return true;
    }
}

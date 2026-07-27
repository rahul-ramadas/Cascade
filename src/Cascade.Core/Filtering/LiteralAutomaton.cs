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
    private readonly int[] _trans;    // node * _alphabetSize + symbol -> node
    private readonly ushort[]? _trans16; // same table narrowed to 16 bits; halves the traffic of the hot loop
    private readonly ulong[] _mask;   // node * _words -> bitset of patterns matched at this node
    private readonly bool[] _hasOut;  // most nodes end no pattern; skip the mask merge for those
    private readonly int _alphabetSize;

    /// <summary>Number of 64-bit words a hit bitset needs.</summary>
    public int Words { get; }

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

    private LiteralAutomaton(byte[] alpha, int[] trans, ulong[] mask, bool[] hasOut, int alphabetSize, int words, int nodes)
    {
        _alpha = alpha;
        _trans = trans;
        _mask = mask;
        _hasOut = hasOut;
        _alphabetSize = alphabetSize;
        Words = words;
        // The transition table is walked randomly for every character, so its size drives cache behaviour.
        if (nodes <= ushort.MaxValue)
        {
            _trans16 = new ushort[trans.Length];
            for (int i = 0; i < trans.Length; i++) _trans16[i] = (ushort)trans[i];
        }
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
        int n = children.Count;
        var trans = new int[n * alphabetSize];
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
                    fail[t] = trans[fail[node] * alphabetSize + sym];
                    trans[node * alphabetSize + sym] = t;
                    queue.Enqueue(t);
                }
                else trans[node * alphabetSize + sym] = trans[fail[node] * alphabetSize + sym];
            }
        }
        return new LiteralAutomaton(alpha, trans, mask, hasOut, alphabetSize, words, n);
    }

    /// <summary>ORs into <paramref name="hits"/> the bit of every pattern occurring in <paramref name="line"/>.</summary>
    public void Match(ReadOnlySpan<char> line, Span<ulong> hits)
    {
        byte[] alpha = _alpha;
        ulong[] mask = _mask;
        bool[] hasOut = _hasOut;
        int a = _alphabetSize, w = Words;
        int node = 0;

        if (_trans16 is ushort[] trans16)
        {
            for (int i = 0; i < line.Length; i++)
            {
                int sym = alpha[line[i]];
                node = sym == 0 ? 0 : trans16[node * a + sym];
                if (hasOut[node])
                    for (int k = 0; k < w; k++) hits[k] |= mask[node * w + k];
            }
            return;
        }

        int[] trans = _trans;
        for (int i = 0; i < line.Length; i++)
        {
            int sym = alpha[line[i]];
            node = sym == 0 ? 0 : trans[node * a + sym];
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

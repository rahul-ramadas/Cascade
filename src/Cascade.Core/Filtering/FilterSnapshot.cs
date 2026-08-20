using System.Text.RegularExpressions;
using Cascade.Core.Markers;
using Cascade.Core.Model;

namespace Cascade.Core.Filtering;

/// <summary>Result of evaluating one line: whether it is shown and, if so, the filter whose style
/// colors it (the first enabled include in list order that matched, refined by its own descendants).</summary>
public readonly record struct LineEval(bool Shown, Filter? ColorFilter);

/// <summary>
/// An immutable, compiled snapshot of the filter tree used to evaluate lines on background threads
/// without racing UI edits. Regexes are compiled once. Implements the exact hierarchical semantics:
/// a line is shown iff some enabled include filter <i>deep-matches</i> (it and all ancestors' predicates
/// match) and no enabled exclude deep-matches.
/// <para>The color comes from the <b>first</b> enabled include that deep-matches, reading the list top to
/// bottom as it is drawn - even when that filter sets no style, in which case the line takes the view's
/// defaults. Only a filter <b>nested under</b> that one may take it from there, and among those, again the
/// first. Depth is not a criterion: a deeper filter in a later branch loses to an earlier, shallower one.
/// </para>
/// </summary>
public sealed class FilterSnapshot
{
    private sealed class Node
    {
        public FilterMatchType Type;
        public string Text = "";
        public bool CaseSensitive;
        public Regex? Regex;
        public bool IsRegex;
        public int MarkerIndex;
        public bool Enabled;
        public FilterKind Kind;
        public int Index;
        /// <summary>One past the last index in this node's subtree. Indices are handed out in the order the
        /// tree is drawn, so a subtree is a contiguous range and "is <c>x</c> below me?" is
        /// <c>x.Index &lt; SubtreeEnd</c> - no walk up the parents, no depth arithmetic.</summary>
        public int SubtreeEnd;
        public bool SubtreeHasEnabled;
        public Node[] Children = Array.Empty<Node>();
        public Filter Source = null!;

        /// <summary>Bit of this node's literal in the shared automaton, or -1 when it is matched another way.</summary>
        public int LiteralBit = -1;
        /// <summary>Literals of a rewritten "L0.+L1" regex, matched with plain substring searches.</summary>
        public string[]? Sequence;
        /// <summary>Identifies the chain of predicates (root..this) that decides this filter's deep match, so a
        /// cached result can be reused only while that whole chain is unchanged.</summary>
        public string CacheKey = "";
        /// <summary>False when the deep match depends on markers, which change independently of the filters.</summary>
        public bool Cacheable;
        /// <summary>Automaton bits for each literal in <see cref="Sequence"/>: all must occur for the
        /// sequence to have any chance, so this rejects almost every line without scanning it again.</summary>
        public int[]? SequenceBits;
        public StringComparison Comparison;
    }

    /// <summary>Per-thread scratch for <see cref="Evaluate"/>: the automaton's hit bitset for one line.
    /// <see cref="Regex"/> deliberately is <b>not</b> shared across threads — it caches a single internal
    /// runner, so concurrent callers allocate a fresh one on every call (measured 5.8x slower).</summary>
    public sealed class MatchContext
    {
        internal readonly FilterSnapshot Owner;
        internal readonly ulong[] Hits;
        internal readonly Regex?[] Regexes;

        internal MatchContext(FilterSnapshot owner, int words, int nodes)
        {
            Owner = owner;
            Hits = new ulong[words];
            Regexes = new Regex?[nodes];
        }

        /// <summary>Width of the automaton hit bitset, i.e. how many patterns the automatons hold.</summary>
        public int HitWords => Hits.Length;
    }

    private readonly Node[] _roots;
    /// <summary>Each root's automaton bit (or -1). Testing these first keeps the per-line scan inside two tiny
    /// arrays instead of dereferencing every root object — with ~175 filters almost all of them do not match,
    /// and chasing their pointers cost more than the matching itself.</summary>
    private readonly int[] _rootBits;
    private readonly Dictionary<Filter, int> _index;
    private readonly Node[] _nodesByIndex;
    private readonly LiteralAutomaton? _ciAutomaton;
    private readonly LiteralAutomaton? _csAutomaton;
    private readonly int _ciWords;
    private readonly int _hitWords;

    [ThreadStatic] private static MatchContext?[]? _threadContexts;

    /// <summary>How many snapshots one thread can be asked about without rebuilding scratch: the filters in
    /// force, plus the ones the view may still be showing rows from.</summary>
    private const int ContextSlots = 3;

    public bool ShowOnlyFilteredLines { get; }
    public bool HasAnyEnabled { get; }
    public bool HasEnabledInclude { get; }
    /// <summary>True if a marker-type filter participates in filtering (it is enabled, or it is an ancestor
    /// of an enabled filter). When false, toggling a line marker cannot change any filter result, so the
    /// view need not be re-filtered.</summary>
    public bool HasMarkerFilter { get; }
    public int FilterCount { get; }

    private FilterSnapshot(Node[] roots, Dictionary<Filter, int> index, Node[] nodesByIndex, int filterCount,
        bool showOnlyFiltered, bool hasAnyEnabled, bool hasEnabledInclude, bool hasMarkerFilter,
        LiteralAutomaton? ciAutomaton, LiteralAutomaton? csAutomaton)
    {
        _roots = roots;
        _rootBits = new int[roots.Length];
        for (int i = 0; i < roots.Length; i++) _rootBits[i] = roots[i].LiteralBit;
        _index = index;
        _nodesByIndex = nodesByIndex;
        FilterCount = filterCount;
        ShowOnlyFilteredLines = showOnlyFiltered;
        HasAnyEnabled = hasAnyEnabled;
        HasEnabledInclude = hasEnabledInclude;
        HasMarkerFilter = hasMarkerFilter;
        _ciAutomaton = ciAutomaton;
        _csAutomaton = csAutomaton;
        _ciWords = ciAutomaton?.Words ?? 0;
        _hitWords = _ciWords + (csAutomaton?.Words ?? 0);
    }

    /// <summary>Scratch buffers for evaluating lines on one thread. Reused across calls on that thread.</summary>
    public MatchContext CreateContext() => new(this, Math.Max(1, _hitWords), _nodesByIndex.Length);

    /// <summary>This thread's cached scratch for this snapshot. Filter workers take one per thread rather than
    /// per work item, so the buffers (and any per-thread <see cref="Regex"/>) are built once, not per block.</summary>
    public MatchContext GetThreadContext() => ThreadContext;

    private MatchContext ThreadContext
    {
        get
        {
            // One slot per snapshot that can be in play at once. A view catching up with a filter change
            // asks the old snapshot and the new one about alternate lines, and a change made before the
            // previous pass could finish leaves a third in the mixture. With too few slots each question
            // throws another's scratch away, and building it again compiles every regex again - MEASURED at
            // 32.4us an evaluation against 1.7us. Most recently used first, so the ordinary case is one
            // comparison and costs what the two fields this replaced did.
            var slots = _threadContexts ??= new MatchContext?[ContextSlots];
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] is not { } held || !ReferenceEquals(held.Owner, this)) continue;
                if (i > 0) { slots[i] = slots[0]; slots[0] = held; }
                return held;
            }
            var made = CreateContext();
            Array.Copy(slots, 0, slots, 1, slots.Length - 1);
            slots[0] = made;
            return made;
        }
    }

    /// <summary>A filter taking part in evaluation, described for the match cache.</summary>
    public readonly record struct CacheableFilter(int Index, string Key, bool Enabled, bool IsExclude);

    /// <summary>Words needed for a deep-match bitset (one bit per filter).</summary>
    public int DeepMatchWords => (FilterCount + 63) / 64;

    /// <summary>The filters that take part in evaluation, each with the key identifying the predicate chain
    /// behind its deep match. Returns false when any of them cannot be cached — marker filters depend on
    /// markers, which change independently of the filter set, so those results must never be reused.</summary>
    public bool TryGetCacheableFilters(out List<CacheableFilter> filters)
    {
        filters = new List<CacheableFilter>();
        foreach (var node in _nodesByIndex)
        {
            if (!node.SubtreeHasEnabled) continue;   // pruned: never evaluated, nothing to cache
            if (!node.Cacheable) { filters.Clear(); return false; }
            filters.Add(new CacheableFilter(node.Index, node.CacheKey, node.Enabled, node.Kind == FilterKind.Exclude));
        }
        return true;
    }

    /// <summary>Every cache key this filter set could ask for, disabled filters included - their results
    /// are exactly what makes re-enabling them instant. Anything the cache holds beyond these belongs to a
    /// filter that has been deleted or edited and is dead weight.</summary>
    public HashSet<string> CacheKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in _nodesByIndex)
            if (node.Cacheable) keys.Add(node.CacheKey);
        return keys;
    }

    /// <summary>Maps a source filter to its count index (aligned with the counts array).</summary>
    public bool TryGetIndex(Filter filter, out int index) => _index.TryGetValue(filter, out index);

    /// <summary>The filter at a count index - the reverse of <see cref="TryGetIndex"/>.</summary>
    public Filter FilterAt(int index) => _nodesByIndex[index].Source;

    /// <summary>How many filters this snapshot holds, i.e. the width of the index space.</summary>
    public int NodeCount => _nodesByIndex.Length;

    /// <summary>Records a bit (indexed by <see cref="TryGetIndex"/>) for every filter that deep-matches the
    /// line, <i>including</i> ones that are switched off. Evaluation proper prunes subtrees with nothing
    /// enabled, since they cannot change what is shown; this is for telling the user why a line looks the
    /// way it does, where a switched-off filter that would have matched is worth knowing about.</summary>
    public void MatchingFilters(ReadOnlySpan<char> line, long lineNumber, MarkerStore? markers, Span<ulong> deepMatches)
    {
        var context = ThreadContext;
        var hits = context.Hits.AsSpan();
        if (_hitWords > 0)
        {
            hits.Clear();
            _ciAutomaton?.Match(line, hits[.._ciWords]);
            _csAutomaton?.Match(line, hits[_ciWords..]);
        }
        foreach (var root in _roots) DfsAll(root, line, lineNumber, markers, context, deepMatches);
    }

    private static void DfsAll(Node node, ReadOnlySpan<char> line, long lineNumber, MarkerStore? markers,
        MatchContext context, Span<ulong> deepMatches)
    {
        if (!Matches(node, line, lineNumber, markers, context)) return;   // descendants require this match
        deepMatches[node.Index >> 6] |= 1UL << (node.Index & 63);
        foreach (var child in node.Children) DfsAll(child, line, lineNumber, markers, context, deepMatches);
    }

    /// <summary>The key identifying a filter's deep-match results in the match cache. False when the filter
    /// is not in this snapshot, or its chain involves a marker (whose results must never be reused).</summary>
    public bool TryGetCacheKey(Filter filter, out string key)
    {
        key = "";
        if (!_index.TryGetValue(filter, out int index)) return false;
        var node = _nodesByIndex[index];
        if (!node.Cacheable) return false;
        key = node.CacheKey;
        return true;
    }

    /// <summary>True if <paramref name="target"/> <i>deep-matches</i> the line: its own predicate and
    /// every ancestor's predicate match (independent of enabled state). Used by per-filter find.</summary>
    public bool DeepMatches(ReadOnlySpan<char> line, long lineNumber, MarkerStore? markers, Filter target)
    {
        var context = ThreadContext;
        var hits = context.Hits.AsSpan();
        if (_hitWords > 0)
        {
            hits.Clear();
            _ciAutomaton?.Match(line, hits[.._ciWords]);
            _csAutomaton?.Match(line, hits[_ciWords..]);
        }
        for (Filter? f = target; f is not null; f = f.Parent)
        {
            if (!_index.TryGetValue(f, out int idx)) return false; // not part of this snapshot
            if (!Matches(_nodesByIndex[idx], line, lineNumber, markers, context)) return false;
        }
        return true;
    }

    public static FilterSnapshot Build(FilterCollection filters) => Build(filters, null);

    /// <summary>Builds a snapshot in which <paramref name="forceEnabled"/> takes part in evaluation even when
    /// the user has it switched off. Used by "find this filter's next match", which has to compute exactly
    /// what enabling the filter would compute without changing what the view shows.</summary>
    public static FilterSnapshot Build(FilterCollection filters, Filter? forceEnabled)
        => Build(filters, forceEnabled, null);

    /// <summary>Builds a snapshot holding <paramref name="target"/> and its ancestors and nothing else, all
    /// taking part in evaluation. A filter's deep match - and the key it is cached under - depends on nothing
    /// but that chain, so the result is interchangeable with one worked out from the whole filter set while
    /// costing a handful of predicates per line instead of every filter in the list.</summary>
    public static FilterSnapshot BuildForChain(FilterCollection filters, Filter target)
    {
        var chain = new HashSet<Filter>();
        for (Filter? f = target; f is not null; f = f.Parent) chain.Add(f);
        return Build(filters, target, chain);
    }

    private static FilterSnapshot Build(FilterCollection filters, Filter? forceEnabled, HashSet<Filter>? chain)
    {
        bool anyEnabled = false, anyInclude = false, anyMarker = false;
        int counter = 0;
        var index = new Dictionary<Filter, int>();
        var nodes = new List<Node>();

        Node Convert(Filter f, string parentKey, bool parentCacheable)
        {
            bool enabled = f.Enabled || ReferenceEquals(f, forceEnabled);
            var node = new Node
            {
                Type = f.Match.Type,
                Text = f.Match.Text,
                CaseSensitive = f.Match.CaseSensitive,
                MarkerIndex = f.Match.MarkerIndex,
                Enabled = enabled,
                Kind = f.Kind,
                Index = counter++,
                Source = f
            };
            index[f] = node.Index;
            nodes.Add(node);

            // A filter's deep match is decided by its own predicate and every ancestor's, so the cache key is
            // the whole chain: editing a parent must invalidate its children too.
            string own = f.Match.Type == FilterMatchType.Marker
                ? $"M{f.Match.MarkerIndex}"
                : $"T{(f.Match.Regex ? 'r' : 'l')}{(f.Match.CaseSensitive ? 'S' : 'i')}:{f.Match.Text}";
            node.CacheKey = parentKey.Length == 0 ? own : parentKey + "\u0001" + own;
            node.Cacheable = parentCacheable && f.Match.Type != FilterMatchType.Marker;

            if (f.Match.Type == FilterMatchType.Text && f.Match.Regex && f.Match.Text.Length > 0)
            {
                // "L0.+L1" style patterns - by far the most common in log filters - become plain substring
                // searches: vectorized, and with no Regex object to contend on.
                if (RegexLiteralRewriter.TryRewrite(f.Match.Text, out string[] parts))
                    node.Sequence = parts;
                else
                {
                    var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
                    if (!f.Match.CaseSensitive) options |= RegexOptions.IgnoreCase;
                    try { node.Regex = new Regex(f.Match.Text, options); }
                    catch (ArgumentException) { node.Regex = null; } // invalid regex → never matches
                }
            }
            node.IsRegex = f.Match.Type == FilterMatchType.Text && f.Match.Regex;
            node.Comparison = f.Match.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

            if (enabled)
            {
                anyEnabled = true;
                if (f.Kind == FilterKind.Include) anyInclude = true;
            }

            var kept = new List<Node>(f.Children.Count);
            foreach (var child in f.Children)
                if (chain is null || chain.Contains(child)) kept.Add(Convert(child, node.CacheKey, node.Cacheable));
            node.Children = kept.ToArray();

            // Every descendant has now taken its index, so the counter is one past the last of them.
            node.SubtreeEnd = counter;

            node.SubtreeHasEnabled = enabled;
            foreach (var c in node.Children) node.SubtreeHasEnabled |= c.SubtreeHasEnabled;
            if (node.Type == FilterMatchType.Marker && node.SubtreeHasEnabled) anyMarker = true;
            return node;
        }

        var rootFilters = new List<Filter>(filters.Roots.Count);
        foreach (var root in filters.Roots)
            if (chain is null || chain.Contains(root)) rootFilters.Add(root);
        var roots = new Node[rootFilters.Count];
        for (int i = 0; i < rootFilters.Count; i++) roots[i] = Convert(rootFilters[i], "", true);

        // Collect the plain literals into one automaton per case mode, so a line is scanned once for all of
        // them instead of once per filter. Case-sensitive and -insensitive patterns cannot share a character
        // table, so they get separate automatons (their hit bits live in one bitset, offset by _ciWords).
        // Literals of rewritten regexes go in too, purely as a prefilter.
        //
        // Only filters that actually take part in evaluation are included. Dfs prunes any node whose subtree
        // holds nothing enabled, so those patterns would only enlarge the automaton (a bigger transition table
        // is the main cost of matching) and widen the hit bitset. Note the test is SubtreeHasEnabled, not
        // Enabled: a disabled ancestor still constrains its enabled descendants. Anything left out simply
        // falls back to a direct substring search, which is what per-filter find uses it for anyway.
        var ciTexts = new List<string>();
        var csTexts = new List<string>();
        foreach (var node in nodes)
        {
            if (node.Type != FilterMatchType.Text || !node.SubtreeHasEnabled) continue;
            var bucket = node.CaseSensitive ? csTexts : ciTexts;
            if (node.Sequence is not null) bucket.AddRange(node.Sequence);
            else if (!node.IsRegex && node.Text.Length > 0) bucket.Add(node.Text);
        }
        var ci = LiteralAutomaton.TryBuild(ciTexts, ignoreCase: true);
        var cs = LiteralAutomaton.TryBuild(csTexts, ignoreCase: false);

        int ciBit = 0, csBit = 0;
        int csBitBase = (ci?.Words ?? 0) * 64;
        foreach (var node in nodes)
        {
            if (node.Type != FilterMatchType.Text || !node.SubtreeHasEnabled) continue;
            bool available = node.CaseSensitive ? cs is not null : ci is not null;
            if (node.Sequence is not null)
            {
                var bits = new int[node.Sequence.Length];
                for (int i = 0; i < bits.Length; i++)
                    bits[i] = node.CaseSensitive ? csBitBase + csBit++ : ciBit++;
                if (available) node.SequenceBits = bits;
            }
            else if (!node.IsRegex && node.Text.Length > 0)
            {
                int bit = node.CaseSensitive ? csBitBase + csBit++ : ciBit++;
                if (available) node.LiteralBit = bit;
            }
        }

        return new FilterSnapshot(roots, index, nodes.ToArray(), counter, filters.ShowOnlyFilteredLines,
            anyEnabled, anyInclude, anyMarker, ci, cs);
    }

    /// <summary>Evaluates a single line. <paramref name="markers"/> may be null when no marker
    /// filters exist.</summary>
    public LineEval Evaluate(ReadOnlySpan<char> line, long lineNumber, MarkerStore? markers)
        => Evaluate(line, lineNumber, markers, null);

    /// <summary>Evaluates a line and, when <paramref name="counts"/> is provided (size
    /// <see cref="FilterCount"/>), increments the entry of every enabled filter that deep-matches.</summary>
    public LineEval Evaluate(ReadOnlySpan<char> line, long lineNumber, MarkerStore? markers, long[]? counts)
        => Evaluate(line, lineNumber, markers, counts, ThreadContext);

    /// <summary>As above, using caller-supplied per-thread scratch (see <see cref="CreateContext"/>). Filter
    /// workers pass their own context so nothing is shared between threads.</summary>
    public LineEval Evaluate(ReadOnlySpan<char> line, long lineNumber, MarkerStore? markers, long[]? counts,
        MatchContext context)
        => Evaluate(line, lineNumber, markers, counts, context, default);

    /// <summary>As above, additionally recording one bit per filter in <paramref name="deepMatches"/> (indexed
    /// by <see cref="TryGetIndex"/>) for every filter that <i>deep-matches</i> this line. Deep match does not
    /// depend on which filters are enabled, so those bits stay valid across enable/disable and are what the
    /// match cache stores.</summary>
    public LineEval Evaluate(ReadOnlySpan<char> line, long lineNumber, MarkerStore? markers, long[]? counts,
        MatchContext context, Span<ulong> deepMatches)
    {
        // One pass over the line finds every literal filter that occurs in it; the tree walk below then just
        // tests bits instead of re-scanning the line once per filter.
        var hits = context.Hits.AsSpan();
        if (_hitWords > 0)
        {
            hits.Clear();
            _ciAutomaton?.Match(line, hits[.._ciWords]);
            _csAutomaton?.Match(line, hits[_ciWords..]);
        }

        // The winner is the first enabled include that deep-matches, and after that only something nested
        // under it can take over. bestEnd is one past the last index in the winner's subtree, so "is this
        // below the winner?" is one comparison - indices are handed out in the order the tree is drawn and
        // visited in that order, so an index below bestEnd is inside the winner's nest and one at or above
        // it is a later branch that has already lost. Starting at int.MaxValue makes "nobody has claimed it
        // yet" the same comparison rather than a null check of its own.
        int bestEnd = int.MaxValue;
        Filter? best = null;
        bool excluded = false;
        bool anyIncludeMatched = false;

        for (int i = 0; i < _roots.Length; i++)
        {
            // A literal root that the automaton did not hit cannot match, and neither can its subtree.
            int bit = _rootBits[i];
            if (bit >= 0 && (hits[bit >> 6] & (1UL << (bit & 63))) == 0) continue;
            Dfs(_roots[i], line, lineNumber, markers, counts, context, deepMatches, ref bestEnd, ref best, ref excluded, ref anyIncludeMatched);
        }
        bool included = HasEnabledInclude ? anyIncludeMatched : true;
        bool shown = included && !excluded;
        return new LineEval(shown, shown ? best : null);
    }

    private static void Dfs(Node node, ReadOnlySpan<char> line, long lineNumber, MarkerStore? markers, long[]? counts,
        MatchContext context, Span<ulong> deepMatches, ref int bestEnd, ref Filter? best, ref bool excluded, ref bool anyIncludeMatched)
    {
        if (!node.SubtreeHasEnabled) return;          // prune: nothing enabled at/below
        if (!Matches(node, line, lineNumber, markers, context)) return; // prune: descendants require this match

        // Reaching here means every ancestor matched too, so this is a deep match.
        if (!deepMatches.IsEmpty) deepMatches[node.Index >> 6] |= 1UL << (node.Index & 63);

        if (node.Enabled)
        {
            if (counts is not null) counts[node.Index]++;
            if (node.Kind == FilterKind.Include)
            {
                anyIncludeMatched = true;
                // Nothing has claimed the line yet, or this is nested under whatever has.
                if (node.Index < bestEnd) { best = node.Source; bestEnd = node.SubtreeEnd; }
            }
            else
            {
                excluded = true;
            }
        }

        foreach (var child in node.Children)
            Dfs(child, line, lineNumber, markers, counts, context, deepMatches, ref bestEnd, ref best, ref excluded, ref anyIncludeMatched);
    }

    private static bool Matches(Node node, ReadOnlySpan<char> line, long lineNumber, MarkerStore? markers,
        MatchContext context)
    {
        if (node.Type == FilterMatchType.Marker)
            return markers is not null && node.MarkerIndex >= 0 && markers.Has(lineNumber, node.MarkerIndex);

        if (node.Text.Length == 0) return true; // empty pattern matches everything

        // Plain literal already resolved by the automaton pass.
        if (node.LiteralBit >= 0) return (context.Hits[node.LiteralBit >> 6] & (1UL << (node.LiteralBit & 63))) != 0;

        // Regex rewritten to "literal .+ literal ...".
        if (node.Sequence is not null)
        {
            if (node.SequenceBits is not null)
            {
                // Every literal must occur somewhere before the ordering can possibly hold.
                foreach (int bit in node.SequenceBits)
                    if ((context.Hits[bit >> 6] & (1UL << (bit & 63))) == 0) return false;
            }
            return RegexLiteralRewriter.Matches(line, node.Sequence, node.Comparison);
        }

        if (node.IsRegex)
        {
            // Use this thread's own Regex instance: a shared one serializes on its internal runner cache.
            return (context.Regexes[node.Index] ??= CloneRegex(node)).IsMatch(line);
        }
        return line.Contains(node.Text, node.Comparison);
    }

    /// <summary>Stand-in for a pattern that failed to compile: "(?!)" can never match.</summary>
    private static readonly Regex AlwaysFails = new("(?!)", RegexOptions.None);

    private static Regex CloneRegex(Node node)
    {
        if (node.Regex is null) return AlwaysFails;
        try { return new Regex(node.Regex.ToString(), node.Regex.Options); }
        catch (ArgumentException) { return AlwaysFails; }
    }
}

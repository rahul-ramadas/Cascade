using System.Text.RegularExpressions;
using Cascade.Core.Markers;
using Cascade.Core.Model;

namespace Cascade.Core.Filtering;

/// <summary>Result of evaluating one line: whether it is shown and, if so, the filter whose style
/// colors it (the deepest enabled include that matched; ties broken by document order).</summary>
public readonly record struct LineEval(bool Shown, Filter? ColorFilter);

/// <summary>
/// An immutable, compiled snapshot of the filter tree used to evaluate lines on background threads
/// without racing UI edits. Regexes are compiled once. Implements the exact hierarchical semantics:
/// a line is shown iff some enabled include filter <i>deep-matches</i> (it and all ancestors' predicates
/// match) and no enabled exclude deep-matches; the color is the deepest enabled matching include.
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
        public int Depth;
        public int Index;
        public bool SubtreeHasEnabled;
        public Node[] Children = Array.Empty<Node>();
        public Filter Source = null!;
    }

    private readonly Node[] _roots;
    private readonly Dictionary<Filter, int> _index;
    private readonly Node[] _nodesByIndex;

    public bool ShowOnlyFilteredLines { get; }
    public bool HasAnyEnabled { get; }
    public bool HasEnabledInclude { get; }
    /// <summary>True if a marker-type filter participates in filtering (it is enabled, or it is an ancestor
    /// of an enabled filter). When false, toggling a line marker cannot change any filter result, so the
    /// view need not be re-filtered.</summary>
    public bool HasMarkerFilter { get; }
    public int FilterCount { get; }

    private FilterSnapshot(Node[] roots, Dictionary<Filter, int> index, Node[] nodesByIndex, int filterCount,
        bool showOnlyFiltered, bool hasAnyEnabled, bool hasEnabledInclude, bool hasMarkerFilter)
    {
        _roots = roots;
        _index = index;
        _nodesByIndex = nodesByIndex;
        FilterCount = filterCount;
        ShowOnlyFilteredLines = showOnlyFiltered;
        HasAnyEnabled = hasAnyEnabled;
        HasEnabledInclude = hasEnabledInclude;
        HasMarkerFilter = hasMarkerFilter;
    }

    /// <summary>Maps a source filter to its count index (aligned with the counts array).</summary>
    public bool TryGetIndex(Filter filter, out int index) => _index.TryGetValue(filter, out index);

    /// <summary>True if <paramref name="target"/> <i>deep-matches</i> the line: its own predicate and
    /// every ancestor's predicate match (independent of enabled state). Used by per-filter find.</summary>
    public bool DeepMatches(ReadOnlySpan<char> line, long lineNumber, MarkerStore? markers, Filter target)
    {
        for (Filter? f = target; f is not null; f = f.Parent)
        {
            if (!_index.TryGetValue(f, out int idx)) return false; // not part of this snapshot
            if (!Matches(_nodesByIndex[idx], line, lineNumber, markers)) return false;
        }
        return true;
    }

    public static FilterSnapshot Build(FilterCollection filters)
    {
        bool anyEnabled = false, anyInclude = false, anyMarker = false;
        int counter = 0;
        var index = new Dictionary<Filter, int>();
        var nodes = new List<Node>();

        Node Convert(Filter f, int depth)
        {
            var node = new Node
            {
                Type = f.Match.Type,
                Text = f.Match.Text,
                CaseSensitive = f.Match.CaseSensitive,
                MarkerIndex = f.Match.MarkerIndex,
                Enabled = f.Enabled,
                Kind = f.Kind,
                Depth = depth,
                Index = counter++,
                Source = f
            };
            index[f] = node.Index;
            nodes.Add(node);

            if (f.Match.Type == FilterMatchType.Text && f.Match.Regex && f.Match.Text.Length > 0)
            {
                var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
                if (!f.Match.CaseSensitive) options |= RegexOptions.IgnoreCase;
                try { node.Regex = new Regex(f.Match.Text, options); }
                catch (ArgumentException) { node.Regex = null; } // invalid regex → never matches
            }
            node.IsRegex = f.Match.Type == FilterMatchType.Text && f.Match.Regex;

            if (f.Enabled)
            {
                anyEnabled = true;
                if (f.Kind == FilterKind.Include) anyInclude = true;
            }

            var children = new Node[f.Children.Count];
            for (int i = 0; i < f.Children.Count; i++) children[i] = Convert(f.Children[i], depth + 1);
            node.Children = children;

            node.SubtreeHasEnabled = f.Enabled;
            foreach (var c in children) node.SubtreeHasEnabled |= c.SubtreeHasEnabled;
            if (node.Type == FilterMatchType.Marker && node.SubtreeHasEnabled) anyMarker = true;
            return node;
        }

        var roots = new Node[filters.Roots.Count];
        for (int i = 0; i < filters.Roots.Count; i++) roots[i] = Convert(filters.Roots[i], 0);

        return new FilterSnapshot(roots, index, nodes.ToArray(), counter, filters.ShowOnlyFilteredLines, anyEnabled, anyInclude, anyMarker);
    }

    /// <summary>Evaluates a single line. <paramref name="markers"/> may be null when no marker
    /// filters exist.</summary>
    public LineEval Evaluate(ReadOnlySpan<char> line, long lineNumber, MarkerStore? markers)
        => Evaluate(line, lineNumber, markers, null);

    /// <summary>Evaluates a line and, when <paramref name="counts"/> is provided (size
    /// <see cref="FilterCount"/>), increments the entry of every enabled filter that deep-matches.</summary>
    public LineEval Evaluate(ReadOnlySpan<char> line, long lineNumber, MarkerStore? markers, long[]? counts)
    {
        int bestDepth = -1;
        Filter? best = null;
        bool excluded = false;
        bool anyIncludeMatched = false;

        foreach (var root in _roots)
            Dfs(root, line, lineNumber, markers, counts, ref bestDepth, ref best, ref excluded, ref anyIncludeMatched);

        bool included = HasEnabledInclude ? anyIncludeMatched : true;
        bool shown = included && !excluded;
        return new LineEval(shown, shown ? best : null);
    }

    private void Dfs(Node node, ReadOnlySpan<char> line, long lineNumber, MarkerStore? markers, long[]? counts,
        ref int bestDepth, ref Filter? best, ref bool excluded, ref bool anyIncludeMatched)
    {
        if (!node.SubtreeHasEnabled) return;          // prune: nothing enabled at/below
        if (!Matches(node, line, lineNumber, markers)) return; // prune: descendants require this match

        if (node.Enabled)
        {
            if (counts is not null) counts[node.Index]++;
            if (node.Kind == FilterKind.Include)
            {
                anyIncludeMatched = true;
                if (node.Depth > bestDepth) { bestDepth = node.Depth; best = node.Source; }
            }
            else
            {
                excluded = true;
            }
        }

        foreach (var child in node.Children)
            Dfs(child, line, lineNumber, markers, counts, ref bestDepth, ref best, ref excluded, ref anyIncludeMatched);
    }

    private static bool Matches(Node node, ReadOnlySpan<char> line, long lineNumber, MarkerStore? markers)
    {
        if (node.Type == FilterMatchType.Marker)
            return markers is not null && node.MarkerIndex >= 0 && markers.Has(lineNumber, node.MarkerIndex);

        if (node.Text.Length == 0) return true; // empty pattern matches everything
        if (node.IsRegex) return node.Regex is not null && node.Regex.IsMatch(line); // invalid regex → no match
        var cmp = node.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return line.Contains(node.Text, cmp);
    }
}

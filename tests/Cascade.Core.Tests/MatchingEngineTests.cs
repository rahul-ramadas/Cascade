using System.Text;
using System.Text.RegularExpressions;
using Cascade.Core.Filtering;
using Cascade.Core.Markers;
using Cascade.Core.Model;

namespace Cascade.Core.Tests;

/// <summary>
/// The matching engine scans each line once for ALL literal filters (an Aho-Corasick automaton) and rewrites
/// "literal .+ literal" regexes into plain substring searches. Both are large behavioural rewrites of the
/// hottest code in the product, so these tests check them against the straightforward implementations they
/// replaced: <see cref="string.Contains(string, StringComparison)"/> and <see cref="Regex"/> itself.
/// </summary>
public class MatchingEngineTests
{
    // ---- Aho-Corasick automaton ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Automaton_agrees_with_Contains_on_random_text(bool ignoreCase)
    {
        var rnd = new Random(20260726 + (ignoreCase ? 1 : 0));
        const string alphabet = "abcABC[]:_ 0123\u00e4\u00c4\u0131I";
        var patterns = new List<string>();
        for (int i = 0; i < 40; i++)
        {
            int len = 1 + rnd.Next(6);
            var sb = new StringBuilder();
            for (int k = 0; k < len; k++) sb.Append(alphabet[rnd.Next(alphabet.Length)]);
            patterns.Add(sb.ToString());
        }

        var automaton = LiteralAutomaton.TryBuild(patterns, ignoreCase);
        Assert.NotNull(automaton);
        var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var hits = new ulong[automaton!.Words];

        for (int line = 0; line < 400; line++)
        {
            int len = rnd.Next(40);
            var sb = new StringBuilder();
            for (int k = 0; k < len; k++) sb.Append(alphabet[rnd.Next(alphabet.Length)]);
            string text = sb.ToString();

            Array.Clear(hits);
            automaton.Match(text, hits);

            for (int p = 0; p < patterns.Count; p++)
            {
                bool expected = text.Contains(patterns[p], cmp);
                bool actual = (hits[p >> 6] & (1UL << (p & 63))) != 0;
                Assert.True(expected == actual,
                    $"pattern '{patterns[p]}' in '{text}' (ignoreCase={ignoreCase}): expected {expected}, got {actual}");
            }
        }
    }

    [Fact]
    public void Automaton_handles_overlapping_and_nested_patterns()
    {
        var patterns = new List<string> { "abc", "bc", "c", "abcabc", "xyz" };
        var automaton = LiteralAutomaton.TryBuild(patterns, ignoreCase: false)!;
        var hits = new ulong[automaton.Words];
        automaton.Match("zzabcabczz", hits);

        for (int p = 0; p < patterns.Count; p++)
        {
            bool expected = "zzabcabczz".Contains(patterns[p], StringComparison.Ordinal);
            Assert.Equal(expected, (hits[p >> 6] & (1UL << (p & 63))) != 0);
        }
    }

    [Fact]
    public void Automaton_ignore_case_agrees_with_dotnet_for_tricky_characters()
    {
        // Ordinal casing is not invariant uppercasing: .NET deliberately does not fold the Turkish dotless ı
        // onto 'I'. Whatever it does, the automaton must agree - so assert against .NET rather than a rule.
        string[] inputs = { "\u0131", "I", "i", "\u0130", "\u00e4", "\u00c4", "stra\u00dfe", "MASSE" };
        string[] patterns = { "I", "i", "\u0131", "\u0130", "\u00c4", "SS", "\u00df" };

        var automaton = LiteralAutomaton.TryBuild(patterns, ignoreCase: true)!;
        var hits = new ulong[automaton.Words];
        foreach (string text in inputs)
        {
            Array.Clear(hits);
            automaton.Match(text, hits);
            for (int p = 0; p < patterns.Length; p++)
                Assert.True(text.Contains(patterns[p], StringComparison.OrdinalIgnoreCase)
                            == ((hits[p >> 6] & (1UL << (p & 63))) != 0),
                    $"pattern '{patterns[p]}' in '{text}'");
        }
    }

    [Fact]
    public void Automaton_supports_more_than_64_patterns()
    {
        var patterns = Enumerable.Range(0, 200).Select(i => $"p{i}_").ToList();
        var automaton = LiteralAutomaton.TryBuild(patterns, ignoreCase: false)!;
        Assert.True(automaton.Words >= 4);

        var hits = new ulong[automaton.Words];
        automaton.Match("xx p7_ yy p199_ zz", hits);
        for (int p = 0; p < patterns.Count; p++)
        {
            bool expected = p is 7 or 199;
            Assert.Equal(expected, (hits[p >> 6] & (1UL << (p & 63))) != 0);
        }
    }

    // ---- regex rewriting ----

    [Theory]
    [InlineData(@"\[OrderService\].+Svc::", true)]
    [InlineData(@"a.+b.+c", true)]
    [InlineData(@"x.+?y", true)]
    [InlineData(@"nogap", false)]           // no separator: nothing to gain
    [InlineData(@"a.*b", false)]            // ".*" allows an empty gap - not the shape we rewrite
    [InlineData(@"a(b|c).+d", false)]       // alternation
    [InlineData(@"a\d+.+b", false)]         // character class
    [InlineData(@"^a.+b$", false)]          // anchors
    [InlineData(@"a.+", false)]             // trailing literal is empty
    public void Rewriter_only_accepts_literal_sequences(string pattern, bool expected)
        => Assert.Equal(expected, RegexLiteralRewriter.TryRewrite(pattern, out _));

    [Fact]
    public void Rewritten_sequence_agrees_with_the_regex_engine()
    {
        string[] patterns =
        {
            @"\[OrderService\].+Svc::",
            @"a.+b",
            @"a.+b.+c",
            @"x.+?y",
            @"\[A\].+\[B\].+\[C\]",
        };
        var rnd = new Random(99);
        const string alphabet = "abcxyABC[]:. ";

        foreach (string pattern in patterns)
        {
            Assert.True(RegexLiteralRewriter.TryRewrite(pattern, out string[] parts), pattern);
            foreach (bool ignoreCase in new[] { true, false })
            {
                var options = RegexOptions.CultureInvariant | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
                var rx = new Regex(pattern, options);
                var cmp = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

                for (int i = 0; i < 500; i++)
                {
                    var sb = new StringBuilder();
                    int len = rnd.Next(30);
                    for (int k = 0; k < len; k++) sb.Append(alphabet[rnd.Next(alphabet.Length)]);
                    string text = sb.ToString();
                    Assert.True(rx.IsMatch(text) == RegexLiteralRewriter.Matches(text, parts, cmp),
                        $"'{pattern}' vs '{text}' (ignoreCase={ignoreCase})");
                }
            }
        }
    }

    [Fact]
    public void Rewritten_sequence_requires_at_least_one_character_between_literals()
    {
        Assert.True(RegexLiteralRewriter.TryRewrite("a.+b", out string[] parts));
        Assert.False(RegexLiteralRewriter.Matches("ab", parts, StringComparison.Ordinal));  // ".+" needs a gap
        Assert.True(RegexLiteralRewriter.Matches("axb", parts, StringComparison.Ordinal));
        Assert.Equal(new Regex("a.+b").IsMatch("ab"), RegexLiteralRewriter.Matches("ab", parts, StringComparison.Ordinal));
    }

    // ---- whole snapshot: the engine vs a brute-force reference ----

    [Fact]
    public void Snapshot_evaluation_matches_a_brute_force_reference()
    {
        var filters = new FilterCollection();
        Filter Add(string text, bool regex = false, bool caseSensitive = false,
                   FilterKind kind = FilterKind.Include, Filter? parent = null)
        {
            var f = new Filter
            {
                Enabled = true,
                Kind = kind,
                Match = { Type = FilterMatchType.Text, Text = text, Regex = regex, CaseSensitive = caseSensitive }
            };
            filters.Add(f, parent);
            return f;
        }

        var error = Add("ERROR");
        Add("disk", parent: error);                       // nested: parent must match too
        Add("WARN");
        Add("Abc", caseSensitive: true);                  // case-sensitive literal
        Add(@"\[x\].+\[y\]", regex: true);                // rewritten to a literal sequence
        Add(@"q(1|2)z", regex: true);                     // stays a real regex
        Add("noise", kind: FilterKind.Exclude);           // exclude
        var disabled = Add("ignored");
        disabled.Enabled = false;

        var snapshot = FilterSnapshot.Build(filters);
        var all = filters.EnumerateDepthFirst().ToList();

        var rnd = new Random(4242);
        const string alphabet = "ERORWANdiskAbcqz12[]xy noise";
        for (int i = 0; i < 3_000; i++)
        {
            var sb = new StringBuilder();
            int len = rnd.Next(40);
            for (int k = 0; k < len; k++) sb.Append(alphabet[rnd.Next(alphabet.Length)]);
            if (i % 7 == 0) sb.Append("[x]mid[y]");
            if (i % 11 == 0) sb.Append("ERROR disk");
            string text = sb.ToString();

            var counts = new long[snapshot.FilterCount];
            var eval = snapshot.Evaluate(text, i, null, counts);

            // Reference: deep-match every filter with plain Contains / Regex, then apply the display rules.
            bool anyInclude = false, excluded = false;
            Filter? best = null;
            int bestDepth = -1;
            foreach (var f in all)
            {
                if (!DeepMatch(f, text)) continue;
                snapshot.TryGetIndex(f, out int idx);
                Assert.Equal(counts[idx], f.Enabled ? 1 : 0);
                if (!f.Enabled) continue;
                if (f.Kind == FilterKind.Exclude) excluded = true;
                else
                {
                    anyInclude = true;
                    if (f.Depth > bestDepth) { bestDepth = f.Depth; best = f; }
                }
            }
            bool shown = anyInclude && !excluded;
            Assert.Equal(shown, eval.Shown);
            Assert.Same(shown ? best : null, eval.ColorFilter);
        }

        static bool DeepMatch(Filter filter, string text)
        {
            for (Filter? f = filter; f is not null; f = f.Parent)
            {
                var cmp = f.Match.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                bool m = f.Match.Text.Length == 0 || (f.Match.Regex
                    ? Regex.IsMatch(text, f.Match.Text, RegexOptions.CultureInvariant |
                        (f.Match.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase))
                    : text.Contains(f.Match.Text, cmp));
                if (!m) return false;
            }
            return true;
        }
    }

    [Fact]
    public void Marker_and_empty_filters_still_work_alongside_the_automaton()
    {
        var filters = new FilterCollection();
        var marker = new Filter { Enabled = true, Match = { Type = FilterMatchType.Marker, MarkerIndex = 2 } };
        var literal = new Filter { Enabled = true, Match = { Type = FilterMatchType.Text, Text = "keep" } };
        var empty = new Filter { Enabled = true, Match = { Type = FilterMatchType.Text, Text = "" } };
        filters.Add(marker);
        filters.Add(literal);
        filters.Add(empty);

        var markers = new MarkerStore();
        markers.Toggle(5, 2);
        var snapshot = FilterSnapshot.Build(filters);

        Assert.True(snapshot.HasMarkerFilter);
        Assert.True(snapshot.Evaluate("nothing here", 5, markers).Shown);   // marker filter matches line 5
        Assert.True(snapshot.Evaluate("keep this", 1, markers).Shown);      // literal matches
        Assert.True(snapshot.Evaluate("anything", 1, markers).Shown);       // empty pattern matches everything
    }

    [Fact]
    public void Disabled_filters_are_left_out_of_the_automaton_but_still_evaluate_correctly()
    {
        // Filters whose whole subtree is disabled are never evaluated, so keeping their patterns out of the
        // automaton keeps it small (its transition table dominates matching cost). They must still give the
        // right answer through the direct-search fallback, which per-filter find relies on.
        var filters = new FilterCollection();
        Filter Add(string text, bool enabled, Filter? parent = null)
        {
            var f = new Filter { Enabled = enabled, Match = { Type = FilterMatchType.Text, Text = text } };
            filters.Add(f, parent);
            return f;
        }

        var enabled = Add("keep", enabled: true);
        var disabledLeaf = Add("gone", enabled: false);
        // A disabled PARENT still constrains its enabled child, so it does take part in evaluation.
        var disabledParent = Add("outer", enabled: false);
        var enabledChild = Add("inner", enabled: true, parent: disabledParent);

        var snapshot = FilterSnapshot.Build(filters);

        // Only lines matching an enabled include are shown; the disabled leaf must not make a line visible.
        Assert.True(snapshot.Evaluate("keep this", 0, null).Shown);
        Assert.False(snapshot.Evaluate("gone from view", 0, null).Shown);

        // The disabled parent still constrains: "inner" alone is not enough, it must also contain "outer".
        Assert.False(snapshot.Evaluate("inner only", 0, null).Shown);
        Assert.True(snapshot.Evaluate("outer and inner", 0, null).Shown);

        // Per-filter find evaluates filters regardless of enabled state, including the excluded leaf.
        Assert.True(snapshot.DeepMatches("gone from view", 0, null, disabledLeaf));
        Assert.False(snapshot.DeepMatches("nothing", 0, null, disabledLeaf));
        Assert.True(snapshot.DeepMatches("outer and inner", 0, null, enabledChild));
        Assert.False(snapshot.DeepMatches("inner only", 0, null, enabledChild));
        Assert.True(snapshot.DeepMatches("keep", 0, null, enabled));
    }

    [Fact]
    public void Enabling_fewer_filters_builds_a_smaller_automaton()
    {
        // Guards the optimization itself: the hit bitset widens with the number of patterns in the automaton,
        // so a mostly-disabled filter set must not pay for the patterns it never evaluates.
        var filters = new FilterCollection();
        var all = new List<Filter>();
        for (int i = 0; i < 200; i++)
        {
            var f = new Filter { Enabled = true, Match = { Type = FilterMatchType.Text, Text = $"pattern_{i}_" } };
            filters.Add(f);
            all.Add(f);
        }

        var ctxAllEnabled = FilterSnapshot.Build(filters).CreateContext();
        foreach (var f in all.Skip(3)) f.Enabled = false;
        var ctxFewEnabled = FilterSnapshot.Build(filters).CreateContext();

        Assert.True(WordCount(ctxFewEnabled) < WordCount(ctxAllEnabled),
            $"expected a smaller hit bitset when only 3 of 200 filters are enabled " +
            $"(got {WordCount(ctxFewEnabled)} vs {WordCount(ctxAllEnabled)} words)");

        static int WordCount(FilterSnapshot.MatchContext ctx) => ctx.HitWords;
    }

    [Fact]
    public void Invalid_regex_never_matches()
    {
        var filters = new FilterCollection();
        filters.Add(new Filter { Enabled = true, Match = { Type = FilterMatchType.Text, Text = "([unclosed", Regex = true } });
        var snapshot = FilterSnapshot.Build(filters);
        Assert.False(snapshot.Evaluate("([unclosed", 0, null).Shown);
        Assert.False(snapshot.Evaluate("anything", 0, null).Shown);
    }

    [Fact]
    public void Evaluation_is_consistent_across_threads()
    {
        // Each thread gets its own scratch (and its own Regex): results must not depend on concurrency.
        var filters = new FilterCollection();
        filters.Add(new Filter { Enabled = true, Match = { Type = FilterMatchType.Text, Text = "alpha" } });
        filters.Add(new Filter { Enabled = true, Match = { Type = FilterMatchType.Text, Text = @"a(b|c)z", Regex = true } });
        filters.Add(new Filter { Enabled = true, Match = { Type = FilterMatchType.Text, Text = @"x\[.+\]y", Regex = true } });
        var snapshot = FilterSnapshot.Build(filters);

        string[] lines = { "alpha", "abz", "x[q]y", "nothing", "ALPHA", "acz" };
        var expected = lines.Select(l => snapshot.Evaluate(l, 0, null).Shown).ToArray();

        Parallel.For(0, 2_000, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
        {
            var ctx = snapshot.CreateContext();
            for (int k = 0; k < lines.Length; k++)
                Assert.Equal(expected[k], snapshot.Evaluate(lines[k], 0, null, null, ctx).Shown);
        });
    }
}

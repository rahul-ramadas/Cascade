using Cascade.Core.Filtering;
using Cascade.Core.Markers;
using Cascade.Core.Model;

namespace Cascade.Core.Tests;

/// <summary>
/// Which filter gives a line its colour.
///
/// <para>Two rules decide it, and neither is depth:</para>
/// <list type="bullet">
/// <item>the <b>first</b> enabled include in the list (read top to bottom, as the tree is drawn) that
/// deep-matches <b>claims</b> the line - even when it sets no style at all, in which case the line is
/// drawn in the view's default colours;</item>
/// <item>only filters <b>nested under that claimant</b> may take it from there, and among those, again
/// the first in the list.</item>
/// </list>
///
/// <para>Depth is not a tie-break and never overrides list order: it only ever mattered because a
/// descendant is always deeper than its ancestor. A deeper filter in a <i>different</i> branch loses to
/// an earlier, shallower one.</para>
/// </summary>
public class FilterColouringTests
{
    private static readonly RgbColor Red = new(0xC0, 0x00, 0x00);
    private static readonly RgbColor Blue = new(0x00, 0x00, 0xC0);
    private static readonly RgbColor Pink = new(0xFF, 0xC0, 0xCB);
    private static readonly ResolvedStyle Defaults =
        new(new RgbColor(0x10, 0x20, 0x30), new RgbColor(0xF0, 0xF0, 0xF0), false, false);

    private static Filter Text(string text, bool enabled = true, RgbColor? fg = null, RgbColor? bg = null,
                               FilterKind kind = FilterKind.Include, bool regex = false, bool caseSensitive = false)
        => new()
        {
            Enabled = enabled,
            Kind = kind,
            Description = text,
            Match = { Type = FilterMatchType.Text, Text = text, Regex = regex, CaseSensitive = caseSensitive },
            Style = { Foreground = fg, Background = bg }
        };

    private static Filter Marker(int index, bool enabled = true, RgbColor? fg = null)
        => new()
        {
            Enabled = enabled,
            Description = "marker" + index,
            Match = { Type = FilterMatchType.Marker, MarkerIndex = index },
            Style = { Foreground = fg }
        };

    private static LineEval Eval(FilterCollection c, string line, MarkerStore? markers = null)
        => FilterSnapshot.Build(c).Evaluate(line.AsSpan(), 0, markers);

    // ---------------------------------------------------------------- claiming

    [Fact]
    public void The_first_match_claims_the_line_even_when_it_sets_no_style()
    {
        // plain sets nothing, so the line must come out in the view's defaults. The coloured filter below
        // it matches too, but it is lower in the list and in a branch of its own, so it does not get a say.
        var c = new FilterCollection();
        var plain = Text("cat");
        var animals = Text("sat");
        var feline = Text("cat", fg: Red);
        c.Add(plain);
        c.Add(animals);
        c.Add(feline, animals);

        var eval = Eval(c, "the cat sat");
        Assert.True(eval.Shown);
        Assert.Same(plain, eval.ColorFilter);
        Assert.Equal(Defaults, StyleResolver.Resolve(eval.ColorFilter!, Defaults));
    }

    [Fact]
    public void A_nested_filter_wins_when_it_is_the_first_match_in_the_list()
    {
        // Being nested is no handicap: the list is read as it is drawn, so a child of the first root comes
        // before the second root.
        var c = new FilterCollection();
        var animals = Text("sat");
        var feline = Text("cat", fg: Red);
        var plain = Text("cat");
        c.Add(animals);
        c.Add(feline, animals);
        c.Add(plain);

        Assert.Same(feline, Eval(c, "the cat sat").ColorFilter);
    }

    [Fact]
    public void A_descendant_of_the_claiming_filter_refines_it()
    {
        var c = new FilterCollection();
        var error = Text("Error", bg: Pink);
        var disk = Text("disk", fg: Red);
        c.Add(error);
        c.Add(disk, error);

        var eval = Eval(c, "Error: disk failure");
        Assert.Same(disk, eval.ColorFilter);

        // ...and the refinement inherits what it does not set from its own ancestors.
        var style = StyleResolver.Resolve(eval.ColorFilter!, Defaults);
        Assert.Equal(Red, style.Foreground);
        Assert.Equal(Pink, style.Background);
    }

    [Fact]
    public void The_first_descendant_wins_not_the_deepest_one()
    {
        // a claims. b and f are both below a, but b and f are in different branches of it, so between
        // those two it is list order that decides - not that f is nested one level deeper.
        var c = new FilterCollection();
        var a = Text("a");
        var b = Text("b", fg: Blue);
        var e = Text("e");
        var f = Text("f", fg: Red);
        c.Add(a);
        c.Add(b, a);
        c.Add(e, a);
        c.Add(f, e);

        Assert.Same(b, Eval(c, "a b e f").ColorFilter);

        // With b out of the way the same line goes to f, which proves the fixture really can reach it.
        b.Enabled = false;
        Assert.Same(f, Eval(c, "a b e f").ColorFilter);
    }

    [Fact]
    public void Siblings_under_the_claimant_break_ties_topmost()
    {
        var c = new FilterCollection();
        var parent = Text("p");
        var first = Text("x", fg: Blue);
        var second = Text("x", fg: Red);
        c.Add(parent);
        c.Add(first, parent);
        c.Add(second, parent);

        Assert.Same(first, Eval(c, "p x").ColorFilter);
    }

    [Fact]
    public void A_whole_later_subtree_cannot_take_the_line_from_the_first_match()
    {
        // The second root is three levels deep and every level matches. It still loses to a bare root
        // above it, because nothing in it is nested under that root.
        var c = new FilterCollection();
        var top = Text("line");
        var one = Text("one");
        var two = Text("two");
        var three = Text("three", fg: Red);
        c.Add(top);
        c.Add(one);
        c.Add(two, one);
        c.Add(three, two);

        Assert.Same(top, Eval(c, "line one two three").ColorFilter);

        // Reverse the two roots and the deep chain is now first, so it claims and refines down to three.
        var reordered = new FilterCollection();
        reordered.Add(one);
        reordered.Add(top);
        Assert.Same(three, FilterSnapshot.Build(reordered).Evaluate("line one two three".AsSpan(), 0, null).ColorFilter);
    }

    [Fact]
    public void Refinement_continues_through_a_disabled_middle_filter()
    {
        // b is switched off so it never claims anything, but it does not break the chain: d is still a
        // refinement of a and still takes the line.
        var c = new FilterCollection();
        var a = Text("a", bg: Pink);
        var b = Text("b", enabled: false, fg: Blue);
        var d = Text("d");
        c.Add(a);
        c.Add(b, a);
        c.Add(d, b);

        var eval = Eval(c, "a b d");
        Assert.Same(d, eval.ColorFilter);

        // d sets nothing, so it inherits through the disabled b to a's pink, and b's blue on the way.
        var style = StyleResolver.Resolve(eval.ColorFilter!, Defaults);
        Assert.Equal(Blue, style.Foreground);
        Assert.Equal(Pink, style.Background);
    }

    [Fact]
    public void A_disabled_claimant_hands_the_line_to_the_next_match_in_the_list()
    {
        var c = new FilterCollection();
        var off = Text("cat", enabled: false, fg: Blue);
        var on = Text("cat", fg: Red);
        c.Add(off);
        c.Add(on);

        Assert.Same(on, Eval(c, "the cat").ColorFilter);
    }

    [Fact]
    public void A_subtree_with_nothing_enabled_takes_no_part_in_claiming()
    {
        var c = new FilterCollection();
        var dormant = Text("cat", enabled: false);
        var alsoOff = Text("the", enabled: false, fg: Blue);
        var live = Text("cat", fg: Red);
        c.Add(dormant);
        c.Add(alsoOff, dormant);
        c.Add(live);

        Assert.Same(live, Eval(c, "the cat").ColorFilter);
    }

    [Fact]
    public void A_full_depth_chain_ends_at_its_deepest_enabled_link()
    {
        var c = new FilterCollection();
        Filter? parent = null;
        var chain = new List<Filter>();
        for (int i = 0; i < FilterCollection.MaxDepth; i++)
        {
            var f = Text("t" + i);
            c.Add(f, parent);
            chain.Add(f);
            parent = f;
        }
        string line = string.Join(' ', chain.Select(f => f.Match.Text));
        Assert.Same(chain[^1], Eval(c, line).ColorFilter);

        // Switch the last two off and the claim settles on the deepest one still enabled.
        chain[^1].Enabled = false;
        chain[^2].Enabled = false;
        Assert.Same(chain[^3], Eval(c, line).ColorFilter);

        // A bare root above the chain takes the line off it entirely.
        var above = Text("t0");
        c.Add(above, null, 0);
        Assert.Same(above, Eval(c, line).ColorFilter);
    }

    // ---------------------------------------------------------------- excludes

    [Fact]
    public void An_enabled_exclude_hides_the_line_wherever_it_sits()
    {
        foreach (int at in new[] { 0, 1 })
        {
            var c = new FilterCollection();
            var keep = Text("cat", fg: Red);
            var drop = Text("dog", kind: FilterKind.Exclude);
            c.Add(keep);
            c.Add(drop, null, at);   // above the winner, then below it

            var eval = Eval(c, "the cat and dog");
            Assert.False(eval.Shown);
            Assert.Null(eval.ColorFilter);      // a hidden line names no filter
            Assert.True(Eval(c, "the cat").Shown);
        }
    }

    [Fact]
    public void An_exclude_only_applies_inside_its_own_ancestors()
    {
        var c = new FilterCollection();
        var error = Text("Error");
        var retry = Text("will retry", kind: FilterKind.Exclude);
        var other = Text("Note");
        c.Add(error);
        c.Add(retry, error);
        c.Add(other);

        Assert.False(Eval(c, "Error: timeout, will retry").Shown);
        Assert.True(Eval(c, "Note: will retry later").Shown);        // no ancestor "Error", so the exclude sleeps
        Assert.Same(other, Eval(c, "Note: will retry later").ColorFilter);
    }

    [Fact]
    public void A_disabled_exclude_takes_nothing_away()
    {
        var c = new FilterCollection();
        var keep = Text("cat", fg: Red);
        var drop = Text("dog", enabled: false, kind: FilterKind.Exclude);
        c.Add(keep);
        c.Add(drop);

        var eval = Eval(c, "the cat and dog");
        Assert.True(eval.Shown);
        Assert.Same(keep, eval.ColorFilter);
    }

    [Fact]
    public void With_no_enabled_include_everything_shows_uncoloured_but_excludes_still_bite()
    {
        var c = new FilterCollection();
        c.Add(Text("DEBUG", kind: FilterKind.Exclude));
        c.Add(Text("cat", enabled: false, fg: Red));

        var shown = Eval(c, "INFO the cat");
        Assert.True(shown.Shown);
        Assert.Null(shown.ColorFilter);          // nothing claimed it, so the view's defaults apply
        Assert.False(Eval(c, "DEBUG chatter").Shown);
    }

    [Fact]
    public void A_line_no_enabled_include_matches_is_hidden_and_uncoloured()
    {
        var c = new FilterCollection();
        c.Add(Text("cat", fg: Red));

        var eval = Eval(c, "only dogs here");
        Assert.False(eval.Shown);
        Assert.Null(eval.ColorFilter);
    }

    // ---------------------------------------------------------------- predicates

    [Fact]
    public void An_empty_pattern_matches_everything_and_claims_from_the_top()
    {
        var c = new FilterCollection();
        var catchAll = Text("");
        var specific = Text("cat", fg: Red);
        c.Add(catchAll);
        c.Add(specific);

        Assert.Same(catchAll, Eval(c, "the cat").ColorFilter);
        Assert.Same(catchAll, Eval(c, "nothing in particular").ColorFilter);
    }

    [Fact]
    public void Regexes_claim_and_refine_like_literals()
    {
        var c = new FilterCollection();
        var plainRegex = Text(@"q(1|2)z", regex: true);                 // stays a real Regex
        var sequence = Text(@"\[x\].+\[y\]", regex: true, fg: Red);     // rewritten to a literal sequence
        c.Add(plainRegex);
        c.Add(sequence);

        Assert.Same(plainRegex, Eval(c, "q1z and [x]mid[y]").ColorFilter);
        Assert.Same(sequence, Eval(c, "[x]mid[y] only").ColorFilter);

        // Nested the other way round, the sequence claims and the regex refines it.
        var nested = new FilterCollection();
        var outer = Text(@"\[x\].+\[y\]", regex: true, bg: Pink);
        var inner = Text(@"q(1|2)z", regex: true, fg: Red);
        nested.Add(outer);
        nested.Add(inner, outer);
        Assert.Same(inner, FilterSnapshot.Build(nested).Evaluate("q1z and [x]mid[y]".AsSpan(), 0, null).ColorFilter);
    }

    [Fact]
    public void Case_sensitivity_decides_whether_a_filter_can_claim()
    {
        var c = new FilterCollection();
        var exact = Text("Abc", caseSensitive: true, fg: Blue);
        var loose = Text("abc", fg: Red);
        c.Add(exact);
        c.Add(loose);

        Assert.Same(exact, Eval(c, "Abc here").ColorFilter);
        Assert.Same(loose, Eval(c, "aBc here").ColorFilter);   // exact cannot match, so the claim falls through
    }

    [Fact]
    public void An_invalid_regex_never_claims()
    {
        var c = new FilterCollection();
        var broken = Text("([unclosed", regex: true, fg: Blue);
        var good = Text("unclosed", fg: Red);
        c.Add(broken);
        c.Add(good);

        Assert.Same(good, Eval(c, "([unclosed").ColorFilter);
    }

    [Fact]
    public void Marker_filters_claim_and_constrain_like_any_other()
    {
        var markers = new MarkerStore();
        markers.Set(0, 3, true);

        var c = new FilterCollection();
        var marked = Marker(3, fg: Blue);
        var inside = Text("cat", fg: Red);
        var elsewhere = Text("cat");
        c.Add(marked);
        c.Add(inside, marked);
        c.Add(elsewhere);

        // Line 0 carries the marker, so the marker filter claims and its child refines.
        Assert.Same(inside, FilterSnapshot.Build(c).Evaluate("the cat".AsSpan(), 0, markers).ColorFilter);

        // Line 1 does not, so neither the marker filter nor its child can match.
        Assert.Same(elsewhere, FilterSnapshot.Build(c).Evaluate("the cat".AsSpan(), 1, markers).ColorFilter);
    }

    [Fact]
    public void A_filter_forced_on_for_a_find_can_claim()
    {
        // "Find this filter's next match" evaluates as though the filter were switched on. It has to be
        // able to win the colour too, or the caller cannot tell which lines it accounted for.
        var c = new FilterCollection();
        var off = Text("cat", enabled: false, fg: Blue);
        var on = Text("cat", fg: Red);
        c.Add(off);
        c.Add(on);

        Assert.Same(on, FilterSnapshot.Build(c).Evaluate("the cat".AsSpan(), 0, null).ColorFilter);
        Assert.Same(off, FilterSnapshot.Build(c, off).Evaluate("the cat".AsSpan(), 0, null).ColorFilter);
    }

    // ---------------------------------------------------------------- the other outputs are unchanged

    [Fact]
    public void Counts_and_deep_match_bits_are_not_narrowed_to_the_winner()
    {
        // Losing the colour is not the same as not matching: the list's counts and the match cache both
        // have to see every filter that deep-matched, winner or not.
        var c = new FilterCollection();
        var first = Text("cat");
        var branch = Text("sat");
        var deeper = Text("cat", fg: Red);
        var dormantParent = Text("the", enabled: false);
        var liveChild = Text("cat");
        c.Add(first);
        c.Add(branch);
        c.Add(deeper, branch);
        c.Add(dormantParent);
        c.Add(liveChild, dormantParent);

        var snapshot = FilterSnapshot.Build(c);
        var counts = new long[snapshot.FilterCount];
        var deep = new ulong[snapshot.DeepMatchWords];
        var eval = snapshot.Evaluate("the cat sat".AsSpan(), 0, null, counts, snapshot.CreateContext(), deep);

        Assert.Same(first, eval.ColorFilter);

        bool Bit(Span<ulong> bits, Filter f)
        {
            Assert.True(snapshot.TryGetIndex(f, out int i));
            return (bits[i >> 6] & (1UL << (i & 63))) != 0;
        }
        long Count(Filter f)
        {
            Assert.True(snapshot.TryGetIndex(f, out int i));
            return counts[i];
        }

        // Every enabled filter that deep-matched is counted, including the three that lost the colour.
        Assert.Equal(1, Count(first));
        Assert.Equal(1, Count(branch));
        Assert.Equal(1, Count(deeper));
        Assert.Equal(1, Count(liveChild));
        Assert.Equal(0, Count(dormantParent));   // counts follow enabled, and this one is switched off

        // The cache's bits follow matching, not enabling, so the switched-off parent is in them too - it
        // constrains its enabled child, so its result is part of what makes that child's answer.
        Assert.True(Bit(deep, first));
        Assert.True(Bit(deep, branch));
        Assert.True(Bit(deep, deeper));
        Assert.True(Bit(deep, dormantParent));
        Assert.True(Bit(deep, liveChild));
    }

    [Fact]
    public void A_subtree_with_nothing_enabled_is_left_out_of_evaluation_but_still_explains_a_line()
    {
        // Evaluation skips a subtree that could not change the answer. Asking what matched a line is a
        // different question - there a switched-off filter that would have matched is worth knowing about.
        var c = new FilterCollection();
        var live = Text("cat");
        var dormant = Text("the", enabled: false);
        c.Add(live);
        c.Add(dormant);

        var snapshot = FilterSnapshot.Build(c);
        var deep = new ulong[snapshot.DeepMatchWords];
        snapshot.Evaluate("the cat".AsSpan(), 0, null, null, snapshot.CreateContext(), deep);
        Assert.True(snapshot.TryGetIndex(dormant, out int idx));
        Assert.True((deep[idx >> 6] & (1UL << (idx & 63))) == 0);

        var explained = new ulong[snapshot.DeepMatchWords];
        snapshot.MatchingFilters("the cat".AsSpan(), 0, null, explained);
        Assert.True((explained[idx >> 6] & (1UL << (idx & 63))) != 0);
    }

    // ---------------------------------------------------------------- style of the winner

    [Fact]
    public void The_winner_inherits_only_from_its_own_ancestors()
    {
        // The filter above the winner in the list is not above it in the tree, and must contribute nothing.
        var c = new FilterCollection();
        var loudButLosing = Text("nothing here", fg: Red, bg: Red);
        var parent = Text("a", bg: Pink);
        var winner = Text("b");
        c.Add(loudButLosing);
        c.Add(parent);
        c.Add(winner, parent);

        var eval = Eval(c, "a b");
        Assert.Same(winner, eval.ColorFilter);
        var style = StyleResolver.Resolve(eval.ColorFilter!, Defaults);
        Assert.Equal(Defaults.Foreground, style.Foreground);   // not the red above it in the list
        Assert.Equal(Pink, style.Background);                  // but its own parent's pink
    }

    // ---------------------------------------------------------------- moving filters about

    [Fact]
    public void Nesting_a_filter_under_the_one_above_makes_it_a_refinement()
    {
        var c = new FilterCollection();
        var top = Text("a", bg: Pink);
        var other = Text("b", fg: Red);
        c.Add(top);
        c.Add(other);

        Assert.Same(top, Eval(c, "a b").ColorFilter);           // two roots: the first claims

        Assert.True(c.Indent(other));
        var eval = Eval(c, "a b");
        Assert.Same(other, eval.ColorFilter);                   // now a refinement, so it takes the line
        Assert.Equal(Pink, StyleResolver.Resolve(eval.ColorFilter!, Defaults).Background);
    }

    [Fact]
    public void Outdenting_a_filter_takes_it_out_of_its_parents_claim()
    {
        var c = new FilterCollection();
        var top = Text("a", bg: Pink);
        var child = Text("b", fg: Red);
        c.Add(top);
        c.Add(child, top);

        Assert.Same(child, Eval(c, "a b").ColorFilter);

        Assert.True(c.Outdent(child));                          // lands directly after its old parent
        var eval = Eval(c, "a b");
        Assert.Same(top, eval.ColorFilter);                     // a claims, and b is no longer below it
        Assert.Equal(Defaults.Background, StyleResolver.Resolve(child, Defaults).Background);   // pink no longer inherited
    }

    [Fact]
    public void Dragging_a_filter_between_subtrees_moves_the_claim_with_it()
    {
        var c = new FilterCollection();
        var alpha = Text("a", bg: Pink);
        var beta = Text("b", bg: Blue);
        var rover = Text("r", fg: Red);
        c.Add(alpha);
        c.Add(beta);
        c.Add(rover, beta);                     // starts in the second subtree

        // alpha claims "a b r" and rover, being in beta's branch, cannot take it.
        Assert.Same(alpha, Eval(c, "a b r").ColorFilter);

        Assert.True(c.Move(rover, alpha, 0));   // drag it into the first subtree
        var eval = Eval(c, "a b r");
        Assert.Same(rover, eval.ColorFilter);   // now it refines the claimant
        Assert.Equal(Pink, StyleResolver.Resolve(eval.ColorFilter!, Defaults).Background);

        Assert.True(c.Move(rover, beta, 0));    // and back again
        Assert.Same(alpha, Eval(c, "a b r").ColorFilter);
        Assert.Equal(Blue, StyleResolver.Resolve(rover, Defaults).Background);
    }

    [Fact]
    public void Reordering_roots_hands_the_claim_to_whichever_is_now_first()
    {
        var c = new FilterCollection();
        var first = Text("a", fg: Blue);
        var second = Text("b", fg: Red);
        c.Add(first);
        c.Add(second);

        Assert.Same(first, Eval(c, "a b").ColorFilter);
        Assert.True(c.Reorder(second, -1));
        Assert.Same(second, Eval(c, "a b").ColorFilter);
    }

    [Fact]
    public void Moving_several_filters_at_once_keeps_the_rule()
    {
        var c = new FilterCollection();
        var host = Text("h", bg: Pink);
        var one = Text("one", fg: Blue);
        var two = Text("two", fg: Red);
        c.Add(host);
        c.Add(one);
        c.Add(two);

        Assert.Same(host, Eval(c, "h one two").ColorFilter);

        Assert.True(c.MoveMany(new[] { one, two }, host, 0));
        Assert.Equal(new[] { one, two }, host.Children);
        Assert.Same(one, Eval(c, "h one two").ColorFilter);   // first child of the claimant, not the last

        // Move them back out, after the host, and the host claims again.
        Assert.True(c.MoveMany(new[] { one, two }, null, 1));
        Assert.Same(host, Eval(c, "h one two").ColorFilter);
    }

    [Fact]
    public void A_moved_filter_carries_its_whole_subtree_into_the_new_claim()
    {
        var c = new FilterCollection();
        var alpha = Text("a", bg: Pink);
        var beta = Text("b");
        var rover = Text("r");
        var cub = Text("c", fg: Red);
        c.Add(alpha);
        c.Add(beta);
        c.Add(rover, beta);
        c.Add(cub, rover);

        Assert.Same(alpha, Eval(c, "a b r c").ColorFilter);

        Assert.True(c.Move(rover, alpha, 0));
        var eval = Eval(c, "a b r c");
        Assert.Same(cub, eval.ColorFilter);   // the grandchild came along and is now the deepest refinement
        Assert.Equal(Pink, StyleResolver.Resolve(eval.ColorFilter!, Defaults).Background);
    }

    // ---------------------------------------------------------------- differential test

    [Fact]
    public void The_engine_agrees_with_a_plain_walk_of_the_list_over_random_trees()
    {
        // The engine decides the winner with pre-order index arithmetic. This reference does it the way the
        // rule is written - walk the list from the top, first match claims, only its descendants may take
        // over - so agreement is a real cross-check and not the same code twice.
        var rnd = new Random(90210);
        string[] tokens = { "alpha", "beta", "gamma", "delta", "eps", "zeta", "Abc", "q1z", "[x]m[y]" };

        for (int trial = 0; trial < 400; trial++)
        {
            var c = new FilterCollection();
            var all = new List<Filter>();
            int wanted = rnd.Next(1, 18);
            for (int i = 0; i < wanted; i++)
            {
                bool nest = all.Count > 0 && rnd.Next(100) < 55;
                Filter? parent = nest ? all[rnd.Next(all.Count)] : null;
                if (parent is not null && !FilterCollection.CanMove(new Filter(), parent)) parent = null;

                var f = Text(tokens[rnd.Next(tokens.Length)],
                             enabled: rnd.Next(100) < 70,
                             kind: rnd.Next(100) < 15 ? FilterKind.Exclude : FilterKind.Include,
                             caseSensitive: rnd.Next(100) < 20);
                if (rnd.Next(100) < 10) f.Match.Text = "";        // the catch-all case
                c.Add(f, parent, rnd.Next(100) < 50 ? 0 : -1);    // sometimes at the front, to shuffle order
                all.Add(f);
            }

            var snapshot = FilterSnapshot.Build(c);
            for (int k = 0; k < 12; k++)
            {
                string line = string.Join(' ', Enumerable.Range(0, rnd.Next(1, 6)).Select(_ => tokens[rnd.Next(tokens.Length)]));
                var eval = snapshot.Evaluate(line.AsSpan(), 0, null);

                Assert.Equal(ReferenceShown(c, line), eval.Shown);
                Assert.Same(eval.Shown ? ReferenceWinner(c, line) : null, eval.ColorFilter);
            }
        }
    }

    /// <summary>The display rule, written out longhand.</summary>
    private static bool ReferenceShown(FilterCollection c, string line)
    {
        var all = c.EnumerateDepthFirst().ToList();
        bool anyEnabledInclude = all.Any(f => f.Enabled && f.Kind == FilterKind.Include);
        bool included = !anyEnabledInclude;
        foreach (var f in all)
        {
            if (!f.Enabled || !ReferenceDeepMatch(f, line)) continue;
            if (f.Kind == FilterKind.Exclude) return false;
            included = true;
        }
        return included;
    }

    /// <summary>The colour rule, written out longhand: read the list top to bottom, the first enabled
    /// include that deep-matches claims it, and only a filter nested under the current claimant may
    /// take over.</summary>
    private static Filter? ReferenceWinner(FilterCollection c, string line)
    {
        Filter? winner = null;
        foreach (var f in c.EnumerateDepthFirst())
        {
            if (!f.Enabled || f.Kind != FilterKind.Include || !ReferenceDeepMatch(f, line)) continue;
            if (winner is null || winner.IsAncestorOf(f)) winner = f;
        }
        return winner;
    }

    /// <summary>A filter deep-matches when its own predicate and every ancestor's match, whether or not
    /// those ancestors are switched on.</summary>
    private static bool ReferenceDeepMatch(Filter f, string line)
    {
        for (Filter? n = f; n is not null; n = n.Parent)
        {
            if (n.Match.Text.Length == 0) continue;   // an empty pattern matches everything
            var comparison = n.Match.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            if (!line.Contains(n.Match.Text, comparison)) return false;
        }
        return true;
    }
}

using Cascade.Core.Filtering;
using Cascade.Core.Model;

namespace Cascade.Core.Tests;

/// <summary>Golden tests for the hierarchical filter semantics: deep-match, the first matching filter in
/// list order claiming a line and only its own descendants refining it, ancestor constraints even when
/// parents are disabled, and leaf excludes scoped to their parent.</summary>
public class FilterSemanticsTests
{
    private static Filter Make(string text, bool enabled, FilterKind kind = FilterKind.Include)
        => new() { Enabled = enabled, Kind = kind, Match = { Type = FilterMatchType.Text, Text = text } };

    private static LineEval Eval(FilterCollection c, string line)
        => FilterSnapshot.Build(c).Evaluate(line.AsSpan(), 0, null);

    private static Filter Marker(int index, bool enabled)
        => new() { Enabled = enabled, Match = { Type = FilterMatchType.Marker, MarkerIndex = index } };

    [Fact]
    public void HasMarkerFilter_is_true_only_when_a_marker_filter_participates()
    {
        // No marker filter → toggling a marker can't affect results.
        var textOnly = new FilterCollection();
        textOnly.Add(Make("Error", enabled: true));
        Assert.False(FilterSnapshot.Build(textOnly).HasMarkerFilter);

        // An enabled marker filter participates.
        var enabledMarker = new FilterCollection();
        enabledMarker.Add(Marker(0, enabled: true));
        Assert.True(FilterSnapshot.Build(enabledMarker).HasMarkerFilter);

        // A disabled marker filter with no enabled descendants does NOT participate.
        var disabledMarker = new FilterCollection();
        disabledMarker.Add(Marker(0, enabled: false));
        Assert.False(FilterSnapshot.Build(disabledMarker).HasMarkerFilter);

        // A disabled marker filter that is an ancestor of an enabled child still constrains it → participates.
        var ancestor = new FilterCollection();
        var marker = Marker(1, enabled: false);
        ancestor.Add(marker);
        ancestor.Add(Make("boom", enabled: true), marker);
        Assert.True(FilterSnapshot.Build(ancestor).HasMarkerFilter);
    }

    [Fact]
    public void Example1_refinement_and_coloring()
    {
        var c = new FilterCollection();
        var error = Make("Error", true);
        var disk = Make("disk", true);
        var timeout = Make("timeout", false);
        var network = Make("network", false);
        c.Add(error);
        c.Add(disk, error);
        c.Add(timeout, disk);
        c.Add(network, error);

        var r1 = Eval(c, "Error: disk failure");
        Assert.True(r1.Shown);
        Assert.Same(disk, r1.ColorFilter);

        // Matches Timeout too, but Timeout is disabled → the claim stops at Disk.
        var r2 = Eval(c, "Error: disk timeout");
        Assert.True(r2.Shown);
        Assert.Same(disk, r2.ColorFilter);

        // Rule (d): matches Error, no enabled descendant matches → shown in parent's color.
        var r3 = Eval(c, "Error: network down");
        Assert.True(r3.Shown);
        Assert.Same(error, r3.ColorFilter);

        // Disk requires ancestor "Error" too → not shown.
        Assert.False(Eval(c, "Warning: disk full").Shown);
        Assert.False(Eval(c, "Info: all good").Shown);
    }

    [Fact]
    public void Example2_disabled_parent_still_constrains_child()
    {
        var c = new FilterCollection();
        var error = Make("Error", enabled: false);
        var disk = Make("disk", enabled: true);
        c.Add(error);
        c.Add(disk, error);

        var shown = Eval(c, "Error: disk failure");
        Assert.True(shown.Shown);
        Assert.Same(disk, shown.ColorFilter);

        Assert.False(Eval(c, "Error: network").Shown); // no "disk"
        Assert.False(Eval(c, "Warning: disk").Shown);  // no ancestor "Error"
    }

    [Fact]
    public void Example3_leaf_exclude_scoped_to_parent()
    {
        var c = new FilterCollection();
        var error = Make("Error", true);
        var retry = Make("will retry", true, FilterKind.Exclude);
        c.Add(error);
        c.Add(retry, error);

        Assert.True(Eval(c, "Error: disk failure").Shown);
        Assert.False(Eval(c, "Error: timeout, will retry").Shown); // excluded within Error scope
        // A retry line without the ancestor "Error" is unaffected by the exclude and simply not matched.
        Assert.False(Eval(c, "Note: will retry later").Shown);
    }

    [Fact]
    public void Flat_topmost_wins_color_on_ties()
    {
        var c = new FilterCollection();
        var first = Make("cat", true);
        var second = Make("cat", true);
        c.Add(first);
        c.Add(second);

        var r = Eval(c, "the cat sat");
        Assert.True(r.Shown);
        Assert.Same(first, r.ColorFilter); // first in the list claims it
    }

    [Fact]
    public void No_enabled_include_shows_all_but_excludes_still_remove()
    {
        var c = new FilterCollection();
        var noise = Make("DEBUG", true, FilterKind.Exclude);
        c.Add(noise);

        Assert.True(Eval(c, "INFO something").Shown);
        Assert.False(Eval(c, "DEBUG chatter").Shown);
    }

    [Fact]
    public void Regex_and_case_sensitivity()
    {
        var c = new FilterCollection();
        var f = new Filter { Enabled = true, Match = { Type = FilterMatchType.Text, Text = @"err\d+", Regex = true, CaseSensitive = true } };
        c.Add(f);
        Assert.True(Eval(c, "err42 happened").Shown);
        Assert.False(Eval(c, "ERR42 happened").Shown); // case-sensitive
        Assert.False(Eval(c, "error").Shown);
    }

    [Fact]
    public void Invalid_regex_never_matches()
    {
        var c = new FilterCollection();
        c.Add(new Filter { Enabled = true, Match = { Type = FilterMatchType.Text, Text = "([unclosed", Regex = true } });
        Assert.False(Eval(c, "([unclosed").Shown);
    }

    [Fact]
    public void MaxDepth_move_validation()
    {
        var c = new FilterCollection();
        Filter? prev = null;
        var nodes = new List<Filter>();
        for (int i = 0; i < FilterCollection.MaxDepth; i++)
        {
            var f = Make("f" + i, true);
            c.Add(f, prev);
            nodes.Add(f);
            prev = f;
        }
        // Chain already occupies all 8 depths (0..7). Moving a new node under the deepest must fail.
        var extra = Make("x", true);
        c.Add(extra);
        Assert.False(FilterCollection.CanMove(extra, nodes[^1]));
        Assert.True(FilterCollection.CanMove(extra, nodes[0]));
    }

    [Fact]
    public void Add_puts_a_filter_where_it_was_asked_for()
    {
        // Where a new filter lands is a preference now, and adding one below another is a command of its
        // own, so the index is no longer always "at the end".
        var c = new FilterCollection();
        Filter a = Make("a", true), b = Make("b", true);
        c.Add(a); c.Add(b);

        var top = Make("top", true);
        c.Add(top, null, 0);
        Assert.Equal(new[] { top, a, b }, c.Roots);

        var end = Make("end", true);
        c.Add(end, null, -1);
        Assert.Equal(new[] { top, a, b, end }, c.Roots);

        var between = Make("between", true);
        c.Add(between, null, 2);
        Assert.Equal(new[] { top, a, between, b, end }, c.Roots);

        // Among children, and the parent has to be set whichever index was asked for.
        var first = Make("first", true);
        c.Add(first, a, 0);
        var second = Make("second", true);
        c.Add(second, a, 1);
        Assert.Equal(new[] { first, second }, a.Children);
        Assert.Same(a, first.Parent);
        Assert.Same(a, second.Parent);

        // An index past the end is the end, not an exception.
        var late = Make("late", true);
        c.Add(late, a, 99);
        Assert.Equal(new[] { first, second, late }, a.Children);
    }

    [Fact]
    public void Reorder_moves_a_filter_within_its_own_siblings()
    {
        var c = new FilterCollection();
        Filter a = Make("a", true), b = Make("b", true), d = Make("d", true);
        c.Add(a); c.Add(b); c.Add(d);

        Assert.True(c.Reorder(b, -1));
        Assert.Equal(new[] { b, a, d }, c.Roots);
        Assert.True(c.Reorder(b, +1));
        Assert.Equal(new[] { a, b, d }, c.Roots);

        // The ends are hard stops, and a failed move must not disturb the order.
        Assert.False(c.Reorder(a, -1));
        Assert.False(c.Reorder(d, +1));
        Assert.Equal(new[] { a, b, d }, c.Roots);

        // Reordering happens within the parent, not across the whole tree.
        var child = Make("child", true);
        c.Add(child, a);
        Assert.False(c.Reorder(child, -1));
        Assert.Equal(new[] { child }, a.Children);
    }

    [Fact]
    public void Indent_nests_a_filter_under_the_one_above_it()
    {
        var c = new FilterCollection();
        Filter a = Make("a", true), b = Make("b", true);
        c.Add(a); c.Add(b);

        Assert.True(c.Indent(b));
        Assert.Equal(a, b.Parent);
        Assert.Equal(new[] { b }, a.Children);
        Assert.Equal(new[] { a }, c.Roots);

        // The first filter at a level has nothing above it to nest under.
        Assert.False(c.Indent(a));
        Assert.False(c.Indent(b));   // b is now an only child

        // Indenting appends to the end of the new parent's children.
        var e = Make("e", true);
        c.Add(e);
        Assert.True(c.Indent(e));
        Assert.Equal(new[] { b, e }, a.Children);
    }

    [Fact]
    public void Indent_refuses_to_exceed_the_depth_limit()
    {
        var c = new FilterCollection();
        Filter? prev = null;
        for (int i = 0; i < FilterCollection.MaxDepth; i++)
        {
            var f = Make("f" + i, true);
            c.Add(f, prev);
            prev = f;
        }
        // The chain already fills every depth, so a sibling of the deepest node cannot go one deeper.
        var sibling = Make("x", true);
        c.Add(sibling, prev!.Parent);
        Assert.False(c.Indent(sibling));
        Assert.Equal(prev.Parent, sibling.Parent);
    }

    [Fact]
    public void Outdent_moves_a_filter_out_one_level_below_its_old_parent()
    {
        var c = new FilterCollection();
        Filter a = Make("a", true), b = Make("b", true), child = Make("child", true), sib = Make("sib", true);
        c.Add(a); c.Add(b);
        c.Add(child, a); c.Add(sib, a);

        Assert.True(c.Outdent(child));
        Assert.Null(child.Parent);
        Assert.Equal(new[] { a, child, b }, c.Roots);   // lands directly after its old parent
        Assert.Equal(new[] { sib }, a.Children);

        // Top-level filters have nowhere further to go.
        Assert.False(c.Outdent(child));
        Assert.Equal(new[] { a, child, b }, c.Roots);
    }

    [Fact]
    public void Outdent_keeps_its_own_children()
    {
        var c = new FilterCollection();
        Filter a = Make("a", true), child = Make("child", true), grand = Make("grand", true);
        c.Add(a);
        c.Add(child, a);
        c.Add(grand, child);

        Assert.True(c.Outdent(child));
        Assert.Equal(new[] { a, child }, c.Roots);
        Assert.Equal(new[] { grand }, child.Children);
        Assert.Equal(child, grand.Parent);
    }

    /// <summary>The tree the two comparison tests below mutate. Ids are fixed so the copies line up.</summary>
    private static FilterCollection Shaped()
    {
        var c = new FilterCollection();
        var error = new Filter { Id = "a", Enabled = true, Match = { Text = "Error" }, Style = { Bold = true } };
        var disk = new Filter { Id = "b", Kind = FilterKind.Exclude, Match = { Text = "disk", Regex = true } };
        var marked = new Filter { Id = "c", Enabled = true, Match = { Type = FilterMatchType.Marker, MarkerIndex = 2 } };
        c.Add(error);
        c.Add(disk, error);
        c.Add(marked);
        return c;
    }

    private static Filter At(FilterCollection c, string id) => c.FindById(id)!;

    /// <summary>The rule the appearance-only edit path turns on: two trees filter the same iff every filter
    /// is still in the same place with the same predicate, kind and enabled state. Anything that only
    /// decides how a row is painted must be invisible to it, and everything else must not be.</summary>
    [Fact]
    public void SameMatching_ignores_appearance_and_catches_everything_else()
    {
        var baseline = Shaped();

        void Same(string what, Action<FilterCollection> change)
        {
            var other = Shaped();
            change(other);
            Assert.True(FilterCollection.SameMatching(baseline.Roots, other.Roots), what);
        }

        void Differs(string what, Action<FilterCollection> change)
        {
            var other = Shaped();
            change(other);
            Assert.False(FilterCollection.SameMatching(baseline.Roots, other.Roots), what);
        }

        Same("an untouched copy", _ => { });
        Same("a text colour", c => At(c, "a").Style.Foreground = new RgbColor(1, 2, 3));
        Same("a background", c => At(c, "b").Style.Background = new RgbColor(4, 5, 6));
        Same("bold", c => At(c, "a").Style.Bold = null);
        Same("italic", c => At(c, "c").Style.Italic = true);
        Same("underline", c => At(c, "a").Style.Underline = true);
        Same("a description", c => At(c, "b").Description = "disk errors");

        Differs("the pattern", c => At(c, "b").Match.Text = "disc");
        Differs("the regex flag", c => At(c, "b").Match.Regex = false);
        Differs("case sensitivity", c => At(c, "a").Match.CaseSensitive = true);
        Differs("the match type", c => At(c, "c").Match.Type = FilterMatchType.Text);
        Differs("the marker", c => At(c, "c").Match.MarkerIndex = 3);
        Differs("include or exclude", c => At(c, "b").Kind = FilterKind.Include);
        Differs("switching one on", c => At(c, "b").Enabled = true);
        Differs("switching one off", c => At(c, "a").Enabled = false);
        Differs("which filter this is", c => At(c, "a").Id = "z");
        Differs("a filter added", c => c.Add(new Filter { Id = "d", Match = { Text = "new" } }));
        Differs("a filter removed", c => c.Remove(At(c, "c")));
        Differs("a child added", c => c.Add(new Filter { Id = "d", Match = { Text = "new" } }, At(c, "a")));
        Differs("the order of two filters", c => c.Move(At(c, "c"), null, 0));
        Differs("one of them nested elsewhere", c => c.Move(At(c, "b"), At(c, "c"), 0));
        Differs("one of them un-nested", c => c.Outdent(At(c, "b")));
    }

    /// <summary>The other half of the pair: undo records an edit, so it must see a colour change and must
    /// not see a filter being switched on. The two comparisons share their predicate test, and this is what
    /// pins the difference between them.</summary>
    [Fact]
    public void SameStructure_catches_appearance_and_ignores_enabling()
    {
        var baseline = Shaped();

        var restyled = Shaped();
        At(restyled, "a").Style.Underline = true;
        Assert.False(FilterCollection.SameStructure(baseline.Roots, restyled.Roots));
        Assert.True(FilterCollection.SameMatching(baseline.Roots, restyled.Roots));

        var toggled = Shaped();
        At(toggled, "b").Enabled = true;
        Assert.True(FilterCollection.SameStructure(baseline.Roots, toggled.Roots));
        Assert.False(FilterCollection.SameMatching(baseline.Roots, toggled.Roots));

        var retyped = Shaped();
        At(retyped, "b").Match.Text = "disc";
        Assert.False(FilterCollection.SameStructure(baseline.Roots, retyped.Roots));
        Assert.False(FilterCollection.SameMatching(baseline.Roots, retyped.Roots));
    }

    /// <summary>Why skipping the pipeline for an appearance edit is legal at all: a snapshot hands back the
    /// LIVE filter, not a copy of its style, so restyling one is visible through a snapshot built before the
    /// change. If this ever stopped holding, the log view would go on painting the old colours.</summary>
    [Fact]
    public void A_lines_colour_is_read_from_the_live_filter_not_from_the_snapshot()
    {
        var c = new FilterCollection();
        var f = Make("Error", enabled: true);
        f.Style.Foreground = new RgbColor(1, 2, 3);
        c.Add(f);

        var snapshot = FilterSnapshot.Build(c);
        Assert.Same(f, snapshot.Evaluate("Error: disk".AsSpan(), 0, null).ColorFilter);

        f.Style.Foreground = new RgbColor(9, 9, 9);
        f.Style.Underline = true;

        var again = snapshot.Evaluate("Error: disk".AsSpan(), 0, null);
        Assert.Same(f, again.ColorFilter);
        var style = StyleResolver.Resolve(again.ColorFilter!, new ResolvedStyle(default, default, false, false));
        Assert.Equal(new RgbColor(9, 9, 9), style.Foreground);
        Assert.True(style.Underline);
    }
}

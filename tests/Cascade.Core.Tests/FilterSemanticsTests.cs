using Cascade.Core.Filtering;
using Cascade.Core.Model;

namespace Cascade.Core.Tests;

/// <summary>Golden tests for the hierarchical filter semantics (design §8.3): deep-match, deepest
/// enabled coloring with topmost tie-break, ancestor constraints even when parents are disabled,
/// and leaf excludes scoped to their parent.</summary>
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
    public void Example1_refinement_and_deepest_coloring()
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

        // Matches Timeout too, but Timeout is disabled → deepest *enabled* match is Disk.
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
        Assert.Same(first, r.ColorFilter); // topmost among equal depth
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
        Assert.False(c.CanMove(extra, nodes[^1]));
        Assert.True(c.CanMove(extra, nodes[0]));
    }
}

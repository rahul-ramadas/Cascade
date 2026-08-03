using Cascade.Core.Model;

namespace Cascade.Core.Tests;

/// <summary>What it means to act on several filters at once: which of them a structural operation really
/// touches, that a group move lands them together or not at all, and that changing a shared appearance
/// writes exactly the attributes it was asked for and nothing else.</summary>
public class FilterMultiSelectTests
{
    private static Filter Make(string text)
        => new() { Match = { Type = FilterMatchType.Text, Text = text } };

    /// <summary>a, b[b1, b2], c, d - one nested pair and three flat filters.</summary>
    private static FilterCollection Fixture(out Filter a, out Filter b, out Filter b1, out Filter b2, out Filter c, out Filter d)
    {
        var filters = new FilterCollection();
        filters.Add(a = Make("a"));
        filters.Add(b = Make("b"));
        filters.Add(b1 = Make("b1"), b);
        filters.Add(b2 = Make("b2"), b);
        filters.Add(c = Make("c"));
        filters.Add(d = Make("d"));
        return filters;
    }

    private static string Shape(FilterCollection filters)
        => string.Join(" ", filters.Roots.Select(Describe));

    private static string Describe(Filter f)
        => f.Children.Count == 0 ? f.Match.Text : $"{f.Match.Text}[{string.Join(" ", f.Children.Select(Describe))}]";

    [Fact]
    public void A_filter_carried_by_a_chosen_ancestor_is_not_a_root_of_its_own()
    {
        Fixture(out var a, out var b, out var b1, out var b2, out var c, out _);

        Assert.Equal(new[] { a, b, c }, FilterCollection.SelectionRoots([a, b, b1, b2, c]));
        // Order is the order it was given in, which is the order the list reads them off in.
        Assert.Equal(new[] { c, a }, FilterCollection.SelectionRoots([c, a]));
        // Cousins are all roots; a child on its own is its own root.
        Assert.Equal(new[] { b1, c }, FilterCollection.SelectionRoots([b1, c]));
        Assert.Empty(FilterCollection.SelectionRoots([]));
    }

    [Fact]
    public void Removing_several_takes_their_children_with_them_and_leaves_the_rest()
    {
        var filters = Fixture(out var a, out var b, out var b1, out _, out _, out var d);

        filters.RemoveMany([a, b, b1, d]);

        Assert.Equal("c", Shape(filters));
        // Naming a child as well as its parent must not remove it twice or strand it: the subtree comes out
        // whole, which is what lets an undo put it back.
        Assert.Same(b, b1.Parent);
        Assert.Null(b.Parent);
        Assert.DoesNotContain(b1, filters.EnumerateDepthFirst());
    }

    [Fact]
    public void Removing_several_is_one_step_to_undo()
    {
        var filters = Fixture(out var a, out _, out _, out _, out var c, out _);
        var history = new FilterHistory();

        history.Begin("Remove 2 Filters", filters);
        filters.RemoveMany([a, c]);
        history.Commit(filters);

        Assert.Equal(1, history.Count);
        Assert.Equal("b[b1 b2] d", Shape(filters));
        Assert.Equal("Remove 2 Filters", history.Undo(filters));
        Assert.Equal("a b[b1 b2] c d", Shape(filters));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Moving_several_lands_them_together_in_the_order_they_were_given()
    {
        var filters = Fixture(out var a, out var b, out _, out _, out var c, out var d);

        // a and c to the very front, keeping their order relative to each other.
        Assert.True(filters.MoveMany([a, c], null, 0));
        Assert.Equal("a c b[b1 b2] d", Shape(filters));

        // ...and to the end, from wherever they now are.
        Assert.True(filters.MoveMany([a, c], null, filters.Roots.Count));
        Assert.Equal("b[b1 b2] d a c", Shape(filters));

        // Into a filter, which is what dropping onto one means.
        Assert.True(filters.MoveMany([a, c], b, 1));
        Assert.Equal("b[b1 a c b2] d", Shape(filters));
    }

    [Fact]
    public void A_group_move_the_model_refuses_changes_nothing_at_all()
    {
        var filters = Fixture(out var a, out var b, out var b1, out _, out var c, out _);
        string before = Shape(filters);

        // Into one of the filters being moved: the group would have to contain itself.
        Assert.False(filters.MoveMany([a, b, c], b, 0));
        Assert.Equal(before, Shape(filters));

        // ...and into a filter inside one of them.
        Assert.False(filters.MoveMany([a, b, c], b1, 0));
        Assert.Equal(before, Shape(filters));

        Assert.False(filters.MoveMany([], null, 0));
        Assert.Equal(before, Shape(filters));
    }

    [Fact]
    public void A_group_move_is_refused_when_any_one_of_them_would_be_too_deep()
    {
        var filters = new FilterCollection();
        // A chain as deep as the limit allows, so nothing can be nested under its last filter.
        Filter? parent = null;
        var chain = new List<Filter>();
        for (int i = 0; i < FilterCollection.MaxDepth; i++)
        {
            var f = Make($"level{i}");
            filters.Add(f, parent);
            chain.Add(f);
            parent = f;
        }
        var flat = Make("flat");
        var tall = Make("tall");
        filters.Add(flat);
        filters.Add(tall);
        filters.Add(Make("under"), tall);
        string before = Shape(filters);

        // On its own the flat one fits under the deepest filter's parent...
        Assert.True(FilterCollection.CanMove(flat, chain[^2]));
        // ...but the two-level one does not, and one refusal refuses the group.
        Assert.False(filters.MoveMany([flat, tall], chain[^2], 0));
        Assert.Equal(before, Shape(filters));
    }

    // ---- appearance ----

    private static Filter Styled(RgbColor? fore, RgbColor? back, bool? bold, bool? italic)
        => new() { Match = { Text = "f" }, Style = { Foreground = fore, Background = back, Bold = bold, Italic = italic } };

    private static readonly RgbColor Red = new(255, 0, 0);
    private static readonly RgbColor Blue = new(0, 0, 255);

    [Fact]
    public void What_the_filters_agree_on_is_offered_back_and_what_they_do_not_is_left_alone()
    {
        var same = new[]
        {
            Styled(Red, Blue, true, null),
            Styled(Red, Blue, true, null),
        };
        var common = StyleChange.Describe(same);
        Assert.Equal(StyleEdit.Set, common.Foreground);
        Assert.Equal(Red, common.ForegroundValue);
        Assert.Equal(StyleEdit.Set, common.Bold);
        Assert.True(common.BoldValue);
        // None of them sets italic, so "inherit" is what they agree on - not "they vary".
        Assert.Equal(StyleEdit.Inherit, common.Italic);

        var differ = new[]
        {
            Styled(Red, Blue, true, null),
            Styled(Blue, Blue, false, null),
        };
        var mixed = StyleChange.Describe(differ);
        Assert.Equal(StyleEdit.Leave, mixed.Foreground);
        Assert.Equal(StyleEdit.Leave, mixed.Bold);
        Assert.Equal(StyleEdit.Set, mixed.Background);   // this one they do agree on
        Assert.Equal(StyleEdit.Inherit, mixed.Italic);

        Assert.Equal(StyleChange.Nothing, StyleChange.Describe([]));
    }

    [Fact]
    public void Only_the_attributes_the_change_speaks_for_are_written()
    {
        var f = Styled(Red, Blue, true, false);

        var justBackground = new StyleChange(
            StyleEdit.Leave, default, StyleEdit.Set, Red,
            StyleEdit.Leave, false, StyleEdit.Leave, false);
        Assert.True(justBackground.ApplyTo(f));

        Assert.Equal(Red, f.Style.Foreground);   // untouched
        Assert.Equal(Red, f.Style.Background);   // written
        Assert.True(f.Style.Bold);               // untouched
        Assert.False(f.Style.Italic);            // untouched

        // Inherit is the one that clears, and it is not the same as leaving alone.
        var clearBold = new StyleChange(
            StyleEdit.Leave, default, StyleEdit.Leave, default,
            StyleEdit.Inherit, false, StyleEdit.Leave, false);
        Assert.True(clearBold.ApplyTo(f));
        Assert.Null(f.Style.Bold);
        Assert.False(f.Style.Italic);

        // Nothing to do is reported as nothing to do, so a dialog dismissed with OK costs no re-filtering.
        Assert.False(StyleChange.Nothing.ApplyTo(f));
        Assert.False(clearBold.ApplyTo(f));
    }

    [Fact]
    public void A_change_leaves_everything_that_is_not_a_style_exactly_as_it_was()
    {
        var f = new Filter
        {
            Description = "the description",
            Kind = FilterKind.Exclude,
            Enabled = true,
            Match = { Type = FilterMatchType.Text, Text = "pattern", Regex = true, CaseSensitive = true }
        };
        var everything = new StyleChange(
            StyleEdit.Set, Red, StyleEdit.Set, Blue,
            StyleEdit.Set, true, StyleEdit.Set, true);

        Assert.True(everything.ApplyTo(f));

        Assert.Equal("the description", f.Description);
        Assert.Equal(FilterKind.Exclude, f.Kind);
        Assert.True(f.Enabled);
        Assert.Equal("pattern", f.Match.Text);
        Assert.True(f.Match.Regex);
        Assert.True(f.Match.CaseSensitive);
        Assert.Equal(Red, f.Style.Foreground);
        Assert.Equal(Blue, f.Style.Background);
    }
}

using Cascade.Core.Filtering;
using Cascade.Core.Model;

namespace Cascade.Core.Tests;

/// <summary>Golden tests for <see cref="FilterPrecedence.ListOrder"/>: one rule for the whole list, where an
/// exclude is an include whose style is "do not draw it". The first enabled filter that matches decides
/// whether the line is seen and what it looks like; only a filter nested under it may take over.
/// <para>The cases that hold under BOTH rules are left to the random-tree differential test, which covers far
/// more shapes than hand-written ones could. What is written out here is the intent - the places the two
/// rules deliberately part company.</para></summary>
public class FilterListOrderTests
{
    private static Filter Make(string text, bool enabled, FilterKind kind = FilterKind.Include)
        => new() { Enabled = enabled, Kind = kind, Match = { Type = FilterMatchType.Text, Text = text } };

    private static FilterCollection Set() => new() { Precedence = FilterPrecedence.ListOrder };

    private static LineEval Eval(FilterCollection c, string line)
        => FilterSnapshot.Build(c).Evaluate(line.AsSpan(), 0, null);

    [Fact]
    public void An_exclude_above_an_include_takes_the_line_and_below_it_does_not()
    {
        // The whole change in one test. Under the old rule the exclude wins from either position; here the
        // list is read top to bottom and position is the entire answer.
        var above = Set();
        above.Add(Make("noise", true, FilterKind.Exclude));
        above.Add(Make("Error", true));
        Assert.False(Eval(above, "Error: noise on the line").Shown);
        Assert.True(Eval(above, "Error: disk failure").Shown);

        var below = Set();
        var error = Make("Error", true);
        below.Add(error);
        below.Add(Make("noise", true, FilterKind.Exclude));
        var claimed = Eval(below, "Error: noise on the line");
        Assert.True(claimed.Shown);
        Assert.Same(error, claimed.ColorFilter);   // the include claimed it first, so it also colours it
        Assert.False(Eval(below, "just noise").Shown);
    }

    [Fact]
    public void The_same_two_filters_hide_the_same_line_under_the_old_rule_from_either_position()
    {
        // The control for the test above: without it, "position decides" could be read as something the old
        // rule did too. Same filters, same lines, and the answer does not move.
        foreach (bool excludeFirst in new[] { true, false })
        {
            var c = new FilterCollection { Precedence = FilterPrecedence.ExcludesWin };
            if (excludeFirst) c.Add(Make("noise", true, FilterKind.Exclude));
            c.Add(Make("Error", true));
            if (!excludeFirst) c.Add(Make("noise", true, FilterKind.Exclude));

            Assert.False(Eval(c, "Error: noise on the line").Shown);
            Assert.True(Eval(c, "Error: disk failure").Shown);
        }
    }

    [Fact]
    public void A_nested_exclude_beats_its_parent_and_everything_after_it()
    {
        // Nesting still refines: the exclude is inside Error's claim, so it takes over from Error - and with
        // it the whole of Error's turn, which is what keeps a later filter from reinstating the line.
        var c = Set();
        var error = Make("Error", true);
        c.Add(error);
        c.Add(Make("will retry", true, FilterKind.Exclude), error);
        var warning = Make("Warning", true);
        c.Add(warning);

        Assert.True(Eval(c, "Error: disk failure").Shown);
        Assert.False(Eval(c, "Error: timeout, will retry").Shown);
        Assert.False(Eval(c, "Error: timeout, will retry - Warning too").Shown);

        var later = Eval(c, "Warning: nothing wrong");
        Assert.True(later.Shown);
        Assert.Same(warning, later.ColorFilter);
    }

    [Fact]
    public void An_include_nested_under_an_exclude_is_still_the_exception_with_no_rule_of_its_own()
    {
        // The feature that used to need its own machinery. Here it is just the nesting rule: the include is
        // inside the exclude's claim, so it takes over on the lines it matches, and nothing else changed.
        var c = Set();
        var heartbeat = Make("Heartbeat", true, FilterKind.Exclude);
        var kept = Make("Error", true);
        c.Add(heartbeat);
        c.Add(kept, heartbeat);

        Assert.False(Eval(c, "Heartbeat ok").Shown);
        var rescued = Eval(c, "Heartbeat Error timeout");
        Assert.True(rescued.Shown);
        Assert.Same(kept, rescued.ColorFilter);   // and the filter that rescued it colours it
        Assert.True(Eval(c, "Payment accepted").Shown);
    }

    [Fact]
    public void A_deeper_filter_in_a_later_branch_still_loses_to_an_earlier_shallower_one()
    {
        // Depth is not priority - the list is. An exclude nested three deep in a later root has already lost
        // to a root include that claimed the line first.
        var c = Set();
        var first = Make("alpha", true);
        c.Add(first);
        var beta = Make("beta", true);
        c.Add(beta);
        var gamma = Make("gamma", true);
        c.Add(gamma, beta);
        c.Add(Make("delta", true, FilterKind.Exclude), gamma);

        var eval = Eval(c, "alpha beta gamma delta");
        Assert.True(eval.Shown);
        Assert.Same(first, eval.ColorFilter);
    }

    [Fact]
    public void A_catch_all_at_the_foot_of_the_list_shows_everything_nothing_else_claimed()
    {
        // How "show the rest of the file" is said once the list decides everything: a filter matching every
        // line, last. An empty pattern matches all of them - a regex "." does not match a blank line.
        var c = Set();
        c.Add(Make("noise", true, FilterKind.Exclude));
        var rest = Make("", true);
        c.Add(rest);

        Assert.False(Eval(c, "chatty noise here").Shown);
        var other = Eval(c, "");
        Assert.True(other.Shown);
        Assert.Same(rest, other.ColorFilter);
        Assert.True(Eval(c, "Payment accepted").Shown);
    }

    [Fact]
    public void With_nothing_asked_for_the_file_is_shown_less_what_the_excludes_take()
    {
        var c = Set();
        c.Add(Make("DEBUG", true, FilterKind.Exclude));
        Assert.True(FilterSnapshot.Build(c).HidesUnmatchedLines == false);
        Assert.True(Eval(c, "INFO something").Shown);
        Assert.False(Eval(c, "DEBUG chatter").Shown);
    }

    [Fact]
    public void An_exception_under_an_exclude_does_not_start_hiding_the_rest_of_the_file()
    {
        // The rule that was fixed before this one landed, checked again under the new precedence: whether a
        // filter is an exception is a question about the tree, so it cannot move with the precedence.
        var c = Set();
        var heartbeat = Make("Heartbeat", true, FilterKind.Exclude);
        c.Add(heartbeat);
        c.Add(Make("Error", true), heartbeat);
        Assert.False(FilterSnapshot.Build(c).HidesUnmatchedLines);
        Assert.True(Eval(c, "Payment accepted").Shown);

        c.Add(Make("Payment", true));   // now something does ask for its own lines
        Assert.True(FilterSnapshot.Build(c).HidesUnmatchedLines);
        Assert.False(Eval(c, "Warning: retry").Shown);
    }

    [Fact]
    public void Nothing_vetoes_so_no_subtree_answer_has_to_be_carried_back_up()
    {
        // The walk optimisation, pinned: with nothing vetoing there is no reason for the DFS to collect what
        // its children said, which is the cheap path it takes for an ordinary filter set. A snapshot built
        // for list order must never report the shape that turns it off.
        var c = Set();
        var a = Make("A", true);
        c.Add(a);
        c.Add(Make("AB", true, FilterKind.Exclude), a);
        c.Add(Make("ABC", true), a.Children[0]);
        Assert.False(FilterSnapshot.Build(c).HasOverruledExclude);

        var classic = new FilterCollection { Precedence = FilterPrecedence.ExcludesWin };
        var a2 = Make("A", true);
        classic.Add(a2);
        classic.Add(Make("AB", true, FilterKind.Exclude), a2);
        classic.Add(Make("ABC", true), a2.Children[0]);
        Assert.True(FilterSnapshot.Build(classic).HasOverruledExclude);
    }

    [Fact]
    public void The_paint_order_puts_a_filter_after_everything_it_should_beat()
    {
        // What the cached-set path relies on: applied in this order, the winner is the last to touch a line.
        // Written as the property rather than as a list of indices, so it still states the contract if the
        // tree walk that produces it is rewritten.
        var c = Set();
        var a = Make("a", true);
        var b = Make("b", true);
        c.Add(a);
        c.Add(b);
        var aKid = Make("a1", true);
        c.Add(aKid, a);
        c.Add(Make("a2", true), a);

        var snapshot = FilterSnapshot.Build(c);
        var order = snapshot.PaintOrder.Select(s => s.Index).ToList();
        Assert.Equal(snapshot.NodeCount, order.Count);              // every enabled filter takes a turn

        int At(Filter f) { Assert.True(snapshot.TryGetIndex(f, out int i)); return order.IndexOf(i); }
        Assert.True(At(aKid) > At(a), "a child must paint after its parent");
        Assert.True(At(a) > At(b), "an earlier sibling must paint after a later one");
        Assert.True(At(aKid) > At(c.Roots[0].Children[1]), "an earlier child must paint after a later one");
    }

    [Fact]
    public void A_switched_off_filter_takes_no_turn_at_painting()
    {
        var c = Set();
        c.Add(Make("on", true));
        c.Add(Make("off", false));
        var snapshot = FilterSnapshot.Build(c);
        Assert.Single(snapshot.PaintOrder);
        Assert.True(snapshot.TryGetIndex(c.Roots[0], out int index));
        Assert.Equal(index, snapshot.PaintOrder[0].Index);
    }

    [Fact]
    public void Only_an_exclude_paints_a_line_out()
    {
        var c = Set();
        c.Add(Make("keep", true));
        c.Add(Make("drop", true, FilterKind.Exclude));
        var snapshot = FilterSnapshot.Build(c);

        Assert.True(snapshot.TryGetIndex(c.Roots[1], out int excludeIndex));
        foreach (var step in snapshot.PaintOrder)
            Assert.Equal(step.Index == excludeIndex, step.Hides);
    }
}

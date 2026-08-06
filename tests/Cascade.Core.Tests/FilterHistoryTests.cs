using Cascade.Core.Model;
using Xunit;

namespace Cascade.Core.Tests;

public class FilterHistoryTests
{
    private static Filter F(string text, string? description = null, bool enabled = true) =>
        new() { Enabled = enabled, Description = description ?? "", Match = { Text = text } };

    private static FilterCollection Tree(params string[] roots)
    {
        var c = new FilterCollection();
        foreach (var r in roots) c.Add(F(r));
        return c;
    }

    private static string[] Names(FilterCollection c) => Flatten(c.Roots).ToArray();

    private static IEnumerable<string> Flatten(IReadOnlyList<Filter> filters, string prefix = "")
    {
        foreach (var f in filters)
        {
            yield return prefix + f.Match.Text;
            foreach (var child in Flatten(f.Children, prefix + f.Match.Text + "/")) yield return child;
        }
    }

    [Fact]
    public void Cloning_the_tree_is_deep_and_keeps_every_id()
    {
        var c = Tree("a", "b");
        c.Add(F("a1"), c.Roots[0]);

        var clone = c.CloneRoots();
        Assert.Equal(c.Roots[0].Id, clone[0].Id);
        Assert.Equal(c.Roots[0].Children[0].Id, clone[0].Children[0].Id);
        Assert.Same(clone[0], clone[0].Children[0].Parent);

        // Deep: editing the original must not reach into the copy.
        c.Roots[0].Match.Text = "changed";
        c.Roots[0].Children[0].Description = "changed";
        Assert.Equal("a", clone[0].Match.Text);
        Assert.Equal("", clone[0].Children[0].Description);
    }

    [Fact]
    public void Structural_comparison_ignores_enabled_but_catches_every_other_edit()
    {
        var c = Tree("a", "b");
        var before = c.CloneRoots();

        c.Roots[0].Enabled = !c.Roots[0].Enabled;
        Assert.True(FilterCollection.SameStructure(before, c.Roots));

        void Differs(Action change)
        {
            var snapshot = c.CloneRoots();
            change();
            Assert.False(FilterCollection.SameStructure(snapshot, c.Roots));
            c.ReplaceRoots(snapshot);
        }

        Differs(() => c.Roots[0].Match.Text = "other");
        Differs(() => c.Roots[0].Match.Regex = true);
        Differs(() => c.Roots[0].Match.CaseSensitive = true);
        Differs(() => c.Roots[0].Match.Type = FilterMatchType.Marker);
        Differs(() => c.Roots[0].Match.MarkerIndex = 3);
        Differs(() => c.Roots[0].Description = "note");
        Differs(() => c.Roots[0].Kind = FilterKind.Exclude);
        Differs(() => c.Roots[0].Style.Foreground = new RgbColor(1, 2, 3));
        Differs(() => c.Roots[0].Style.Background = new RgbColor(1, 2, 3));
        Differs(() => c.Roots[0].Style.Bold = true);
        Differs(() => c.Roots[0].Style.Italic = true);
        Differs(() => c.Roots[0].Style.Underline = true);
        Differs(() => c.Add(F("c")));
        Differs(() => c.Remove(c.Roots[1]));
        Differs(() => c.Reorder(c.Roots[0], +1));
        Differs(() => c.Indent(c.Roots[1]));
    }

    [Fact]
    public void Undo_and_redo_walk_back_and_forth_through_edits()
    {
        var c = Tree("a", "b");
        var h = new FilterHistory();

        h.Begin("Add Filter", c);
        c.Add(F("c"));
        h.Commit(c);

        h.Begin("Remove Filter", c);
        c.Remove(c.Roots[0]);
        h.Commit(c);

        Assert.Equal(new[] { "b", "c" }, Names(c));
        Assert.Equal("Remove Filter", h.UndoLabel);

        Assert.Equal("Remove Filter", h.Undo(c));
        Assert.Equal(new[] { "a", "b", "c" }, Names(c));
        Assert.Equal("Add Filter", h.Undo(c));
        Assert.Equal(new[] { "a", "b" }, Names(c));
        Assert.False(h.CanUndo);
        Assert.Null(h.Undo(c));

        Assert.Equal("Add Filter", h.Redo(c));
        Assert.Equal(new[] { "a", "b", "c" }, Names(c));
        Assert.Equal("Remove Filter", h.Redo(c));
        Assert.Equal(new[] { "b", "c" }, Names(c));
        Assert.False(h.CanRedo);
    }

    [Fact]
    public void Undo_restores_nesting_and_order_not_just_membership()
    {
        var c = Tree("a", "b", "c");
        var h = new FilterHistory();

        h.Begin("Move Filter", c);
        c.Move(c.Roots[2], c.Roots[0], 0);      // c becomes a's child
        c.Reorder(c.Roots[1], -1);              // b moves above a
        h.Commit(c);
        Assert.Equal(new[] { "b", "a", "a/c" }, Names(c));

        h.Undo(c);
        Assert.Equal(new[] { "a", "b", "c" }, Names(c));
        Assert.Null(c.Roots[2].Parent);
    }

    [Fact]
    public void Enabling_and_disabling_never_reaches_the_history()
    {
        var c = Tree("a", "b");
        var h = new FilterHistory();

        h.Begin("Toggle", c);
        foreach (var f in c.Roots) f.Enabled = !f.Enabled;
        h.Commit(c);

        Assert.False(h.CanUndo);
    }

    [Fact]
    public void A_cancelled_edit_records_nothing()
    {
        var c = Tree("a");
        var h = new FilterHistory();

        h.Begin("Edit Filter", c);
        h.Abandon();
        Assert.False(h.CanUndo);

        // ...and even without Abandon, an edit that changed nothing must not take a slot.
        h.Begin("Edit Filter", c);
        h.Commit(c);
        Assert.False(h.CanUndo);
    }

    [Fact]
    public void A_new_edit_clears_what_was_undone()
    {
        var c = Tree("a");
        var h = new FilterHistory();

        h.Begin("Add Filter", c);
        c.Add(F("b"));
        h.Commit(c);
        h.Undo(c);
        Assert.True(h.CanRedo);

        h.Begin("Add Filter", c);
        c.Add(F("z"));
        h.Commit(c);

        Assert.False(h.CanRedo);
        Assert.Equal(new[] { "a", "z" }, Names(c));
    }

    [Fact]
    public void The_history_is_capped_and_drops_the_oldest_edits()
    {
        var c = Tree("a");
        var h = new FilterHistory();

        for (int i = 0; i < FilterHistory.MaxEntries + 20; i++)
        {
            h.Begin($"Add {i}", c);
            c.Add(F($"f{i}"));
            h.Commit(c);
        }

        Assert.Equal(FilterHistory.MaxEntries, h.Count);
        int undone = 0;
        while (h.CanUndo) { h.Undo(c); undone++; }
        Assert.Equal(FilterHistory.MaxEntries, undone);
        // The 20 oldest additions are beyond reach, so those filters stay.
        Assert.Equal(21, c.Roots.Count);
    }

    [Fact]
    public void Restoring_keeps_ids_so_anything_keyed_on_them_still_matches()
    {
        var c = Tree("a", "b");
        string keptId = c.Roots[1].Id;
        var h = new FilterHistory();

        h.Begin("Remove Filter", c);
        c.Remove(c.Roots[1]);
        h.Commit(c);
        h.Undo(c);

        Assert.Equal(keptId, c.Roots[1].Id);
    }

    [Fact]
    public void Collection_state_outside_the_tree_survives_a_restore()
    {
        var c = Tree("a");
        c.ShowOnlyFilteredLines = true;
        var h = new FilterHistory();

        h.Begin("Add Filter", c);
        c.Add(F("b"));
        h.Commit(c);
        h.Undo(c);

        Assert.True(c.ShowOnlyFilteredLines);
    }
}

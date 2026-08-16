using Cascade.Core.Columns;

namespace Cascade.Core.Tests;

/// <summary>What the two layouts show: the cells of the grid, and the line the Inline layout rebuilds with
/// the hidden parts left out.</summary>
public class ColumnTests
{
    private const string Trace =
        "[2026-08-05T05:00:02.0472099][BthPort][6][0EF8][1590][rundown_cpp142][PerformWppRundown][INFO][TFLAG_RUNDOWN] WDF PnP state: WdfDevStatePnpStarted";

    private const string Nine = "{[*]}{[*]}{[*]}{[*]}{[*]}{[*]}{[*]}{[*]}{[*]} {*}";

    private static ColumnSpec Spec(string template)
    {
        var spec = new ColumnSpec { Enabled = true, Template = template };
        spec.Reset();
        return spec;
    }

    /// <summary>The line as the Inline layout shows it.</summary>
    private static string Inline(ColumnSpec spec, string line)
    {
        var match = new TemplateMatch();
        spec.Compiled.Match(line, match);
        var projection = new LineProjection();
        projection.Build(line, spec, match);
        return projection.Text;
    }

    /// <summary>What the grid draws: each column, in the order the columns are listed, showing the part it
    /// says it shows.</summary>
    private static List<string> Cells(ColumnSpec spec, string line)
    {
        var match = new TemplateMatch();
        spec.Compiled.Match(line, match);
        var cells = new List<string>();
        foreach (var column in spec.Columns)
        {
            if (!column.Visible) continue;
            int value = spec.Compiled.PartAt(column.Source).Value;
            if (value < 0) continue;
            var (start, length) = match.Value(value);
            cells.Add(line.Substring(start, length));
        }
        return cells;
    }

    // ---- the grid ----

    /// <summary>Carrying a header to another place has to take its data with it. The two used to be the
    /// same thing - a column showed whichever field sat at its own index - so reordering relabelled the
    /// fields and left the text where it was.</summary>
    [Fact]
    public void Reordering_the_columns_moves_the_data_not_just_the_names()
    {
        var spec = Spec("{*} {*} {*}");
        spec.Columns[0].Name = "time";
        spec.Columns[1].Name = "level";
        spec.Columns[2].Name = "message";
        const string line = "09:31 WARN disk";
        Assert.Equal(["09:31", "WARN", "disk"], Cells(spec, line));

        var message = spec.Columns[2];
        spec.Columns.RemoveAt(2);
        spec.Columns.Insert(0, message);

        Assert.Equal(["message", "time", "level"], spec.Columns.Select(c => c.Name));
        Assert.Equal(["disk", "09:31", "WARN"], Cells(spec, line));
    }

    [Fact]
    public void A_hidden_column_does_not_shift_the_ones_after_it()
    {
        var spec = Spec("{*} {*} {*}");
        spec.Columns[1].Visible = false;
        Assert.Equal(["one", "three"], Cells(spec, "one two three"));
    }

    /// <summary>A part of pure literal text is a thing to hide, not a column, so the grid passes over it.</summary>
    [Fact]
    public void A_part_that_captures_nothing_draws_no_cell()
    {
        var spec = Spec("{ERR }{*} {*}");
        Assert.Equal(3, spec.Columns.Count);
        Assert.Equal(["a", "b"], Cells(spec, "ERR a b"));
    }

    /// <summary>A file written before columns had a source of their own has to keep showing what it did:
    /// each column showing the part at its own place in the list.</summary>
    [Fact]
    public void Columns_that_never_said_which_part_they_show_take_their_own_place()
    {
        var spec = new ColumnSpec { Enabled = true, Template = "{*} {*}" };
        spec.Columns.Add(new ColumnDef { Name = "a" });
        spec.Columns.Add(new ColumnDef { Name = "b" });
        Assert.All(spec.Columns, c => Assert.Equal(-1, c.Source));
        spec.NormalizeSources();
        Assert.Equal([0, 1], spec.Columns.Select(c => c.Source));
        Assert.Equal(["1", "2"], Cells(spec, "1 2"));
    }

    /// <summary>A column left pointing past the end of the template would draw nothing for ever, so it is
    /// dropped rather than kept as an empty row nobody can explain.</summary>
    [Fact]
    public void Columns_pointing_past_the_template_are_dropped()
    {
        var spec = new ColumnSpec { Enabled = true, Template = "{*} {*}" };
        spec.Columns.Add(new ColumnDef { Name = "a", Source = 0 });
        spec.Columns.Add(new ColumnDef { Name = "stale", Source = 7 });
        spec.NormalizeSources();
        Assert.Equal(["a"], spec.Columns.Select(c => c.Name));
    }

    // ---- the inline layout ----

    /// <summary>The property that makes the layout safe to leave on: until something is hidden it changes
    /// nothing whatsoever.</summary>
    [Fact]
    public void Nothing_hidden_leaves_the_line_exactly_as_it_was()
        => Assert.Equal(Trace, Inline(Spec(Nine), Trace));

    /// <summary>Hiding a part takes its punctuation with it, so a run of bracketed fields closes up to
    /// nothing rather than leaving a trail of empty brackets.</summary>
    [Fact]
    public void Hiding_a_part_takes_its_punctuation_with_it()
    {
        var spec = Spec(Nine);
        foreach (int hide in new[] { 2, 3, 4, 8 }) spec.Columns[hide].Visible = false;
        Assert.Equal(
            "[2026-08-05T05:00:02.0472099][BthPort][rundown_cpp142][PerformWppRundown][INFO] WDF PnP state: WdfDevStatePnpStarted",
            Inline(spec, Trace));
    }

    /// <summary>The separator that is left is the one that belongs to the part still there, so hiding
    /// several in a row does not leave a space behind for each of them.</summary>
    [Fact]
    public void Hiding_several_space_separated_fields_leaves_one_space()
    {
        var spec = Spec("{*} {*} {*} {*}");
        spec.Columns[1].Visible = false;
        spec.Columns[2].Visible = false;
        Assert.Equal("a d", Inline(spec, "a b c d"));
    }

    [Fact]
    public void The_space_before_the_message_survives_hiding_the_field_before_it()
    {
        var spec = Spec(Nine);
        spec.Columns[8].Visible = false;
        Assert.Equal(
            "[2026-08-05T05:00:02.0472099][BthPort][6][0EF8][1590][rundown_cpp142][PerformWppRundown][INFO] WDF PnP state: WdfDevStatePnpStarted",
            Inline(spec, Trace));
    }

    /// <summary>Carrying a part backwards leaves no separator that means anything, so one is put in.</summary>
    [Fact]
    public void Carrying_a_part_to_the_front_joins_with_a_single_space()
    {
        var spec = Spec("{[*]}{[*]} {*}");
        var level = spec.Columns[1];
        spec.Columns.RemoveAt(1);
        spec.Columns.Insert(0, level);
        Assert.Equal("[INFO] [09:31] hello", Inline(spec, "[09:31][INFO] hello"));
    }

    /// <summary>Text the template never reached is data, not punctuation, and is never dropped.</summary>
    [Fact]
    public void The_tail_the_template_never_reached_is_kept()
    {
        Assert.Equal("[a] and the rest", Inline(Spec("{[*]}"), "[a] and the rest"));

        var spec = Spec("{[*]}");
        spec.Columns[0].Visible = false;
        Assert.Equal(" and the rest", Inline(spec, "[a] and the rest"));
    }

    /// <summary>A trailing literal in the template is part of the line too.</summary>
    [Fact]
    public void A_literal_after_the_last_part_is_kept()
        => Assert.Equal("[a]! rest", Inline(Spec("{[*]}! "), "[a]! rest"));

    /// <summary>A line the template does not fit is shown whole and untouched. Columns can shorten a line;
    /// they can never hide one.</summary>
    [Fact]
    public void A_line_that_does_not_fit_the_template_is_shown_whole()
    {
        var spec = Spec(Nine);
        spec.Columns[0].Visible = false;
        Assert.Equal("plain line", Inline(spec, "plain line"));
    }

    [Fact]
    public void Everything_hidden_leaves_only_what_the_template_never_reached()
    {
        var spec = Spec(Nine);
        foreach (var column in spec.Columns) column.Visible = false;
        Assert.Equal("", Inline(spec, Trace));
    }

    // ---- the map back to the raw line ----

    [Fact]
    public void A_character_of_the_shown_line_can_be_traced_back_to_the_file()
    {
        var spec = Spec(Nine);
        foreach (int hide in new[] { 2, 3, 4 }) spec.Columns[hide].Visible = false;

        var match = new TemplateMatch();
        spec.Compiled.Match(Trace, match);
        var projection = new LineProjection();
        projection.Build(Trace, spec, match);

        for (int i = 0; i < projection.Text.Length; i++)
        {
            int line = projection.ToLine(i);
            Assert.True(line >= 0, $"character {i} came from nowhere");
            Assert.Equal(projection.Text[i], Trace[line]);
            Assert.Equal(i, projection.FromLine(line));
        }
    }

    [Fact]
    public void A_hidden_character_maps_to_nothing()
    {
        var spec = Spec(Nine);
        spec.Columns[2].Visible = false;
        var match = new TemplateMatch();
        spec.Compiled.Match(Trace, match);
        var projection = new LineProjection();
        projection.Build(Trace, spec, match);

        var (start, length) = match.Part(2);
        for (int i = start; i < start + length; i++) Assert.Equal(-1, projection.FromLine(i));
    }

    /// <summary>Invented text belongs to no line in the file, which is what a filter or a search made from
    /// a selection across it has to be warned about.</summary>
    [Fact]
    public void Invented_text_belongs_to_no_line_and_a_selection_across_it_is_not_contiguous()
    {
        var spec = Spec("{[*]}{[*]} {*}");
        var level = spec.Columns[1];
        spec.Columns.RemoveAt(1);
        spec.Columns.Insert(0, level);

        var match = new TemplateMatch();
        spec.Compiled.Match("[09:31][INFO] hello", match);
        var projection = new LineProjection();
        projection.Build("[09:31][INFO] hello", spec, match);

        int joiner = projection.Text.IndexOf("] [", StringComparison.Ordinal) + 1;
        Assert.Equal(-1, projection.ToLine(joiner));
        Assert.False(projection.IsContiguous(0, "[09:31][INFO]".Length));
    }

    [Fact]
    public void A_selection_inside_one_part_is_contiguous()
    {
        var spec = Spec(Nine);
        spec.Columns[2].Visible = false;
        var match = new TemplateMatch();
        spec.Compiled.Match(Trace, match);
        var projection = new LineProjection();
        projection.Build(Trace, spec, match);

        var (start, length) = match.Part(1);
        Assert.True(projection.IsContiguous(start, start + length));
    }

    // ---- carrying the list across an edit to the template ----

    [Fact]
    public void A_part_added_in_the_middle_does_not_shift_the_names_along()
    {
        var spec = Spec("{[*]}{[*]} {*}");
        spec.Columns[0].Name = "Time";
        spec.Columns[1].Name = "Level";
        spec.Columns[2].Name = "Message";
        spec.Columns[1].Visible = false;

        spec.Template = "{[*]}{[*]}{[*]} {*}";
        spec.Sync(spec.Compiled.PartIndexAtOffset(7));

        Assert.Equal(["Time", "Col 2", "Level", "Message"],
            spec.Columns.OrderBy(c => c.Source).Select(c => c.Name));
        Assert.False(spec.Columns.Single(c => c.Name == "Level").Visible);
    }

    [Fact]
    public void A_part_taken_away_takes_its_own_name_with_it()
    {
        var spec = Spec("{[*]}{[*]}{[*]} {*}");
        spec.Columns[0].Name = "Time";
        spec.Columns[1].Name = "Thread";
        spec.Columns[2].Name = "Level";
        spec.Columns[3].Name = "Message";

        spec.Template = "{[*]}{[*]} {*}";
        spec.Sync(spec.Compiled.PartIndexAtOffset(5));

        Assert.Equal(["Time", "Level", "Message"],
            spec.Columns.OrderBy(c => c.Source).Select(c => c.Name));
    }

    [Fact]
    public void An_edit_that_keeps_the_same_parts_disturbs_nothing()
    {
        var spec = Spec("{[*]}{[*]} {*}");
        spec.Columns[0].Name = "When";
        spec.Columns[0].Width = 120;
        spec.Columns[0].Align = ColumnAlign.Right;

        spec.Template = "{<*>}{[*]} {*}";
        spec.Sync(1);

        Assert.Equal("When", spec.Columns[0].Name);
        Assert.Equal(120, spec.Columns[0].Width);
        Assert.Equal(ColumnAlign.Right, spec.Columns[0].Align);
    }

    [Fact]
    public void A_wholesale_edit_starts_the_list_over()
    {
        var spec = Spec("{[*]}{[*]} {*}");
        spec.Columns[0].Name = "Time";
        spec.Template = "{*}";
        spec.Sync(0);
        Assert.Equal(["Col 1"], spec.Columns.Select(c => c.Name));
    }

    // ---- the spec itself ----

    [Fact]
    public void A_spec_is_only_active_when_it_has_something_to_draw()
    {
        var spec = new ColumnSpec();
        Assert.False(spec.Active);

        spec.Enabled = true;
        Assert.False(spec.Active);              // no template yet

        spec.Template = Nine;
        spec.Reset();
        Assert.True(spec.Active);

        spec.Template = "{*}{*}";               // two values with nothing between
        Assert.False(spec.Active);
    }

    [Fact]
    public void A_clone_shares_nothing_with_what_it_came_from()
    {
        var spec = Spec("{[*]} {*}");
        var copy = spec.Clone();
        copy.Columns[0].Name = "changed";
        copy.Template = "{*}";
        Assert.Equal("Col 1", spec.Columns[0].Name);
        Assert.Equal("{[*]} {*}", spec.Template);
    }

    [Fact]
    public void The_compiled_template_is_rebuilt_only_when_the_text_changes()
    {
        var spec = Spec("{[*]} {*}");
        var first = spec.Compiled;
        Assert.Same(first, spec.Compiled);
        spec.Template = "{[*]} {*}";            // the same text is not a change
        Assert.Same(first, spec.Compiled);
        spec.Template = "{*}";
        Assert.NotSame(first, spec.Compiled);
    }
}

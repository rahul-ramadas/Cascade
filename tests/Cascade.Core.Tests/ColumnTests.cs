using Cascade.Core.Columns;

namespace Cascade.Core.Tests;

public class ColumnTests
{
    private static List<string> Split(ColumnSpec spec, string line)
    {
        var splitter = new ColumnSplitter(spec);
        var values = new List<ColumnValue>();
        splitter.Split(line, values);
        return values.Select(v => line.Substring(v.Start, v.Length)).ToList();
    }

    /// <summary>What the grid draws: each column, in the order the columns are listed, showing the field it
    /// says it shows.</summary>
    private static List<string> Cells(ColumnSpec spec, string line)
    {
        var splitter = new ColumnSplitter(spec);
        var values = new List<ColumnValue>();
        splitter.Split(line, values);
        return spec.Columns.Where(c => c.Visible)
                   .Select(c => c.Source >= 0 && c.Source < values.Count
                                ? line.Substring(values[c.Source].Start, values[c.Source].Length) : "")
                   .ToList();
    }

    /// <summary>Carrying a header to another place has to take its data with it. The two used to be the
    /// same thing - a column showed whichever field sat at its own index - so reordering relabelled the
    /// fields and left the text where it was.</summary>
    [Fact]
    public void Reordering_the_columns_moves_the_data_not_just_the_names()
    {
        var spec = new ColumnSpec { Mode = ColumnSplitMode.Template, Template = "[time] [level] [message]" };
        spec.SyncColumnsFromTemplate();
        const string line = "09:31 WARN disk is filling up";
        Assert.Equal(["09:31", "WARN", "disk is filling up"], Cells(spec, line));

        // Carry the message to the front.
        var message = spec.Columns[2];
        spec.Columns.RemoveAt(2);
        spec.Columns.Insert(0, message);

        Assert.Equal(["message", "time", "level"], spec.Columns.Select(c => c.Name));
        Assert.Equal(["disk is filling up", "09:31", "WARN"], Cells(spec, line));
    }

    [Fact]
    public void Reordering_delimited_columns_moves_the_data_too()
    {
        var spec = new ColumnSpec { Mode = ColumnSplitMode.Delimiter, Delimiter = "," };
        spec.Columns.AddRange([new ColumnDef { Name = "a" }, new ColumnDef { Name = "b" }, new ColumnDef { Name = "c" }]);
        spec.NormalizeSources();
        Assert.Equal(["1", "2", "3"], Cells(spec, "1,2,3"));

        var first = spec.Columns[0];
        spec.Columns.RemoveAt(0);
        spec.Columns.Add(first);
        Assert.Equal(["2", "3", "1"], Cells(spec, "1,2,3"));
    }

    [Fact]
    public void A_hidden_column_does_not_shift_the_ones_after_it()
    {
        var spec = new ColumnSpec { Mode = ColumnSplitMode.Template, Template = "[a] [b] [c]" };
        spec.SyncColumnsFromTemplate();
        spec.Columns[1].Visible = false;
        Assert.Equal(["one", "three"], Cells(spec, "one two three"));
    }

    /// <summary>A file written before columns had a source of their own has to keep showing what it did:
    /// each column showing the field at its own place in the list.</summary>
    [Fact]
    public void Columns_that_never_said_which_field_they_show_take_their_own_place()
    {
        var spec = new ColumnSpec { Mode = ColumnSplitMode.Delimiter, Delimiter = "," };
        spec.Columns.AddRange([new ColumnDef { Name = "a" }, new ColumnDef { Name = "b" }]);
        Assert.All(spec.Columns, c => Assert.Equal(-1, c.Source));
        spec.NormalizeSources();
        Assert.Equal([0, 1], spec.Columns.Select(c => c.Source));
        Assert.Equal(["1", "2"], Cells(spec, "1,2,3"));
    }

    /// <summary>A split is of the LINE, so it yields the line's own fields however many columns happen to
    /// be listed - a column indexes into that, and would otherwise be reading past the end of a list sized
    /// by something unrelated to the data.</summary>
    [Fact]
    public void A_split_yields_the_lines_own_fields_however_many_columns_are_listed()
    {
        var spec = new ColumnSpec { Mode = ColumnSplitMode.Template, Template = "[a] [b] [c] [d]" };
        spec.Columns.Add(new ColumnDef { Name = "last", Source = 3 });
        Assert.Equal(["1", "2", "3", "4"], Split(spec, "1 2 3 4"));
        Assert.Equal(["4"], Cells(spec, "1 2 3 4"));
    }

    /// <summary>Two columns can be renamed the same thing from the header - there is nothing to stop it -
    /// so rebuilding the list from the template must not choke on the pair.</summary>
    [Fact]
    public void Two_columns_with_the_same_name_do_not_break_a_refresh()
    {
        var spec = new ColumnSpec { Mode = ColumnSplitMode.Template, Template = "[a] [b] [c]" };
        spec.SyncColumnsFromTemplate();
        spec.Columns[0].Name = "same";
        spec.Columns[1].Name = "same";
        spec.Template = "[same] [c]";
        spec.SyncColumnsFromTemplate();
        Assert.Equal(["same", "c"], spec.Columns.Select(c => c.Name));
        Assert.Equal([0, 1], spec.Columns.Select(c => c.Source));
    }

    [Fact]
    public void Delimiter_tab()
    {
        var spec = new ColumnSpec { Mode = ColumnSplitMode.Delimiter, Delimiter = "\t" };
        Assert.Equal(new[] { "a", "b", "c" }, Split(spec, "a\tb\tc"));
    }

    [Fact]
    public void Delimiter_collapse_consecutive_whitespace()
    {
        var spec = new ColumnSpec { Mode = ColumnSplitMode.Delimiter, Delimiter = " ", CollapseConsecutive = true };
        Assert.Equal(new[] { "a", "b", "c" }, Split(spec, "a  b   c"));
    }

    [Fact]
    public void Delimiter_max_splits_keeps_remainder()
    {
        var spec = new ColumnSpec { Mode = ColumnSplitMode.Delimiter, Delimiter = " ", MaxSplits = 2 };
        Assert.Equal(new[] { "a", "b c d" }, Split(spec, "a b c d"));
    }

    [Fact]
    public void Template_named_columns()
    {
        var spec = new ColumnSpec { Mode = ColumnSplitMode.Template, Template = "[time] [level] [message]" };
        spec.SyncColumnsFromTemplate();
        Assert.Equal(new[] { "2026", "INFO", "hello world" }, Split(spec, "2026 INFO hello world"));
    }

    [Fact]
    public void Template_literal_brackets()
    {
        var spec = new ColumnSpec { Mode = ColumnSplitMode.Template, Template = "[[level]] [msg]" };
        spec.SyncColumnsFromTemplate();
        Assert.Equal(new[] { "INFO", "hello" }, Split(spec, "[INFO] hello"));
    }

    [Fact]
    public void Template_bracket_delimited_line()
    {
        var spec = new ColumnSpec
        {
            Mode = ColumnSplitMode.Template,
            Template = "[[time]][[provider]][[cpu]][[pid]][[tid]][[source]][[func]][[level]][[flags]] [message]"
        };
        spec.SyncColumnsFromTemplate();
        const string line = "[2026-07-31T09:31:17.8710000][api-gateway][3][2FA8][315C][http_c5024][HandleCheckout][INFO][TRACE_FLAG_HTTP] req-e38a626f message here";
        var fields = Split(spec, line);
        Assert.Equal("2026-07-31T09:31:17.8710000", fields[0]);
        Assert.Equal("api-gateway", fields[1]);
        Assert.Equal("3", fields[2]);
        Assert.Equal("INFO", fields[7]);
        Assert.Equal("TRACE_FLAG_HTTP", fields[8]);
        Assert.Equal("req-e38a626f message here", fields[9]);
    }

    [Fact]
    public void Template_empty_field_allowed()
    {
        var spec = new ColumnSpec { Mode = ColumnSplitMode.Template, Template = "[[a]][[b]] [c]" };
        spec.SyncColumnsFromTemplate();
        Assert.Equal(new[] { "x", "", "rest" }, Split(spec, "[x][] rest"));
    }

    [Fact]
    public void Template_non_match_falls_back_to_single_cell()
    {
        var spec = new ColumnSpec { Mode = ColumnSplitMode.Template, Template = "[a]-[b]" };
        spec.SyncColumnsFromTemplate();
        var splitter = new ColumnSplitter(spec);
        var values = new List<ColumnValue>();
        bool ok = splitter.Split("no separator here", values);
        Assert.False(ok);
        Assert.Single(values);
        Assert.Equal("no separator here", "no separator here".Substring(values[0].Start, values[0].Length));
    }
}

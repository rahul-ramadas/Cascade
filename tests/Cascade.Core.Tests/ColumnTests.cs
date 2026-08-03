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

using System.Text;
using Cascade.Core.Columns;
using Cascade.Core.Model;
using Cascade.Core.Persistence;

namespace Cascade.Core.Tests;

public class PersistenceTests
{
    private const string SampleTat = """
        <?xml version="1.0" encoding="utf-8" standalone="yes"?>
        <TextAnalysisTool.NET version="2025-11-21" showOnlyFilteredLines="True">
          <filters>
            <filter enabled="y" excluding="n" description="errors" foreColor="ff0000" type="matches_text" case_sensitive="n" regex="n" text="[ERROR]" />
            <filter enabled="n" excluding="y" description="" type="matches_text" case_sensitive="y" regex="y" text="\[OrderService\].+Svc::" />
            <filter enabled="n" excluding="n" description="" foreColor="ffff00" backColor="000000" type="matches_text" case_sensitive="n" regex="n" text="&quot;TraceType&quot;:&quot;Warning&quot;" />
          </filters>
        </TextAnalysisTool.NET>
        """;

    [Fact]
    public void Tat_import_maps_attributes()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(SampleTat));
        var c = TatImporter.Import(stream);

        Assert.True(c.ShowOnlyFilteredLines);
        Assert.Equal(3, c.Roots.Count);

        var f0 = c.Roots[0];
        Assert.True(f0.Enabled);
        Assert.Equal(FilterKind.Include, f0.Kind);
        Assert.Equal("errors", f0.Description);
        Assert.Equal("[ERROR]", f0.Match.Text);
        Assert.Equal(new RgbColor(0xff, 0x00, 0x00), f0.Style.Foreground);
        Assert.Null(f0.Style.Background); // absent → unset

        var f1 = c.Roots[1];
        Assert.False(f1.Enabled);
        Assert.Equal(FilterKind.Exclude, f1.Kind);
        Assert.True(f1.Match.Regex);
        Assert.True(f1.Match.CaseSensitive);
        Assert.Equal(@"\[OrderService\].+Svc::", f1.Match.Text);

        var f2 = c.Roots[2];
        Assert.Equal("\"TraceType\":\"Warning\"", f2.Match.Text); // XML entities unescaped
        Assert.Equal(new RgbColor(0xff, 0xff, 0x00), f2.Style.Foreground);
        Assert.Equal(new RgbColor(0x00, 0x00, 0x00), f2.Style.Background);
    }

    [Fact]
    public void Cascade_round_trip_preserves_hierarchy_styles_and_columns()
    {
        var filters = new FilterCollection { ShowOnlyFilteredLines = true };
        var parent = new Filter { Enabled = true, Description = "Error", Match = { Text = "Error" }, Style = { Foreground = new RgbColor(0, 0, 0), Background = new RgbColor(255, 0, 0) } };
        var child = new Filter { Enabled = false, Kind = FilterKind.Exclude, Match = { Text = "retry" }, Style = { Foreground = new RgbColor(1, 2, 3) } };
        filters.Add(parent);
        filters.Add(child, parent);

        var cols = new ColumnSpec { Enabled = true, Mode = ColumnSplitMode.Template, Template = "[a] [b]" };
        cols.SyncColumnsFromTemplate();
        cols.Columns[1].Visible = false;

        string path = Path.Combine(Path.GetTempPath(), "cascade_" + Guid.NewGuid().ToString("N") + ".cascade");
        try
        {
            CascadeFile.Save(path, filters, cols);
            var (loaded, loadedCols) = CascadeFile.Load(path);

            Assert.True(loaded.ShowOnlyFilteredLines);
            Assert.Single(loaded.Roots);
            var p = loaded.Roots[0];
            Assert.Equal("Error", p.Description);
            Assert.Equal(new RgbColor(255, 0, 0), p.Style.Background);
            Assert.Single(p.Children);

            var ch = p.Children[0];
            Assert.Same(p, ch.Parent);
            Assert.Equal(FilterKind.Exclude, ch.Kind);
            Assert.Equal(new RgbColor(1, 2, 3), ch.Style.Foreground);
            Assert.Null(ch.Style.Background);

            Assert.NotNull(loadedCols);
            Assert.Equal(ColumnSplitMode.Template, loadedCols!.Mode);
            Assert.Equal(2, loadedCols.Columns.Count);
            Assert.False(loadedCols.Columns[1].Visible);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Real_orders_tat_imports_if_present()
    {
        const string path = @"E:\Scripts\Orders.tat";
        if (!File.Exists(path)) return; // environment-dependent fixture

        var c = TatImporter.Import(path);
        Assert.Equal(175, c.Roots.Count);
        Assert.All(c.Roots, f => Assert.Equal(FilterMatchType.Text, f.Match.Type));
        Assert.Contains(c.Roots, f => f.Match.Regex);
        Assert.Contains(c.Roots, f => f.Kind == FilterKind.Exclude);
    }
}

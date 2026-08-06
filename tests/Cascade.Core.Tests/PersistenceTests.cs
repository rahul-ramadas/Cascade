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
            <filter enabled="n" excluding="y" description="" type="matches_text" case_sensitive="y" regex="y" text="\[payment-svc\].+declined" />
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
        Assert.Equal(@"\[payment-svc\].+declined", f1.Match.Text);

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

    /// <summary>Every attribute a filter's style can carry has to survive a save and a load. Driven by
    /// reflection over <see cref="FilterStyle"/> rather than by a list, so an attribute added later cannot
    /// be quietly left out of the file - which is exactly how underline could have gone missing.</summary>
    [Fact]
    public void Cascade_round_trip_preserves_every_style_attribute()
    {
        var props = typeof(FilterStyle).GetProperties()
            .Where(p => p.CanRead && p.CanWrite).ToArray();
        Assert.True(props.Length >= 5, $"only {props.Length} style attributes found");

        var f = new Filter { Enabled = true, Match = { Text = "styled" } };
        f.Style.Foreground = new RgbColor(9, 8, 7);
        f.Style.Background = new RgbColor(6, 5, 4);
        foreach (var p in props.Where(p => p.PropertyType == typeof(bool?)))
            p.SetValue(f.Style, true);
        // ...and one turned deliberately OFF, since "off" and "inherit" are different answers.
        f.Style.Italic = false;

        var filters = new FilterCollection();
        filters.Add(f);

        string path = Path.Combine(Path.GetTempPath(), "cascade_" + Guid.NewGuid().ToString("N") + ".cascade");
        try
        {
            CascadeFile.Save(path, filters, new ColumnSpec());
            var loaded = CascadeFile.Load(path).Filters.Roots[0].Style;
            foreach (var p in props)
                Assert.Equal(p.GetValue(f.Style), p.GetValue(loaded));
            Assert.True(loaded.Underline);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Everything the header can be used to change has to come back, or the work of laying the
    /// columns out is lost the moment the file is closed. The filter file is where all of it lives.</summary>
    [Fact]
    public void Cascade_round_trip_preserves_how_the_columns_were_laid_out()
    {
        var cols = new ColumnSpec { Enabled = true, Mode = ColumnSplitMode.Template, Template = "[a] [b] [c]" };
        cols.SyncColumnsFromTemplate();
        cols.Columns[0].Name = "When";            // renamed in the header
        cols.Columns[0].WidthChars = 24;          // dragged, with a fixed-pitch font
        cols.Columns[0].Width = 192;
        cols.Columns[1].Align = ColumnAlign.Right;
        cols.Columns[1].Width = 130;              // dragged, with a proportional font
        cols.Columns[2].Visible = false;          // hidden from the header's menu
        var order = new[] { cols.Columns[2], cols.Columns[0], cols.Columns[1] };   // and carried about
        cols.Columns.Clear();
        cols.Columns.AddRange(order);

        string path = Path.Combine(Path.GetTempPath(), "cascade_" + Guid.NewGuid().ToString("N") + ".cascade");
        try
        {
            CascadeFile.Save(path, new FilterCollection(), cols);
            var loaded = CascadeFile.Load(path).Columns;

            Assert.NotNull(loaded);
            Assert.Equal(["c", "When", "b"], loaded!.Columns.Select(c => c.Name));
            Assert.Equal([2, 0, 1], loaded.Columns.Select(c => c.Source));   // each still shows its own field
            Assert.Equal(24, loaded.Columns[1].WidthChars);
            Assert.Equal(192, loaded.Columns[1].Width);
            Assert.Equal(0, loaded.Columns[2].WidthChars);
            Assert.Equal(130, loaded.Columns[2].Width);
            Assert.Equal(ColumnAlign.Right, loaded.Columns[2].Align);
            Assert.False(loaded.Columns[0].Visible);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A file written before a column could be carried away from its own field has to keep
    /// meaning what it did: each column showing the field at its own place in the list.</summary>
    [Fact]
    public void A_file_that_predates_column_sources_still_shows_the_right_fields()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "filters": [],
          "columns": {
            "enabled": true,
            "mode": "Delimiter",
            "delimiter": ",",
            "columns": [ { "name": "a", "visible": true }, { "name": "b", "visible": true } ]
          }
        }
        """;
        string path = Path.Combine(Path.GetTempPath(), "cascade_" + Guid.NewGuid().ToString("N") + ".cascade");
        try
        {
            File.WriteAllText(path, json);
            var loaded = CascadeFile.Load(path).Columns;
            Assert.NotNull(loaded);
            Assert.Equal([0, 1], loaded!.Columns.Select(c => c.Source));
        }
        finally { File.Delete(path); }
    }

    /// <summary>A copy has to carry the width in characters too - the dialog edits a clone and hands it
    /// back, so anything the clone drops is silently lost every time the settings are opened.</summary>
    [Fact]
    public void Cloning_a_spec_keeps_every_column_property()
    {
        var spec = new ColumnSpec { Enabled = true, Delimiter = "|" };
        spec.Columns.Add(new ColumnDef { Name = "n", Visible = false, Width = 77, WidthChars = 9, Align = ColumnAlign.Center });
        var clone = spec.Clone().Columns[0];
        Assert.Equal("n", clone.Name);
        Assert.False(clone.Visible);
        Assert.Equal(77, clone.Width);
        Assert.Equal(9, clone.WidthChars);
        Assert.Equal(ColumnAlign.Center, clone.Align);
    }
}

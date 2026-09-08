using System.Text;
using Cascade.Core.Model;
using Cascade.Core.Persistence;

namespace Cascade.Core.Tests;

/// <summary>
/// Reading the original tool's .tat filter files.
///
/// <para>This is the one place Cascade reads a file some other program wrote, and the format has a decade
/// of variation in it: attributes that came and went, two spellings of the marker index, four spellings of
/// yes. So the interesting cases are all the awkward ones - an attribute missing, an attribute the format
/// no longer uses, a value out of range - and what matters is that every one of them lands somewhere
/// sensible rather than throwing at a reader who only wanted their filters back.</para>
/// </summary>
public class TatImportTests
{
    private static FilterCollection Import(string xml)
        => TatImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    private static FilterCollection ImportFilters(string filters)
        => Import($"""
            <TextAnalysisTool.NET version="2025-11-21" showOnlyFilteredLines="False">
              <filters>
            {filters}
              </filters>
            </TextAnalysisTool.NET>
            """);

    [Theory]
    [InlineData("y", true)]
    [InlineData("Y", true)]
    [InlineData("yes", true)]
    [InlineData("YES", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    [InlineData("n", false)]
    [InlineData("no", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("perhaps", false)]
    public void Every_spelling_of_yes_that_the_format_has_ever_used_is_understood(string written, bool expected)
    {
        var filters = ImportFilters($"""    <filter enabled="{written}" excluding="{written}" type="matches_text" text="x" />""");

        Assert.Equal(expected, filters.Roots[0].Enabled);
        Assert.Equal(expected ? FilterKind.Exclude : FilterKind.Include, filters.Roots[0].Kind);
    }

    [Fact]
    public void A_filter_with_almost_no_attributes_still_reads()
    {
        // The failure this is for is an import that throws on the one file somebody actually has.
        var filters = ImportFilters("""    <filter />""");

        var f = filters.Roots[0];
        Assert.Equal("", f.Description);
        Assert.False(f.Enabled);
        Assert.Equal(FilterKind.Include, f.Kind);
        Assert.Equal(FilterMatchType.Text, f.Match.Type);
        Assert.Equal("", f.Match.Text);
        Assert.Null(f.Style.Foreground);
        Assert.Null(f.Style.Background);
    }

    [Fact]
    public void The_pre_2014_colour_attribute_is_read_as_the_foreground()
    {
        var filters = ImportFilters("""    <filter type="matches_text" text="x" color="FF0000" />""");

        Assert.Equal(new RgbColor(255, 0, 0), filters.Roots[0].Style.Foreground);
        Assert.Null(filters.Roots[0].Style.Background);
    }

    [Fact]
    public void The_modern_colour_attribute_wins_over_the_legacy_one()
    {
        // A file written by something that emitted both. foreColor is the one the format settled on.
        var filters = ImportFilters("""    <filter type="matches_text" text="x" color="FF0000" foreColor="00FF00" />""");

        Assert.Equal(new RgbColor(0, 255, 0), filters.Roots[0].Style.Foreground);
    }

    [Theory]
    [InlineData("""<filter type="marker" marker="3" />""", 3)]
    [InlineData("""<filter type="marker" markerIndex="5" />""", 5)]      // the other spelling
    [InlineData("""<filter type="marked_by_marker_2" />""", 2)]          // the index in the type name
    [InlineData("""<filter type="marker" />""", 0)]                      // nothing to go on
    [InlineData("""<filter type="marker" marker="99" />""", 7)]          // clamped to the eight there are
    [InlineData("""<filter type="marker" marker="-4" />""", 0)]
    [InlineData("""<filter type="marker" marker="not a number" />""", 0)]
    public void A_marker_filter_finds_its_index_however_the_file_spells_it(string element, int expected)
    {
        var f = ImportFilters("    " + element).Roots[0];

        Assert.Equal(FilterMatchType.Marker, f.Match.Type);
        Assert.Equal(expected, f.Match.MarkerIndex);
    }

    [Fact]
    public void A_marker_filter_ignores_the_text_attributes_beside_it()
    {
        // The two kinds are exclusive: a marker filter that also carried a pattern would match on the
        // pattern too, and the file cannot mean that.
        var f = ImportFilters("""    <filter type="marker" marker="1" text="ERROR" regex="y" case_sensitive="y" />""").Roots[0];

        Assert.Equal(FilterMatchType.Marker, f.Match.Type);
        Assert.Equal("", f.Match.Text);
        Assert.False(f.Match.Regex);
        Assert.False(f.Match.CaseSensitive);
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("False", false)]
    [InlineData("true", true)]
    public void Whether_only_matching_lines_are_shown_comes_across(string written, bool expected)
    {
        var filters = Import($"""
            <TextAnalysisTool.NET showOnlyFilteredLines="{written}"><filters /></TextAnalysisTool.NET>
            """);

        Assert.Equal(expected, filters.ShowOnlyFilteredLines);
    }

    [Theory]
    [InlineData("""<TextAnalysisTool.NET showOnlyFilteredLines="perhaps"><filters /></TextAnalysisTool.NET>""")]
    [InlineData("""<TextAnalysisTool.NET><filters /></TextAnalysisTool.NET>""")]
    public void A_view_setting_that_cannot_be_read_leaves_the_default_alone(string xml)
        => Assert.False(Import(xml).ShowOnlyFilteredLines);

    [Fact]
    public void A_file_whose_root_is_the_filter_list_itself_reads_the_same()
    {
        // Some files in the wild have no wrapper element.
        var filters = Import("""<filters><filter type="matches_text" text="a" /><filter text="b" /></filters>""");

        Assert.Equal(2, filters.Roots.Count);
        Assert.Equal(["a", "b"], filters.Roots.Select(f => f.Match.Text));
    }

    [Theory]
    [InlineData("""<TextAnalysisTool.NET />""")]                        // no filter list at all
    [InlineData("""<TextAnalysisTool.NET><filters /></TextAnalysisTool.NET>""")]
    [InlineData("""<TextAnalysisTool.NET><filters><notafilter /></filters></TextAnalysisTool.NET>""")]
    public void A_file_with_nothing_to_import_gives_an_empty_list_rather_than_an_error(string xml)
        => Assert.Empty(Import(xml).Roots);

    [Fact]
    public void A_wrapper_element_by_any_other_name_is_still_looked_inside()
    {
        // Only the filter list is looked for, not the name of what holds it, so a file written by
        // something with its own idea of a root element still imports.
        var filters = Import("""<somethingelse><filters><filter text="a" /></filters></somethingelse>""");

        Assert.Equal("a", Assert.Single(filters.Roots).Match.Text);
    }

    [Fact]
    public void Filters_arrive_flat_and_in_the_order_the_file_wrote_them()
    {
        // The format has no hierarchy, so every filter is a root - and the order is the reader's own
        // priority order, which decides which one colours a line.
        var filters = ImportFilters(string.Join('\n',
            Enumerable.Range(0, 5).Select(i => $"""    <filter enabled="y" type="matches_text" text="t{i}" />""")));

        Assert.Equal(5, filters.Roots.Count);
        Assert.All(filters.Roots, f => Assert.Null(f.Parent));
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"t{i}"), filters.Roots.Select(f => f.Match.Text));
    }

    [Fact]
    public void Text_the_file_escaped_comes_back_as_it_was_written()
    {
        var f = ImportFilters("""    <filter type="matches_text" text="a &lt; b &amp;&amp; c &quot;d&quot;" />""").Roots[0];

        Assert.Equal("a < b && c \"d\"", f.Match.Text);
    }

    [Fact]
    public void A_file_that_is_not_xml_at_all_says_so()
    {
        // Loudly, so the caller can tell the reader which file was no good - the app puts up a message.
        // Returning an empty filter list instead would read as "your filters are gone".
        Assert.ThrowsAny<System.Xml.XmlException>(() => Import("this is not xml"));
    }

    [Fact]
    public void Importing_by_path_reads_the_same_file_the_stream_would()
    {
        string path = Path.Combine(Path.GetTempPath(), "cascade_tat_" + Guid.NewGuid().ToString("N") + ".tat");
        System.IO.File.WriteAllText(path, """
            <TextAnalysisTool.NET showOnlyFilteredLines="True">
              <filters><filter enabled="y" type="matches_text" text="ERROR" foreColor="FF0000" /></filters>
            </TextAnalysisTool.NET>
            """);
        try
        {
            var filters = TatImporter.Import(path);

            Assert.True(filters.ShowOnlyFilteredLines);
            Assert.Equal("ERROR", filters.Roots[0].Match.Text);
            Assert.Equal(new RgbColor(255, 0, 0), filters.Roots[0].Style.Foreground);
        }
        finally { System.IO.File.Delete(path); }
    }
}

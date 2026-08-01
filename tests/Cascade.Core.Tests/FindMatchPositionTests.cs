using Cascade.Core.Find;
using Xunit;

namespace Cascade.Core.Tests;

/// <summary>Walking the occurrences within a line, which is what the highlighting and the occurrence
/// counts are both built on. Checked against a plain scan, because "every hit on the line" is the sort of
/// loop that quietly stops one short or never stops at all.</summary>
public class FindMatchPositionTests
{
    private static (int At, int Length)[] All(string line, string term, bool regex = false, bool caseSensitive = false)
    {
        var matcher = FindEngine.CompileQuery(new FindQuery(term, regex, caseSensitive));
        if (matcher is null) return Array.Empty<(int, int)>();
        var found = new List<(int, int)>();
        int from = 0;
        while (matcher.NextMatch(line, from, out int at, out int len))
        {
            found.Add((at, len));
            from = at + Math.Max(1, len);
        }
        return found.ToArray();
    }

    [Fact]
    public void Every_literal_occurrence_is_reported_once()
    {
        Assert.Equal(new[] { (0, 3), (4, 3), (8, 3) }, All("abc abc abc", "abc"));
        Assert.Equal(new[] { (0, 2), (2, 2) }, All("aaaa", "aa"));      // no overlaps: they would double-count
        Assert.Empty(All("nothing here", "zzz"));
        Assert.Empty(All("anything", ""));
    }

    [Fact]
    public void Case_sensitivity_is_honoured()
    {
        Assert.Equal(new[] { (0, 3), (4, 3) }, All("ABC abc", "abc"));
        Assert.Equal(new[] { (4, 3) }, All("ABC abc", "abc", caseSensitive: true));
    }

    [Fact]
    public void Regular_expressions_report_what_they_matched()
    {
        Assert.Equal(new[] { (4, 3), (13, 2) }, All("GET 200 POST 40 x", @"\d+", regex: true));
        Assert.Equal(new[] { (0, 5) }, All("error: disk", "error", regex: true));
    }

    [Fact]
    public void A_pattern_that_can_match_nothing_still_terminates()
    {
        // An empty match would leave the scan standing still; it has to move on instead.
        var found = All("abc", "x*", regex: true);
        Assert.True(found.Length <= 4, $"{found.Length} matches for an empty-capable pattern");
        Assert.All(found, m => Assert.True(m.Length >= 1));
    }

    [Fact]
    public void Counting_occurrences_agrees_with_walking_them()
    {
        var matcher = FindEngine.CompileQuery(new FindQuery("ab", false, false))!;
        Assert.Equal(3, matcher.CountIn("ab ab xx ab"));
        Assert.Equal(0, matcher.CountIn("nothing"));
        Assert.Equal(2, matcher.CountIn("abab"));
    }

    [Fact]
    public void Starting_part_way_through_skips_what_is_behind_it()
    {
        var matcher = FindEngine.CompileQuery(new FindQuery("ab", false, false))!;
        Assert.True(matcher.NextMatch("ab ab", 1, out int at, out _));
        Assert.Equal(3, at);
        Assert.False(matcher.NextMatch("ab ab", 4, out _, out _));
        Assert.False(matcher.NextMatch("ab ab", 99, out _, out _));
    }
}

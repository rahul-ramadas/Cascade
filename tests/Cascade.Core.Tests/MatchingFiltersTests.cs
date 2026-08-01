using Cascade.Core.Filtering;
using Cascade.Core.Model;

namespace Cascade.Core.Tests;

/// <summary>
/// <see cref="FilterSnapshot.MatchingFilters"/> answers "why does this line look like this?", which is a
/// different question from "what does the view show". Evaluation prunes anything that cannot change the
/// view - whole subtrees with nothing switched on - and this must not, because a switched-off filter that
/// would have matched is exactly what the user is hovering to find out about.
/// </summary>
public class MatchingFiltersTests
{
    private static Filter Make(string text, bool enabled, FilterKind kind = FilterKind.Include)
        => new() { Enabled = enabled, Kind = kind, Match = { Type = FilterMatchType.Text, Text = text } };

    private static string[] Matching(FilterCollection c, string line)
    {
        var snapshot = FilterSnapshot.Build(c);
        var bits = new ulong[(snapshot.NodeCount + 63) / 64];
        snapshot.MatchingFilters(line.AsSpan(), 0, null, bits);
        var names = new List<string>();
        for (int i = 0; i < snapshot.NodeCount; i++)
            if ((bits[i >> 6] & (1UL << (i & 63))) != 0) names.Add(snapshot.FilterAt(i).Match.Text);
        return names.ToArray();
    }

    [Fact]
    public void Reports_every_matching_filter_including_switched_off_ones()
    {
        var c = new FilterCollection();
        c.Add(Make("Error", enabled: true));
        c.Add(Make("timeout", enabled: false));
        c.Add(Make("nothing here", enabled: true));

        Assert.Equal(new[] { "Error", "timeout" }, Matching(c, "Error: timeout talking to db"));
    }

    [Fact]
    public void Reports_a_whole_subtree_that_has_nothing_switched_on()
    {
        // Evaluation prunes this subtree outright (SubtreeHasEnabled is false), so a naive reuse of
        // Evaluate's deep-match bits would report nothing at all here.
        var c = new FilterCollection();
        var parent = Make("Error", enabled: false);
        c.Add(parent);
        c.Add(Make("timeout", enabled: false), parent);

        Assert.Equal(new[] { "Error", "timeout" }, Matching(c, "Error: timeout talking to db"));
    }

    [Fact]
    public void A_child_only_matches_when_its_ancestors_do()
    {
        var c = new FilterCollection();
        var parent = Make("Error", enabled: true);
        c.Add(parent);
        c.Add(Make("timeout", enabled: true), parent);

        // "timeout" occurs, but its parent's predicate does not - so it is not a deep match.
        Assert.Equal(Array.Empty<string>(), Matching(c, "Warn: timeout talking to db"));
        Assert.Equal(new[] { "Error", "timeout" }, Matching(c, "Error: timeout talking to db"));
    }

    [Fact]
    public void Excludes_are_reported_as_matches_too()
    {
        // The line is hidden BY the exclude, which is precisely what the user needs told.
        var c = new FilterCollection();
        c.Add(Make("Error", enabled: true));
        c.Add(Make("heartbeat", enabled: true, FilterKind.Exclude));

        var snapshot = FilterSnapshot.Build(c);
        Assert.False(snapshot.Evaluate("Error: heartbeat".AsSpan(), 0, null).Shown);
        Assert.Equal(new[] { "Error", "heartbeat" }, Matching(c, "Error: heartbeat"));
    }

    [Fact]
    public void Reports_them_in_document_order()
    {
        var c = new FilterCollection();
        var first = Make("a", enabled: true);
        c.Add(first);
        c.Add(Make("b", enabled: true), first);
        c.Add(Make("c", enabled: true));

        Assert.Equal(new[] { "a", "b", "c" }, Matching(c, "a b c"));
    }

    [Fact]
    public void Agrees_with_asking_each_filter_one_at_a_time()
    {
        // Brute-force reference: DeepMatches is the existing, independently used answer for one filter.
        var c = new FilterCollection();
        var http = Make("http", enabled: true);
        c.Add(http);
        c.Add(Make("500", enabled: false), http);
        c.Add(Make("GET", enabled: true), http);
        var db = Make("db", enabled: false);
        c.Add(db);
        c.Add(Make("timeout", enabled: false, FilterKind.Exclude), db);
        var re = Make("[0-9]+ms", enabled: true);
        re.Match.Regex = true;
        c.Add(re);

        var snapshot = FilterSnapshot.Build(c);
        string[] lines =
        [
            "http GET /orders 200 12ms",
            "http 500 /orders",
            "db timeout after 30s",
            "nothing interesting",
            "http GET db 500 timeout 900ms"
        ];

        foreach (string line in lines)
        {
            var expected = new List<string>();
            for (int i = 0; i < snapshot.NodeCount; i++)
            {
                var f = snapshot.FilterAt(i);
                if (snapshot.DeepMatches(line.AsSpan(), 0, null, f)) expected.Add(f.Match.Text);
            }
            Assert.Equal(expected.ToArray(), Matching(c, line));
        }
    }
}

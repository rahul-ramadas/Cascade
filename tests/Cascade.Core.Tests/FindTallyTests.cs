using Cascade.Core.Find;
using Xunit;

namespace Cascade.Core.Tests;

/// <summary>What the status bar says about a search: how many lines matched, how many of them the view is
/// showing, and how many occurrences that is. The split has to be re-derived rather than remembered - the
/// filters can change under a search that has already run.</summary>
public class FindTallyTests
{
    private static readonly FindQuery Q = new("x", false, false);

    /// <summary>A scanner with a fixed answer: which lines match, and how many times each does.</summary>
    private static FindSearch Search(long lines, long start, Func<long, int> occurrences)
    {
        var s = new FindSearch(Q, lines, start, (from, count, hits, ct) =>
        {
            for (long i = from; i < from + count; i++)
            {
                ct.ThrowIfCancellationRequested();
                int n = occurrences(i);
                if (n > 0) hits.Add(new FindHit(i, n));
            }
        });
        s.Start();
        s.WaitForCompletionForTesting();
        return s;
    }

    [Fact]
    public void Lines_and_occurrences_are_counted_separately()
    {
        // Every 10th line matches; every 20th matches three times.
        using var search = Search(1000, 0, l => l % 20 == 0 ? 3 : l % 10 == 0 ? 1 : 0);
        var tally = search.Count(null, -1);

        Assert.True(tally.Complete);
        Assert.Equal(100, tally.VisibleLines);                 // 100 matching lines
        Assert.Equal(0, tally.HiddenLines);
        Assert.Equal(50 * 3 + 50 * 1, tally.Occurrences);      // 50 of them match three times
        Assert.Equal(tally.Occurrences, tally.VisibleOccurrences);
        Assert.False(tally.Approximate);
    }

    [Fact]
    public void Hidden_matches_are_counted_apart_from_shown_ones()
    {
        using var search = Search(1000, 0, l => l % 10 == 0 ? 2 : 0);

        // Only the even hundreds are visible: 0, 200, 400, 600, 800.
        var tally = search.Count(l => l % 200 == 0, -1);
        Assert.Equal(5, tally.VisibleLines);
        Assert.Equal(95, tally.HiddenLines);
        Assert.Equal(10, tally.VisibleOccurrences);
        Assert.Equal(200, tally.Occurrences);
    }

    [Fact]
    public void The_split_follows_the_filters_rather_than_being_remembered()
    {
        using var search = Search(100, 0, l => l % 10 == 0 ? 1 : 0);

        Assert.Equal(10, search.Count(null, -1).VisibleLines);
        Assert.Equal(0, search.Count(_ => false, -1).VisibleLines);
        Assert.Equal(10, search.Count(_ => false, -1).HiddenLines);
        Assert.Equal(10, search.Count(null, -1).VisibleLines);   // and back again
    }

    [Fact]
    public void Position_counts_among_the_matches_you_can_actually_reach()
    {
        using var search = Search(100, 0, l => l % 10 == 0 ? 1 : 0);

        Assert.Equal(1, search.Count(null, 0).Position);
        Assert.Equal(4, search.Count(null, 30).Position);
        Assert.Equal(10, search.Count(null, 90).Position);
        Assert.Equal(0, search.Count(null, 35).Position);        // not on a match at all

        // With half of them hidden, position counts within what is left.
        Assert.Equal(2, search.Count(l => l % 20 == 0, 20).Position);
        Assert.Equal(0, search.Count(l => l % 20 == 0, 30).Position);   // that one is hidden
    }

    [Fact]
    public void An_unfinished_sweep_says_so()
    {
        using var gate = new SemaphoreSlim(0);
        var search = new FindSearch(Q, 100_000, 50_000, (from, count, hits, ct) =>
        {
            if (from < 40_000) gate.Wait(ct);   // the backward sweep stalls part way
            for (long i = from; i < from + count; i++) if (i % 100 == 0) hits.Add(new FindHit(i, 1));
        });
        search.Start();

        var tally = search.Count(null, -1);
        Assert.False(tally.Complete);

        gate.Release(100);
        search.WaitForCompletionForTesting();
        Assert.True(search.Count(null, -1).Complete);
        search.Dispose();
    }

    [Fact]
    public void An_empty_file_tallies_to_nothing()
    {
        using var search = Search(0, 0, _ => 1);
        var tally = search.Count(null, -1);
        Assert.Equal(0, tally.VisibleLines);
        Assert.Equal(0, tally.Occurrences);
        Assert.True(tally.Complete);
    }
}

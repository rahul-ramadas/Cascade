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

    /// <summary>Visibility as the counter wants it - 64 lines to a word - from a rule about one line.</summary>
    private static VisibleWordReader Shown(Func<long, bool> visible) => (fromWord, words) =>
    {
        for (int i = 0; i < words.Length; i++)
        {
            ulong bits = 0;
            for (int b = 0; b < 64; b++)
                if (visible(((fromWord + i) << 6) + b)) bits |= 1UL << b;
            words[i] = bits;
        }
    };

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
        var tally = search.Count(Shown(l => l % 200 == 0), -1);
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
        Assert.Equal(0, search.Count(Shown(_ => false), -1).VisibleLines);
        Assert.Equal(10, search.Count(Shown(_ => false), -1).HiddenLines);
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
        Assert.Equal(2, search.Count(Shown(l => l % 20 == 0), 20).Position);
        Assert.Equal(0, search.Count(Shown(l => l % 20 == 0), 30).Position);   // that one is hidden
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

    [Fact]
    public void Visibility_is_asked_about_a_word_at_a_time()
    {
        // The status bar asks for this on every caret move, so it must scale with the file rather than with
        // how much matched. Asking line by line cost 160 ms per keystroke on a term matching twenty million
        // lines - the counting itself was never the problem, the twenty million callbacks were.
        const long lines = 200_000;
        using var search = Search(lines, 0, l => l % 2 == 0 ? 2 : 0);   // 100,000 matches, all with extras

        long asked = 0;
        VisibleWordReader counting = (from, words) =>
        {
            asked += words.Length;
            for (int i = 0; i < words.Length; i++) words[i] = (from + i) % 2 == 0 ? ulong.MaxValue : 0;
        };

        var tally = search.Count(counting, -1);
        Assert.Equal(100_000, tally.VisibleLines + tally.HiddenLines);   // every match accounted for
        Assert.True(tally.VisibleLines > 40_000, $"{tally.VisibleLines} shown");

        // One pass for the split and at most one more to attribute the repeats, plus a chunk of slack.
        long words = (lines + 63) / 64;
        Assert.True(asked <= words * 2 + 512, $"asked for {asked:N0} words of a {words:N0}-word file");
    }

    [Fact]
    public void Counting_does_not_slow_down_as_the_matches_pile_up()
    {
        // The same guarantee where nothing is hidden, which is the ordinary case and has no callback to
        // count. Two million matches, each recorded as matching twice: walking them one at a time took tens
        // of milliseconds, which is what made every keypress feel heavy.
        using var search = Search(4_000_000, 0, l => l % 2 == 0 ? 2 : 0);
        Assert.Equal(2_000_000, search.Count(null, -1).VisibleLines);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20; i++) search.Count(null, 3_999_998 - i * 2);
        sw.Stop();

        double each = sw.Elapsed.TotalMilliseconds / 20;
        Assert.True(each < 5, $"a tally over two million matches took {each:F1} ms");
    }
}

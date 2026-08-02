using Cascade.Core.Find;
using Xunit;

namespace Cascade.Core.Tests;

/// <summary>Counting a search's hits inside a slice of the file. The match map asks this once per pixel row,
/// so it has to be exact at every word boundary and it has to cost one pass over the bitmap in total, not
/// one pass per band.</summary>
public class FindHitRangeTests
{
    private static readonly FindQuery Q = new("x", false, false);

    private static FindSearch Search(long lines, Func<long, bool> matches)
    {
        var s = new FindSearch(Q, lines, 0, (from, count, hits, ct) =>
        {
            for (long i = from; i < from + count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (matches(i)) hits.Add(new FindHit(i, 1));
            }
        });
        s.Start();
        s.WaitForCompletionForTesting();
        return s;
    }

    [Fact]
    public void Every_range_agrees_with_counting_the_lines_one_at_a_time()
    {
        const long lines = 5_000;
        // Deliberately irregular, so a range never lines up neatly with the 64-line words underneath.
        bool Matches(long l) => l % 37 == 0 || l % 64 == 63 || l is 0 or 1 or 4_999;
        using var search = Search(lines, Matches);

        var rng = new Random(7);
        for (int t = 0; t < 400; t++)
        {
            long a = rng.NextInt64(-5, lines + 5), b = rng.NextInt64(-5, lines + 5);
            (long from, long to) = a <= b ? (a, b) : (b, a);

            long brute = 0;
            for (long l = Math.Max(0, from); l < Math.Min(lines, to); l++) if (Matches(l)) brute++;

            Assert.Equal(brute, search.HitsInRange(from, to));
        }
    }

    [Fact]
    public void The_edges_of_a_word_are_counted_once_each()
    {
        using var search = Search(256, l => true);
        Assert.Equal(0, search.HitsInRange(64, 64));
        Assert.Equal(1, search.HitsInRange(63, 64));
        Assert.Equal(2, search.HitsInRange(63, 65));
        Assert.Equal(64, search.HitsInRange(0, 64));
        Assert.Equal(65, search.HitsInRange(0, 65));
        Assert.Equal(256, search.HitsInRange(0, 1_000));   // past the end, not off it
        Assert.Equal(0, search.HitsInRange(300, 400));
        Assert.Equal(0, search.HitsInRange(100, 50));      // backwards is empty, not negative
    }

    [Fact]
    public void A_band_costs_the_same_wherever_in_the_file_it_is()
    {
        // Counting a range as "rank up to the end minus rank up to the start" reads the whole bitmap from
        // the beginning every time, so a band at the end of the file costs a thousand times a band at the
        // start - and summarising the file band by band becomes quadratic. Timed as a ratio rather than
        // against a clock, so the check means the same thing on any machine.
        const long lines = 8_000_000;
        using var search = Search(lines, l => l % 3 == 0);
        const long band = lines / 1000;

        long Time(long from)
        {
            var w = System.Diagnostics.Stopwatch.StartNew();
            long sink = 0;
            for (int i = 0; i < 2_000; i++) sink += search.HitsInRange(from, from + band);
            w.Stop();
            Assert.True(sink > 0);
            return Math.Max(1, w.ElapsedTicks);
        }

        // The other thing running on the machine can only ever make a measurement slower, so the fastest of
        // several runs is the honest cost and a scheduler hiccup cannot fail the check. The two are
        // measured turn about, so a slow patch of the machine lands on both alike.
        Time(0);                                   // warm up the JIT before either measurement counts
        long atStart = long.MaxValue, atEnd = long.MaxValue;
        for (int rep = 0; rep < 5; rep++)
        {
            atStart = Math.Min(atStart, Time(0));
            atEnd = Math.Min(atEnd, Time(lines - band));
        }

        Assert.True(atEnd < atStart * 10, $"a band at the end cost {(double)atEnd / atStart:0.0}x one at the start");
    }
}

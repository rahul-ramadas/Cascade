using Cascade.Core.Filtering;
using Xunit;

namespace Cascade.Core.Tests;

/// <summary>The match map summarises the whole file one pixel at a time, so its counting primitives are
/// asked about millions of lines per repaint. They are checked here against a brute-force reference at both
/// storage densities, because an off-by-one in a rank lookup is invisible in a 20-pixel-wide picture.</summary>
public class RangeCountTests
{
    private static bool[] Pattern(int lines, int seed, double density)
    {
        var rng = new Random(seed);
        var bits = new bool[lines];
        for (int i = 0; i < lines; i++) bits[i] = rng.NextDouble() < density;
        return bits;
    }

    private static long BruteForce(bool[] bits, long from, long to)
    {
        long n = 0;
        for (long i = Math.Max(0, from); i < Math.Min(bits.Length, to); i++) if (bits[i]) n++;
        return n;
    }

    [Theory]
    [InlineData(0.02)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(0.0)]
    public void Counting_visible_lines_in_a_range_matches_a_brute_force_walk(double density)
    {
        const int lines = 30_000;
        var bits = Pattern(lines, 17, density);
        var set = new VisibleLineSet();
        set.EnsureLines(lines);
        set.ApplyRange(0, bits);
        set.Publish();

        var rng = new Random(99);
        for (int t = 0; t < 400; t++)
        {
            long a = rng.Next(-50, lines + 50);
            long b = a + rng.Next(0, 5000);
            Assert.Equal(BruteForce(bits, a, b), set.CountInRange(a, b));
        }

        // The band boundaries a map actually asks about: whole-set, empty, and exact block multiples.
        Assert.Equal(BruteForce(bits, 0, lines), set.CountInRange(0, lines));
        Assert.Equal(0, set.CountInRange(500, 500));
        Assert.Equal(0, set.CountInRange(900, 100));
        Assert.Equal(BruteForce(bits, 4096, 8192), set.CountInRange(4096, 8192));
        Assert.Equal(BruteForce(bits, 0, lines), set.CountInRange(-1000, lines + 1000));
    }

    [Theory]
    [InlineData(0.001)]   // sparse storage: a sorted list of line numbers
    [InlineData(0.9)]     // dense storage: one bit per line
    public void Counting_a_filters_matches_in_a_range_matches_a_brute_force_walk(double density)
    {
        const int lines = 20_000;
        var bits = Pattern(lines, 23, density);
        var builder = new FilterMatchCache.SetBuilder(lines);
        for (long w = 0; w * 64 < lines; w++)
        {
            ulong word = 0;
            for (int b = 0; b < 64; b++)
            {
                long line = w * 64 + b;
                if (line < lines && bits[line]) word |= 1UL << b;
            }
            if (word != 0) builder.AddWord(w, word);
        }
        var set = builder.Build(lines);

        var rng = new Random(7);
        for (int t = 0; t < 400; t++)
        {
            long a = rng.Next(-50, lines + 50);
            long b = a + rng.Next(0, 4000);
            Assert.Equal(BruteForce(bits, a, b), set.CountInRange(a, b));
        }

        Assert.Equal(set.Matches, set.CountInRange(0, lines));
        Assert.Equal(0, set.CountInRange(1000, 1000));
        Assert.Equal(0, set.CountInRange(lines + 5, lines + 500));
        // Single-word and word-boundary ranges, where the head/tail masks meet.
        Assert.Equal(BruteForce(bits, 64, 128), set.CountInRange(64, 128));
        Assert.Equal(BruteForce(bits, 63, 65), set.CountInRange(63, 65));
        Assert.Equal(BruteForce(bits, 100, 101), set.CountInRange(100, 101));
    }
}

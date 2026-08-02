using Cascade.Core.Filtering;

namespace Cascade.Core.Tests;

/// <summary>
/// Golden tests for the bit-per-line visible set that backs the filtered view: rank/select must agree with a
/// brute-force reference (including across word, block and page boundaries), a re-evaluation must update it
/// in place (keep / drop / add), a half-finished pass must leave a valid mix of new and previous results, and
/// readers must stay safe and in-range while the filter worker mutates bits underneath them.
/// </summary>
public class VisibleLineSetTests
{
    private const int ApplyBlock = 4321; // deliberately not a multiple of the 64-line word or 4096-line block

    /// <summary>Builds a set from a visibility pattern, applied in blocks like the filter service does.</summary>
    private static VisibleLineSet Build(bool[] visible, int block = ApplyBlock)
    {
        var set = new VisibleLineSet();
        Apply(set, visible, block);
        return set;
    }

    private static void Apply(VisibleLineSet set, bool[] visible, int block = ApplyBlock)
    {
        for (int start = 0; start < visible.Length; start += block)
        {
            int len = Math.Min(block, visible.Length - start);
            set.ApplyRange(start, visible.AsSpan(start, len));
            set.Publish();
        }
    }

    /// <summary>Checks every reader against a brute-force walk of the pattern.</summary>
    private static void AssertMatchesReference(VisibleLineSet set, bool[] visible)
    {
        var lines = new List<long>();
        for (long i = 0; i < visible.Length; i++) if (visible[i]) lines.Add(i);

        Assert.Equal(lines.Count, set.Count);
        Assert.Equal(visible.Length, set.KnownLines);

        for (int row = 0; row < lines.Count; row++)
            Assert.Equal(lines[row], set.LineAt(row));

        long rank = 0;
        for (long line = 0; line < visible.Length; line++)
        {
            Assert.Equal(rank, set.RowAtOrAfterLine(line));
            Assert.Equal(visible[line] ? rank : -1, set.RowForLine(line));
            if (visible[line]) rank++;
        }

        Assert.Equal(lines.Count, set.RowAtOrAfterLine(visible.Length));
        Assert.Equal(-1, set.RowForLine(visible.Length));
        Assert.Equal(-1, set.RowForLine(-1));
    }

    [Theory]
    [InlineData(1234, 0.50)]
    [InlineData(9999, 0.02)] // sparse
    [InlineData(4321, 0.98)] // dense
    [InlineData(7, 0.25)]
    public void Rank_and_select_agree_with_a_brute_force_reference(int seed, double density)
    {
        var rnd = new Random(seed);
        var visible = new bool[20_000]; // spans several 4,096-line rank blocks
        for (int i = 0; i < visible.Length; i++) visible[i] = rnd.NextDouble() < density;
        AssertMatchesReference(Build(visible), visible);
    }

    [Fact]
    public void Handles_empty_all_hidden_and_all_visible_sets()
    {
        var empty = new VisibleLineSet();
        Assert.Equal(0, empty.Count);
        Assert.Equal(0, empty.KnownLines);
        Assert.Equal(0, empty.LineAt(0));       // clamped rather than throwing
        Assert.Equal(-1, empty.RowForLine(0));
        Assert.Equal(0, empty.RowAtOrAfterLine(5));

        var hidden = new bool[5_000];
        AssertMatchesReference(Build(hidden), hidden);

        var all = new bool[5_000];
        Array.Fill(all, true);
        AssertMatchesReference(Build(all), all);
    }

    [Fact]
    public void Exact_block_and_word_multiples_are_consistent()
    {
        foreach (int n in new[] { 64, 4096, 8192, 4096 * 3 })
        {
            var visible = new bool[n];
            for (int i = 0; i < n; i++) visible[i] = i % 3 == 0;
            AssertMatchesReference(Build(visible, block: 64), visible);
        }
    }

    [Fact]
    public void Spans_a_page_boundary()
    {
        // More than 4,194,304 lines, so the bitmap needs a second page, with matches on both sides.
        const int n = 4_200_000;
        const int pageBoundary = 4_194_304;
        var visible = new bool[n];
        for (int i = 0; i < n; i++) visible[i] = i % 7 == 0;
        var set = Build(visible, block: 32_768);

        long expected = 0;
        for (int i = 0; i < n; i++) if (visible[i]) expected++;
        Assert.Equal(expected, set.Count);
        Assert.Equal(n, set.KnownLines);

        for (long line = pageBoundary - 100; line < pageBoundary + 100; line++)
        {
            long row = set.RowForLine(line);
            if (line % 7 == 0)
            {
                Assert.True(row >= 0, $"line {line} should be visible");
                Assert.Equal(line, set.LineAt(row));
            }
            else Assert.Equal(-1, row);
        }

        Assert.Equal(0, set.LineAt(0));
        Assert.Equal((n - 1) / 7 * 7, set.LineAt(set.Count - 1));
    }

    [Fact]
    public void Updates_in_place_keeping_dropping_and_adding_lines()
    {
        // First pass: multiples of 3 are visible.
        var first = new bool[10_000];
        for (int i = 0; i < first.Length; i++) first[i] = i % 3 == 0;
        var set = Build(first);
        long firstCount = set.Count;

        // Second pass: multiples of 2. Multiples of 6 are KEPT, other multiples of 3 are DROPPED, and the
        // remaining even lines are ADDED - all without rebuilding the set.
        var second = new bool[first.Length];
        for (int i = 0; i < second.Length; i++) second[i] = i % 2 == 0;
        Apply(set, second);

        Assert.NotEqual(firstCount, set.Count);
        AssertMatchesReference(set, second);
    }

    [Fact]
    public void A_half_finished_pass_leaves_a_valid_mix_of_new_and_previous_results()
    {
        // This is what keeps the view stable: mid-sweep the set is new results before the frontier and the
        // previous pass's results after it - complete and coherent, never empty.
        var first = new bool[10_000];
        for (int i = 0; i < first.Length; i++) first[i] = i % 3 == 0;
        var set = Build(first);

        var second = new bool[first.Length];
        for (int i = 0; i < second.Length; i++) second[i] = i % 2 == 0;

        const int frontier = 4_000;
        set.ApplyRange(0, second.AsSpan(0, frontier));
        set.Publish();

        var mixed = new bool[first.Length];
        Array.Copy(second, mixed, frontier);
        Array.Copy(first, frontier, mixed, frontier, first.Length - frontier);
        AssertMatchesReference(set, mixed);
    }

    [Fact]
    public void FillVisible_seeds_every_line_then_a_pass_narrows_it()
    {
        var set = new VisibleLineSet();
        set.FillVisible(9_000);
        set.Publish();

        Assert.Equal(9_000, set.Count);
        Assert.Equal(9_000, set.KnownLines);
        Assert.Equal(4_500, set.LineAt(4_500));
        Assert.Equal(123, set.RowForLine(123));

        var keep = new bool[9_000];
        for (int i = 0; i < keep.Length; i++) keep[i] = i % 100 == 0;
        Apply(set, keep);
        AssertMatchesReference(set, keep);
    }

    [Fact]
    public void FillVisible_clears_lines_left_over_from_a_previous_pass()
    {
        var wide = new bool[8_000];
        Array.Fill(wide, true);
        var set = Build(wide);
        Assert.Equal(8_000, set.Count);

        // Seeding a shorter file must hide everything past its end.
        set.FillVisible(3_000);
        set.Publish();
        Assert.Equal(3_000, set.Count);
        Assert.Equal(-1, set.RowForLine(5_000));
        Assert.Equal(3_000, set.RowAtOrAfterLine(5_000));
    }

    [Fact]
    public void Grows_as_indexing_adds_lines()
    {
        var set = new VisibleLineSet();
        var all = new bool[30_000];
        for (int i = 0; i < all.Length; i++) all[i] = i % 5 == 0;

        // Apply in ascending chunks, checking the set stays consistent for the part covered so far.
        for (int covered = 5_000; covered <= all.Length; covered += 5_000)
        {
            set.ApplyRange(covered - 5_000, all.AsSpan(covered - 5_000, 5_000));
            set.Publish();
            Assert.Equal(covered, set.KnownLines);
            long expected = 0;
            for (int i = 0; i < covered; i++) if (all[i]) expected++;
            Assert.Equal(expected, set.Count);
        }
        AssertMatchesReference(set, all);
    }

    [Fact]
    public void ResolveWindow_places_the_anchor_line_at_the_requested_offset()
    {
        var visible = new bool[20_000];
        for (int i = 0; i < visible.Length; i++) visible[i] = i % 3 == 0;
        var set = Build(visible);

        const long anchorLine = 9_000; // divisible by 3, so visible
        const int offset = 20;
        var lines = new long[43];
        long first = set.ResolveWindow(anchorLine, offset, lines, out int count);

        Assert.Equal(43, count);
        Assert.Equal(anchorLine, lines[offset]);
        Assert.Equal(set.RowForLine(anchorLine) - offset, first);
        for (int i = 0; i < count; i++) Assert.Equal(set.LineAt(first + i), lines[i]);
    }

    [Fact]
    public void ResolveWindow_uses_the_next_visible_line_when_the_anchor_was_dropped()
    {
        var visible = new bool[10_000];
        for (int i = 0; i < visible.Length; i++) visible[i] = i % 3 == 0;
        var set = Build(visible);

        const long hidden = 5_000; // 5000 % 3 == 2, so not visible
        Assert.Equal(-1, set.RowForLine(hidden));

        var lines = new long[20];
        long first = set.ResolveWindow(hidden, 5, lines, out int count);
        Assert.Equal(20, count);
        Assert.Equal(set.RowAtOrAfterLine(hidden) - 5, first);
        Assert.Equal(5_001, lines[5]); // nearest visible line at or after the dropped anchor
    }

    [Fact]
    public void ResolveWindow_clamps_at_the_start_and_end_of_the_set()
    {
        var visible = new bool[1_000];
        for (int i = 0; i < visible.Length; i++) visible[i] = i % 10 == 0; // 100 visible rows
        var set = Build(visible);
        var lines = new long[43];

        long first = set.ResolveWindow(0, 20, lines, out int count); // would start before row 0
        Assert.Equal(0, first);
        Assert.Equal(43, count);
        Assert.Equal(0, lines[0]);

        first = set.ResolveWindow(990, 0, lines, out count); // would run past the end
        Assert.Equal(100 - 43, first);
        Assert.Equal(43, count);
        Assert.Equal(set.LineAt(99), lines[42]);
    }

    [Fact]
    public void LinesForRows_matches_row_by_row_lookups()
    {
        var rnd = new Random(99);
        var visible = new bool[20_000];
        for (int i = 0; i < visible.Length; i++) visible[i] = rnd.Next(4) == 0;
        var set = Build(visible);

        var lines = new long[50];
        foreach (long firstRow in new[] { 0L, 1L, 17L, set.Count / 2, set.Count - 10 })
        {
            int n = set.LinesForRows(firstRow, lines);
            Assert.Equal((int)Math.Min(50, set.Count - firstRow), n);
            for (int i = 0; i < n; i++) Assert.Equal(set.LineAt(firstRow + i), lines[i]);
        }
        Assert.Equal(0, set.LinesForRows(set.Count, lines));
    }

    [Fact]
    public void ResolveWindow_holds_the_anchor_still_while_a_pass_rewrites_earlier_lines()
    {
        // The reported bug: re-filtering drops lines BEFORE the viewport, which shifts every row index. The
        // anchored line must stay at the same screen offset and the window must keep the same content, no
        // matter how many rows moved underneath it.
        var first = new bool[20_000];
        for (int i = 0; i < first.Length; i++) first[i] = i % 2 == 0;
        var set = Build(first);

        const long anchorLine = 15_000;
        const int offset = 20;
        var expected = new long[43];
        set.ResolveWindow(anchorLine, offset, expected, out _);
        Assert.Equal(anchorLine, expected[offset]);

        // Sweep a far more selective filter over everything BEFORE the anchor, block by block.
        var second = (bool[])first.Clone();
        for (int i = 0; i < 14_000; i++) second[i] = i % 50 == 0;

        var window = new long[43];
        for (int start = 0; start < 14_000; start += 1024)
        {
            int len = Math.Min(1024, 14_000 - start);
            set.ApplyRange(start, second.AsSpan(start, len));
            set.Publish();

            set.ResolveWindow(anchorLine, offset, window, out int filled);
            Assert.Equal(43, filled);
            Assert.Equal(anchorLine, window[offset]);
            Assert.Equal(expected, window); // identical content at every step of the sweep
        }
    }

    [Fact]
    public void A_row_index_is_not_a_stable_position_while_the_set_changes()
    {
        // Why the view must be anchored to a LINE and never to a row index: as a pass drops lines before a
        // row, that same row index comes to mean a completely different line. Anything that holds a row index
        // across frames - the viewport top, mouse hit-testing, scrolling - drifts unless it re-derives its
        // position from the anchored line first.
        var first = new bool[20_000];
        Array.Fill(first, true);
        var set = Build(first);

        const long row = 15_000;
        long anchorLine = set.LineAt(row);

        // A more selective filter sweeps everything before the anchor.
        var second = (bool[])first.Clone();
        for (int i = 0; i < 14_000; i++) second[i] = i % 10 == 0;
        Apply(set, second);

        Assert.NotEqual(anchorLine, set.LineAt(row));                     // same row, different line now
        Assert.Equal(anchorLine, set.LineAt(set.RowForLine(anchorLine))); // but the line is still exact
    }

    [Fact]
    public async Task Readers_stay_safe_while_the_writer_updates_in_place()
    {
        const int n = 200_000;
        var set = new VisibleLineSet();
        var initial = new bool[n];
        for (int i = 0; i < n; i++) initial[i] = i % 4 == 0;
        set.ApplyRange(0, initial);
        set.Publish();

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Exception? failure = null;

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var rnd = new Random(Environment.CurrentManagedThreadId);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    long count = set.Count;
                    if (count > 0) Assert.InRange(set.LineAt(rnd.NextInt64(count)), 0, n - 1);
                    Assert.InRange(set.RowAtOrAfterLine(rnd.Next(n)), 0, n);
                    Assert.InRange(set.RowForLine(rnd.Next(n)), -1, n);
                }
            }
            catch (Exception ex) { failure = ex; }
        })).ToArray();

        var writer = Task.Run(() =>
        {
            for (int pass = 0; !stop.IsCancellationRequested; pass++)
            {
                var next = new bool[n];
                int step = 2 + pass % 5;
                for (int i = 0; i < n; i++) next[i] = i % step == 0;
                for (int start = 0; start < n && !stop.IsCancellationRequested; start += 8192)
                {
                    int len = Math.Min(8192, n - start);
                    set.ApplyRange(start, next.AsSpan(start, len));
                    set.Publish();
                }
            }
        });

        await Task.WhenAll(readers.Append(writer).ToArray());
        Assert.Null(failure);

        // Once the writer is quiet, a full sweep still lands on exactly the right answer.
        var final = new bool[n];
        for (int i = 0; i < n; i++) final[i] = i % 3 == 0;
        Apply(set, final, block: 8192);
        AssertMatchesReference(set, final);
    }
}

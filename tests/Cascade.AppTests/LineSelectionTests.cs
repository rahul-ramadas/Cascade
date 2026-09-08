using Cascade.App;

namespace Cascade.AppTests;

/// <summary>
/// What is selected, held as disjoint ascending ranges so that selecting a whole 30-million-line file
/// costs one pair of numbers rather than 30 million.
///
/// <para>The interesting property is not any single operation but that the compressed form and the obvious
/// one never disagree, so most of this is a walk against a <see cref="HashSet{T}"/> reference doing the
/// same operations the simple way. A range list has exactly the failure modes a set does not - two
/// adjacent ranges left unmerged, a removal splitting one in the wrong place, an anchor left pointing at a
/// line that is no longer in it - and none of them show up in a test that only checks a couple of
/// operations in isolation.</para>
///
/// <para>No windows here: it is a model, and holding it to a reference is the cheapest test in the
/// project.</para>
/// </summary>
public class LineSelectionTests
{
    [Fact]
    public void A_new_selection_holds_nothing_and_has_no_anchor()
    {
        var sel = new LineSelection();
        Assert.True(sel.IsEmpty);
        Assert.Equal(0, sel.LineCount);
        Assert.Equal(-1, sel.Anchor);
        Assert.False(sel.Contains(0));
    }

    [Fact]
    public void Choosing_one_line_replaces_whatever_was_chosen_before()
    {
        var sel = new LineSelection();
        sel.SetRange(10, 20);
        sel.SetSingle(5);

        Assert.Equal(new[] { (5L, 5L) }, sel.Ranges);
        Assert.Equal(5, sel.Anchor);
        Assert.Equal(1, sel.LineCount);
    }

    [Theory]
    [InlineData(10, 20)]
    [InlineData(20, 10)]   // dragged upwards: the ends arrive the other way round
    public void A_range_reads_the_same_whichever_end_it_was_dragged_from(long a, long b)
    {
        var sel = new LineSelection();
        sel.SetRange(a, b);

        Assert.Equal(new[] { (10L, 20L) }, sel.Ranges);
        Assert.Equal(11, sel.LineCount);
        Assert.Equal(a, sel.Anchor);      // the anchor is where the drag STARTED, not the lower end
    }

    [Fact]
    public void A_negative_line_is_refused_rather_than_stored()
    {
        var sel = new LineSelection();
        sel.SetRange(-1, 5);
        Assert.True(sel.IsEmpty);

        sel.SetSingle(-1);
        Assert.True(sel.IsEmpty);

        sel.ToggleSingle(-1);
        Assert.True(sel.IsEmpty);
    }

    [Fact]
    public void Selecting_everything_is_one_range_however_long_the_file_is()
    {
        var sel = new LineSelection();
        sel.SelectAll(30_000_000);

        Assert.Single(sel.Ranges);
        Assert.Equal(30_000_000, sel.LineCount);
        Assert.True(sel.Contains(29_999_999));
        Assert.False(sel.Contains(30_000_000));
    }

    [Fact]
    public void Selecting_everything_in_an_empty_file_selects_nothing()
    {
        var sel = new LineSelection();
        sel.SelectAll(0);
        Assert.True(sel.IsEmpty);
    }

    [Fact]
    public void Touching_ranges_are_merged_rather_than_left_side_by_side()
    {
        var sel = new LineSelection();
        foreach (long line in new long[] { 1, 3, 2 }) sel.ToggleSingle(line);

        // 1..3 as one range, not three: anything that summarises the selection walks the ranges, so
        // leaving them apart turns an O(1) read into an O(lines) one.
        Assert.Equal(new[] { (1L, 3L) }, sel.Ranges);
    }

    [Fact]
    public void Taking_a_line_out_of_the_middle_splits_the_range_around_it()
    {
        var sel = new LineSelection();
        sel.SetRange(0, 10);
        sel.ToggleSingle(5);

        Assert.Equal(new[] { (0L, 4L), (6L, 10L) }, sel.Ranges);
        Assert.Equal(10, sel.LineCount);
        Assert.False(sel.Contains(5));
    }

    [Fact]
    public void Taking_a_line_off_an_end_shortens_the_range_instead_of_splitting_it()
    {
        var sel = new LineSelection();
        sel.SetRange(0, 10);
        sel.ToggleSingle(0);
        sel.ToggleSingle(10);

        Assert.Equal(new[] { (1L, 9L) }, sel.Ranges);
    }

    [Fact]
    public void The_version_moves_on_every_change_so_a_summary_can_be_cached_against_it()
    {
        var sel = new LineSelection();
        int start = sel.Version;

        sel.SetSingle(1);
        sel.SetRange(2, 3);
        sel.ToggleSingle(9);
        sel.SelectAll(10);
        sel.Restore(sel.Ranges.ToList(), 0);
        sel.Clear();

        Assert.Equal(start + 6, sel.Version);
    }

    [Fact]
    public void A_selection_handed_back_is_the_one_that_was_borrowed()
    {
        // Cropping to the selection takes it away and lifting the crop gives it back; this is that.
        var sel = new LineSelection();
        sel.SetRange(100, 200);
        sel.ToggleSingle(300);
        var borrowed = sel.Ranges.ToList();
        long anchor = sel.Anchor;

        sel.Clear();
        sel.Restore(borrowed, anchor);

        Assert.Equal(borrowed, sel.Ranges);
        Assert.Equal(anchor, sel.Anchor);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void It_agrees_with_a_plain_set_of_lines_over_a_long_random_walk(int seed)
    {
        // The reference does the same thing the obvious way. Whatever the ranges are doing internally, the
        // set of lines they stand for has to match line for line, and the ranges have to stay normalized:
        // ascending, disjoint, and never merely touching.
        var random = new Random(seed);
        var sel = new LineSelection();
        var reference = new HashSet<long>();
        const long Lines = 200;

        for (int step = 0; step < 3_000; step++)
        {
            switch (random.Next(6))
            {
                case 0:
                    long single = random.NextInt64(Lines);
                    sel.SetSingle(single);
                    reference.Clear();
                    reference.Add(single);
                    break;

                case 1:
                    long a = random.NextInt64(Lines), b = random.NextInt64(Lines);
                    sel.SetRange(a, b);
                    reference.Clear();
                    for (long l = Math.Min(a, b); l <= Math.Max(a, b); l++) reference.Add(l);
                    break;

                case 2:
                case 3:
                case 4:
                    long toggled = random.NextInt64(Lines);
                    sel.ToggleSingle(toggled);
                    if (!reference.Remove(toggled)) reference.Add(toggled);
                    break;

                case 5:
                    if (random.Next(4) == 0) { sel.Clear(); reference.Clear(); }
                    else { sel.SelectAll(Lines); reference.Clear(); for (long l = 0; l < Lines; l++) reference.Add(l); }
                    break;
            }

            AssertSameAs(sel, reference, step, seed);
        }
    }

    private static void AssertSameAs(LineSelection sel, HashSet<long> reference, int step, int seed)
    {
        string where = $"seed {seed}, step {step}";

        Assert.True(reference.Count == sel.LineCount,
                    $"{where}: {sel.LineCount} lines selected, {reference.Count} in the reference");
        Assert.Equal(reference.Count == 0, sel.IsEmpty);

        var ranges = sel.Ranges;
        for (int i = 0; i < ranges.Count; i++)
        {
            Assert.True(ranges[i].A <= ranges[i].B, $"{where}: range {i} runs backwards");
            if (i > 0)
                Assert.True(ranges[i - 1].B + 1 < ranges[i].A,
                            $"{where}: ranges {i - 1} and {i} touch or overlap and should have been merged");
        }

        long walked = 0;
        foreach (var (a, b) in ranges)
        {
            for (long l = a; l <= b; l++)
            {
                Assert.True(reference.Contains(l), $"{where}: line {l} is selected but should not be");
                walked++;
            }
        }
        Assert.Equal(reference.Count, walked);

        // Contains has its own walk, so ask it too rather than trusting the ranges twice.
        foreach (long line in reference) Assert.True(sel.Contains(line), $"{where}: Contains missed line {line}");
    }
}

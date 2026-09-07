using Cascade.Core.Filtering;

namespace Cascade.Core.Tests;

public class FilteredViewTests
{
    /// <summary>An explicit view over exactly the given visible lines.</summary>
    private static FilteredView Explicit(params long[] lines)
    {
        var set = new VisibleLineSet();
        long max = lines.Length == 0 ? 0 : lines[^1] + 1;
        set.EnsureLines(max);
        var flags = new bool[max];
        foreach (long l in lines) flags[l] = true;
        set.ApplyRange(0, flags);
        set.Publish();
        return FilteredView.CreateExplicit(set);
    }

    [Fact]
    public void Explicit_view_maps_rows_and_finds_lines()
    {
        var v = Explicit(2, 5, 9, 12, 100);

        Assert.Equal(5, v.Count);
        Assert.Equal(9, v.LineAt(2));
        Assert.Equal(2, v.RowForLine(9));
        Assert.Equal(-1, v.RowForLine(7)); // not visible
        Assert.Equal(0, v.RowForLine(2));
        Assert.Equal(4, v.RowForLine(100));
    }

    [Fact]
    public void RowAtOrAfter_returns_insertion_point()
    {
        var v = Explicit(10, 20, 30);

        Assert.Equal(0, v.RowAtOrAfterLine(5));
        Assert.Equal(1, v.RowAtOrAfterLine(15));
        Assert.Equal(1, v.RowAtOrAfterLine(20));
        Assert.Equal(3, v.RowAtOrAfterLine(100));
    }

    [Fact]
    public void Identity_view_is_one_to_one()
    {
        long n = 42;
        var v = FilteredView.CreateIdentity(() => n);
        Assert.True(v.IsIdentity);
        Assert.Equal(42, v.Count);
        Assert.Equal(7, v.LineAt(7));
        Assert.Equal(7, v.RowForLine(7));
        Assert.Equal(-1, v.RowForLine(999));
    }

    [Fact]
    public void Identity_view_resolves_whole_windows()
    {
        long n = 1000;
        var v = FilteredView.CreateIdentity(() => n);
        var lines = new long[10];

        long first = v.ResolveWindow(500, 3, lines, out int count);
        Assert.Equal(497, first);
        Assert.Equal(10, count);
        Assert.Equal(500, lines[3]);

        Assert.Equal(5, v.LinesForRows(995, lines)); // clipped by the end of the file
        Assert.Equal(995, lines[0]);
    }

    /// <summary>An explicit view over a set left as a running pass leaves it between publishes: bits set in
    /// the block the crop begins in, with the published snapshot still predating them.</summary>
    private static FilteredView BetweenPublishes(int lines, int from, int toExclusive)
    {
        var set = new VisibleLineSet();
        set.ApplyRange(0, new bool[lines]);
        set.Publish();
        var added = new bool[toExclusive - from];
        Array.Fill(added, true);
        set.ApplyRange(from, added);                // deliberately NOT published
        return FilteredView.CreateExplicit(set);
    }

    /// <summary>Reported 2026-09-07: with a crop on and almost nothing matching inside it, the whole log view
    /// turned into WinForms' red X and stayed there - an ArgumentException escaping OnPaint. A cropped view
    /// bounds the row it returns by <c>Count</c>, and Count is two drifting ranks subtracted, so over a crop
    /// holding hardly anything the bound came out below zero and <c>Math.Clamp</c> threw.</summary>
    [Fact]
    public void A_cropped_view_answers_for_a_row_while_the_writer_is_between_publishes()
    {
        var v = BetweenPublishes(40_000, 8_192, 10_000).Cropped(10_000, 30_000);

        long visible = 0;
        for (long line = 10_000; line < 30_000; line++) if (v.IsVisible(line)) visible++;
        Assert.Equal(0, visible);                   // the crop really does admit nothing that is on

        Assert.InRange(v.RowAtOrAfterLine(15_000), 0, 20_000);
        Assert.InRange(v.Count, 0, 20_000);
    }
}

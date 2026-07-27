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
}

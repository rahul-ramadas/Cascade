using Cascade.Core.Filtering;

namespace Cascade.Core.Tests;

public class FilteredViewTests
{
    [Fact]
    public void Explicit_view_maps_rows_and_finds_lines()
    {
        var v = FilteredView.CreateExplicit();
        foreach (long line in new long[] { 2, 5, 9, 12, 100 }) v.Append(line);

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
        var v = FilteredView.CreateExplicit();
        foreach (long line in new long[] { 10, 20, 30 }) v.Append(line);

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
}

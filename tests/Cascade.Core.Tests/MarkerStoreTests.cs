using Cascade.Core.Markers;

namespace Cascade.Core.Tests;

/// <summary>
/// Markers are hand-picked, so the store is built for a handful of lines - but the UI can put one on every
/// selected line at once, and the minimap and the scrollbar both ask for the whole set while PAINTING. So
/// what a repaint costs matters as much as what a change costs.
/// </summary>
public class MarkerStoreTests
{
    [Fact]
    public void Marked_lines_come_back_in_order_with_their_masks()
    {
        var markers = new MarkerStore();
        markers.Toggle(50, 0);
        markers.Toggle(10, 1);
        markers.Toggle(10, 3);
        markers.Toggle(30, 2);

        Assert.Equal([(10L, (byte)0b1010), (30L, (byte)0b100), (50L, (byte)0b1)], markers.Snapshot());
    }

    [Fact]
    public void Asking_again_while_nothing_has_changed_costs_nothing()
    {
        // Both the minimap and the scrollbar ask for this on every repaint. Sorting the marks afresh each
        // time is nothing for a handful and a quarter of a second for the two million a select-all can make.
        var markers = new MarkerStore();
        for (long line = 0; line < 1000; line++) markers.Toggle(line, 0);

        var first = markers.Snapshot();
        Assert.Same(first, markers.Snapshot());

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) markers.Snapshot();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1000, $"{allocated:N0} bytes allocated over 50 repaints' worth of asking");
    }

    [Fact]
    public void Every_kind_of_change_is_noticed()
    {
        var markers = new MarkerStore();
        markers.Toggle(5, 0);

        var afterToggle = markers.Snapshot();
        markers.Toggle(7, 0);
        Assert.NotSame(afterToggle, markers.Snapshot());
        Assert.Equal(2, markers.Snapshot().Count);

        var afterAdd = markers.Snapshot();
        markers.Set(9, 1, true);
        Assert.NotSame(afterAdd, markers.Snapshot());

        var afterSet = markers.Snapshot();
        markers.Set(9, 1, false);
        Assert.NotSame(afterSet, markers.Snapshot());

        var afterUnset = markers.Snapshot();
        markers.Toggle(5, 0);                       // back off again: the line drops out entirely
        Assert.NotSame(afterUnset, markers.Snapshot());
        Assert.Equal([(7L, (byte)1)], markers.Snapshot());

        var afterRemove = markers.Snapshot();
        markers.Clear();
        Assert.NotSame(afterRemove, markers.Snapshot());
        Assert.Empty(markers.Snapshot());
    }

    [Fact]
    public void Each_change_in_a_run_of_them_is_picked_up()
    {
        var markers = new MarkerStore();
        for (int i = 0; i < 200; i++)
        {
            markers.Toggle(i, 0);
            Assert.Equal(i + 1, markers.Snapshot().Count);
            Assert.Equal(i, markers.Snapshot()[i].Line);
        }
    }

    [Fact]
    public void Navigation_and_usage_still_follow_the_marks()
    {
        var markers = new MarkerStore();
        markers.Toggle(10, 2);
        markers.Toggle(20, 2);
        markers.Snapshot();                          // take a snapshot, then keep changing things

        markers.Toggle(30, 5);
        Assert.Equal(20, markers.Next(10, 2));
        Assert.Equal(-1, markers.Next(20, 2));
        Assert.Equal(10, markers.Previous(20, 2));
        Assert.Equal((1 << 2) | (1 << 5), markers.UsedMarkers);
        Assert.True(markers.Has(30, 5));
        Assert.Equal(3, markers.Snapshot().Count);
    }
}

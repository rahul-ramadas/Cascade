using Cascade.Core.Columns;

namespace Cascade.Core.Tests;

/// <summary>Where the column edges go. These are the rules the user feels: an edge lands on a character,
/// the columns fill the window, and a dragged header settles instead of flickering.</summary>
public class ColumnLayoutTests
{
    private static int Total(int[] widths) => widths.Sum();

    // ---- snapping ----

    [Theory]
    [InlineData(0, 8, 8)]        // never nothing: one character is the floor
    [InlineData(3, 8, 8)]
    [InlineData(11, 8, 8)]       // rounds to the nearer character
    [InlineData(13, 8, 16)]
    [InlineData(12, 8, 16)]      // exactly half a character rounds up, so a drag always moves
    [InlineData(20, 8, 24)]
    [InlineData(80, 8, 80)]
    [InlineData(-5, 8, 8)]
    public void A_width_snaps_to_whole_characters(int width, int charWidth, int expected)
        => Assert.Equal(expected, ColumnLayout.SnapToChars(width, charWidth));

    [Fact]
    public void Snapping_without_a_character_width_leaves_the_width_alone()
    {
        Assert.Equal(37, ColumnLayout.SnapToChars(37, 0));
        Assert.Equal(1, ColumnLayout.SnapToChars(0, 0));
    }

    // ---- fitting ----

    [Fact]
    public void Spare_room_goes_to_the_last_automatic_column()
    {
        var w = ColumnLayout.Fit([100, 100, 100], [true, true, true], 500, 20);
        Assert.Equal([100, 100, 300], w);
        Assert.Equal(500, Total(w));
    }

    [Fact]
    public void A_column_the_user_sized_keeps_exactly_what_it_was_given()
    {
        var w = ColumnLayout.Fit([250, 100, 100], [false, true, true], 500, 20);
        Assert.Equal(250, w[0]);
        Assert.Equal(500, Total(w));
        Assert.Equal(150, w[2]);   // the surplus lands on the last automatic one, not the sized one
    }

    [Fact]
    public void The_widest_columns_are_capped_when_there_is_not_enough_room()
    {
        // A short field must not be squeezed on behalf of a long one: 40 and 60 are untouched and the
        // 400-wide column gives up everything that has to go.
        var w = ColumnLayout.Fit([40, 60, 400], [true, true, true], 300, 20);
        Assert.Equal(40, w[0]);
        Assert.Equal(60, w[1]);
        Assert.Equal(200, w[2]);
        Assert.Equal(300, Total(w));
    }

    [Fact]
    public void Capping_shares_the_shortfall_between_the_columns_that_are_over_it()
    {
        var w = ColumnLayout.Fit([300, 300, 40], [true, true, true], 340, 20);
        Assert.Equal(340, Total(w));
        Assert.Equal(40, w[2]);                       // still under the ceiling, so untouched
        Assert.InRange(Math.Abs(w[0] - w[1]), 0, 1);  // the two big ones end up level with each other
    }

    [Fact]
    public void Every_visible_column_ends_up_at_least_the_minimum_wide()
    {
        var w = ColumnLayout.Fit([5, 5, 5], [true, true, true], 600, 20);
        Assert.All(w, x => Assert.True(x >= 20, $"width {x}"));
        Assert.Equal(600, Total(w));
    }

    [Fact]
    public void With_no_room_at_all_the_columns_keep_their_floor_and_the_row_scrolls()
    {
        var w = ColumnLayout.Fit([200, 200, 200], [true, true, true], 30, 20);
        Assert.Equal([20, 20, 20], w);
        Assert.True(Total(w) > 30, "the row is allowed to run past the window rather than vanish");
    }

    [Fact]
    public void Columns_the_user_sized_are_left_alone_even_when_they_do_not_fit()
    {
        var w = ColumnLayout.Fit([400, 400], [false, false], 300, 20);
        Assert.Equal([400, 400], w);
    }

    [Fact]
    public void An_automatic_column_still_takes_what_is_left_beside_oversized_fixed_ones()
    {
        // Nothing is left over, so the automatic column falls back to its floor rather than to nothing.
        var w = ColumnLayout.Fit([400, 100], [false, true], 300, 20);
        Assert.Equal(400, w[0]);
        Assert.Equal(20, w[1]);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(457)]
    [InlineData(1000)]
    [InlineData(4001)]
    public void The_columns_fill_the_window_exactly_whenever_one_of_them_can_stretch(int available)
    {
        var w = ColumnLayout.Fit([80, 40, 300, 120], [true, false, true, true], available, 20);
        Assert.Equal(available, Total(w));
    }

    [Fact]
    public void Fitting_an_empty_set_asks_for_nothing()
        => Assert.Empty(ColumnLayout.Fit([], [], 500, 20));

    // ---- carrying a header to a new place ----

    [Fact]
    public void A_header_stays_put_until_the_pointer_passes_the_middle_of_its_neighbour()
    {
        int[] widths = [200, 50];
        Assert.Equal(1, ColumnLayout.DropTarget(widths, 1, 190));   // still over its own column
        Assert.Equal(1, ColumnLayout.DropTarget(widths, 1, 150));   // over the neighbour, past its middle
        Assert.Equal(0, ColumnLayout.DropTarget(widths, 1, 90));    // through the middle: it moves
    }

    [Fact]
    public void Carrying_a_narrow_column_past_a_wide_one_settles_instead_of_flickering()
    {
        // The bug this rule exists for: swap on contact and the pointer lands back over the wide column,
        // which swaps them again. Walk in, swap, and walk on - the answer must never point back.
        int[] before = [200, 50];
        int moveAt = -1;
        for (int x = 240; x >= 0; x -= 5)
            if (ColumnLayout.DropTarget(before, 1, x) == 0) { moveAt = x; break; }
        Assert.True(moveAt >= 0, "carrying it left never moved it");

        int[] after = [50, 200];   // as laid out once it has moved
        for (int x = moveAt; x >= 0; x -= 5)
            Assert.Equal(0, ColumnLayout.DropTarget(after, 0, x));
    }

    [Fact]
    public void Dragging_past_either_end_takes_the_column_to_that_end()
    {
        int[] widths = [100, 100, 100];
        Assert.Equal(0, ColumnLayout.DropTarget(widths, 2, -40));
        Assert.Equal(2, ColumnLayout.DropTarget(widths, 0, 900));
    }

    [Fact]
    public void A_lone_column_has_nowhere_to_go()
    {
        Assert.Equal(0, ColumnLayout.DropTarget([120], 0, 300));
        Assert.Equal(3, ColumnLayout.DropTarget([], 3, 10));
    }

    [Fact]
    public void Every_column_can_be_carried_to_every_other_place()
    {
        int[] widths = [100, 60, 140, 80];
        for (int from = 0; from < widths.Length; from++)
        {
            var reached = new HashSet<int>();
            for (int x = 0; x < widths.Sum(); x += 5) reached.Add(ColumnLayout.DropTarget(widths, from, x));
            Assert.Equal(widths.Length, reached.Count);
        }
    }
}

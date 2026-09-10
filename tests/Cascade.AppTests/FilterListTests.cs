using Xunit.Abstractions;

namespace Cascade.AppTests;

/// <summary>The filter pane: what it shows, what it lets you do to a filter, and the surgical updates
/// that keep it from flashing while you do it.</summary>
public class FilterListTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void A_row_says_what_the_filter_is() => Verify(Checks.RunFilterListChecks, atLeast: 35);
    [Fact] public void The_list_updates_in_place_rather_than_rebuilding() => Verify(Checks.RunFilterSyncChecks, atLeast: 15);
    [Fact] public void Searching_the_list_reveals_the_match() => Verify(Checks.RunFilterSearchRevealChecks, atLeast: 4);
    [Fact] public void The_search_bar_opens_below_and_closes_on_escape() => Verify(Checks.RunFilterSearchBarChecks, atLeast: 21);
    [Fact] public void Ticking_a_filter_switches_on_what_it_should() => Verify(Checks.RunFilterEnableChecks, atLeast: 22);
    [Fact] public void Several_filters_can_be_chosen_at_once() => Verify(Checks.RunFilterSelectionChecks, atLeast: 35);
    [Fact] public void Nothing_is_remembered_about_a_filter_that_is_gone() => Verify(Checks.RunFilterPaneMemoryChecks, atLeast: 11);
    [Fact] public void Hovering_a_line_says_which_filters_match_it() => Verify(Checks.RunFilterTipChecks, atLeast: 21);
    [Fact] public void A_new_filter_can_be_made_from_anywhere() => Verify(Checks.RunNewFilterChecks, atLeast: 27);
    [Fact] public void A_new_filter_lands_where_the_preference_says() => Verify(Checks.RunFilterPlacementChecks, atLeast: 27);
    [Fact] public void A_new_filter_starts_from_the_line_beneath_it() => Verify(Checks.RunNewFilterFromLineChecks, atLeast: 15);
    [Fact] public void Which_of_two_matching_filters_wins_follows_the_preference() => Verify(Checks.RunFilterPrecedenceChecks, atLeast: 7);
}

/// <summary>Carrying a filter about with the mouse. Where it lands, what it nests under, and every way a
/// drag can end - including the ones OLE never tells the control about.</summary>
public class FilterDragTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void Where_a_drop_lands_is_pure_geometry() => Verify(Checks.RunDropPlacementChecks, atLeast: 9);
    [Fact] public void One_row_of_travel_is_one_place() => Verify(Checks.RunFilterDragChecks, atLeast: 42);
    [Fact] public void Unfolding_a_row_does_not_scroll_the_list() => Verify(Checks.RunFilterExpandChecks, atLeast: 5);
    [Fact] public void A_drop_under_a_filter_nests_at_the_top() => Verify(Checks.RunDragNestingChecks, atLeast: 6);
}

/// <summary>Presets: a tick says what is in effect, the selection says what a command will act on.</summary>
public class FilterPresetTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void A_preset_toggles_only_the_filters_it_names() => Verify(Checks.RunFilterPresetChecks, atLeast: 27);
}

/// <summary>Loading, saving and letting go of a filter set.</summary>
public class FilterFileTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void Every_path_that_discards_filters_offers_to_save_them() => Verify(Checks.RunCloseFiltersChecks, atLeast: 23);
}

/// <summary>How a filter decides what a line looks like: inheritance, the styles themselves, and the
/// colours offered for them.</summary>
public class AppearanceTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void Underline_is_a_style_like_bold_and_italic() => Verify(Checks.RunUnderlineChecks, atLeast: 6);
    [Fact] public void Restyling_a_filter_does_not_re_run_it() => Verify(Checks.RunRestyleChecks, atLeast: 22);
    [Fact] public void A_frame_s_colours_do_not_change_half_way_through_it() => Verify(Checks.RunColourWhileFilteringChecks, atLeast: 12);
    [Fact] public void Several_filters_can_be_restyled_together() => Verify(Checks.RunAppearanceChecks, atLeast: 12);
    [Fact] public void Every_colour_offered_looks_unlike_the_others() => Verify(Checks.RunLuckyColorChecks, atLeast: 17);
    [Fact] public void The_pattern_box_previews_the_colours_chosen() => Verify(Checks.RunColorPreviewChecks, atLeast: 25);
    [Fact] public void A_style_box_turns_on_before_it_turns_off() => Verify(Checks.RunStyleBoxChecks, atLeast: 9);
}

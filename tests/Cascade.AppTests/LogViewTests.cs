using Xunit.Abstractions;

namespace Cascade.AppTests;

/// <summary>What the log view paints, and where. Every one of these renders a real
/// <c>LineGridControl</c> and reads the answer off the pixels, because a repaint that quietly stops
/// happening looks exactly like one that did.</summary>
public class RenderTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void Scrolling_sideways_never_paints_over_the_margin() => Verify(Checks.RunRenderChecks);
    [Fact] public void Home_and_end_reach_both_edges_of_the_widest_line() => Verify(Checks.RunHorizontalScrollChecks);
    [Fact] public void Wrapped_rows_leave_no_dead_space() => Verify(Checks.RunWordWrapChecks);
    [Fact] public void The_scrollable_width_matches_what_is_drawn() => Verify(Checks.RunTextWidthChecks);
    [Fact] public void Line_spacing_is_the_font_s_own_height() => Verify(Checks.RunLineSpacingChecks);
    [Fact] public void A_progress_bar_paints_the_value_it_was_given() => Verify(Checks.RunProgressPaintChecks);
}

/// <summary>Moving about the log: the caret, the keys that drive it, and what stays where when the view
/// changes underneath it.</summary>
public class LogViewTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void The_editing_keys_do_what_the_menu_says() => Verify(Checks.RunEditingKeyChecks);
    [Fact] public void A_jump_leaves_room_to_read_around_it() => Verify(Checks.RunNavigationChecks);
    [Fact] public void Text_can_be_picked_out_of_a_line() => Verify(Checks.RunTextSelectionChecks);
    [Fact] public void Text_can_be_picked_out_of_a_cell() => Verify(Checks.RunColumnSelectionChecks);
    [Fact] public void A_selection_is_keyed_to_lines_not_rows() => Verify(Checks.RunSelectionFollowsTextChecks);
    [Fact] public void A_selection_survives_the_filters_changing() => Verify(Checks.RunSelectionStabilityChecks);
    [Fact] public void The_viewport_holds_still_across_a_mode_change() => Verify(Checks.RunViewportStabilityChecks);
    [Fact] public void Copying_only_ever_takes_what_is_shown() => Verify(Checks.RunCopyBudgetChecks);
}

/// <summary>Splitting a line into columns: the layout arithmetic, the header gestures, and the dialog
/// that names the fields.</summary>
public class ColumnTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void Columns_are_laid_out_from_the_header() => Verify(Checks.RunColumnChecks);
    [Fact] public void The_field_settings_dialog_says_what_it_will_do() => Verify(Checks.RunFieldSettingsChecks);
    [Fact] public void Turning_column_mode_on_holds_the_text_still() => Verify(Checks.RunColumnModeChecks);
}

/// <summary>Cropping the view to a stretch of the file, and what that does to everything keyed to a
/// line.</summary>
public class CroppingTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void A_crop_shows_exactly_the_stretch_it_names() => Verify(Checks.RunCropChecks);
    [Fact] public void A_crop_and_the_selection_hand_each_other_back() => Verify(Checks.RunCropSelectionChecks);
}

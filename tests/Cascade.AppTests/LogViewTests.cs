using Xunit.Abstractions;

namespace Cascade.AppTests;

/// <summary>What the log view paints, and where. Every one of these renders a real
/// <c>LineGridControl</c> and reads the answer off the pixels, because a repaint that quietly stops
/// happening looks exactly like one that did.</summary>
public class RenderTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void Scrolling_sideways_never_paints_over_the_margin() => Verify(Checks.RunRenderChecks, atLeast: 32);
    [Fact] public void Home_and_end_reach_both_edges_of_the_widest_line() => Verify(Checks.RunHorizontalScrollChecks, atLeast: 8);
    [Fact] public void Wrapped_rows_leave_no_dead_space() => Verify(Checks.RunWordWrapChecks, atLeast: 26);
    [Fact] public void The_scrollable_width_matches_what_is_drawn() => Verify(Checks.RunTextWidthChecks, atLeast: 6);
    [Fact] public void Line_spacing_is_the_font_s_own_height() => Verify(Checks.RunLineSpacingChecks, atLeast: 6);
    [Fact] public void A_progress_bar_paints_the_value_it_was_given() => Verify(Checks.RunProgressPaintChecks, atLeast: 2);
}

/// <summary>Moving about the log: the caret, the keys that drive it, and what stays where when the view
/// changes underneath it.</summary>
public class LogViewTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void The_editing_keys_do_what_the_menu_says() => Verify(Checks.RunEditingKeyChecks, atLeast: 14);
    [Fact] public void A_jump_leaves_room_to_read_around_it() => Verify(Checks.RunNavigationChecks, atLeast: 9);
    [Fact] public void Text_can_be_picked_out_of_a_line() => Verify(Checks.RunTextSelectionChecks, atLeast: 14);
    [Fact] public void Text_can_be_picked_out_of_a_cell() => Verify(Checks.RunColumnSelectionChecks, atLeast: 21);
    [Fact] public void A_selection_is_keyed_to_lines_not_rows() => Verify(Checks.RunSelectionFollowsTextChecks, atLeast: 34);
    [Fact] public void A_selection_survives_the_filters_changing() => Verify(Checks.RunSelectionStabilityChecks, atLeast: 19);
    [Fact] public void The_viewport_holds_still_across_a_mode_change() => Verify(Checks.RunViewportStabilityChecks, atLeast: 11);
    [Fact] public void Copying_only_ever_takes_what_is_shown() => Verify(Checks.RunCopyBudgetChecks, atLeast: 5);
}

/// <summary>Splitting a line into columns: the layout arithmetic, the header gestures, and the dialog
/// that names the fields.</summary>
public class ColumnTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    // 91 rather than the 92 this machine makes. The field-settings dialog is checked one way when the
    // screen has room for it and another when it has to scroll, and a runner's 1024x768 takes the second
    // branch - which is exactly the case that group was taught about. A floor from one display is not one.
    [Fact] public void Columns_are_laid_out_from_the_header() => Verify(Checks.RunColumnChecks, atLeast: 91);
    [Fact] public void The_field_settings_dialog_says_what_it_will_do() => Verify(Checks.RunFieldSettingsChecks, atLeast: 77);
    [Fact] public void Turning_column_mode_on_holds_the_text_still() => Verify(Checks.RunColumnModeChecks, atLeast: 28);
}

/// <summary>Cropping the view to a stretch of the file, and what that does to everything keyed to a
/// line.</summary>
public class CroppingTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void A_crop_shows_exactly_the_stretch_it_names() => Verify(Checks.RunCropChecks, atLeast: 30);
    [Fact] public void A_crop_and_the_selection_hand_each_other_back() => Verify(Checks.RunCropSelectionChecks, atLeast: 13);
}

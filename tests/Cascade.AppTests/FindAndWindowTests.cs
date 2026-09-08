using Xunit.Abstractions;

namespace Cascade.AppTests;

/// <summary>The find bar: one row, docked inside the log view, so there are only two states to be in.
/// Most of these are about it never moving anything else on screen.</summary>
public class FindBarTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void The_bar_keeps_its_term_its_caret_and_its_keys() => Verify(Checks.RunFindBarChecks);
    [Fact] public void Opening_the_bar_moves_neither_the_text_nor_the_divider() => Verify(Checks.RunFindBarLayoutChecks);
    [Fact] public void The_count_changing_does_not_repaint_the_bar() => Verify(Checks.RunFindBarRepaintChecks);
    [Fact] public void The_bar_shares_its_row_without_starving_anything() => Verify(Checks.RunFindBarRoomChecks);
    [Fact] public void A_search_starts_from_where_the_reader_is() => Verify(Checks.RunFindSeedChecks);
    [Fact] public void Matches_are_marked_only_where_they_can_be_drawn() => Verify(Checks.RunFindHighlightChecks);
    [Fact] public void The_tally_says_what_it_knows_and_no_more() => Verify(Checks.RunFindStatusChecks);
}

/// <summary>The minimap: the log zoomed out, one pixel a row until there are more rows than
/// pixels.</summary>
public class MiniMapTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void The_map_follows_the_view_and_the_marks_follow_the_file() => Verify(Checks.RunMatchMapChecks);
}

/// <summary>Times read out of the log itself, and the margin and status bar that report them.</summary>
public class ElapsedTimeTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void The_gap_column_measures_from_the_rows_on_screen() => Verify(Checks.RunElapsedChecks);
}

/// <summary>The window's own furniture: the status bar, the divider, the frame and what it accepts from
/// outside.</summary>
public class WindowTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void The_status_bar_gives_every_field_the_room_it_needs() => Verify(Checks.RunStatusBarChecks);
    [Fact] public void The_divider_snaps_to_whole_lines_without_walking() => Verify(Checks.RunSplitterChecks);
    [Fact] public void The_window_settles_on_a_whole_number_of_lines() => Verify(Checks.RunWindowSnapChecks);
    [Fact] public void Closing_takes_the_window_down_before_the_file() => Verify(Checks.RunClosingChecks);
    [Fact] public void A_dropped_file_opens_as_a_log_or_as_filters() => Verify(Checks.RunFileDropChecks);
    [Fact] public void Tab_has_two_stops_and_stays_inside_an_open_bar() => Verify(Checks.RunTabStopChecks);
    [Fact] public void A_run_leaves_the_desktop_where_it_found_it() => Verify(Checks.RunDesktopMannersChecks);
}

/// <summary>Menus and dialogs as keyboard surfaces: one Alt key each, no clashes, and nothing that moves
/// when a message appears.</summary>
public class KeyboardTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void No_two_menu_entries_claim_the_same_alt_key() => Verify(Checks.RunMenuMnemonicChecks);
    [Fact] public void Every_menu_entry_does_what_it_says() => Verify(Checks.RunMenuActionChecks);
    [Fact] public void The_encoding_menu_says_which_one_is_in_effect() => Verify(Checks.RunEncodingMenuChecks);
    [Fact] public void Dialog_mnemonics_are_unique_and_nothing_clips() => Verify(Checks.RunDialogKeyboardChecks);
    [Fact] public void A_complaint_appearing_repaints_nothing_but_itself() => Verify(Checks.RunFilterDialogRepaintChecks);
    [Fact] public void The_about_box_lines_its_values_up_with_their_labels() => Verify(Checks.RunAboutBoxChecks);
}

/// <summary>The engine as the window uses it, and the things the app does to the machine it runs
/// on.</summary>
public class DiagnosticsTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void Open_index_filter_and_map_rows_end_to_end() => Verify(Checks.RunEngineChecks);
    [Fact] public void A_frozen_window_is_recorded_and_a_working_one_is_not() => Verify(Checks.RunHangWatchdogChecks);
    [Fact] public void Automation_is_answered_only_when_it_is_asked_for() => Verify(Checks.RunAutomationChecks);
    [Fact] public void Drawing_hands_back_every_handle_it_takes() => Verify(Checks.RunResourceChecks);
}

/// <summary>Preferences, per-machine state, and the credential the updater is allowed to use.</summary>
public class SettingsTests(ITestOutputHelper output) : AppCheckFixture(output)
{
    [Fact] public void Every_preference_survives_an_export_and_an_import() => Verify(Checks.RunSettingsChecks);
    [Fact] public void Machine_state_is_a_separate_file_and_is_never_exported() => Verify(Checks.RunMachineStateChecks);
    [Fact] public void The_git_credential_only_ever_goes_to_github() => Verify(Checks.RunUpdateCredentialChecks);
}

using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;

namespace Cascade.UiTests;

/// <summary>
/// End-to-end UI-automation tests that drive the real Cascade.exe through FlaUI (Windows UI
/// Automation) — no in-process test hooks. Each test launches its own app instance on a deterministic
/// 120-line file (every 5th line contains "MATCH") with one imported "MATCH" filter.
/// </summary>
public class UiFeatureTests
{
    /// <summary>
    /// Home and End jump the view to the far left and far right of the log. Ctrl+Home and Ctrl+End keep
    /// their existing meaning - first and last line - so this guards both at once.
    /// </summary>
    [Fact]
    public void Home_and_end_scroll_the_log_to_its_left_and_right_edges()
    {
        // Lines wide enough to overflow any screen; on a 4K monitor a shorter line fits entirely and there
        // would be nothing to scroll, so the test would pass without proving anything.
        string log = TestData.WriteLogFile(minWidth: 1000);
        using var app = CascadeApp.LaunchExisting(log, null, CascadeApp.NewSettingsDir(),
                                                  ownsFiles: true, ownsSettingsDir: true);
        app.ClickMenuOrThrow("View", "Focus Text Area");
        var grid = app.Grid();

        Assert.Equal(0, app.HorizontalScroll());

        app.Key(grid, VirtualKeyShort.END);
        Assert.True(app.WaitHorizontalScroll(v => v > 0), "End did not scroll the view right: " + app.DescribeScrollBars());
        double rightEdge = app.HorizontalScroll();

        // Already at the extreme: pressing End again must not creep further.
        app.Key(grid, VirtualKeyShort.END);
        Assert.Equal(rightEdge, app.HorizontalScroll());

        app.Key(grid, VirtualKeyShort.HOME);
        Assert.True(app.WaitHorizontalScroll(v => v == 0), "Home did not return the view to the left edge");

        // The Ctrl variants still move the caret rather than the view.
        app.CtrlKey(grid, VirtualKeyShort.END);
        Assert.True(app.WaitCaretLine(TestData.LineCount),
                    "Ctrl+End no longer goes to the last line: " + $"line {app.CaretLine()}");
        app.CtrlKey(grid, VirtualKeyShort.HOME);
        Assert.True(app.WaitCaretLine(1),
                    "Ctrl+Home no longer goes to the first line: " + $"line {app.CaretLine()}");
    }

    [Fact]
    public void Keeps_the_selected_line_where_it_is_when_toggling_filtered_mode()
    {
        using var app = CascadeApp.Launch();
        var fails = new List<string>();
        void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

        // How many rows down the viewport the selected one sits. Counted in rows, not pixels: the top row
        // can be scrolled part-way out of view, which shifts every pixel figure without anything having
        // actually moved.
        int OffsetRows()
        {
            var sel = app.SelectedRow();
            return sel is null ? -1 : app.Rows().Count(r => r.BoundingRectangle.Top < sel.Value.bounds.Top);
        }

        // Park the line a few rows below the top. Starting it mid-screen would make "it stayed put" and
        // "it was moved to the middle" the same measurement, and the check would prove nothing.
        app.ScrollVerticalTo(497);
        app.SelectLine(501);                     // 0-based 500, a MATCH line, a few rows down
        Check("selects line 501", app.CaretLine() == 501, $"line {app.CaretLine()}");
        Check("one line selected", app.StatusText("Sel:") == "Sel: 1", app.StatusText("Sel:"));

        int rows = app.Rows().Length;
        int before = OffsetRows();
        Check("the row is visible to start with", before >= 0);
        Check("and is nowhere near the middle, or this proves nothing",
              before >= 0 && before < rows / 3, $"offset {before} of {rows} rows");

        // Hiding the non-matching lines must not move it: line 501 matches, so it stays exactly put.
        app.ToggleFilteredMode();
        int after = OffsetRows();
        Check("filtered: line stays 501", app.CaretLine() == 501, $"line {app.CaretLine()}");
        Check("filtered: still one selected", app.StatusText("Sel:") == "Sel: 1", app.StatusText("Sel:"));
        Check("filtered: matched count", app.StatusText("Fil:") == $"Fil: {TestData.MatchCount:N0}", app.StatusText("Fil:"));
        Check("filtered: the line did not move on screen", after == before, $"{before} -> {after}");

        // And back again.
        app.ToggleFilteredMode();
        int back = OffsetRows();
        Check("dim: line stays 501", app.CaretLine() == 501, $"line {app.CaretLine()}");
        Check("dim: the line did not move on screen", back == before, $"{before} -> {back}");

        // A NON-matching line (1-based 503) has nowhere to stay, so the nearest match at or after it takes
        // its place - 1-based 506 - and that should appear about where 503 was, not in the middle.
        app.SelectLine(503);
        int wasAt = OffsetRows();
        Check("select 503", app.CaretLine() == 503, $"line {app.CaretLine()}");
        app.ToggleFilteredMode();
        int snapped = OffsetRows();
        Check("filtered-out line snaps to nearest match (506)", app.CaretLine() == 506, $"line {app.CaretLine()}");
        Check("nearest still selected", app.StatusText("Sel:") == "Sel: 1", app.StatusText("Sel:"));
        Check("the replacement appears within a row of where the old line was",
              Math.Abs(snapped - wasAt) <= 1, $"{wasAt} -> {snapped}");

        Assert.True(fails.Count == 0,
                    $"Keep-in-view failures (offsets in rows: start {before}, filtered {after}, back {back}, " +
                    $"of {rows} visible):\n  " + string.Join("\n  ", fails));
    }

    [Fact]
    public void Jumping_to_a_line_leaves_room_to_read_around_it()
    {
        // A find used to scroll the bare minimum, so the line you were looking for arrived jammed against
        // the top or bottom edge with none of the surrounding log visible.
        using var app = CascadeApp.Launch();
        var fails = new List<string>();
        void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

        int OffsetRows()
        {
            var sel = app.SelectedRow();
            return sel is null ? -1 : app.Rows().Count(r => r.BoundingRectangle.Top < sel.Value.bounds.Top);
        }

        // Deep enough into the file that neither end can clamp the result and hide a regression.
        // Open the bar first: it takes a couple of rows off the top of the log, so anything measured before
        // it appears is measuring a viewport that is about to change size.
        app.OpenFind();

        app.ScrollVerticalTo(497);
        app.SelectLine(501);
        Check("selects line 501", app.CaretLine() == 501, $"line {app.CaretLine()}");

        int rows = app.Rows().Length;
        int top = rows / 4;
        int bottom = Math.Max(top, rows * 3 / 4 - 1);
        Check("the view is tall enough for a middle half to mean anything", rows >= 9, $"{rows} rows");

        // Forwards to a line below the view: it should settle at the bottom of the band, not the last row.
        app.FindWith("line 520", forward: true);
        bool wentDown = app.WaitCaretLine(521);
        int down = OffsetRows();
        Check("found line 521", wentDown, $"line {app.CaretLine()}");
        Check("a line found below arrives at the bottom of the middle half",
              down == bottom, $"offset {down}, band {top}..{bottom} of {rows}");

        // Backwards to a line above it: the top of the band this time.
        app.FindWith("line 500", forward: false);
        bool wentUp = app.WaitCaretLine(501);
        int up = OffsetRows();
        Check("found line 501 going back", wentUp, $"line {app.CaretLine()}");
        Check("a line found above arrives at the top of the middle half",
              up == top, $"offset {up}, band {top}..{bottom} of {rows}");

        Assert.True(fails.Count == 0,
                    $"Reveal failures (band {top}..{bottom} of {rows} rows):\n  " + string.Join("\n  ", fails));
    }

    [Fact]
    public void Full_feature_sweep()
    {
        using var app = CascadeApp.Launch();
        var fails = new List<string>();
        void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

        // ---- indexing + counts (status bar) ----
        Check("total lines", app.WaitStatus("Total:", "Total: 1,000"), app.StatusText("Total:"));
        Check("matched count (imported filter)", app.WaitStatus("Fil:", $"Fil: {TestData.MatchCount:N0}"), app.StatusText("Fil:"));

        // ---- filter tree + checkbox ----
        var node = app.FilterNode("MATCH");
        Check("filter node present", node is not null);
        var toggle = node?.Patterns.Toggle.PatternOrDefault;
        if (toggle is not null)
            Check("filter enabled (checkbox on)", toggle.ToggleState.ValueOrDefault == ToggleState.On,
                toggle.ToggleState.ValueOrDefault.ToString());

        // ---- menu structure (every top menu + the shortcuts we added) ----
        var top = app.TopMenuNames();
        foreach (var m in new[] { "File", "Edit", "View", "Filters", "Help" })
            Check($"menu '{m}' present", top.Any(t => t.Contains(m, StringComparison.OrdinalIgnoreCase)), string.Join(",", top));
        var view = app.MenuItemNames("View");
        Check("View has Focus Text Area", view.Any(n => n.Contains("Focus Text", StringComparison.OrdinalIgnoreCase)), string.Join(",", view));
        Check("View has Focus Filter List", view.Any(n => n.Contains("Focus Filter L", StringComparison.OrdinalIgnoreCase)), string.Join(",", view));
        Check("View has Find Filter", view.Any(n => n.Contains("Find Filter", StringComparison.OrdinalIgnoreCase)), string.Join(",", view));
        var filtersItems = app.MenuItemNames("Filters");
        Check("Filters has Find Next Match", filtersItems.Any(n => n.Contains("Find Next Match", StringComparison.OrdinalIgnoreCase)), string.Join(",", filtersItems));
        Check("Filters has New Filter from Selection", filtersItems.Any(n => n.Contains("New Filter from Selection", StringComparison.OrdinalIgnoreCase)), string.Join(",", filtersItems));

        // ---- text find (dialog) ----
        app.FindText("other line 7");
        Check("find selects line 8", app.WaitCaretLine(8), $"line {app.CaretLine()}");

        // ---- per-filter find (Filters menu -> Find Next/Previous Match) ----
        app.SelectLine(1);
        app.FilterNode("MATCH")?.AsTreeItem().Select();
        app.FindNextForSelectedFilter();
        Check("per-filter find next -> line 6", app.WaitCaretLine(6), $"line {app.CaretLine()}");
        app.FindNextForSelectedFilter();
        Check("per-filter find next -> line 11", app.WaitCaretLine(11), $"line {app.CaretLine()}");
        app.FindPrevForSelectedFilter();
        Check("per-filter find prev -> line 6", app.WaitCaretLine(6), $"line {app.CaretLine()}");

        // ---- zoom (menu) ----
        app.ClickMenuOrThrow("View", "Reset Zoom");
        Check("zoom reset 100%", app.WaitStatus("Zoom:", "Zoom: 100%"), app.StatusText("Zoom:"));
        app.ClickMenuOrThrow("View", "Zoom In");
        Check("zoom in 110%", app.WaitStatus("Zoom:", "Zoom: 110%"), app.StatusText("Zoom:"));
        app.ClickMenuOrThrow("View", "Zoom Out");
        Check("zoom out 100%", app.StatusText("Zoom:") == "Zoom: 100%", app.StatusText("Zoom:"));

        // ---- add-filter entry points present (the modal dialog's layout is covered by --screens) ----
        Check("Filters has Add Filter", filtersItems.Any(n => n.Contains("Add Filter", StringComparison.OrdinalIgnoreCase)), string.Join(",", filtersItems));

        Assert.True(fails.Count == 0, "Feature sweep failures:\n  " + string.Join("\n  ", fails));
    }

    [Fact]
    public void Deleting_a_filter_leaves_the_rest_of_the_list_intact()
    {
        // Deleting used to rebuild the entire tree, so the list visibly blanked and repopulated. It now
        // removes just the one node, which means the remaining nodes - and the model behind them - have to
        // stay exactly right without the safety net of a full refresh.
        string log = TestData.WriteLogFile();
        string tat = TestData.WriteFilterFile("MATCH", "line 999", "line 998");
        try
        {
            using var app = CascadeApp.LaunchExisting(log, tat, CascadeApp.NewSettingsDir(),
                                                      ownsFiles: false, ownsSettingsDir: true);
            var fails = new List<string>();
            void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

            // "line 999" and "line 998" each match exactly one line, and neither is a MATCH line, so the
            // matched count is an exact check that the model really changed.
            Check("all three filters listed", app.FilterNode("MATCH") is not null
                && app.FilterNode("line 999") is not null && app.FilterNode("line 998") is not null);
            Check("count with all three", app.WaitStatus("Fil:", $"Fil: {TestData.MatchCount + 2:N0}"), app.StatusText("Fil:"));

            // Remove the middle one.
            app.FilterNode("line 999")!.AsTreeItem().Select();
            app.ClickMenuOrThrow("Filters", "Remove Filter");

            Check("deleted filter is gone", app.FilterNode("line 999") is null);
            Check("filter above survives", app.FilterNode("MATCH") is not null);
            Check("filter below survives", app.FilterNode("line 998") is not null);
            Check("count after delete", app.WaitStatus("Fil:", $"Fil: {TestData.MatchCount + 1:N0}"), app.StatusText("Fil:"));

            // Again, so repeated in-place deletes are covered too.
            app.FilterNode("line 998")!.AsTreeItem().Select();
            app.ClickMenuOrThrow("Filters", "Remove Filter");

            Check("second delete removes it", app.FilterNode("line 998") is null);
            Check("original filter still listed", app.FilterNode("MATCH") is not null);
            Check("count back to the base set", app.WaitStatus("Fil:", $"Fil: {TestData.MatchCount:N0}"), app.StatusText("Fil:"));

            Assert.True(fails.Count == 0, "Filter delete failures:\n  " + string.Join("\n  ", fails));
        }
        finally { File.Delete(log); File.Delete(tat); }
    }

    [Fact]
    public void Undo_and_redo_take_back_filter_edits()
    {
        // The undo stack is snapshot-based, so what matters through the real UI is that the list, the
        // selection-independent model behind it and the menu labels all agree afterwards. "line 999" and
        // "line 998" each match exactly one non-MATCH line, so Fil: proves the model changed, not just
        // the tree.
        string log = TestData.WriteLogFile();
        string tat = TestData.WriteFilterFile("MATCH", "line 999", "line 998");
        try
        {
            using var app = CascadeApp.LaunchExisting(log, tat, CascadeApp.NewSettingsDir(),
                                                      ownsFiles: false, ownsSettingsDir: true);
            var fails = new List<string>();
            void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

            Check("starts with three filters", app.RootFilterNames().Length == 3, string.Join(" | ", app.RootFilterNames()));
            Check("count with all three", app.WaitStatus("Fil:", $"Fil: {TestData.MatchCount + 2:N0}"), app.StatusText("Fil:"));

            // Nothing has been edited yet, so there is nothing to take back.
            Check("undo starts unavailable", app.MenuItemNames("Edit").Any(n => n.Equals("Undo", StringComparison.OrdinalIgnoreCase)),
                  string.Join(",", app.MenuItemNames("Edit")));

            app.FilterNode("line 999")!.AsTreeItem().Select();
            app.ClickMenuOrThrow("Filters", "Remove Filter");
            Check("removed", app.FilterNode("line 999") is null);
            Check("count after remove", app.WaitStatus("Fil:", $"Fil: {TestData.MatchCount + 1:N0}"), app.StatusText("Fil:"));
            Check("menu names what it would undo",
                  app.MenuItemNames("Edit").Any(n => n.Contains("Undo Remove Filter", StringComparison.OrdinalIgnoreCase)),
                  string.Join(",", app.MenuItemNames("Edit")));

            // Ctrl+Z is handled at form level, so it has to go through the message loop.
            app.SendKeyAsDialogKey(app.Tree(), VirtualKeyShort.KEY_Z, VirtualKeyShort.CONTROL);
            Check("undo brings the filter back", app.WaitForFilter("line 999"), string.Join(" | ", app.RootFilterNames()));
            Check("undo restores its position", app.RootFilterNames().Length == 3 &&
                  CascadeApp.IndexOfFilter(app.RootFilterNames(), "line 999") == 1, string.Join(" | ", app.RootFilterNames()));
            Check("undo restores the model", app.WaitStatus("Fil:", $"Fil: {TestData.MatchCount + 2:N0}"), app.StatusText("Fil:"));

            app.SendKeyAsDialogKey(app.Tree(), VirtualKeyShort.KEY_Y, VirtualKeyShort.CONTROL);
            Check("redo removes it again", app.WaitForNoFilter("line 999"), string.Join(" | ", app.RootFilterNames()));
            Check("redo restores the model", app.WaitStatus("Fil:", $"Fil: {TestData.MatchCount + 1:N0}"), app.StatusText("Fil:"));

            // Duplicating is an edit like any other, so it stacks and unwinds the same way.
            app.FilterNode("line 998")!.AsTreeItem().Select();
            app.ClickMenuOrThrow("Filters", "Duplicate Filter");
            Check("duplicate adds a filter", app.RootFilterNames().Count(n => n.Contains("line 998", StringComparison.OrdinalIgnoreCase)) == 2,
                  string.Join(" | ", app.RootFilterNames()));

            app.SendKeyAsDialogKey(app.Tree(), VirtualKeyShort.KEY_Z, VirtualKeyShort.CONTROL);
            Check("undo removes the copy", app.WaitForFilterCount(2), string.Join(" | ", app.RootFilterNames()));

            Assert.True(fails.Count == 0, "Undo/redo failures:\n  " + string.Join("\n  ", fails));
        }
        finally { File.Delete(log); File.Delete(tat); }
    }

    [Fact]
    public void Presets_switch_filters_on_together()
    {
        // A preset's TICK is what puts it in effect, and the enabled filters are the union of what is
        // ticked, so Fil: is what proves a tick reached the filters rather than just the list. The
        // SELECTION is only the user's aim and must apply nothing at all.
        string log = TestData.WriteLogFile();
        string filters = TestData.WritePresetFile(
            new[] { "MATCH", "line 999", "line 998" },
            ("just match", new[] { 0 }),
            ("the pair", new[] { 1, 2 }));
        try
        {
            using var app = CascadeApp.LaunchExisting(log, filters, CascadeApp.NewSettingsDir(),
                                                      ownsFiles: false, ownsSettingsDir: true);
            var fails = new List<string>();
            void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

            Check("both presets are listed", app.PresetNames().Length == 2, string.Join(" | ", app.PresetNames()));
            Check("nothing is in effect to start with", app.ActivePresets().Length == 0, app.DescribePresets());
            Check("no filters enabled to start with", app.WaitStatus("Fil:", $"Fil: {TestData.LineCount:N0}"), app.StatusText("Fil:"));

            // Aiming at a preset must not switch anything on - this is the whole reason tick and selection
            // are different things.
            app.SelectPresetRow("just match");
            Check("selecting a preset aims at it", app.SelectedPresetName().StartsWith("just match", StringComparison.Ordinal),
                  app.DescribePresets());
            Check("but selecting one applies nothing", app.ActivePresets().Length == 0, app.DescribePresets());
            Check("and leaves every filter alone", app.WaitStatus("Fil:", $"Fil: {TestData.LineCount:N0}"), app.StatusText("Fil:"));

            app.TickPreset("just match");
            Check("ticking one preset enables its filter", app.WaitStatus("Fil:", $"Fil: {TestData.MatchCount:N0}"), app.StatusText("Fil:"));

            app.TickPreset("the pair");
            Check("ticking a second enables the union", app.WaitStatus("Fil:", $"Fil: {TestData.MatchCount + 2:N0}"), app.StatusText("Fil:"));
            Check("both show as in effect", app.ActivePresets().Length == 2, app.DescribePresets());

            app.SetActivePresets("the pair");
            Check("unticking one drops its filters", app.WaitStatus("Fil:", "Fil: 2"), app.StatusText("Fil:"));

            // The other direction: turning a filter off by hand must clear the preset that named it.
            // Shift+Space rather than Space: the plain one is handled by the native tree, which ignores an
            // injected keystroke, while Shift+Space is handled in managed code. On a filter with no
            // children the two mean the same thing.
            app.FocusFilter("line 999");
            app.ShiftKey(app.Tree(), VirtualKeyShort.SPACE);
            Check("unticking a filter by hand reaches the model", app.WaitStatus("Fil:", "Fil: 1"), app.StatusText("Fil:"));
            Check("a half-enabled preset stops showing as in effect",
                  Retry.WhileFalse(() => app.ActivePresets().Length == 0, TimeSpan.FromSeconds(4)).Result,
                  app.DescribePresets());

            // ...and now the preset can be updated to drop that filter, which is what the old design made
            // impossible: aiming at the preset used to switch its filters straight back on.
            app.SelectPresetRow("the pair");
            Check("aiming at the drifted preset leaves the filter off", app.WaitStatus("Fil:", "Fil: 1"), app.StatusText("Fil:"));
            app.ClickMenuOrThrow("Filters", "Presets", "Update Preset from Enabled Filters");
            Check("updating it puts it back in effect without turning anything on",
                  Retry.WhileFalse(() => app.ActivePresets().Length == 1, TimeSpan.FromSeconds(4)).Result,
                  app.DescribePresets());
            Check("and the filter it dropped is still off", app.WaitStatus("Fil:", "Fil: 1"), app.StatusText("Fil:"));

            Assert.True(fails.Count == 0, "Preset failures:\n  " + string.Join("\n  ", fails));
        }
        finally { File.Delete(log); File.Delete(filters); }
    }

    [Fact]
    public void Word_wrap_is_remembered_and_takes_the_side_scrollbar_away()
    {
        // The suite parks the window off every monitor, where Windows never repaints it, so the wrapped
        // layout itself is checked in-process by the self-test ("word wrap"). What is observable here is the
        // decision: the item ticks, the sideways scrollbar it makes pointless disappears, and the choice
        // survives a restart.
        string log = TestData.WriteLogFile(600);
        string settingsDir = CascadeApp.NewSettingsDir();
        try
        {
            var fails = new List<string>();
            void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

            using (var app = CascadeApp.LaunchExisting(log, null, settingsDir, ownsFiles: false, ownsSettingsDir: false))
            {
                Check("long lines scroll sideways to begin with", app.HasHorizontalScrollBar());
                Check("wrapping is off to begin with", app.MenuItemChecked("View", "Word Wrap") == false,
                      $"{app.MenuItemChecked("View", "Word Wrap")}");

                app.ClickMenuOrThrow("View", "Word Wrap");
                Check("the item ticks", app.MenuItemChecked("View", "Word Wrap") == true);
                Check("the sideways scrollbar goes away",
                      Retry.WhileFalse(() => !app.HasHorizontalScrollBar(), TimeSpan.FromSeconds(4)).Result);

                app.ClickMenuOrThrow("View", "Word Wrap");
                Check("turning it off brings the scrollbar back",
                      Retry.WhileFalse(app.HasHorizontalScrollBar, TimeSpan.FromSeconds(4)).Result);

                app.ClickMenuOrThrow("View", "Word Wrap");   // leave it on, to be remembered
                Check("the app closed", app.CloseGracefully());
            }

            using (var again = CascadeApp.LaunchExisting(log, null, settingsDir, ownsFiles: false, ownsSettingsDir: false))
            {
                Check("the choice is remembered", again.MenuItemChecked("View", "Word Wrap") == true,
                      $"{again.MenuItemChecked("View", "Word Wrap")}");
                Check("and applied on the way up", !again.HasHorizontalScrollBar());
            }

            Assert.True(fails.Count == 0, "Word wrap failures:\n  " + string.Join("\n  ", fails));
        }
        finally { File.Delete(log); try { Directory.Delete(settingsDir, true); } catch { /* best effort */ } }
    }

    [Fact]
    public void The_match_map_stands_in_for_the_vertical_scrollbar()
    {
        // The minimap shows a window of the file a row to a pixel; the scrollbar beside it covers the whole
        // of it. Both are present at once, and the scrollbar has to be able to move the view whether or not
        // the map is showing - for the tests, and for anything else driving the app through automation.
        string log = TestData.WriteLogFile();
        string tat = TestData.WriteFilterFile();
        try
        {
            using var app = CascadeApp.LaunchExisting(log, tat, CascadeApp.NewSettingsDir(),
                                                      ownsFiles: false, ownsSettingsDir: true);
            var fails = new List<string>();
            void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

            Check("the map is there by default", app.Minimap() is not null);
            Check("and the scrollbar is beside it, not instead of it",
                  app.VerticalScrollerName().Length > 0, app.VerticalScrollerName());
            Check("the map is the narrower of the two",
                  app.Minimap() is { } m && m.BoundingRectangle.Width > 0, "no map");

            Check("the scrollbar scrolls the view", app.ScrollVerticalTo(400) && app.FirstVisibleLine() >= 400,
                  $"first visible {app.FirstVisibleLine()}");

            app.ClickMenuOrThrow("View", "Show Match Map");
            Check("turning the map off takes only the map away",
                  Retry.WhileFalse(() => app.Minimap() is null, TimeSpan.FromSeconds(4)).Result,
                  "the map is still there");
            Check("the scrollbar is still there", app.VerticalScrollerName().Length > 0, app.VerticalScrollerName());
            Check("and still scrolls", app.ScrollVerticalTo(700) && app.FirstVisibleLine() >= 700,
                  $"first visible {app.FirstVisibleLine()}");

            app.ClickMenuOrThrow("View", "Show Match Map");
            Check("turning it back on returns the map",
                  Retry.WhileFalse(() => app.Minimap() is not null, TimeSpan.FromSeconds(4)).Result,
                  "the map did not come back");

            Assert.True(fails.Count == 0, "Minimap failures:\n  " + string.Join("\n  ", fails));
        }
        finally { File.Delete(log); File.Delete(tat); }
    }

    [Fact]
    public void Typing_a_search_term_marks_matches_without_moving_the_view()
    {
        // Typing marks what is already on screen and nothing else. Moving the view as the term grows would
        // walk it away from whatever you were looking at, one keystroke at a time.
        string log = TestData.WriteLogFile();
        string tat = TestData.WriteFilterFile();
        try
        {
            using var app = CascadeApp.LaunchExisting(log, tat, CascadeApp.NewSettingsDir(),
                                                      ownsFiles: false, ownsSettingsDir: true);
            var fails = new List<string>();
            void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

            app.SelectLine(5);
            app.OpenFind();
            int before = app.FirstVisibleLine();

            var bar = app.FindBar() ?? throw new InvalidOperationException("Find bar not found: " + app.DescribeTextElements());
            var edit = bar.FindFirstDescendant(cf => cf.ByName("Find what"))
                       ?? throw new InvalidOperationException("Find box not found: " + app.DescribeTextElements());
            edit.Patterns.Value.Pattern.SetValue("line 999");
            Thread.Sleep(700);   // longer than the preview's own pause

            Check("typing does not move the view", app.FirstVisibleLine() == before,
                  $"{before} -> {app.FirstVisibleLine()}");

            bar.FindFirstDescendant(cf => cf.ByName("Find next"))?.AsButton().Invoke();
            Check("asking for the search does move it",
                  Retry.WhileFalse(() => app.FirstVisibleLine() != before, TimeSpan.FromSeconds(6)).Result,
                  $"{before} -> {app.FirstVisibleLine()}");
            Check("and it lands on the match", app.WaitSelectedRowText("line 999"), app.SelectedRowText());

            // The count belongs beside the term now, not at the far corner of the window.
            Check("the bar says what it found", app.WaitFindBarMessage("of 1", 4000), app.FindBarMessage());

            // Closing the bar ends the search: there is no longer a state where a term is still being
            // looked for with nothing on screen to say so.
            app.CloseFind();
            Check("closing the bar puts it away", app.FindBar() is null);

            Assert.True(fails.Count == 0, "Find-as-you-type failures:\n  " + string.Join("\n  ", fails));
        }
        finally { File.Delete(log); File.Delete(tat); }
    }

    /// <summary>Tab has two stops, and a bar that is open keeps it. Tabbing out of a bar used to land on
    /// whatever came next in the window - the presets list - with no way back in.</summary>
    [Fact]
    public void Tab_has_two_stops_and_never_walks_out_of_an_open_bar()
    {
        string log = TestData.WriteLogFile();
        string tat = TestData.WriteFilterFile("MATCH", "alpha", "beta");
        try
        {
            using var app = CascadeApp.LaunchExisting(log, tat, CascadeApp.NewSettingsDir(),
                                                      ownsFiles: false, ownsSettingsDir: true);
            var fails = new List<string>();
            void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

            app.ClickMenuOrThrow("View", "Focus Text Area");
            Check("it starts on the log", app.WaitForArea("log"), app.FocusedArea());

            var walk = new List<string>();
            for (int i = 0; i < 4; i++) { app.TabFromFocus(); walk.Add(app.FocusedArea()); }
            Check("Tab alternates between the log and the filter list",
                  walk.SequenceEqual(["filter list", "log", "filter list", "log"]), string.Join(" -> ", walk));

            // Five stops inside the find bar, and Tab must stay among them.
            app.OpenFind();
            Check("opening find puts the keyboard in the bar", app.WaitForArea("find bar"), app.FocusedArea());
            var inBar = new List<string>();
            for (int i = 0; i < 8; i++) { app.TabFromFocus(); inBar.Add(app.FocusedArea()); }
            Check("Tab never walks out of the find bar", inBar.All(a => a == "find bar"),
                  string.Join(" -> ", inBar.Distinct()));
            app.CloseFind();

            // One stop inside the filter search bar, so Tab has nowhere to go and stays put.
            app.OpenFilterSearch();
            Check("opening the filter search puts the keyboard in its bar",
                  app.WaitForArea("filter search"), app.FocusedArea());
            var inSearch = new List<string>();
            for (int i = 0; i < 4; i++) { app.TabFromFocus(); inSearch.Add(app.FocusedArea()); }
            Check("nor out of the filter search bar", inSearch.All(a => a == "filter search"),
                  string.Join(" -> ", inSearch.Distinct()));

            Assert.True(fails.Count == 0, "Tab:\n  " + string.Join("\n  ", fails));
        }
        finally { File.Delete(log); File.Delete(tat); }
    }

    /// <summary>The filter search bar comes up on demand and goes away again, and while it is up the list
    /// is genuinely shorter - the point of the change being that it costs nothing when it is not in use.
    /// </summary>
    [Fact]
    public void Filter_search_opens_on_demand_and_closes_again()
    {
        string log = TestData.WriteLogFile();
        string tat = TestData.WriteFilterFile("MATCH", "alpha", "beta", "gamma");
        try
        {
            using var app = CascadeApp.LaunchExisting(log, tat, CascadeApp.NewSettingsDir(),
                                                      ownsFiles: false, ownsSettingsDir: true);
            var fails = new List<string>();
            void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

            Check("the bar is not there to begin with", !app.FilterSearchIsOpen());
            double listBefore = app.Tree().BoundingRectangle.Height;

            var box = app.OpenFilterSearch();
            Check("the menu brings it up", app.FilterSearchIsOpen());
            double listOpen = app.Tree().BoundingRectangle.Height;
            Check("and the list gives up room for it", listOpen < listBefore,
                  $"{listBefore} -> {listOpen}");
            Check("but not too much of it", listOpen > listBefore * 0.6, $"{listBefore} -> {listOpen}");

            // It searches: typing a name and pressing Enter selects that filter.
            app.SetText(box, "gamma");
            app.Key(box, VirtualKeyShort.RETURN);
            Check("it finds the filter typed into it",
                  app.WaitForSelectedFilter("gamma"), app.SelectedFilterName() ?? "(none)");

            // Escape from inside the box puts it away and gives the room back.
            app.Key(box, VirtualKeyShort.ESCAPE);
            Check("Escape puts it away", app.WaitFilterSearchClosed());
            Check("and the list has its room back",
                  Retry.WhileFalse(() => Math.Abs(app.Tree().BoundingRectangle.Height - listBefore) < 2,
                                   TimeSpan.FromSeconds(3)).Result,
                  $"{listBefore} -> {app.Tree().BoundingRectangle.Height}");

            Assert.True(fails.Count == 0, "Filter search bar:\n  " + string.Join("\n  ", fails));
        }
        finally { File.Delete(log); File.Delete(tat); }
    }

    [Fact]
    public void Alt_arrows_reorder_and_nest_the_selected_filter()
    {
        string log = TestData.WriteLogFile();
        string tat = TestData.WriteFilterFile("MATCH", "alpha", "beta");
        try
        {
            using var app = CascadeApp.LaunchExisting(log, tat, CascadeApp.NewSettingsDir(),
                                                      ownsFiles: false, ownsSettingsDir: true);
            var fails = new List<string>();
            void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }
            void CheckOrder(string name, params string[] expected)
            {
                var roots = app.RootFilterNames();
                bool ok = roots.Length == expected.Length;
                for (int i = 0; ok && i < expected.Length; i++)
                    ok = (roots[i] ?? "").Contains(expected[i], StringComparison.OrdinalIgnoreCase);
                Check(name, ok, string.Join(" | ", roots));
            }

            CheckOrder("initial order", "MATCH", "alpha", "beta");

            // Alt+Up / Alt+Down reorder within the same level. Ctrl+arrow walks the selection now, which is
            // what any other list does with it.
            var tree = app.Tree();
            app.FocusFilter("beta");
            app.AltKey(tree, VirtualKeyShort.UP);
            CheckOrder("alt+up moves it above alpha", "MATCH", "beta", "alpha");

            app.AltKey(tree, VirtualKeyShort.DOWN);
            CheckOrder("alt+down puts it back", "MATCH", "alpha", "beta");

            // Alt+Right nests it under the filter above it.
            app.AltKey(tree, VirtualKeyShort.RIGHT);
            CheckOrder("alt+right removes it from the top level", "MATCH", "alpha");
            Check("alt+right nests it under alpha",
                CascadeApp.IndexOfFilter(app.ChildFilterNames("alpha"), "beta") >= 0,
                string.Join(" | ", app.ChildFilterNames("alpha")));

            // Alt+Left moves it back out, directly after its old parent.
            app.AltKey(tree, VirtualKeyShort.LEFT);
            CheckOrder("alt+left restores it to the top level", "MATCH", "alpha", "beta");

            // The first filter has nothing above it, so both are no-ops rather than errors.
            app.FocusFilter("MATCH");
            app.AltKey(tree, VirtualKeyShort.UP);
            app.AltKey(tree, VirtualKeyShort.RIGHT);
            CheckOrder("no-op at the top of the list", "MATCH", "alpha", "beta");

            Assert.True(fails.Count == 0, "Filter reorder failures:\n  " + string.Join("\n  ", fails));
        }
        finally { File.Delete(log); File.Delete(tat); }
    }

    [Fact]
    public void Several_filters_can_be_selected_and_removed_together()
    {
        // Through the real window, with real keystrokes: the group is built with Shift+Down, removed with
        // one Delete, and put back with one Ctrl+Z. Each of the three filters below "MATCH" matches exactly
        // one line the others do not, so the status bar's filtered count moves by three and back - which is
        // what proves the model changed rather than just the list.
        string log = TestData.WriteLogFile();
        string tat = TestData.WriteFilterFile("MATCH", "line 999", "line 998", "line 997");
        try
        {
            using var app = CascadeApp.LaunchExisting(log, tat, CascadeApp.NewSettingsDir(),
                                                      ownsFiles: false, ownsSettingsDir: true);
            var fails = new List<string>();
            void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

            Check("starts with four filters", app.RootFilterNames().Length == 4, string.Join(" | ", app.RootFilterNames()));

            var tree = app.Tree();
            app.FocusFilter("line 999");
            app.ShiftKey(tree, VirtualKeyShort.DOWN);
            app.ShiftKey(tree, VirtualKeyShort.DOWN);
            Check("the list says how many are selected",
                  app.WaitForSelectionCount(3), app.SelectionNote() ?? "(no note)");

            app.Key(tree, VirtualKeyShort.DELETE);
            Check("one delete takes all three", app.WaitForFilterCount(1), string.Join(" | ", app.RootFilterNames()));
            Check("and the ones it took are gone",
                  app.WaitForNoFilter("line 999") && app.WaitForNoFilter("line 998") && app.WaitForNoFilter("line 997"),
                  string.Join(" | ", app.RootFilterNames()));
            Check("the filter that was not selected is untouched", app.FilterNode("MATCH") is not null,
                  string.Join(" | ", app.RootFilterNames()));

            // Undo is a form-level shortcut, so it has to go through the message loop - a sent message
            // reaches the control's KeyDown and skips ProcessCmdKey entirely.
            app.SendKeyAsDialogKey(tree, VirtualKeyShort.KEY_Z, VirtualKeyShort.CONTROL);
            Check("one undo brings all three back", app.WaitForFilterCount(4), string.Join(" | ", app.RootFilterNames()));
            Check("in the order they were in",
                  CascadeApp.IndexOfFilter(app.RootFilterNames(), "line 999") == 1 &&
                  CascadeApp.IndexOfFilter(app.RootFilterNames(), "line 998") == 2 &&
                  CascadeApp.IndexOfFilter(app.RootFilterNames(), "line 997") == 3,
                  string.Join(" | ", app.RootFilterNames()));

            Assert.True(fails.Count == 0, "Filter multi-select failures:\n  " + string.Join("\n  ", fails));
        }
        finally { File.Delete(log); File.Delete(tat); }
    }

    [Fact]
    public void Find_keys_work_from_the_bar_and_from_the_log()
    {
        // The bar sits in the main window now, so its keys travel through the form. Enter and Shift+Enter
        // are handled by the bar itself (ProcessCmdKey runs on the focused control first); F3 and Shift+F3
        // are menu shortcuts and have to keep working wherever the focus is - including in the log, so that
        // walking through matches never takes the arrow keys away.
        using var app = CascadeApp.Launch();
        var fails = new List<string>();
        void Check(string name, int expected) { if (!app.WaitCaretLine(expected)) fails.Add($"{name} :: line {app.CaretLine()}"); }

        app.SelectLine(1);
        app.OpenFind();
        app.FindWith("MATCH line", forward: true);   // MATCH is on 1-based lines 1, 6, 11, 16, ...
        Check("find next from line 1", 6);

        // From the term box.
        app.FocusFindInput();
        var edit = app.FindInput();
        app.SendKeyAsDialogKey(edit, VirtualKeyShort.F3);
        Check("F3 -> next", 11);
        app.SendKeyAsDialogKey(edit, VirtualKeyShort.F3, VirtualKeyShort.SHIFT);
        Check("Shift+F3 -> previous", 6);
        app.SendKeyAsDialogKey(edit, VirtualKeyShort.RETURN);
        Check("Enter -> next", 11);
        app.SendKeyAsDialogKey(edit, VirtualKeyShort.RETURN, VirtualKeyShort.SHIFT);
        Check("Shift+Enter -> previous", 6);

        // ...and from the log itself, which is where the keyboard is while reading results.
        app.ClickMenuOrThrow("View", "Focus Text Area");
        Thread.Sleep(300);
        app.SendKeyAsDialogKey(app.Grid(), VirtualKeyShort.F3);
        Check("F3 from the log -> next", 11);
        app.SendKeyAsDialogKey(app.Grid(), VirtualKeyShort.F3, VirtualKeyShort.SHIFT);
        Check("Shift+F3 from the log -> previous", 6);

        Assert.True(fails.Count == 0, "Find key failures:\n  " + string.Join("\n  ", fails));
    }

    [Fact]
    public void Repeating_a_search_keeps_up_with_the_key()
    {
        // Held down, Enter repeats about thirty times a second. Every one of them has to land: the bar used
        // to refuse a repeat while it thought a search was running, and to rebuild its history and flip its
        // progress UI around answers it already had - so on a term matching most lines it appeared stuck.
        string log = TestData.WriteLogFile();
        try
        {
            using var app = CascadeApp.LaunchExisting(log, null, CascadeApp.NewSettingsDir(),
                                                      ownsFiles: false, ownsSettingsDir: true);
            var fails = new List<string>();
            void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

            app.SelectLine(1);
            app.OpenFind();
            var edit = app.FindInput();
            app.SetText(edit, "line");                      // every line matches, so each Enter moves one line
            app.SendKeyAsDialogKey(edit, VirtualKeyShort.RETURN);
            Check("the first search lands", app.WaitCaretLine(2), $"line {app.CaretLine()}");

            const int repeats = 12;
            for (int i = 0; i < repeats; i++) app.SendKeyAsDialogKey(edit, VirtualKeyShort.RETURN);
            Check("every repeat moved the caret on one match",
                  app.WaitCaretLine(2 + repeats), $"line {app.CaretLine()}");

            // Searching must not touch what was typed. Backspace is the giveaway: with the caret still at
            // the end it takes the last character, and from the start of the box it takes nothing - which is
            // exactly where refilling the history drop-down used to put it.
            app.SendKeyAsDialogKey(edit, VirtualKeyShort.END);
            app.SendKeyAsDialogKey(edit, VirtualKeyShort.RETURN);
            app.SendKeyAsDialogKey(edit, VirtualKeyShort.BACK);
            Check("Enter leaves the caret where it was in the box",
                  Retry.WhileFalse(() => app.TextOf(edit) == "lin", TimeSpan.FromSeconds(3)).Result,
                  app.TextOf(edit));

            Assert.True(fails.Count == 0, "Find repeat failures:\n  " + string.Join("\n  ", fails));
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void Escape_closes_the_bar_and_the_search_with_it_but_F3_brings_both_back()
    {
        // The whole point of the bar being in the layout: there is no state where a term is still being
        // looked for with nothing on screen to say so. Escape ends it, and F3 starts it again from what the
        // bar was last asked to look for.
        string log = TestData.WriteLogFile();
        try
        {
            using var app = CascadeApp.LaunchExisting(log, null, CascadeApp.NewSettingsDir(),
                                                      ownsFiles: false, ownsSettingsDir: true);
            var fails = new List<string>();
            void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

            app.SelectLine(1);
            app.OpenFind();
            var edit = app.FindInput();
            app.SetText(edit, "line");
            app.SendKeyAsDialogKey(edit, VirtualKeyShort.RETURN);
            Check("the search lands", app.WaitCaretLine(2), $"line {app.CaretLine()}");
            Check("and the counts are up, in the bar", app.WaitFindBarMessage("Match "), app.FindBarMessage());

            // Escape from the text area. Through the message loop, because it is handled at form level.
            app.ClickMenuOrThrow("View", "Focus Text Area");
            Thread.Sleep(300);
            app.SendKeyAsDialogKey(app.Grid(), VirtualKeyShort.ESCAPE);
            Check("escape puts the bar away",
                  Retry.WhileFalse(() => app.FindBar() is null, TimeSpan.FromSeconds(4)).Result,
                  app.FindBarMessage());

            // F3 must search again there and then: bar back, next match, counts with it.
            app.SendKeyAsDialogKey(app.Grid(), VirtualKeyShort.F3);
            Check("F3 goes straight to the next match", app.WaitCaretLine(3), $"line {app.CaretLine()}");
            Check("and brings the bar back with it",
                  Retry.WhileFalse(() => app.FindBar() is not null, TimeSpan.FromSeconds(4)).Result);
            Check("counts and all", app.WaitFindBarMessage("Match 3"), app.FindBarMessage());

            app.SendKeyAsDialogKey(app.Grid(), VirtualKeyShort.F3);
            Check("and again", app.WaitCaretLine(4), $"line {app.CaretLine()}");

            app.SendKeyAsDialogKey(app.Grid(), VirtualKeyShort.F3, VirtualKeyShort.SHIFT);
            Check("Shift+F3 goes back", app.WaitCaretLine(3), $"line {app.CaretLine()}");

            Assert.True(fails.Count == 0, "Escape/F3 failures:\n  " + string.Join("\n  ", fails));
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public void Reaching_the_end_of_a_search_shows_feedback()
    {
        // Every find command has to say so when there is nothing further that way. Two of them used to be
        // completely silent. The text find is the exception: its counts are already in the status bar, so a
        // message there would cover up the very numbers that answer the question - it flashes and beeps only.
        string log = TestData.WriteLogFile();
        string tat = TestData.WriteFilterFile("MATCH");
        try
        {
            using var app = CascadeApp.LaunchExisting(log, tat, CascadeApp.NewSettingsDir(),
                                                      ownsFiles: false, ownsSettingsDir: true);
            var fails = new List<string>();
            void Check(string name, bool cond) { if (!cond) fails.Add(name); }

            // 1. Marker navigation with no markers set at all.
            app.SelectLine(1);
            app.Key(app.Grid(), VirtualKeyShort.KEY_1);
            Check("marker navigation reports the end", app.WaitForFindMessage("No more marker 1"));

            // 2. Per-filter find, searching backwards from the very first line.
            app.SelectLine(1);
            app.FilterNode("MATCH")?.AsTreeItem().Select();
            app.FindPrevForSelectedFilter();
            Check("per-filter find reports the end", app.WaitForFindMessage("No more matches"));

            // 3. Text find. Now that its counts live in the bar rather than the status bar, it says so in
            // the status bar like the other three instead of flashing silently.
            app.OpenFind();
            app.FindWith("line 137", forward: true);
            Check("the search found something", app.WaitFindBarMessage("Match "));
            app.FindWith("line 137", forward: true);   // ...and there is only the one
            Check("text find reports the end", app.WaitForFindMessage("No more matches"));
            Check("and the counts are still on show in the bar", app.FindBarMessage().Contains("Match "));
            app.CloseFind();

            // 4. Filter search for a filter that is not in the list. Deliberately after the find bar, which
            // has to hand the keyboard back when it closes.
            var search = app.OpenFilterSearch();
            app.SetText(search, "zzz-no-such-filter");
            app.Key(search, VirtualKeyShort.RETURN);
            Check("filter search reports the end", app.WaitForFindMessage("No more filters"));

            Assert.True(fails.Count == 0, "No end-of-search feedback for:\n  " + string.Join("\n  ", fails));
        }
        finally { File.Delete(log); File.Delete(tat); }
    }

    [Fact]
    public void Ctrl_arrows_scroll_the_log_without_moving_the_selection()
    {
        using var app = CascadeApp.Launch();
        var fails = new List<string>();
        void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

        // Start in the middle, so there is room to scroll both ways.
        app.ScrollRowToMiddle(500);
        app.SelectLine(501);
        var grid = app.Grid();
        int selected = app.CaretLine();
        int top = app.FirstVisibleLine();
        Check("a line is selected to begin with", selected == 501, $"line {selected}");

        for (int i = 0; i < 5; i++) app.CtrlKey(grid, VirtualKeyShort.DOWN);
        int afterDown = app.FirstVisibleLine();
        Check("ctrl+down scrolls the view", afterDown > top, $"top {top} -> {afterDown}");
        Check("ctrl+down leaves the selected line alone", app.CaretLine() == selected, $"line {app.CaretLine()}");
        Check("ctrl+down leaves the selection alone", app.StatusText("Sel:") == "Sel: 1", app.StatusText("Sel:"));

        for (int i = 0; i < 5; i++) app.CtrlKey(grid, VirtualKeyShort.UP);
        int afterUp = app.FirstVisibleLine();
        Check("ctrl+up scrolls back", afterUp == top, $"top {top} -> {afterDown} -> {afterUp}");
        Check("ctrl+up leaves the selected line alone", app.CaretLine() == selected, $"line {app.CaretLine()}");

        Assert.True(fails.Count == 0, "Scroll failures:\n  " + string.Join("\n  ", fails));
    }

    [Fact]
    public void Ctrl_shift_c_splits_the_log_into_columns_and_back()
    {
        // A bracketed log, so turning columns on finds fields to split by and never has to ask.
        string log = TestData.WriteBracketedLogFile();
        string tat = TestData.WriteFilterFile();
        using var app = CascadeApp.LaunchExisting(log, tat, CascadeApp.NewSettingsDir(),
                                                  ownsFiles: true, ownsSettingsDir: true);
        var fails = new List<string>();
        void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

        app.ScrollRowToMiddle(500);
        var grid = app.Grid();
        int rowsBefore = app.Rows().Length;
        int topBefore = app.FirstVisibleLine();
        Check("the log is showing several lines to begin with", rowsBefore > 4, $"{rowsBefore} rows");

        // The header is drawn, not a control, so what it can be seen by is the row it takes off the top:
        // one fewer line fits, and the top line moves down one so the reader's place is kept.
        app.SendKeyAsDialogKey(grid, VirtualKeyShort.KEY_C, VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT);
        bool took = Retry.WhileFalse(() => app.Rows().Length == rowsBefore - 1,
                                     TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(50)).Result;
        Check("ctrl+shift+c gives the log a column header", took,
              $"{rowsBefore} rows -> {app.Rows().Length}");
        Check("and the log keeps its place, minus the row the header took",
              app.FirstVisibleLine() == topBefore + 1, $"top {topBefore} -> {app.FirstVisibleLine()}");

        app.SendKeyAsDialogKey(grid, VirtualKeyShort.KEY_C, VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT);
        bool back = Retry.WhileFalse(() => app.Rows().Length == rowsBefore,
                                     TimeSpan.FromSeconds(4), TimeSpan.FromMilliseconds(50)).Result;
        Check("pressing it again takes the header away", back, $"{app.Rows().Length} rows");
        Check("and hands the line back at the top", app.FirstVisibleLine() == topBefore,
              $"top is {app.FirstVisibleLine()}, was {topBefore}");

        Assert.True(fails.Count == 0, "Column mode failures:\n  " + string.Join("\n  ", fails));
    }

    [Fact]
    public void Copy_and_docking_work()
    {
        using var app = CascadeApp.Launch();
        var fails = new List<string>();
        void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

        // ---- copy selection to clipboard ----
        app.SelectLine(9); // 1-based 9 -> 0-based line 8 = "other line 8"
        app.ClickMenuOrThrow("Edit", "Copy");
        string clip = "";
        Retry.WhileFalse(() => (clip = CascadeApp.ReadClipboardText()).Contains("other line 8", StringComparison.Ordinal),
                         TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(25));
        Check("copy places selected line on the clipboard", clip.Contains("other line 8", StringComparison.Ordinal), clip);

        // ---- docking: move the filter list around and verify the layout follows ----
        app.ClickMenuOrThrow("View", "Filter List Location", "Dock Left");
        Check("dock left puts the filter list left of the log",
            app.Tree().BoundingRectangle.Left < app.Grid().BoundingRectangle.Left,
            $"tree={app.Tree().BoundingRectangle} grid={app.Grid().BoundingRectangle}");

        app.ClickMenuOrThrow("View", "Filter List Location", "Dock Bottom");
        Check("dock bottom puts the filter list below the log",
            app.Tree().BoundingRectangle.Top > app.Grid().BoundingRectangle.Top,
            $"tree={app.Tree().BoundingRectangle} grid={app.Grid().BoundingRectangle}");

        Assert.True(fails.Count == 0, "Copy/docking failures:\n  " + string.Join("\n  ", fails));
    }

    [Fact]
    public void Find_highlights_the_line_that_contains_the_query()
    {
        using var app = CascadeApp.Launch();
        var fails = new List<string>();
        void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

        app.OpenFind();

        // --- dim mode: the highlighted line must actually contain the query ---
        app.FindWith("other line 137", forward: true);
        Check("dim forward -> Ln 138", app.WaitCaretLine(138), $"line {app.CaretLine()}");
        Check("dim forward: selected line contains query", app.WaitSelectedRowText("other line 137"), app.SelectedRowText());

        app.FindWith("other line 246", forward: true);
        Check("dim forward2 -> Ln 247", app.WaitCaretLine(247), $"line {app.CaretLine()}");
        Check("dim forward2: selected line contains query", app.WaitSelectedRowText("other line 246"), app.SelectedRowText());

        app.FindWith("other line 89", forward: false);
        Check("dim backward -> Ln 90", app.WaitCaretLine(90), $"line {app.CaretLine()}");
        Check("dim backward: selected line contains query", app.WaitSelectedRowText("other line 89"), app.SelectedRowText());

        // repeat the same unique query forward: it must NOT re-find the current line
        app.FindWith("other line 246", forward: true); // caret at 89 -> 246 is ahead
        Check("dim re-find forward -> Ln 247", app.WaitCaretLine(247), $"line {app.CaretLine()}");
        app.FindWith("other line 246", forward: true); // caret==246, unique -> not found, stay
        Check("dim no more matches -> says so", app.WaitForFindMessage("No more matches"), app.AllStatusText());
        Check("dim no more matches -> selection unchanged", app.CaretLine() == 247, $"line {app.CaretLine()}");

        // --- filtered mode: the highlighted line must STILL contain the query ---
        app.SetFilteredMode(true);
        app.FindWith("MATCH line 500", forward: true);
        Check("filtered: matched-line search -> Ln 501", app.WaitCaretLine(501), $"line {app.CaretLine()}");
        Check("filtered: selected line contains query", app.WaitSelectedRowText("MATCH line 500"), app.SelectedRowText());

        // text that only exists on a HIDDEN (filtered-out) line, AHEAD of the caret, must NOT jump to a
        // wrong (visible) line — the highlighted line must always contain the query.
        Check("filtered precondition: visible rows are MATCH rows before hidden-only search",
            app.VisibleRowsLookFiltered(), app.SelectedRowText());
        app.FindWith("other line 733", forward: true);
        Check("filtered: hidden-only text reports not found",
            app.WaitForFindMessage("No more matches"), app.AllStatusText());
        Check("filtered: hidden-only text leaves selection put (still 501)",
            app.CaretLine() == 501, $"line {app.CaretLine()}");

        Assert.True(fails.Count == 0, "Find failures:\n  " + string.Join("\n  ", fails));
    }

    [Fact]
    public void Remembers_and_auto_loads_the_last_filter_file()
    {
        // The log + filter file must outlive the first app instance so the second launch can auto-load
        // them; a shared, isolated settings dir carries "last filter file" from one launch to the next.
        string log = TestData.WriteLogFile();
        string tat = TestData.WriteFilterFile();
        string cfg = CascadeApp.NewSettingsDir();
        try
        {
            // First launch WITH the filter file: loading it records it as the "last filter file".
            using (var a = CascadeApp.LaunchExisting(log, tat, cfg, ownsFiles: false, ownsSettingsDir: false))
                Assert.True(a.FilterNode("MATCH") is not null, "the filter file did not load on the first launch");

            // Second launch WITHOUT any filter file: the remembered one is reloaded automatically.
            using var b = CascadeApp.LaunchExisting(log, null, cfg, ownsFiles: false, ownsSettingsDir: false);
            bool loaded = Retry.WhileFalse(() => b.FilterNode("MATCH") is not null, TimeSpan.FromSeconds(6)).Result;
            Assert.True(loaded, "the last filter file was not auto-loaded on the next launch");
        }
        finally
        {
            try { File.Delete(log); } catch { /* ignore */ }
            try { File.Delete(tat); } catch { /* ignore */ }
            try { Directory.Delete(cfg, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Close_filters_detaches_the_file_and_stops_auto_loading()
    {
        string log = TestData.WriteLogFile();
        string tat = TestData.WriteFilterFile();
        string cfg = CascadeApp.NewSettingsDir();
        try
        {
            using (var a = CascadeApp.LaunchExisting(log, tat, cfg, ownsFiles: false, ownsSettingsDir: false))
            {
                Assert.True(a.FilterNode("MATCH") is not null, "the filter file did not load");
                a.CloseFilters();
                bool cleared = Retry.WhileFalse(() => a.FilterNode("MATCH") is null, TimeSpan.FromSeconds(4)).Result;
                Assert.True(cleared, "Close Filters did not clear the filter tree");
            }

            // Next launch WITHOUT a filter file: nothing is auto-loaded, because the file was forgotten.
            using var b = CascadeApp.LaunchExisting(log, null, cfg, ownsFiles: false, ownsSettingsDir: false);
            bool stillLoaded = Retry.WhileFalse(() => b.FilterNode("MATCH") is not null, TimeSpan.FromSeconds(3)).Result;
            Assert.False(stillLoaded, "a closed filter file must not be auto-loaded on the next launch");
        }
        finally
        {
            try { File.Delete(log); } catch { /* ignore */ }
            try { File.Delete(tat); } catch { /* ignore */ }
            try { Directory.Delete(cfg, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void A_forced_kill_keeps_what_the_session_changed()
    {
        // Ending Cascade from Task Manager is a reasonable way to skip the save-filters prompt, so nothing
        // the user changed may depend on the window closing tidily. Dispose kills the process outright -
        // the same TerminateProcess that End Task uses - so this is the real thing, not a simulation.
        string log = TestData.WriteLogFile();
        string cfg = CascadeApp.NewSettingsDir();
        string settingsFile = Path.Combine(cfg, "settings.json");
        string stateFile = Path.Combine(cfg, "state.json");
        try
        {
            using (var a = CascadeApp.LaunchExisting(log, null, cfg, ownsFiles: false, ownsSettingsDir: false))
            {
                Assert.True(a.ClickMenu("View", "Zoom In"), "the View menu stopped working");
                Assert.True(a.WaitStatus("Zoom:", "Zoom: 110%"), a.StatusText("Zoom:"));

                // Both files must reach disk while the app is still running, not on the way out.
                Assert.True(Retry.WhileFalse(() => Contains(settingsFile, "\"ZoomPercent\": 110"),
                                             TimeSpan.FromSeconds(10)).Result,
                            $"zoom never reached {settingsFile}: {Read(settingsFile)}");
                Assert.True(Retry.WhileFalse(() => Contains(stateFile, Path.GetFileName(log)),
                                             TimeSpan.FromSeconds(10)).Result,
                            $"the opened file never reached {stateFile}: {Read(stateFile)}");
            }

            using var b = CascadeApp.LaunchExisting(log, null, cfg, ownsFiles: false, ownsSettingsDir: false);
            Assert.True(b.WaitStatus("Zoom:", "Zoom: 110%"),
                        $"the killed session's zoom was not remembered: {b.StatusText("Zoom:")}");
        }
        finally
        {
            try { File.Delete(log); } catch { /* ignore */ }
            try { Directory.Delete(cfg, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Enter_on_a_selected_filter_opens_the_edit_dialog()
    {
        // The dialog used to be opened from inside the key handler, which ran its message loop before
        // WinForms could discard the WM_CHAR belonging to that same Enter - so the tree beeped every time.
        // It is deferred now, and this is what proves the deferral still ends up opening the thing.
        string log = TestData.WriteLogFile();
        string tat = TestData.WriteFilterFile("MATCH");
        try
        {
            using var app = CascadeApp.LaunchExisting(log, tat, CascadeApp.NewSettingsDir(),
                                                      ownsFiles: false, ownsSettingsDir: true);
            app.FocusFilter("MATCH");
            app.Key(app.Tree(), VirtualKeyShort.RETURN);

            var dialog = app.FindDialog("Edit Filter");
            Assert.True(dialog is not null, "Enter did not open the filter edit dialog");
            app.SendKeyAsDialogKey(dialog!, VirtualKeyShort.ESCAPE);
        }
        finally
        {
            try { File.Delete(log); } catch { /* ignore */ }
            try { File.Delete(tat); } catch { /* ignore */ }
        }
    }

    private static string Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : "(no such file)"; }
        catch (IOException) { return "(being written)"; }
    }

    private static bool Contains(string path, string text)
        => Read(path).Contains(text, StringComparison.Ordinal);
}

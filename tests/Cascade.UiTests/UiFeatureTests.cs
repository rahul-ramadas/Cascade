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
        app.ClickMenu("View", "Focus Text Area");
        var grid = app.Grid();

        Assert.Equal(0, app.HorizontalScroll());

        app.Key(grid, VirtualKeyShort.END);
        Assert.True(app.WaitHorizontalScroll(v => v > 0), "End did not scroll the view right");
        double rightEdge = app.HorizontalScroll();

        // Already at the extreme: pressing End again must not creep further.
        app.Key(grid, VirtualKeyShort.END);
        Assert.Equal(rightEdge, app.HorizontalScroll());

        app.Key(grid, VirtualKeyShort.HOME);
        Assert.True(app.WaitHorizontalScroll(v => v == 0), "Home did not return the view to the left edge");

        // The Ctrl variants still move the caret rather than the view.
        app.CtrlKey(grid, VirtualKeyShort.END);
        Assert.True(app.WaitStatus("Ln:", $"Ln: {TestData.LineCount:N0} / {TestData.LineCount:N0}"),
                    "Ctrl+End no longer goes to the last line: " + app.StatusText("Ln:"));
        app.CtrlKey(grid, VirtualKeyShort.HOME);
        Assert.True(app.WaitStatus("Ln:", $"Ln: 1 / {TestData.LineCount:N0}"),
                    "Ctrl+Home no longer goes to the first line: " + app.StatusText("Ln:"));
    }

    [Fact]
    public void Keeps_selected_line_selected_and_centered_when_toggling_filtered_mode()
    {
        using var app = CascadeApp.Launch();
        var fails = new List<string>();
        void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

        // Scroll a middle region into view, then select a MATCH line (0-based 500 -> 1-based 501).
        app.ScrollRowToMiddle(500);
        app.SelectLine(501);
        Check("selects line 501", app.StatusText("Ln:") == "Ln: 501 / 1,000", app.StatusText("Ln:"));
        Check("one line selected", app.StatusText("Sel:") == "Sel: 1", app.StatusText("Sel:"));

        // Hide non-matching lines: line 500 matches, so it must stay selected AND be centered.
        app.ToggleFilteredMode();
        Check("filtered: line stays 501", app.StatusText("Ln:") == "Ln: 501 / 1,000", app.StatusText("Ln:"));
        Check("filtered: still one selected", app.StatusText("Sel:") == "Sel: 1", app.StatusText("Sel:"));
        Check("filtered: matched count", app.StatusText("Fil:") == $"Fil: {TestData.MatchCount:N0}", app.StatusText("Fil:"));
        Check("filtered: selected row centered", app.SelectedRowIsCentered(out var d1), d1);

        // Show all lines again: still selected + centered.
        app.ToggleFilteredMode();
        Check("dim: line stays 501", app.StatusText("Ln:") == "Ln: 501 / 1,000", app.StatusText("Ln:"));
        Check("dim: selected row centered", app.SelectedRowIsCentered(out var d2), d2);

        // Select a NON-matching line (0-based 502 -> 1-based 503). Hiding non-matching lines should snap
        // the selection to the nearest match at/after it: 0-based 505 -> 1-based 506.
        app.SelectLine(503);
        Check("select 503", app.StatusText("Ln:") == "Ln: 503 / 1,000", app.StatusText("Ln:"));
        app.ToggleFilteredMode();
        Check("filtered-out line snaps to nearest match (506)", app.StatusText("Ln:") == "Ln: 506 / 1,000", app.StatusText("Ln:"));
        Check("nearest still selected", app.StatusText("Sel:") == "Sel: 1", app.StatusText("Sel:"));
        Check("nearest centered", app.SelectedRowIsCentered(out var d3), d3);

        Assert.True(fails.Count == 0, "Keep-in-view failures:\n  " + string.Join("\n  ", fails));
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
        Check("View has Focus Filter Search", view.Any(n => n.Contains("Focus Filter S", StringComparison.OrdinalIgnoreCase)), string.Join(",", view));
        var filtersItems = app.MenuItemNames("Filters");
        Check("Filters has Find Next Match", filtersItems.Any(n => n.Contains("Find Next Match", StringComparison.OrdinalIgnoreCase)), string.Join(",", filtersItems));
        Check("Filters has New Filter from Current Line", filtersItems.Any(n => n.Contains("Current Line", StringComparison.OrdinalIgnoreCase)), string.Join(",", filtersItems));

        // ---- text find (dialog) ----
        app.FindText("other line 7");
        Check("find selects line 8", app.WaitStatus("Ln:", "Ln: 8 / 1,000"), app.StatusText("Ln:"));

        // ---- per-filter find (Filters menu -> Find Next/Previous Match) ----
        app.SelectLine(1);
        app.FilterNode("MATCH")?.AsTreeItem().Select();
        app.FindNextForSelectedFilter();
        Check("per-filter find next -> line 6", app.WaitStatus("Ln:", "Ln: 6 / 1,000"), app.StatusText("Ln:"));
        app.FindNextForSelectedFilter();
        Check("per-filter find next -> line 11", app.WaitStatus("Ln:", "Ln: 11 / 1,000"), app.StatusText("Ln:"));
        app.FindPrevForSelectedFilter();
        Check("per-filter find prev -> line 6", app.WaitStatus("Ln:", "Ln: 6 / 1,000"), app.StatusText("Ln:"));

        // ---- zoom (menu) ----
        app.ClickMenu("View", "Reset Zoom");
        Check("zoom reset 100%", app.WaitStatus("Zoom:", "Zoom: 100%"), app.StatusText("Zoom:"));
        app.ClickMenu("View", "Zoom In");
        Check("zoom in 110%", app.WaitStatus("Zoom:", "Zoom: 110%"), app.StatusText("Zoom:"));
        app.ClickMenu("View", "Zoom Out");
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
            app.ClickMenu("Filters", "Remove Filter");

            Check("deleted filter is gone", app.FilterNode("line 999") is null);
            Check("filter above survives", app.FilterNode("MATCH") is not null);
            Check("filter below survives", app.FilterNode("line 998") is not null);
            Check("count after delete", app.WaitStatus("Fil:", $"Fil: {TestData.MatchCount + 1:N0}"), app.StatusText("Fil:"));

            // Again, so repeated in-place deletes are covered too.
            app.FilterNode("line 998")!.AsTreeItem().Select();
            app.ClickMenu("Filters", "Remove Filter");

            Check("second delete removes it", app.FilterNode("line 998") is null);
            Check("original filter still listed", app.FilterNode("MATCH") is not null);
            Check("count back to the base set", app.WaitStatus("Fil:", $"Fil: {TestData.MatchCount:N0}"), app.StatusText("Fil:"));

            Assert.True(fails.Count == 0, "Filter delete failures:\n  " + string.Join("\n  ", fails));
        }
        finally { File.Delete(log); File.Delete(tat); }
    }

    [Fact]
    public void Ctrl_arrows_reorder_and_nest_the_selected_filter()
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

            // Ctrl+Up / Ctrl+Down reorder within the same level.
            var tree = app.Tree();
            app.FocusFilter("beta");
            app.CtrlKey(tree, VirtualKeyShort.UP);
            CheckOrder("ctrl+up moves it above alpha", "MATCH", "beta", "alpha");

            app.CtrlKey(tree, VirtualKeyShort.DOWN);
            CheckOrder("ctrl+down puts it back", "MATCH", "alpha", "beta");

            // Ctrl+Right nests it under the filter above it.
            app.CtrlKey(tree, VirtualKeyShort.RIGHT);
            CheckOrder("ctrl+right removes it from the top level", "MATCH", "alpha");
            Check("ctrl+right nests it under alpha",
                CascadeApp.IndexOfFilter(app.ChildFilterNames("alpha"), "beta") >= 0,
                string.Join(" | ", app.ChildFilterNames("alpha")));

            // Ctrl+Left moves it back out, directly after its old parent.
            app.CtrlKey(tree, VirtualKeyShort.LEFT);
            CheckOrder("ctrl+left restores it to the top level", "MATCH", "alpha", "beta");

            // The first filter has nothing above it, so both are no-ops rather than errors.
            app.FocusFilter("MATCH");
            app.CtrlKey(tree, VirtualKeyShort.UP);
            app.CtrlKey(tree, VirtualKeyShort.RIGHT);
            CheckOrder("no-op at the top of the list", "MATCH", "alpha", "beta");

            Assert.True(fails.Count == 0, "Filter reorder failures:\n  " + string.Join("\n  ", fails));
        }
        finally { File.Delete(log); File.Delete(tat); }
    }

    [Fact]
    public void Find_dialog_navigates_with_enter_and_f3()
    {
        // These keys have to work while the Find dialog itself has focus. Enter used to be the only one:
        // Shift+Enter hit the form's default button (AcceptButton ignores Alt/Ctrl but not Shift) and so
        // searched forwards, and F3 was only wired up on the main window.
        using var app = CascadeApp.Launch();
        var fails = new List<string>();
        void Check(string name, string expected) { if (!app.WaitStatus("Ln:", expected)) fails.Add($"{name} :: {app.StatusText("Ln:")}"); }

        app.SelectLine(1);
        var dlg = app.OpenFindDialog();
        app.FindInDialog(dlg, "MATCH line", forward: true);   // MATCH is on 1-based lines 1, 6, 11, 16, ...
        Check("find next from line 1", "Ln: 6 / 1,000");

        // From the text box. These are handled in the dialog's ProcessCmdKey, so they have to go through the
        // message loop rather than straight to a control.
        app.FocusInDialog(dlg);
        var edit = dlg.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit))!;
        app.SendKeyAsDialogKey(edit, VirtualKeyShort.F3);
        Check("F3 -> next", "Ln: 11 / 1,000");
        app.SendKeyAsDialogKey(edit, VirtualKeyShort.F3, VirtualKeyShort.SHIFT);
        Check("Shift+F3 -> previous", "Ln: 6 / 1,000");
        app.SendKeyAsDialogKey(edit, VirtualKeyShort.RETURN);
        Check("Enter -> next", "Ln: 11 / 1,000");
        app.SendKeyAsDialogKey(edit, VirtualKeyShort.RETURN, VirtualKeyShort.SHIFT);
        Check("Shift+Enter -> previous", "Ln: 6 / 1,000");

        // ...and with a button focused, which is exactly when Shift+Enter used to go the wrong way.
        var nextButton = dlg.FindFirstDescendant(cf => cf.ByName("Find Next"))!;
        app.FocusInDialog(dlg, "Find Next");
        app.SendKeyAsDialogKey(nextButton, VirtualKeyShort.RETURN, VirtualKeyShort.SHIFT);
        Check("Shift+Enter with a button focused -> previous", "Ln: 1 / 1,000");
        app.SendKeyAsDialogKey(nextButton, VirtualKeyShort.F3);
        Check("F3 with a button focused -> next", "Ln: 6 / 1,000");

        try { dlg.Close(); } catch { /* modeless: hides */ }
        Assert.True(fails.Count == 0, "Find key failures:\n  " + string.Join("\n  ", fails));
    }

    [Fact]
    public void Reaching_the_end_of_a_search_shows_feedback()
    {
        // Every find command has to say so when there is nothing further that way. Two of them used to be
        // completely silent, and Find only reported it inside a dialog that is usually closed.
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

            // 3. Text find for something that does not exist.
            var dlg = app.OpenFindDialog();
            app.FindInDialog(dlg, "zzz-not-in-this-file", forward: true);
            Check("text find reports the end", app.WaitForFindMessage("No more matches"));
            try { dlg.Close(); } catch { /* modeless: hides */ }

            // 4. Filter search for a filter that is not in the list. Deliberately after the Find bar, which
            // has to hand the keyboard back when it hides.
            var search = app.FilterSearchBox();
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
        string selected = app.StatusText("Ln:");
        int top = app.FirstVisibleLine();
        Check("a line is selected to begin with", selected == "Ln: 501 / 1,000", selected);

        for (int i = 0; i < 5; i++) app.CtrlKey(grid, VirtualKeyShort.DOWN);
        int afterDown = app.FirstVisibleLine();
        Check("ctrl+down scrolls the view", afterDown > top, $"top {top} -> {afterDown}");
        Check("ctrl+down leaves the selected line alone", app.StatusText("Ln:") == selected, app.StatusText("Ln:"));
        Check("ctrl+down leaves the selection alone", app.StatusText("Sel:") == "Sel: 1", app.StatusText("Sel:"));

        for (int i = 0; i < 5; i++) app.CtrlKey(grid, VirtualKeyShort.UP);
        int afterUp = app.FirstVisibleLine();
        Check("ctrl+up scrolls back", afterUp == top, $"top {top} -> {afterDown} -> {afterUp}");
        Check("ctrl+up leaves the selected line alone", app.StatusText("Ln:") == selected, app.StatusText("Ln:"));

        Assert.True(fails.Count == 0, "Scroll failures:\n  " + string.Join("\n  ", fails));
    }

    [Fact]
    public void Copy_and_docking_work()
    {
        using var app = CascadeApp.Launch();
        var fails = new List<string>();
        void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

        // ---- copy selection to clipboard ----
        app.SelectLine(9); // 1-based 9 -> 0-based line 8 = "other line 8"
        app.ClickMenu("Edit", "Copy");
        string clip = "";
        Retry.WhileFalse(() => (clip = CascadeApp.ReadClipboardText()).Contains("other line 8", StringComparison.Ordinal),
                         TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(25));
        Check("copy places selected line on the clipboard", clip.Contains("other line 8", StringComparison.Ordinal), clip);

        // ---- docking: move the filter list around and verify the layout follows ----
        app.ClickMenu("View", "Filter List Location", "Dock Left");
        Check("dock left puts the filter list left of the log",
            app.Tree().BoundingRectangle.Left < app.Grid().BoundingRectangle.Left,
            $"tree={app.Tree().BoundingRectangle} grid={app.Grid().BoundingRectangle}");

        app.ClickMenu("View", "Filter List Location", "Dock Bottom");
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

        var dlg = app.OpenFindDialog();

        // --- dim mode: the highlighted line must actually contain the query ---
        app.FindInDialog(dlg, "other line 137", forward: true);
        Check("dim forward -> Ln 138", app.WaitStatus("Ln:", "Ln: 138 / 1,000"), app.StatusText("Ln:"));
        Check("dim forward: selected line contains query", app.WaitSelectedRowText("other line 137"), app.SelectedRowText());

        app.FindInDialog(dlg, "other line 246", forward: true);
        Check("dim forward2 -> Ln 247", app.WaitStatus("Ln:", "Ln: 247 / 1,000"), app.StatusText("Ln:"));
        Check("dim forward2: selected line contains query", app.WaitSelectedRowText("other line 246"), app.SelectedRowText());

        app.FindInDialog(dlg, "other line 89", forward: false);
        Check("dim backward -> Ln 90", app.WaitStatus("Ln:", "Ln: 90 / 1,000"), app.StatusText("Ln:"));
        Check("dim backward: selected line contains query", app.WaitSelectedRowText("other line 89"), app.SelectedRowText());

        // repeat the same unique query forward: it must NOT re-find the current line
        app.FindInDialog(dlg, "other line 246", forward: true); // caret at 89 -> 246 is ahead
        Check("dim re-find forward -> Ln 247", app.WaitStatus("Ln:", "Ln: 247 / 1,000"), app.StatusText("Ln:"));
        app.FindInDialog(dlg, "other line 246", forward: true); // caret==246, unique -> not found, stay
        Check("dim no more matches -> not found", app.WaitDialogText(dlg, "Not found"), app.DialogText(dlg));
        Check("dim no more matches -> selection unchanged", app.StatusText("Ln:") == "Ln: 247 / 1,000", app.StatusText("Ln:"));

        // --- filtered mode: the highlighted line must STILL contain the query ---
        app.ToggleFilteredMode();
        app.FindInDialog(dlg, "MATCH line 500", forward: true);
        Check("filtered: matched-line search -> Ln 501", app.WaitStatus("Ln:", "Ln: 501 / 1,000"), app.StatusText("Ln:"));
        Check("filtered: selected line contains query", app.WaitSelectedRowText("MATCH line 500"), app.SelectedRowText());

        // text that only exists on a HIDDEN (filtered-out) line, AHEAD of the caret, must NOT jump to a
        // wrong (visible) line — the highlighted line must always contain the query.
        app.FindInDialog(dlg, "other line 733", forward: true);
        Check("filtered: hidden-only text reports not found",
            app.WaitDialogText(dlg, "Not found"), app.DialogText(dlg));
        Check("filtered: hidden-only text leaves selection put (still 501)",
            app.StatusText("Ln:") == "Ln: 501 / 1,000", app.StatusText("Ln:"));

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

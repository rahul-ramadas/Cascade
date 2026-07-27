using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;

namespace Cascade.UiTests;

/// <summary>
/// End-to-end UI-automation tests that drive the real Cascade.exe through FlaUI (Windows UI
/// Automation) — no in-process test hooks. Each test launches its own app instance on a deterministic
/// 120-line file (every 5th line contains "MATCH") with one imported "MATCH" filter.
/// </summary>
public class UiFeatureTests
{
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
    public void Copy_and_docking_work()
    {
        using var app = CascadeApp.Launch();
        var fails = new List<string>();
        void Check(string name, bool cond, string detail = "") { if (!cond) fails.Add($"{name} :: {detail}"); }

        // ---- copy selection to clipboard ----
        app.SelectLine(9); // 1-based 9 -> 0-based line 8 = "other line 8"
        app.ClickMenu("Edit", "Copy");
        System.Threading.Thread.Sleep(150);
        string clip = CascadeApp.ReadClipboardText();
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
}

using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text;
using Cascade.Core.Columns;
using Cascade.Core.Document;
using Cascade.Core.Find;
using Cascade.Core.Model;
using Cascade.Core.Persistence;

namespace Cascade.App;

/// <summary>
/// Headless end-to-end verification run via <c>Cascade.exe --selftest [file] [/Filters:x.tat]</c>.
/// Exercises open → stream-index → import/apply filters → row mapping and prints timings. Writes a
/// log file (and to the attached console) and returns a non-zero exit code on failure.
/// </summary>
internal static class SelfTest
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "cascade_selftest.log");
    private static StreamWriter _log = null!;
    private static string? _only;
    private static int _skipped;

    /// <summary>Runs a group of checks and records how long it took, so a self-test that starts dragging
    /// says where the time went rather than leaving it to be guessed at. Groups whose name does not contain
    /// <c>--only</c>'s text are skipped, which is how you iterate on one of them without paying for the
    /// drag checks every time.</summary>
    private static bool Timed(string name, Func<bool> checks)
    {
        if (_only is not null && !name.Contains(_only, StringComparison.OrdinalIgnoreCase)) { _skipped++; return true; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool ok = checks();
        clock.Stop();
        Line($"   ({name}: {clock.ElapsedMilliseconds:N0} ms)");
        return ok;
    }

    public static int Run(string[] args)
    {
        _log = new StreamWriter(LogPath, false) { AutoFlush = true };
        try
        {
            Line("=== Cascade self-test ===");
            Line("Log: " + LogPath);

            string? file = args.FirstOrDefault(a => !a.StartsWith('/') && !a.StartsWith("--"));
            string? tat = args.FirstOrDefault(a => a.StartsWith("/Filters:", StringComparison.OrdinalIgnoreCase))?["/Filters:".Length..].Trim('"');
            _only = args.FirstOrDefault(a => a.StartsWith("--only=", StringComparison.OrdinalIgnoreCase))?["--only=".Length..].Trim('"');
            _skipped = 0;
            if (_only is not null) Line($"(only groups matching \"{_only}\")");

            bool ok = Timed("engine", RunEngineChecks);
            ok &= Timed("settings", RunSettingsChecks);
            ok &= Timed("machine state", RunMachineStateChecks);
            ok &= Timed("render", RunRenderChecks);
            ok &= Timed("navigation", RunNavigationChecks);
            ok &= Timed("filter list", RunFilterListChecks);
            ok &= Timed("filter search", RunFilterSearchRevealChecks);
            ok &= Timed("filter presets", RunFilterPresetChecks);
            ok &= Timed("match map", RunMatchMapChecks);
            ok &= Timed("text selection", RunTextSelectionChecks);
            ok &= Timed("find highlighting", RunFindHighlightChecks);
            ok &= Timed("find status wording", RunFindStatusChecks);
            ok &= Timed("word wrap", RunWordWrapChecks);
            ok &= Timed("filter tips", RunFilterTipChecks);
            ok &= Timed("find bar", RunFindBarChecks);
            ok &= Timed("drop placement", RunDropPlacementChecks);
            ok &= Timed("filter drag", RunFilterDragChecks);
            ok &= Timed("filter enable", RunFilterEnableChecks);
            ok &= Timed("dialog keyboard", RunDialogKeyboardChecks);
            ok &= Timed("menu keyboard", RunMenuMnemonicChecks);
            ok &= Timed("progress paint", RunProgressPaintChecks);
            ok &= Timed("new filter from line", RunNewFilterFromLineChecks);
            if (file is not null && File.Exists(file)) ok &= RunFileChecks(file, tat);
            else Line("(no real file supplied; skipped large-file checks)");

            // Says so plainly, so a filtered run can never be mistaken for a clean full one.
            Line((ok ? "RESULT: PASSED" : "RESULT: FAILED") + (_skipped > 0 ? $" ({_skipped} groups skipped by --only)" : ""));
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Line("EXCEPTION: " + ex);
            return 2;
        }
        finally { _log.Dispose(); }
    }

    /// <summary>
    /// Scrolling right must never paint line text over the marker or line-number margin.
    ///
    /// The margin does not scroll, so every pixel of it has to be identical whatever the horizontal offset
    /// is - which makes the check exact rather than a judgement about what looks wrong. It bites because
    /// TextRenderer draws through GDI and silently ignores the GDI+ clip region unless asked not to, so the
    /// SetClip guarding the text looked sufficient and was not.
    /// </summary>
    private static bool RunRenderChecks()
    {
        Line("-- rendering --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_render_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        // Long enough that the content is far wider than the viewport below, so there is room to scroll.
        for (int i = 0; i < 40; i++)
            sb.Append($"[2026-07-16T18:06:{i:00}.123][provider][{i:000}][INFO ] a deliberately long line " +
                      $"of message text {i} that runs well past the right hand edge of the viewport\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();
            doc.Markers.Toggle(2, 0);   // a marker bar, so the marker gutter has content to compare too

            var settings = new AppSettings { MarkerVisibility = MarkerVisibilityMode.Always };
            var grid = new LineGridControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(420, 320),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(grid);
            grid.Attach(doc, settings);
            host.Show();
            Pump();

            int gutter = grid.GutterWidthForTesting;
            if (gutter <= 0) return Check("the grid has a margin to protect", false);
            var margin = grid.GutterAreaForTesting;

            grid.ScrollHorizontallyTo(0);
            grid.RefreshView();
            Pump();
            using var unscrolled = Capture(host);

            grid.ScrollHorizontallyTo(260);
            grid.RefreshView();
            Pump();
            using var scrolled = Capture(host);

            bool moved = !SameRegion(unscrolled, scrolled,
                             new Rectangle(gutter, margin.Top, unscrolled.Width - gutter - 20, margin.Height));
            bool ok = Check("scrolling right actually moved the text", moved);

            var diff = FirstDifference(unscrolled, scrolled, margin);
            ok &= Check($"scrolling right leaves the line-number margin (0..{gutter}) untouched" +
                        (diff is null ? "" : $" [first differs at x={diff.Value.X},y={diff.Value.Y}: " +
                                              $"{unscrolled.GetPixel(diff.Value.X, diff.Value.Y)} -> " +
                                              $"{scrolled.GetPixel(diff.Value.X, diff.Value.Y)}]"),
                        diff is null);
            if (diff is not null)
                WriteRenderDiagnostics("scrolling right leaves the line-number margin untouched",
                    host, grid, margin, diff.Value, unscrolled, scrolled);

            // Columns are a different drawing path - per-cell text plus a header row - and had the same flaw.
            doc.Columns.Enabled = true;
            doc.Columns.Mode = ColumnSplitMode.Delimiter;
            doc.Columns.Delimiter = "]";
            doc.Columns.Columns.Clear();
            foreach (var (n, w) in new[] { ("Time", 190), ("Provider", 90), ("Id", 55), ("Message", 360) })
                doc.Columns.Columns.Add(new ColumnDef { Name = n, Width = w });

            grid.ScrollHorizontallyTo(0);
            grid.RefreshView();
            Pump();
            using var colUnscrolled = Capture(host);

            grid.ScrollHorizontallyTo(260);
            grid.RefreshView();
            Pump();
            using var colScrolled = Capture(host);

            var colMargin = grid.GutterAreaForTesting;
            ok &= Check("scrolling right actually moved the columns",
                        !SameRegion(colUnscrolled, colScrolled,
                            new Rectangle(gutter, colMargin.Top, colUnscrolled.Width - gutter - 20, colMargin.Height)));
            var colDiff = FirstDifference(colUnscrolled, colScrolled, colMargin);
            ok &= Check("scrolling right with columns leaves the margin untouched" +
                        (colDiff is null ? "" : $" [first differs at x={colDiff.Value.X},y={colDiff.Value.Y}]"),
                        colDiff is null);
            if (colDiff is not null)
                WriteRenderDiagnostics("scrolling right with columns leaves the margin untouched",
                    host, grid, colMargin, colDiff.Value, colUnscrolled, colScrolled);

            // An automated or assistive scroll sets the scrollbar's Value, which raises ValueChanged but not
            // Scroll. If that path does not drop the view anchor, the next refresh re-applies the anchor and
            // puts the view straight back - scrolling silently does nothing, which is what happened on a
            // machine slow enough for a filter pass to still be running.
            doc.Columns.Enabled = false;
            grid.RefreshView();
            Pump();
            grid.ClearViewAnchor();
            grid.SetViewAnchor(new ViewAnchor(0, 0, -1), select: false);
            grid.SetVerticalScrollValue(15);
            grid.RefreshView();
            Pump();
            ok &= Check($"an automated scroll survives an armed view anchor (row {grid.FirstRowForTesting})",
                        grid.FirstRowForTesting == 15);
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>The filter list draws three columns into one owner-drawn row, and TextRenderer goes through
    /// GDI, which ignores the GDI+ clip the columns rely on unless told not to. The symptom was a long
    /// pattern painting straight across the description and the count.</summary>
    private static bool RunFilterListChecks()
    {
        Line("-- filter list columns --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_filters_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < 200; i++) sb.Append($"ERROR SomeVeryLongComponentName line {i}\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var tree = new FilterTreeControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(260, 200),   // narrow enough that a long pattern cannot fit
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(tree);
            tree.Attach(doc);
            host.Show();
            Pump();

            var longFilter = new Filter
            {
                Description = "a description that also needs room",
                Match = new FilterMatch { Text = "SomeVeryLongComponentName that runs on well past the column edge" }
            };
            var other = new Filter { Description = "another description", Match = new FilterMatch { Text = "ERROR" } };
            SetFilters(doc, tree, longFilter, other);
            var columns = tree.ColumnsForTesting;
            bool ok = Check("the description column is shown when a filter has one", columns.HasDescription);
            if (!ok) return false;

            using var withLongPattern = Capture(host);

            // Swap in a pattern that is just as far too long, so the columns land in exactly the same place
            // and the only thing that changed is the glyphs in the pattern column. Anything that differs
            // from there rightwards is the pattern painting outside its own column.
            longFilter.Match.Text = "ZZZZZZZZZZZZZZZZZZZZZZZZZ that also runs on well past the column edge";
            SetFilters(doc, tree, longFilter, other);
            using var withOtherPattern = Capture(host);

            ok &= Check("the columns did not move, so the comparison is about the text alone",
                        tree.ColumnsForTesting == columns);

            var area = tree.TreeAreaForTesting;
            var patternArea = new Rectangle(area.Left, area.Top, columns.FilterRight, Math.Min(area.Height, 80));
            ok &= Check("changing the pattern changed the pattern column",
                        !SameRegion(withLongPattern, withOtherPattern, patternArea));

            var rightOfPattern = new Rectangle(area.Left + columns.FilterRight, area.Top,
                                               Math.Max(1, area.Width - columns.FilterRight), Math.Min(area.Height, 80));
            var bleed = FirstDifference(withLongPattern, withOtherPattern, rightOfPattern);
            ok &= Check($"a pattern too long for its column does not paint over the ones beside it" +
                        (bleed is null ? "" : $" [first differs at x={bleed.Value.X},y={bleed.Value.Y}]"),
                        bleed is null);

            // With nothing to put in it, the description column is not shown at all - and comes back when a
            // description does.
            longFilter.Description = "";
            other.Description = "";
            SetFilters(doc, tree, longFilter, other);
            ok &= Check("the description column is dropped when no filter has one",
                        !tree.ColumnsForTesting.HasDescription);
            ok &= Check("dropping it gives the space to the pattern",
                        tree.ColumnsForTesting.FilterRight > columns.FilterRight);

            other.Description = "back again";
            SetFilters(doc, tree, longFilter, other);
            ok &= Check("the description column returns when one is set", tree.ColumnsForTesting.HasDescription);

            // Half of whatever the count did not want belongs to the pattern, however long the descriptions
            // are. DescX is the pattern's width and CountX is the space left after the count.
            var wide = tree.ColumnsForTesting;
            ok &= Check($"the description takes at most half the space left after the count " +
                        $"(pattern {wide.DescX}px of {wide.CountX}px)",
                        wide.DescX * 2 >= wide.CountX);

            host.ClientSize = new Size(150, 200);
            Pump();
            var squeezed = tree.ColumnsForTesting;
            ok &= Check($"the same holds in a pane too narrow for any of it " +
                        $"(pattern {squeezed.DescX}px of {squeezed.CountX}px)",
                        squeezed.DescX * 2 >= squeezed.CountX);

            // Descriptions far shorter than the word "Description": the column still has to be able to show
            // its own heading, or it reads as broken however well the content fits.
            host.ClientSize = new Size(400, 200);
            longFilter.Description = "a";
            other.Description = "b";
            SetFilters(doc, tree, longFilter, other);
            var tiny = tree.ColumnsForTesting;
            ok &= Check($"a column is at least as wide as its own heading " +
                        $"(description {tiny.DescriptionWidth}px, heading needs {tree.HeaderWidthForTesting("Description")}px)",
                        tiny.DescriptionWidth >= tree.HeaderWidthForTesting("Description"));

            // Selecting a row outlines it, and the outline has to be just that. Windows paints its own
            // selection across the whole label before the row is drawn over the top, so a row that starts
            // painting even a couple of pixels in leaves a stripe of it between the checkbox and the text.
            tree.SelectForTesting(other);
            Pump();
            var selArea = tree.TreeAreaForTesting;
            var rowRect = tree.RowBoundsForTesting(other);
            using var selected = Capture(host);
            int worst = 0, worstX = -1;
            for (int x = Math.Max(0, rowRect.Left - 8); x < rowRect.Left + 8; x++)
            {
                int run = 0;
                for (int y = rowRect.Top; y < rowRect.Bottom; y++)
                {
                    int hx = selArea.Left + x, hy = selArea.Top + y;
                    if (hx >= selected.Width || hy >= selected.Height) continue;
                    var px = selected.GetPixel(hx, hy);
                    if (px.R == SystemColors.Highlight.R && px.G == SystemColors.Highlight.G &&
                        px.B == SystemColors.Highlight.B) run++;
                }
                if (run > worst) { worst = run; worstX = x; }
            }
            // Two is the outline itself crossing the column: one pixel at the top, one at the bottom.
            ok &= Check($"the selection outline is a plain box, with no stripe left in it beside the " +
                        $"checkbox (worst column x={worstX} is highlighted down {worst} of {rowRect.Height} pixels)",
                        worst <= 2);
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    private static void SetFilters(CascadeDocument doc, FilterTreeControl tree, params Filter[] filters)
    {
        var collection = new FilterCollection();
        foreach (var f in filters) collection.Roots.Add(f);
        doc.SetFilters(collection);
        tree.Rebuild();
        Pump();
    }

    /// <summary>The presets pane's selection IS the set of presets in effect, and the enabled filters are
    /// the union of what is selected. Both directions have to hold: selecting applies, and enabling by hand
    /// selects.</summary>
    private static bool RunFilterPresetChecks()
    {
        Line("-- filter presets --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_presets_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < 50; i++) sb.Append($"alpha beta gamma line {i}\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var pane = new FilterPresetsControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(240, 220),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(pane);
            pane.Attach(doc);
            host.Show();
            Pump();

            var collection = new FilterCollection();
            var a = new Filter { Match = new FilterMatch { Text = "alpha" } };
            var b = new Filter { Match = new FilterMatch { Text = "beta" } };
            var c = new Filter { Match = new FilterMatch { Text = "gamma" } };
            foreach (var f in new[] { a, b, c }) collection.Roots.Add(f);
            collection.Presets.Add(new FilterPreset("first", new[] { a.Id }));
            collection.Presets.Add(new FilterPreset("second", new[] { b.Id, c.Id }));
            doc.SetFilters(collection);
            pane.Attach(doc);
            Pump();

            int applied = 0;
            pane.PresetsApplied += () => applied++;

            bool ok = Check("both presets are listed", pane.LabelsForTesting.SequenceEqual(new[] { "first", "second" }),
                            string.Join(" | ", pane.LabelsForTesting));
            ok &= Check("nothing is in effect while every filter is off", pane.ActiveForTesting.Length == 0);

            // Selecting one preset means "just this".
            pane.SelectForTesting("first");
            Pump();
            ok &= Check("selecting a preset enables exactly its filters", a.Enabled && !b.Enabled && !c.Enabled,
                        $"a={a.Enabled} b={b.Enabled} c={c.Enabled}");

            // Adding a second means "both", and does not drop the first.
            pane.SelectForTesting("first", "second");
            Pump();
            ok &= Check("adding a preset enables the union", a.Enabled && b.Enabled && c.Enabled,
                        $"a={a.Enabled} b={b.Enabled} c={c.Enabled}");

            pane.SelectForTesting("second");
            Pump();
            ok &= Check("dropping a preset turns its filters back off", !a.Enabled && b.Enabled && c.Enabled,
                        $"a={a.Enabled} b={b.Enabled} c={c.Enabled}");

            pane.SelectForTesting();
            Pump();
            ok &= Check("selecting nothing leaves every filter off", !a.Enabled && !b.Enabled && !c.Enabled,
                        $"a={a.Enabled} b={b.Enabled} c={c.Enabled}");

            // A burst of selection changes must cost one re-filter, not one each: applying is what re-runs
            // the filters over the whole file.
            applied = 0;
            pane.SelectForTesting("first");
            pane.SelectForTesting("second");
            pane.SelectForTesting("first", "second");
            Pump();
            ok &= Check("a burst of selection changes re-filters once", applied == 1, $"applied {applied} times");

            // The other direction: enabling by hand is enough to put a preset in effect.
            a.Enabled = false; b.Enabled = false; c.Enabled = false;
            pane.RefreshActive();
            ok &= Check("nothing is in effect after everything is switched off by hand", pane.ActiveForTesting.Length == 0,
                        string.Join(",", pane.ActiveForTesting));
            b.Enabled = true;
            pane.RefreshActive();
            ok &= Check("a half-enabled preset is not in effect", pane.ActiveForTesting.Length == 0,
                        string.Join(",", pane.ActiveForTesting));
            c.Enabled = true;
            pane.RefreshActive();
            ok &= Check("enabling every filter of a preset by hand puts it in effect",
                        pane.ActiveForTesting.SequenceEqual(new[] { "second" }), string.Join(",", pane.ActiveForTesting));

            // A deleted filter is remembered but reported.
            collection.Remove(c);
            pane.Rebuild();
            ok &= Check("a preset says how many of its filters have gone",
                        pane.LabelsForTesting[1].Contains("1 missing"), string.Join(" | ", pane.LabelsForTesting));

            return ok;
        }
        finally
        {
            host?.Close();
            host?.Dispose();
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>The match map is a summary of the whole file in one pixel column, so what matters is that a
    /// band says the right thing - how much of it matched and in what colours - not what the picture looks
    /// like. Both are checked: the summary directly, and that the picture actually gets painted.</summary>
    private static bool RunMatchMapChecks()
    {
        Line("-- match map --");
        const int lines = 40_000;
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_map_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        // Lines 0..999 are COMMON; line 2500 alone is RARE. One band is a many-line block, so the rare one
        // is what proves a lone match is not rounded away.
        for (int i = 0; i < lines; i++)
            sb.Append(i < 10_000 ? "COMMON " : i == 25_000 ? "RARE " : "plain ").Append("line ").Append(i).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var grid = new LineGridControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(600, 400),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(grid);
            grid.Attach(doc, new AppSettings());
            host.Show();
            Pump();

            var common = new Filter { Enabled = true, Match = new FilterMatch { Text = "COMMON" }, Style = { Background = new RgbColor(0x22, 0x44, 0xEE) } };
            var rare = new Filter { Enabled = true, Match = new FilterMatch { Text = "RARE" }, Style = { Background = new RgbColor(0xEE, 0x22, 0x22) } };
            var collection = new FilterCollection();
            collection.Roots.Add(common);
            collection.Roots.Add(rare);
            doc.SetFilters(collection);
            WaitForFiltering(doc);
            Pump();

            var map = grid.MatchMapForTesting;
            bool ok = Check("the map replaces the vertical scrollbar", map is not null && map.Visible && !grid.VerticalScrollBarVisibleForTesting);
            if (map is null) return false;

            map.RebuildForTesting();
            int height = map.BandCountForTesting;
            ok &= Check("the map has one band per pixel row", height > 50, $"{height} bands");
            if (height <= 50) return false;

            int BandOf(long line) => (int)(line * height / lines);

            ok &= Check("a band of matches is dense", map.DensityForTesting(BandOf(5000)) > 0.99,
                        map.DensityForTesting(BandOf(5000)).ToString("0.000"));
            ok &= Check("a band with nothing in it is empty", map.DensityForTesting(BandOf(18_000)) == 0,
                        map.DensityForTesting(BandOf(18_000)).ToString("0.000"));

            // The band holding the single RARE line: thin, but it must still be there and in its own colour.
            double rareDensity = map.DensityForTesting(BandOf(25_000));
            ok &= Check("a lone match still registers in its band", rareDensity > 0 && rareDensity < 0.2,
                        rareDensity.ToString("0.0000"));
            var rareColors = map.ColorsForTesting(BandOf(25_000));
            ok &= Check("the lone match keeps its own colour", rareColors.Length == 1 && rareColors[0].R == 0xEE,
                        string.Join(",", rareColors.Select(c => c.Name)));
            var commonColors = map.ColorsForTesting(BandOf(5000));
            ok &= Check("a dense band takes its filter's colour", commonColors.Length == 1 && commonColors[0].B == 0xEE,
                        string.Join(",", commonColors.Select(c => c.Name)));

            // ...and it is actually painted: the band with the lone match must differ from an empty one.
            using var picture = Capture(host);
            int mapLeft = picture.Width - map.Width;
            bool rareRowPainted = RowHasColor(picture, mapLeft, map.Width, MapRowY(map, BandOf(25_000)), 0xEE, 0x22, 0x22);
            ok &= Check("the lone match is painted, not rounded away", rareRowPainted);

            // The viewport rectangle is the thumb: it has to follow the view.
            long before = grid.FirstVisibleRow;
            grid.ScrollToRow(30_000);
            Pump();
            ok &= Check("scrolling moves the view the map reports", grid.FirstVisibleRow > before,
                        $"{before} -> {grid.FirstVisibleRow}");

            // Filtered mode: the map covers the same rows the scrollbar does, and there every row is a
            // match - so the whole strip fills, and the density channel gives way to the colours.
            doc.Filters.ShowOnlyFilteredLines = true;
            grid.RefreshView();
            map.RebuildForTesting();
            int solid = 0, sparse = 0;
            for (int y = 0; y < map.BandCountForTesting; y++)
            {
                double d = map.DensityForTesting(y);
                if (d > 0.99) solid++;
                else if (d > 0) sparse++;
            }
            ok &= Check("every band is solid once only matching lines are shown", sparse == 0 && solid > 50,
                        $"{solid} solid, {sparse} partial");

            // Markers draw down the map's own edge, and they change without any filter changing - so the
            // real gesture has to reach the map, or a mark never appears on it at all.
            doc.Filters.ShowOnlyFilteredLines = false;
            grid.RefreshView();
            map.RebuildForTesting();
            Pump();
            grid.ScrollToRow(12_345);
            grid.RefreshView();
            Pump();
            grid.SelectRowForAccessibility(12_345);
            Pump();
            int markerBand = BandOf(12_345);
            // Look at the band from a distance: the viewport rectangle is drawn over the same lane, and its
            // blue blended with the mark's red is neither one nor the other.
            grid.ScrollToRow(0);
            grid.RefreshView();
            Pump();
            bool beforeMark = MapRowHasMarker(host, map, markerBand);
            grid.PressKeyForTesting(Keys.Control | Keys.D1);
            grid.ScrollToRow(0);
            grid.RefreshView();
            Pump();
            bool afterMark = MapRowHasMarker(host, map, markerBand);
            ok &= Check("marking a line shows up on the map at once", !beforeMark && afterMark,
                        $"before {beforeMark}, after {afterMark}; marks {doc.Markers.UsedMarkers}, " +
                        $"band {markerBand}; got {MapLaneColours(host, map, markerBand)}");

            grid.PressKeyForTesting(Keys.Control | Keys.D1);
            grid.ScrollToRow(0);
            grid.RefreshView();
            Pump();
            ok &= Check("and goes again when the mark is cleared", !MapRowHasMarker(host, map, markerBand),
                        MapLaneColours(host, map, markerBand));

            return ok;
        }
        finally
        {
            host?.Close();
            host?.Dispose();
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>Y of a band within the captured window (the map fills the grid's height).</summary>
    private static int MapRowY(MatchMapControl map, int band) => map.Top + band;

    /// <summary>Whether marker 1's colour is painted in the map's marker lane at a band. The tick is three
    /// pixels tall, so a band either side counts as the same mark.</summary>
    private static bool MapRowHasMarker(Form host, MatchMapControl map, int band)
    {
        var c = AppSettings.MarkerColors[0];
        using var picture = Capture(host);
        int left = picture.Width - map.Width;
        for (int dy = -2; dy <= 3; dy++)
            if (RowHasColor(picture, left, Math.Min(map.Width, 6), MapRowY(map, band + dy), c.R, c.G, c.B))
                return true;
        return false;
    }

    /// <summary>What is actually painted in the map's marker lane around a band, for diagnosing a miss.</summary>
    private static string MapLaneColours(Form host, MatchMapControl map, int band)
    {
        using var picture = Capture(host);
        int left = picture.Width - map.Width;
        var seen = new List<string>();
        for (int dy = -3; dy <= 4; dy++)
        {
            int y = MapRowY(map, band + dy);
            if (y < 0 || y >= picture.Height) continue;
            for (int x = left; x < Math.Min(picture.Width, left + 6); x++)
            {
                var p = picture.GetPixel(x, y);
                string s = $"{p.R},{p.G},{p.B}";
                if (!seen.Contains(s)) seen.Add(s);
            }
        }
        return string.Join(" | ", seen);
    }

    private static bool RowHasColor(Bitmap bmp, int left, int width, int y, int r, int g, int b)
    {
        if (y < 0 || y >= bmp.Height) return false;
        for (int x = Math.Max(0, left); x < Math.Min(bmp.Width, left + width); x++)
        {
            var c = bmp.GetPixel(x, y);
            if (Math.Abs(c.R - r) < 24 && Math.Abs(c.G - g) < 24 && Math.Abs(c.B - b) < 24) return true;
        }
        return false;
    }

    /// <summary>Waits for a filter pass to finish, so the per-filter caches the map reads exist.</summary>
    private static void WaitForFiltering(CascadeDocument doc)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (doc.IsBusy && DateTime.UtcNow < until) { Pump(); Thread.Sleep(5); }
        Pump();
    }

    /// <summary>Selecting part of a line. There is no caret and none is drawn, so every rule here is about
    /// what the mouse does: a click takes the whole line, a drag within one line takes a range, a drag off
    /// it goes back to whole lines, and moving away drops the range.</summary>
    private static bool RunTextSelectionChecks()
    {
        Line("-- text selection --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_sel_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < 40; i++) sb.Append($"line {i:00} req-abc123 GET /v1/orders/99 -> 200 in 41ms\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var grid = new LineGridControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(700, 300),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(grid);
            grid.Attach(doc, new AppSettings());
            host.Show();
            Pump();

            string text = doc.GetLineText(2);
            int reqAt = text.IndexOf("req-abc123", StringComparison.Ordinal);
            int xOfChar(int index) => grid.XForCharForTesting(2, index);

            // A click takes the whole line and leaves no range behind.
            grid.ClickForTesting(2, xOfChar(reqAt) + 2);
            bool ok = Check("a click selects the whole line", !grid.HasCharSelection && grid.SelectedText is null);

            // A drag within the line takes exactly what it covered.
            grid.DragForTesting(2, xOfChar(reqAt), xOfChar(reqAt + 10));
            ok &= Check("dragging inside a line selects that part", grid.SelectedText == "req-abc123",
                        grid.SelectedText ?? "(none)");

            // Dragging off the line means whole lines after all.
            grid.ClickForTesting(2, xOfChar(reqAt));
            grid.DragToRowForTesting(4, xOfChar(reqAt + 10));
            ok &= Check("dragging onto another line goes back to whole lines", !grid.HasCharSelection,
                        grid.SelectedText ?? "(none)");

            // Double-click takes the word under the pointer; the separators around it are not part of it.
            grid.DoubleClickForTesting(2, xOfChar(reqAt + 3));
            ok &= Check("double-click selects the word", grid.SelectedText == "req-abc123", grid.SelectedText ?? "(none)");

            // ...and it stops at whitespace rather than running to the end of the line.
            int getAt = text.IndexOf("GET", StringComparison.Ordinal);
            grid.DoubleClickForTesting(2, xOfChar(getAt + 1));
            ok &= Check("a word stops at the space around it", grid.SelectedText == "GET", grid.SelectedText ?? "(none)");

            // Moving away drops it: the range meant a place the user is no longer looking at.
            grid.PressKeyForTesting(Keys.Down);
            ok &= Check("moving the caret drops the range", !grid.HasCharSelection, grid.SelectedText ?? "(none)");

            // Starting past the right-hand end clamps to the end of the line rather than running off it.
            grid.DragForTesting(2, 5000, xOfChar(text.Length - 5));
            ok &= Check("a drag starting past the end selects up to the end",
                        grid.SelectedText == text[^5..], grid.SelectedText ?? "(none)");

            // The visual contract: only the range is in the selection colours, and the rest of the row keeps
            // its own - which is what makes it read like a text box rather than a selected row.
            grid.DragForTesting(2, xOfChar(reqAt), xOfChar(reqAt + 10));
            grid.RefreshView();
            Pump();
            using var picture = Capture(host);
            var settings = new AppSettings();
            int rowY = grid.RowMiddleForTesting(2);
            int inside = xOfChar(reqAt + 5), before = xOfChar(reqAt) - 6, after = xOfChar(reqAt + 10) + 6;
            ok &= Check("the selected part is drawn selected", IsBackground(picture, inside, rowY, settings.SelectionBack),
                        picture.GetPixel(Math.Clamp(inside, 0, picture.Width - 1), rowY).Name);
            ok &= Check("the rest of the row is not", !IsBackground(picture, before, rowY, settings.SelectionBack) &&
                                                     !IsBackground(picture, after, rowY, settings.SelectionBack),
                        $"{picture.GetPixel(Math.Clamp(before, 0, picture.Width - 1), rowY).Name} / " +
                        $"{picture.GetPixel(Math.Clamp(after, 0, picture.Width - 1), rowY).Name}");

            return ok;
        }
        finally
        {
            host?.Close();
            host?.Dispose();
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>What fraction of the columns across a span of a row show a colour. Not "is any pixel that
    /// colour": ClearType puts a warm fringe on every dark glyph, and one of those is a close enough match
    /// to a soft highlight to answer yes anywhere in the line. A real highlight fills its whole span.</summary>
    private static double PixelFraction(Bitmap bmp, int x0, int x1, int y, Color colour)
    {
        int from = Math.Max(0, x0), to = Math.Min(bmp.Width, x1);
        if (to <= from) return 0;
        int hits = 0;
        for (int x = from; x < to; x++)
            for (int dy = -3; dy <= 3; dy++)
            {
                int yy = Math.Clamp(y + dy, 0, bmp.Height - 1);
                var c = bmp.GetPixel(x, yy);
                if (Math.Abs(c.R - colour.R) < 24 && Math.Abs(c.G - colour.G) < 24 && Math.Abs(c.B - colour.B) < 24) { hits++; break; }
            }
        return (double)hits / (to - from);
    }

    /// <summary>Whether a pixel is (close to) a given colour. A scanline through a row crosses glyphs, so a
    /// check about the BACKGROUND has to look at more than one pixel and take the commonest answer.</summary>
    private static bool IsBackground(Bitmap bmp, int x, int y, Color colour)
    {
        if (x < 0 || x >= bmp.Width || y < 0 || y >= bmp.Height) return false;
        int hits = 0;
        for (int dy = -2; dy <= 2; dy++)
        {
            int yy = Math.Clamp(y + dy, 0, bmp.Height - 1);
            var c = bmp.GetPixel(x, yy);
            if (Math.Abs(c.R - colour.R) < 30 && Math.Abs(c.G - colour.G) < 30 && Math.Abs(c.B - colour.B) < 30) hits++;
        }
        return hits >= 3;
    }

    /// <summary>Every occurrence of the find term is marked on every visible line, and the line the search
    /// landed on is marked more strongly - which is how navigation can stay line-by-line without leaving you
    /// wondering which line it meant.</summary>
    private static bool RunFindHighlightChecks()
    {
        Line("-- find highlighting --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_hl_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < 20; i++) sb.Append($"line {i:00} alpha middle alpha tail\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var settings = new AppSettings();
            var grid = new LineGridControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(700, 300),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(grid);
            grid.Attach(doc, settings);
            host.Show();
            Pump();

            string text = doc.GetLineText(1);
            int first = text.IndexOf("alpha", StringComparison.Ordinal);
            int second = text.IndexOf("alpha", first + 1, StringComparison.Ordinal);
            int gap = text.IndexOf("middle", StringComparison.Ordinal);
            int X(int index) => grid.XForCharForTesting(1, index);

            grid.SetFindHighlight(FindEngine.CompileQuery(new FindQuery("alpha", false, false)));
            grid.RefreshView();
            Pump();

            using (var picture = Capture(host))
            {
                int y = grid.RowMiddleForTesting(1);
                bool ok0 = Check("the first occurrence is marked", PixelFraction(picture, X(first), X(first + 5), y, settings.FindHighlight) > 0.5);
                ok0 &= Check("so is the second one on the same line", PixelFraction(picture, X(second), X(second + 5), y, settings.FindHighlight) > 0.5);
                ok0 &= Check("the text between them is not", PixelFraction(picture, X(gap), X(gap + 6), y, settings.FindHighlight) < 0.2);
                if (!ok0) return false;
            }

            // The line the search landed on is marked more strongly than the rest.
            grid.SelectRowForAccessibility(1);
            grid.RefreshView();
            Pump();
            using (var picture = Capture(host))
            {
                bool ok1 = Check("the line the search landed on is marked differently",
                                 PixelFraction(picture, X(first), X(first + 5), grid.RowMiddleForTesting(1), settings.FindCurrent) > 0.5);
                ok1 &= Check("other lines keep the ordinary mark",
                             PixelFraction(picture, X(first), X(first + 5), grid.RowMiddleForTesting(2), settings.FindHighlight) > 0.5);
                if (!ok1) return false;
            }

            // Putting the term away takes the marks with it.
            grid.SetFindHighlight(null);
            grid.RefreshView();
            Pump();
            using (var picture = Capture(host))
            {
                bool ok2 = Check("clearing the term clears the marks",
                                 PixelFraction(picture, X(first), X(first + 5), grid.RowMiddleForTesting(2), settings.FindHighlight) < 0.2);
                return ok2;
            }
        }
        finally
        {
            host?.Close();
            host?.Dispose();
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>What a search reports. The rules are all about saying no more than there is to say: no
    /// occurrence count when every line matched once, no hidden count when nothing is hidden, and a "+" on
    /// anything the sweep has not finished counting.</summary>
    private static bool RunFindStatusChecks()
    {
        Line("-- find status wording --");
        static FindTally T(long pos, long shown, long hidden, long shownOcc, long occ, bool complete = true, bool approx = false)
            => new(pos, shown, hidden, shownOcc, occ, complete, approx);

        string plain = FindStatusText.Short(T(12, 348, 0, 348, 348));
        bool ok = Check("the simple case says just where you are", plain == "Match 12 of 348", plain);

        string multi = FindStatusText.Short(T(12, 348, 0, 1204, 1204));
        ok &= Check("occurrences appear only when a line matched more than once",
                    multi == "Match 12 of 348 lines \u00b7 1,204 hits", multi);

        string hiddenText = FindStatusText.Short(T(12, 252, 96, 891, 1204));
        ok &= Check("hidden matches are reported apart from shown ones",
                    hiddenText == "Match 12 of 252 lines \u00b7 96 hidden \u00b7 891 of 1,204 hits", hiddenText);

        string partial = FindStatusText.Short(T(12, 252, 96, 891, 1204, complete: false));
        ok &= Check("an unfinished sweep marks every count", partial.Count(c => c == '+') == 4, partial);

        string none = FindStatusText.Short(T(0, 0, 0, 0, 0));
        ok &= Check("nothing found says so", none == "No matches", none);

        string searching = FindStatusText.Short(T(0, 0, 0, 0, 0, complete: false));
        ok &= Check("nothing found yet does not", searching == "Searching\u2026", searching);

        string offMatch = FindStatusText.Short(T(0, 348, 0, 348, 348));
        ok &= Check("off a match the count says what it is a count of", offMatch == "348 matches", offMatch);

        string offMatchDetailed = FindStatusText.Short(T(0, 252, 96, 891, 1204));
        ok &= Check("and still splits the shown from the hidden",
                    offMatchDetailed == "252 lines \u00b7 96 hidden \u00b7 891 of 1,204 hits", offMatchDetailed);

        ok &= Check("no bare number ever reaches the status bar",
                    !long.TryParse(offMatch.Replace(",", ""), out _) &&
                    !long.TryParse(plain.Replace(",", ""), out _), $"{offMatch} / {plain}");

        string approx = FindStatusText.Short(T(1, 10, 0, 99, 99, approx: true));
        ok &= Check("a floored occurrence count says it is a floor", approx.Contains('\u2265'), approx);

        string detail = FindStatusText.Long(T(12, 252, 96, 891, 1204), "disk");
        ok &= Check("the long form names the term and holds every number",
                    detail.Contains("disk") && detail.Contains("252") && detail.Contains("96") &&
                    detail.Contains("891") && detail.Contains("1,204"), detail);

        return ok;
    }

    /// <summary>The hover tip is the only place the app answers "why is this line here, and why that
    /// colour?". It has to name every filter that matched - including switched-off ones, which are the whole
    /// point of asking - and spell out patterns in full, since a friendly description is exactly what stops
    /// being enough at that moment.</summary>
    private static bool RunFilterTipChecks()
    {
        Line("-- filter tips --");
        var filters = new FilterCollection();

        var error = new Filter { Enabled = true, Description = "Errors", Match = { Text = "ERROR" } };
        filters.Add(error);
        var timeout = new Filter { Enabled = true, Match = { Text = "timeout" } };
        filters.Add(timeout, error);
        var noisy = new Filter { Enabled = false, Match = { Text = "heartbeat" } };
        filters.Add(noisy);
        var drop = new Filter { Enabled = true, Kind = FilterKind.Exclude, Match = { Text = "healthz" } };
        filters.Add(drop);
        var rx = new Filter { Enabled = false, Match = { Text = "[0-9]+ms", Regex = true, CaseSensitive = true } };
        filters.Add(rx);

        bool ok = Check("nothing matched means no tip at all", FilterTipText.Build(Array.Empty<Filter>()).Length == 0);

        string tip = FilterTipText.Build(new[] { error, timeout });
        ok &= Check("a described filter still shows its pattern in full",
                    tip.Contains("Errors") && tip.Contains("ERROR"), tip);

        tip = FilterTipText.Build(new[] { noisy, error });
        ok &= Check("switched-on filters come first", tip.IndexOf("ERROR", StringComparison.Ordinal) <
                                                     tip.IndexOf("heartbeat", StringComparison.Ordinal), tip);
        ok &= Check("a switched-off filter says so", tip.Contains("heartbeat (off)"), tip);

        tip = FilterTipText.Build(new[] { drop });
        ok &= Check("an exclude is marked as one", tip.StartsWith('\u2260'), tip);

        tip = FilterTipText.Build(new[] { rx });
        ok &= Check("a regex reads as one", tip.Contains("/[0-9]+ms/"), tip);
        ok &= Check("case sensitivity is spelled out", tip.Contains("(case)"), tip);

        var many = new List<Filter>();
        for (int i = 0; i < FilterTipText.MaxListed + 5; i++)
            many.Add(new Filter { Enabled = true, Match = { Text = "f" + i } });
        tip = FilterTipText.Build(many);
        ok &= Check("a long list is cut short and says by how much",
                    tip.Split('\n').Length == FilterTipText.MaxListed + 1 && tip.EndsWith("and 5 more"), tip);

        // ...and end to end: the tip for a real line in a real grid.
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_tip_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, "ERROR db timeout after 30s\nplain line\nheartbeat ok\n", new UTF8Encoding(false));
        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();
            foreach (var f in filters.Roots) doc.Filters.Add(f.Clone(newIds: false));
            doc.ApplyFilters();
            WaitForFiltering(doc);

            var settings = new AppSettings();
            var grid = new LineGridControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(700, 300),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(grid);
            grid.Attach(doc, settings);
            host.Show();
            Pump();

            string first = grid.TipTextForTesting(0);
            ok &= Check("the tip names the filters that matched the line",
                        first.Contains("Errors") && first.Contains("timeout"), first);
            ok &= Check("and not one that did not", !first.Contains("healthz"), first);
            ok &= Check("a line nothing matched gets no tip", grid.TipTextForTesting(1).Length == 0,
                        grid.TipTextForTesting(1));
            ok &= Check("a switched-off filter that matched is still named",
                        grid.TipTextForTesting(2).Contains("heartbeat (off)"), grid.TipTextForTesting(2));
            return ok;
        }
        finally
        {
            host?.Close();
            host?.Dispose();
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>Word wrap breaks the "one row, one line of pixels" rule the whole view is built on, so what
    /// matters is that everything downstream reads where a row was actually painted: hit-testing, how many
    /// rows fit, and the accessible bounds.</summary>
    private static bool RunWordWrapChecks()
    {
        Line("-- word wrap --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_wrap_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < 60; i++)
            sb.Append(i % 3 == 0 ? $"line {i:00} short\n" : $"line {i:00} " + string.Join(' ', Enumerable.Repeat("wordy", 40)) + "\n");
        sb.Append("runaway " + string.Join(' ', Enumerable.Repeat("endless", 600)) + "\n");
        for (int i = 0; i < 10; i++) sb.Append($"tail {i}\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var settings = new AppSettings();
            var grid = new LineGridControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(420, 400),   // narrow, so the long lines have to break
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(grid);
            grid.Attach(doc, settings);
            host.Show();
            Pump();

            bool ok = Check("a long line is one row while wrapping is off", grid.SegmentsForTesting(1) == 1,
                            $"{grid.SegmentsForTesting(1)} segments");
            int rowsWithoutWrap = grid.RowsPaintedForTesting;

            settings.WordWrap = true;
            grid.RefreshView();
            Pump();

            ok &= Check("a long line breaks into several rows", grid.SegmentsForTesting(1) > 1,
                        $"{grid.SegmentsForTesting(1)} segments");
            ok &= Check("a short line still takes one", grid.SegmentsForTesting(0) == 1,
                        $"{grid.SegmentsForTesting(0)} segments");
            ok &= Check("fewer lines fit once they wrap", grid.RowsPaintedForTesting < rowsWithoutWrap,
                        $"{grid.RowsPaintedForTesting} of {rowsWithoutWrap}");

            // Clicking a wrapped row's second segment has to select that row, not the one below it.
            long tall = 1;
            int top = grid.RowTopForTesting(tall);
            grid.ClickForTesting2(top + grid.RowHeightForTesting + 2, 60);
            ok &= Check("clicking the second half of a wrapped line selects that line",
                        grid.CaretRowForTesting == tall, $"caret on row {grid.CaretRowForTesting}");

            // ...and the row after it is still reachable, at its own place further down.
            int after = grid.RowTopForTesting(2);
            grid.ClickForTesting2(after + 2, 60);
            ok &= Check("the row below a wrapped one is still where it is drawn",
                        grid.CaretRowForTesting == 2, $"caret on row {grid.CaretRowForTesting}");

            // Page down must move by what is actually on screen, or it skips content.
            grid.ScrollToRow(0);
            grid.RefreshView();
            Pump();
            long caretBefore = Math.Max(0, grid.CaretRowForTesting);
            int fits = grid.RowsPaintedForTesting;
            grid.PressKeyForTesting(Keys.PageDown);
            Pump();
            ok &= Check("page down moves by the rows that fit, not by the rows that would have",
                        grid.CaretRowForTesting > caretBefore && grid.CaretRowForTesting <= caretBefore + fits,
                        $"caret {caretBefore} -> {grid.CaretRowForTesting} with {fits} rows on screen");

            // A pathological line must not be allowed to fill the window on its own.
            grid.ScrollToRow(60);
            grid.RefreshView();
            Pump();
            ok &= Check("a runaway line is capped rather than taking over the window",
                        grid.SegmentsForTesting(60) is > 1 and <= 20,
                        $"{grid.SegmentsForTesting(60)} segments, top row {grid.FirstRowForTesting}, {grid.RowsPaintedForTesting} painted, {doc.RowCount} rows");

            // Wrapping fits fewer lines on screen, so scrolling has to be allowed to go further, or the end
            // of the file becomes unreachable.
            grid.ScrollToRow(doc.RowCount - 1);
            grid.RefreshView();
            Pump();
            ok &= Check("the end of the file is still reachable while wrapping",
                        grid.SegmentsForTesting(doc.RowCount - 1) >= 1,
                        $"top row {grid.FirstRowForTesting} of {doc.RowCount}");

            grid.ScrollToRow(0);
            grid.RefreshView();
            Pump();
            settings.WordWrap = false;
            grid.RefreshView();
            Pump();
            ok &= Check("turning it off puts the lines back on one row each", grid.SegmentsForTesting(1) == 1,
                        $"{grid.SegmentsForTesting(1)} segments");

            return ok;
        }
        finally
        {
            host?.Close();
            host?.Dispose();
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>Where a dragged filter lands is decided by the pointer alone: vertical position picks the
    /// gap, horizontal picks the nesting. Every rule here is a judgement about how the list should feel.</summary>
    private static bool RunDropPlacementChecks()
    {
        Line("-- drag placement --");
        const int h = 20, indent = 16;
        // level 0 / level 1 / level 0, twenty pixels each.
        var rows = new List<DropRow> { new(0, 0, h), new(1, h, h), new(0, h * 2, h) };

        DropSpot At(int y, int x) => DropPlacement.For(rows, y, x, indent);

        bool ok = Check("above the middle of the first row drops before it", At(5, 0).Slot == 0);
        ok &= Check("below the middle of the first row drops after it", At(15, 0).Slot == 1);
        ok &= Check("past the last row drops at the end", At(55, 0).Slot == 3);

        // In the gap between a level-0 row and its level-1 child there is only one legal depth: any
        // shallower and the child below would be orphaned from the parent above.
        ok &= Check("a gap with only one legal depth ignores the pointer's x",
                    At(15, 0).Level == 1 && At(15, indent * 5).Level == 1);

        // Between the level-1 child and the next level-0 row, anything from 0 to 2 is legal.
        ok &= Check("x at the left edge drops at the top level", At(35, 0).Level == 0);
        ok &= Check("x one indent in nests one level", At(35, indent).Level == 1);
        ok &= Check("x two indents in nests under the row above", At(35, indent * 2).Level == 2);
        ok &= Check("x beyond the row above's depth is clamped", At(35, indent * 9).Level == 2);

        // Nothing can be nested under a row that is not there.
        ok &= Check("the first gap of all can only be top level",
                    At(-5, indent * 4).Level == 0 && At(-5, 0).Slot == 0);
        return ok;
    }

    /// <summary>Dragging a filter rearranges the list under the pointer, which is exactly what makes it
    /// easy to break: re-homing the node scrolls the list, and a subtree at full height fills the pane it
    /// is being dragged through. Either one slides the rows out from under a pointer that has not moved,
    /// so the filter leaps several places at once instead of walking. These checks pin the walk.</summary>
    private static bool RunFilterDragChecks()
    {
        Line("-- dragging a filter --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_drag_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, "one line is enough\n", new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var tree = new FilterTreeControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                // Deliberately shorter than the list: the bugs this guards only appear once the list
                // has to scroll, so a pane that shows everything would pass no matter what.
                ClientSize = new Size(300, 520),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(tree);
            tree.Attach(doc);
            host.Show();
            Pump();

            var filters = new FilterCollection();
            for (int i = 0; i < 60; i++)
                filters.Roots.Add(new Filter { Match = new FilterMatch { Text = $"f{i:00}" } });
            var carried = filters.Roots[^1];
            for (int c = 0; c < 6; c++)
            {
                var kid = new Filter { Match = new FilterMatch { Text = $"kid{c}" } };
                filters.Roots.Add(kid);
                filters.Move(kid, carried, carried.Children.Count);
            }
            doc.SetFilters(filters);
            tree.Rebuild();
            Pump();

            int rowH = tree.RowHeightForTesting;
            int viewport = tree.TreeHeightForTesting;
            bool ok = Check($"the pane is too short for the list, so dragging has to scroll " +
                            $"({viewport / rowH} rows of {filters.Roots.Count + carried.Children.Count})",
                            viewport / rowH < filters.Roots.Count);
            if (!ok) return false;

            // A filter can be picked up anywhere in its own content, the blank space between and after the
            // columns included - but not on the checkbox, where a press has to keep meaning tick.
            var row = tree.RowBoundsForTesting(filters.Roots[0]);
            int mid = row.Top + row.Height / 2;
            ok &= Check("a press on the filter's own text picks it up",
                        tree.PressArmsDragForTesting(new Point(row.Left + 2, mid)));
            ok &= Check("so does one out in the empty space to the right of it",
                        tree.PressArmsDragForTesting(new Point(tree.TreeWidthForTesting - 4, mid)));
            ok &= Check("a press on the checkbox does not",
                        !tree.PressArmsDragForTesting(new Point(row.Left - 2, mid)));
            ok &= Check("nor does one to the left of the checkbox",
                        !tree.PressArmsDragForTesting(new Point(0, mid)));

            // Grab the tall subtree, which sits last, and walk the pointer up a row at a time.
            tree.StartDragForTesting(carried, new Point(20, viewport - rowH));
            ok &= Check("a subtree is carried collapsed, so it cannot fill the pane it moves through",
                        !tree.IsExpandedForTesting(carried));

            var seen = new List<int>();
            var tops = new List<string>();
            // Stay clear of the edges: the auto-scroll zone is a row deep at each end, and scrolling is
            // meant to move the list, which would confuse a check about the pointer alone.
            var stops = new List<int>();
            for (int y = viewport - rowH * 2; y >= rowH * 2; y -= rowH) stops.Add(y);
            foreach (int y in stops)
            {
                tree.DragToForTesting(new Point(20, y));
                Pump();
                seen.Add(doc.Filters.Roots.IndexOf(carried));
                tops.Add(tree.VisibleFiltersForTesting.FirstOrDefault()?.Match.Text ?? "?");
            }

            int biggestStep = 0;
            for (int i = 1; i < seen.Count; i++) biggestStep = Math.Max(biggestStep, seen[i - 1] - seen[i]);
            ok &= Check($"one row of travel moves the filter one place [{string.Join(" ", seen)}]",
                        seen.Count > 2 && biggestStep == 1);
            ok &= Check($"it walks up rather than wandering [{string.Join(" ", seen)}]",
                        seen[^1] < seen[0] && seen.SequenceEqual(seen.OrderByDescending(v => v)));
            ok &= Check($"placing the filter does not scroll the list out from under the pointer " +
                        $"[{string.Join(" ", tops.Distinct())}]",
                        tops.Distinct().Count() == 1);

            // Back down the exact same positions. Where the filter lands has to follow from where the
            // pointer is rather than from how it got there, give or take the one place the filter itself
            // takes up in the list - drifting further than that is what "it jumped somewhere I did not
            // mean it to go" actually feels like.
            var back = new List<int>();
            for (int i = stops.Count - 1; i >= 0; i--)
            {
                tree.DragToForTesting(new Point(20, stops[i]));
                Pump();
                back.Add(doc.Filters.Roots.IndexOf(carried));
            }
            back.Reverse();
            int drift = seen.Zip(back, (d, u) => Math.Abs(d - u)).Max();
            ok &= Check($"the same pointer position gives the same place on the way back, within the one " +
                        $"place the filter itself occupies [down {string.Join(" ", seen)}] [up {string.Join(" ", back)}]",
                        drift <= 1);

            tree.DropForTesting();
            Pump();
            ok &= Check("dropping puts back what the user had open", tree.IsExpandedForTesting(carried));
            ok &= Check("the children came along", carried.Children.Count == 6);

            // The other half of it: a filter has to be able to reach somewhere that was not on screen
            // when the drag started. Holding at the bottom edge scrolls the list, and the filter has to
            // travel with it rather than being left behind while the view slides past. Start it at the
            // very top so the journey is far longer than one paneful.
            doc.Filters.Move(carried, null, 0);
            tree.Rebuild();
            Pump();
            tree.StartDragForTesting(carried, new Point(20, rowH));
            // Where every other filter sits relative to its neighbours cannot change during the drag, so
            // this is a fixed ruler to read the view's travel against.
            var ruler = doc.Filters.Roots.Where(r => !ReferenceEquals(r, carried)).ToList();
            List<int> Travel(int y, int ticks)
            {
                var seenAt = new List<int>();
                // Point at the edge once and then hold perfectly still: with the mouse stationary the drag
                // events stop arriving, so everything from here has to come from the scroll itself.
                tree.DragToForTesting(new Point(20, y));
                for (int i = 0; i < ticks; i++)
                {
                    tree.AutoScrollTickForTesting();
                    Pump();
                    // Read the travel off the topmost row that is NOT the one being dragged: at the top
                    // edge the dragged filter is legitimately the first row, and it moves by design.
                    var settled = tree.VisibleFiltersForTesting.FirstOrDefault(f => !ReferenceEquals(f, carried));
                    if (settled is not null && ruler.IndexOf(settled) is var ix && ix >= 0) seenAt.Add(ix);
                }
                return seenAt;
            }

            var down = Travel(viewport - 2, 80);
            ok &= Check($"holding at the bottom edge carries the filter all the way to the end " +
                        $"(place {doc.Filters.Roots.IndexOf(carried)} of {doc.Filters.Roots.Count - 1})",
                        doc.Filters.Roots.IndexOf(carried) == doc.Filters.Roots.Count - 1);
            ok &= Check($"the view slides steadily down the list instead of jumping about " +
                        $"[{string.Join(" ", down.Distinct())}]",
                        down.Count > 2 && down.SequenceEqual(down.OrderBy(v => v)));

            // And back the other way, which is the direction that used to fling it about.
            var up = Travel(2, 80);
            ok &= Check($"holding at the top edge carries it all the way back to the start " +
                        $"(place {doc.Filters.Roots.IndexOf(carried)})",
                        doc.Filters.Roots.IndexOf(carried) == 0);
            ok &= Check($"and slides steadily back up [{string.Join(" ", up.Distinct())}]",
                        up.Count > 2 && up.SequenceEqual(up.OrderByDescending(v => v)));
            tree.DropForTesting();
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>A filter's checkbox has to keep meaning that filter and nothing else: a parent's pattern is
    /// required of its children whether or not the parent is on, so "off here, on underneath" is a real and
    /// useful arrangement that cascading by default would wipe out. Shift is what asks for the subtree.</summary>
    private static bool RunFilterEnableChecks()
    {
        Line("-- enabling a filter and its subtree --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_enable_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, "one line is enough\n", new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var tree = new FilterTreeControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(300, 400),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(tree);
            tree.Attach(doc);
            host.Show();
            Pump();

            var filters = new FilterCollection();
            var parent = new Filter { Match = new FilterMatch { Text = "parent" } };
            var other = new Filter { Match = new FilterMatch { Text = "other" } };
            filters.Roots.Add(parent);
            filters.Roots.Add(other);
            var kids = new List<Filter>();
            for (int i = 0; i < 3; i++)
            {
                var kid = new Filter { Match = new FilterMatch { Text = $"kid{i}" } };
                filters.Roots.Add(kid);
                filters.Move(kid, parent, parent.Children.Count);
                kids.Add(kid);
            }
            doc.SetFilters(filters);
            tree.Rebuild();
            Pump();

            int changes = 0;
            tree.FiltersChanged += () => changes++;
            bool Uniform(bool on) => parent.Enabled == on && kids.All(k => k.Enabled == on);
            bool ShownAsStored() => tree.IsCheckedForTesting(parent) == parent.Enabled &&
                                    kids.All(k => tree.IsCheckedForTesting(k) == k.Enabled);

            // The checkbox on its own: that filter, nothing else.
            tree.ToggleCheckboxForTesting(parent);
            bool ok = Check("ticking a filter turns on that filter and no other",
                            parent.Enabled && kids.All(k => !k.Enabled) && !other.Enabled);

            // Shift+Space: the whole subtree, to a single state rather than each flipped in turn.
            tree.SelectForTesting(parent);
            changes = 0;
            tree.PressKeyForTesting(Keys.Space | Keys.Shift);
            ok &= Check($"shift+space with the parent on turns the subtree off together",
                        Uniform(false));
            ok &= Check($"and reports the change once, not once per filter (raised {changes})", changes == 1);

            tree.PressKeyForTesting(Keys.Space | Keys.Shift);
            ok &= Check("shift+space again turns the subtree on together", Uniform(true));
            ok &= Check("a filter outside the subtree is left alone", !other.Enabled);
            ok &= Check("the checkboxes show what is actually stored", ShownAsStored());

            // A mixed subtree settles on one state. Flipping each in turn would leave this one odd.
            tree.ToggleCheckboxForTesting(kids[1]);
            ok &= Check("a single child can still be turned off on its own",
                        parent.Enabled && !kids[1].Enabled && kids[0].Enabled);
            tree.SelectForTesting(parent);
            tree.PressKeyForTesting(Keys.Space | Keys.Shift);
            ok &= Check($"a subtree in a mix of states settles on one, rather than each being flipped " +
                        $"[{string.Join(" ", new[] { parent }.Concat(kids).Select(f => f.Enabled ? "on" : "off"))}]",
                        Uniform(false) && ShownAsStored());

            // From a child, it is that child's own subtree - not the parent's.
            tree.SelectForTesting(kids[0]);
            tree.PressKeyForTesting(Keys.Space | Keys.Shift);
            ok &= Check("from a leaf it is just that leaf", kids[0].Enabled && !parent.Enabled && !kids[1].Enabled);

            // ---- double-clicking a row ----
            var row = tree.RowBoundsForTesting(other);
            int mid = row.Top + row.Height / 2;
            ok &= Check("double-clicking a filter's text asks to edit it",
                        ReferenceEquals(tree.DoubleClickForTesting(new Point(row.Left + 2, mid)), other));
            ok &= Check("so does double-clicking the empty space out to its right",
                        ReferenceEquals(tree.DoubleClickForTesting(new Point(tree.TreeWidthForTesting - 4, mid)), other));
            ok &= Check("double-clicking the checkbox does not",
                        tree.DoubleClickForTesting(new Point(row.Left - 2, mid)) is null);
            ok &= Check("nor does double-clicking left of it",
                        tree.DoubleClickForTesting(new Point(0, mid)) is null);

            // The real message sequence, because the tree's own handling of it is what used to leave the
            // tick and the filter disagreeing: it flipped the box and reported nothing.
            other.Enabled = false;
            tree.Rebuild();
            Pump();
            row = tree.RowBoundsForTesting(other);
            mid = row.Top + row.Height / 2;
            bool boxBefore = tree.IsCheckedForTesting(other);
            tree.SendDoubleClickForTesting(new Point(row.Left - 2, mid));
            Pump();
            bool boxAfter = tree.IsCheckedForTesting(other);
            ok &= Check($"two quick clicks on a checkbox tick it twice, leaving it as it was " +
                        $"({(boxBefore ? "on" : "off")} -> {(boxAfter ? "on" : "off")})", boxAfter == boxBefore);
            ok &= Check($"and the filter still agrees with its tick " +
                        $"(filter {(other.Enabled ? "on" : "off")}, box {(boxAfter ? "on" : "off")})",
                        other.Enabled == boxAfter);
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>Every option in a dialog should be reachable with Alt+letter, and no two may claim the same
    /// letter - a duplicate silently makes one of them unreachable, which is invisible on screen because
    /// Windows only underlines the letters while Alt is held.</summary>
    private static bool RunDialogKeyboardChecks()
    {
        Line("-- dialog keyboard access --");

        static IEnumerable<Control> Walk(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (var d in Walk(c)) yield return d;
            }
        }

        // The same call WinForms makes for Alt+letter, so this exercises the real dispatch rather than
        // just looking for an ampersand in the caption.
        static bool AltKey(Form form, char ch)
            => (bool)typeof(Control)
                .GetMethod("ProcessMnemonic", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(form, new object[] { ch })!;

        static char? MnemonicOf(string text) => SelfTest.MnemonicOf(text);

        bool ok = true;

        bool CheckDialog(string name, Form dlg, params string[] mustBeReachable)
        {
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = new Point(0, 0);
            dlg.Opacity = 0;
            dlg.Show();
            Pump();

            var claimed = new Dictionary<char, string>();
            var clashes = new List<string>();
            foreach (var c in Walk(dlg))
            {
                if (c is not (ButtonBase or Label) || MnemonicOf(c.Text) is not { } m) continue;
                if (claimed.TryGetValue(m, out string? already)) clashes.Add($"'{m}' on both \"{already}\" and \"{c.Text}\"");
                else claimed[m] = c.Text;
            }
            bool good = Check($"{name}: no two controls claim the same Alt key" +
                              (clashes.Count > 0 ? " [" + string.Join("; ", clashes) + "]" : $" ({claimed.Count} keys)"),
                              clashes.Count == 0);

            // Every tick box has to be operable from the keyboard, and pressing its key has to move it.
            foreach (var box in Walk(dlg).OfType<CheckBox>())
            {
                if (MnemonicOf(box.Text) is not { } m)
                {
                    good &= Check($"{name}: \"{box.Text}\" has an Alt key", false);
                    continue;
                }
                var before = box.CheckState;
                bool handled = AltKey(dlg, m);
                good &= Check($"{name}: Alt+{char.ToUpperInvariant(m)} works {box.Text}",
                              handled && box.CheckState != before);
            }

            foreach (string caption in mustBeReachable)
            {
                var hit = Walk(dlg).FirstOrDefault(c => c.Text == caption);
                good &= Check($"{name}: \"{caption.Replace("&", "")}\" can be reached with Alt",
                              hit is not null && MnemonicOf(caption) is not null);
            }

            // Everything is sized from the font and from DPI-scaled values, so at any scaling the frame has
            // to be at least as big as what it holds. A fixed size would clip here first.
            var content = dlg.Controls.Count > 0 ? dlg.Controls[0].PreferredSize : Size.Empty;
            good &= Check($"{name}: nothing is clipped at this DPI " +
                          $"(frame {dlg.ClientSize.Width}x{dlg.ClientSize.Height}, content {content.Width}x{content.Height})",
                          dlg.ClientSize.Width >= content.Width && dlg.ClientSize.Height >= content.Height);

            dlg.Close();
            dlg.Dispose();
            Pump();
            return good;
        }

        var filter = new Filter { Match = { Text = "sample text" } };
        ok &= CheckDialog("filter", new FilterEditDialog(filter, isNew: true),
                          "&Matches text", "Mar&ked by marker", "&Text:", "&Description:");
        ok &= CheckDialog("find", new FindDialog((_, _) => { }), "&Find Next", "Find &Previous", "Fi&nd:");

        // A message appearing must not shove the rest of the dialog sideways. A TableLayoutPanel hands out
        // its cells to the VISIBLE controls in order, so a control that appears can take a cell meant for
        // something else and drag a whole column along with it.
        bool NothingShifts(string name, Form dlg, Action show)
        {
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = new Point(0, 0);
            dlg.Opacity = 0;
            dlg.Show();
            Pump();

            List<(string Label, Rectangle Bounds, bool Shown)> Snapshot()
            {
                var list = new List<(string, Rectangle, bool)>();
                int i = 0;
                foreach (var c in Walk(dlg))
                {
                    string text = c.Text.Replace("&", "");
                    list.Add(($"{c.GetType().Name}#{i++}{(text.Length > 0 ? $" \"{text}\"" : "")}", c.Bounds, c.Visible));
                }
                return list;
            }

            var before = Snapshot();
            show();
            Pump();
            var after = Snapshot();

            var moved = new List<string>();
            foreach (var (label, bounds, shown) in before)
            {
                if (!shown) continue;   // something appearing has to take up its place; the rest must not move
                var now = after.FirstOrDefault(a => a.Label == label);
                if (now.Label is null || now.Bounds.Location == bounds.Location) continue;
                moved.Add($"{label} {bounds.X},{bounds.Y}->{now.Bounds.X},{now.Bounds.Y}");
            }
            bool good = Check($"{name}: showing a message moves nothing that was already on screen" +
                              (moved.Count > 0 ? " [" + string.Join("; ", moved) + "]" : ""),
                              moved.Count == 0);
            dlg.Close();
            dlg.Dispose();
            Pump();
            return good;
        }

        var findDlg = new FindDialog((_, _) => { });
        ok &= NothingShifts("find", findDlg, () => findDlg.SetStatus("Not found."));

        // Searching brings up the progress bar and turns the Cancel button on, which is the other thing that
        // used to push the buttons about.
        var busyDlg = new FindDialog((_, _) => { });
        ok &= NothingShifts("find (searching)", busyDlg, () => { busyDlg.SetSearching(true); busyDlg.SetProgress(0.4); });

        // The filter dialog's regex error line is the same shape of thing.
        var broken = new Filter { Match = { Text = "fine", Regex = true } };
        var editDlg = new FilterEditDialog(broken, isNew: true);
        ok &= NothingShifts("filter", editDlg, () => editDlg.SetTextForTesting("((unclosed"));
        return ok;
    }

    /// <summary>The find bar as a text box: pressing Enter is a request to search, not a reason to disturb
    /// what has been typed. Repeating a search must also cost nothing - it is held down.</summary>
    private static bool RunFindBarChecks()
    {
        Line("-- the find bar --");
        var searched = new List<(FindQuery Query, bool Forward)>();
        var dlg = new FindDialog((q, f) => searched.Add((q, f)));
        try
        {
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = new Point(0, 0);
            dlg.Opacity = 0;
            dlg.Show();
            Pump();

            dlg.SetTermForTesting("order-service", 3, 2);
            var before = dlg.SelectionForTesting();
            bool ok = Check("the term and the place in it are set up", dlg.TermForTesting() == "order-service" && before == (3, 2),
                            $"{dlg.TermForTesting()} at {before}");

            // Filling the drop-down used to reset the box, and it ran after every single search.
            dlg.SetHistory(new[] { "order-service", "earlier", "older still" });
            Pump();
            ok &= Check("recalling the history leaves the term alone", dlg.TermForTesting() == "order-service",
                        dlg.TermForTesting());
            ok &= Check("and leaves the caret and selection where they were",
                        dlg.SelectionForTesting() == before, $"{dlg.SelectionForTesting()} was {before}");

            dlg.EnterForTesting();
            Pump();
            ok &= Check("Enter searches for what is in the box",
                        searched.Count == 1 && searched[0].Query.Text == "order-service" && searched[0].Forward,
                        string.Join(",", searched.Select(s => s.Query.Text)));
            ok &= Check("and does not disturb it", dlg.TermForTesting() == "order-service" && dlg.SelectionForTesting() == before,
                        $"{dlg.TermForTesting()} at {dlg.SelectionForTesting()}");

            dlg.SetSearching(true);
            Pump();
            ok &= Check("a search that has to wait shows itself", dlg.SearchingForTesting());
            ok &= Check("and refuses a second one while it runs", RunsAnother() == 0);

            dlg.SetSearching(false);
            Pump();
            ok &= Check("and once it is done the box takes Enter again", RunsAnother() == 1);

            // Down reaches the terms searched for before - with the list open, so it is clear where the
            // term came from and what else is there.
            dlg.SetTermForTesting("", 0, 0);
            dlg.History = () => new[] { "newest", "middle", "oldest" };
            dlg.StepHistoryForTesting(1);
            Pump();
            ok &= Check("down opens the history", dlg.HistoryIsOpenForTesting());
            ok &= Check("and takes the most recent term first", dlg.TermForTesting() == "newest", dlg.TermForTesting());
            dlg.StepHistoryForTesting(1);
            ok &= Check("again goes further back", dlg.TermForTesting() == "middle", dlg.TermForTesting());
            dlg.StepHistoryForTesting(-1);
            ok &= Check("and up comes back", dlg.TermForTesting() == "newest", dlg.TermForTesting());
            dlg.StepHistoryForTesting(-1);
            ok &= Check("which stops at the most recent rather than emptying the box",
                        dlg.TermForTesting() == "newest", dlg.TermForTesting());

            int RunsAnother()
            {
                int was = searched.Count;
                dlg.EnterForTesting();
                return searched.Count - was;
            }

            // When the counts get re-read. Two things move underneath them - the sweep gathering matches and
            // the filters deciding which of them can be reached - and both have to be watched the same way.
            var fresh = TimeSpan.Zero;
            var old = TimeSpan.FromSeconds(1);
            bool Stale(bool swept = true, bool wasSwept = true, bool settled = true, bool wasSettled = true,
                       bool sameLine = true, bool sameFilters = true, bool sameHiding = true,
                       bool haveText = true, TimeSpan? age = null)
                => MainForm.TallyIsStale(swept, wasSwept, settled, wasSettled, sameLine, sameFilters,
                                         sameHiding, haveText, age ?? fresh);

            ok &= Check("a running sweep is re-read as it goes", Stale(swept: false, wasSwept: false, age: old));
            ok &= Check("but not faster than the eye", !Stale(swept: false, wasSwept: false, age: fresh));
            ok &= Check("the sweep finishing is a reason on its own", Stale(swept: true, wasSwept: false));
            ok &= Check("a settled search that nothing has touched is left alone", !Stale(age: old));
            ok &= Check("moving the caret changes which match you are on", Stale(sameLine: false));
            ok &= Check("and a filter edit changes what is hidden", Stale(sameFilters: false));
            ok &= Check("so does hiding or showing the lines that did not match", Stale(sameHiding: false));
            ok &= Check("a filter pass under way is re-read as it goes",
                        Stale(settled: false, wasSettled: false, age: old));
            ok &= Check("and once more when it settles", Stale(settled: true, wasSettled: false));

            return ok;
        }
        finally
        {
            dlg.Close();
            dlg.Dispose();
            Pump();
        }
    }

    /// <summary>Windows slides a progress bar's fill towards a rising value over a few hundred milliseconds,
    /// so a job that finishes quickly is over long before the fill arrives - the find bar crawled to a
    /// seventh full while the search itself was four fifths done. What is PAINTED is the only thing that
    /// matters here, and WM_PRINT (what DrawToBitmap uses) reports the slid position, not the value.</summary>
    private static bool RunProgressPaintChecks()
    {
        Line("-- progress bars paint what they are told --");

        var dlg = new FindDialog((_, _) => { });
        dlg.StartPosition = FormStartPosition.Manual;
        dlg.Location = new Point(0, 0);
        dlg.Opacity = 0;
        dlg.Show();
        Pump();
        dlg.SetSearching(true);
        Pump();

        var bar = AllControls(dlg).OfType<ProgressBar>().FirstOrDefault();
        bool ok = Check("find: the searching state has a progress bar", bar is not null);
        if (bar is not null)
        {
            // Straight from empty to most of the way along - the jump the slide is slowest to follow.
            dlg.SetProgress(0.8);
            double painted = PaintedFraction(bar);
            ok &= Check($"find: it paints the figure it was given at once, rather than crawling towards it " +
                        $"(asked 80%, painted {painted:P0})", Math.Abs(painted - 0.8) <= 0.1);
        }

        dlg.Close();
        dlg.Dispose();
        Pump();
        return ok;
    }

    /// <summary>A filter started from a log line has to arrive holding that line. It used to keep only the
    /// first 200 characters, and the lines worth filtering on are exactly the long ones.</summary>
    private static bool RunNewFilterFromLineChecks()
    {
        Line("-- a filter made from a log line --");

        string line = "[2026-07-16T18:06:48][inventory-svc][3][2FA8][315C][util][Func][INFO][TFLAG] " +
                      new string('x', 900) + " tail";
        string seeded = MainForm.SeedPatternFromLine("  " + line + "  \t");
        bool ok = Check($"the whole line is carried over, not a prefix ({seeded.Length} of {line.Length} characters)",
                        seeded == line);

        // Whatever is seeded has to survive being put in the box, or it is lost just as quietly.
        var dlg = new FilterEditDialog(new Filter { Match = { Text = seeded } }, isNew: true);
        dlg.StartPosition = FormStartPosition.Manual;
        dlg.Location = new Point(0, 0);
        dlg.Opacity = 0;
        dlg.Show();
        Pump();
        var box = AllControls(dlg).OfType<TextBox>().FirstOrDefault(t => t.Text.Length > 100);
        ok &= Check($"the edit box keeps all of it (holds {box?.Text.Length ?? -1})", box?.Text == line);
        dlg.Close();
        dlg.Dispose();
        Pump();

        string huge = MainForm.SeedPatternFromLine(new string('y', FilterEditDialog.MaxPatternLength + 5_000));
        ok &= Check($"an absurd line is cut to what the box can hold ({huge.Length})",
                    huge.Length == FilterEditDialog.MaxPatternLength);
        return ok;
    }

    /// <summary>Jumping to a particular line - Go To, find, per-filter find, marker navigation - lands it in
    /// the middle half of the view, so it arrives with context above and below instead of hard against an
    /// edge with nothing to read around it. Stepping about with the arrow keys keeps the old behaviour of
    /// scrolling as little as possible, which is why the two paths are separate.</summary>
    private static bool RunNavigationChecks()
    {
        Line("-- jumping to a line --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_nav_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < 400; i++) sb.Append($"line {i}\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var grid = new LineGridControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(500, 420),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(grid);
            grid.Attach(doc, new AppSettings());
            host.Show();
            Pump();

            // No filters, so a display row is its own file line and the offset arithmetic below is exact.
            int visible = grid.VisibleRowCountForTesting;
            long top = visible / 4;
            long bottom = Math.Max(top, visible * 3 / 4 - 1);
            bool ok = Check($"the view is tall enough for a middle half to mean anything " +
                            $"({visible} rows, band {top}..{bottom})", visible >= 9);
            if (!ok) return false;

            // GoToLine takes a 0-based line; the offset is how far down the view the line ended up.
            long Offset(long line) => line - grid.FirstRowForTesting;
            void Go(long line) { grid.GoToLine(line); Pump(); }

            Go(250);
            ok &= Check($"a line below the view arrives at the bottom of the middle half " +
                        $"(offset {Offset(250)} of {visible})", Offset(250) == bottom);

            Go(120);
            ok &= Check($"a line above the view arrives at the top of the middle half " +
                        $"(offset {Offset(120)} of {visible})", Offset(120) == top);

            // The point of the band being a range: walking through nearby matches must not drag the view
            // about, or repeated F3 turns into a flicker.
            long settled = grid.FirstRowForTesting;
            Go(120 + (bottom - top) / 2);
            ok &= Check("a line already inside the band does not move the view at all",
                        grid.FirstRowForTesting == settled);

            // Both ends of the file cannot honour the band, and must simply stop rather than scroll into
            // blank space.
            Go(1);
            ok &= Check($"near the start the view stops at the top (row {grid.FirstRowForTesting})",
                        grid.FirstRowForTesting == 0);
            Go(399);
            ok &= Check($"near the end the view stops at the last screenful (row {grid.FirstRowForTesting})",
                        grid.FirstRowForTesting == 400 - visible);

            // Marker navigation is the other jump, and reaches the view by a different route.
            doc.Markers.Toggle(300, 0);
            Go(120);
            grid.PressKeyForTesting(Keys.D1);
            Pump();
            ok &= Check($"jumping to the next marker also lands in the band (offset {Offset(300)} of {visible})",
                        grid.CaretRowForTesting == 300 && Offset(300) == bottom);

            // Arrow keys are a different thing entirely: they move the caret one line, and the view should
            // follow only when it has to. Walk to the bottom edge, which must not scroll, then one further.
            Go(250);
            long before = grid.FirstRowForTesting;
            for (long o = grid.CaretRowForTesting - before; o < visible - 1; o++) grid.PressKeyForTesting(Keys.Down);
            Pump();
            ok &= Check($"walking down inside the view does not scroll it (row {grid.FirstRowForTesting}, " +
                        $"caret {grid.CaretRowForTesting})",
                        grid.FirstRowForTesting == before && grid.CaretRowForTesting == before + visible - 1);
            grid.PressKeyForTesting(Keys.Down);
            Pump();
            ok &= Check($"walking off the bottom scrolls one line, not back into the band " +
                        $"({before} -> {grid.FirstRowForTesting})",
                        grid.FirstRowForTesting == before + 1);
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>Searching the filter list has the same problem as jumping to a log line: a match pinned to
    /// the bottom edge hides the siblings that give it its meaning.</summary>
    private static bool RunFilterSearchRevealChecks()
    {
        Line("-- finding a filter in the list --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_fsearch_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, "one\ntwo\nthree\n", new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var tree = new FilterTreeControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(300, 520),   // the search box and header eat into this, so allow plenty
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(tree);
            tree.Attach(doc);
            host.Show();
            Pump();

            // Far more filters than fit, with the two targets deliberately deep in the list and apart, so a
            // jump to either really does have to scroll.
            var filters = new List<Filter>();
            for (int i = 0; i < 60; i++)
                filters.Add(new Filter { Match = new FilterMatch { Text = i == 20 ? "zulu early" : i == 45 ? "zulu late" : $"alpha {i}" } });
            SetFilters(doc, tree, filters.ToArray());

            int visible = Math.Max(1, tree.TreeHeightForTesting / Math.Max(1, tree.RowHeightForTesting));
            int top = visible / 4;
            int bottom = Math.Max(top, visible * 3 / 4 - 1);
            bool ok = Check($"the filter pane is tall enough for a middle half to mean anything " +
                            $"({visible} rows, band {top}..{bottom})", visible >= 9);
            if (!ok) return false;

            int OffsetOf(string text) =>
                tree.VisibleFiltersForTesting.FindIndex(f => f.Match.Text == text);

            // Typing jumps to the first match, which is below the view.
            tree.SetSearchText("zulu");
            Pump();
            ok &= Check($"a filter below the view arrives at the bottom of the middle half " +
                        $"(offset {OffsetOf("zulu early")} of {visible})", OffsetOf("zulu early") == bottom);

            // F3 walks to the next match, further down again.
            tree.PressKeyForTesting(Keys.F3);
            Pump();
            ok &= Check($"the next match down also arrives at the bottom of the band " +
                        $"(offset {OffsetOf("zulu late")} of {visible})", OffsetOf("zulu late") == bottom);

            // Shift+F3 goes back up, so the match comes in at the top of the band instead.
            tree.PressKeyForTesting(Keys.F3 | Keys.Shift);
            Pump();
            ok &= Check($"a match above the view arrives at the top of the middle half " +
                        $"(offset {OffsetOf("zulu early")} of {visible})", OffsetOf("zulu early") == top);
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>The letter Alt activates for a caption, or null when it declares none.</summary>
    private static char? MnemonicOf(string text)    {
        int i = text.IndexOf('&');
        return i >= 0 && i + 1 < text.Length && text[i + 1] != '&' ? char.ToLowerInvariant(text[i + 1]) : null;
    }

    /// <summary>An Alt key has to be unique within its own menu. Where two items claim the same letter
    /// Windows cycles between them rather than running either, so the key must be pressed twice and then
    /// Enter - and nothing complains, which is how five of these had quietly accumulated.</summary>
    private static bool RunMenuMnemonicChecks()
    {
        Line("-- menu keyboard access --");

        // Built by the constructor; arguments are only acted on once the form is shown, so constructing one
        // and never showing it opens no file and touches no settings.
        using var form = new MainForm(new AppSettings(), new MachineState(), Array.Empty<string>());
        if (form.MainMenuStrip is not { } bar) return Check("the menu bar was built", false);

        bool Walk(string path, ToolStripItemCollection items)
        {
            var claimed = new Dictionary<char, string>();
            var clashes = new List<string>();
            foreach (ToolStripItem item in items)
            {
                if (MnemonicOf(item.Text ?? "") is not { } m) continue;
                if (claimed.TryGetValue(m, out string? already)) clashes.Add($"'{m}' on \"{already}\" and \"{item.Text}\"");
                else claimed[m] = item.Text ?? "";
            }
            bool good = Check($"{path}: no two items claim the same Alt key" +
                              (clashes.Count > 0 ? " [" + string.Join("; ", clashes) + "]"
                                                 : $" ({string.Join(",", claimed.Keys.OrderBy(c => c))})"),
                              clashes.Count == 0);

            foreach (ToolStripItem item in items)
                if (item is ToolStripMenuItem sub && sub.DropDownItems.Count > 0)
                    good &= Walk($"{path} > {(sub.Text ?? "").Replace("&", "")}", sub.DropDownItems);
            return good;
        }

        return Walk("menu", bar.Items);
    }

    private static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in AllControls(c)) yield return d;
        }
    }

    /// <summary>How much of a progress bar's width is actually coloured in, 0..1.</summary>
    private static double PaintedFraction(ProgressBar bar)
    {
        using var bmp = new Bitmap(Math.Max(1, bar.Width), Math.Max(1, bar.Height));
        bar.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        int y = bmp.Height / 2;
        var empty = bmp.GetPixel(bmp.Width - 2, y);   // the far end, which 80% never reaches
        int filled = 0;
        for (int x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (Math.Abs(c.R - empty.R) + Math.Abs(c.G - empty.G) + Math.Abs(c.B - empty.B) > 40) filled++;
        }
        return (double)filled / bmp.Width;
    }

    private static Bitmap Capture(Form host)    {
        var bmp = new Bitmap(host.ClientSize.Width, host.ClientSize.Height);
        host.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        return bmp;
    }

    private static bool SameRegion(Bitmap a, Bitmap b, Rectangle r)
        => FirstDifference(a, b, r) is null;

    private static Point? FirstDifference(Bitmap a, Bitmap b, Rectangle r)
    {
        for (int y = r.Top; y < r.Bottom && y < a.Height; y++)
            for (int x = r.Left; x < r.Right && x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return new Point(x, y);
        return null;
    }

    private static void WriteRenderDiagnostics(string check, Form host, LineGridControl grid, Rectangle area,
        Point diff, Bitmap before, Bitmap after)
    {
        try
        {
            TryDiagnosticLine("  render diagnostics:");
            WriteDiagnosticValue("check", () => check);
            WriteDiagnosticValue("terminal server session", () => SystemInformation.TerminalServerSession.ToString());
            WriteDiagnosticValue("dpi", () =>
            {
                using var graphics = host.CreateGraphics();
                return $"device={host.DeviceDpi}, graphics={graphics.DpiX:F1}x{graphics.DpiY:F1}";
            });
            WriteDiagnosticValue("host", () => $"client={host.ClientSize.Width}x{host.ClientSize.Height}, bounds={host.Bounds}");
            WriteDiagnosticValue("grid metrics", () =>
                $"gutter={grid.GutterWidthForTesting}, gutterTop/headerHeight={area.Top}, " +
                $"rowHeight={PrivateInt(grid, "_rowHeight")?.ToString() ?? "unknown"}, gutterArea={area}");
            WriteDiagnosticValue("first difference", () =>
            {
                Color beforePixel = before.GetPixel(diff.X, diff.Y);
                Color afterPixel = after.GetPixel(diff.X, diff.Y);
                return $"x={diff.X}, y={diff.Y}, before=0x{beforePixel.ToArgb():X8} ({beforePixel}), " +
                       $"after=0x{afterPixel.ToArgb():X8} ({afterPixel})";
            });
            WriteScreenDiagnostics();
            WriteDiagnosticValue("font smoothing", FontSmoothingSettings);
        }
        catch (Exception ex)
        {
            TryDiagnosticLine("  render diagnostics unavailable: " + ExceptionSummary(ex));
        }
    }

    private static void WriteDiagnosticValue(string label, Func<string> value)
    {
        try { TryDiagnosticLine($"    {label}: {value()}"); }
        catch (Exception ex) { TryDiagnosticLine($"    {label}: diagnostics unavailable ({ExceptionSummary(ex)})"); }
    }

    private static void TryDiagnosticLine(string text)
    {
        try { Line(text); }
        catch { }
    }

    private static void WriteScreenDiagnostics()
    {
        Screen[] screens;
        try { screens = Screen.AllScreens; }
        catch (Exception ex)
        {
            TryDiagnosticLine("    screens: diagnostics unavailable (" + ExceptionSummary(ex) + ")");
            return;
        }

        for (int i = 0; i < screens.Length; i++)
        {
            Screen screen = screens[i];
            WriteDiagnosticValue("screen", () =>
                $"{screen.DeviceName}, primary={screen.Primary}, bounds={screen.Bounds}, bpp={screen.BitsPerPixel}");
        }
    }

    private static string ExceptionSummary(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    private static int? PrivateInt(object target, string fieldName)
    {
        object? value = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);
        return value is int i ? i : null;
    }

    private static string FontSmoothingSettings()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key is null) return "registry key unavailable";
            string Value(string name) => $"{name}={key.GetValue(name) ?? "unset"}";
            return string.Join(", ", Value("FontSmoothing"), Value("FontSmoothingType"),
                Value("FontSmoothingGamma"), Value("FontSmoothingOrientation"));
        }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }

    /// <summary>Lets the UI finish whatever it has queued, and comes back as soon as it goes quiet. It used
    /// to wait a flat 250ms every time; the drag checks alone call it about 190 times, so nearly the whole
    /// self-test was spent sitting idle. The old wait is kept as the cap.</summary>
    private static void Pump()
    {
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 250;)
        {
            Application.DoEvents();
            if (PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE)) { Thread.Sleep(1); continue; }
            // Quiet once is not the same as settled - a timer may be about to post. Ask again after a pause.
            Thread.Sleep(15);
            Application.DoEvents();
            if (!PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE)) return;
        }
    }

    private const uint PM_NOREMOVE = 0;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool PeekMessage(out MSG message, IntPtr window, uint first, uint last, uint remove);

    /// <summary>Exported settings must come back exactly, or carrying them to another machine silently
    /// loses whichever preference was forgotten. Every persisted property is compared, so a newly added one
    /// is covered automatically. The export must also stay free of anything machine-specific, or importing
    /// it elsewhere plants paths that do not exist there.</summary>
    private static bool RunSettingsChecks()
    {
        Line("-- settings export/import --");
        string dir = Path.Combine(Path.GetTempPath(), "cascade_st_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var original = new AppSettings
            {
                FontFamily = "Cascadia Mono",
                FontSize = 13.5f,
                ZoomPercent = 130,
                TabSize = 7,
                ShowLineNumbers = false,
                MarkerVisibility = MarkerVisibilityMode.Never,
                ForegroundArgb = Color.Teal.ToArgb(),
                BackgroundArgb = Color.Ivory.ToArgb(),
                AutoLoadLastFilterFile = false
            };

            string file = Path.Combine(dir, "exported.json");
            original.ExportTo(file);

            var restored = new AppSettings();          // starts at defaults
            restored.ImportFrom(file);

            bool ok = true;
            foreach (var p in AppSettings.Persisted)
            {
                object? a = p.GetValue(original), b = p.GetValue(restored);
                bool same = a is IEnumerable<string> left && b is IEnumerable<string> right
                    ? left.SequenceEqual(right)
                    : Equals(a, b);
                ok &= Check($"round-trips {p.Name}", same);
            }

            // Nothing in the export may name this machine. Checked against the state class by reflection so
            // that a property moved into it later cannot quietly start leaking.
            string exported = File.ReadAllText(file);
            foreach (var p in typeof(MachineState).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                                                  .Where(p => p.CanWrite))
                ok &= Check($"export leaves out {p.Name}",
                            !exported.Contains($"\"{p.Name}\"", StringComparison.Ordinal));

            // Something that is not a settings file must be refused, not allowed to wipe the preferences.
            string junk = Path.Combine(dir, "junk.json");
            File.WriteAllText(junk, "definitely not json");
            bool refused = false;
            try { new AppSettings().ImportFrom(junk); }
            catch (InvalidDataException) { refused = true; }
            ok &= Check("refuses a file that is not settings", refused);
            return ok;
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>Per-machine state lives in its own file and survives a save/load round trip. Importing
    /// someone else's preferences must leave it untouched.</summary>
    private static bool RunMachineStateChecks()
    {
        Line("-- machine state --");
        string dir = Path.Combine(Path.GetTempPath(), "cascade_st_state_" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("CASCADE_SETTINGS_DIR");
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("CASCADE_SETTINGS_DIR", dir);
        try
        {
            var state = new MachineState { LastFilterFile = @"C:\somewhere\filters.cascade" };
            state.AddRecentFile(@"C:\logs\a.log");
            state.AddRecentFilterFile(@"C:\logs\f.cascade");
            state.Save();

            bool ok = Check("state is a separate file", File.Exists(MachineState.FilePath)
                                                        && MachineState.FilePath != AppSettings.FilePath);

            var reloaded = MachineState.Load();
            ok &= Check("round-trips RecentFiles", reloaded.RecentFiles.SequenceEqual(state.RecentFiles));
            ok &= Check("round-trips RecentFilterFiles", reloaded.RecentFilterFiles.SequenceEqual(state.RecentFilterFiles));
            ok &= Check("round-trips LastFilterFile", reloaded.LastFilterFile == state.LastFilterFile);

            // Importing preferences from another machine must not disturb any of it.
            string exported = Path.Combine(dir, "exported.json");
            new AppSettings { FontFamily = "Cascadia Mono" }.ExportTo(exported);
            new AppSettings().ImportFrom(exported);
            var afterImport = MachineState.Load();
            ok &= Check("import leaves recent files alone", afterImport.RecentFiles.SequenceEqual(state.RecentFiles));
            ok &= Check("import leaves the last filter file alone", afterImport.LastFilterFile == state.LastFilterFile);

            // Upgrading from the single-file layout must carry the old lists across rather than drop them.
            File.Delete(MachineState.FilePath);
            File.WriteAllText(AppSettings.FilePath,
                """{"FontFamily":"Consolas","RecentFiles":["C:\\logs\\old.log"],"LastFilterFile":"C:\\old.cascade"}""");
            var migrated = MachineState.Load();
            ok &= Check("reads state left in an old settings file",
                        migrated.RecentFiles.SequenceEqual([@"C:\logs\old.log"])
                        && migrated.LastFilterFile == @"C:\old.cascade");

            // A file that will not parse is kept, not quietly replaced by defaults on the next save.
            File.WriteAllText(AppSettings.FilePath, """{"FontFamily":"Cascadia Mono","Fon""");
            _ = AppSettings.Load();
            ok &= Check("keeps an unparseable settings file aside",
                        File.Exists(AppSettings.FilePath + ".bad"));
            return ok;
        }
        finally
        {
            Environment.SetEnvironmentVariable("CASCADE_SETTINGS_DIR", previous);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static bool RunEngineChecks()
    {
        Line("-- engine checks (temp file) --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < 5000; i++)
            sb.Append(i % 4 == 0 ? "ERROR disk " : i % 4 == 1 ? "ERROR net " : "info ").Append(i).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();
            bool ok = Check("indexed 5000 lines", doc.CompletedLineCount == 5000);

            var error = new Filter { Enabled = false, Match = { Text = "ERROR" } };
            var disk = new Filter { Enabled = true, Match = { Text = "disk" } };
            doc.Filters.Add(error);
            doc.Filters.Add(disk, error);
            doc.Filters.ShowOnlyFilteredLines = true;
            doc.ApplyFilters();
            WaitFilter(doc);

            // disabled parent still constrains: only "ERROR disk" (every 4th) match
            ok &= Check("hierarchical match = 1250", doc.MatchedLineCount == 1250);
            ok &= Check("row0 maps to line 0", doc.RowToLine(0) == 0);
            return ok;
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static bool RunFileChecks(string file, string? tat)
    {
        Line($"-- file checks: {file} --");
        var total = Stopwatch.StartNew();
        using var doc = new CascadeDocument();
        doc.Open(file);

        var first = Stopwatch.StartNew();
        while (doc.CompletedLineCount == 0 && first.ElapsedMilliseconds < 10000) Thread.Sleep(1);
        Line($"first lines available: {first.ElapsedMilliseconds} ms");

        doc.WaitForIndex();
        Line($"indexed {doc.CompletedLineCount:N0} lines in {total.ElapsedMilliseconds} ms");
        Line("first line: " + Truncate(doc.GetLineText(0), 100));
        Line($"encoding: {doc.Encoding.WebName}");

        int enabled;
        if (tat is not null && File.Exists(tat))
        {
            doc.SetFilters(TatImporter.Import(tat));
            int count = doc.Filters.EnumerateDepthFirst().Count();
            Line($"imported {count} filters from {tat}");
            foreach (var f in doc.Filters.Roots.Take(5)) f.Enabled = true;
            enabled = doc.Filters.EnumerateDepthFirst().Count(f => f.Enabled);
        }
        else
        {
            doc.Filters.Add(new Filter { Enabled = true, Match = { Text = "Error", CaseSensitive = false } });
            enabled = 1;
        }

        doc.Filters.ShowOnlyFilteredLines = true;
        var fsw = Stopwatch.StartNew();
        doc.ApplyFilters();
        WaitFilter(doc, 180000);
        Line($"filtered with {enabled} enabled filter(s): {doc.MatchedLineCount:N0} matches in {fsw.ElapsedMilliseconds} ms");

        bool ok = Check("matched count within total", doc.MatchedLineCount <= doc.CompletedLineCount);
        if (doc.RowCount > 0)
        {
            long l0 = doc.RowToLine(0);
            ok &= Check("row->line ascending", doc.RowCount < 2 || doc.RowToLine(1) > l0);
            Line("first match at line " + (l0 + 1) + ": " + Truncate(doc.GetLineText(l0), 100));
        }
        return ok;
    }

    private static void WaitFilter(CascadeDocument doc, int timeoutMs = 30000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (doc.IsIndexComplete && doc.IsFilterIdle) return;
            Thread.Sleep(3);
        }
    }

    private static bool Check(string name, bool condition)
    {
        Line((condition ? "[PASS] " : "[FAIL] ") + name);
        return condition;
    }

    /// <summary>Same, but reports what was actually seen when it fails - which is the difference between a
    /// failure you can act on and one you have to reproduce first.</summary>
    private static bool Check(string name, bool condition, string detail)
    {
        Line((condition ? "[PASS] " : "[FAIL] ") + name + (condition ? "" : $" [{detail}]"));
        return condition;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static void Line(string text)
    {
        _log.WriteLine(text);
        try { Console.WriteLine(text); } catch { /* no console attached */ }
    }
}

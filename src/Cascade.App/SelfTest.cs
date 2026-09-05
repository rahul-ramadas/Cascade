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
        // Several groups below build a real window, and a window writes preferences and the recent-file
        // lists - on its refresh timer as well as on the way out. Pointed at the developer's own directory
        // it would save the empty state it was constructed with, which wipes their recent files outright.
        string configDir = Path.Combine(Path.GetTempPath(), "cascade_selftest_cfg_" + Guid.NewGuid().ToString("N"));
        string? previousConfig = Environment.GetEnvironmentVariable("CASCADE_SETTINGS_DIR");
        Directory.CreateDirectory(configDir);
        Environment.SetEnvironmentVariable("CASCADE_SETTINGS_DIR", configDir);
        try
        {
            Line("=== Cascade self-test ===");
            Line("Log: " + LogPath);
            Line("Settings: " + configDir + " (throwaway)");

            string? file = args.FirstOrDefault(a => !a.StartsWith('/') && !a.StartsWith("--", StringComparison.Ordinal));
            string? tat = args.FirstOrDefault(a => a.StartsWith("/Filters:", StringComparison.OrdinalIgnoreCase))?["/Filters:".Length..].Trim('"');
            _only = args.FirstOrDefault(a => a.StartsWith("--only=", StringComparison.OrdinalIgnoreCase))?["--only=".Length..].Trim('"');
            _skipped = 0;
            if (_only is not null) Line($"(only groups matching \"{_only}\")");

            bool ok = Timed("engine", RunEngineChecks);
            ok &= Timed("settings", RunSettingsChecks);
            ok &= Timed("machine state", RunMachineStateChecks);
            ok &= Timed("render", RunRenderChecks);
            ok &= Timed("columns", RunColumnChecks);
            ok &= Timed("field settings", RunFieldSettingsChecks);
            ok &= Timed("column mode", RunColumnModeChecks);
            ok &= Timed("navigation", RunNavigationChecks);
            ok &= Timed("filter list", RunFilterListChecks);
            ok &= Timed("filter search", RunFilterSearchRevealChecks);
            ok &= Timed("filter presets", RunFilterPresetChecks);
            ok &= Timed("match map", RunMatchMapChecks);
            ok &= Timed("text selection", RunTextSelectionChecks);
            ok &= Timed("cell selection", RunColumnSelectionChecks);
            ok &= Timed("selection follows the text", RunSelectionFollowsTextChecks);
            ok &= Timed("underline", RunUnderlineChecks);
            ok &= Timed("restyling", RunRestyleChecks);
            ok &= Timed("colour while filtering", RunColourWhileFilteringChecks);
            ok &= Timed("hang watchdog", RunHangWatchdogChecks);
            ok &= Timed("automation", RunAutomationChecks);
            ok &= Timed("letting go of the filters", RunCloseFiltersChecks);
            ok &= Timed("find highlighting", RunFindHighlightChecks);
            ok &= Timed("find status wording", RunFindStatusChecks);
            ok &= Timed("word wrap", RunWordWrapChecks);
            ok &= Timed("text width", RunTextWidthChecks);
            ok &= Timed("filter tips", RunFilterTipChecks);
            ok &= Timed("find bar", RunFindBarChecks);
            ok &= Timed("find bar layout", RunFindBarLayoutChecks);
            ok &= Timed("find bar repaint", RunFindBarRepaintChecks);
            ok &= Timed("filter dialog repaint", RunFilterDialogRepaintChecks);
            ok &= Timed("find seed", RunFindSeedChecks);
            ok &= Timed("find bar room", RunFindBarRoomChecks);
            ok &= Timed("status bar", RunStatusBarChecks);
            ok &= Timed("elapsed times", RunElapsedChecks);
            ok &= Timed("encoding menu", RunEncodingMenuChecks);
            ok &= Timed("line spacing", RunLineSpacingChecks);
            ok &= Timed("drop placement", RunDropPlacementChecks);
            ok &= Timed("filter drag", RunFilterDragChecks);
            ok &= Timed("filter expand", RunFilterExpandChecks);
            ok &= Timed("drag nesting", RunDragNestingChecks);
            ok &= Timed("filter enable", RunFilterEnableChecks);
            ok &= Timed("filter selection", RunFilterSelectionChecks);
            ok &= Timed("appearance", RunAppearanceChecks);
            ok &= Timed("lucky colours", RunLuckyColorChecks);
            ok &= Timed("colour preview", RunColorPreviewChecks);
            ok &= Timed("style boxes", RunStyleBoxChecks);
            ok &= Timed("about box", RunAboutBoxChecks);
            ok &= Timed("update credential", RunUpdateCredentialChecks);
            ok &= Timed("filter list sync", RunFilterSyncChecks);
            ok &= Timed("new filter", RunNewFilterChecks);
            ok &= Timed("filter search bar", RunFilterSearchBarChecks);
            ok &= Timed("tab stops", RunTabStopChecks);
            ok &= Timed("dialog keyboard", RunDialogKeyboardChecks);
            ok &= Timed("menu keyboard", RunMenuMnemonicChecks);
            ok &= Timed("divider", RunSplitterChecks);
            ok &= Timed("window snapping", RunWindowSnapChecks);
            ok &= Timed("closing", RunClosingChecks);
            ok &= Timed("file drop", RunFileDropChecks);
            ok &= Timed("the menus", RunMenuActionChecks);
            ok &= Timed("resources", RunResourceChecks);
            ok &= Timed("progress paint", RunProgressPaintChecks);
            ok &= Timed("new filter from line", RunNewFilterFromLineChecks);
            ok &= Timed("copying", RunCopyBudgetChecks);
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
        finally
        {
            Environment.SetEnvironmentVariable("CASCADE_SETTINGS_DIR", previousConfig);
            try { Directory.Delete(configDir, true); } catch { /* best-effort */ }
            _log.Dispose();
        }
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

            // ...and the sideways scrollbar has to say so. It is drawn rather than borrowed now, so it only
            // reports a position because it was given one to report - and that is what the UI tests read.
            var hbar = grid.HScrollBarForTesting;
            ok &= Check("and the sideways scrollbar has somewhere to scroll to", hbar.MaxValue > 0,
                        $"max {hbar.MaxValue}");
            ok &= Check("and reports where it is to anyone asking",
                        hbar.AccessibilityObject.Value == hbar.Value.ToString() && hbar.Value > 0,
                        $"value {hbar.Value}, reported {hbar.AccessibilityObject.Value ?? "(null)"}");

            // ...and it knows its range before anything is painted. A window placed off the screen never
            // gets a paint, so a range measured only while drawing stays empty and Home and End have
            // nowhere to go - which is exactly the state the UI tests run in.
            using (var quiet = new Form { ClientSize = new Size(420, 300) })
            using (var unpainted = new LineGridControl())
            {
                quiet.Controls.Add(unpainted);
                unpainted.Dock = DockStyle.Fill;
                quiet.CreateControl();
                unpainted.Attach(doc, settings);
                unpainted.RefreshView();
                ok &= Check("and knows its range before it has painted anything",
                            unpainted.HScrollBarForTesting.MaxValue > 0 && unpainted.RowsPaintedForTesting == 0,
                            $"max {unpainted.HScrollBarForTesting.MaxValue}, " +
                            $"{unpainted.RowsPaintedForTesting} rows painted");
            }

            // ...and the End key drives it, which is the path the UI tests take.
            grid.ScrollHorizontallyTo(0);
            grid.PressKeyForTesting(Keys.End);
            ok &= Check("End takes the view to the far right", hbar.Value > 0 && hbar.Value == hbar.MaxValue,
                        $"value {hbar.Value} of {hbar.MaxValue}");
            grid.PressKeyForTesting(Keys.Home);
            ok &= Check("and Home brings it back", hbar.Value == 0, hbar.Value.ToString());

            // A drag is paced to the screen, which has to be asked what it can show - and a remote desktop
            // has no answer, because it has no refresh rate of its own. What is done with that matters most
            // exactly where it cannot be tried: a session begun on a fast monitor and then picked up over a
            // remote desktop must not go on drawing at the monitor's rate down the wire.
            ok &= Check("a screen that answers is taken at its word",
                        LineGridControl.RateFrom(240, remote: false) == 240 &&
                        LineGridControl.RateFrom(60, remote: false) == 60,
                        $"{LineGridControl.RateFrom(240, false)}, {LineGridControl.RateFrom(60, false)}");
            ok &= Check("one that does not falls to a rate every screen can manage, not to the last one seen",
                        LineGridControl.RateFrom(0, remote: false) == 60 &&
                        LineGridControl.RateFrom(1, remote: false) == 60,
                        $"{LineGridControl.RateFrom(0, false)}, {LineGridControl.RateFrom(1, false)}");
            ok &= Check("and down a wire nothing draws faster than the wire shows it",
                        LineGridControl.RateFrom(240, remote: true) == 30 &&
                        LineGridControl.RateFrom(1, remote: true) == 30 &&
                        LineGridControl.RateFrom(24, remote: true) == 24,
                        $"{LineGridControl.RateFrom(240, true)}, {LineGridControl.RateFrom(1, true)}, " +
                        $"{LineGridControl.RateFrom(24, true)}");

            var diff = FirstDifference(unscrolled, scrolled, margin);
            ok &= Check($"scrolling right leaves the line-number margin (0..{gutter}) untouched" +
                        (diff is null ? "" : $" [first differs at x={diff.Value.X},y={diff.Value.Y}: " +
                                              $"{unscrolled.GetPixel(diff.Value.X, diff.Value.Y)} -> " +
                                              $"{scrolled.GetPixel(diff.Value.X, diff.Value.Y)}]"),
                        diff is null);
            if (diff is not null)
                WriteRenderDiagnostics("scrolling right leaves the line-number margin untouched",
                    host, grid, margin, diff.Value, unscrolled, scrolled);

            // Only the stretch of a line that can land in the window is handed to GDI, which lays out
            // everything it is given whether or not it shows. What is left out is outside the clip either
            // way, so the two must draw the same picture - here, with a line running off BOTH edges, which
            // is where a character of overhang at either end would show up.
            grid.DrawWholeLinesForTesting = true;
            Pump();
            using (var whole = Capture(host))
            {
                grid.DrawWholeLinesForTesting = false;
                Pump();
                using var clipped = Capture(host);
                var partDiff = FirstDifference(whole, clipped, new Rectangle(0, 0, whole.Width, whole.Height));
                ok &= Check("drawing only the part of a line that shows draws what drawing all of it does" +
                            (partDiff is null ? "" : $" [first differs at x={partDiff.Value.X},y={partDiff.Value.Y}]"),
                            partDiff is null);
            }

            // The same, in every face a filter can ask for. A slanted or heavy cut hangs outside the cell
            // its width says it occupies, so the stretch handed over is widened by a character at each end -
            // and whether one character is enough is a question about typefaces, not about this machine.
            var faces = new FilterCollection();
            var styles = new (string Text, bool Bold, bool Italic, bool Underline)[]
                { ("line 1", true, false, false), ("line 2", false, true, false),
                  ("line 3", true, true, true),   ("line 4", false, false, true) };
            foreach (var (text, bold, italic, underline) in styles)
                faces.Roots.Add(new Filter
                {
                    Enabled = true,
                    Match = new FilterMatch { Text = text },
                    Style = { Bold = bold, Italic = italic, Underline = underline }
                });
            doc.SetFilters(faces);
            WaitForFiltering(doc);
            foreach (int scrolledTo in (int[])[0, 40, 260])
            {
                grid.ScrollHorizontallyTo(scrolledTo);
                grid.RefreshView();
                grid.DrawWholeLinesForTesting = true;
                Pump();
                using var whole = Capture(host);
                grid.DrawWholeLinesForTesting = false;
                Pump();
                using var clipped = Capture(host);
                var faceDiff = FirstDifference(whole, clipped, new Rectangle(0, 0, whole.Width, whole.Height));
                ok &= Check($"and does so in bold, italic and underline, scrolled to {scrolledTo}" +
                            (faceDiff is null ? "" : $" [first differs at x={faceDiff.Value.X},y={faceDiff.Value.Y}]"),
                            faceDiff is null);
            }
            doc.SetFilters(new FilterCollection());
            WaitForFiltering(doc);
            grid.ScrollHorizontallyTo(260);
            grid.RefreshView();
            Pump();

            // A face where the characters are not all one width cannot have its glyphs placed by
            // multiplication - asked of the SHAPES of every one of the eight faces, because a family may be
            // cut fixed-pitch in one and proportionally in another. Asked of the same measurement that lays
            // the text out, which is also what draws it, so the two cannot disagree about where a character
            // goes.
            string sample = "plain ascii 12345";
            bool[] fixedPitch = Enumerable.Range(0, 8).Select(i => grid.WidthWasArithmeticForTesting(sample, i)).ToArray();
            ok &= Check($"a fixed-pitch face takes the short road in every style [{string.Join(",", fixedPitch.Select(b => b ? '1' : '0'))}]",
                        fixedPitch.All(b => b), $"font {settings.FontFamily}");

            var proportional = new AppSettings { MarkerVisibility = MarkerVisibilityMode.Always, FontFamily = "Segoe UI" };
            grid.ApplySettings(proportional);
            grid.RefreshView();
            Pump();
            bool[] variable = Enumerable.Range(0, 8).Select(i => grid.WidthWasArithmeticForTesting(sample, i)).ToArray();
            ok &= Check($"and a proportional one refuses it in every style [{string.Join(",", variable.Select(b => b ? '1' : '0'))}]",
                        variable.All(b => !b), $"font {proportional.FontFamily}");
            // ...and still draws, through the layout that has always drawn it.
            grid.ScrollHorizontallyTo(0);
            grid.RefreshView();
            Pump();
            using (var propUnscrolled = Capture(host))
            {
                grid.ScrollHorizontallyTo(120);
                grid.RefreshView();
                Pump();
                using var propScrolled = Capture(host);
                var propArea = new Rectangle(gutter, grid.GutterAreaForTesting.Top,
                                             propUnscrolled.Width - gutter - 20, grid.GutterAreaForTesting.Height);
                ok &= Check("a proportional face still draws, and still scrolls",
                            !SameRegion(propUnscrolled, propScrolled, propArea));
                var propMargin = FirstDifference(propUnscrolled, propScrolled, grid.GutterAreaForTesting);
                ok &= Check("and still leaves the line-number margin alone", propMargin is null,
                            propMargin?.ToString() ?? "");
            }
            grid.ApplySettings(settings);
            grid.ScrollHorizontallyTo(260);
            grid.RefreshView();
            Pump();

            // Plain ASCII in a fixed-pitch face goes to GDI by the shortest road there is: the text draws
            // its own background in one call, and the line number is placed by arithmetic rather than by
            // asking for it to be right-aligned. Both are worth half the paint, and both are only allowed
            // because they put exactly the same pixels on the screen as the general path does.
            grid.ScrollHorizontallyTo(0);
            grid.DrawTextTheLongWayForTesting = true;
            Pump();
            using (var longWay = Capture(host))
            {
                grid.DrawTextTheLongWayForTesting = false;
                Pump();
                using var direct = Capture(host);
                var inkDiff = FirstDifference(longWay, direct, new Rectangle(0, 0, longWay.Width, longWay.Height));
                ok &= Check("text drawn straight onto the device context is the same picture as text laid out" +
                            (inkDiff is null ? "" : $" [first differs at x={inkDiff.Value.X},y={inkDiff.Value.Y}: " +
                                                    $"{longWay.GetPixel(inkDiff.Value.X, inkDiff.Value.Y)} -> " +
                                                    $"{direct.GetPixel(inkDiff.Value.X, inkDiff.Value.Y)}]"),
                            inkDiff is null);
            }

            // Columns are a different drawing path - per-cell text plus a header row - and had the same flaw.
            doc.Columns.Enabled = true;
            doc.Columns.Template = "{[*]}{[*]}{[*]} {*}";
            doc.Columns.Columns.Clear();
            int at = 0;
            foreach (var (n, w) in new[] { ("Time", 190), ("Provider", 90), ("Id", 55), ("Message", 360) })
                doc.Columns.Columns.Add(new ColumnDef { Name = n, Width = w, Source = at++ });

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

            // A cell takes the same short road a whole line does when its text is plain ASCII in a
            // fixed-pitch face and fits its box - placed by arithmetic where the layout would have put it,
            // aligned as its column asks. Against the layout, pixel for pixel, in all three alignments.
            //
            // With and without the line-number margin, because the two are not the same drawing at all.
            // Narrowing the clip to the text area saves the whole device context and puts it back, which
            // puts the selected face back with it - and a margin drawn before that happens hides the fault,
            // because drawing the number selects the face again on every row.
            grid.ScrollHorizontallyTo(0);
            for (int i = 0; i < doc.Columns.Columns.Count; i++)
            {
                doc.Columns.Columns[i].Align = (ColumnAlign)(i % 3);
                // Wide enough for what is in them at any font size, or every cell asks for the ellipsis
                // instead and none of them takes the short road this is here to check.
                doc.Columns.Columns[i].Width = 400;
            }
            foreach (bool numbers in (bool[])[true, false])
            {
                settings.ShowLineNumbers = numbers;
                grid.ApplySettings(settings);
                grid.RefreshView();
                grid.DrawTextTheLongWayForTesting = true;
                Pump();
                using var cellsLaidOut = Capture(host);
                grid.DrawTextTheLongWayForTesting = false;
                Pump();
                using var cellsDirect = Capture(host);
                var cellDiff = FirstDifference(cellsLaidOut, cellsDirect,
                    new Rectangle(0, 0, cellsLaidOut.Width, cellsLaidOut.Height));
                ok &= Check($"a cell drawn straight onto the device context is the same picture too " +
                            $"({(numbers ? "with" : "without")} line numbers)" +
                            (cellDiff is null ? "" : $" [first differs at x={cellDiff.Value.X},y={cellDiff.Value.Y}]"),
                            cellDiff is null);
            }
            settings.ShowLineNumbers = true;
            grid.ApplySettings(settings);
            foreach (var column in doc.Columns.Columns) column.Align = ColumnAlign.Left;
            grid.RefreshView();
            Pump();
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
            grid.SetViewAnchor(new ViewAnchor(0, 0, -1));
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

    /// <summary>
    /// The column header is where columns are laid out: dragging an edge sizes one, carrying a header
    /// moves one, double-clicking a name renames it and the header's own menu hides and shows them. All of
    /// it is driven here against a real control, because none of it can be driven through UI Automation -
    /// a drag needs a real mouse, and these gestures have no automation pattern to invoke.
    /// </summary>
    private static bool RunColumnChecks()
    {
        Line("-- columns --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_columns_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        // The service field is short at the top of the file and long further down, so "the widths do not
        // follow the scroll" and "asking for a fit does change them" can be told apart.
        for (int i = 0; i < 40; i++)
            sb.Append($"[2026-08-04T09:31:{i % 60:00}][api][INFO ] short message {i}\n");
        for (int i = 40; i < 90; i++)
            sb.Append($"[2026-08-04T09:31:{i % 60:00}][payment-service-europe-west][WARN ] short message {i}\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            doc.Columns.Enabled = true;
            doc.Columns.Template = "{[*]}{[*]}{[*]} {*}";
            doc.Columns.Reset();
            doc.Columns.Columns[0].Name = "time";
            doc.Columns.Columns[1].Name = "service";
            doc.Columns.Columns[2].Name = "level";
            doc.Columns.Columns[3].Name = "message";

            var settings = new AppSettings();
            var grid = new LineGridControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(900, 420),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(grid);
            grid.Attach(doc, settings);
            host.Show();
            Pump();

            int edits = 0;
            grid.ColumnsChanged += () => edits++;

            // --- what "auto" means: every column as wide as it needs, and the row filling the window ---

            bool ok = Check("the log is being read in a fixed-pitch font, so the character rules apply",
                            grid.MonospacedForTesting);
            int[] Widths() => [.. Enumerable.Range(0, 4).Select(grid.ColumnWidthForTesting)];
            var auto = Widths();
            ok &= Check($"the columns fill the width of the view exactly ({auto.Sum()} of {grid.ContentWidthForTesting})",
                        auto.Sum() == grid.ContentWidthForTesting, string.Join(", ", auto));
            ok &= Check("and none of them is the old fixed 160 pixels for everything",
                        auto.Distinct().Count() > 1, string.Join(", ", auto));
            for (int i = 0; i < 3; i++)
                ok &= Check($"column {i} is at least as wide as what is in it ({auto[i]} vs {grid.NaturalWidthForTesting(i)})",
                            auto[i] >= grid.NaturalWidthForTesting(i));
            ok &= Check("the last column takes the room left over rather than leaving a gap",
                        auto[3] > grid.NaturalWidthForTesting(3) || auto.Sum() == grid.ContentWidthForTesting);

            // --- the widths do not chase the content as the view scrolls ---

            grid.SetVerticalScrollValue(60);
            grid.RefreshView();
            Pump();
            var afterScroll = Widths();
            ok &= Check("the columns hold still while the view scrolls over wider content",
                        afterScroll.SequenceEqual(auto), string.Join(", ", afterScroll));

            // ...but asking for a fit does re-measure, or the check above would pass by never changing.
            grid.FitColumnsToWindow();
            Pump();
            var refitted = Widths();
            ok &= Check($"asking for a fit re-measures what is on screen ({refitted[1]} vs {auto[1]} for the service)",
                        refitted[1] > auto[1], string.Join(", ", refitted));
            ok &= Check("and still fills the width exactly", refitted.Sum() == grid.ContentWidthForTesting);

            grid.SetVerticalScrollValue(0);
            grid.FitColumnsToWindow();
            Pump();

            // --- dragging an edge ---

            edits = 0;
            int before = grid.ColumnWidthForTesting(0);
            int charWidth = grid.CharWidthForTesting;
            grid.DragColumnEdgeForTesting(0, grid.ColumnLeftForTesting(0) + before + charWidth * 5 + 3);
            int dragged = grid.ColumnWidthForTesting(0);
            ok &= Check($"dragging a column edge widens that column ({before} -> {dragged})", dragged > before);
            ok &= Check($"and the width lands on a whole number of characters ({dragged} / {charWidth})",
                        dragged % charWidth == 0, $"{dragged} % {charWidth} = {dragged % charWidth}");
            ok &= Check($"and it is recorded in characters, so a zoom keeps the same fields ({doc.Columns.Columns[0].WidthChars} chars)",
                        doc.Columns.Columns[0].WidthChars == dragged / charWidth);
            ok &= Check("and the file now differs from what is on disk", edits > 0, $"{edits} edits reported");
            ok &= Check("and the other columns give up the room, so the row still fills the view",
                        Widths().Sum() == grid.ContentWidthForTesting, string.Join(", ", Widths()));

            // Zooming keeps the column the same number of characters wide - the point of storing it that way.
            int charsWide = doc.Columns.Columns[0].WidthChars;
            grid.Zoom(20);
            Pump();
            ok &= Check($"zooming keeps that column {charsWide} characters wide, not {before} pixels",
                        grid.ColumnWidthForTesting(0) == charsWide * grid.CharWidthForTesting
                        && grid.CharWidthForTesting != charWidth,
                        $"{grid.ColumnWidthForTesting(0)} px at {grid.CharWidthForTesting} px/char");
            grid.ResetZoom();
            Pump();

            // A column may not be dragged away to nothing: its own edge would then be unreachable.
            grid.DragColumnEdgeForTesting(0, 0);
            ok &= Check($"a column cannot be dragged narrower than it can be grabbed ({grid.ColumnWidthForTesting(0)})",
                        grid.ColumnWidthForTesting(0) >= grid.MinColumnWidthForTesting);

            // With a proportional font there is no character to snap to, so the width is plain pixels.
            settings.FontFamily = "Segoe UI";
            grid.ApplySettings(settings);
            Pump();
            ok &= Check("a proportional font is recognised as one", !grid.MonospacedForTesting);
            grid.DragColumnEdgeForTesting(1, grid.ColumnLeftForTesting(1) + grid.ColumnWidthForTesting(1) + 37);
            ok &= Check("and a column dragged in one is sized in pixels, not characters",
                        doc.Columns.Columns[1].WidthChars == 0 && doc.Columns.Columns[1].Width > 0,
                        $"{doc.Columns.Columns[1].Width} px, {doc.Columns.Columns[1].WidthChars} chars");
            settings.FontFamily = "Consolas";
            grid.ApplySettings(settings);
            grid.FitColumnsToWindow();
            Pump();

            // --- aiming at the header ---

            int mid = grid.ColumnLeftForTesting(1) + grid.ColumnWidthForTesting(1) / 2;
            ok &= Check("a point in the middle of a header names that column", grid.ColumnAtForTesting(mid) == 1);
            ok &= Check("and is not mistaken for its edge", grid.DividerAtForTesting(mid) < 0);
            int edge = grid.ColumnLeftForTesting(1) + grid.ColumnWidthForTesting(1);
            ok &= Check("a point on an edge names the column it belongs to", grid.DividerAtForTesting(edge) == 1);

            // --- carrying a header to another place ---

            edits = 0;
            var startOrder = grid.ColumnNamesForTesting;
            string[] Cells() => [.. Enumerable.Range(0, 4).Select(i => grid.CellTextForTesting(0, i))];
            var startCells = Cells();
            ok &= Check($"a row reads as its fields, left to right ({string.Join(" | ", startCells)})",
                        startCells[0].StartsWith("2026", StringComparison.Ordinal) && startCells[3].Contains("message", StringComparison.Ordinal));

            grid.PressHeaderForTesting(grid.ColumnLeftForTesting(3) + grid.ColumnWidthForTesting(3) / 2);
            var places = new List<int>();
            for (int x = grid.ColumnLeftForTesting(3) + grid.ColumnWidthForTesting(3) / 2; x >= grid.ColumnLeftForTesting(0); x -= 6)
            {
                grid.DragHeaderToForTesting(x);
                places.Add(Array.IndexOf(grid.ColumnNamesForTesting, "message"));
            }
            grid.ReleaseHeaderForTesting();
            ok &= Check($"carrying a header left walks it to the front ({string.Join("", places)})",
                        places[^1] == 0 && places[0] == 3);
            bool walked = true;
            for (int i = 1; i < places.Count; i++) if (places[i] > places[i - 1]) walked = false;
            ok &= Check("and it walks rather than flickering between two places",
                        walked, string.Join("", places));
            ok &= Check("the other columns keep their order behind it",
                        grid.ColumnNamesForTesting.SequenceEqual(startOrder.Where(n => n != "message").Prepend("message")),
                        string.Join(", ", grid.ColumnNamesForTesting));
            // The header used to move on its own and leave the text where it was: the column's place in
            // the list was also which field it showed, so carrying one relabelled the fields.
            var carriedCells = Cells();
            ok &= Check($"and the data goes with it ({string.Join(" | ", carriedCells)})",
                        carriedCells[0] == startCells[3] && carriedCells[1] == startCells[0]
                        && carriedCells[2] == startCells[1] && carriedCells[3] == startCells[2]);
            ok &= Check("and the move is something to save", edits > 0);

            // Put it back the way round it started, through the same gesture.
            grid.PressHeaderForTesting(grid.ColumnLeftForTesting(0) + grid.ColumnWidthForTesting(0) / 2);
            for (int x = grid.ColumnLeftForTesting(0); x <= grid.ColumnLeftForTesting(3) + grid.ColumnWidthForTesting(3); x += 6)
                grid.DragHeaderToForTesting(x);
            grid.ReleaseHeaderForTesting();
            ok &= Check("and carrying it back restores the order it started in",
                        grid.ColumnNamesForTesting.SequenceEqual(startOrder), string.Join(", ", grid.ColumnNamesForTesting));
            ok &= Check("and the rows read as they did to begin with", Cells().SequenceEqual(startCells),
                        string.Join(" | ", Cells()));

            // --- hiding and showing ---

            edits = 0;
            int wasWide = grid.ColumnWidthForTesting(1);
            grid.SetColumnVisible(1, false);
            Pump();
            ok &= Check("a hidden column takes up no room at all", grid.ColumnWidthForTesting(1) == 0);
            ok &= Check("and the columns still show their own fields, not the ones next door",
                        grid.CellTextForTesting(0, 0) == startCells[0] && grid.CellTextForTesting(0, 2) == startCells[2],
                        string.Join(" | ", Cells()));
            ok &= Check("and the rest spread out to fill the view",
                        Widths().Sum() == grid.ContentWidthForTesting, string.Join(", ", Widths()));
            ok &= Check("and hiding it is something to save", edits > 0);
            grid.SetColumnVisible(1, true);
            Pump();
            ok &= Check($"showing it again gives it its room back ({grid.ColumnWidthForTesting(1)} vs {wasWide})",
                        grid.ColumnWidthForTesting(1) > 0);

            for (int i = 1; i < 4; i++) grid.SetColumnVisible(i, false);
            ok &= Check("the last column standing cannot be hidden - there would be no header left to bring it back",
                        doc.Columns.Columns.Count(c => c.Visible) == 1);
            grid.SetColumnVisible(0, false);
            ok &= Check("...even asked directly", doc.Columns.Columns[0].Visible);
            for (int i = 1; i < 4; i++) grid.SetColumnVisible(i, true);
            Pump();

            // --- renaming in place ---

            edits = 0;
            grid.BeginRename(0);
            ok &= Check("double-clicking a name opens an edit box over it", grid.IsRenamingForTesting);
            grid.SetRenameTextForTesting("Timestamp");
            grid.EndRename(commit: true);
            ok &= Check("...and what is typed becomes the column's name",
                        doc.Columns.Columns[0].Name == "Timestamp", doc.Columns.Columns[0].Name);
            ok &= Check("and the box is gone afterwards", !grid.IsRenamingForTesting);
            ok &= Check("and renaming is something to save", edits > 0);

            grid.BeginRename(0);
            grid.SetRenameTextForTesting("discarded");
            grid.EndRename(commit: false);
            ok &= Check("giving up on a rename leaves the name alone",
                        doc.Columns.Columns[0].Name == "Timestamp", doc.Columns.Columns[0].Name);

            grid.BeginRename(0);
            grid.SetRenameTextForTesting("   ");
            grid.EndRename(commit: true);
            ok &= Check("and a name cannot be emptied", doc.Columns.Columns[0].Name == "Timestamp");

            // --- fitting one column to what is in it ---

            grid.SetColumnWidthForTesting(2, grid.CharWidthForTesting * 30);
            grid.FitColumnToContent(2);
            int fitted = grid.ColumnWidthForTesting(2);
            ok &= Check($"double-clicking an edge sizes that column to what is in it ({fitted} for \"WARN \")",
                        fitted < grid.CharWidthForTesting * 30 && fitted >= grid.NaturalWidthForTesting(2) - grid.CharWidthForTesting,
                        $"natural {grid.NaturalWidthForTesting(2)}");

            // --- and the header still draws, with everything moved about ---

            grid.RefreshView();
            Pump();
            using (var shot = Capture(host))
                ok &= Check("the header is still drawn after all of that",
                            shot.Width == host.ClientSize.Width && grid.RowsPaintedForTesting > 0,
                            $"{grid.RowsPaintedForTesting} rows painted");

            // --- the header's own menu ---

            using (var menu = grid.ColumnMenuForTesting(2))
            {
                var ticks = menu.Items.OfType<ToolStripMenuItem>().Take(4).ToArray();
                ok &= Check($"the menu lists every column, hidden ones included " +
                            $"[{string.Join(", ", ticks.Select(t => t.Text))}]",
                            ticks.Length == 4 && ticks.All(t => t.Checked));

                ticks[1].PerformClick();
                ok &= Check("ticking one off hides that column", !doc.Columns.Columns[1].Visible);
                ok &= Check("and only that one", doc.Columns.Columns.Count(c => c.Visible) == 3);
                ok &= Check("and the menu stays up, so the next one is one click away",
                            LineGridControl.StaysOpenOnItemClickForTesting(menu));

                ticks[2].PerformClick();
                ok &= Check("so a second column can be turned off without opening it again",
                            !doc.Columns.Columns[2].Visible && doc.Columns.Columns.Count(c => c.Visible) == 2);

                // The entries below the list act on the column the menu was opened over - column 2, which
                // has just been hidden. Renaming or fitting one nobody can see does nothing, and a menu that
                // closed itself never had to say so.
                var forColumn = menu.Items.OfType<ToolStripMenuItem>()
                    .Where(i => (i.Text ?? "").Contains("\"level\"", StringComparison.OrdinalIgnoreCase)).ToArray();
                ok &= Check($"the menu offers commands for the column it was opened over " +
                            $"[{string.Join(", ", forColumn.Select(i => i.Text))}]", forColumn.Length == 3);
                ok &= Check("which are greyed out once that column is hidden",
                            forColumn.All(i => !i.Enabled),
                            string.Join(", ", forColumn.Select(i => $"{i.Text}:{i.Enabled}")));

                ticks[1].PerformClick();
                ok &= Check("and back on again", doc.Columns.Columns[1].Visible);
                ticks[2].PerformClick();
                ok &= Check("bringing the column back makes its commands usable again",
                            doc.Columns.Columns[2].Visible && forColumn.All(i => i.Enabled),
                            string.Join(", ", forColumn.Select(i => $"{i.Text}:{i.Enabled}")));
                ticks[2].PerformClick();

                // Down to one column, and then a press on the one still standing. It cannot go - and the
                // list must not be left showing a tick that was refused, which is what a menu that closes
                // itself never had to worry about.
                ticks[1].PerformClick();
                ticks[3].PerformClick();
                ok &= Check($"one column is left ({doc.Columns.Columns.Count(c => c.Visible)})",
                            doc.Columns.Columns.Count(c => c.Visible) == 1 && doc.Columns.Columns[0].Visible);
                ticks[0].PerformClick();
                ok &= Check("pressing the last one standing does not hide it",
                            doc.Columns.Columns[0].Visible && doc.Columns.Columns.Count(c => c.Visible) == 1);
                ok &= Check("and its tick is not left showing a change that was refused",
                            ticks.Select((t, i) => t.Checked == doc.Columns.Columns[i].Visible).All(x => x),
                            string.Join(", ", ticks.Select((t, i) => $"{t.Text}:{t.Checked}/{doc.Columns.Columns[i].Visible}")));

                // A command is a command: choosing one puts the menu away as any menu does.
                ok &= Check("but the menu does go away when something asks it to",
                            LineGridControl.ClosesWhenAskedForTesting(menu));
            }
            for (int i = 0; i < 4; i++) grid.SetColumnVisible(i, true);
            Pump();

            // --- renaming from the dialog, which is the only way to reach a hidden column's name ---

            var samples = new[]
            {
                doc.GetLineText(0), doc.GetLineText(1), doc.GetLineText(2),
                "a line of quite another shape entirely"
            };

            doc.Columns.Columns[1].Visible = false;
            using (var dlg = new ColumnsDialog(doc.Columns, samples))
            {
                dlg.SetCellForTesting(1, "name", "  Service  ");
                dlg.ApplyForTesting();
                ok &= Check($"and what is typed becomes the column's name (\"{dlg.Result.Columns[1].Name}\")",
                            dlg.Result.Columns[1].Name == "Service");
                ok &= Check("a hidden column can be renamed there, which the header cannot do at all",
                            !dlg.Result.Columns[1].Visible);
                ok &= Check("and the columns beside it are left alone",
                            dlg.Result.Columns[0].Name == doc.Columns.Columns[0].Name &&
                            dlg.Result.Columns[2].Name == doc.Columns.Columns[2].Name);
                dlg.SetCellForTesting(1, "name", "   ");
                dlg.ApplyForTesting();
                ok &= Check("and a name cannot be emptied from there either, as on the header",
                            dlg.Result.Columns[1].Name == "Service");
            }
            doc.Columns.Columns[1].Visible = true;

            // The buttons are one row, so they belong at one height - a flow panel positions each control
            // by its own top margin, and a default margin on one of them is enough to knock it out of line.
            using (var dlg = new ColumnsDialog(doc.Columns, samples))
            {
                dlg.StartPosition = FormStartPosition.Manual;
                dlg.Location = new Point(0, 0);
                dlg.Opacity = 0;
                dlg.Show();
                Pump();
                Rectangle Where(Control c) => dlg.RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));
                var okBtn = AllControls(dlg).OfType<Button>().First(b => b.Text == "OK");
                var cancelBtn = AllControls(dlg).OfType<Button>().First(b => b.Text == "Cancel");
                var list = AllControls(dlg).OfType<DataGridView>().First();
                Rectangle okR = Where(okBtn), cancelR = Where(cancelBtn), listR = Where(list);
                ok &= Check($"OK sits at the same height as Cancel (OK {okR}, Cancel {cancelR})",
                            okR.Top == cancelR.Top && okR.Height == cancelR.Height);
                ok &= Check($"and to its left, with a gap ({okR.Right} to {cancelR.Left})",
                            okR.Right < cancelR.Left && cancelR.Left - okR.Right <= dlg.LogicalToDeviceUnits(12));
                ok &= Check($"and the row ends where the list above it does ({cancelR.Right} vs {listR.Right})",
                            Math.Abs(cancelR.Right - listR.Right) <= 1,
                            $"client {dlg.ClientSize}, Cancel margin {cancelBtn.Margin}");

                // Everything on the dialog has to sit inside it, at any font size or DPI - a row that
                // overflows is how a dialog ends up with its buttons off the bottom edge.
                var stray = AllControls(dlg).Where(c => c.Visible && c.Parent is not null)
                    .Select(c => (c, r: Where(c)))
                    .Where(t => t.r.Right > dlg.ClientSize.Width + 1 || t.r.Bottom > dlg.ClientSize.Height + 1)
                    .ToList();
                ok &= Check("nothing on the dialog hangs off its edge", stray.Count == 0,
                            string.Join(", ", stray.Select(t => $"{t.c.GetType().Name}'{t.c.Text}' {t.r}")));

                // Opened at its own size it fits, so there must be no scroll bar: one that is always there
                // takes width off the content and says the dialog is bigger than it is.
                var fits = dlg.ContentScrollForTesting;
                ok &= Check("and at its own size there is nothing to scroll",
                            !fits.VerticalScroll.Visible && !fits.HorizontalScroll.Visible,
                            $"content {fits.DisplayRectangle} in {fits.ClientSize}; {dlg.ContentFloorForTesting}");

                // Width and alignment mean nothing to the Inline layout, so they are put out of reach
                // rather than left looking as though they do something.
                dlg.SetLayoutForTesting(FieldLayout.Columns);
                Pump();
                ok &= Check("a width can be typed while the layout is columns", dlg.WidthIsEditableForTesting);
                dlg.SetLayoutForTesting(FieldLayout.Inline);
                Pump();
                ok &= Check("and not while it is inline, where it would do nothing", !dlg.WidthIsEditableForTesting);

                // The count of lines that fit is the thing that stops a template being trusted because it
                // happened to suit the one line the caret was on.
                ok &= Check($"the dialog says how many of the sampled lines fit (\"{dlg.FitForTesting}\")",
                            dlg.FitForTesting.Contains($"of {samples.Length}", StringComparison.Ordinal));

                // A template that cannot be read says so, rather than quietly splitting nothing.
                dlg.SetTemplateForTesting("{[*]");
                Pump();
                ok &= Check($"a template that cannot be read is refused out loud (\"{dlg.StatusForTesting}\")",
                            dlg.StatusForTesting.Contains("never closed", StringComparison.OrdinalIgnoreCase));
                dlg.SetTemplateForTesting("{[*]}{[*]}{[*]} {*}");
                Pump();
                ok &= Check($"and a good one says what it found (\"{dlg.StatusForTesting}\")",
                            dlg.StatusForTesting.Contains("4 fields", StringComparison.Ordinal));

                dlg.Close();
                Pump();
            }

            // The same dialog at a much larger font: every row has to give way rather than push its
            // neighbours off the edge, which is how a dialog ends up with its buttons out of reach. Measured
            // against the CONTENT rather than the window, because a window with less room than the content
            // needs scrolls over it, and what has been scrolled past is not lost - see the check below.
            using (var big = new ColumnsDialog(doc.Columns, samples))
            {
                big.Font = new Font(big.Font.FontFamily, 16f);
                big.StartPosition = FormStartPosition.Manual;
                big.Location = new Point(0, 0);
                big.Opacity = 0;
                big.Show();
                Pump();
                var room = big.ContentScrollForTesting;
                Rectangle Where(Control c) => room.RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));
                var over = AllControls(big).Where(c => c.Visible && c.Parent is not null && c != room)
                    .Select(c => (c, r: Where(c)))
                    .Where(t => t.r.Right > room.ClientSize.Width + 1 || t.r.Bottom > room.DisplayRectangle.Bottom + 1)
                    .ToList();
                ok &= Check("at 16pt nothing on the dialog is pushed off its edge",
                            over.Count == 0,
                            string.Join(", ", over.Select(t => $"{t.c.GetType().Name}'{t.c.Text}' {t.r}")));

                var okBig = AllControls(big).OfType<Button>().First(b => b.Text == "OK");
                ok &= Check($"and the buttons are on the content ({Where(okBig)} in {room.DisplayRectangle})",
                            Where(okBig).Bottom <= room.DisplayRectangle.Bottom + 1);
                big.Close();
                Pump();
            }

            // And with less room than it needs at all. The size at which that happens is the SCREEN's, so on a
            // large monitor opening it normally never reaches it, while a build machine's screen does - hence
            // forcing it here rather than trusting the font to do it. Whatever will not fit has to be reachable
            // by scrolling to it, because a dialog whose OK button is off the bottom edge cannot be answered.
            using (var squashed = new ColumnsDialog(doc.Columns, samples))
            {
                // At a large font AND with too little room, which is the pair a build machine's screen puts
                // together: the scroll bar the one needs takes width from the other, and text fitted before
                // it appeared runs on underneath it.
                squashed.Font = new Font(squashed.Font.FontFamily, 16f);
                squashed.StartPosition = FormStartPosition.Manual;
                squashed.Location = new Point(0, 0);
                squashed.Opacity = 0;
                squashed.Show();
                Pump();
                squashed.MinimumSize = new Size(squashed.MinimumSize.Width, 0);
                squashed.Height = squashed.Height / 4;
                squashed.Width = squashed.MinimumSize.Width;
                squashed.PerformLayout();
                Pump();

                var okSquashed = AllControls(squashed).OfType<Button>().First(b => b.Text == "OK");
                Rectangle Seen(Control c) => squashed.RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));
                var scroller = squashed.ContentScrollForTesting;
                ok &= Check($"a dialog with less room than it needs can be scrolled ({squashed.ClientSize} client)",
                            scroller.VerticalScroll.Visible, $"content {scroller.DisplayRectangle.Height} in {scroller.ClientSize.Height}");

                // The list is the row that gives way, and it may not give way to nothing: squeezed out
                // altogether it takes the rows below it off the bottom with it.
                ok &= Check($"and the field list is still a list ({squashed.ListForTesting.ClientSize.Height}px for {squashed.RowCountForTesting} rows)",
                            squashed.ListForTesting.ClientSize.Height >= 4 * 20,
                            $"{squashed.ListForTesting.Bounds} in {squashed.ClientSize}");

                Rectangle Inside(Control c) => scroller.RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));
                var spilled = AllControls(squashed).Where(c => c.Visible && c.Parent is not null && c != scroller)
                    .Select(c => (c, r: Inside(c)))
                    .Where(t => t.r.Right > scroller.ClientSize.Width + 1 || t.r.Bottom > scroller.DisplayRectangle.Bottom + 1)
                    .ToList();
                ok &= Check("and nothing has been pushed off the content it scrolls over",
                            spilled.Count == 0,
                            string.Join(", ", spilled.Select(t => $"{t.c.GetType().Name}'{t.c.Text}' {t.r}")));

                scroller.VerticalScroll.Value = scroller.VerticalScroll.Maximum;
                scroller.PerformLayout();
                Pump();
                ok &= Check($"and scrolling down reaches the buttons ({Seen(okSquashed)} in {squashed.ClientSize})",
                            Seen(okSquashed).Bottom <= squashed.ClientSize.Height + 1);
                squashed.Close();
                Pump();
            }

            // --- what turning columns on with nothing set up offers ---

            string detected = LineTemplate.Detect("[2026-08-04T09:31:17][api-gateway][INFO ] a message");
            ok &= Check($"the parts of a bracketed line are read off it ({detected})",
                        detected == "{[*]}{[*]}{[*]} {*}");
            ok &= Check("a line with nothing to split on offers nothing rather than an empty header",
                        LineTemplate.Detect("plain text with no fields").Length == 0);

            // --- the chip strip: how parts are hidden and carried about while the layout is Inline ---

            for (int i = 0; i < doc.Columns.Columns.Count; i++) doc.Columns.Columns[i].Visible = true;
            doc.Columns.Layout = FieldLayout.Inline;
            grid.RefreshView();
            Pump();

            ok &= Check($"the strip still takes one row, as the header did ({grid.HeaderHeightForTesting})",
                        grid.HeaderHeightForTesting > 0);

            string whole = grid.DisplayTextForTesting(0);
            ok &= Check($"nothing hidden leaves the line exactly as it was (\"{whole}\")",
                        whole == doc.GetLineText(doc.RowToLine(0)));

            grid.ClickChipForTesting(1);      // the service field
            Pump();
            ok &= Check($"clicking a chip puts that part away ({grid.ChipNamesForTesting})",
                        !doc.Columns.Columns[1].Visible);
            string shortened = grid.DisplayTextForTesting(0);
            ok &= Check($"and the row loses it, brackets and all (\"{shortened}\")",
                        shortened.Length < whole.Length && !shortened.Contains("[api]", StringComparison.Ordinal));

            grid.ClickChipForTesting(1);
            Pump();
            ok &= Check($"clicking it again brings the part back ({grid.ChipNamesForTesting})",
                        doc.Columns.Columns[1].Visible && grid.DisplayTextForTesting(0) == whole);

            string orderBefore = string.Join(",", doc.Columns.Columns.Select(c => c.Name));
            grid.DragChipForTesting(0, 1);
            Pump();
            string orderAfter = string.Join(",", doc.Columns.Columns.Select(c => c.Name));
            ok &= Check($"carrying a chip sideways moves that part along the row ({orderBefore} -> {orderAfter})",
                        orderAfter != orderBefore);

            // Everything hidden would leave a row with nothing in it, and no chip to bring anything back.
            for (int i = 0; i < doc.Columns.Columns.Count; i++)
                if (doc.Columns.Columns.Count(c => c.Visible) > 1) grid.ClickChipForTesting(i);
            Pump();
            ok &= Check($"the last part standing cannot be put away too ({grid.ChipNamesForTesting})",
                        doc.Columns.Columns.Count(c => c.Visible) == 1);

            for (int i = 0; i < doc.Columns.Columns.Count; i++) doc.Columns.Columns[i].Visible = true;
            doc.Columns.Layout = FieldLayout.Columns;
            grid.RefreshView();
            Pump();

            // --- a search can find what the layout is not showing, and the app has to say so ---

            // The file's lines are [time][api][INFO ] short message N, so "api" is the value of part 1 -
            // which by now may sit anywhere in the list, the checks above having carried it about.
            grid.SetFindHighlight(FindEngine.CompileQuery(new FindQuery("api", Regex: false, CaseSensitive: false)));
            ok &= Check("with everything shown, a match is visible", grid.FindTermIsVisibleOn(0));

            var service = doc.Columns.Columns.First(c => c.Source == 1);
            service.Visible = false;
            grid.RefreshView();
            Pump();
            ok &= Check("hiding the field it is in makes it invisible, laid out in columns",
                        !grid.FindTermIsVisibleOn(0));
            doc.Columns.Layout = FieldLayout.Inline;
            grid.RefreshView();
            Pump();
            ok &= Check("and inline as well", !grid.FindTermIsVisibleOn(0));

            // A pattern that leans on what surrounds a field still has to be judged against the whole line:
            // asked of the field's own text alone, the bracket it looks ahead to is not there.
            service.Visible = true;
            doc.Columns.Layout = FieldLayout.Columns;
            grid.RefreshView();
            Pump();
            grid.SetFindHighlight(FindEngine.CompileQuery(new FindQuery(@"api(?=\])", Regex: true, CaseSensitive: false)));
            ok &= Check("a pattern that reaches outside its own field is still seen",
                        grid.FindTermIsVisibleOn(0));

            // ...and a term that runs across two fields is visible while both of them are.
            grid.SetFindHighlight(FindEngine.CompileQuery(new FindQuery("api][INFO", Regex: false, CaseSensitive: false)));
            ok &= Check("a term running across two shown fields is seen", grid.FindTermIsVisibleOn(0));

            grid.SetFindHighlight(null);
            for (int i = 0; i < doc.Columns.Columns.Count; i++) doc.Columns.Columns[i].Visible = true;
            grid.RefreshView();
            Pump();
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// The Fields dialog and the chip strip, looked at the way a reader looks at them: is anything cut off,
    /// does the sample stay inside its box when it is scrolled, do the columns in the result line up, and
    /// does a field carried elsewhere take the space in front of it along.
    /// </summary>
    private static bool RunFieldSettingsChecks()
    {
        Line("-- the field settings dialog, and the chips --");

        var samples = new[]
        {
            "[2026-08-05T05:00:02][api-gateway][INFO ] payment order 4417 accepted",
            "[2026-08-05T05:00:03][payment-service][WARN ] retrying charge for order 4417",
            "[2026-08-05T05:00:04][api-gateway][ERROR] " + string.Join(" ", Enumerable.Range(0, 60).Select(i => $"word{i}")),
            "a line of quite another shape",
        };

        var spec = new ColumnSpec { Enabled = true, Template = "{[*]}{[*]}{[*]} {*}" };
        spec.Reset();
        spec.Columns[0].Name = "Time";
        spec.Columns[1].Name = "Service";
        spec.Columns[2].Name = "Level";
        spec.Columns[3].Name = "Message";

        bool ok = true;

        using (var dlg = new ColumnsDialog(spec, samples))
        {
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = new Point(0, 0);
            dlg.Opacity = 0;
            dlg.Show();
            Pump();
            var preview = dlg.PreviewForTesting;

            // --- the sample box while it is scrolled ---

            dlg.StepSampleForTesting(2);   // the long line, which is the one with anywhere to scroll to
            Pump();
            ok &= Check("a line too long for the box can be scrolled", preview.CanScrollForTesting());

            var gutter = new Rectangle(0, 0, preview.GutterForTesting(), preview.Height - preview.ScrollBarHeightForTesting - 2);
            using (var still = CaptureControl(preview))
            {
                preview.ScrollToForTesting(preview.FurthestScrollForTesting());
                Pump();
                using var scrolled = CaptureControl(preview);
                // The words that name the two rows live to the left of the text. TextRenderer draws through
                // GDI, which ignores the clip region unless told not to - and without that the scrolled
                // sample was painted straight over them.
                var differs = FirstDifference(still, scrolled, gutter);
                ok &= Check("scrolling the sample leaves the words that name the rows untouched",
                            differs is null, $"first difference at {differs}");
                ok &= Check("and the sample itself did move", !SameRegion(still, scrolled,
                            new Rectangle(gutter.Right + 4, 0, preview.Width - gutter.Right - 8, gutter.Height)));
            }
            preview.ScrollToForTesting(0);
            dlg.StepSampleForTesting(-2);
            Pump();

            // --- nothing is said about a line not matching until there is a template to match it with ---

            dlg.SetTemplateForTesting("");
            Pump();
            ok &= Check("an empty template is not reported as a line that does not match",
                        !preview.SaysWhyNotForTesting);
            dlg.SetTemplateForTesting("{[*]}{[*]");
            Pump();
            ok &= Check("nor is a half-written one", !preview.SaysWhyNotForTesting);
            dlg.SetTemplateForTesting("{[*]}{[*]}{[*]} {*}");
            dlg.StepSampleForTesting(3);
            Pump();
            ok &= Check("a line that really does not fit is", preview.SaysWhyNotForTesting);
            dlg.StepSampleForTesting(-3);
            Pump();

            // --- Detect reads a bracketed header, and says so when there is not one ---

            dlg.SetTemplateForTesting("");
            Pump();
            dlg.DetectForTesting();
            Pump();
            ok &= Check($"Detect reads the header off the sample ({dlg.TemplateForTesting})",
                        dlg.TemplateForTesting == "{[*]}{[*]}{[*]} {*}", dlg.TemplateForTesting);
            // The space before the message is a SEPARATOR, so it sits outside the braces and stays behind
            // when the message is carried elsewhere.
            ok &= Check("with the space before the message left outside the braces",
                        dlg.TemplateForTesting.EndsWith("} {*}", StringComparison.Ordinal));

            // --- the columns of the result line up, width and alignment included ---

            dlg.SetTemplateForTesting("{[*]}{[*]}{[*]} {*}");
            dlg.SetLayoutForTesting(FieldLayout.Columns);
            dlg.SetCellForTesting(1, "align", "Right");
            Pump();
            string row = dlg.ResultForTesting;
            ok &= Check($"the result is a row of the table, in cells of its own (\"{row}\")",
                        row.Contains("     api-gateway", StringComparison.Ordinal), row);

            // --- getting round the dialog with nothing but a keyboard ---

            dlg.SetTemplateForTesting("{[*]}{[*]}{[*]} {*}");
            dlg.SelectRowForTesting(0);
            Pump();
            var stops = dlg.TabStopsForTesting;
            string walk = string.Join(" > ", stops);
            ok &= Check($"the field list is one stop on the way round ({walk})",
                        stops.Count(s => s == "fields") == 1, walk);
            // Which the walk above cannot tell you on its own: a grid is one control either way, and then
            // takes Tab at run time to move its own current cell along with. Tabbing out of a list of four
            // fields by five columns took twenty presses.
            ok &= Check("and Tab walks out of it rather than along its cells", dlg.TabLeavesListForTesting);
            ok &= Check("and the template box is where the round begins",
                        stops.Length > 0 && stops[0] == "template", walk);
            int okAt = Array.IndexOf(stops, "OK"), cancelAt = Array.IndexOf(stops, "Cancel");
            // The row flows right to left so that OK sits left of Cancel, which without saying otherwise
            // makes Cancel the first of the two Tab reaches.
            ok &= Check($"and OK is reached before Cancel ({okAt} then {cancelAt})",
                        okAt >= 0 && cancelAt >= 0 && okAt < cancelAt, walk);
            ok &= Check($"and the whole dialog is a short walk ({stops.Length} stops)", stops.Length <= 14, walk);

            var mnemonics = dlg.MnemonicsForTesting;
            var clashes = mnemonics.GroupBy(m => m[0]).Where(g => g.Count() > 1)
                                   .Select(g => string.Join(" and ", g)).ToArray();
            ok &= Check($"no two things on it claim the same Alt key ({string.Join(", ", mnemonics)})",
                        clashes.Length == 0, string.Join("; ", clashes));

            // Alt with an arrow carries a field up and down, as it does in the filter list.
            dlg.SelectRowForTesting(0);
            Pump();
            dlg.PressListKeyForTesting(Keys.Alt | Keys.Down);
            Pump();
            ok &= Check($"Alt+Down carries the field down the list ({string.Join(",", dlg.Result.Columns.Select(c => c.Source))})",
                        dlg.Result.Columns[1].Source == 0 && dlg.SelectedRowForTesting == 1);
            dlg.PressListKeyForTesting(Keys.Alt | Keys.Up);
            Pump();
            ok &= Check($"and Alt+Up carries it back ({string.Join(",", dlg.Result.Columns.Select(c => c.Source))})",
                        dlg.Result.Columns[0].Source == 0 && dlg.SelectedRowForTesting == 0);

            // --- renaming a field from the keyboard ---

            dlg.SetTemplateForTesting("{[*]}{[*]}{[*]} {*}");
            Pump();
            dlg.SetCellForTesting(1, "name", "Service");
            dlg.SelectRowForTesting(1);
            Pump();
            dlg.PressF2ForTesting();
            Pump();
            ok &= Check("F2 opens the field's name for typing", dlg.IsRenamingForTesting);
            // Selected, not merely open: renaming a field almost always means a NEW name, and a caret left
            // at the end makes the reader clear the old one out by hand first.
            ok &= Check($"with the whole of it selected, ready to be typed over (\"{dlg.SelectedInEditorForTesting}\")",
                        dlg.SelectedInEditorForTesting == "Service", dlg.SelectedInEditorForTesting);
            dlg.TypeInEditorForTesting("Provider");
            Pump();

            // Escape belongs to the innermost thing it can close. While a name is being typed over that is
            // the name - throwing the whole dialog away would take every other change with it.
            ok &= Check("Escape while renaming is taken by the rename", dlg.PressDialogKeyForTesting(Keys.Escape));
            Pump();
            ok &= Check("which puts the editor away", !dlg.IsRenamingForTesting);
            dlg.ApplyForTesting();
            ok &= Check($"and leaves the name as it was ({dlg.Result.Columns[1].Name})",
                        dlg.Result.Columns[1].Name == "Service");
            ok &= Check("and the dialog is still open", dlg.Visible && dlg.DialogResult != DialogResult.Cancel,
                        $"visible {dlg.Visible}, result {dlg.DialogResult}");

            // --- and a row has to show something ---

            for (int i = 0; i < dlg.RowCountForTesting; i++) dlg.SetCellForTesting(i, "show", false);
            Pump();
            dlg.ApplyForTesting();
            ok &= Check($"the last field standing cannot be unticked either ({string.Join(",", dlg.Result.Columns.Select(c => $"{c.Name}:{c.Visible}"))})",
                        dlg.Result.Columns.Count(c => c.Visible) == 1);
            ok &= Check($"and the dialog says why (\"{dlg.StatusForTesting}\")",
                        dlg.StatusForTesting.Contains("only field left", StringComparison.Ordinal));
            for (int i = 0; i < dlg.RowCountForTesting; i++) dlg.SetCellForTesting(i, "show", true);
            Pump();

            // --- a field is carried up and down the list by dragging as well as by the buttons ---

            dlg.SetTemplateForTesting("{[*]}{[*]}{[*]} {*}");
            Pump();
            string OrderOf() => string.Join(",", dlg.Result.Columns.Select(c => c.Source));
            dlg.DropRowForTesting(3, 0);          // the last field carried to the front
            Pump();
            ok &= Check($"a field dropped at the front of the list lands there ({OrderOf()})",
                        dlg.Result.Columns[0].Source == 3);
            ok &= Check("and the row that moved is the one left selected", dlg.SelectedRowForTesting == 0);
            dlg.DropRowForTesting(0, 4);          // ...and back to the end, past every other row
            Pump();
            ok &= Check($"and dropped past the last row it lands at the end ({OrderOf()})",
                        dlg.Result.Columns[^1].Source == 3);
            dlg.DropRowForTesting(1, 1);
            Pump();
            ok &= Check($"dropping a row where it already is changes nothing ({OrderOf()})",
                        OrderOf() == "0,1,2,3");

            // --- pointing at the sample, or at the result, says which field it is ---

            dlg.SelectRowForTesting(0);
            Pump();
            dlg.PreviewForTesting.ClickSampleForTesting(30);   // inside [api-gateway], the second field
            Pump();
            ok &= Check($"clicking a field in the sample picks its row out of the list ({dlg.SelectedRowForTesting})",
                        dlg.SelectedRowForTesting == 1);
            ok &= Check($"and the band drawn round that field moves with it ({dlg.PreviewForTesting.Highlight})",
                        dlg.PreviewForTesting.Highlight == 1);
            // Both rows do the same thing on a click, so both have to SAY the same thing. The sample used to
            // offer an I-beam, left over from a drag-to-select that nothing reads any more.
            ok &= Check($"the pointer says the same thing over both rows ({dlg.PreviewForTesting.CursorOverSampleForTesting(30)} / {dlg.PreviewForTesting.CursorOverResultForTesting(2)})",
                        dlg.PreviewForTesting.CursorOverSampleForTesting(30) == Cursors.Hand &&
                        dlg.PreviewForTesting.CursorOverResultForTesting(2) == Cursors.Hand);
            dlg.SetLayoutForTesting(FieldLayout.Inline);
            dlg.DropRowForTesting(3, 0);          // Message to the front, so the result is in another order
            dlg.SelectRowForTesting(3);
            Pump();
            dlg.PreviewForTesting.ClickResultForTesting(2);    // the first cell of the RESULT is now Message
            Pump();
            ok &= Check($"and clicking the result picks the field the RESULT has there ({dlg.SelectedRowForTesting})",
                        dlg.SelectedRowForTesting == 0 && dlg.Result.Columns[0].Source == 3);
            ok &= Check($"with the band moved to that one too ({dlg.PreviewForTesting.Highlight})",
                        dlg.PreviewForTesting.Highlight == 3);
            dlg.DropRowForTesting(0, 4);
            dlg.SetLayoutForTesting(FieldLayout.Columns);
            Pump();

            // ...and with nothing being typed over, Escape means what it always did. Last of all, because
            // what it means is that the dialog goes away.
            ok &= Check("Escape with nothing being renamed is left to the dialog",
                        dlg.PressDialogKeyForTesting(Keys.Escape) && dlg.DialogResult == DialogResult.Cancel,
                        $"result {dlg.DialogResult}");

            dlg.Close();
            Pump();
        }

        // --- the dialog opens with room to work in, and in the middle of what opened it ---

        using (var opener = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(40, 40, 900, 700), Opacity = 0 })
        {
            opener.Show();
            Pump();
            using var sized = new ColumnsDialog(spec, samples);
            sized.Opacity = 0;
            // After the dialog has finished coming up, not during: sizing and centring are the last things
            // OnShown does, and closing from inside the Shown event would cut them off half way.
            sized.Shown += (_, _) => sized.BeginInvoke(sized.Close);
            sized.ShowDialog(opener);
            Pump();
            ok &= Check($"the field list opens with room for the fields and a few spare ({sized.ListRoomInRowsForTesting} rows for {sized.RowCountForTesting} fields)",
                        sized.ListRoomInRowsForTesting >= sized.RowCountForTesting + 2,
                        $"{sized.ClientSize}");
            var middle = new Point(sized.Left + sized.Width / 2, sized.Top + sized.Height / 2);
            var screen = Screen.FromControl(opener).WorkingArea;
            var want = new Point(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
            bool centred = Math.Abs(middle.X - want.X) <= 2 && Math.Abs(middle.Y - want.Y) <= 2;
            ok &= Check($"and in the middle of the screen that window is on ({middle} vs {want})", centred,
                        $"dialog {sized.Bounds}, owner {opener.Bounds}, screen {screen}");
        }

        // --- a dialog with more on it than the screen is tall ---

        // The height is held between a floor (the rest of the dialog, plus enough list to read) and a
        // ceiling (the screen). They are worked out separately, so on a screen too short for what is on the
        // dialog the floor rises above the ceiling - and asking for a value between two bounds the wrong way
        // round throws. This runs from OnShown, where there is nobody to catch it. A font this size puts any
        // screen in that state.
        using (var huge = new ColumnsDialog(spec, samples))
        {
            huge.Font = new Font(huge.Font.FontFamily, 40f);
            huge.StartPosition = FormStartPosition.Manual;
            huge.Location = new Point(0, 0);
            huge.Opacity = 0;
            string blew = "";
            try { huge.Show(); Pump(); }
            catch (Exception ex) { blew = $"{ex.GetType().Name}: {ex.Message}"; }
            ok &= Check("a dialog with more on it than the screen is tall still opens", blew.Length == 0, blew);
            if (blew.Length == 0)
            {
                var screen = Screen.FromControl(huge).WorkingArea;
                ok &= Check($"and is no taller than the screen ({huge.Height} of {screen.Height})",
                            huge.Height <= screen.Height);
                ok &= Check($"and can still be dragged down to fit it ({huge.MinimumSize.Height})",
                            huge.MinimumSize.Height <= screen.Height);

                // ...and the list, which is what the leftover room went to, can be worked in whatever is
                // left of it - including none at all, where a grid refuses to say which row comes first.
                try
                {
                    huge.SetTemplateForTesting("{[*]}{[*]}{[*]}");
                    Pump();
                    huge.SetTemplateForTesting("{[*]}{[*]}{[*]} {*}");
                    huge.SelectRowForTesting(0);
                    huge.PressListKeyForTesting(Keys.Alt | Keys.Down);
                    Pump();
                }
                catch (Exception ex) { blew = $"{ex.GetType().Name}: {ex.Message}"; }
                ok &= Check($"and its rows can still be worked with no room to show one ({huge.RowCountForTesting} fields)",
                            blew.Length == 0, blew);

                var list = huge.ListForTesting;
                try
                {
                    huge.DragOverForTesting(list.ClientSize.Width / 2, Math.Max(0, list.ClientSize.Height - 2));
                    huge.DragOverForTesting(list.ClientSize.Width / 2, 1);
                    Pump();
                }
                catch (Exception ex) { blew = $"{ex.GetType().Name}: {ex.Message}"; }
                ok &= Check("and a row can be carried over its edges without the list objecting",
                            blew.Length == 0, blew);
            }
            try { huge.Close(); } catch { /* it never opened */ }
            Pump();
        }

        // --- the dialog at a large font: everything on it still has room ---
        using (var big = new ColumnsDialog(spec, samples))
        {
            big.Font = new Font(big.Font.FontFamily, 16f);
            big.StartPosition = FormStartPosition.Manual;
            big.Location = new Point(0, 0);
            big.Opacity = 0;
            big.Show();
            Pump();

            var preview = big.PreviewForTesting;
            ok &= Check($"the sample box grows with the font ({preview.Height} vs {preview.PreferredHeight} wanted)",
                        preview.Height >= preview.PreferredHeight - 1,
                        $"{preview.Bounds}");

            var list = big.ListForTesting;
            int rows = big.RowCountForTesting;
            ok &= Check($"and the field list still shows its rows ({list.ClientSize.Height}px for {rows} rows)",
                        list.ClientSize.Height >= 4 * 20, $"{list.Bounds} in {big.ClientSize}");

            // The sentence beside a layout is the longest text on the dialog. A label sizes itself to one
            // line however long that line is, so at this size the end of it used to leave the window
            // altogether; told how much room there is, the same label wraps instead.
            var says = big.LongestHelpForTesting;
            ok &= Check($"the layout description wraps inside the dialog rather than off it ({says.Right} of {big.ClientSize.Width})",
                        says.Right <= big.ClientSize.Width, $"{says} in {big.ClientSize}");
            big.Width = big.MinimumSize.Width;
            Pump();
            says = big.LongestHelpForTesting;
            ok &= Check($"and still does once the window is dragged as narrow as it goes ({says.Right} of {big.ClientSize.Width})",
                        says.Right <= big.ClientSize.Width, $"{says} in {big.ClientSize}");
            big.Close();
            Pump();
        }

        // --- the chips: a label has to fit inside the chip it is drawn in ---

        string path = Path.Combine(Path.GetTempPath(), "cascade_st_chips_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(path, Enumerable.Range(1, 200)
            .Select(i => $"[2026-08-05T09:31:{i % 60:00}][api-gateway][INFO ] request {i} handled"));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();
            doc.Columns.Enabled = true;
            doc.Columns.Layout = FieldLayout.Inline;
            doc.Columns.Template = "{[*]}{[*]}{[*]} {*}";
            doc.Columns.Reset();
            string[] names = ["Time", "Provider", "Level", "Message"];
            for (int i = 0; i < doc.Columns.Columns.Count && i < names.Length; i++)
                doc.Columns.Columns[i].Name = names[i];

            var settings = new AppSettings();
            var grid = new LineGridControl { Dock = DockStyle.Fill };
            host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), ClientSize = new Size(900, 320), Opacity = 0, FormBorderStyle = FormBorderStyle.None };
            host.Controls.Add(grid);
            grid.Attach(doc, settings);
            host.Show();
            Pump();

            // A chip is a control, not log text: it is read at the size the rest of the window is read at,
            // whatever the log has been set to, and it must not be cropped to make that fit.
            int windowText = TextRenderer.MeasureText("Xg", host.Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding).Height;
            foreach (var (family, size) in new[] { ("Consolas", 10f), ("Consolas", 8f), ("Consolas", 16f), ("Segoe UI", 10f) })
            {
                settings.FontFamily = family;
                settings.FontSize = size;
                grid.ApplySettings(settings);
                grid.RefreshView();
                Pump();

                var rect = grid.ChipRectForTesting(1);
                int text = grid.ChipLabelHeightForTesting;
                ok &= Check($"a chip's label fits inside it at {family} {size}pt (label {text}, chip {rect.Height})",
                            !rect.IsEmpty && text <= rect.Height,
                            $"row {grid.RowPitch}, chip {rect}");
                ok &= Check($"and is drawn no smaller than the rest of the window ({text} vs {windowText})",
                            text >= windowText, $"log font {family} {size}pt");
                ok &= Check($"and the chip fits in the strip ({rect.Height} of {grid.HeaderHeightForTesting})",
                            rect.Height > 0 && rect.Height <= grid.HeaderHeightForTesting,
                            $"{grid.HeaderRows} rows of {grid.RowPitch}px");
                // The strip is chrome, and chrome is a whole number of log lines - as the find bar is - so
                // that showing it can be paid for by scrolling and nothing on screen shifts by half a line.
                ok &= Check($"and the strip is a whole number of lines ({grid.HeaderHeightForTesting} of {grid.RowPitch})",
                            grid.HeaderHeightForTesting % grid.RowPitch == 0);
            }

            settings.FontFamily = "Consolas";
            settings.FontSize = 10f;
            grid.ApplySettings(settings);
            grid.RefreshView();
            Pump();

            // The strip starts where the text does, so the eye has something to line it up by.
            ok &= Check($"the first chip starts level with the text ({grid.ChipRectForTesting(0).Left} vs {grid.GutterWidthForTesting})",
                        grid.ChipRectForTesting(0).Left == grid.GutterWidthForTesting);

            // --- a chip's tip answers for the state the chip is in NOW ---

            grid.HoverChipForTesting(1);
            grid.ShowTipNowForTesting();
            Pump();
            string saidBefore = grid.ShownTipForTesting;
            static string First(string tip) => tip.Split('\n')[0];
            ok &= Check($"resting on a chip says what clicking it would do (\"{First(saidBefore)}\")",
                        saidBefore.Contains("is being shown", StringComparison.Ordinal) &&
                        saidBefore.Contains("Click to leave it out", StringComparison.Ordinal), saidBefore);
            grid.ClickChipForTesting(1);
            Pump();
            string saidAfter = grid.ShownTipForTesting;
            // The tip does not move when the chip under it is clicked, so if it is not rewritten it goes on
            // offering to do the thing that has just been done.
            ok &= Check($"and clicking it rewrites the tip on the spot (\"{First(saidAfter)}\")",
                        saidAfter.Contains("is being left out", StringComparison.Ordinal) &&
                        saidAfter.Contains("Click to bring it back", StringComparison.Ordinal), saidAfter);
            grid.ClickChipForTesting(1);
            Pump();
            ok &= Check($"and clicking it back says so again (\"{First(grid.ShownTipForTesting)}\")",
                        grid.ShownTipForTesting.Contains("is being shown", StringComparison.Ordinal),
                        grid.ShownTipForTesting);
            grid.DragChipForTesting(1, 2);
            Pump();
            ok &= Check("but carrying it off takes the tip down rather than leaving it behind",
                        grid.ShownTipForTesting.Length == 0, grid.ShownTipForTesting);
            grid.DragChipForTesting(2, 1);
            Pump();

            // --- renaming from a chip happens ON the chip ---

            grid.DoubleClickChipForTesting(1);
            Pump();
            var chip = grid.ChipRectForTesting(1);
            var box = grid.RenameBoxBoundsForTesting;
            ok &= Check($"double-clicking a chip opens an edit box over it (chip {chip}, box {box})",
                        grid.IsRenamingForTesting && box.Left == chip.Left && box.Top == chip.Top,
                        $"{box} vs {chip}");
            ok &= Check("and the field it names is still shown - the first of the two clicks put it away",
                        doc.Columns.Columns[1].Visible);
            grid.SetRenameTextForTesting("Service");
            grid.EndRename(commit: true);
            Pump();
            ok &= Check($"and what is typed becomes its name ({grid.ChipNamesForTesting})",
                        doc.Columns.Columns[1].Name == "Service");

            // A hidden field still has a chip, so it can be renamed where it stands.
            doc.Columns.Columns[1].Visible = false;
            grid.RefreshView();
            Pump();
            using (var menu = grid.ColumnMenuForTesting(1))
            {
                var rename = menu.Items.OfType<ToolStripMenuItem>()
                    .FirstOrDefault(i => (i.Text ?? "").StartsWith("&Rename", StringComparison.Ordinal));
                ok &= Check("a hidden field can be renamed from its chip's menu",
                            rename is { Enabled: true }, rename?.Text ?? "(no rename entry)");
                var hide = menu.Items.OfType<ToolStripMenuItem>()
                    .FirstOrDefault(i => (i.Text ?? "").StartsWith("&Hide", StringComparison.Ordinal));
                ok &= Check("but not hidden again, which would do nothing", hide is { Enabled: false });
            }
            doc.Columns.Columns[1].Visible = true;
            grid.RefreshView();
            Pump();

            // A selection made against one arrangement of the fields cannot survive another: hiding a field
            // moves every character after it, and what is picked out is what a filter would be made from.
            doc.Columns.Layout = FieldLayout.Columns;
            grid.RefreshView();
            Pump();
            grid.SelectPartOfCellForTesting(0, 3, 0, 4);
            Pump();
            bool picked = grid.CharColumnForTesting >= 0;
            doc.Columns.Columns[2].Visible = false;
            grid.RefreshView();
            Pump();
            ok &= Check("a selection does not outlive the arrangement it was made against",
                        picked && grid.CharColumnForTesting < 0,
                        $"picked {picked}, now column {grid.CharColumnForTesting}");
            doc.Columns.Columns[2].Visible = true;
            doc.Columns.Layout = FieldLayout.Inline;
            grid.RefreshView();
            Pump();

            // A column built in code says nothing about which part it shows. The table settles that while it
            // measures its widths; inline has no such pass, and a column showing no part draws nothing at
            // all - so the rows came out empty.
            foreach (var column in doc.Columns.Columns) column.Source = -1;
            grid.RefreshView();
            Pump();
            ok &= Check($"a spec that never said which part each field shows still draws (\"{grid.DisplayTextForTesting(0)}\")",
                        grid.DisplayTextForTesting(0) == doc.GetLineText(doc.RowToLine(0)),
                        grid.DisplayTextForTesting(0));

            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    private static Bitmap CaptureControl(Control c)
    {
        var bmp = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height));
        c.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        return bmp;
    }

    /// <summary>Turning the column view on and off - from the keyboard, which is what it is here for, and
    /// without the log appearing to slide when the header takes a row off the top of it.</summary>
    private static bool RunColumnModeChecks()
    {
        Line("-- turning columns on and off --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_colmode_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(path, Enumerable.Range(1, 2000)
            .Select(i => $"[2026-08-05T09:31:{i % 60:00}][api-gateway][INFO ] request {i} handled"));

        MainForm? form = null;
        try
        {
            form = new MainForm(new AppSettings(), new MachineState(), new[] { path })
            {
                NoSavePrompt = true,
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(1100, 800),
            };
            form.Show();
            Pump();
            var doc = form.DocForTesting;
            for (int i = 0; i < 60 && doc.CompletedLineCount < 2000; i++) { Thread.Sleep(20); Pump(); }

            var grid = form.GridForTesting;
            grid.GoToLine(1000);   // well away from either end, so nothing is clamped
            Pump();

            long firstBefore = grid.FirstRowForTesting;
            long watched = firstBefore + grid.VisibleRowCountForTesting / 2;
            // Where a line sits ON SCREEN is the thing that must not change; the grid's own coordinates
            // shift with the header, so they cannot tell.
            int ScreenYOf(long row) => grid.PointToScreen(new Point(0, grid.RowMiddleForTesting(row))).Y;
            int yBefore = ScreenYOf(watched);

            bool ok = Check("the columns start off", !doc.Columns.Enabled);
            ok &= Check("View > Split Lines Into Fields turns them on",
                        form.ClickMenuForTesting("View", "Split Lines Into Fields") && doc.Columns.Enabled);
            Pump();
            ok &= Check($"the fields of the line are read off it ({doc.Columns.Columns.Count} columns: " +
                        $"{string.Join(", ", doc.Columns.Columns.Select(c => c.Name))})",
                        doc.Columns.Columns.Count == 4);
            ok &= Check($"the header takes a row off the top of the log ({firstBefore} -> {grid.FirstRowForTesting})",
                        grid.FirstRowForTesting == firstBefore + 1);
            ok &= Check($"so the line being read has not moved on screen ({yBefore} -> {ScreenYOf(watched)})",
                        ScreenYOf(watched) == yBefore);

            ok &= Check("and the same item turns them off again",
                        form.ClickMenuForTesting("View", "Split Lines Into Fields") && !doc.Columns.Enabled);
            Pump();
            ok &= Check($"which hands the row back ({grid.FirstRowForTesting})",
                        grid.FirstRowForTesting == firstBefore);
            ok &= Check($"and still nothing has moved on screen ({ScreenYOf(watched)})",
                        ScreenYOf(watched) == yBefore);

            // The Inline layout has a strip of its own above the log. It need not be the same height as the
            // header - the chips are labelled in the window's font, the header in the log's - so what has to
            // hold is that whatever it takes, it takes in whole rows and hands every one of them back.
            doc.Columns.Layout = FieldLayout.Inline;
            bool turnedOn = form.ClickMenuForTesting("View", "Split Lines Into Fields");
            Pump();
            int inlineRows = grid.HeaderRows;
            ok &= Check($"laid out inline, the strip costs a whole number of rows ({inlineRows})",
                        turnedOn && inlineRows >= 1 &&
                        grid.FirstRowForTesting == firstBefore + inlineRows,
                        $"{firstBefore} -> {grid.FirstRowForTesting}, strip {inlineRows} rows");
            ok &= Check($"and the line being read has not moved on screen either ({ScreenYOf(watched)})",
                        ScreenYOf(watched) == yBefore);

            // ...and switching between the two layouts pays the difference, rather than letting the log slide
            // by however much taller one strip is than the other.
            ok &= Check("switching to columns keeps it where it is",
                        form.ClickMenuForTesting("View", "Split Lines Into Fields", "Lay Out as Columns") &&
                        ScreenYOf(watched) == yBefore,
                        $"{ScreenYOf(watched)} vs {yBefore}, strip {grid.HeaderRows} rows");
            Pump();
            ok &= Check("and switching back does too",
                        form.ClickMenuForTesting("View", "Split Lines Into Fields", "Lay Out Inline") && ScreenYOf(watched) == yBefore,
                        $"{ScreenYOf(watched)} vs {yBefore}, strip {grid.HeaderRows} rows");
            Pump();
            // The two layouts are nested UNDER the switch, so clicking the switch itself must still throw it
            // rather than only opening what is beneath it - which is the one thing WinForms could reasonably
            // have decided either way.
            ok &= Check("and turning them off hands every row back",
                        form.ClickMenuForTesting("View", "Split Lines Into Fields") &&
                        grid.FirstRowForTesting == firstBefore,
                        $"{grid.FirstRowForTesting}");
            Pump();
            ok &= Check($"with nothing moved on screen ({ScreenYOf(watched)})", ScreenYOf(watched) == yBefore);
            doc.Columns.Layout = FieldLayout.Columns;

            // A key that switches between the layouts, and says so beside the layout it would switch TO.
            ok &= Check("the layout key does nothing while the fields are off", !form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.X));
            // The switch is a menu item with items nested under it, and WinForms hands an item like that
            // neither its shortcut nor the drawing of it - so the key is worth pressing here rather than
            // trusting that clicking the item stands in for it.
            ok &= Check("Ctrl+Shift+C reaches the switch even though it has a submenu now",
                        form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.C) && doc.Columns.Enabled);
            Pump();
            ok &= Check("the key switches layout once they are on",
                        form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.X) &&
                        doc.Columns.Layout == FieldLayout.Inline);
            Pump();
            ok &= Check("and switches back",
                        form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.X) &&
                        doc.Columns.Layout == FieldLayout.Columns);
            form.ClickMenuForTesting("View");   // the strings are put right as the menu opens
            Pump();
            var columnsItem = AllMenuItems(form.MainMenuStrip!.Items).First(m => (m.Text ?? "").Replace("&", "") == "Lay Out as Columns");
            var inlineItem = AllMenuItems(form.MainMenuStrip!.Items).First(m => (m.Text ?? "").Replace("&", "") == "Lay Out Inline");
            ok &= Check("the key is offered against the layout it goes to, not the one already showing",
                        columnsItem.ShortcutKeyDisplayString is null && inlineItem.ShortcutKeyDisplayString == "Ctrl+Shift+X",
                        $"columns=\"{columnsItem.ShortcutKeyDisplayString}\" inline=\"{inlineItem.ShortcutKeyDisplayString}\"");
            form.ClickMenuForTesting("View", "Split Lines Into Fields");
            Pump();

            // The menu is where the key is discovered, so it has to say so - and stay in step with the state.
            // That the key itself reaches the item is WinForms' own shortcut handling, covered end to end by
            // UiFeatureTests.Ctrl_shift_c_splits_the_log_into_columns_and_back.
            var item = AllMenuItems(form.MainMenuStrip!.Items)
                .FirstOrDefault(m => (m.Text ?? "").Replace("&", "") == "Split Lines Into Fields");
            ok &= Check("the menu offers it", item is not null);
            if (item is not null)
            {
                var keys = System.ComponentModel.TypeDescriptor.GetConverter(typeof(Keys));
                // WinForms stops DRAWING a shortcut once an item has children, so having the key is no
                // longer proof that anyone can read it: the item has to be the kind that draws its own.
                ok &= Check($"and advertises the key beside it ({keys.ConvertToString(item.ShortcutKeys)})",
                            item.ShortcutKeys == (Keys.Control | Keys.Shift | Keys.C) && item.ShowShortcutKeys &&
                            (!item.HasDropDownItems || item is CommandWithSubmenu));
                form.ClickMenuForTesting("View");   // the tick is set as the menu opens
                Pump();
                ok &= Check("and is unticked while the columns are off", !item.Checked);
                ok &= Check("and ticked once they are on",
                            form.ClickMenuForTesting("View", "Split Lines Into Fields") && item.Checked);
            }

            // --- the find bar says which text it is going to search, while the shown text is not it ---

            if (!doc.Columns.Enabled) form.ClickMenuForTesting("View", "Split Lines Into Fields");
            Pump();
            var bar = form.FindBarForTesting;
            form.PressCmdKeyForTesting(Keys.Control | Keys.F);
            Pump();
            ok &= Check($"opening find while the fields are on says what will be searched (\"{bar.MessageForTesting()}\")",
                        bar.MessageForTesting() == MainForm.RawLineCaution, bar.MessageForTesting());
            // It gives way to the tally and does not come back between searches - a warning that reappears
            // every time is one that stops being read.
            bar.SetMessage("3 of 200 lines");
            Pump();
            ok &= Check("a tally replaces it", bar.MessageForTesting() == "3 of 200 lines");
            form.PressCmdKeyForTesting(Keys.Control | Keys.F);
            Pump();
            ok &= Check("and Ctrl+F again, with the bar already up, does not put it back",
                        bar.MessageForTesting() == "3 of 200 lines", bar.MessageForTesting());
            // ...but closing and opening the bar is a fresh start, and says it again.
            form.PressCmdKeyForTesting(Keys.Escape);
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.F);
            Pump();
            ok &= Check($"closing and opening it says it again (\"{bar.MessageForTesting()}\")",
                        bar.MessageForTesting() == MainForm.RawLineCaution, bar.MessageForTesting());
            form.PressCmdKeyForTesting(Keys.Escape);
            Pump();
            // With the fields off there is nothing to warn about: the line on screen IS the line.
            form.ClickMenuForTesting("View", "Split Lines Into Fields");
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.F);
            Pump();
            ok &= Check($"and with the fields off it says nothing (\"{bar.MessageForTesting()}\")",
                        bar.MessageForTesting().Length == 0, bar.MessageForTesting());
            form.PressCmdKeyForTesting(Keys.Escape);
            Pump();
            return ok;
        }
        finally
        {
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
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

            ok &= CheckExcludeIcon(doc, tree, host);
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>An exclude hides the lines it matches, so it is marked with an eye that has a slash through
    /// it. The marker is DRAWN, and only exclude rows give up any width to it - most filters are includes
    /// and a column of blank space to their left reads as a mistake.</summary>
    private static bool CheckExcludeIcon(CascadeDocument doc, FilterTreeControl tree, Form host)
    {
        host.ClientSize = new Size(420, 200);
        Pump();

        // Same pattern, same colours, same everything but the kind: then the only thing that can differ
        // between the two rows is the marker and what it displaces.
        var style = new FilterStyle { Foreground = new RgbColor(31, 31, 112), Background = new RgbColor(255, 255, 255) };
        var shown = new Filter { Match = new FilterMatch { Text = "ERROR" }, Style = style.Clone() };
        var hidden = new Filter { Kind = FilterKind.Exclude, Match = new FilterMatch { Text = "ERROR" }, Style = style.Clone() };
        SetFilters(doc, tree, shown, hidden);
        tree.SelectForTesting(shown);
        Pump();

        int room = tree.ExcludeIconRoomForTesting(hidden);
        bool ok = Check($"an include gives up no width to a marker it does not have",
                        tree.ExcludeIconRoomForTesting(shown) == 0);
        ok &= Check($"an exclude reserves room for its marker ({room}px)", room > 0);

        var area = tree.TreeAreaForTesting;
        var incRow = tree.RowBoundsForTesting(shown);
        var excRow = tree.RowBoundsForTesting(hidden);
        int incLeft = tree.ContentLeftForTesting(shown);
        int excLeft = tree.ContentLeftForTesting(hidden);
        using var picture = Capture(host);

        Color At(int x, int rowY) => x < 0 || x >= picture.Width || rowY < 0 || rowY >= picture.Height
            ? Color.Transparent : picture.GetPixel(x, rowY);
        var paper = Color.White;                                    // the fixture's own row background
        int Away(Color c) => Math.Abs(c.R - paper.R) + Math.Abs(c.G - paper.G) + Math.Abs(c.B - paper.B);

        // Something is actually drawn in the space the marker reserved. Measured against the row's own
        // background, not an absolute darkness, so dimming the marker cannot quietly weaken this.
        int drawn = 0, boldest = 0;
        for (int dx = 0; dx < room; dx++)
            for (int y = 1; y < excRow.Height - 1; y++)
            {
                int away = Away(At(area.Left + excLeft + dx, area.Top + excRow.Top + y));
                if (away > 30) drawn++;
                boldest = Math.Max(boldest, away);
            }
        ok &= Check($"the marker is drawn, not just reserved ({drawn} pixels of ink in its {room}px)", drawn > 8);

        // ...and it sits behind the pattern rather than beside it.
        int text = 0;
        for (int dx = room; dx < room + 120; dx++)
            for (int y = 1; y < excRow.Height - 1; y++)
                text = Math.Max(text, Away(At(area.Left + excLeft + dx, area.Top + excRow.Top + y)));
        ok &= Check($"the marker is quieter than the pattern it marks " +
                    $"(marker {boldest} from the background, text {text})",
                    text > 0 && boldest < text * 3 / 4);

        // ...and the pattern beyond it is the same picture as the include's, just moved right by the room
        // the marker took. That is the whole claim, and it fails whichever way the marker goes wrong.
        int compared = 0, differing = 0, firstX = -1;
        int height = Math.Min(incRow.Height, excRow.Height);
        for (int dx = 0; dx < 120; dx++)
            for (int y = 1; y < height - 1; y++)
            {
                var a = At(area.Left + incLeft + dx, area.Top + incRow.Top + y);
                var b = At(area.Left + excLeft + room + dx, area.Top + excRow.Top + y);
                compared++;
                if (a.ToArgb() == b.ToArgb()) continue;
                differing++;
                if (firstX < 0) firstX = dx;
            }
        ok &= Check($"the marker shifts the pattern right by exactly its own width and nothing else " +
                    $"({differing} of {compared} pixels differ" + (firstX < 0 ? "" : $", first at +{firstX}px") + ")",
                    compared > 0 && differing == 0);

        // Sized from the list's own text, so it follows the font and the DPI together - and then stops,
        // because past a point a bigger marker says nothing more and only takes width the pattern needs.
        var baseFont = host.Font;
        using (var larger = new Font(baseFont.FontFamily, baseFont.Size * 1.6f))
        using (var enormous = new Font(baseFont.FontFamily, baseFont.Size * 6f))
        {
            host.Font = larger;
            Pump();
            int grown = tree.ExcludeIconRoomForTesting(hidden);
            host.Font = enormous;
            Pump();
            int capped = tree.ExcludeIconRoomForTesting(hidden);
            host.Font = baseFont;
            Pump();

            ok &= Check($"the marker grows with the font ({room}px at {baseFont.Size:0.#}pt, " +
                        $"{grown}px at {larger.Size:0.#}pt)", grown > room);
            ok &= Check($"and stops growing rather than eating the pattern column " +
                        $"({capped}px at {enormous.Size:0.#}pt)", capped < room * 3);
        }
        return ok;
    }

    private static void SetFilters(CascadeDocument doc, FilterTreeControl tree, params Filter[] filters)
    {
        var collection = new FilterCollection();
        foreach (var f in filters) collection.Roots.Add(f);
        doc.SetFilters(collection);
        tree.Rebuild();
        Pump();
    }

    /// <summary>A preset's TICK says it is in effect: it switches the filters it names on and off and leaves
    /// every other filter alone. Its SELECTION is only the user's aim, and must survive anything the model
    /// does - while the two were one thing, aiming at a preset switched its filters back on and it could
    /// never be updated to drop one.</summary>
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
            // In no preset at all, which is what says the presets keep their hands off everything else.
            var loose = new Filter { Match = new FilterMatch { Text = "line" } };
            foreach (var f in new[] { a, b, c, loose }) collection.Roots.Add(f);
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

            // Ticking one preset puts exactly its filters on.
            pane.TickForTesting("first");
            Pump();
            ok &= Check("ticking a preset enables exactly its filters", a.Enabled && !b.Enabled && !c.Enabled,
                        $"a={a.Enabled} b={b.Enabled} c={c.Enabled}");

            // Ticking a second means "and these too", and does not drop the first.
            pane.TickForTesting("first", "second");
            Pump();
            ok &= Check("ticking a second adds its filters to the first's", a.Enabled && b.Enabled && c.Enabled,
                        $"a={a.Enabled} b={b.Enabled} c={c.Enabled}");

            pane.TickForTesting("second");
            Pump();
            ok &= Check("unticking one turns its filters back off", !a.Enabled && b.Enabled && c.Enabled,
                        $"a={a.Enabled} b={b.Enabled} c={c.Enabled}");

            pane.TickForTesting();
            Pump();
            ok &= Check("unticking the last leaves every filter off", !a.Enabled && !b.Enabled && !c.Enabled,
                        $"a={a.Enabled} b={b.Enabled} c={c.Enabled}");

            // THE REPORTED BUG. A preset says which filters belong to it and nothing about the rest, so a
            // filter switched on by hand has to survive a preset being put in and taken out of effect.
            loose.Enabled = true;
            pane.TickForTesting("first");
            Pump();
            ok &= Check("ticking a preset leaves a filter it does not name alone", a.Enabled && loose.Enabled,
                        $"a={a.Enabled} loose={loose.Enabled}");
            pane.TickForTesting();
            Pump();
            ok &= Check("and unticking it takes only its own filters away", !a.Enabled && loose.Enabled,
                        $"a={a.Enabled} loose={loose.Enabled}");
            loose.Enabled = false;
            pane.RefreshActive();

            // A burst that nets out must cost nothing at all: re-running a pass over a whole file to arrive
            // back where it started is a visible flicker of the progress bar and a lot of work for no answer.
            applied = 0;
            pane.TickForTesting("first");
            pane.TickForTesting();
            Pump();
            ok &= Check("ticking a preset and unticking it again before the pass runs re-filters nothing",
                        applied == 0 && !a.Enabled, $"applied {applied} times, a={a.Enabled}");

            // A burst of tick changes must cost one re-filter, not one each: applying is what re-runs the
            // filters over the whole file.
            applied = 0;
            pane.TickForTesting("first");
            pane.TickForTesting("second");
            pane.TickForTesting("first", "second");
            Pump();
            ok &= Check("a burst of tick changes re-filters once", applied == 1, $"applied {applied} times");

            // Landing on the same set of filters must cost nothing at all. Every click in the pane used to
            // re-run a pass over the whole file to arrive back where it started, which on a big file is a
            // visible flicker of the progress bar and a great deal of work for no answer.
            applied = 0;
            pane.TickForTesting("first", "second");
            Pump();
            pane.TickForTesting("first", "second");
            Pump();
            ok &= Check("but re-picking the same presets does not re-filter at all", applied == 0,
                        $"applied {applied} times");

            // ---- the tick and the selection are different things ----

            // THE REPORTED BUG. Aiming at a preset - by clicking its name, or by right-clicking it to reach
            // the menu - must not switch a single filter.
            pane.TickForTesting();
            Pump();
            pane.ClickForTesting("first");
            Pump();
            ok &= Check("clicking a preset's name aims at it and switches nothing on",
                        pane.SelectedForTesting == "first" && pane.ActiveForTesting.Length == 0 && !a.Enabled,
                        $"selected '{pane.SelectedForTesting}', in effect [{string.Join(",", pane.ActiveForTesting)}], a={a.Enabled}");

            pane.ClickForTesting("second", MouseButtons.Right);
            Pump();
            ok &= Check("right-clicking a preset aims the menu at it and switches nothing on",
                        pane.SelectedForTesting == "second" && pane.ActiveForTesting.Length == 0 && !b.Enabled && !c.Enabled,
                        $"selected '{pane.SelectedForTesting}', in effect [{string.Join(",", pane.ActiveForTesting)}]");

            // The tick box is the one place a press does switch filters.
            pane.ClickForTesting("first", onTick: true);
            Pump();
            ok &= Check("pressing the tick box does put the preset in effect", a.Enabled && !b.Enabled,
                        $"a={a.Enabled} b={b.Enabled}");
            pane.ClickForTesting("first", onTick: true);
            Pump();
            ok &= Check("and pressing it again takes it out", !a.Enabled, $"a={a.Enabled}");

            // Windows toggles a tick of its own accord when an already-selected row is clicked, and on the
            // second click of a double-click - which is how renaming would switch a preset on.
            pane.NativeToggleForTesting("first");
            Pump();
            ok &= Check("a tick Windows set on its own is refused",
                        pane.ActiveForTesting.Length == 0 && !a.Enabled,
                        $"in effect [{string.Join(",", pane.ActiveForTesting)}], a={a.Enabled}");

            // Nothing in the model may move the user's aim.
            pane.SelectForTesting("first");
            b.Enabled = true; c.Enabled = true;
            pane.RefreshActive();
            Pump();
            ok &= Check("the selection stays where the user put it while the filters change under it",
                        pane.SelectedForTesting == "first" && pane.ActiveForTesting.SequenceEqual(new[] { "second" }),
                        $"selected '{pane.SelectedForTesting}', in effect [{string.Join(",", pane.ActiveForTesting)}]");

            // ---- the workflow the whole change exists for ----
            pane.TickForTesting("second");
            Pump();
            c.Enabled = false;                       // drop one of its filters by hand
            pane.RefreshActive();
            ok &= Check("dropping one of a preset's filters unticks it", pane.ActiveForTesting.Length == 0,
                        string.Join(",", pane.ActiveForTesting));
            pane.ClickForTesting("second", MouseButtons.Right);
            Pump();
            ok &= Check("and the filter stays off while the menu is aimed at the preset", !c.Enabled);
            pane.UpdateSelected();
            Pump();
            ok &= Check("so the preset can be updated to drop it",
                        collection.Presets[1].FilterIds.SequenceEqual(new[] { b.Id }),
                        $"second now names {collection.Presets[1].FilterIds.Count} filters");
            ok &= Check("and what is left of it is in effect again",
                        pane.ActiveForTesting.SequenceEqual(new[] { "second" }), string.Join(",", pane.ActiveForTesting));

            // "Apply Only This Preset" is what a single click used to mean.
            pane.TickForTesting("first", "second");
            Pump();
            pane.SelectForTesting("first");
            pane.ApplyOnlySelected();
            Pump();
            ok &= Check("applying only the selected preset takes the others out",
                        pane.ActiveForTesting.SequenceEqual(new[] { "first" }) && a.Enabled && !b.Enabled,
                        $"in effect [{string.Join(",", pane.ActiveForTesting)}], a={a.Enabled} b={b.Enabled}");

            // The other direction: enabling by hand is enough to put a preset in effect.
            a.Enabled = false; b.Enabled = false; c.Enabled = false;
            pane.RefreshActive();
            ok &= Check("nothing is in effect after everything is switched off by hand", pane.ActiveForTesting.Length == 0,
                        string.Join(",", pane.ActiveForTesting));
            b.Enabled = true;
            pane.RefreshActive();
            ok &= Check("ticking every filter of a preset by hand puts it in effect",
                        pane.ActiveForTesting.SequenceEqual(new[] { "second" }), string.Join(",", pane.ActiveForTesting));

            // The tick box has to be inside the strip that reacts to a press, or the two disagree about
            // where a preset is switched on. Measured against the zone, never a raw pixel count.
            ok &= CheckTickZoneCoversTheBox(pane);

            // A deleted filter is remembered but reported.
            collection.Remove(b);
            pane.Rebuild();
            ok &= Check("a preset says how many of its filters have gone",
                        pane.LabelsForTesting[1].Contains("1 missing"), string.Join(" | ", pane.LabelsForTesting));

            // ...and one that names nothing still standing has nothing to put in effect: the tick springs
            // back rather than claiming it is on, and no pass is run for it.
            applied = 0;
            pane.ClickForTesting("second", onTick: true);
            Pump();
            ok &= Check("and ticking one whose filters have all gone comes to nothing",
                        applied == 0 && !pane.ActiveForTesting.Contains("second", StringComparer.Ordinal),
                        $"applied {applied} times, in effect [{string.Join(",", pane.ActiveForTesting)}]");

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

    /// <summary>Ticks the first preset and compares the two renderings: whatever changed is the box, and all
    /// of it has to fall inside the strip a press toggles from.</summary>
    private static bool CheckTickZoneCoversTheBox(FilterPresetsControl pane)
    {
        pane.TickForTesting();
        Pump();
        using var off = pane.RenderRowsForTesting();
        pane.TickForTesting("first");
        Pump();
        using var on = pane.RenderRowsForTesting();

        var row = pane.RowBoundsForTesting(0);
        int left = int.MaxValue, right = -1;
        int wide = Math.Min(off.Width, on.Width), tall = Math.Min(off.Height, on.Height);
        for (int y = Math.Max(0, row.Top); y < Math.Min(row.Bottom, tall); y++)
            for (int x = 0; x < wide; x++)
                if (off.GetPixel(x, y).ToArgb() != on.GetPixel(x, y).ToArgb())
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                }

        int zone = pane.TickZoneWidthForTesting;
        return Check("the tick zone covers the box it draws", right >= 0 && right < zone,
                     $"the box spans {left}..{right}, the zone is {zone}px wide");
    }

    /// <summary>The minimap is the log seen from far enough away that a row is a pixel, so what matters is
    /// that a pixel stands for the right row in the right colour - and that the window it shows follows the
    /// view without chasing it. The summary is checked directly, and separately that it is painted, that it
    /// repaints when it must, and that it stays cheap.</summary>
    private static bool RunMatchMapChecks()
    {
        Line("-- minimap --");
        const int lines = 40_000;
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_map_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        // Lines 0..9,999 are COMMON; line 25,000 alone is RARE. The lone one is what proves a match is never
        // rounded away, and the twenty-five thousand plain lines around it are what the gaps compress.
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

            var settings = new AppSettings();
            var common = new Filter { Enabled = true, Match = new FilterMatch { Text = "COMMON" }, Style = { Background = new RgbColor(0x22, 0x44, 0xEE) } };
            var rare = new Filter { Enabled = true, Match = new FilterMatch { Text = "RARE" }, Style = { Foreground = new RgbColor(0xEE, 0x22, 0x22) } };
            var collection = new FilterCollection();
            collection.Roots.Add(common);
            collection.Roots.Add(rare);
            doc.SetFilters(collection);
            WaitForFiltering(doc);
            Pump();

            var map = grid.MatchMapForTesting;
            bool ok = Check("the map and the scrollbar are both there",
                            map is not null && map.Visible && grid.VerticalScrollBarVisibleForTesting);
            if (map is null) return false;

            // Side by side and both hittable: two narrow strips of the same colour cannot be told apart or
            // aimed at, which is what they were. In device pixels, so it holds at whatever the screen is
            // scaled to - measured against the same scaling, or a high-DPI screen would pass it on its own.
            var mapBounds = grid.MapBoundsForTesting;
            var barBounds = grid.ScrollBarBoundsForTesting;
            ok &= Check("each is wide enough to hit",
                        mapBounds.Width >= map.LogicalToDeviceUnits(16) &&
                        barBounds.Width >= map.LogicalToDeviceUnits(12),
                        $"map {mapBounds.Width}px, scrollbar {barBounds.Width}px, " +
                        $"wanting {map.LogicalToDeviceUnits(16)} and {map.LogicalToDeviceUnits(12)}");
            ok &= Check("the scrollbar is the outer one", barBounds.Left >= mapBounds.Right - 1,
                        $"map ends {mapBounds.Right}, scrollbar starts {barBounds.Left}");
            using (var picture = Capture(host))
            {
                int y = mapBounds.Top + mapBounds.Height / 2;
                var textSide = picture.GetPixel(Math.Max(0, mapBounds.Left - 2), y);
                var rule = picture.GetPixel(mapBounds.Left, y);
                var mapSide = picture.GetPixel(mapBounds.Left + mapBounds.Width / 2, y);
                var trough = picture.GetPixel(barBounds.Left + barBounds.Width - 2, y);
                ok &= Check("a rule separates the map from the text", rule.ToArgb() != textSide.ToArgb() &&
                            rule.ToArgb() != mapSide.ToArgb(),
                            $"text {textSide}, rule {rule}, map {mapSide}");
                // Against the gutter the map is drawn on, not against whatever row happens to be at this
                // height: the two strips have to stay apart where the map has nothing on it, which is most
                // of it, and a coloured row would answer for the trough by accident.
                ok &= Check("and the scrollbar's trough is not the map's background",
                            trough.ToArgb() != settings.GutterBack.ToArgb() && trough.ToArgb() != mapSide.ToArgb(),
                            $"map background {settings.GutterBack}, row here {mapSide}, trough {trough}");
                // ...and it is closed off at the ends, as the scrollbar is, rather than running into
                // whatever is above and below it.
                int x = mapBounds.Left + mapBounds.Width / 2;
                var mapTop = picture.GetPixel(x, mapBounds.Top);
                var mapBottom = picture.GetPixel(x, mapBounds.Bottom - 1);
                ok &= Check("and the map is closed off at the top and bottom too",
                            mapTop.ToArgb() == rule.ToArgb() && mapBottom.ToArgb() == rule.ToArgb(),
                            $"rule {rule}, top {mapTop}, bottom {mapBottom}");
            }

            // The scrollbar is framed on every side, not just the one facing the map, and its thumb has
            // square corners - a rounded one against the map's square window read as a different kind of
            // thing altogether. Read off the painted pixels: the frame is drawn, not laid out, so geometry
            // would answer for a bar that paints only one edge.
            var bar = grid.ScrollBarForTesting;
            if (bar is not null)
            {
                var trough = bar.TroughForTesting;
                using var shot = new Bitmap(bar.Width, bar.Height);
                bar.DrawToBitmap(shot, new Rectangle(0, 0, bar.Width, bar.Height));
                // Down the first column of the trough, which the thumb is inset away from.
                var inside = shot.GetPixel(trough.Left, trough.Top + trough.Height / 2);
                var above = shot.GetPixel(trough.Left, 0);
                var below = shot.GetPixel(trough.Left, bar.Height - 1);
                var beside = shot.GetPixel(0, trough.Top + trough.Height / 2);
                ok &= Check("the scrollbar is framed on all four sides",
                            above.ToArgb() == beside.ToArgb() && below.ToArgb() == beside.ToArgb() &&
                            beside.ToArgb() != inside.ToArgb(),
                            $"trough {inside}, above {above}, below {below}, beside {beside}");
                var thumb = bar.ThumbForTesting;
                var corner = shot.GetPixel(thumb.Left, thumb.Top);
                var middle = shot.GetPixel(thumb.Left + thumb.Width / 2, thumb.Top + thumb.Height / 2);
                ok &= Check("and its thumb has square corners", corner.ToArgb() == middle.ToArgb(),
                            $"corner {corner}, middle {middle}");

                // A bar is laid out before it has any height, and can be squeezed to nothing by a window
                // being dragged shut. A thumb has a minimum length, and asking a bar shorter than that for
                // one threw - which reached the outside world as a window that would not lay out.
                bool squeezedAnswers;
                try
                {
                    using var squeezed = new SlimScrollBar(grid) { Size = new Size(14, 2) };
                    squeezed.Configure(1_000, 10);
                    squeezedAnswers = squeezed.ThumbForTesting.Height >= 0;
                }
                catch (ArgumentException) { squeezedAnswers = false; }
                ok &= Check("a scrollbar squeezed shorter than its own thumb still answers for one",
                            squeezedAnswers);

                // ---- a drag draws as often as the screen can show it, and no oftener ----
                // A mouse reports up to a thousand times a second and each report moves the view a page or
                // more. Drawing every one of them is work nobody can see: the screen shows the newest frame
                // at its next refresh and throws the rest away. What must never happen is the other failure -
                // the hand stopping on a position that is never drawn at all.
                //
                // Held to what the refresh rate allows in the time the reports actually took, rather than to
                // a flat "fewer frames than reports": on a machine where a paint takes longer than a refresh
                // interval, drawing every report IS the right answer, and a check that called that a failure
                // would be reporting the hardware rather than the code.
                grid.ScrollToRow(0);
                Pump();
                var track = bar.TroughForTesting;
                int painted = grid.PaintsForTesting;
                bar.GrabForTesting();
                const int Reports = 60;
                var dragClock = Stopwatch.StartNew();
                for (int i = 0; i < Reports; i++)
                    bar.DragToForTesting(track.Top + thumb.Height / 2 + i * (track.Height / (2 * Reports)));
                dragClock.Stop();
                int frames = grid.PaintsForTesting - painted;
                long stoppedAt = grid.FirstRowForTesting;
                bar.DropForTesting();
                int hz = grid.ScreenRateForTesting;
                // One for the frame that may already have been owed when the drag began, one for rounding.
                int allowed = (int)Math.Ceiling(dragClock.Elapsed.TotalSeconds * hz) + 2;
                Line($"   (drag: {Reports} reports in {dragClock.Elapsed.TotalMilliseconds:F0} ms drew {frames} " +
                     $"frames; {hz} Hz allows {allowed})");
                ok &= Check("a drag never draws more frames than the screen can show in the time it took",
                            frames <= allowed, $"{frames} frames, {allowed} allowed at {hz} Hz");
                ok &= Check("and reporting faster than that really does cost frames",
                            frames < Reports || allowed >= Reports,
                            $"{frames} frames for {Reports} reports, {allowed} allowed");
                ok &= Check("but the view really did move all the way", stoppedAt > 0, stoppedAt.ToString());
                Pump();
                ok &= Check("and where the hand stopped is what ends up on the screen",
                            grid.FirstPaintedRowForTesting == grid.FirstRowForTesting,
                            $"drawn from row {grid.FirstPaintedRowForTesting}, view at {grid.FirstRowForTesting}");
            }

            // The sideways scrollbar is the same control, so the two edges of the window match.
            var hbar = grid.HScrollBarForTesting;
            if (hbar.Visible && hbar.Width > 8)
            {
                var htrough = hbar.TroughForTesting;
                using var hshot = new Bitmap(hbar.Width, hbar.Height);
                hbar.DrawToBitmap(hshot, new Rectangle(0, 0, hbar.Width, hbar.Height));
                var inside = hshot.GetPixel(htrough.Left + htrough.Width / 2, htrough.Top);
                var above = hshot.GetPixel(htrough.Left + htrough.Width / 2, 0);
                var below = hshot.GetPixel(htrough.Left + htrough.Width / 2, hbar.Height - 1);
                var beside = hshot.GetPixel(0, htrough.Top);
                ok &= Check("the sideways scrollbar is framed on all four sides too",
                            above.ToArgb() == beside.ToArgb() && below.ToArgb() == beside.ToArgb() &&
                            beside.ToArgb() != inside.ToArgb(),
                            $"trough {inside}, above {above}, below {below}, beside {beside}");
                ok &= Check("and is as thick as the vertical one",
                            bar is null || hbar.Height == bar.Width, $"{hbar.Height}px vs {bar?.Width}px");
                // It stops before them rather than running underneath, so switching wrapping on and off -
                // which takes it away and brings it back - does not shift them up and down by its height.
                ok &= Check("and stops before the map and the scrollbar",
                            hbar.Right <= grid.MapBoundsForTesting.Left,
                            $"sideways bar ends {hbar.Right}, map starts {grid.MapBoundsForTesting.Left}");
                ok &= Check("so they run the full height of the view",
                            grid.MapBoundsForTesting.Bottom >= hbar.Bottom &&
                            grid.ScrollBarBoundsForTesting.Bottom >= hbar.Bottom,
                            $"map ends {grid.MapBoundsForTesting.Bottom}, bar ends {hbar.Bottom}");
            }

            // Both bars have to answer for where they are: assistive technology reads a scrollbar's value,
            // and so do the UI tests. A drawn control gets that only by saying so.
            grid.ScrollBarForTesting.Value = 7;
            ok &= Check("the scrollbar tells anyone asking where it is",
                        grid.ScrollBarForTesting.AccessibilityObject.Value == "7",
                        grid.ScrollBarForTesting.AccessibilityObject.Value ?? "(null)");
            grid.ScrollBarForTesting.Value = 0;

            grid.ScrollToRow(0);
            map.RebuildForTesting();
            int slots = map.SlotCountForTesting;
            ok &= Check("the map is one pixel row per row of text", slots > 50 && map.RowPixelsForTesting >= 1,
                        $"{slots} slots of {map.RowPixelsForTesting}px");
            if (slots <= 50) return false;

            // Half a map, in rows. Anything nearer the start of the file than this has its window clamped
            // against it, and a screen at another scaling puts the map at another height and so another
            // row - which is how several of these checks quietly stopped meaning anything on the build
            // machine while passing here.
            long HalfMap() => (long)(map.SlotCountForTesting / 2) * map.RowsPerPixelForTesting;

            // ---- one rate the whole way down, in the rows' own colours ----
            ok &= Check("it starts at the top of the file", map.TopRowForTesting == 0, map.TopRowForTesting.ToString());
            int step = map.RowsPerPixelForTesting;
            ok &= Check("and every pixel stands for the same number of rows",
                        step > 1 && step <= 32 && Enumerable.Range(0, slots).All(s => map.RowAtForTesting(s) == (long)s * step),
                        $"{step} rows a pixel; pixel 1 holds row {map.RowAtForTesting(1)}, pixel 20 holds {map.RowAtForTesting(20)}");
            ok &= Check("a matching row takes its filter's background",
                        map.ColourAtForTesting(5) == Color.FromArgb(0x22, 0x44, 0xEE).ToArgb(),
                        Color.FromArgb(map.ColourAtForTesting(5)).ToString());

            // A filter with a text colour and no background of its own: the row would be invisible without
            // falling back to it. And it is a single line among tens of thousands, so this is also what
            // proves the rarest colour in a pixel wins - thirty-one plain rows do not vote it away.
            grid.ScrollToRow(25_000);
            map.RebuildForTesting();
            int rareSlot = map.SlotOfForTesting(25_000);
            var rareRows = map.RowsAtForTesting(rareSlot);
            ok &= Check("the lone match is somewhere on the map",
                        rareSlot >= 0 && rareRows.From <= 25_000 && rareRows.To > 25_000,
                        $"slot {rareSlot} holds rows {rareRows.From}-{rareRows.To - 1}");
            ok &= Check("and takes its filter's text colour when it sets no background",
                        map.ColourAtForTesting(rareSlot) == Color.FromArgb(0xEE, 0x22, 0x22).ToArgb(),
                        Color.FromArgb(map.ColourAtForTesting(rareSlot)).ToString());

            // ---- gaps ----
            // Twenty-five thousand plain lines lie between the two filters. At one pixel a row the map would
            // never reach the second from the first; compressed, it does.
            ok &= Check("a stretch with nothing in it is compressed", map.SpanForTesting > slots * 4,
                        $"{map.SpanForTesting} rows across {slots} pixels");
            ok &= Check("but the compression is bounded, not unlimited",
                        map.SpanForTesting < (long)slots * 40, map.SpanForTesting.ToString());
            ok &= Check("a compressed pixel is blank", map.ColourAtForTesting(Math.Max(0, rareSlot - 3)) == 0,
                        Color.FromArgb(map.ColourAtForTesting(Math.Max(0, rareSlot - 3))).ToString());
            ok &= Check("and the rows behind the pixels never go backwards",
                        Enumerable.Range(1, slots - 1).All(s => map.RowAtForTesting(s) >= map.RowAtForTesting(s - 1)));

            // Hiding the unmatched rows used to leave nothing to compress, so the map held one row per pixel
            // and reached ten thousand rows across two hundred - the mode that needed it most got none of it.
            doc.Filters.ShowOnlyFilteredLines = true;
            grid.RefreshView();
            grid.ScrollToRow(0);
            map.RebuildForTesting();
            ok &= Check("with only matching lines shown the rows are compressed too",
                        map.SpanForTesting > map.SlotCountForTesting * 4,
                        $"{map.SpanForTesting} rows across {map.SlotCountForTesting} pixels");
            ok &= Check("and every pixel is coloured", Enumerable.Range(0, map.SlotCountForTesting).All(s => map.ColourAtForTesting(s) != 0));
            doc.Filters.ShowOnlyFilteredLines = false;
            grid.RefreshView();
            Pump();

            // ---- the rarest colour in a pixel wins ----
            // A pixel stands for many rows but can only be one colour, and the one worth showing is the one
            // you would otherwise miss. SPECIAL matches a single line inside the COMMON block, so its pixel
            // holds thirty-one COMMON rows and one of it - and it is SPECIAL that has to come through.
            var special = new Filter { Enabled = true, Match = new FilterMatch { Text = "line 5000" }, Style = { Background = new RgbColor(0x11, 0xCC, 0x33) } };
            collection.Roots.Insert(0, special);   // topmost wins the colour, so the row really is SPECIAL
            doc.SetFilters(collection);
            WaitForFiltering(doc);
            grid.ScrollToRow(5_000);
            map.RebuildForTesting();
            int specialSlot = map.SlotOfForTesting(5_000);
            var specialRows = map.RowsAtForTesting(specialSlot);
            ok &= Check("the pixel holding the lone SPECIAL row is mostly COMMON rows",
                        specialRows.To - specialRows.From > 8,
                        $"rows {specialRows.From}-{specialRows.To - 1}");
            ok &= Check("and it shows SPECIAL, not the colour of the many",
                        map.ColourAtForTesting(specialSlot) == Color.FromArgb(0x11, 0xCC, 0x33).ToArgb(),
                        Color.FromArgb(map.ColourAtForTesting(specialSlot)).ToString());
            ok &= Check("while the pixels either side are COMMON",
                        map.ColourAtForTesting(specialSlot - 1) == Color.FromArgb(0x22, 0x44, 0xEE).ToArgb() &&
                        map.ColourAtForTesting(specialSlot + 1) == Color.FromArgb(0x22, 0x44, 0xEE).ToArgb(),
                        $"{Color.FromArgb(map.ColourAtForTesting(specialSlot - 1))} / {Color.FromArgb(map.ColourAtForTesting(specialSlot + 1))}");

            // ---- the colours are remembered, and never stale ----
            // Scrolling slides the remembered colours along rather than reading the file again, so the one
            // thing that can go wrong is a pixel keeping a colour that belongs to another row. SPECIAL is
            // deliberately still on: a row nothing matched never asks the remembered colours at all, so
            // sliding them wrongly inside one solid block puts blue where blue belongs and nothing notices.
            // It takes a second colour in view for the fault to have anywhere to show.
            grid.ScrollToRow(HalfMap() + 4_000);
            map.RebuildForTesting();
            long cacheTop = map.TopRowForTesting;
            int beforeSliding = map.ColoursResolvedForTesting;
            grid.ScrollToRow(HalfMap() + 4_400);
            map.RebuildForTesting();
            int resolvedSliding = map.ColoursResolvedForTesting - beforeSliding;
            var slid = Enumerable.Range(0, map.SlotCountForTesting).Select(map.ColourAtForTesting).ToArray();
            ok &= Check("the window really slid, so there is something to have kept",
                        map.TopRowForTesting > cacheTop, $"top {cacheTop} -> {map.TopRowForTesting}");
            ok &= Check("the map really is showing more than one colour",
                        slid.Distinct().Count() >= 2, $"{slid.Distinct().Count()} colours across {slid.Length} pixels");
            int beforeCold = map.ColoursResolvedForTesting;
            map.InvalidateSummary();
            map.RebuildForTesting();
            int resolvedCold = map.ColoursResolvedForTesting - beforeCold;
            var fresh = Enumerable.Range(0, map.SlotCountForTesting).Select(map.ColourAtForTesting).ToArray();
            int firstWrong = Enumerable.Range(0, Math.Min(slid.Length, fresh.Length)).FirstOrDefault(i => slid[i] != fresh[i], -1);
            Line($"   (map cache: {resolvedSliding} rows read after the scroll, {resolvedCold} from cold, " +
                 $"{slid.Distinct().Count()} colours across {slid.Length} pixels)");
            ok &= Check("what it remembered is what a fresh read gives",
                        slid.Length == fresh.Length && firstWrong < 0,
                        firstWrong < 0 ? $"{slid.Length} pixels agree"
                                       : $"pixel {firstWrong}: {Color.FromArgb(slid[firstWrong])} vs {Color.FromArgb(fresh[firstWrong])}");
            ok &= Check("and sliding it read far fewer rows than starting over",
                        resolvedSliding * 4 < resolvedCold,
                        $"{resolvedSliding} rows read after a 400-row scroll, {resolvedCold} from cold");

            // ---- and remembered for the whole file, not just the window ----
            // Reading a log is dragging up and down it, and a drag moves the window by more than its own
            // height on every mouse report - so what makes a drag affordable is that the way back costs
            // nothing. The colours must be the same ones, too: a block of the file is a block of the file
            // wherever the window happens to be sitting when it is asked about.
            grid.ScrollToRow(HalfMap() + 4_000);
            map.RebuildForTesting();
            var wasHere = Enumerable.Range(0, map.SlotCountForTesting).Select(map.ColourAtForTesting).ToArray();
            grid.ScrollToRow(lines - HalfMap() - 1);   // the far end, well past anything this window covers
            map.RebuildForTesting();
            int beforeReturn = map.ColoursResolvedForTesting;
            grid.ScrollToRow(HalfMap() + 4_000);
            map.RebuildForTesting();
            var backHere = Enumerable.Range(0, map.SlotCountForTesting).Select(map.ColourAtForTesting).ToArray();
            int changed = Enumerable.Range(0, Math.Min(wasHere.Length, backHere.Length))
                                    .FirstOrDefault(i => wasHere[i] != backHere[i], -1);
            ok &= Check("coming back to a stretch it has been over reads nothing at all",
                        map.ColoursResolvedForTesting == beforeReturn,
                        $"{map.ColoursResolvedForTesting - beforeReturn} rows read on the way back");
            ok &= Check("and shows exactly the colours it showed the first time",
                        wasHere.Length == backHere.Length && changed < 0,
                        changed < 0 ? $"{wasHere.Length} pixels agree"
                                    : $"pixel {changed}: {Color.FromArgb(wasHere[changed])} vs {Color.FromArgb(backHere[changed])}");

            // A change of settings has to reach the map: what counts as "no colour at all" is one of them,
            // so the colours it remembered were worked out against the settings that were in force.
            int beforeSettings = map.ColoursResolvedForTesting;
            grid.ApplySettings(settings);
            map.RebuildForTesting();
            ok &= Check("a change of settings makes the map work its colours out again",
                        map.ColoursResolvedForTesting > beforeSettings,
                        $"{map.ColoursResolvedForTesting - beforeSettings} rows read");

            collection.Roots.Remove(special);
            doc.SetFilters(collection);
            WaitForFiltering(doc);
            Pump();

            // ---- two common filters share the map rather than one hiding the other ----
            // Every pixel of a compressed map holds rows of both, so always giving way to the same one paints
            // the whole map in it and the other might as well not be enabled. Found on a real window: a map
            // with [api-gateway] and [order-service] on was one flat colour, identical 700,000 rows apart.
            // The two have to be of COMPARABLE weight (here roughly 30% against 70% of the COMMON block) or
            // the rarer simply wins outright and this proves nothing.
            var alsoCommon = new Filter
            {
                Enabled = true,
                Match = new FilterMatch { Text = "[012]$", Regex = true },
                Style = { Background = new RgbColor(0xEE, 0x99, 0x11) }
            };
            collection.Roots.Insert(0, alsoCommon);
            doc.SetFilters(collection);
            WaitForFiltering(doc);
            grid.ScrollToRow(HalfMap() + 3_000);
            map.RebuildForTesting();
            var mixed = Enumerable.Range(0, map.SlotCountForTesting).Select(map.ColourAtForTesting).ToArray();
            int bluePixels = mixed.Count(c => c == Color.FromArgb(0x22, 0x44, 0xEE).ToArgb());
            int amberPixels = mixed.Count(c => c == Color.FromArgb(0xEE, 0x99, 0x11).ToArgb());
            ok &= Check("both of two common filters reach the map, neither hiding the other",
                        bluePixels > mixed.Length / 10 && amberPixels > mixed.Length / 10,
                        $"{bluePixels} pixels of one, {amberPixels} of the other, across {mixed.Length}");
            // ...and it is the file they follow, not the map: the same rows keep the same colour when the
            // window moves, or the pattern would crawl about under the eye on every scroll.
            long mixedTop = map.TopRowForTesting;
            var wasAt = Enumerable.Range(0, map.SlotCountForTesting)
                                  .ToDictionary(s => map.RowAtForTesting(s), map.ColourAtForTesting);
            grid.ScrollToRow(HalfMap() + 3_400);
            map.RebuildForTesting();
            int restained = Enumerable.Range(0, map.SlotCountForTesting)
                                      .Count(s => wasAt.TryGetValue(map.RowAtForTesting(s), out int was) && was != map.ColourAtForTesting(s));
            ok &= Check("and the window moved, so the rows were really re-divided",
                        map.TopRowForTesting > mixedTop, $"top {mixedTop} -> {map.TopRowForTesting}");
            ok &= Check("and scrolling does not repaint the rows that did not move",
                        restained == 0, $"{restained} pixels changed colour without their rows changing");
            collection.Roots.Remove(alsoCommon);
            doc.SetFilters(collection);
            WaitForFiltering(doc);
            Pump();

            // ---- the rate is the least that fits ----
            // Compressing harder than the file needs throws away detail for nothing; refusing to compress
            // past the cap keeps a pixel something that can be aimed at.
            ok &= Check("a file that fits outright is not compressed at all",
                        MiniMapControl.RowsPerPixelFor(100, 700) == 1 && MiniMapControl.RowsPerPixelFor(700, 700) == 1,
                        $"{MiniMapControl.RowsPerPixelFor(100, 700)}, {MiniMapControl.RowsPerPixelFor(700, 700)}");
            ok &= Check("one that nearly fits is compressed only as much as it has to be",
                        MiniMapControl.RowsPerPixelFor(1_400, 700) == 2 && MiniMapControl.RowsPerPixelFor(6_300, 700) == 9,
                        $"{MiniMapControl.RowsPerPixelFor(1_400, 700)}, {MiniMapControl.RowsPerPixelFor(6_300, 700)}");
            ok &= Check("and one past its reach stops at the cap",
                        MiniMapControl.RowsPerPixelFor(22_400, 700) == 32 && MiniMapControl.RowsPerPixelFor(4_000_000, 700) == 32,
                        $"{MiniMapControl.RowsPerPixelFor(22_400, 700)}, {MiniMapControl.RowsPerPixelFor(4_000_000, 700)}");

            // Nothing enabled means no row can have a colour, so there is nothing to read at all.
            var nothing = new FilterCollection();
            doc.SetFilters(nothing);
            WaitForFiltering(doc);
            map.InvalidateSummary();
            int resolvedBefore = map.ColoursResolvedForTesting;
            map.RebuildForTesting();
            ok &= Check("with no filters on it reads no lines whatever",
                        map.ColoursResolvedForTesting == resolvedBefore,
                        $"{map.ColoursResolvedForTesting - resolvedBefore} rows read");
            doc.SetFilters(collection);
            WaitForFiltering(doc);
            Pump();

            // ---- the window stays centred on the view ----
            // The rectangle holds still and the picture moves under it. Letting it drift instead means the
            // context runs out ahead of you exactly as you scroll towards it.
            // Park at least half a map into the file, worked out from the map's own scale: closer to the
            // start than that and the window is rightly clamped against it, which on a screen at a
            // different scaling is a different row entirely.
            long halfMap = HalfMap();
            grid.ScrollToRow(halfMap + 5_000);
            map.RebuildForTesting();
            long settled = map.TopRowForTesting;
            var before = map.ViewportForTesting;
            long behindBefore = map.RowAtForTesting(map.SlotCountForTesting / 2);
            ok &= Check("the window is clear of the start of the file, so it has somewhere to move from",
                        settled > 0, $"top {settled}, half a map is {halfMap} rows");
            // Worth whole pixels of the map: the window sits on a fixed grid of the file, so a scroll of
            // fewer rows than one pixel stands for rightly leaves it exactly where it is.
            grid.ScrollToRow(halfMap + 5_000 + map.RowsPerPixelForTesting * 10L);
            map.RebuildForTesting();
            ok &= Check("a scroll carries the window with it", map.TopRowForTesting > settled,
                        $"{settled} -> {map.TopRowForTesting}");
            ok &= Check("so the rectangle stays where it is", Math.Abs(map.ViewportForTesting.Top - before.Top) <= 4,
                        $"y {before.Top} -> {map.ViewportForTesting.Top}");
            ok &= Check("and the picture moves under it",
                        map.RowAtForTesting(map.SlotCountForTesting / 2) != behindBefore,
                        $"row {behindBefore} -> {map.RowAtForTesting(map.SlotCountForTesting / 2)}");
            ok &= Check("while a scroll of less than a pixel leaves it alone",
                        StillAfterTinyScroll(grid, map), "the window moved for a sub-pixel scroll");
            grid.ScrollToRow(30_000);
            map.RebuildForTesting();
            int at = map.SlotOfForTesting(30_000), of = map.SlotCountForTesting;
            ok &= Check("a jump lands centred too", at > of / 5 && at < of * 4 / 5,
                        $"top {map.TopRowForTesting}, view at slot {at} of {of}");
            ok &= Check("and the rectangle never collapses to nothing", map.ViewportForTesting.Height >= 8,
                        map.ViewportForTesting.Height.ToString());

            // ---- the end of the file ----
            // The window has to stop against the end rather than running past it, or the map empties out
            // just as you reach the end of what you are reading.
            grid.ScrollToRow(0);
            map.RebuildForTesting();
            int full = map.SlotCountForTesting;
            grid.ScrollToRow(lines);
            map.RebuildForTesting();
            ok &= Check("at the end of the file the map is still full", map.SlotCountForTesting == full,
                        $"{map.SlotCountForTesting} of {full} pixels");
            var lastRows = map.RowsAtForTesting(map.SlotCountForTesting - 1);
            ok &= Check("and its last pixel holds the last row", lastRows.To == lines,
                        $"rows {lastRows.From}-{lastRows.To - 1} of {lines}");
            var end = map.ViewportForTesting;
            ok &= Check("so the rectangle is at the bottom", end.Top + end.Height >= map.Height - map.RowPixelsForTesting * 2,
                        $"{end.Top}+{end.Height} of {map.Height}");

            // ---- the caret is never compressed away, and never disturbs the map either ----
            // It is drawn from the rows a pixel stands for rather than being given a pixel of its own:
            // splitting a compressed stretch at the caret re-lays out everything below it on every arrow
            // key, which reads as the whole map shivering.
            grid.ScrollToRow(20_000);
            grid.SelectRowForAccessibility(20_000);
            grid.RefreshView();
            Pump();
            map.RebuildForTesting();
            int caretSlot = map.SlotOfForTesting(20_000);
            var (caretFrom, caretTo) = map.RowsAtForTesting(caretSlot);
            ok &= Check("a caret on a line nothing matched is still somewhere on the map",
                        caretFrom <= 20_000 && caretTo > 20_000,
                        $"slot {caretSlot} stands for rows {caretFrom}..{caretTo}");
            ok &= Check("and the stretch it is in is still compressed", caretTo - caretFrom > 1,
                        $"{caretTo - caretFrom} rows on one pixel");

            // Walking the caret down the view must not move a single pixel of the map.
            long[] before20 = map.RowsForTesting();
            long stillAt = grid.FirstVisibleRow;
            int walk = Math.Max(2, grid.VisibleRows - 1);
            for (int i = 1; i <= walk; i++)
            {
                grid.SelectRowForAccessibility(20_000 + i);
                grid.RefreshView();
                map.RebuildForTesting();
            }
            long[] after20 = map.RowsForTesting();
            int moved = before20.Length == after20.Length
                ? Enumerable.Range(0, before20.Length).Count(i => before20[i] != after20[i])
                : Math.Max(before20.Length, after20.Length);
            ok &= Check("the caret walked without scrolling the view, so the map had no reason to move",
                        grid.FirstVisibleRow == stillAt, $"{stillAt} -> {grid.FirstVisibleRow}");
            ok &= Check("walking the caret through it leaves the map exactly where it was", moved == 0,
                        $"{moved} of {before20.Length} pixels moved over {walk} steps");
            int endSlot = map.SlotOfForTesting(20_000 + walk);
            var endRows = map.RowsAtForTesting(endSlot);
            ok &= Check("and the caret is still on the map at the end of the walk",
                        endRows.From <= 20_000 + walk && endRows.To > 20_000 + walk,
                        $"slot {endSlot} stands for rows {endRows.From}..{endRows.To}, caret at {20_000 + walk}");

            grid.SelectRowForAccessibility(0);
            grid.RefreshView();
            Pump();

            // ---- the window is dragged, not flung ----
            grid.ScrollToRow(15_000);
            map.RebuildForTesting();
            var held = map.ViewportForTesting;
            int grabAt = held.Top + held.Height - 1;      // by its bottom edge, the worst case for a snap
            map.GrabForTesting(grabAt);
            ok &= Check("taking hold of the window does not move it",
                        map.ViewportForTesting.Top == held.Top, $"{held.Top} -> {map.ViewportForTesting.Top}");
            map.DragToForTesting(grabAt + map.RowPixelsForTesting * 20);
            var dragged = map.ViewportForTesting;
            ok &= Check("and it follows the pointer from where it was taken hold of",
                        dragged.Top > held.Top && Math.Abs(dragged.Top - held.Top - map.RowPixelsForTesting * 20) <= 4,
                        $"{held.Top} -> {dragged.Top}, asked for +{map.RowPixelsForTesting * 20}");

            // Dropping it and moving away must leave it where it was dropped. Re-centring there would be a
            // rubber band: the map would snap back the moment the pointer left it.
            long droppedAt = map.TopRowForTesting;
            map.DropForTesting();
            map.LeaveForTesting();
            map.RebuildForTesting();
            ok &= Check("letting go and moving off leaves the window where it was dropped",
                        map.TopRowForTesting == droppedAt, $"{droppedAt} -> {map.TopRowForTesting}");
            ok &= Check("and the rectangle stays where it was dropped too",
                        Math.Abs(map.ViewportForTesting.Top - dragged.Top) <= 2,
                        $"{dragged.Top} -> {map.ViewportForTesting.Top}");

            // ...but a scroll from anywhere else still re-centres it.
            grid.ScrollToRow(15_500);
            map.RebuildForTesting();
            int reAt = map.SlotOfForTesting(15_500), reOf = map.SlotCountForTesting;
            ok &= Check("while scrolling from outside re-centres it again",
                        reAt > reOf / 5 && reAt < reOf * 4 / 5, $"view at slot {reAt} of {reOf}");

            // ---- the window cannot be dragged off the map ----
            map.GrabForTesting(map.ViewportForTesting.Top);
            map.DragToForTesting(-500);
            ok &= Check("dragging above the map stops at its top edge", map.ViewportForTesting.Top == 0,
                        map.ViewportForTesting.Top.ToString());
            ok &= Check("and the view stops at the first row the map shows",
                        grid.FirstVisibleRow == map.RowAtForTesting(0),
                        $"view {grid.FirstVisibleRow}, map starts {map.RowAtForTesting(0)}");
            map.DragToForTesting(map.Height + 500);
            var low = map.ViewportForTesting;
            ok &= Check("dragging below it stops at the bottom edge", low.Top + low.Height <= map.Height,
                        $"{low.Top}+{low.Height} of {map.Height}");
            ok &= Check("and the view stops at the last row the map shows",
                        grid.FirstVisibleRow + grid.VisibleRows - 1 <= map.RowAtForTesting(map.SlotCountForTesting - 1),
                        $"view ends {grid.FirstVisibleRow + grid.VisibleRows - 1}, " +
                        $"map ends {map.RowAtForTesting(map.SlotCountForTesting - 1)}");
            map.DropForTesting();
            map.LeaveForTesting();

            // ---- painted, and repainted when it must be ----
            grid.ScrollToRow(0);
            Pump();
            using (var picture = Capture(host))
            {
                var r = grid.MapBoundsForTesting;
                // Well below the viewport rectangle, whose tint would be blended into whatever is under it.
                int y = r.Top + 100 * map.RowPixelsForTesting;
                var pixel = picture.GetPixel(r.Left + r.Width / 2, y);
                ok &= Check("the map is painted in the row's own colour",
                            pixel.R == 0x22 && pixel.G == 0x44 && pixel.B == 0xEE, pixel.ToString());
            }

            int paintsBefore = map.PaintsForTesting;
            grid.ScrollToRow(200);
            Pump();
            ok &= Check("scrolling repaints the map without anyone asking it to",
                        map.PaintsForTesting > paintsBefore, $"{paintsBefore} -> {map.PaintsForTesting} paints");

            paintsBefore = map.PaintsForTesting;
            doc.Filters.ShowOnlyFilteredLines = true;
            grid.RefreshView();
            Pump();
            ok &= Check("switching to filtered lines repaints it too",
                        map.PaintsForTesting > paintsBefore, $"{paintsBefore} -> {map.PaintsForTesting} paints");
            doc.Filters.ShowOnlyFilteredLines = false;
            grid.RefreshView();
            Pump();

            var watch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) { map.Invalidate(); map.Update(); }
            watch.Stop();
            ok &= Check("and a repaint is a blit, not a rebuild", watch.ElapsedMilliseconds < 200,
                        $"{watch.ElapsedMilliseconds} ms for 100 repaints");

            // Scrubbing the scrollbar re-centres the window on every mouse move, so a rebuild has to be
            // cheap enough to keep up with a hand - the whole point of the live update.
            watch.Restart();
            for (int i = 0; i < 60; i++) { grid.ScrollToRow(1_000 + i * 40); map.RebuildForTesting(); }
            watch.Stop();
            ok &= Check("and a rebuild keeps up with a dragging hand", watch.ElapsedMilliseconds < 400,
                        $"{watch.ElapsedMilliseconds} ms for 60 rebuilds");

            // ---- clicking it ----
            grid.ScrollToRow(0);
            map.RebuildForTesting();
            int target = map.SlotCountForTesting / 2;
            long wanted = map.RowAtForTesting(target);
            map.ClickForTesting(target * map.RowPixelsForTesting);
            Pump();
            // Within the rectangle's own height: it has a minimum, so on a short view it stands for more of
            // the map than the view really covers, and that is what the click is centred on.
            long slack = (long)Math.Max(2, map.ViewportForTesting.Height / Math.Max(1, map.RowPixelsForTesting))
                         * map.RowsPerPixelForTesting;
            ok &= Check("clicking a pixel goes to the row behind it",
                        Math.Abs(grid.FirstVisibleRow + grid.VisibleRows / 2 - wanted) <= slack,
                        $"wanted {wanted}, got {grid.FirstVisibleRow + grid.VisibleRows / 2}, " +
                        $"{map.RowsPerPixelForTesting} rows a pixel, {slack} rows of slack");

            // ---- markers and the selection ----
            ok &= RunMapMarkChecks(doc, grid, map, host);

            // ---- the tip ----
            grid.ScrollToRow(0);
            map.RebuildForTesting();
            string tip = map.TipTextForTesting(5);
            var tipRows = map.RowsAtForTesting(5);
            ok &= Check("hovering a pixel names the lines it stands for and their filter",
                        tip.Contains($"Line {tipRows.From + 1:N0}\u2013{tipRows.To:N0}") && tip.Contains("COMMON"),
                        tip.Replace("\n", " | "));
            grid.ScrollToRow(20_000);
            map.RebuildForTesting();
            string blank = map.TipTextForTesting(2);
            ok &= Check("and a compressed stretch says there is nothing in it", blank.Contains("nothing matching"),
                        blank.Replace("\n", " | "));

            // ---- a file the map can hold is shown whole ----
            ok &= RunMapWholeFileChecks();

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

    /// <summary>Scrolling by fewer rows than one pixel of the map stands for must not move the window: the
    /// pixels sit on a fixed grid of the file, and re-dividing the rows between them for every line scrolled
    /// is what makes a map crawl about while you read.</summary>
    private static bool StillAfterTinyScroll(LineGridControl grid, MiniMapControl map)
    {
        long was = map.TopRowForTesting;
        long from = grid.FirstVisibleRow;
        grid.ScrollToRow(from + Math.Max(1, map.RowsPerPixelForTesting / 4));
        map.RebuildForTesting();
        bool still = map.TopRowForTesting == was;
        grid.ScrollToRow(from);
        map.RebuildForTesting();
        return still;
    }

    /// <summary>A file small enough for the map to hold is shown whole: compressed only as much as it takes
    /// to fit, anchored at the top, and never a window - so the map and the scrollbar agree about where you
    /// are, and scrolling the log never moves the map at all.</summary>
    private static bool RunMapWholeFileChecks()    {
        const int lines = 3_000;
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_mapfit_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++) sb.Append("HIT line ").Append(i).Append('\n');
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

            var collection = new FilterCollection();
            collection.Roots.Add(new Filter { Enabled = true, Match = new FilterMatch { Text = "HIT" }, Style = { Background = new RgbColor(0x22, 0x44, 0xEE) } });
            doc.SetFilters(collection);
            WaitForFiltering(doc);
            Pump();

            var map = grid.MatchMapForTesting;
            if (map is null) return Check("the map is there to check", false);
            grid.ScrollToRow(0);
            map.RebuildForTesting();
            int step = map.RowsPerPixelForTesting, slots = map.SlotCountForTesting;
            bool ok = Check("a file the map can hold is compressed, but not to the cap",
                            step > 1 && step < 32, $"{step} rows a pixel across {slots} pixels");
            ok &= Check("and all of it is on the map", (long)step * slots >= lines && map.SpanForTesting >= lines,
                        $"{map.SpanForTesting} rows of {lines} across {slots} pixels");
            ok &= Check("so it starts at the first row", map.TopRowForTesting == 0, map.TopRowForTesting.ToString());

            // Nothing left to scroll to: the whole file is already drawn, so the picture must not move.
            long[] atTop = map.RowsForTesting();
            grid.ScrollToRow(lines);
            map.RebuildForTesting();
            ok &= Check("and scrolling to the end leaves it exactly where it was",
                        map.TopRowForTesting == 0 && map.RowsForTesting().SequenceEqual(atTop),
                        $"top {map.TopRowForTesting}, {map.RowsForTesting().Zip(atTop).Count(p => p.First != p.Second)} pixels moved");
            var end = map.ViewportForTesting;
            // Against how much of the map the file actually fills, not the control's height: a file that
            // fits leaves whatever does not divide evenly blank at the bottom.
            int drawn = map.SlotCountForTesting * map.RowPixelsForTesting;
            ok &= Check("only the rectangle moves, to the bottom",
                        end.Top + end.Height >= drawn - map.RowPixelsForTesting * 2,
                        $"{end.Top}+{end.Height} of {drawn} drawn ({map.Height} tall)");
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

    /// <summary>Marks down the map's left edge, and every marked line in the file down the scrollbar's
    /// trough - which is the only place a mark outside the map's window can appear.</summary>
    private static bool RunMapMarkChecks(CascadeDocument doc, LineGridControl grid, MiniMapControl map, Form host)
    {        grid.ScrollToRow(0);
        grid.RefreshView();
        Pump();

        int MarkPixels(Color want) => CountColour(host, grid.MapBoundsForTesting, want, leftEdgeOnly: true);
        int TroughPixels(Color want) => CountColour(host, grid.ScrollBarBoundsForTesting, want, leftEdgeOnly: false);

        var markColour = AppSettings.MarkerColors[0];
        int mapBefore = MarkPixels(markColour), troughBefore = TroughPixels(markColour);

        // A mark well outside the map's window: it can only show on the scrollbar.
        grid.ScrollToRow(30_000);
        grid.SelectRowForAccessibility(30_000);
        Pump();
        grid.PressKeyForTesting(Keys.Control | Keys.D1);
        grid.ScrollToRow(0);
        grid.RefreshView();
        Pump();

        bool ok = Check("a mark outside the map's window still shows on the scrollbar",
                        TroughPixels(markColour) > troughBefore,
                        $"{troughBefore} -> {TroughPixels(markColour)} pixels");
        ok &= Check("and not on the map, which is not looking there",
                    MarkPixels(markColour) == mapBefore, $"{mapBefore} -> {MarkPixels(markColour)}");

        // Bring it into the window and it shows on both. Not right under the viewport rectangle, whose tint
        // is drawn over the mark - still visible, but no longer exactly the marker's colour.
        grid.ScrollToRow(28_000);
        grid.RefreshView();
        Pump();
        ok &= Check("scroll near it and the map shows it too", MarkPixels(markColour) > 0,
                    MarkPixels(markColour).ToString());

        grid.PressKeyForTesting(Keys.Control | Keys.D1);
        grid.RefreshView();
        Pump();
        ok &= Check("clearing it takes it off both", MarkPixels(markColour) == 0 && TroughPixels(markColour) == troughBefore,
                    $"map {MarkPixels(markColour)}, trough {TroughPixels(markColour)}");

        // The selection gets the same edge, in the selection colour - well below the viewport rectangle,
        // which is drawn in the same colour and would otherwise be what the count found.
        grid.ScrollToRow(0);
        grid.RefreshView();
        Pump();
        int selBefore = MarkPixels(new AppSettings().SelectionBack);
        grid.SelectRowForAccessibility(150);
        grid.ScrollToRow(0);
        grid.RefreshView();
        Pump();
        ok &= Check("a selected row is marked on the map", MarkPixels(new AppSettings().SelectionBack) > selBefore,
                    $"{selBefore} -> {MarkPixels(new AppSettings().SelectionBack)}");
        return ok;
    }

    /// <summary>Pixels of exactly a colour inside a control's own rectangle - taken from the control rather
    /// than worked out from docking order, which is exactly the kind of guess that makes a test lie. Exact,
    /// because the marks are drawn solid and the viewport rectangle over them is not: a tolerance would
    /// count its tint as a mark.</summary>
    private static int CountColour(Form host, Rectangle r, Color want, bool leftEdgeOnly)
    {
        if (r.Width <= 0 || r.Height <= 0) return 0;
        using var picture = Capture(host);
        int right = leftEdgeOnly ? Math.Min(r.Left + 4, r.Right) : r.Right;
        int n = 0;
        for (int y = r.Top; y < r.Bottom && y < picture.Height; y++)
            for (int x = r.Left; x < right && x < picture.Width; x++)
                if (picture.GetPixel(x, y).ToArgb() == want.ToArgb()) n++;
        return n;
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

            // ...and coming back to the row it started on picks the characters up again. Leaving used to
            // throw the starting point away, so a drag that wandered off by a pixel could never get back.
            grid.PressForTesting(2, xOfChar(reqAt));
            grid.DragOverRowForTesting(4, xOfChar(reqAt + 10));
            ok &= Check("a drag that has wandered off is selecting whole lines",
                        !grid.HasCharSelection && grid.CaretRowForTesting == 4,
                        $"{grid.SelectedText ?? "(none)"}, caret {grid.CaretRowForTesting}");
            grid.DragOverRowForTesting(2, xOfChar(reqAt + 10));
            ok &= Check("and coming back to where it started selects characters again",
                        grid.SelectedText == "req-abc123",
                        $"{grid.SelectedText ?? "(none)"} [origin {grid.CharOriginForTesting}, caret {grid.CaretRowForTesting}]");
            grid.DragOverRowForTesting(2, xOfChar(reqAt + 3));
            ok &= Check("from the same starting point it set out from", grid.SelectedText == "req",
                        grid.SelectedText ?? "(none)");
            grid.ReleaseForTesting(2, xOfChar(reqAt + 3));
            ok &= Check("and letting go there keeps it", grid.SelectedText == "req", grid.SelectedText ?? "(none)");

            // Double-click asks for a filter for this line rather than selecting the word under the pointer:
            // this view is for reading a log, and turning a line into a filter is what deserves a gesture
            // that short.
            var asked = new List<string?>();
            grid.NewFilterRequested += part => asked.Add(part);
            grid.ClickForTesting(4, 5);   // somewhere else first, so nothing is picked out to carry
            grid.DoubleClickForTesting(2, xOfChar(reqAt + 3));
            ok &= Check("double-click asks for a filter", asked.Count == 1, asked.Count.ToString());
            ok &= Check("for the whole line, nothing having been picked out", asked.Count == 1 && asked[0] is null,
                        asked.Count == 1 ? asked[0] ?? "(the whole line)" : "(nothing asked)");

            // ...and with part of the line picked out, for that part - the click that starts the double-click
            // clears the selection, so it has to be carried across.
            grid.DragForTesting(2, xOfChar(reqAt), xOfChar(reqAt + 10));
            grid.DoubleClickForTesting(2, xOfChar(reqAt + 3));
            ok &= Check("and for the part picked out when there is one",
                        asked.Count == 2 && asked[1] == "req-abc123",
                        asked.Count == 2 ? asked[1] ?? "(the whole line)" : "(nothing asked)");

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

    /// <summary>What is picked out belongs to the TEXT, not to the place on screen it happened to be when
    /// it was picked. Turning a filter on drops lines out from above the selection, so every row below moves
    /// - and a highlight remembered by row would be left over whatever slid into its place.</summary>
    private static bool RunSelectionFollowsTextChecks()
    {
        Line("-- selection follows the text --");
        const int Lines = 60, Noise = 5, Pad = 7;
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_selfollow_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        // One line in five is dropped by the filter below, so everything under it shifts up. The rest are
        // padded by differing amounts, so a highlight left on a row lands on the wrong CHARACTERS as well
        // as on the wrong line - which is what the reader sees.
        for (int i = 0; i < Lines; i++)
            sb.Append(i % Noise == 0 ? "cache miss, nothing here" : new string('.', i % Pad) + "TARGET request handled okay")
              .Append(" #").Append(i.ToString("00")).Append('\n');
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
                ClientSize = new Size(900, 560),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(grid);
            grid.Attach(doc, settings);
            host.Show();
            Pump();

            // "Show only matching lines" from the start with nothing enabled, which is what a reader has
            // after opening a file: every line is shown, and the first filter is what moves them.
            void Filter(string? text)
            {
                var filters = new FilterCollection { ShowOnlyFilteredLines = true };
                if (text is not null)
                    filters.Roots.Add(new Filter { Enabled = true, Match = new FilterMatch { Text = text } });
                // Exactly what MainForm.OnFiltersChanged does, in the same order.
                var anchor = grid.CaptureViewAnchor();
                doc.SetFilters(filters);
                grid.SetViewAnchor(anchor);
                grid.RefreshView();
                WaitForFiltering(doc);
                grid.RefreshView();
            }

            Filter(null);
            bool ok = Check("every line is shown to begin with", doc.RowCount == Lines, $"{doc.RowCount} rows");

            // ---- part of a line ----
            const long Picked = 17;                       // a TARGET line, padded by three
            string text = doc.GetLineText(Picked);
            int at = text.IndexOf("request", StringComparison.Ordinal);
            long startRow = doc.RowForLine(Picked);
            grid.DragForTesting(startRow, grid.XForCharForTesting(startRow, at),
                                          grid.XForCharForTesting(startRow, at + 7));
            ok &= Check("part of a line is picked out", grid.SelectedText == "request", grid.SelectedText ?? "(none)");

            Filter("TARGET");
            long moved = doc.RowForLine(Picked);
            ok &= Check("the filter really did move the line", moved >= 0 && moved != startRow,
                        $"row {startRow} -> {moved} of {doc.RowCount}");
            ok &= Check("the same text is still picked out", grid.SelectedText == "request",
                        grid.SelectedText ?? "(none)");
            ok &= Check("and it is still on the line it was picked from",
                        grid.CharSelectionLineForTesting == Picked, $"line {grid.CharSelectionLineForTesting}");

            // The claim in pixels, which is what the reader is actually complaining about: exactly one row
            // is drawn picked out, it is the row now showing that line, and it is a PART of the row - a
            // highlight left behind shows up as a second one, and the line it belonged to as a whole row.
            using (var picture = Capture(host))
            {
                var runs = SelectionRuns(grid, host, picture, settings);
                ok &= Check("only one row on screen is picked out", runs.Count == 1,
                            string.Join(", ", runs.Select(r => $"row {r.Row} ({r.Pixels}px)")));
                ok &= Check("and it is the row that line is on now",
                            runs.Count == 1 && runs[0].Row == moved,
                            runs.Count == 1 ? $"row {runs[0].Row}, wanted {moved}" : "(nothing picked out)");
                ok &= Check("part of the row, not the whole of it",
                            runs.Count == 1 && runs[0].Pixels < grid.ContentWidthForTesting / 2,
                            runs.Count == 1 ? $"{runs[0].Pixels}px of {grid.ContentWidthForTesting}" : "(nothing picked out)");
            }

            // ---- several whole lines ----
            Filter(null);
            long fromLine = 12, toLine = 22;
            grid.PressForTesting(doc.RowForLine(fromLine), 5);
            grid.DragOverRowForTesting(doc.RowForLine(toLine), 5);
            grid.ReleaseForTesting(doc.RowForLine(toLine), 5);
            ok &= Check("a run of whole lines is selected", grid.SelectedCount == toLine - fromLine + 1,
                        $"{grid.SelectedCount} lines");

            Filter("TARGET");
            var wanted = new List<long>();
            for (long line = fromLine; line <= toLine; line++) if (line % Noise != 0) wanted.Add(line);
            var still = new List<long>();
            for (long line = 0; line < Lines; line++) if (grid.IsLineSelectedForTesting(line)) still.Add(line);
            ok &= Check("the same lines are selected after the filter",
                        still.SequenceEqual(Enumerable.Range((int)fromLine, (int)(toLine - fromLine + 1)).Select(i => (long)i)),
                        string.Join(",", still));
            ok &= Check("and the count is of the ones being shown", grid.SelectedCount == wanted.Count,
                        $"{grid.SelectedCount}, wanted {wanted.Count}");
            using (var picture = Capture(host))
            {
                var drawn = SelectionRuns(grid, host, picture, settings).Select(r => doc.RowToLine(r.Row)).ToList();
                ok &= Check("exactly those lines are drawn selected", drawn.SequenceEqual(wanted),
                            $"drew {string.Join(",", drawn)}, wanted {string.Join(",", wanted)}");
            }

            // Anything that acts on the selection acts on what is being shown, not on the whole stretch:
            // a marker put on it must not land on lines the filter is hiding.
            grid.PressKeyForTesting(Keys.D1 | Keys.Control);
            var marked = new List<long>();
            for (long line = 0; line < Lines; line++) if (doc.Markers.MaskOf(line) != 0) marked.Add(line);
            ok &= Check("marking the selection marks the lines being shown", marked.SequenceEqual(wanted),
                        $"marked {string.Join(",", marked)}, wanted {string.Join(",", wanted)}");
            grid.PressKeyForTesting(Keys.D1 | Keys.Control);

            // Taking the filter away brings the hidden ones back, still selected - the selection was never
            // narrowed, only some of it was out of sight.
            Filter(null);
            ok &= Check("the hidden lines come back selected", grid.SelectedCount == toLine - fromLine + 1,
                        $"{grid.SelectedCount} lines");

            ok &= RunStandInChecks(grid, doc, Lines, Filter);
            ok &= SelectionStress(grid, doc, Lines, Filter);
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

    /// <summary>Hiding every selected line puts a stand-in in its place so the reader keeps their place, and
    /// showing the lines again brings the original selection back - the whole of it, not the stand-in. What
    /// was chosen is never written over, which is what makes undoing the filter enough to restore it.</summary>
    private static bool RunStandInChecks(LineGridControl grid, CascadeDocument doc, int lines,
                                         Action<string?> filter)
    {
        // ---- one line ----
        filter(null);
        const long Lone = 25;                      // every fifth line says "cache miss", so "TARGET" hides it
        grid.ClickForTesting(doc.RowForLine(Lone), 5);
        bool ok = Check("one line is chosen", grid.IsLineSelectedForTesting(Lone) && grid.SelectedCount == 1,
                        $"{grid.SelectedCount} shown");

        filter("TARGET");
        long stand = grid.StandInLineForTesting;
        ok &= Check("hiding it leaves the choice alone", grid.IsLineSelectedForTesting(Lone));
        ok &= Check("and stands another line in for it", stand >= 0 && stand != Lone && doc.IsLineVisible(stand),
                    $"stand-in {stand}");
        ok &= Check("which is drawn selected", stand >= 0 && grid.IsLineShownSelectedForTesting(stand));
        ok &= Check("and counts as the one selected line", grid.SelectedCount == 1, $"{grid.SelectedCount}");

        filter(null);
        ok &= Check("showing it again takes the stand-in away", grid.StandInLineForTesting < 0,
                    $"stand-in {grid.StandInLineForTesting}");
        ok &= Check("and puts the selection back where it was",
                    grid.IsLineSelectedForTesting(Lone) && grid.IsLineShownSelectedForTesting(Lone)
                    && grid.SelectedCount == 1, $"{grid.SelectedCount} selected");

        // ---- several lines, all hidden ----
        long from = 11, to = 15;                   // 15 says "cache miss", the rest carry TARGET
        grid.PressForTesting(doc.RowForLine(from), 5);
        grid.DragOverRowForTesting(doc.RowForLine(to), 5);
        grid.ReleaseForTesting(doc.RowForLine(to), 5);
        ok &= Check("a stretch of lines is chosen", grid.SelectedCount == to - from + 1, $"{grid.SelectedCount}");

        filter("nothing matches this at all");
        ok &= Check("with none of them on show, one line stands in", grid.SelectedCount <= 1,
                    $"{grid.SelectedCount} selected");
        for (long line = from; line <= to; line++)
            ok &= Check($"line {line} is still chosen underneath", grid.IsLineSelectedForTesting(line));

        filter(null);
        ok &= Check("and the whole stretch comes back", grid.SelectedCount == to - from + 1,
                    $"{grid.SelectedCount}");
        ok &= Check("with no stand-in left over", grid.StandInLineForTesting < 0);

        // ---- several lines, some hidden: no stand-in, the rest stay put ----
        filter("TARGET");
        long visible = 0;
        for (long line = from; line <= to; line++) if (doc.IsLineVisible(line)) visible++;
        ok &= Check("some of the stretch is still on show", visible > 0, $"{visible} of {to - from + 1}");
        ok &= Check("so nothing stands in", grid.StandInLineForTesting < 0, $"{grid.StandInLineForTesting}");
        ok &= Check("and the count is of the ones on show", grid.SelectedCount == visible,
                    $"said {grid.SelectedCount}, {visible} on show");
        filter(null);
        return ok;
    }

    /// <summary>Random gestures, each followed by a filter change, checked against a reference worked out
    /// from the document rather than from the view: whatever was selected must still be selected, and the
    /// number reported must be however many of those lines the view is showing.</summary>
    private static bool SelectionStress(LineGridControl grid, CascadeDocument doc, int lines,
                                        Action<string?> filter)
    {
        var terms = new string?[] { null, "TARGET", "handled okay", "#1", "cache" };
        var rnd = new Random(20260813);
        int gestures = 0;
        for (int iter = 0; iter < 60; iter++)
        {
            long rows = doc.RowCount;
            if (rows > 2)
            {
                long a = rnd.Next((int)rows), b = rnd.Next((int)rows);
                switch (rnd.Next(4))
                {
                    case 0: grid.ClickForTesting(a, 5); break;
                    case 1:
                        grid.PressForTesting(a, 5);
                        grid.DragOverRowForTesting(b, 5);
                        grid.ReleaseForTesting(b, 5);
                        break;
                    case 2: grid.SelectAll(); break;
                    default:
                        grid.DragForTesting(a, grid.XForCharForTesting(a, 1), grid.XForCharForTesting(a, 6));
                        break;
                }
                gestures++;
            }

            var before = new bool[lines];
            for (long line = 0; line < lines; line++) before[line] = grid.IsLineSelectedForTesting(line);
            string? pickedOut = grid.SelectedText;

            filter(terms[rnd.Next(terms.Length)]);

            // Nothing a filter does narrows or moves what was chosen. It is held in lines, so hiding one is
            // a question of what gets drawn - and putting it back shows it again, still selected.
            for (long line = 0; line < lines; line++)
                if (grid.IsLineSelectedForTesting(line) != before[line])
                    return Check($"the same lines stay selected (iteration {iter})", false,
                                 $"line {line}: {before[line]} -> {grid.IsLineSelectedForTesting(line)}");

            long shown = 0;
            for (long line = 0; line < lines; line++) if (before[line] && doc.IsLineVisible(line)) shown++;
            long standIn = grid.StandInLineForTesting;
            bool anyChosen = Array.IndexOf(before, true) >= 0;

            // With every chosen line hidden, one line stands in for them so the reader keeps their place -
            // and it has to be a line actually on show.
            if (anyChosen && shown == 0 && standIn < 0)
                return Check($"a wholly hidden selection leaves a stand-in (iteration {iter})", false,
                             "nothing is highlighted at all");
            if (standIn >= 0 && !doc.IsLineVisible(standIn))
                return Check($"the stand-in is a line being shown (iteration {iter})", false,
                             $"line {standIn} is not on show");
            if (shown > 0 && standIn >= 0)
                return Check($"no stand-in while any chosen line is on show (iteration {iter})", false,
                             $"{shown} shown, yet line {standIn} stands in");

            long counted = shown > 0 ? shown : (standIn >= 0 ? 1 : 0);
            if (grid.SelectedCount != counted)
                return Check($"the count is of the selected lines being shown (iteration {iter})", false,
                             $"said {grid.SelectedCount}, wanted {counted}");

            if (pickedOut is not null && grid.SelectedText != pickedOut)
                return Check($"the part of a line stays picked out (iteration {iter})", false,
                             $"'{pickedOut}' -> '{grid.SelectedText ?? "(none)"}'");
        }
        return Check($"{gestures} random selections all survive a filter change", true);
    }

    /// <summary>Which rows have anything drawn in the selection colour, and how much of each. Read off the
    /// picture rather than asked of the control: where the highlight LANDS is the whole complaint.</summary>
    private static List<(long Row, int Pixels)> SelectionRuns(LineGridControl grid, Form host, Bitmap picture,
                                                             AppSettings settings)
    {
        var found = new List<(long Row, int Pixels)>();
        int left = grid.GutterWidthForTesting + 2;
        int right = host.ClientSize.Width - grid.MapWidthForTesting - grid.ScrollBarWidthForTesting - 2;
        for (int i = 0; i < grid.RowsPaintedForTesting; i++)
        {
            long row = grid.FirstRowForTesting + i;
            int y = grid.RowMiddleForTesting(row), pixels = 0;
            for (int x = left; x < right; x += 2)
                if (IsBackground(picture, x, y, settings.SelectionBack)) pixels += 2;
            if (pixels > 0) found.Add((row, pixels));
        }
        return found;
    }

    /// <summary>Anything that throws away the filter file on screen has to ask first: closing them, closing
    /// the window, and loading another set over the top - which is the same loss on one menu click, with no
    /// undo behind it.</summary>
    private static bool RunCloseFiltersChecks()
    {
        Line("-- letting go of the filters --");

        // The rule itself, read without a modal prompt standing in the way.
        bool ok = Check("unsaved changes to a filter file are worth asking about",
                        MainForm.ShouldOfferToSaveFilters(false, dirty: true, "x.cascade"));
        ok &= Check("nothing to ask when nothing has changed",
                    !MainForm.ShouldOfferToSaveFilters(false, dirty: false, "x.cascade"));
        ok &= Check("nor when there is no file to save to",
                    !MainForm.ShouldOfferToSaveFilters(false, dirty: true, null));
        ok &= Check("and a headless run is never asked", !MainForm.ShouldOfferToSaveFilters(true, true, "x.cascade"));

        string log = Path.Combine(Path.GetTempPath(), "cascade_st_close_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(log, Enumerable.Range(1, 200).Select(i => i % 5 == 0 ? $"ERROR line {i}" : $"plain line {i}"));
        string filters = Path.Combine(Path.GetTempPath(), "cascade_st_close_" + Guid.NewGuid().ToString("N") + ".cascade");
        const string OriginalFile = """
            { "filters": [ { "id": "f1", "enabled": true, "matchType": "Text", "text": "ERROR" } ] }
            """;

        MainForm? form = null;
        try
        {
            File.WriteAllText(filters, OriginalFile, new UTF8Encoding(false));
            form = new MainForm(new AppSettings(), new MachineState(), [log, "/Filters:" + filters])
            {
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(900, 600),
            };
            var answer = DialogResult.Cancel;
            form.AnswerSavePromptForTesting = () => answer;
            form.Show();
            Pump();
            var doc = form.DocForTesting;
            for (int i = 0; i < 60 && doc.CompletedLineCount < 200; i++) { Thread.Sleep(20); Pump(); }

            int Filters() => doc.Filters.EnumerateDepthFirst().Count();
            ok &= Check($"the filter file is loaded ({Filters()} filters)", Filters() == 1 && form.FilterFileForTesting == filters);

            // Nothing unsaved: closing them asks nothing and simply does it.
            ok &= Check("with nothing changed there is nothing to ask about", !form.FiltersAreDirtyForTesting);
            ok &= Check("so closing the filters just closes them",
                        form.ClickMenuForTesting("File", "Close Filters") && Filters() == 0 &&
                        form.FilterFileForTesting is null);

            // Now with something unsaved. Load it again and change it.
            form.LoadFiltersForTesting(filters);
            Pump();
            ok &= Check("the file can be loaded again", Filters() == 1 && form.FilterFileForTesting == filters,
                        $"{Filters()} filters, {form.FilterFileForTesting}");
            form.ClickMenuForTesting("Filters", "Disable All");
            Pump();
            ok &= Check("and turning a filter off is an unsaved change", form.FiltersAreDirtyForTesting);

            answer = DialogResult.Cancel;
            form.ClickMenuForTesting("File", "Close Filters");
            Pump();
            ok &= Check("answering \"cancel\" leaves the filters exactly where they were",
                        Filters() == 1 && form.FilterFileForTesting == filters && form.FiltersAreDirtyForTesting,
                        $"{Filters()} filters, {form.FilterFileForTesting}");

            answer = DialogResult.No;
            form.ClickMenuForTesting("File", "Close Filters");
            Pump();
            ok &= Check("answering \"no\" closes them", Filters() == 0 && form.FilterFileForTesting is null);
            ok &= Check("and leaves the file on disk as it was",
                        File.ReadAllText(filters).Contains("\"enabled\": true", StringComparison.Ordinal) ||
                        File.ReadAllText(filters) == OriginalFile,
                        File.ReadAllText(filters));

            // ...and "yes" writes it out before letting go of it.
            form.LoadFiltersForTesting(filters);
            Pump();
            form.ClickMenuForTesting("Filters", "Disable All");
            Pump();
            answer = DialogResult.Yes;
            form.ClickMenuForTesting("File", "Close Filters");
            Pump();
            ok &= Check("answering \"yes\" closes them too", Filters() == 0 && form.FilterFileForTesting is null);
            var saved = CascadeFile.Load(filters).Filters;
            ok &= Check("having written the change out first",
                        saved.Roots.Count == 1 && !saved.Roots[0].Enabled,
                        $"{saved.Roots.Count} filters, first enabled = {(saved.Roots.Count > 0 ? saved.Roots[0].Enabled : (bool?)null)}");

            // ---- loading another set over the top is the same loss, so it asks the same question ----

            File.WriteAllText(filters, OriginalFile, new UTF8Encoding(false));
            string other = Path.Combine(Path.GetTempPath(), "cascade_st_close_" + Guid.NewGuid().ToString("N") + ".cascade");
            File.WriteAllText(other, """
                { "filters": [ { "id": "f2", "enabled": true, "matchType": "Text", "text": "WARN" } ] }
                """, new UTF8Encoding(false));
            try
            {
                answer = DialogResult.No;
                form.LoadFiltersForTesting(filters);
                Pump();
                form.ClickMenuForTesting("Filters", "Disable All");
                Pump();
                string Pattern() => doc.Filters.Roots.Count > 0 ? doc.Filters.Roots[0].Match.Text : "(none)";
                ok &= Check($"a fresh unsaved change to start from ({Pattern()}, dirty {form.FiltersAreDirtyForTesting})",
                            form.FiltersAreDirtyForTesting && Pattern() == "ERROR");

                // The file being loaded is the one already open. Deliberately not a special case: it is
                // asked about like any other, saved if that is the answer, and then read back.
                answer = DialogResult.Cancel;
                form.LoadFiltersForTesting(filters);
                Pump();
                ok &= Check("re-opening the file already open asks, and cancelling leaves the change alone",
                            form.FiltersAreDirtyForTesting && !doc.Filters.Roots[0].Enabled,
                            $"dirty {form.FiltersAreDirtyForTesting}, enabled {doc.Filters.Roots[0].Enabled}");
                ok &= Check("and writes nothing",
                            CascadeFile.Load(filters).Filters.Roots[0].Enabled);

                answer = DialogResult.Yes;
                form.LoadFiltersForTesting(filters);
                Pump();
                ok &= Check("saying yes writes the change out and then reads the same file back",
                            !form.FiltersAreDirtyForTesting && !doc.Filters.Roots[0].Enabled
                            && !CascadeFile.Load(filters).Filters.Roots[0].Enabled,
                            $"dirty {form.FiltersAreDirtyForTesting}, on screen {doc.Filters.Roots[0].Enabled}, " +
                            $"on disk {CascadeFile.Load(filters).Filters.Roots[0].Enabled}");

                // ...and a DIFFERENT file goes down exactly the same path.
                form.ClickMenuForTesting("Filters", "Enable All");
                Pump();
                answer = DialogResult.Cancel;
                form.LoadFiltersForTesting(other);
                Pump();
                ok &= Check("cancelling stops a different file being opened too",
                            form.FilterFileForTesting == filters && Pattern() == "ERROR",
                            $"{form.FilterFileForTesting}, {Pattern()}");

                answer = DialogResult.No;
                form.LoadFiltersForTesting(other);
                Pump();
                ok &= Check($"and \"no\" opens it without saving what was on screen ({Pattern()})",
                            form.FilterFileForTesting == other && Pattern() == "WARN");
                ok &= Check("leaving the file it came from as it was on disk",
                            !CascadeFile.Load(filters).Filters.Roots[0].Enabled);

                // The report was about the Recent Filter Files menu, so drive that rather than the method
                // behind it - every way in has to reach the same guard.
                form.ClickMenuForTesting("Filters", "Enable All");
                Pump();
                answer = DialogResult.Cancel;
                ok &= Check("the recent filter files menu lists the file",
                            form.ClickMenuForTesting("File", "Recent Filter Files", filters));
                Pump();
                ok &= Check("and going back to one through it asks before throwing the change away",
                            form.FilterFileForTesting == other && form.FiltersAreDirtyForTesting,
                            $"{form.FilterFileForTesting}, dirty {form.FiltersAreDirtyForTesting}");
            }
            finally { try { File.Delete(other); } catch { /* ignore */ } }

            form.AnswerSavePromptForTesting = () => DialogResult.No;
            return ok;
        }
        finally
        {
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            try { File.Delete(log); } catch { /* ignore */ }
            try { File.Delete(filters); } catch { /* ignore */ }
        }
    }

    /// <summary>Underline is a style a filter can set, like bold and italic. What proves it is on screen is
    /// a long unbroken run of the filter's own colour across a scanline - glyphs never draw one, so the
    /// same measurement over a line coloured but NOT underlined is the control that makes it mean
    /// something.</summary>
    private static bool RunUnderlineChecks()
    {
        Line("-- underline --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_under_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(path, [
            "AAA this line has no filter of its own at all",
            "BBB this line is given a colour and nothing else",
            "CCC this line is given the same colour and a rule",
            "DDD this line is given the same colour, a rule and weight",
        ]);

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();
            var ink = new RgbColor(0xC0, 0x00, 0x00);
            doc.Filters.Add(new Filter { Enabled = true, Match = { Text = "BBB" }, Style = { Foreground = ink } });
            doc.Filters.Add(new Filter { Enabled = true, Match = { Text = "CCC" }, Style = { Foreground = ink, Underline = true } });
            doc.Filters.Add(new Filter { Enabled = true, Match = { Text = "DDD" }, Style = { Foreground = ink, Underline = true, Bold = true } });
            doc.ApplyFilters();
            for (int i = 0; i < 100 && doc.IsBusy; i++) { Thread.Sleep(10); Pump(); }

            var grid = new LineGridControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(700, 200),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(grid);
            grid.Attach(doc, new AppSettings());
            host.Show();
            grid.RefreshView();
            Pump();

            using var picture = Capture(host);
            int gutter = grid.GutterWidthForTesting;

            // The longest unbroken run of the filter's colour anywhere in the row.
            int LongestRun(long row)
            {
                int top = grid.RowTopForTesting(row), height = grid.RowHeightForTesting;
                int best = 0;
                for (int y = Math.Max(0, top); y < Math.Min(picture.Height, top + height); y++)
                {
                    int run = 0;
                    for (int x = gutter; x < picture.Width; x++)
                    {
                        var c = picture.GetPixel(x, y);
                        bool inky = Math.Abs(c.R - ink.R) < 60 && Math.Abs(c.G - ink.G) < 60 && Math.Abs(c.B - ink.B) < 60;
                        run = inky ? run + 1 : 0;
                        if (run > best) best = run;
                    }
                }
                return best;
            }

            int plain = LongestRun(0), coloured = LongestRun(1), under = LongestRun(2), both = LongestRun(3);
            Line($"   (longest run of the filter's colour: plain {plain}, coloured {coloured}, " +
                 $"underlined {under}, bold+underlined {both})");

            bool ok = Check($"an unstyled line has none of the filter's colour at all ({plain}px)", plain <= 2);
            ok &= Check($"a coloured line draws it only as glyphs ({coloured}px)", coloured < 40);
            ok &= Check($"an underlined one draws a rule right across its text ({under}px)",
                        under > coloured * 3 && under > 100);
            ok &= Check($"and underline combines with bold rather than replacing it ({both}px)",
                        both > coloured * 3 && both > 100);

            // The style has to be a REAL font attribute, or a check that only looks at pixels could be
            // satisfied by something drawn over the text.
            ok &= Check("and the row is drawn in an underlined face",
                        grid.FontForRowForTesting(2).Underline && !grid.FontForRowForTesting(1).Underline);
            ok &= Check("bold and underlined at once", grid.FontForRowForTesting(3) is { Bold: true, Underline: true });
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>Changing how a filter LOOKS must not send the filtering pipeline round again. The lines on
    /// screen are already the right ones, and both the log view and the map resolve a line's colour from the
    /// live filter every time they paint, so the whole gesture is a repaint. On a large file the difference
    /// is a window that answers straight away against one that stops answering for seconds.
    ///
    /// The half of this that earns its keep is the other one: an edit to what a filter MATCHES still has to
    /// go the whole way round, and a shortcut that swallowed those would be far worse than the cost it saves.</summary>
    private static bool RunRestyleChecks()
    {
        Line("-- restyling a filter --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_restyle_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        // Big enough that the map cannot hold the whole file even at its most compressed, so it is a WINDOW
        // over the file - which is the only state in which moving it is something a check can see.
        const int Lines = 60_000;
        for (int i = 0; i < Lines; i++)
            sb.Append(i % 5 == 0 ? $"ERROR line {i}\n" : i % 7 == 0 ? $"WARN line {i}\n" : $"plain line {i}\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        string filters = Path.Combine(Path.GetTempPath(), "cascade_st_restyle_" + Guid.NewGuid().ToString("N") + ".cascade");
        File.WriteAllText(filters, """
            { "filters": [ { "id": "f1", "enabled": true, "matchType": "Text", "text": "ERROR",
                             "style": { "background": "#FFD0D0" } } ] }
            """, new UTF8Encoding(false));

        MainForm? form = null;
        try
        {
            form = new MainForm(new AppSettings(), new MachineState(), new[] { path, "/Filters:" + filters })
            {
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(1000, 760),
            };
            form.NoSavePrompt = true;
            form.Show();
            Pump();
            var doc = form.DocForTesting;
            for (int i = 0; i < 200 && doc.CompletedLineCount < Lines; i++) { Thread.Sleep(20); Pump(); }
            for (int i = 0; i < 200 && doc.IsBusy; i++) { Thread.Sleep(20); Pump(); }

            var grid = form.GridForTesting;
            var map = grid.MatchMapForTesting!;
            var tree = form.FilterTreeForTesting;
            var filter = doc.Filters.Roots[0];

            bool ok = Check("the file is open with its filter", doc.CompletedLineCount == Lines && doc.MatchedLineCount == 12_000,
                            $"{doc.CompletedLineCount} lines, {doc.MatchedLineCount} matched");

            // Park deep in the file and then carry the map's window off centre, which is the one state a
            // refresh that forgot where it was placed would visibly undo.
            grid.ScrollToRow(30_000);
            Pump();
            map.GrabForTesting(map.Height / 2);
            map.DragToForTesting(0);
            map.DropForTesting();
            map.LeaveForTesting();
            Pump();

            ok &= Check($"the map is a window over the file, not the whole of it ({map.SpanForTesting} of {doc.RowCount} rows)",
                        map.SpanForTesting < doc.RowCount);
            ok &= Check($"and it has been carried away from the middle (top row {map.TopRowForTesting})",
                        map.TopRowForTesting > 0);

            int generation = doc.FilterGeneration;
            long matched = doc.MatchedLineCount, rows = doc.RowCount, firstRow = grid.FirstVisibleRow;
            long mapTop = map.TopRowForTesting, tracked = map.TrackedViewForTesting;
            int gridPaints = grid.PaintsForTesting, mapPaints = map.PaintsForTesting, listPaints = tree.PaintsForTesting;
            int resolved = map.ColoursResolvedForTesting;
            long visibleRow = (grid.FirstVisibleRow + 4) / 5 * 5;   // the first ERROR line on screen
            ok &= Check($"a line the filter claims is in view (row {visibleRow})",
                        doc.GetLineText(visibleRow).StartsWith("ERROR", StringComparison.Ordinal),
                        doc.GetLineText(visibleRow));
            bool underlinedBefore = grid.FontForRowForTesting(visibleRow).Underline;

            form.EditFilterForTesting(() => filter.Style.Underline = true);
            Pump();

            ok &= Check($"underlining a filter starts no filtering pass (generation {generation})",
                        doc.FilterGeneration == generation, $"generation is now {doc.FilterGeneration}");
            ok &= Check("so there is nothing to wait for", doc.IsFilterIdle && !doc.IsBusy);
            ok &= Check("the same lines are still shown", doc.MatchedLineCount == matched && doc.RowCount == rows,
                        $"{doc.MatchedLineCount} matched, {doc.RowCount} rows");
            ok &= Check("and the view has not moved", grid.FirstVisibleRow == firstRow,
                        $"first row {firstRow} -> {grid.FirstVisibleRow}");
            ok &= Check("nor has the map's window", map.TopRowForTesting == mapTop,
                        $"map top {mapTop} -> {map.TopRowForTesting}");
            ok &= Check($"and it is still tracking the view it was placed for ({tracked})",
                        tracked >= 0 && map.TrackedViewForTesting == tracked,
                        $"tracking {map.TrackedViewForTesting}");

            // Nothing was re-evaluated, so the only proof the change reached the screen is that the places
            // that draw a filter painted again.
            ok &= Check($"the log view repaints ({grid.PaintsForTesting - gridPaints} times)",
                        grid.PaintsForTesting > gridPaints);
            ok &= Check("in an underlined face", !underlinedBefore && grid.FontForRowForTesting(visibleRow).Underline);
            ok &= Check($"the filter list repaints ({tree.PaintsForTesting - listPaints} times)",
                        tree.PaintsForTesting > listPaints);
            ok &= Check($"and the map works its colours out again ({map.ColoursResolvedForTesting - resolved} rows)",
                        map.PaintsForTesting > mapPaints && map.ColoursResolvedForTesting > resolved);

            // A description is not a colour, but it is just as invisible to the engine.
            generation = doc.FilterGeneration;
            listPaints = tree.PaintsForTesting;
            form.EditFilterForTesting(() => filter.Description = "the ones that matter");
            Pump();
            ok &= Check("naming a filter does not start one either", doc.FilterGeneration == generation);
            ok &= Check("and the list still shows it", tree.PaintsForTesting > listPaints);

            // The one that must NOT be shortcut.
            generation = doc.FilterGeneration;
            form.EditFilterForTesting(() => filter.Match.Text = "WARN");
            for (int i = 0; i < 200 && doc.IsBusy; i++) { Thread.Sleep(20); Pump(); }
            Pump();
            ok &= Check("editing what a filter matches does start a pass",
                        doc.FilterGeneration > generation, $"generation {generation} -> {doc.FilterGeneration}");
            ok &= Check("and the view follows it", doc.MatchedLineCount is > 0 and not 12_000,
                        $"{doc.MatchedLineCount} matched, was {matched}");

            // Undo restores CLONES of the filters, so it cannot take the shortcut: the snapshot in force
            // still points at the instances that were replaced. This is what says so out loud.
            long afterEdit = doc.MatchedLineCount;
            ok &= Check("undo puts the pattern back", form.ClickMenuForTesting("Edit", "Undo Edit Filter"));
            for (int i = 0; i < 200 && doc.IsBusy; i++) { Thread.Sleep(20); Pump(); }
            Pump();
            ok &= Check($"and the lines with it ({afterEdit} -> {doc.MatchedLineCount})", doc.MatchedLineCount == 12_000);
            ok &= Check("undo of a restyle takes the underline away too",
                        form.ClickMenuForTesting("Edit", "Undo Edit Filter") &&
                        form.ClickMenuForTesting("Edit", "Undo Edit Filter"));
            for (int i = 0; i < 200 && doc.IsBusy; i++) { Thread.Sleep(20); Pump(); }
            Pump();
            ok &= Check("the log view is drawn plainly again", !grid.FontForRowForTesting(visibleRow).Underline);
            return ok;
        }
        finally
        {
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            try { File.Delete(path); } catch { /* ignore */ }
            try { File.Delete(filters); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// What a frame draws must not change under it. A filtering pass ending half way through one used to
    /// switch which filters answer for a row, so every row painted after that instant came out with no
    /// colour at all - plain unfiltered text across the bottom of an otherwise coloured screen, for one
    /// frame. Driven through a real window, and read off the pixels, because that is the only place the
    /// symptom exists.
    /// </summary>
    private static bool RunColourWhileFilteringChecks()
    {
        Line("-- colour while a pass finishes --");
        string stem = Guid.NewGuid().ToString("N");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_colour_" + stem + ".log");
        string filterFile = Path.Combine(Path.GetTempPath(), "cascade_st_colour_" + stem + ".cascade");

        // More than one 32,768-line block, so the pass can be held with the view still listing the rows the
        // old filters put there below it. In runs, and parked a few lines short of one the exclude hides, so
        // a screenful holds several of those rows however tall a row is on the machine running this.
        const int Lines = 60_000, Run = 25, Park = 40_020, FirstHidden = 40_025;
        var sb = new StringBuilder();
        for (int i = 0; i < Lines; i++)
            sb.Append(i / Run % 2 == 0 ? "ALPHA" : "BETA").Append(" line ").Append(i).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var filters = new FilterCollection { ShowOnlyFilteredLines = true };
        filters.Add(new Filter
        {
            Enabled = true,
            Match = { Text = "ALPHA" },
            Style = { Background = new RgbColor(0xD0, 0xE4, 0xFF), Foreground = new RgbColor(0, 0, 0x60) },
        });
        filters.Add(new Filter
        {
            Enabled = true,
            Match = { Text = "BETA" },
            Style = { Background = new RgbColor(0xD0, 0xFF, 0xD0), Foreground = new RgbColor(0, 0x50, 0) },
        });
        var exclude = new Filter { Enabled = false, Kind = FilterKind.Exclude, Match = { Text = "BETA" } };
        filters.Add(exclude);
        // Enabled and matching nothing: keeps the match cache out of it, so ticking the exclude really
        // sweeps the file instead of being answered in one go from what an earlier pass recorded.
        filters.Add(new Filter { Enabled = true, Match = { Type = FilterMatchType.Marker, MarkerIndex = 3 } });
        CascadeFile.Save(filterFile, filters);

        var settings = new AppSettings();
        MainForm? form = null;
        var gate = new SemaphoreSlim(0);
        int blocks = 0;
        try
        {
            form = new MainForm(settings, new MachineState(), new[] { path, "/Filters:" + filterFile })
            {
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(1000, 760),
            };
            form.NoSavePrompt = true;
            form.Show();
            Pump();
            var doc = form.DocForTesting;
            for (int i = 0; i < 400 && doc.CompletedLineCount < Lines; i++) { Thread.Sleep(10); Pump(); }
            for (int i = 0; i < 400 && doc.IsBusy; i++) { Thread.Sleep(10); Pump(); }

            var grid = form.GridForTesting;
            var live = doc.Filters.EnumerateDepthFirst().First(f => f.Kind == FilterKind.Exclude);
            grid.ScrollToRow(Park);
            Pump();

            bool ok = Check($"the file is open with every line shown and coloured ({doc.RowCount:N0} rows)",
                            doc.CompletedLineCount == Lines && doc.RowCount == Lines && doc.FilteredMode);
            long firstRow = grid.FirstVisibleRow;
            ok &= Check($"parked past the first block, so a held pass has not reached it (row {firstRow:N0})",
                        firstRow >= 32_768);

            // Tick the exclude with the pass held after its first block: the sweep is then a long way above
            // the view, which is still listing every line the old filters showed.
            doc.FilterCheckpointForTesting = _ => { Interlocked.Increment(ref blocks); gate.Wait(TimeSpan.FromSeconds(20)); };
            form.FilterTreeForTesting.ToggleCheckboxForTesting(live, false);
            for (int i = 0; i < 600 && Volatile.Read(ref blocks) == 0; i++) { Thread.Sleep(5); Pump(); }
            ok &= Check($"the pass is held short of the view ({doc.FilterProcessedLineCount:N0} lines done)",
                        Volatile.Read(ref blocks) > 0 && doc.FilterProcessedLineCount < firstRow);
            // What it has already swept it has already dropped; the stretch the view is over, it has not.
            ok &= Check($"so the view is still showing the lines it hides ({doc.RowCount:N0} rows)",
                        doc.RowCount > Lines / 2 && doc.IsLineVisible(FirstHidden));

            var coming = new long[grid.VisibleRowCountForTesting];
            int have = doc.LinesForRows(firstRow, coming);
            int doomed = 0;
            for (int i = 0; i < have; i++) if (doc.GetLineText(coming[i]).StartsWith("BETA", StringComparison.Ordinal)) doomed++;
            ok &= Check($"and a good part of the screen is rows it hides ({doomed} of {have})", doomed >= 5);
            ok &= Check("while the pass runs it holds on to the filters that put those rows there",
                        doc.HoldsOldFiltersForTesting);

            // The frame the report is about: its rows are resolved, and THEN the pass finishes.
            grid.AfterWindowForTesting = () =>
            {
                doc.FilterCheckpointForTesting = null;
                gate.Release(1000);
                for (int i = 0; i < 5000 && !doc.IsFilterIdle; i++) Thread.Sleep(1);
            };
            using var shot = new Bitmap(Math.Max(1, grid.Width), Math.Max(1, grid.Height));
            grid.DrawToBitmap(shot, new Rectangle(0, 0, shot.Width, shot.Height));
            grid.AfterWindowForTesting = null;
            ok &= Check("the pass really did finish inside that frame", doc.IsFilterIdle);

            var drawn = RowBackgrounds(shot, grid);
            int blank = drawn.Count(c => SameColour(c, settings.Background));
            ok &= Check($"every row it drew still carries a filter's colour ({drawn.Count} rows)",
                        blank == 0, $"{blank} of {drawn.Count} were drawn on the view's own background");

            // And the rule is still doing its job: once the view has caught up those rows are gone, not
            // kept alive in the colour of filters that no longer show them.
            for (int i = 0; i < 400 && doc.IsBusy; i++) { Thread.Sleep(10); Pump(); }
            Pump();
            ok &= Check($"the view catches up ({doc.RowCount:N0} rows)", doc.RowCount == Lines / 2);
            // Letting go is driven by the window's own timer, so this is a check of that wiring.
            for (int i = 0; i < 200 && doc.HoldsOldFiltersForTesting; i++) { Thread.Sleep(10); Pump(); }
            ok &= Check("and lets go of them once nothing can be asked about them again",
                        !doc.HoldsOldFiltersForTesting);
            using var settled = new Bitmap(Math.Max(1, grid.Width), Math.Max(1, grid.Height));
            grid.DrawToBitmap(settled, new Rectangle(0, 0, settled.Width, settled.Height));
            var after = RowBackgrounds(settled, grid);
            ok &= Check($"and every row on screen is still coloured ({after.Count} rows)",
                        after.Count > 0 && !after.Any(c => SameColour(c, settings.Background)));
            ok &= Check("with nothing but the lines that are still shown",
                        doc.GetLineText(doc.RowToLine(grid.FirstVisibleRow)).StartsWith("ALPHA", StringComparison.Ordinal));
            return ok;
        }
        finally
        {
            try { if (form is not null) { form.DocForTesting.FilterCheckpointForTesting = null; form.GridForTesting.AfterWindowForTesting = null; } } catch { /* ignore */ }
            gate.Release(1000);
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            gate.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
            try { File.Delete(filterFile); } catch { /* ignore */ }
        }
    }

    private static bool SameColour(Color a, Color b) => a.ToArgb() == b.ToArgb();

    /// <summary>The background each row was painted in, read as the commonest colour along its middle. The
    /// glyphs are a minority of any row, so the mode is the fill - and it is the fill that says whether a
    /// filter coloured the row or nothing did.</summary>
    private static List<Color> RowBackgrounds(Bitmap shot, LineGridControl grid)
    {
        var rows = new List<Color>();
        int top = grid.GutterAreaForTesting.Top;
        int pitch = Math.Max(1, grid.RowHeightForTesting);
        int x0 = grid.GutterWidthForTesting + 4;
        int x1 = Math.Min(shot.Width, grid.GutterWidthForTesting + grid.ContentWidthForTesting) - 4;
        var counts = new Dictionary<int, int>();
        for (int i = 0; i < grid.RowsPaintedForTesting; i++)
        {
            int y = top + i * pitch + pitch / 2;
            if (y >= shot.Height || x1 <= x0) break;
            counts.Clear();
            for (int x = x0; x < x1; x += 3)
            {
                int argb = shot.GetPixel(x, y).ToArgb();
                counts[argb] = counts.GetValueOrDefault(argb) + 1;
            }
            int best = 0, bestCount = -1;
            foreach (var (argb, n) in counts) if (n > bestCount) { best = argb; bestCount = n; }
            rows.Add(Color.FromArgb(best));
        }
        return rows;
    }

    /// <summary>The hang watchdog: it must stay out of the way until asked for, notice a UI thread that has
    /// stopped answering, leave one dump and one report per episode rather than a pile of them, and tell the
    /// window so when it comes back.
    ///
    /// It is driven through a real <see cref="MainForm"/> rather than the class alone, because the half most
    /// likely to be broken is the wiring: a heartbeat that is never sent looks exactly like a hang, and would
    /// dump the process every few seconds of ordinary use.</summary>
    private static bool RunHangWatchdogChecks()
    {
        Line("-- hang watchdog --");
        string dir = Path.Combine(Path.GetTempPath(), "cascade_st_hang_" + Guid.NewGuid().ToString("N"));
        string? oldOn = Environment.GetEnvironmentVariable("CASCADE_HANG_WATCHDOG");
        string? oldSeconds = Environment.GetEnvironmentVariable("CASCADE_HANG_SECONDS");
        string? oldDir = Environment.GetEnvironmentVariable("CASCADE_HANG_DIR");
        string? oldDump = Environment.GetEnvironmentVariable("CASCADE_HANG_DUMP");
        Form? probe = null;
        MainForm? form = null;
        try
        {
            // Every one of these is read back below, so the group has to start from nothing: a run launched
            // with the watchdog already switched on would otherwise be checking the environment it inherited
            // rather than what the app defaults to.
            Environment.SetEnvironmentVariable("CASCADE_HANG_WATCHDOG", null);
            Environment.SetEnvironmentVariable("CASCADE_HANG_SECONDS", null);
            Environment.SetEnvironmentVariable("CASCADE_HANG_DIR", null);
            Environment.SetEnvironmentVariable("CASCADE_HANG_DUMP", null);
            var off = new AppSettings();
            bool ok = Check("nobody is watched unless they ask", !HangWatchdog.IsWanted(off));
            ok &= Check("and the limit is shorter than a freeze anyone would sit through",
                        HangWatchdog.SecondsToWait(off) == 2, $"{HangWatchdog.SecondsToWait(off)}s");
            ok &= Check("a dump leaves the mapped log out unless told otherwise",
                        HangWatchdog.WantedDetail() == DumpDetail.Heap);

            probe = new Form { Opacity = 0, FormBorderStyle = FormBorderStyle.None, ClientSize = new Size(80, 60) };
            ok &= Check("switched off, nothing is started", HangWatchdog.Start(probe, off) is null);

            Environment.SetEnvironmentVariable("CASCADE_HANG_WATCHDOG", "1");
            ok &= Check("the environment can turn it on without touching the preferences", HangWatchdog.IsWanted(off));
            Environment.SetEnvironmentVariable("CASCADE_HANG_WATCHDOG", null);

            // From here the PREFERENCE turns it on, which is how a user would.
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("CASCADE_HANG_SECONDS", "1");
            Environment.SetEnvironmentVariable("CASCADE_HANG_DIR", dir);
            var settings = new AppSettings { HangWatchdog = true };   // and the shipping dump kind, not a cheap one

            form = new MainForm(settings, new MachineState(), Array.Empty<string>())
            {
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(900, 600),
            };
            form.NoSavePrompt = true;
            form.Show();

            // Answering. Longer than the limit, so a heartbeat that never arrived would already have been
            // called a hang - which is what makes this the check on the wiring.
            for (int i = 0; i < 14; i++) { Pump(); Thread.Sleep(100); }
            // "Nothing was recorded" only means something once it is clear something was watching.
            ok &= Check("the preference really did start one", form.WatchingForHangsForTesting);
            ok &= Check("and a window that keeps answering is left alone", Directory.GetFiles(dir).Length == 0,
                        string.Join(", ", Directory.GetFiles(dir).Select(Path.GetFileName)));

            // Not answering: no pumping at all, on the thread that owns the window. The real thing.
            Thread.Sleep(1500);

            // A dump of a process holding a large file takes a second or so to write, so the artefacts do
            // not all appear at once - the report says so until the dump is finished with.
            for (int i = 0; i < 150 && Directory.GetFiles(dir, "cascade_hang_*.txt")
                                                .All(f => File.ReadAllText(f).Contains("still being taken", StringComparison.Ordinal));
                 i++)
                Thread.Sleep(100);

            string[] dumps = Directory.GetFiles(dir, "*.dmp");
            string[] reports = Directory.GetFiles(dir, "cascade_hang_*.txt");
            ok &= Check($"a stalled window is caught ({dumps.Length} dump(s), {reports.Length} report(s))",
                        dumps.Length == 1 && reports.Length == 1);

            if (reports.Length >= 1)
            {
                string text = File.ReadAllText(reports[0]);
                foreach (string line in text.Split('\n'))
                    if (line.Length > 0) Line("   | " + line.TrimEnd());
                ok &= Check("the report says how long it was stuck",
                            text.Contains("stopped answering for", StringComparison.Ordinal), text.Split('\n')[0]);
                ok &= Check("and what the collector was doing, which is what the stacks cannot say",
                            text.Contains("gc pause", StringComparison.Ordinal) &&
                            text.Contains("VERDICT", StringComparison.Ordinal));
                ok &= Check("and how hard the process was having to page",
                            text.Contains("page faults", StringComparison.Ordinal));
                // Whether anything is reading the window through UI Automation decides whether closing a
                // window blocks at all, so a report that does not say is missing the first thing to check.
                ok &= Check("and whether anything is reading the window through automation",
                            text.Contains("automation", StringComparison.Ordinal),
                            text.Split('\n').FirstOrDefault(l => l.Contains("automation", StringComparison.Ordinal))?.Trim()
                            ?? "(not mentioned)");
            }
            if (dumps.Length >= 1)
            {
                byte[] head = new byte[4];
                using (var f = File.OpenRead(dumps[0])) f.ReadExactly(head);
                ok &= Check($"the dump is a real one ({new FileInfo(dumps[0]).Length / 1024:N0} KB)",
                            Encoding.ASCII.GetString(head) == "MDMP", Encoding.ASCII.GetString(head));
            }

            // Coming back: the window is told what was written, so the evidence does not have to be found.
            for (int i = 0; i < 6; i++) { Pump(); Thread.Sleep(20); }
            ok &= Check("and the window says so when it comes back",
                        form.StatusForTesting.Contains("Hang recorded", StringComparison.Ordinal),
                        form.StatusForTesting);

            // The dump a machine will not allow is the whole reason this has a fallback: thread stacks are a
            // fraction of the size and still name the thread that stopped.
            HangWatchdog.RefuseDumpForTesting = flags => flags == HangWatchdog.FlagsForTesting(DumpDetail.Heap);
            int dumpsBefore = Directory.GetFiles(dir, "*.dmp").Length;
            Thread.Sleep(1500);
            for (int i = 0; i < 100 && Directory.GetFiles(dir, "*.dmp").Length == dumpsBefore; i++)
            { Pump(); Thread.Sleep(50); }
            for (int i = 0; i < 6; i++) { Pump(); Thread.Sleep(20); }

            string[] afterFallback = Directory.GetFiles(dir, "cascade_hang_*.txt").OrderBy(f => f).ToArray();
            ok &= Check($"a refused heap dump falls back to thread stacks ({Directory.GetFiles(dir, "*.dmp").Length} dumps)",
                        Directory.GetFiles(dir, "*.dmp").Length == dumpsBefore + 1 && afterFallback.Length == 2);
            if (afterFallback.Length == 2)
            {
                string fell = File.ReadAllText(afterFallback[1]);
                Line("   | " + (fell.Split('\n').FirstOrDefault(l => l.Contains("dump ", StringComparison.Ordinal)) ?? "").Trim());
                ok &= Check("and says so rather than pretending it got what it asked for",
                            fell.Contains("refused", StringComparison.Ordinal) &&
                            fell.Contains("thread stacks only", StringComparison.Ordinal));
            }

            // A dump that cannot be taken at all - which is what security software does to this on some
            // machines - must leave a report saying WHY, or the reader has a hang and no evidence at all.
            HangWatchdog.RefuseDumpForTesting = _ => true;
            int before = Directory.GetFiles(dir, "*.dmp").Length;
            for (int i = 0; i < 6; i++) { Pump(); Thread.Sleep(20); }
            Thread.Sleep(1500);
            for (int i = 0; i < 60 && Directory.GetFiles(dir, "cascade_hang_*.txt").Length < 3; i++)
            { Pump(); Thread.Sleep(50); }
            for (int i = 0; i < 6; i++) { Pump(); Thread.Sleep(20); }

            string[] afterRefusal = Directory.GetFiles(dir, "cascade_hang_*.txt").OrderBy(f => f).ToArray();
            ok &= Check($"a third stall is recorded even when no dump can be taken ({afterRefusal.Length} reports)",
                        afterRefusal.Length == 3 && Directory.GetFiles(dir, "*.dmp").Length == before);
            if (afterRefusal.Length == 3)
            {
                string refused = File.ReadAllText(afterRefusal[2]);
                Line("   | " + (refused.Split('\n').FirstOrDefault(l => l.Contains("dump ", StringComparison.Ordinal)) ?? "").Trim());
                ok &= Check("and the report says no dump was written",
                            refused.Contains("NO DUMP WRITTEN", StringComparison.Ordinal));
                ok &= Check("and carries what Windows gave as the reason, in words and in numbers",
                            System.Text.RegularExpressions.Regex.IsMatch(refused, @"NO DUMP WRITTEN: \S.*\(\d+\)"),
                            refused.Split('\n').FirstOrDefault(l => l.Contains("NO DUMP", StringComparison.Ordinal))?.Trim()
                            ?? "(no such line)");
            }
            ok &= Check("the reason is explained rather than left as a number",
                        HangWatchdog.Explain(112).Contains("(112)", StringComparison.Ordinal) &&
                        HangWatchdog.Explain(112).Contains("no room", StringComparison.Ordinal),
                        HangWatchdog.Explain(112));
            ok &= Check("and the one security software causes is named for what it is",
                        HangWatchdog.Explain(299).Contains("security software", StringComparison.Ordinal),
                        HangWatchdog.Explain(299));
            // dbghelp reports its failures as HRESULTs, so the one error that really turns up here reads as
            // a large negative number unless it is unwrapped.
            ok &= Check("and an HRESULT from dbghelp reads as the error it wraps",
                        HangWatchdog.Explain(unchecked((int)0x8007012B)).Contains("(299)", StringComparison.Ordinal),
                        HangWatchdog.Explain(unchecked((int)0x8007012B)));
            return ok;
        }
        finally
        {
            HangWatchdog.RefuseDumpForTesting = null;
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            try { probe?.Dispose(); } catch { /* ignore */ }
            Environment.SetEnvironmentVariable("CASCADE_HANG_WATCHDOG", oldOn);
            Environment.SetEnvironmentVariable("CASCADE_HANG_SECONDS", oldSeconds);
            Environment.SetEnvironmentVariable("CASCADE_HANG_DIR", oldDir);
            Environment.SetEnvironmentVariable("CASCADE_HANG_DUMP", oldDump);
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    /// <summary>UI Automation is answered only when it is asked for. Checked by sending the very message a
    /// client sends, to the real windows the app builds: the mechanism is a window message, so nothing above
    /// it could tell the difference, and a window that still answers would still arm the teardown that
    /// freezes the app for seconds on a machine that inspects thread creation.</summary>
    private static bool RunAutomationChecks()
    {
        Line("-- automation --");
        string? old = Environment.GetEnvironmentVariable(Automation.Variable);
        try
        {
            // Read back below, so the group has to start from nothing rather than from what it inherited.
            Environment.SetEnvironmentVariable(Automation.Variable, null);
            bool ok = Check("a fresh installation does not answer automation", !new AppSettings().Automation);

            Automation.Configure(false);
            ok &= Check("nor does the app, left alone", !Automation.Wanted);
            Automation.Configure(true);
            ok &= Check("the preference turns it on", Automation.Wanted);

            // Nothing calls Configure on the headless paths, so the answer they get is the bare default.
            using (Automation.ForTesting(null))
                ok &= Check("and a run that never settles the question is silent too", !Automation.Wanted);

            Environment.SetEnvironmentVariable(Automation.Variable, "0");
            Automation.Configure(true);
            ok &= Check("the environment overrules the preference", !Automation.Wanted);
            Environment.SetEnvironmentVariable(Automation.Variable, "1");
            Automation.Configure(false);
            ok &= Check("and does so in both directions", Automation.Wanted);
            Environment.SetEnvironmentVariable(Automation.Variable, null);

            var silent = AskDialogForProviders(automationWanted: false);
            Line($"   | silent: window {silent.Window}, nested {silent.Nested}, older interface {silent.Msaa}");
            ok &= Check("switched off, a window offers nothing", silent.Window == 0);
            // The nested one is the check on the wiring: a dialog's controls are all added after the window
            // has been hooked, so a hook that did not follow them would leave every one of them answering.
            ok &= Check("nor does anything nested inside it", silent.Nested == 0);
            ok &= Check("and the older interface is refused too, or WinForms builds the object anyway",
                        silent.Msaa == 0);

            var answering = AskDialogForProviders(automationWanted: true);
            Line($"   | answering: window {answering.Window}, nested {answering.Nested}, older interface {answering.Msaa}");
            ok &= Check("switched on, a window hands out a provider", answering.Window != 0);
            ok &= Check("and so does a control nested inside it", answering.Nested != 0);
            ok &= Check("and the older interface it can also be reached through", answering.Msaa != 0);

            // The whole point of the warm-up is WHERE the cost lands. Held long enough that a call which
            // waited for it could not possibly come back in time.
            Automation.BeforeWarmUpForTesting = () => Thread.Sleep(600);
            var clock = Stopwatch.StartNew();
            var warmUp = Automation.PayTheStartupCost();
            long handedBack = clock.ElapsedMilliseconds;
            warmUp.Join(10_000);
            ok &= Check($"the startup warm-up does not hold up the thread that starts it ({handedBack} ms)",
                        handedBack < 300);
            return ok;
        }
        finally
        {
            Automation.BeforeWarmUpForTesting = null;
            Environment.SetEnvironmentVariable(Automation.Variable, old);
            Automation.Configure(false);
        }
    }

    /// <summary>Asks a real dialog, the way a client asks, for the two objects a window can be reached
    /// through - at the window itself and at a control nested inside it.</summary>
    private static (long Window, long Nested, long Msaa) AskDialogForProviders(bool automationWanted)
    {
        const uint WmGetObject = 0x003D;
        const int UiaRootObjectId = -25;
        const int ObjIdClient = -4;

        using var scope = Automation.ForTesting(automationWanted);
        using var dlg = new FilterEditDialog(new Filter { Match = { Text = "probe" } }, isNew: true)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Opacity = 0,
        };
        dlg.Show();
        Pump();

        var nested = Nest(dlg) ?? throw new InvalidOperationException("the filter dialog has no text box to ask");
        long window = (long)SendMessage(dlg.Handle, WmGetObject, IntPtr.Zero, UiaRootObjectId);
        long inside = (long)SendMessage(nested.Handle, WmGetObject, IntPtr.Zero, UiaRootObjectId);
        long msaa = (long)SendMessage(dlg.Handle, WmGetObject, IntPtr.Zero, ObjIdClient);
        dlg.Close();
        return (window, inside, msaa);

        static Control? Nest(Control parent)
        {
            foreach (Control c in parent.Controls)
                if (c is TextBox) return c;
                else if (Nest(c) is { } found) return found;
            return null;
        }
    }

    /// <summary>Picking text out of a line works the same split into cells as whole: a click still takes the
    /// row, a drag takes what it covered, the same text is marked wherever else it shows, and a double-click
    /// carries the part picked out into a new filter. What it must not do is run out of the cell it began
    /// in - the text between two cells is not on screen, so a selection across them could not be honest.</summary>
    private static bool RunColumnSelectionChecks()
    {
        Line("-- selecting text inside a cell --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_colsel_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < 40; i++)
            sb.Append($"[2026-08-05T09:31:{i % 60:00}][api-gateway][INFO ] req-abc{i:000} GET /v1/orders -> 200\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();
            doc.Columns.Enabled = true;
            doc.Columns.Template = "{[*]}{[*]}{[*]} {*}";
            doc.Columns.Reset();
            doc.Columns.Columns[0].Name = "time";
            doc.Columns.Columns[1].Name = "service";
            doc.Columns.Columns[2].Name = "level";
            doc.Columns.Columns[3].Name = "message";

            var settings = new AppSettings();
            var grid = new LineGridControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(1000, 320),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(grid);
            grid.Attach(doc, settings);
            host.Show();
            Pump();

            const int Row = 2, Message = 3, Service = 1;
            string text = doc.GetLineText(Row);
            var (msgFrom, msgTo) = grid.CellRangeForTesting(Row, Message);
            var (svcFrom, svcTo) = grid.CellRangeForTesting(Row, Service);
            int X(int column, int index) => grid.XForCharInCellForTesting(Row, column, index);
            int reqAt = text.IndexOf("req-abc", StringComparison.Ordinal);

            bool ok = Check($"the message cell holds the message ({text[msgFrom..msgTo]})",
                            text[msgFrom..msgTo].StartsWith("req-abc", StringComparison.Ordinal));
            ok &= Check($"and the service cell the service ({text[svcFrom..svcTo]})", text[svcFrom..svcTo] == "api-gateway");

            // A click still means the whole row, exactly as it does without columns.
            grid.ClickForTesting(Row, X(Message, reqAt + 2));
            ok &= Check("a click in a cell selects the whole line",
                        !grid.HasCharSelection && grid.SelectedText is null, grid.SelectedText ?? "(none)");

            // A drag inside a cell takes what it covered - the thing that could not be done at all before.
            grid.DragForTesting(Row, X(Message, reqAt), X(Message, reqAt + 10));
            ok &= Check("dragging inside a cell selects that part of the line",
                        grid.SelectedText == text.Substring(reqAt, 10), grid.SelectedText ?? "(none)");
            ok &= Check($"and the selection belongs to that cell (column {grid.CharColumnForTesting})",
                        grid.CharColumnForTesting == Message);

            // Dragging past the cell's own end stops at it: what lies between two cells is not on screen.
            grid.DragForTesting(Row, X(Message, msgFrom), X(Message, msgFrom) + 5000);
            ok &= Check("a drag off the right of a cell stops at the end of that cell",
                        grid.SelectedText == text[msgFrom..msgTo], grid.SelectedText ?? "(none)");
            grid.DragForTesting(Row, X(Service, svcTo), 0);
            ok &= Check("and off the left, at the start of it",
                        grid.SelectedText == text[svcFrom..svcTo], grid.SelectedText ?? "(none)");

            // Dragging onto another row is whole lines again, and coming back picks the cell up where it was.
            grid.PressForTesting(Row, X(Message, reqAt));
            grid.DragOverRowForTesting(4, X(Message, reqAt + 10));
            ok &= Check("a drag that has wandered onto another row is selecting whole lines",
                        !grid.HasCharSelection && grid.CaretRowForTesting == 4, grid.SelectedText ?? "(none)");
            grid.DragOverRowForTesting(Row, X(Message, reqAt + 3));
            ok &= Check("and coming back selects inside the cell it started in",
                        grid.SelectedText == text.Substring(reqAt, 3), grid.SelectedText ?? "(none)");
            grid.ReleaseForTesting(Row, X(Message, reqAt + 3));

            // Double-click carries the part picked out into a new filter, as it does for a whole line.
            var asked = new List<string?>();
            grid.NewFilterRequested += part => asked.Add(part);
            grid.DragForTesting(Row, X(Service, svcFrom), X(Service, svcTo));
            grid.DoubleClickForTesting(Row, X(Service, svcFrom + 2));
            ok &= Check("double-clicking a cell asks for a filter for what was picked out",
                        asked.Count == 1 && asked[0] == "api-gateway",
                        asked.Count == 1 ? asked[0] ?? "(the whole line)" : "(nothing asked)");

            // The visual contract, the same one the whole-line selection keeps: only the range is in the
            // selection colours, and it does not spill into the cell beside it.
            grid.DragForTesting(Row, X(Service, svcFrom), X(Service, svcTo));
            grid.RefreshView();
            Pump();
            using (var picture = Capture(host))
            {
                int rowY = grid.RowMiddleForTesting(Row);
                int inside = X(Service, svcFrom + 4);
                int leftOfIt = grid.ColumnLeftForTesting(Service) - 8;
                int rightOfIt = grid.ColumnLeftForTesting(Service) + grid.ColumnWidthForTesting(Service) + 8;
                ok &= Check("the picked-out text is drawn selected",
                            IsBackground(picture, inside, rowY, settings.SelectionBack),
                            picture.GetPixel(Math.Clamp(inside, 0, picture.Width - 1), rowY).Name);
                ok &= Check("and the cells either side of it are not",
                            !IsBackground(picture, leftOfIt, rowY, settings.SelectionBack) &&
                            !IsBackground(picture, rightOfIt, rowY, settings.SelectionBack),
                            $"{picture.GetPixel(Math.Clamp(leftOfIt, 0, picture.Width - 1), rowY).Name} / " +
                            $"{picture.GetPixel(Math.Clamp(rightOfIt, 0, picture.Width - 1), rowY).Name}");
            }

            // The same text elsewhere is marked, which is what makes picking an id out of one line useful.
            // Every row carries "api-gateway", so the row below must show it marked in its own service cell.
            using (var picture = Capture(host))
            {
                int otherY = grid.RowMiddleForTesting(Row + 1);
                int otherX = grid.XForCharInCellForTesting(Row + 1, Service, grid.CellRangeForTesting(Row + 1, Service).From + 4);
                ok &= Check("the same text on another line is marked too",
                            IsBackground(picture, otherX, otherY, settings.FindHighlight),
                            picture.GetPixel(Math.Clamp(otherX, 0, picture.Width - 1), otherY).Name);
            }

            // Turning the columns off drops a selection that only made sense inside a cell.
            doc.Columns.Enabled = false;
            grid.RefreshView();
            Pump();
            ok &= Check("turning the columns off drops the cell's selection", !grid.HasCharSelection,
                        grid.SelectedText ?? "(none)");

            // ...and so does rearranging the fields from the chip strip. Inline, the indices are into the
            // row AS PROJECTED, so putting a field away moves every character after it: a range kept across
            // that would pick out text nobody chose, and that is what a filter made from it would be built
            // out of. The chips do not go through RefreshView, so this is the path that has to drop it too.
            doc.Columns.Enabled = true;
            doc.Columns.Layout = FieldLayout.Inline;
            grid.RefreshView();
            Pump();

            string inline = grid.DisplayTextForTesting(Row);
            int reqInline = inline.IndexOf("req-abc", StringComparison.Ordinal);
            ok &= Check($"the inline row still reads as the line ({inline})", reqInline > 0, inline);

            grid.DragForTesting(Row, grid.XForCharForTesting(Row, reqInline),
                                     grid.XForCharForTesting(Row, reqInline + 10));
            ok &= Check("dragging inside an inline row picks out what it covered",
                        grid.SelectedText == inline.Substring(reqInline, 10), grid.SelectedText ?? "(none)");

            grid.SetColumnVisible(0, false);
            Pump();
            ok &= Check("putting a field away drops a selection made before the row moved",
                        !grid.HasCharSelection, grid.SelectedText ?? "(none)");

            // Carrying one along the row moves the text just as much, so that has to drop it as well. Both
            // are driven through the chips themselves, which is the only way a reader can do either.
            doc.Columns.Columns[0].Visible = true;
            grid.RefreshView();
            Pump();
            inline = grid.DisplayTextForTesting(Row);
            reqInline = inline.IndexOf("req-abc", StringComparison.Ordinal);
            grid.DragForTesting(Row, grid.XForCharForTesting(Row, reqInline),
                                     grid.XForCharForTesting(Row, reqInline + 10));
            ok &= Check("a fresh selection to carry a field out from under",
                        grid.SelectedText == inline.Substring(reqInline, 10), grid.SelectedText ?? "(none)");
            grid.DragChipForTesting(2, 0);
            Pump();
            ok &= Check("carrying a field along the row drops it too",
                        !grid.HasCharSelection, grid.SelectedText ?? "(none)");

            // And a chip clicked, which is the same edit by the other gesture.
            inline = grid.DisplayTextForTesting(Row);
            reqInline = inline.IndexOf("req-abc", StringComparison.Ordinal);
            grid.DragForTesting(Row, grid.XForCharForTesting(Row, reqInline),
                                     grid.XForCharForTesting(Row, reqInline + 10));
            ok &= Check("a fresh selection to click a chip out from under",
                        grid.SelectedText == inline.Substring(reqInline, 10), grid.SelectedText ?? "(none)");
            grid.ClickChipForTesting(0);
            Pump();
            ok &= Check("clicking a chip drops it as well",
                        !grid.HasCharSelection, grid.SelectedText ?? "(none)");
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
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

        // The record of which lines matched more than once is capped, and losing it costs only the split
        // between shown and hidden - so the floor goes on the shown count, never on the file-wide total.
        string approx = FindStatusText.Short(T(12, 252, 96, 891, 1204, approx: true));
        ok &= Check("a floored occurrence count says it is a floor",
                    approx == "Match 12 of 252 lines \u00b7 96 hidden \u00b7 \u2265891 of 1,204 hits", approx);

        string approxLong = FindStatusText.Long(T(12, 252, 96, 891, 1204, approx: true), "disk");
        ok &= Check("and says so in the long form too, about the shown count",
                    approxLong.Contains("at least 891 occurrences shown of 1,204"), approxLong);

        // The reported bug: with nothing hidden every occurrence is on a shown line, so the count is exact
        // whatever the cap did, and marking it as a floor was simply wrong.
        string nothingHidden = FindStatusText.Short(T(12, 252, 0, 1204, 1204, approx: true));
        ok &= Check("with nothing hidden the count is exact and is not marked as a floor",
                    !nothingHidden.Contains('\u2265'), nothingHidden);

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
                    tip.Split('\n').Length == FilterTipText.MaxListed + 1 && tip.EndsWith("and 5 more", StringComparison.Ordinal), tip);

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

    /// <summary>
    /// How wide a line is drawn decides how far right the view can be scrolled, and measuring it for every
    /// row of every frame cost a sixth of a repaint. It is now worked out arithmetically whenever it can be
    /// - a fixed-pitch face, plain ASCII - so what has to hold is that the shortcut and the measurement
    /// never disagree, in either face, on either kind of text.
    /// </summary>
    private static bool RunTextWidthChecks()
    {
        Line("-- text width --");
        bool ok = true;

        string[] samples =
        {
            "",
            "a",
            "2026-08-10T12:00:00.123 [api-gateway] INFO  req-8891 accepted in 12ms",
            new string('W', 400),
            "  leading and trailing spaces   ",
            "tabs are expanded before this point, so: plain",
            "punctuation !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~",
            "caf\u00e9 na\u00efve \u4f60\u597d \u0442\u0435\u043a\u0441\u0442 emoji \U0001F600",   // must fall back to measuring
            "control\u0001chars\u0007here",
        };

        var grid = new LineGridControl();
        var settings = new AppSettings();
        try
        {
            foreach (string family in new[] { "Consolas", "Segoe UI" })
            {
                settings.FontFamily = family;
                grid.ApplySettings(settings);
                bool monospaced = grid.MonospacedForTesting;
                ok &= Check($"\"{family}\" is recognised as {(family == "Consolas" ? "fixed" : "proportional")} pitch",
                            monospaced == (family == "Consolas"), $"monospaced={monospaced}");

                int shortcuts = 0;
                bool agrees = true;
                foreach (string s in samples)
                    for (int style = 0; style < 8; style++)
                    {
                        int drawn = grid.DrawnWidthForTesting(s, style);
                        int measured = grid.MeasuredWidthForTesting(s, style);
                        if (drawn != measured)
                        {
                            agrees = false;
                            Check($"{family} style {style}: the width of \"{Clip(s)}\" agrees", false,
                                  $"worked out {drawn}, measured {measured}");
                            break;
                        }
                        if (grid.WidthWasArithmeticForTesting(s, style)) shortcuts++;
                    }
                ok &= Check($"every width in {family} agrees with the measurement", agrees, "");
                // The shortcut has to be doing something in the fixed-pitch face, and nothing in the other,
                // or this check would pass with it removed.
                ok &= Check($"the shortcut is {(monospaced ? "taken" : "never taken")} in {family}",
                            monospaced ? shortcuts > samples.Length * 4 : shortcuts == 0,
                            $"{shortcuts} of {samples.Length * 8}");
            }
            return ok;
        }
        finally
        {
            grid.Dispose();
        }

        static string Clip(string s) => s.Length <= 24 ? s : s[..24] + "...";
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
        // Enough plain lines after it to fill a screen, or the runaway row is what the end of the file
        // butts up against and no scrolling rule can be told apart from any other there.
        for (int i = 0; i < 40; i++) sb.Append($"tail {i}\n");
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

            // ...and it has to bring the caret with it. Scrolling was counted in unwrapped rows, of which
            // many more fit, so the caret was reckoned to be on screen while it was pages below.
            Pump();
            ok &= Check("and the caret it moved is on screen afterwards",
                        grid.CaretRowForTesting >= grid.FirstRowForTesting &&
                        grid.CaretRowForTesting < grid.FirstRowForTesting + grid.RowsPaintedForTesting,
                        $"caret {grid.CaretRowForTesting}, showing {grid.FirstRowForTesting}.." +
                        $"{grid.FirstRowForTesting + grid.RowsPaintedForTesting - 1}");
            for (int i = 0; i < 3; i++) { grid.PressKeyForTesting(Keys.PageDown); Pump(); }
            ok &= Check("and stays on screen page after page",
                        grid.CaretRowForTesting >= grid.FirstRowForTesting &&
                        grid.CaretRowForTesting < grid.FirstRowForTesting + grid.RowsPaintedForTesting,
                        $"caret {grid.CaretRowForTesting}, showing {grid.FirstRowForTesting}.." +
                        $"{grid.FirstRowForTesting + grid.RowsPaintedForTesting - 1}");
            grid.PressKeyForTesting(Keys.PageUp);
            Pump();
            ok &= Check("and page up keeps it too",
                        grid.CaretRowForTesting >= grid.FirstRowForTesting &&
                        grid.CaretRowForTesting < grid.FirstRowForTesting + grid.RowsPaintedForTesting,
                        $"caret {grid.CaretRowForTesting}, showing {grid.FirstRowForTesting}.." +
                        $"{grid.FirstRowForTesting + grid.RowsPaintedForTesting - 1}");

            // ...and it lands on the row at the bottom, not the one above it. A page was counted from where
            // the caret came from, and the rows it was going to are shorter, so more of them fit than were
            // counted for and the caret stopped short.
            grid.ScrollToRow(0);
            grid.RefreshView();
            Pump();
            grid.PressKeyForTesting(Keys.PageDown);
            Pump();
            ok &= Check("page down leaves the caret on the last row on screen",
                        grid.CaretRowForTesting == grid.FirstRowForTesting + grid.RowsPaintedForTesting - 1,
                        $"caret {grid.CaretRowForTesting}, last on screen " +
                        $"{grid.FirstRowForTesting + grid.RowsPaintedForTesting - 1}");

            // The gutter is the neutral margin all the way down a wrapped row. It used to be filled for one
            // line only, so every segment below the first kept the row's own colour - or, worse, the
            // selection colour, which made the selection look like it ran into the line numbers.
            grid.SelectRowForAccessibility(tall);
            grid.ScrollToRow(tall);
            grid.RefreshView();
            Pump();
            using (var picture = Capture(host))
            {
                int gutterX = grid.GutterWidthForTesting - 4;
                int firstY = grid.RowTopForTesting(tall) + 2;
                int secondY = grid.RowTopForTesting(tall) + grid.RowHeightForTesting + 2;
                var first = picture.GetPixel(gutterX, firstY);
                var second = picture.GetPixel(gutterX, secondY);
                ok &= Check("the line-number margin is the same colour all the way down a wrapped row",
                            first.ToArgb() == second.ToArgb(), $"first segment {first}, second {second}");
                ok &= Check("and the selection does not reach into it",
                            second.ToArgb() != settings.SelectionBack.ToArgb(),
                            $"{second} against a selection of {settings.SelectionBack}");
            }
            grid.SelectRowForAccessibility(0);

            // Scrolling has to stop with the last row against the bottom. Letting it go further leaves a
            // screenful of nothing below the end of the file.
            grid.ScrollToRow(doc.RowCount);
            grid.RefreshView();
            Pump();
            ok &= Check("scrolling to the end stops with the last row on screen, not past it",
                        grid.FirstRowForTesting + grid.RowsPaintedForTesting == doc.RowCount,
                        $"showing {grid.FirstRowForTesting}..{grid.FirstRowForTesting + grid.RowsPaintedForTesting - 1} " +
                        $"of {doc.RowCount}");
            ok &= Check("and more than one row is still on screen there", grid.RowsPaintedForTesting > 1,
                        $"{grid.RowsPaintedForTesting} rows");

            // ...and it has taken every row it could. Requiring the whole of the last row to fit leaves the
            // bottom blank by however much the row above would have overhung, which on a wrapped row is
            // most of a screenful.
            long endFirst = grid.FirstRowForTesting;
            long above = 0;
            for (long r = endFirst; r < doc.RowCount - 1; r++) above += grid.RowHeightOfForTesting(r);
            ok &= Check("and one row further up would have pushed the last one off",
                        endFirst == 0 ||
                        above + grid.RowHeightOfForTesting(endFirst - 1) >= grid.ViewportHeightForTesting,
                        $"rows above the last take {above}px, one more is " +
                        $"{grid.RowHeightOfForTesting(Math.Max(0, endFirst - 1))}px, view is " +
                        $"{grid.ViewportHeightForTesting}px");

            // Nowhere in the middle of the file may the view leave room for a line it did not draw. It used
            // to keep the sideways scrollbar's height back even when wrapping had hidden it, and a hidden
            // docked control takes no space - so about a line of the view went unused.
            ok &= Check("no room is kept for a scrollbar that is not showing", grid.ChromeHeight == 0,
                        $"{grid.ChromeHeight}px reserved with the sideways bar " +
                        (grid.HScrollBarForTesting.Visible ? "showing" : "hidden"));
            foreach (long at in new long[] { 0, 5, 12, 30 })
            {
                grid.ScrollToRow(at);
                grid.RefreshView();
                Pump();
                long last = grid.FirstRowForTesting + grid.RowsPaintedForTesting - 1;
                int bottom = grid.RowTopForTesting(last) + grid.SegmentsForTesting(last) * grid.RowHeightForTesting;
                var hbar = grid.HScrollBarForTesting;
                int room = grid.ClientSize.Height - (hbar.Visible ? hbar.Height : 0);
                ok &= Check($"the view is filled to the bottom from row {at}",
                            last == doc.RowCount - 1 || room - bottom < grid.RowHeightForTesting,
                            $"{room - bottom}px spare under row {last}, a line is {grid.RowHeightForTesting}px");
            }

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

            // Laid out inline a row is still a line, so it wraps like any other - which is what the menu
            // offers. It did not: anything with fields turned on refused to wrap at all, columns or not.
            doc.Columns.Enabled = true;
            doc.Columns.Layout = FieldLayout.Inline;
            doc.Columns.Template = "{[*]} {*}";
            doc.Columns.Reset();
            grid.RefreshView();
            Pump();
            ok &= Check("a long line still wraps while the fields are laid out inline",
                        grid.SegmentsForTesting(1) > 1, $"{grid.SegmentsForTesting(1)} segments");
            doc.Columns.Layout = FieldLayout.Columns;
            grid.RefreshView();
            Pump();
            ok &= Check("and laid out in columns it does not, because a cell is not a line",
                        grid.SegmentsForTesting(1) == 1, $"{grid.SegmentsForTesting(1)} segments");
            doc.Columns.Enabled = false;
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

    /// <summary>Gives the filter list exactly this many rows.
    ///
    /// A pane sized in PIXELS holds a different number of rows on a machine with different display
    /// scaling - 14 here, 21 on the build agent - so a fixture built around "more children than fit"
    /// silently stops meaning anything. Sizing it in rows instead makes the arithmetic the same
    /// everywhere. The loop is because the header's own height moves with the window.</summary>
    private static void FitTreeToRows(Form host, FilterTreeControl tree, int rows)
    {
        for (int i = 0; i < 4 && tree.TreeHeightForTesting / tree.RowHeightForTesting != rows; i++)
        {
            int chrome = host.ClientSize.Height - tree.TreeHeightForTesting;
            host.ClientSize = new Size(host.ClientSize.Width, chrome + rows * tree.RowHeightForTesting);
            Pump();
        }
    }

    /// <summary>Expanding a filter leaves the list where it was.
    ///
    /// Left to itself the tree scrolls on every expansion, to fit as much of the newly revealed subtree on
    /// screen as it can - so the row being looked at is yanked somewhere else, and during a drag a drop
    /// that nests into a folded filter moves the list out from under the pointer. See BufferedTreeView.
    /// The fixture deliberately gives the parent more children than there is room for below it, or the
    /// tree would have had no reason to scroll and the checks would pass by themselves.</summary>
    private static bool RunFilterExpandChecks()
    {
        Line("-- expanding a filter --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_expand_" + Guid.NewGuid().ToString("N") + ".log");
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
                ClientSize = new Size(300, 520),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(tree);
            tree.Attach(doc);
            host.Show();
            Pump();
            FitTreeToRows(host, tree, 14);

            var filters = new FilterCollection();
            for (int i = 0; i < 40; i++)
                filters.Roots.Add(new Filter { Match = new FilterMatch { Text = $"f{i:00}" } });
            var parent = filters.Roots[5];
            for (int c = 0; c < 20; c++)
            {
                var kid = new Filter { Match = new FilterMatch { Text = $"kid{c:00}" } };
                filters.Roots.Add(kid);
                filters.Move(kid, parent, parent.Children.Count);
            }
            doc.SetFilters(filters);
            tree.Rebuild();
            Pump();

            int rows = tree.TreeHeightForTesting / tree.RowHeightForTesting;
            bool ok = Check($"the parent has more children ({parent.Children.Count}) than the pane has rows ({rows}), " +
                            "so the tree has a reason to scroll",
                            parent.Children.Count > rows);
            if (!ok) return false;

            // Folded, with the parent one row down from the top: the tree used to pull it up to the top.
            tree.CollapseForTesting(parent);
            Pump();
            tree.ScrollToForTesting(filters.Roots[4]);
            Pump();
            string before = tree.TopFilterForTesting?.Match.Text ?? "?";
            tree.ExpandForTesting(parent);
            Pump();
            string after = tree.TopFilterForTesting?.Match.Text ?? "?";
            ok &= Check($"unfolding a filter on screen does not scroll the list [{before} -> {after}]", before == after);
            ok &= Check("and it really did unfold", tree.IsExpandedForTesting(parent));

            // Folded, with the parent above the view: the tree used to jump the whole way to its subtree.
            tree.CollapseForTesting(parent);
            Pump();
            tree.ScrollToForTesting(filters.Roots[20]);
            Pump();
            before = tree.TopFilterForTesting?.Match.Text ?? "?";
            tree.ExpandForTesting(parent);
            Pump();
            after = tree.TopFilterForTesting?.Match.Text ?? "?";
            ok &= Check($"nor does unfolding one that is above the view [{before} -> {after}]", before == after);

            // Reaching a filter inside a folded subtree still has to open it and go there.
            tree.CollapseForTesting(parent);
            Pump();
            tree.ScrollToForTesting(filters.Roots[0]);
            Pump();
            tree.RevealForTesting(parent.Children[^1]);
            Pump();
            var shown = tree.VisibleFiltersForTesting;
            ok &= Check($"but reaching a filter inside a folded one still opens it and shows it " +
                        $"[top {tree.TopFilterForTesting?.Match.Text}]",
                        tree.IsExpandedForTesting(parent) && shown.Contains(parent.Children[^1]));
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>Carrying a filter up through an unfolded subtree has to walk it one place at a time, the
    /// same as anywhere else in the list.
    ///
    /// The gap between a filter and its first child is the awkward one: the row above the gap IS the
    /// parent being dropped into, so there is nothing inside that parent to count the position from. Read
    /// as "not found" it becomes "append", and the filter is flung to the BOTTOM of the subtree and then
    /// straight back out on the next row of travel - which is what "it jumps somewhere I did not mean it
    /// to go" looks like. The walk below is measured in display rows, so one row of pointer travel has to
    /// be exactly one row of movement whatever level the filter is at.</summary>
    private static bool RunDragNestingChecks()
    {
        Line("-- dragging into and out of a subtree --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_nest_" + Guid.NewGuid().ToString("N") + ".log");
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
                ClientSize = new Size(300, 520),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(tree);
            tree.Attach(doc);
            host.Show();
            Pump();
            // Verified across 12..24 rows, so nothing here is tuned to one particular row height.
            FitTreeToRows(host, tree, 14);

            var filters = new FilterCollection();
            for (int i = 0; i < 40; i++)
                filters.Roots.Add(new Filter { Match = new FilterMatch { Text = $"f{i:00}" } });
            var parent = filters.Roots[10];
            for (int c = 0; c < 8; c++)
            {
                var kid = new Filter { Match = new FilterMatch { Text = $"kid{c:00}" } };
                filters.Roots.Add(kid);
                filters.Move(kid, parent, parent.Children.Count);
            }
            doc.SetFilters(filters);
            tree.Rebuild();
            Pump();

            int rowH = tree.RowHeightForTesting;
            int lastWholeRow = tree.TreeHeightForTesting / rowH - 1;
            int MiddleOfRow(int index) => index * rowH + rowH / 2;

            // Put the parent's whole subtree on screen with a row to spare either side, and carry the
            // filter below it upwards past every one of its children.
            tree.ScrollToForTesting(filters.Roots[9]);
            Pump();
            bool ok = Check($"the parent and all {parent.Children.Count} of its children are on screen, " +
                            $"with room below ({lastWholeRow + 1} rows)",
                            lastWholeRow >= parent.Children.Count + 3);
            if (!ok) return false;

            var carried = filters.Roots[11];
            int kids = parent.Children.Count;
            var grab = tree.RowBoundsForTesting(carried);
            int grabX = grab.Left + 2;
            tree.StartDragForTesting(carried, new Point(grabX, grab.Top + grab.Height / 2));

            // Stay clear of the top and bottom rows, where holding the pointer starts the auto-scroll.
            var places = new List<string>();
            var inside = new List<int>();
            var display = new List<int>();
            for (int r = lastWholeRow - 1; r >= 1; r--)
            {
                tree.DragToForTesting(new Point(grabX, MiddleOfRow(r)));
                Pump();
                var siblings = carried.Parent?.Children ?? doc.Filters.Roots;
                int at = siblings.IndexOf(carried);
                places.Add($"{carried.Parent?.Match.Text ?? "root"}[{at}]");
                if (ReferenceEquals(carried.Parent, parent)) inside.Add(at);
                display.Add(Array.IndexOf(tree.RowOrderForTesting, carried.Match.Text));
            }
            Line("   " + string.Join(" ", places));

            ok &= Check($"a drop just under a filter makes the dragged one its FIRST child, not its last " +
                        $"[{string.Join(" ", inside)}]",
                        inside.Count > 0 && inside[^1] == 0);
            // Read off the places it visits, not how many samples each took: one place of hysteresis is
            // inherent (the filter occupies a row in the list it is being placed into) and how it falls
            // depends on the row height, which follows the display's scaling.
            var walked = new List<int>();
            foreach (int at in inside) if (walked.Count == 0 || walked[^1] != at) walked.Add(at);
            ok &= Check($"it walks up through the children rather than jumping about inside them " +
                        $"[{string.Join(" ", walked)}]",
                        walked.SequenceEqual(Enumerable.Range(0, kids).Reverse()));
            var steps = display.Zip(display.Skip(1), (a, b) => a - b).ToList();
            ok &= Check($"one row of pointer travel never moves it more than one row, in or out of the " +
                        $"subtree [{string.Join(" ", display)}]",
                        steps.Count > kids && steps.All(s => s is 0 or 1) && display[0] - display[^1] >= kids);

            tree.DropForTesting();
            Pump();

            // Nesting into a FOLDED filter must not move the list either - that is the expansion the tree
            // used to scroll for, and it happens in the middle of a drag with the pointer standing still.
            tree.CollapseForTesting(parent);
            Pump();
            tree.ScrollToForTesting(filters.Roots[4]);
            Pump();
            string before = tree.TopFilterForTesting?.Match.Text ?? "?";
            var folded = tree.RowBoundsForTesting(parent);
            var moved = filters.Roots[6];
            var mRow = tree.RowBoundsForTesting(moved);
            tree.StartDragForTesting(moved, new Point(mRow.Left + 2, mRow.Top + mRow.Height / 2));
            // Straight down to the gap under the folded filter, and one indent right to nest into it.
            tree.DragToForTesting(new Point(mRow.Left + 2 + tree.IndentForTesting, folded.Bottom + 2));
            Pump();
            string after = tree.TopFilterForTesting?.Match.Text ?? "?";
            ok &= Check($"nesting into a folded filter does not scroll the list [{before} -> {after}]", before == after);
            ok &= Check($"and the filter really did go into it, at the top [{moved.Parent?.Match.Text ?? "root"}" +
                        $"[{(moved.Parent?.Children ?? doc.Filters.Roots).IndexOf(moved)}]]",
                        ReferenceEquals(moved.Parent, parent) && parent.Children.IndexOf(moved) == 0);
            tree.CancelDragForTesting();
            Pump();
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
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

            // Grab the tall subtree, which sits last, and walk the pointer up a row at a time. Every stop
            // is the MIDDLE of a row: where a drop lands turns on which half of a row the pointer is in, so
            // stops measured from the pane's height instead land at a different place within a row whenever
            // that height is not a whole number of them - and one row of travel then reads as two places.
            int MiddleOfRow(int index) => index * rowH + rowH / 2;
            int lastWholeRow = viewport / rowH - 1;

            tree.StartDragForTesting(carried, new Point(20, MiddleOfRow(lastWholeRow - 1)));
            ok &= Check("a subtree is carried collapsed, so it cannot fill the pane it moves through",
                        !tree.IsExpandedForTesting(carried));

            var seen = new List<int>();
            var tops = new List<string>();
            // Stay clear of the edges: the auto-scroll zone is a row deep at each end, and scrolling is
            // meant to move the list, which would confuse a check about the pointer alone.
            var stops = new List<int>();
            for (int r = lastWholeRow - 2; r >= 2; r--) stops.Add(MiddleOfRow(r));
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
            Pump();

            ok &= RunGroupDragChecks(doc, tree, rowH);
            ok &= RunAbandonedDragChecks(doc, tree, rowH);
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>Several filters are carried as one placeholder row and placed once, on the drop. Carrying
    /// the real rows would make the block taller than the pane it is being dragged through, which is the
    /// same reason a subtree is carried collapsed.</summary>
    private static bool RunGroupDragChecks(CascadeDocument doc, FilterTreeControl tree, int rowH)
    {
        var roots = doc.Filters.Roots;
        var one = roots[3];
        var two = roots[5];
        var three = roots[7];
        string Order(int n) => string.Join(" ", roots.Take(n).Select(f => f.Match.Text));
        string before = Order(12);

        tree.ClickFilterForTesting(one);
        tree.ClickFilterForTesting(two, Keys.Control);
        tree.ClickFilterForTesting(three, Keys.Control);
        var row = tree.RowBoundsForTesting(one);
        tree.StartDragForTesting(one, new Point(row.Left + 2, row.Top + row.Height / 2));
        Pump();

        bool ok = Check($"a group is carried as one row, which says how many [{tree.GhostTextForTesting}]",
                        tree.GhostTextForTesting == "3 filters");
        ok &= Check($"the filters themselves have not moved yet [{Order(12)}]", Order(12) == before);
        ok &= Check($"and their rows are out of the list while it is carried " +
                    $"[{string.Join(" ", tree.VisibleRowNamesForTesting.Take(10))}]",
                    !tree.VisibleRowNamesForTesting.Take(10).Contains(two.Match.Text));

        // Escape puts everything back, and the model was never touched, so this has to be exact.
        tree.CancelDragForTesting();
        Pump();
        ok &= Check($"escaping a group drag leaves the list exactly as it was [{Order(12)}]", Order(12) == before);
        ok &= Check("and the rows are back", tree.VisibleRowNamesForTesting.Contains(two.Match.Text));

        // Now really drop it, at the very top.
        tree.ClickFilterForTesting(one);
        tree.ClickFilterForTesting(two, Keys.Control);
        tree.ClickFilterForTesting(three, Keys.Control);
        row = tree.RowBoundsForTesting(one);
        tree.StartDragForTesting(one, new Point(row.Left + 2, row.Top + row.Height / 2));
        tree.DragToForTesting(new Point(row.Left + 2, rowH / 4));
        Pump();
        tree.DropGroupForTesting();
        Pump();

        string landed = string.Join(" ", roots.Take(3).Select(f => f.Match.Text));
        ok &= Check($"dropping lands all three together, in the order they were in [{landed}]",
                    landed == $"{one.Match.Text} {two.Match.Text} {three.Match.Text}");
        ok &= Check($"the group it dropped is what stays selected [{string.Join(" ", tree.SelectedNamesForTesting)}]",
                    string.Join(" ", tree.SelectedNamesForTesting) == landed);
        // Read off the rows, not the field that held the placeholder: that field is cleared either way, so
        // a placeholder still standing in the list would go unnoticed.
        var leftovers = tree.VisibleRowNamesForTesting.Where(n => n.EndsWith(" filters", StringComparison.Ordinal)).ToArray();
        ok &= Check($"and there is no placeholder row left behind [{(leftovers.Length == 0 ? "none" : string.Join(" ", leftovers))}]",
                    leftovers.Length == 0);
        ok &= Check($"the list shows what the model says [{string.Join(" ", tree.VisibleRowNamesForTesting.Take(3))}]",
                    string.Join(" ", tree.VisibleRowNamesForTesting.Take(3)) == landed);
        return ok;
    }

    /// <summary>Letting go of a filter somewhere that is not the list ends the drag.
    ///
    /// This is the whole of what the source is told in that case: OLE calls nothing on a window that is not
    /// a drop target, and MainForm's own file-drop targets answer DROPEFFECT_NONE for a filter, which OLE
    /// turns into DragLeave rather than Drop - so DoDragDrop simply returns. Missing that left the control
    /// mid-drag for the rest of the session: the row kept its fade, no filter could be picked up again and
    /// the pane stopped offering to open dropped files. The easiest way to hit it is aiming a filter at the
    /// very top of the list and releasing on the column header a row above it.</summary>
    private static bool RunAbandonedDragChecks(CascadeDocument doc, FilterTreeControl tree, int rowH)
    {
        // A flat fixture of its own: what the checks above leave behind is nested and scrolled, and where a
        // row sits on screen is what a drag is aimed at.
        var filters = new FilterCollection();
        for (int i = 0; i < 20; i++)
            filters.Roots.Add(new Filter { Match = new FilterMatch { Text = $"a{i:00}" } });
        doc.SetFilters(filters);
        tree.Rebuild();
        Pump();

        var roots = doc.Filters.Roots;
        string Order() => string.Join(" ", roots.Select(f => f.Match.Text));
        string Shown() => string.Join(" ", tree.VisibleRowNamesForTesting.Take(8));

        int reevaluated = 0;
        void Count() => reevaluated++;
        tree.FiltersChanged += Count;

        // ---- one filter
        var carried = roots[6];
        string before = Order();
        var row = tree.RowBoundsForTesting(carried);
        tree.StartDragForTesting(carried, new Point(row.Left + 2, row.Top + row.Height / 2));
        tree.DragToForTesting(new Point(row.Left + 2, rowH / 4));
        Pump();
        bool ok = Check($"the drag really did carry the filter somewhere else [{Shown()}]", Order() != before);
        ok &= Check("and the list knows a drag is under way, which is what fades the row",
                    tree.DragInProgressForTesting);

        tree.ReleaseAwayFromTheListForTesting();
        Pump();
        ok &= Check("letting go away from the list ends the drag", !tree.DragInProgressForTesting);
        ok &= Check($"and puts the filter back where it was [{Shown()}]", Order() == before);
        ok &= Check($"without re-running the filters over the file ({reevaluated} times)", reevaluated == 0);
        ok &= Check("the pane offers to open a dropped file again",
                    tree.DragEffectForTesting(DragArgs(Files(doc.FilePath!))) == DragDropEffects.Copy);

        // The reported symptom was not the fade but what came after it: nothing in the list would move.
        var next = roots[3];
        bool pickedUp = tree.StartDragForTesting(next, new Point(row.Left + 2, tree.RowBoundsForTesting(next).Top + rowH / 2));
        tree.DragToForTesting(new Point(row.Left + 2, rowH / 4));
        Pump();
        tree.DropForTesting();
        Pump();
        ok &= Check($"and another filter can still be dragged afterwards [{Shown()}]",
                    pickedUp && ReferenceEquals(roots[0], next));
        ok &= Check($"a real drop re-runs the filters exactly once, however far it was carried ({reevaluated})",
                    reevaluated == 1);
        tree.FiltersChanged -= Count;

        // Escape ends the drag the same way, and the direction that carries the filter back DOWN the list
        // is the one that used to leave it a place short of where it was picked up.
        foreach (bool upward in new[] { true, false })
        {
            tree.ScrollToForTesting(roots[0]);
            Pump();
            var f = roots[upward ? 9 : 4];
            before = Order();
            var r = tree.RowBoundsForTesting(f);
            tree.StartDragForTesting(f, new Point(r.Left + 2, r.Top + r.Height / 2));
            tree.DragToForTesting(new Point(r.Left + 2, upward ? rowH / 4 : rowH * 12 + rowH / 2));
            Pump();
            ok &= Check($"dragging {(upward ? "up" : "down")} moved it [{Shown()}]", Order() != before);
            tree.CancelDragForTesting();
            Pump();
            ok &= Check($"escape after dragging {(upward ? "up" : "down")} puts it back exactly [{Shown()}]",
                        Order() == before);
        }

        // ---- several filters, which are carried as a placeholder with their rows out of the list
        tree.ScrollToForTesting(roots[0]);
        Pump();
        var one = roots[2];
        var two = roots[4];
        var three = roots[6];
        before = Order();
        tree.ClickFilterForTesting(one);
        tree.ClickFilterForTesting(two, Keys.Control);
        tree.ClickFilterForTesting(three, Keys.Control);
        row = tree.RowBoundsForTesting(one);
        tree.StartDragForTesting(one, new Point(row.Left + 2, row.Top + row.Height / 2));
        tree.DragToForTesting(new Point(row.Left + 2, rowH * 9 + rowH / 2));
        Pump();
        tree.ReleaseAwayFromTheListForTesting();
        Pump();

        ok &= Check("letting go of a group away from the list ends that drag too",
                    !tree.DragInProgressForTesting);
        ok &= Check($"the filters it was carrying are back in the list [{Shown()}]",
                    tree.VisibleRowNamesForTesting.Contains(one.Match.Text)
                    && tree.VisibleRowNamesForTesting.Contains(two.Match.Text)
                    && tree.VisibleRowNamesForTesting.Contains(three.Match.Text));
        ok &= Check($"with no placeholder row left standing [{Shown()}]",
                    !tree.VisibleRowNamesForTesting.Any(n => n.EndsWith(" filters", StringComparison.Ordinal)));
        ok &= Check($"and nothing moved [{Shown()}]", Order() == before);
        ok &= Check($"the selection is still something the menus can act on " +
                    $"[{string.Join(" ", tree.SelectedNamesForTesting)}]",
                    tree.SelectedNamesForTesting.Length == 3);
        return ok;
    }

    /// <summary>A filter's checkbox has to keep meaning that filter and nothing else: a parent's pattern is
    /// required of its children whether or not the parent is on, so "off here, on underneath" is a real and
    /// useful arrangement that cascading by default would wipe out. Shift is what asks for the subtree.</summary>
    /// <summary>Putting a restored filter tree on screen must not throw the list away and build it again.
    ///
    /// That is what the flash on every undo was: clear every node, recreate every node, then put the
    /// selection and the scroll position back - and each of those two restores scrolls the list. Flicker
    /// cannot be seen in a screenshot, so it is measured here instead, as rows built and repaints taken.</summary>
    /// <summary>Making a new filter: where it lands, that it can always be asked for, and that it is on
    /// screen and selected the moment it exists.</summary>
    private static bool RunNewFilterChecks()
    {
        Line("-- adding a filter --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_newfilter_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, string.Concat(Enumerable.Range(0, 200).Select(i => $"line {i}\n")), new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        bool ok;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();

            var tree = new FilterTreeControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(320, 400),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(tree);
            tree.Attach(doc);
            host.Show();
            Pump();
            FitTreeToRows(host, tree, 12);

            // ---- where a new filter goes ----
            var filters = new FilterCollection();
            var roots = new List<Filter>();
            for (int i = 0; i < 40; i++)
            {
                var f = new Filter { Enabled = true, Match = new FilterMatch { Text = $"filter {i:00}" } };
                filters.Roots.Add(f);
                roots.Add(f);
            }
            var kid = new Filter { Match = new FilterMatch { Text = "kid" } };
            filters.Roots.Add(kid);
            filters.Move(kid, roots[3], 0);
            doc.SetFilters(filters);
            tree.Rebuild();
            Pump();

            ok = Check("with nothing to go below, the preference sends it to the top",
                       MainForm.NewFilterIndex(addAtTop: true, after: null, filters) == 0);
            ok &= Check("or to the end",
                        MainForm.NewFilterIndex(addAtTop: false, after: null, filters) == -1);
            ok &= Check("below a filter means the next place among its own siblings",
                        MainForm.NewFilterIndex(addAtTop: true, after: roots[3], filters) == 4);
            ok &= Check("and asking for below beats the preference",
                        MainForm.NewFilterIndex(addAtTop: false, after: roots[3], filters) == 4);
            ok &= Check("below a nested filter counts among ITS siblings, not the roots",
                        MainForm.NewFilterIndex(addAtTop: true, after: kid, filters) == 1);

            // ---- somewhere to double-click ----
            // The list cannot be made to scroll past its last filter: a native tree clamps that, MEASURED.
            // The blank space it leaves at the bottom is only the remainder of the pane's height, so with
            // room to spare there is a whole row of it and with the filters filling the pane there is none.
            int rowH = tree.RowHeightForTesting;
            var few = new FilterCollection();
            for (int i = 0; i < 3; i++)
                few.Roots.Add(new Filter { Enabled = true, Match = new FilterMatch { Text = $"few {i}" } });
            doc.SetFilters(few);
            tree.Rebuild();
            Pump();
            ok &= Check($"a list with room to spare has blank space of its own to aim at " +
                        $"({tree.TreeHeightForTesting - few.Roots.Count * rowH} px)",
                        tree.TreeHeightForTesting - few.Roots.Count * rowH >= rowH);

            int asked = 0;
            Filter? askedParent = null;
            void CountAdds(Filter? p) { asked++; askedParent = p; }
            tree.AddRequested += CountAdds;
            tree.RaiseDoubleClickEventForTesting(new Point(tree.TreeWidthForTesting / 2, tree.TreeHeightForTesting - 2));
            Pump();
            ok &= Check($"double-clicking it asks for a new top-level filter (raised {asked})",
                        asked == 1 && askedParent is null);
            tree.AddRequested -= CountAdds;

            // Opening the search bar makes the list shorter; closing it gives every pixel back.
            doc.SetFilters(filters);
            tree.Rebuild();
            Pump();
            int listBefore = tree.TreeAreaForTesting.Height;
            tree.ShowSearch();
            Pump();
            int listOpen = tree.TreeAreaForTesting.Height;
            tree.HideSearch();
            Pump();
            ok &= Check($"opening the search bar takes room from the list ({listBefore} -> {listOpen})",
                        listOpen < listBefore);
            ok &= Check($"and closing it gives all of it back ({listOpen} -> {tree.TreeAreaForTesting.Height})",
                        tree.TreeAreaForTesting.Height == listBefore);

            // ---- what the list's own menu offers ----
            Filter? below = null;
            void NoteBelow(Filter f) => below = f;
            tree.AddBelowRequested += NoteBelow;
            asked = 0;
            tree.AddRequested += CountAdds;

            var menu = tree.FilterMenuForTesting;
            var addItem = (ToolStripMenuItem)menu.Items[0];
            tree.ScrollToForTesting(roots[0]);
            Pump();
            tree.ClickFilterForTesting(roots[2], button: MouseButtons.Right);
            tree.OpenFilterMenuForTesting();
            ok &= Check($"right-clicking a filter offers to add one below it [{addItem.Text}]",
                        addItem.Text == "Add Filter Below\u2026");
            addItem.PerformClick();
            ok &= Check("and that is the filter it means", ReferenceEquals(below, roots[2]));
            ok &= Check("without asking for a plain one as well", asked == 0);

            below = null;
            tree.MouseDownForTesting(new Point(20, tree.TreeAreaForTesting.Height - 2), button: MouseButtons.Right);
            tree.OpenFilterMenuForTesting();
            // The fixture fills the list, so the point above is a row - what matters is the empty case, and
            // the only place that is certain to be empty is the blank strip. Ask about no row at all.
            tree.MouseDownForTesting(new Point(20, tree.TreeAreaForTesting.Height + rowH), button: MouseButtons.Right);
            tree.OpenFilterMenuForTesting();
            ok &= Check($"right-clicking clear of every filter offers a plain one [{addItem.Text}]",
                        addItem.Text == "Add Filter\u2026");
            addItem.PerformClick();
            ok &= Check("and asks for it at the top level", asked == 1 && askedParent is null);
            ok &= Check("not below anything", below is null);
            tree.AddRequested -= CountAdds;
            tree.AddBelowRequested -= NoteBelow;

            // ---- a new filter is on screen and selected ----
            // A row scrolled out of the list still reports a rectangle - with a top above the list, or below
            // its bottom - so "on screen" has to be read as overlapping the list, not as having bounds.
            bool OnScreen(Filter f)
            {
                var b = tree.RowBoundsForTesting(f);
                return !b.IsEmpty && b.Bottom > 0 && b.Top < tree.TreeHeightForTesting;
            }
            tree.ScrollToForTesting(roots[30]);
            Pump();
            ok &= Check($"a filter scrolled out of the list really is out of sight " +
                        $"({tree.RowBoundsForTesting(roots[0])})", !OnScreen(roots[0]));
            tree.RevealFilter(roots[0]);
            Pump();
            ok &= Check("revealing it brings it back into view", OnScreen(roots[0]));
            ok &= Check($"and selects it, so F4 acts on it at once [{string.Join(" ", tree.SelectedNamesForTesting)}]",
                        tree.SelectedNamesForTesting is [var only] && only == roots[0].Match.ToDisplayString());
            ok &= Check("and it is the current row too",
                        ReferenceEquals(tree.SelectedFilter, roots[0]));
        }
        finally
        {
            host?.Dispose();
            doc.Dispose();
            try { File.Delete(path); } catch { /* best effort */ }
        }

        return ok;
    }

    private static bool RunFilterSyncChecks()
    {        Line("-- keeping the filter list still --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_sync_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, string.Concat(Enumerable.Range(0, 200).Select(i => $"line {i}\n")), new UTF8Encoding(false));

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
                ClientSize = new Size(300, 300),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(tree);
            tree.Attach(doc);
            host.Show();
            Pump();

            // Long enough to scroll, and nested, so the sync has more than one level to walk.
            var filters = new FilterCollection();
            for (int i = 0; i < 60; i++)
            {
                var f = new Filter { Enabled = i % 3 == 0, Match = new FilterMatch { Text = $"filter {i:00}" } };
                filters.Roots.Add(f);
                if (i % 10 == 5)
                    for (int k = 0; k < 2; k++)
                    {
                        var kid = new Filter { Match = new FilterMatch { Text = $"kid {i:00}.{k}" } };
                        filters.Roots.Add(kid);
                        filters.Move(kid, f, f.Children.Count);
                    }
            }
            doc.SetFilters(filters);
            tree.Rebuild();
            Pump();

            var history = new FilterHistory();
            var target = filters.Roots[30];
            tree.SelectForTesting(target);
            tree.ScrollToForTesting(filters.Roots[25]);
            Pump();

            var nodesBefore = filters.Roots.Select(tree.NodeForTesting).ToArray();
            var topBefore = tree.TopFilterForTesting;
            int builtBefore = tree.NodesBuiltForTesting;
            int paintsBefore = tree.PaintsForTesting;

            // An edit and an undo of it: exactly what the user does.
            history.Begin("Edit Filter", filters);
            target.Match.Text = "filter 30 changed";
            history.Commit(filters);
            tree.SyncToModel();
            Pump();
            bool ok = Check("an edit shows up in the list", tree.NodeForTesting(filters.Roots[30])?.Text == "filter 30 changed",
                            tree.NodeForTesting(filters.Roots[30])?.Text ?? "(gone)");
            ok &= Check("without building a single new row", tree.NodesBuiltForTesting == builtBefore,
                        $"{tree.NodesBuiltForTesting - builtBefore} built");

            history.Undo(filters);
            tree.SyncToModel();
            Pump();
            ok &= Check("undo puts the text back", tree.NodeForTesting(filters.Roots[30])?.Text == "filter 30",
                        tree.NodeForTesting(filters.Roots[30])?.Text ?? "(gone)");
            ok &= Check("and still builds nothing", tree.NodesBuiltForTesting == builtBefore,
                        $"{tree.NodesBuiltForTesting - builtBefore} built");
            ok &= Check("the very same rows are still there",
                        filters.Roots.Take(60).Select(tree.NodeForTesting)
                               .Zip(nodesBefore, (a, b) => ReferenceEquals(a, b)).All(x => x));
            ok &= Check("the list has not scrolled", ReferenceEquals2(tree.TopFilterForTesting, topBefore, filters),
                        $"{tree.TopFilterForTesting?.Match.Text} (was {topBefore?.Match.Text})");
            ok &= Check("and the selection is where it was", tree.SelectedFilter?.Match.Text == "filter 30",
                        tree.SelectedFilter?.Match.Text ?? "(none)");
            int paints = tree.PaintsForTesting - paintsBefore;
            ok &= Check("two edits cost a handful of repaints, not a rebuild's worth", paints <= 8, $"{paints} repaints");

            // A rebuild is the thing being avoided: it must look measurably different, or the check above
            // is measuring nothing.
            builtBefore = tree.NodesBuiltForTesting;
            tree.Rebuild();
            Pump();
            ok &= Check("(and a full rebuild really does build them all again)",
                        tree.NodesBuiltForTesting - builtBefore >= 60, $"{tree.NodesBuiltForTesting - builtBefore} built");

            // Structure, not just text: an undo that puts a removed filter back, and one that reorders.
            tree.Rebuild();
            Pump();
            int rows = tree.RowCountForTesting;
            builtBefore = tree.NodesBuiltForTesting;
            history.Begin("Remove Filter", filters);
            filters.Remove(filters.Roots[10]);
            history.Commit(filters);
            tree.SyncToModel();
            Pump();
            ok &= Check("removing a filter drops exactly its row", tree.RowCountForTesting == rows - 1,
                        $"{tree.RowCountForTesting} rows, was {rows}");
            ok &= Check("and builds nothing to do it", tree.NodesBuiltForTesting == builtBefore,
                        $"{tree.NodesBuiltForTesting - builtBefore} built");

            history.Undo(filters);
            tree.SyncToModel();
            Pump();
            ok &= Check("undoing the removal puts one row back", tree.RowCountForTesting == rows,
                        $"{tree.RowCountForTesting} rows, was {rows}");
            ok &= Check("building exactly one row to do it", tree.NodesBuiltForTesting - builtBefore == 1,
                        $"{tree.NodesBuiltForTesting - builtBefore} built");
            ok &= Check("in its old place",
                        Array.IndexOf(tree.RowOrderForTesting, "filter 10") == Array.IndexOf(tree.RowOrderForTesting, "filter 09") + 1,
                        string.Join(",", tree.RowOrderForTesting.Skip(9).Take(4)));

            builtBefore = tree.NodesBuiltForTesting;
            history.Begin("Move Filter", filters);
            filters.Reorder(filters.Roots[3], +1);
            history.Commit(filters);
            tree.SyncToModel();
            Pump();
            ok &= Check("reordering moves a row rather than remaking one",
                        tree.NodesBuiltForTesting == builtBefore && tree.RowOrderForTesting[3].Contains("filter 04"),
                        $"{tree.NodesBuiltForTesting - builtBefore} built; {string.Join(",", tree.RowOrderForTesting.Take(6))}");

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

    /// <summary>Same filter, allowing for the fact that undo hands back a fresh object with the same id.</summary>
    private static bool ReferenceEquals2(Filter? a, Filter? b, FilterCollection _)
        => a is null ? b is null : b is not null && a.Id == b.Id;

    /// <summary>The suggested colours have one job each: to be readable, and to not be a colour some other
    /// filter is already wearing. A near-miss is worse than a repeat - two filters you cannot tell apart are
    /// two you will confuse without noticing.</summary>
    private static bool RunLuckyColorChecks()
    {
        Line("-- suggested filter colours --");

        bool ok = Check("there are enough of them for a filter file of hundreds", LuckyColors.Count >= 100,
                        LuckyColors.Count.ToString());

        double worstContrast = double.MaxValue;
        int worstAt = -1;
        for (int i = 0; i < LuckyColors.Count; i++)
        {
            var p = LuckyColors.At(i);
            double c = LuckyColors.Contrast(p.Fore, p.Back);
            if (c < worstContrast) { worstContrast = c; worstAt = i; }
        }
        ok &= Check("every pair is readable, by the ratio and not by eye", worstContrast >= 4.5,
                    $"worst is {worstContrast:0.0}:1 at {worstAt}");

        // No two identical entries: a set that repeats itself would hand back a colour it had already
        // offered while claiming to have moved on.
        int duplicates = LuckyColors.Count -
                         Enumerable.Range(0, LuckyColors.Count).Select(i => LuckyColors.At(i).Back).Distinct().Count();
        ok &= Check("and no two entries are the same colour", duplicates == 0, $"{duplicates} repeats");

        // The whole point of packing the set offline: NOTHING in it looks like anything else in it. This is
        // the check the old weighted-RGB metric passed while the palette visibly held duplicates.
        double closest = double.MaxValue;
        var alike = (a: 0, b: 0);
        for (int i = 0; i < LuckyColors.Count; i++)
            for (int j = i + 1; j < LuckyColors.Count; j++)
            {
                double d = LuckyColors.Distance(LuckyColors.At(i).Back, LuckyColors.At(j).Back);
                if (d < closest) { closest = d; alike = (i, j); }
            }
        ok &= Check("and no two of them look alike", closest >= 11,
                    $"#{LuckyColors.At(alike.a).Back.ToHex()} and #{LuckyColors.At(alike.b).Back.ToHex()} " +
                    $"are {closest:0.0} apart");

        // Consecutive PRESSES, which walk the set by a stride - not neighbours in it, which are sorted by
        // hue and so are meant to be similar.
        var loner = new Filter();
        double presses = double.MaxValue;
        int step = -1;
        for (int i = 0; i < LuckyColors.Count; i++)
        {
            int then = LuckyColors.Next(step, Array.Empty<Filter>(), loner);
            if (i > 0) presses = Math.Min(presses, LuckyColors.Distance(LuckyColors.At(step).Back, LuckyColors.At(then).Back));
            step = then;
        }
        ok &= Check("consecutive presses give visibly different colours", presses > 20,
                    $"nearest two in a row are {presses:0.0} apart");

        // Two presses apart matters as much: the button is pressed until something is liked, so a run of
        // three must not go there and back.
        double twoApart = double.MaxValue;
        for (int i = 0; i < LuckyColors.Count; i++)
        {
            int one = LuckyColors.Next(i, Array.Empty<Filter>(), loner);
            int two = LuckyColors.Next(one, Array.Empty<Filter>(), loner);
            twoApart = Math.Min(twoApart, LuckyColors.Distance(LuckyColors.At(i).Back, LuckyColors.At(two).Back));
        }
        ok &= Check("and so do the ones two presses apart", twoApart > 12, $"nearest are {twoApart:0.0} apart");

        // Colours already in use are skipped.
        var mine = new Filter();
        var taken = new List<Filter>();
        for (int i = 0; i < 3; i++)
            taken.Add(new Filter { Style = { Background = LuckyColors.At(i + 1).Back } });

        int at = LuckyColors.Next(0, taken, mine);
        ok &= Check("a colour another filter is using is passed over",
                    taken.All(f => f.Style.Background != LuckyColors.At(at).Back),
                    $"offered {LuckyColors.At(at).Back}");

        // ...and pressing again keeps moving rather than settling on one.
        var seen = new List<RgbColor>();
        int cursor = -1;
        for (int i = 0; i < 5; i++) { cursor = LuckyColors.Next(cursor, taken, mine); seen.Add(LuckyColors.At(cursor).Back); }
        ok &= Check("and every press offers something new", seen.Distinct().Count() == seen.Count,
                    string.Join(" ", seen));

        // A filter list the size of a real one, none of it enabled, and every press still has to find
        // something none of them is wearing. Disabled filters keep their colours and will be switched back
        // on, so they count exactly as much as enabled ones.
        var many = new List<Filter>();
        for (int i = 0; i < 60; i++)
            many.Add(new Filter { Enabled = false, Style = { Background = LuckyColors.At(i * 2).Back } });
        var offered = new List<RgbColor>();
        int walk = -1;
        for (int i = 0; i < 20; i++) { walk = LuckyColors.Next(walk, many, mine); offered.Add(LuckyColors.At(walk).Back); }
        double nearestTaken = offered.Min(o => many.Min(f => LuckyColors.Distance(f.Style.Background!.Value, o)));
        ok &= Check("with sixty filters coloured it still finds room",
                    nearestTaken > 11 && offered.Distinct().Count() == offered.Count,
                    $"nearest offered is {nearestTaken:0.0} from one in use, {offered.Distinct().Count()} of 20 distinct");

        // Down to almost nothing acceptable it still has to keep moving, not stick on one colour.
        var crowded = new List<Filter>();
        for (int i = 0; i < LuckyColors.Count - 2; i++)
            crowded.Add(new Filter { Style = { Background = LuckyColors.At(i).Back } });
        int a = LuckyColors.Next(0, crowded, mine);
        int b = LuckyColors.Next(a, crowded, mine);
        ok &= Check("and keeps moving even when barely anything is free",
                    LuckyColors.At(a).Back != LuckyColors.At(b).Back,
                    $"{LuckyColors.At(a).Back} then {LuckyColors.At(b).Back}");

        // With every colour but one spoken for it must offer that one - not simply the next along, which
        // would be a plain duplicate of a colour already on screen.
        const int roomy = 55;
        var all = new List<Filter>();
        for (int i = 0; i < LuckyColors.Count; i++)
            if (i != roomy) all.Add(new Filter { Style = { Background = LuckyColors.At(i).Back } });
        int fallback = ((LuckyColors.Next(3, all, mine) % LuckyColors.Count) + LuckyColors.Count) % LuckyColors.Count;
        ok &= Check("with almost nothing left it finds the one colour nobody is wearing",
                    fallback == roomy,
                    $"offered {fallback} ({LuckyColors.At(fallback).Back}), the free one is {roomy}");

        // Every colour but the one on offer taken - the case a long filter list actually produces. That one
        // always looks free, because the filter being edited is not counted as using anything, so a full
        // turn round the ring lands straight back on it and the same colour comes up for ever.
        var allButOne = new List<Filter>();
        for (int i = 1; i < LuckyColors.Count; i++)
            allButOne.Add(new Filter { Style = { Background = LuckyColors.At(i).Back } });
        int p1 = LuckyColors.Next(0, allButOne, mine);
        int p2 = LuckyColors.Next(p1, allButOne, mine);
        ok &= Check("and never offers back the colour it is already on",
                    LuckyColors.At(p1).Back != LuckyColors.At(0).Back &&
                    LuckyColors.At(p2).Back != LuckyColors.At(p1).Back,
                    $"on {LuckyColors.At(0).Back}: offered {LuckyColors.At(p1).Back} then {LuckyColors.At(p2).Back}");

        // The dialog wires it to both colours at once - a background with no matching text colour is how a
        // filter ends up unreadable.
        var target = new Filter();
        using (var dlg = new FilterEditDialog(target, isNew: true, taken))
        {
            dlg.FeelLuckyForTesting();
            var (fore, back) = dlg.ColorsForTesting;
            ok &= Check("the button sets a text colour as well as a background",
                        LuckyColors.Contrast(fore, back) >= 4.5, $"{fore} on {back}");
            var first = back;
            dlg.FeelLuckyForTesting();
            ok &= Check("and pressing it again changes them", dlg.ColorsForTesting.Back != first,
                        dlg.ColorsForTesting.Back.ToString());
        }

        // The reported bug: take a colour from the button, keep it, come back - and the first press offered
        // the colour the filter was already wearing, so nothing happened until it was pressed twice.
        var kept = new Filter();
        RgbColor saved;
        using (var dlg = new FilterEditDialog(kept, isNew: true, taken))
        {
            dlg.FeelLuckyForTesting();
            dlg.SaveForTesting();
            saved = kept.Style.Background!.Value;
        }
        using (var dlg = new FilterEditDialog(kept, isNew: false, taken))
        {
            dlg.FeelLuckyForTesting();
            ok &= Check("editing a filter again, one press moves off the colour it already has",
                        dlg.ColorsForTesting.Back != saved,
                        $"had {saved}, offered {dlg.ColorsForTesting.Back}");
        }

        // And it is only kept back until the ring has been all the way round, not dropped from it.
        var wearing = LuckyColors.At(7);
        var walker = new Filter { Style = { Background = wearing.Back, Foreground = wearing.Fore } };
        using (var dlg = new FilterEditDialog(walker, isNew: false, Array.Empty<Filter>()))
        {
            var offers = new List<RgbColor>();
            for (int i = 0; i < LuckyColors.Count; i++) { dlg.FeelLuckyForTesting(); offers.Add(dlg.ColorsForTesting.Back); }
            int back2 = offers.IndexOf(wearing.Back);
            ok &= Check($"and comes back to it only after the whole ring " +
                        $"(press {back2 + 1} of {LuckyColors.Count})", back2 == LuckyColors.Count - 1);
            ok &= Check($"offering every other colour exactly once on the way ({offers.Distinct().Count()})",
                        offers.Distinct().Count() == LuckyColors.Count);
        }
        return ok;
    }

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
                        ReferenceEquals(tree.DoubleClickForTesting(new Point(row.Left + 2, mid)).Edit, other));
            ok &= Check("so does double-clicking the empty space out to its right",
                        ReferenceEquals(tree.DoubleClickForTesting(new Point(tree.TreeWidthForTesting - 4, mid)).Edit, other));
            ok &= Check("double-clicking the checkbox does not",
                        tree.DoubleClickForTesting(new Point(row.Left - 2, mid)).Edit is null);
            ok &= Check("nor does double-clicking left of it",
                        tree.DoubleClickForTesting(new Point(0, mid)).Edit is null);

            // ---- double-clicking below the last filter ----
            // The list is 5 filters in a pane with room for far more, so there is real empty space under it.
            int lastBottom = tree.VisibleFiltersForTesting.Select(f => tree.RowBoundsForTesting(f).Bottom).Max();
            int belowY = lastBottom + tree.RowHeightForTesting;
            ok &= Check($"there is empty space under the last filter to aim at ({belowY} of {tree.TreeHeightForTesting})",
                        belowY < tree.TreeHeightForTesting);
            var below = tree.DoubleClickForTesting(new Point(tree.TreeWidthForTesting / 2, belowY));
            ok &= Check("double-clicking below the last filter asks for a new one", below.Add);
            ok &= Check("and does not also ask to edit one", below.Edit is null);
            ok &= Check("while double-clicking a filter asks only to edit it",
                        !tree.DoubleClickForTesting(new Point(row.Left + 2, mid)).Add);

            // Through the list's own event, not the seam: the empty part of the list is not a node, so the
            // tree's NodeMouseDoubleClick - where this used to be handled - never fires there.
            int asked = 0;
            void CountAdds(Filter? _) => asked++;
            tree.AddRequested += CountAdds;
            tree.RaiseDoubleClickEventForTesting(new Point(tree.TreeWidthForTesting / 2, belowY));
            Pump();
            tree.AddRequested -= CountAdds;
            ok &= Check($"the list's own double-click event asks for one too (raised {asked})", asked == 1);

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

            // Double-clicking a filter that has children means "edit this" and only that. The tree's own
            // answer is to fold the subtree, which left a double-click doing two unrelated things at once.
            // The expander is untouched by that: it is excluded by hit-test, and a single click on it is not
            // a double-click at all.
            var withKids = tree.RowBoundsForTesting(parent);
            int kidMid = withKids.Top + withKids.Height / 2;
            bool openBefore = tree.IsExpandedForTesting(parent);
            tree.SendDoubleClickOnlyForTesting(new Point(withKids.Left + 2, kidMid));
            Pump();
            ok &= Check($"double-clicking a filter with children does not fold it " +
                        $"({(openBefore ? "open" : "shut")} -> {(tree.IsExpandedForTesting(parent) ? "open" : "shut")})",
                        tree.IsExpandedForTesting(parent) == openBefore);

            // The tree grabs the mouse on a double-click to be sure of the button-up, but it raises
            // MouseDown first and ours opens the filter editor - so the up lands on a disabled window and
            // never arrives. Left holding the mouse, the list swallows the user's next click wherever it
            // was aimed, which reads as the log view ignoring the first click after cancelling the editor.
            ok &= Check("and leaves the mouse free for the next click", !tree.ListHoldsMouseForTesting);
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>Selecting several filters and acting on all of them at once.
    ///
    /// The fixture keeps one filter's children folded away on purpose: a range between two clicks has to
    /// mean the rows you can see, and a list flattened without regard to that would quietly take filters
    /// nobody pointed at.</summary>
    private static bool RunFilterSelectionChecks()
    {
        Line("-- selecting several filters --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_sel_" + Guid.NewGuid().ToString("N") + ".log");
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
                ClientSize = new Size(360, 520),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(tree);
            tree.Attach(doc);
            host.Show();
            Pump();

            // a  b[b1 b2]  c  d  e, with b folded shut.
            var filters = new FilterCollection();
            Filter New(string text, Filter? parent = null)
            {
                var f = new Filter { Match = new FilterMatch { Text = text } };
                filters.Add(f, parent);
                return f;
            }
            var a = New("a");
            var b = New("b");
            var b1 = New("b1", b);
            var b2 = New("b2", b);
            var c = New("c");
            var d = New("d");
            var e = New("e");
            doc.SetFilters(filters);
            tree.Rebuild();
            Pump();
            tree.CollapseForTesting(b);
            Pump();

            string Selected() => string.Join(" ", tree.SelectedNamesForTesting);
            string Current() => tree.CurrentFilterForTesting?.Match.Text ?? "-";

            bool ok = Check($"the fixture folds one filter away, so a range can prove it skips what is " +
                            $"hidden (rows: {string.Join(" ", tree.VisibleRowNamesForTesting)})",
                            !tree.IsExpandedForTesting(b) && tree.VisibleRowNamesForTesting.Length == 5);

            // ---- the mouse ----
            tree.ClickFilterForTesting(a);
            ok &= Check($"a plain click selects one filter [{Selected()}]", Selected() == "a");

            tree.ClickFilterForTesting(c, Keys.Control);
            ok &= Check($"ctrl+click adds to the selection [{Selected()}]", Selected() == "a c");
            tree.ClickFilterForTesting(c, Keys.Control);
            ok &= Check($"and ctrl+click again takes it back out [{Selected()}]", Selected() == "a");

            tree.ClickFilterForTesting(a);
            tree.ClickFilterForTesting(d, Keys.Shift);
            ok &= Check($"shift+click takes everything between, and nothing folded away inside it " +
                        $"[{Selected()}]", Selected() == "a b c d");
            tree.ClickFilterForTesting(c, Keys.Shift);
            ok &= Check($"a second shift+click measures from the same anchor, so the range shrinks " +
                        $"[{Selected()}]", Selected() == "a b c");

            tree.ClickFilterForTesting(e, Keys.Control);
            tree.ClickFilterForTesting(a, Keys.Control);
            ok &= Check($"ctrl+click moves the anchor, so a range after it starts there [{Selected()}]",
                        Selected() == "b c e");
            tree.ClickFilterForTesting(c, Keys.Shift);
            ok &= Check($"...like this [{Selected()}]", Selected() == "a b c");

            // A press inside the group must not throw the group away - that press may be the start of a
            // drag carrying all of it. It only means "just this one" once the button comes up.
            var row = tree.RowBoundsForTesting(b);
            tree.MouseDownForTesting(new Point(row.Left + 2, row.Top + row.Height / 2));
            ok &= Check($"pressing inside the group keeps it, so the whole group can be dragged " +
                        $"[{Selected()}]", Selected() == "a b c" && Current() == "b");
            tree.MouseUpForTesting();
            ok &= Check($"and releasing without dragging collapses it to that one [{Selected()}]",
                        Selected() == "b");

            // ---- the keyboard ----
            tree.ClickFilterForTesting(a);
            tree.PressKeyForTesting(Keys.Down | Keys.Shift);
            tree.PressKeyForTesting(Keys.Down | Keys.Shift);
            ok &= Check($"shift+down grows the selection down the list [{Selected()}]", Selected() == "a b c");
            tree.PressKeyForTesting(Keys.Up | Keys.Shift);
            ok &= Check($"shift+up shrinks it again [{Selected()}]", Selected() == "a b");

            tree.PressKeyForTesting(Keys.Down | Keys.Control);
            ok &= Check($"ctrl+down walks the current row and leaves the group alone " +
                        $"[{Selected()}], current {Current()}", Selected() == "a b" && Current() == "c");
            tree.PressKeyForTesting(Keys.Space | Keys.Control);
            ok &= Check($"ctrl+space adds the row it is standing on [{Selected()}]", Selected() == "a b c");

            tree.SelectForTesting(e);
            ok &= Check($"anything that moves the selection by itself collapses the group [{Selected()}]",
                        Selected() == "e");

            tree.PressCmdKeyForTesting(Keys.Control | Keys.A);
            ok &= Check($"ctrl+a takes every row you can see [{Selected()}]", Selected() == "a b c d e");

            // ---- enabling ----
            tree.ClickFilterForTesting(a);
            tree.ClickFilterForTesting(c, Keys.Control);
            tree.ClickFilterForTesting(e, Keys.Control);
            int changes = 0;
            tree.FiltersChanged += () => changes++;
            tree.ToggleCheckboxForTesting(c);
            ok &= Check($"ticking one of the group ticks all of it " +
                        $"[{string.Join(" ", filters.EnumerateDepthFirst().Where(f => f.Enabled).Select(f => f.Match.Text))}]",
                        a.Enabled && c.Enabled && e.Enabled && !b.Enabled && !d.Enabled && !b1.Enabled);
            ok &= Check($"and reports it once, not once per filter (raised {changes})", changes == 1);
            ok &= Check("the boxes show what is stored",
                        tree.IsCheckedForTesting(a) && tree.IsCheckedForTesting(e) && !tree.IsCheckedForTesting(d));

            changes = 0;
            tree.ClickFilterForTesting(d, onCheckbox: true);
            tree.ToggleCheckboxForTesting(d);
            ok &= Check($"ticking a filter outside the group is only ever itself " +
                        $"({(d.Enabled ? "on" : "off")}, group still {(a.Enabled ? "on" : "off")})",
                        d.Enabled && a.Enabled && changes == 1);
            ok &= Check($"...and it becomes the whole selection [{Selected()}]", Selected() == "d");

            // Shift on the checkbox still means the subtree, and must not be read as extending a range.
            tree.SetAllEnabled(false);
            tree.ClickFilterForTesting(a);
            tree.ClickFilterForTesting(b, Keys.Control);
            tree.ToggleCheckboxForTesting(b, shift: true);
            ok &= Check($"shift on a checkbox takes the subtrees of the whole group, and does not extend it " +
                        $"[{Selected()}]",
                        Selected() == "a b" && a.Enabled && b.Enabled && b1.Enabled && b2.Enabled && !c.Enabled);

            // ---- removing ----
            tree.ClickFilterForTesting(a);
            tree.ClickFilterForTesting(b, Keys.Control);
            tree.ClickFilterForTesting(c, Keys.Control);
            var labels = new List<string>();
            void Watch(string label) => labels.Add(label);
            tree.BeforeFiltersEdited += Watch;
            changes = 0;
            tree.PressKeyForTesting(Keys.Delete);
            tree.BeforeFiltersEdited -= Watch;
            string left = string.Join(" ", filters.EnumerateDepthFirst().Select(f => f.Match.Text));
            ok &= Check($"delete takes the whole group, children and all [{left}]", left == "d e");
            ok &= Check($"as one thing to undo, named for what it did [{string.Join(", ", labels)}]",
                        labels.Count == 1 && labels[0] == "Remove 3 Filters");
            ok &= Check($"and reports one change (raised {changes})", changes == 1);
            ok &= Check($"whatever moved up into its place is selected, so Delete can be pressed again " +
                        $"[{Selected()}]", Selected() == "d");

            // ---- the search must never leave a group selected out of sight ----
            tree.ClickFilterForTesting(d);
            tree.ClickFilterForTesting(e, Keys.Control);
            tree.SetSearchText("e");
            tree.PressSearchKeyForTesting(Keys.Enter);
            ok &= Check($"jumping to a searched-for filter selects just that one [{Selected()}]",
                        Selected() == "e");
            tree.HideSearch();
            Pump();

            // ---- what it looks like ----
            var painted = new FilterCollection();
            var own = new RgbColor(0xFF, 0xEB, 0xB4);
            Filter Add(string text)
            {
                var f = new Filter { Match = new FilterMatch { Text = text }, Style = { Background = own } };
                painted.Add(f);
                return f;
            }
            var p1 = Add("p1"); var p2 = Add("p2"); var p3 = Add("p3"); var p4 = Add("p4");
            doc.SetFilters(painted);
            tree.Rebuild();
            Pump();
            tree.ClickFilterForTesting(p1);
            tree.ClickFilterForTesting(p3, Keys.Shift);
            Pump();

            var area = tree.TreeAreaForTesting;
            using var shot = Capture(host);
            Color Pixel(int x, int y)
            {
                int hx = area.Left + x, hy = area.Top + y;
                return hx < 0 || hy < 0 || hx >= shot.Width || hy >= shot.Height ? Color.Transparent : shot.GetPixel(hx, hy);
            }
            int Rule(int y)
            {
                int n = 0;
                for (int x = 0; x < tree.TreeWidthForTesting; x++)
                {
                    var px = Pixel(x, y);
                    if (px.R == SystemColors.Highlight.R && px.G == SystemColors.Highlight.G &&
                        px.B == SystemColors.Highlight.B) n++;
                }
                return n;
            }
            var r1 = tree.RowBoundsForTesting(p1);
            var r2 = tree.RowBoundsForTesting(p2);
            var r3 = tree.RowBoundsForTesting(p3);
            var r4 = tree.RowBoundsForTesting(p4);
            int wide = tree.TreeWidthForTesting / 2;
            int top = Rule(r1.Top), inside = Rule(r2.Top), below = Rule(r4.Top);
            ok &= Check($"a run of selected filters is drawn as one box: a line across the top ({top}px)",
                        top > wide);
            ok &= Check($"...none between the rows inside it ({inside}px)", inside <= 4);
            ok &= Check($"...and none below the last of them ({below}px)", below <= 4);

            // The strip left of the text is the only part of a row the filter's own colours do not own, so
            // that is where being selected has to show. How blue it is says which of the three states a row
            // is in - and the tint has to be read off a pixel, since it is a wash over what is already there.
            int Blueness(Rectangle row) => Pixel(3, row.Top + row.Height / 2) is var px ? px.B - px.R : 0;
            int plain = Blueness(r4), inGroup = Blueness(r1), cursor = Blueness(r3);
            ok &= Check($"an unselected filter's strip is left alone (blue {plain})", plain < 12);
            ok &= Check($"a selected one is tinted (blue {inGroup})", inGroup > 25);
            ok &= Check($"and the row the keyboard is on is tinted harder still " +
                        $"({cursor} against {inGroup})", cursor > inGroup + 25);

            // ...and it stops where the filter's own colours start, or selecting a filter would misreport
            // the very thing the list is there to show.
            var kept = Pixel(tree.TreeWidthForTesting - 3, r3.Top + r3.Height / 2);
            ok &= Check($"the filter's own colour is not washed over " +
                        $"(#{kept.R:X2}{kept.G:X2}{kept.B:X2} against #{own.R:X2}{own.G:X2}{own.B:X2})",
                        kept.R == own.R && kept.G == own.G && kept.B == own.B);
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>Changing the appearance of several filters at once. The claim that matters most is the
    /// negative one: pressing OK must not write a pattern, a description or a kind onto anything, since one
    /// box cannot stand for what several filters match.</summary>
    private static bool RunAppearanceChecks()
    {
        Line("-- appearance of several filters --");
        var red = new RgbColor(255, 0, 0);
        var blue = new RgbColor(0, 0, 255);
        var green = new RgbColor(0, 128, 0);

        Filter Make(string text, RgbColor? fore, RgbColor? back, bool? bold) => new()
        {
            Description = text + " description",
            Kind = FilterKind.Exclude,
            Match = { Type = FilterMatchType.Text, Text = text, Regex = true, CaseSensitive = true },
            Style = { Foreground = fore, Background = back, Bold = bold }
        };

        // Two agree on everything; the third has a different text colour and is not bold.
        var f1 = Make("alpha", red, blue, true);
        var f2 = Make("beta", red, blue, true);
        var f3 = Make("gamma", green, blue, false);
        var all = new List<Filter> { f1, f2, f3 };
        var defaults = new ResolvedStyle(new RgbColor(0, 0, 0), new RgbColor(255, 255, 255), false, false);

        using var dlg = new AppearanceDialog(all, all, defaults);
        dlg.StartPosition = FormStartPosition.Manual;
        dlg.Location = new Point(0, 0);
        dlg.Opacity = 0;
        dlg.Show();
        Pump();

        var state = dlg.StateForTesting;
        var swatch = dlg.SwatchTextForTesting;
        bool ok = Check($"a colour they all share is offered back (background {state.Back})",
                        state.Back == CheckState.Checked);
        ok &= Check($"one they do not agree on says so instead [{swatch.Fore}]",
                    state.Fore == CheckState.Indeterminate && swatch.Fore == "varies");
        ok &= Check($"and the shared one shows its colour rather than a word [\"{swatch.Back}\"]",
                    swatch.Back.Length == 0);
        ok &= Check($"a style they disagree on starts on \"leave unchanged\" (bold choice {state.Bold})",
                    state.Bold == 0);
        ok &= Check($"and one none of them sets starts on \"inherit\" (italic choice {state.Italic})",
                    state.Italic == 3);
        ok &= Check($"...as does underline (choice {state.Underline})", state.Underline == 3);

        var untouched = dlg.ReadForTesting();
        ok &= Check("opening it and pressing OK changes nothing at all",
                    !untouched.ApplyTo(f1) && !untouched.ApplyTo(f3));

        // Now ask for one text colour across all three, and turn bold off everywhere.
        dlg.SetColorStateForTesting(foreground: true, CheckState.Checked, green);
        dlg.SetFlagForTesting(bold: true, StyleEdit.Set, false);
        dlg.SetUnderlineForTesting(StyleEdit.Set, true);
        dlg.ApplyForTesting();
        Pump();
        var change = dlg.Change;
        foreach (var f in all) change.ApplyTo(f);

        ok &= Check($"the colour that was set lands on every one of them " +
                    $"[{string.Join(" ", all.Select(f => f.Style.Foreground?.ToString() ?? "-"))}]",
                    all.All(f => f.Style.Foreground == green));
        ok &= Check("so does the style", all.All(f => f.Style.Bold == false));
        ok &= Check("and underline, which is a style like the others", all.All(f => f.Style.Underline == true));
        ok &= Check($"what was left alone is still each filter's own " +
                    $"[{string.Join(" ", all.Select(f => f.Style.Background?.ToString() ?? "-"))}]",
                    all.All(f => f.Style.Background == blue) && all.All(f => f.Style.Italic is null));
        string patterns = string.Join(" ", all.Select(f => f.Match.Text));
        ok &= Check($"and nothing that is not a style was touched [{patterns}]",
                    patterns == "alpha beta gamma"
                    && all.All(f => f.Description.EndsWith(" description", StringComparison.Ordinal))
                    && all.All(f => f.Kind == FilterKind.Exclude && f.Match.Regex && f.Match.CaseSensitive));

        dlg.Close();
        Pump();
        return ok;
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
            // to be at least as big as what it holds. A fixed size would clip here first. A dialog that can
            // be RESIZED is exempt: its content is a minimum it grows past, and a wrapping row measured
            // against no width at all answers with everything stacked one item to a line.
            var content = dlg.Controls.Count > 0 ? dlg.Controls[0].PreferredSize : Size.Empty;
            good &= Check($"{name}: nothing is clipped at this DPI " +
                          $"(frame {dlg.ClientSize.Width}x{dlg.ClientSize.Height}, content {content.Width}x{content.Height})",
                          dlg.FormBorderStyle == FormBorderStyle.Sizable ||
                          (dlg.ClientSize.Width >= content.Width && dlg.ClientSize.Height >= content.Height));

            dlg.Close();
            dlg.Dispose();
            Pump();
            return good;
        }

        var filter = new Filter { Match = { Text = "sample text" } };
        ok &= CheckDialog("filter", new FilterEditDialog(filter, isNew: true),
                          "&Matches text", "Mar&ked by marker", "&Text:", "&Description:");

        var group = new List<Filter> { new() { Match = { Text = "one" } }, new() { Match = { Text = "two" } } };
        ok &= CheckDialog("appearance", new AppearanceDialog(group, group,
                              new ResolvedStyle(new RgbColor(0, 0, 0), new RgbColor(255, 255, 255), false, false)),
                          "Text col&or", "&Background", "Bo&ld", "&Italic");

        var fields = new ColumnSpec { Enabled = true, Template = "{[*]}{[*]} {*}" };
        fields.Reset();
        ok &= CheckDialog("fields", new ColumnsDialog(fields, ["[09:31][INFO] hello there", "[09:32][WARN] and again"]),
                          "&Template", "&Detect", "&Layout", "&Columns", "&Inline", "&Fields", "Move &up", "Move d&own");

        // Those keys are pressed while writing the pattern, so they must not take the keyboard out of the
        // box - the same rule the find bar's two options follow.
        using (var opts = new FilterEditDialog(new Filter { Match = { Text = "sample text" } }, isNew: true))
        {
            opts.StartPosition = FormStartPosition.Manual;
            opts.Location = new Point(0, 0);
            opts.Opacity = 0;
            opts.Show();
            Pump();
            opts.FocusTextForTesting(3, 4);
            Pump();
            ok &= Check("the pattern box has the keyboard to start with", opts.TextHasFocusForTesting);
            ok &= Check("and a caret and selection worth keeping", opts.TextSelectionForTesting == (3, 4),
                        opts.TextSelectionForTesting.ToString());

            foreach (char key in "RCEOBLI")
            {
                AltKey(opts, key);
                Pump();
            }
            ok &= Check("ticking the options leaves the keyboard in the box", opts.TextHasFocusForTesting);
            ok &= Check("and the caret and selection where they were",
                        opts.TextSelectionForTesting == (3, 4), opts.TextSelectionForTesting.ToString());
            ok &= Check("and what is inherited is still explained",
                        opts.NoteForTesting.Contains("parent filter", StringComparison.Ordinal),
                        opts.NoteForTesting);

            // A strip that wraps answers for a narrower width than it is then given, so its row reserves
            // lines that are never drawn - which is where the band of empty space down this dialog came
            // from. Every strip should be exactly as tall as the tallest thing in it.
            int spare = AllControls(opts).OfType<FlowLayoutPanel>()
                .Select(s => s.Height - s.Controls.Cast<Control>()
                                         .Select(c => c.Height + c.Margin.Vertical).DefaultIfEmpty(0).Max())
                .DefaultIfEmpty(0).Max();
            ok &= Check("no row of the filter dialog reserves space it never draws", spare <= 0, $"{spare}px over");

            // A tick and the swatch it owns must read as one thing, and the next pair as another: with the
            // gaps the other way round the eye binds a swatch to whatever follows it.
            var appearance = AllControls(opts).OfType<FlowLayoutPanel>()
                             .OrderByDescending(s => s.Controls.Count).First();
            var strip = appearance.Controls.Cast<Control>().OrderBy(c => c.Left).ToList();
            int[] gaps = [.. strip.Zip(strip.Skip(1), (a, b) => b.Left - a.Right)];
            ok &= Check("a swatch sits closer to its own tick than to what comes next",
                        gaps.Length >= 4 && gaps[0] < gaps[1] && gaps[2] < gaps[3],
                        string.Join(", ", gaps));

            // The swatch buttons are taller than the captions beside them, so the row's alignment is a
            // question of centres, not of tops.
            int mid = appearance.Height / 2;
            int worst = strip.Max(c => Math.Abs(c.Top + c.Height / 2 - mid));
            ok &= Check("and every caption is centred against them", worst <= 1,
                        $"{worst}px off centre in a {appearance.Height}px row");

            // The pattern box holds a whole log line copied in from the text, so it gets what is left after
            // the labels - not a share of it.
            ok &= Check("the pattern box runs the width of the dialog",
                        opts.PatternWidthForTesting > opts.ClientSize.Width * 3 / 4,
                        $"box {opts.PatternWidthForTesting} of {opts.ClientSize.Width}");

            opts.Close();
            Pump();
        }

        // The find bar claims exactly two Alt keys, for its two options. It lives in a window that owns a
        // menu bar, so the letters it takes must be ones no top-level menu wants - otherwise Alt+R would
        // cycle between the two instead of ticking the box.
        var bar = new FindBar((_, _) => { });
        var claimed = AllControls(bar).Select(c => c.Text).Select(MnemonicOf).OfType<char>()
                                      .Select(char.ToUpperInvariant).OrderBy(c => c).ToList();
        ok &= Check("the find bar claims Alt+R and Alt+C, and nothing else [" + string.Join(", ", claimed) + "]",
                    string.Concat(claimed) == "CR");

        using (var probe = new MainForm(new AppSettings(), new MachineState(), []))
        {
            var menuKeys = (probe.MainMenuStrip?.Items.OfType<ToolStripMenuItem>() ?? [])
                .Select(i => MnemonicOf(i.Text ?? "")).OfType<char>()
                .Select(char.ToUpperInvariant).ToList();
            ok &= Check("and none of them is a menu's [" + string.Join(", ", menuKeys) + "]",
                        !claimed.Intersect(menuKeys).Any());

            // ...and they really work, through the same call WinForms makes for Alt+letter. The bar has to
            // be up for it: a hidden control takes no mnemonic, which is exactly the wanted behaviour.
            probe.StartPosition = FormStartPosition.Manual;
            probe.Location = new Point(0, 0);
            probe.Opacity = 0;
            probe.NoSavePrompt = true;
            probe.Show();
            Pump();
            probe.ClickMenuForTesting("Edit", "Find");
            Pump();
            var live = probe.FindBarForTesting;
            bool wasRegex = live.RegexIsOnForTesting, wasCase = live.CaseIsOnForTesting;
            ok &= Check("Alt+R is taken by the bar", AltKey(probe, 'R'));
            ok &= Check("and ticks the regex box", live.RegexIsOnForTesting != wasRegex);
            ok &= Check("Alt+C is taken by the bar", AltKey(probe, 'C'));
            ok &= Check("and ticks the case box", live.CaseIsOnForTesting != wasCase);

            // The options are reached mid-term, so ticking one must leave the box exactly as it was - it
            // still has the keyboard, and the caret and selection have not moved. The stock check box
            // selects itself when its Alt key is pressed, which loses all three.
            probe.Activate();
            live.FocusInput();
            live.SetTermForTesting("declined", 3, 2);
            Pump();
            var place = live.SelectionForTesting();
            ok &= Check($"the term box has the keyboard to start with (it is on {live.FocusedForTesting})",
                        live.TermBoxHasFocusForTesting);
            ok &= Check("and a caret and selection worth keeping", place == (3, 2), place.ToString());

            AltKey(probe, 'R');
            AltKey(probe, 'C');
            Pump();
            ok &= Check($"ticking the options leaves the keyboard in the box (it is on {live.FocusedForTesting})",
                        live.TermBoxHasFocusForTesting);
            ok &= Check("and the term untouched", live.TermForTesting() == "declined", live.TermForTesting());
            ok &= Check("and the caret and selection where they were",
                        live.SelectionForTesting() == place, $"{live.SelectionForTesting()} was {place}");
        }
        bar.Dispose();

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

        // The find bar's count arrives beside a term box and two checkboxes and must not shove any of them
        // along. It is hosted in a form here only because that is what the check drives.
        var findHost = new Form { ClientSize = new Size(900, 60) };
        var findBar = new FindBar((_, _) => { }) { Visible = true };
        findHost.Controls.Add(findBar);
        ok &= NothingShifts("find bar", findHost,
            () => findBar.SetMessage("Match 12 of 348 lines \u00b7 96 hidden \u00b7 891 of 1,204 hits"));

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
        var dlg = new FindBar((q, f) => searched.Add((q, f))) { Visible = true };
        var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Opacity = 0, ClientSize = new Size(900, 60) };
        host.Controls.Add(dlg);
        try
        {
            host.Show();
            Pump();

            dlg.SetTermForTesting("declined", 3, 2);
            var before = dlg.SelectionForTesting();
            bool ok = Check("the term and the place in it are set up", dlg.TermForTesting() == "declined" && before == (3, 2),
                            $"{dlg.TermForTesting()} at {before}");

            // Filling the drop-down used to reset the box, and it ran after every single search.
            dlg.SetHistory(["declined", "earlier", "older still"]);
            Pump();
            ok &= Check("recalling the history leaves the term alone", dlg.TermForTesting() == "declined",
                        dlg.TermForTesting());
            ok &= Check("and leaves the caret and selection where they were",
                        dlg.SelectionForTesting() == before, $"{dlg.SelectionForTesting()} was {before}");

            dlg.EnterForTesting();
            Pump();
            ok &= Check("Enter searches for what is in the box",
                        searched.Count == 1 && searched[0].Query.Text == "declined" && searched[0].Forward,
                        string.Join(",", searched.Select(s => s.Query.Text)));
            ok &= Check("and does not disturb it", dlg.TermForTesting() == "declined" && dlg.SelectionForTesting() == before,
                        $"{dlg.TermForTesting()} at {dlg.SelectionForTesting()}");

            // A pattern that will not compile is not worth searching for: it would sweep the whole file to
            // report that nothing matched, and the bar already says what is wrong with it.
            dlg.SetRegexForTesting(true);
            dlg.SetTermForTesting("declin(ed", 0, 0);
            Pump();
            int asked = searched.Count;
            dlg.EnterForTesting();
            Pump();
            ok &= Check("Enter does not search for a pattern that will not compile", searched.Count == asked,
                        string.Join(",", searched.Select(s => s.Query.Text)));
            dlg.SetTermForTesting("declin(ed)", 0, 0);
            Pump();
            dlg.EnterForTesting();
            Pump();
            ok &= Check("and searches again once it will", searched.Count == asked + 1);
            dlg.SetRegexForTesting(false);
            dlg.SetTermForTesting("declined", 3, 2);
            Pump();
            searched.RemoveRange(1, searched.Count - 1);

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

            // Marking the hits looks only at the rows on screen, so it happens as the term is typed rather
            // than after a pause. No Pump() anywhere here: waiting for a timer is the thing that must not
            // be needed.
            var previews = new List<FindQuery?>();
            dlg.PreviewChanged += q => previews.Add(q);
            FindQuery? Latest() => previews.Count > 0 ? previews[^1] : null;
            dlg.SetTermForTesting("bth", 0, 0);
            ok &= Check("typing marks the hits without waiting", previews.Count == 1, previews.Count.ToString());
            dlg.SetTermForTesting("bthp", 0, 0);
            dlg.SetTermForTesting("bthpo", 0, 0);
            ok &= Check("and again on every keystroke", previews.Count == 3, previews.Count.ToString());
            ok &= Check("with the term as it stands", Latest()?.Text == "bthpo", Latest()?.Text ?? "(none)");
            dlg.SetRegexForTesting(true);
            ok &= Check("turning an option on re-marks too", previews.Count == 4 && Latest() is { Regex: true });
            dlg.SetRegexForTesting(false);
            dlg.SetTermForTesting("", 0, 0);
            ok &= Check("and emptying the box takes the marks away", previews.Count > 0 && Latest() is null);

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
            host.Close();
            host.Dispose();
            Pump();
        }
    }

    /// <summary>Windows slides a progress bar's fill towards a rising value over a few hundred milliseconds,
    /// so a job that finishes quickly is over long before the fill arrives - a bar crawled to a seventh full
    /// while the search itself was four fifths done. What is PAINTED is the only thing that matters here,
    /// and WM_PRINT (what DrawToBitmap uses) reports the slid position, not the value.
    ///
    /// The status bar's is now the only progress bar in the app, the find bar having taken its own to the
    /// status bar's when it stopped being a dialog.</summary>
    private static bool RunProgressPaintChecks()
    {
        Line("-- progress bars paint what they are told --");

        var form = new MainForm(new AppSettings(), new MachineState(), [])
        {
            NoSavePrompt = true,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Opacity = 0
        };
        try
        {
            form.Show();
            Pump();

            var bar = form.StatusProgressForTesting;
            bool ok = Check("the status bar has a progress bar", bar is not null);
            if (bar is not null)
            {
                // Straight from empty to most of the way along - the jump the slide is slowest to follow.
                form.SetStatusProgressForTesting(0.8);
                double painted = PaintedFraction(bar);
                ok &= Check($"it paints the figure it was given at once, rather than crawling towards it " +
                            $"(asked 80%, painted {painted:P0})", Math.Abs(painted - 0.8) <= 0.1);
            }
            return ok;
        }
        finally
        {
            form.Close();
            form.Dispose();
            Pump();
        }
    }

    /// <summary>A filter started from a log line has to arrive holding that line. It used to keep only the
    /// first 200 characters, and the lines worth filtering on are exactly the long ones.</summary>
    private static bool RunCopyBudgetChecks()
    {
        Line("-- copying --");
        // Long lines on purpose: the cost of a copy follows CHARACTERS, and a cap counted in lines says
        // nothing at all about how much memory the clipboard is being asked for.
        const int lines = 200, width = 200;
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_copy_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++) sb.Append("line ").Append(i).Append(' ').Append('x', width).Append('\n');
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

            grid.SelectAll();
            bool ok = Check("the whole file is selected", grid.SelectedCount == lines, $"{grid.SelectedCount} lines");

            string whole = grid.BuildCopyText(withLineNumbers: false, out long copiedWhole);
            ok &= Check("within its budget a copy takes every selected line", copiedWhole == lines,
                        $"{copiedWhole} of {lines}");

            // Budget lowered rather than the fixture grown: the rule is what is under test, not the number.
            const int budget = 2_000;
            grid.CopyCharCap = budget;
            string capped = grid.BuildCopyText(withLineNumbers: false, out long copied);

            int lineWidth = whole.Length / lines;   // as copied, newline included
            ok &= Check("a copy stops at its character budget",
                        capped.Length <= budget + lineWidth, $"{capped.Length:N0} chars for a {budget:N0} budget");
            ok &= Check("and takes as many lines as that budget holds",
                        copied > 0 && copied < lines && Math.Abs(copied * lineWidth - capped.Length) <= lineWidth,
                        $"{copied} of {lines} lines, {capped.Length:N0} chars");
            ok &= Check("what it did take is the start of the selection, in one piece",
                        capped.StartsWith("line 0 ", StringComparison.Ordinal) && whole.StartsWith(capped, StringComparison.Ordinal),
                        capped.Length > 30 ? capped[..30] : capped);
            return ok;
        }
        finally
        {
            host?.Dispose();
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    private static bool RunNewFilterFromLineChecks()
    {
        Line("-- a filter made from a log line --");

        string line = "[2026-07-31T09:31:17][api-gateway][3][2FA8][315C][http][Handler][INFO][TFLAG] " +
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

        // What Ctrl+N starts the filter with. The last of these is the one that matters: a file opens with
        // nothing on the caret, and offering an empty filter beats telling the reader to go and click first.
        ok &= Check("the part picked out of a line wins",
                    MainForm.NewFilterSeed("req-abc123", "the whole line") == "req-abc123");
        ok &= Check("the whole caret line when nothing is picked out",
                    MainForm.NewFilterSeed(null, "  the whole line  ") == "the whole line");
        ok &= Check("an empty selection counts as none", MainForm.NewFilterSeed("", "a line") == "a line");
        ok &= Check("and nothing at all when the caret is on no line, so an empty filter is offered",
                    MainForm.NewFilterSeed(null, null) is null);

        // --- when the dialog warns that the screen and the file no longer agree ---

        // The rule is one thing: warn if what was taken off the screen is not IN the line it came from.
        // Everything below is that rule met by the cases it exists for, and by the ones it must stay quiet
        // for - a warning that fires whenever fields are on is one that gets ignored when it matters.
        const string raw = "[09:31][api][INFO] payment declined";
        bool Warns(string seed, bool fieldsOn = true) => MainForm.ShownTextNeedsCaution(fieldsOn, seed, raw);

        ok &= Check("nothing is said while the fields are off", !Warns(raw, fieldsOn: false));
        ok &= Check("nor when the seed is the line itself", !Warns(raw));
        ok &= Check("nor for a value taken out of the middle of it", !Warns("api"));
        // Hiding a field at the FRONT leaves the rest of the line in one piece, so a seed from what is left
        // is still the file's own text and will match.
        ok &= Check("nor for what is left after a field at the front is hidden",
                    !Warns("[api][INFO] payment declined"));
        // ...but a field hidden in the MIDDLE closes a gap that the file does not have.
        ok &= Check("but a seed spanning a gap left by a hidden field is warned about",
                    Warns("[09:31][INFO] payment declined"));
        ok &= Check("and one spanning a join made by carrying a field elsewhere",
                    Warns("payment declined[09:31]"));
        ok &= Check("and one carrying a space the projection had to invent",
                    Warns("[09:31] [api]"));
        ok &= Check("an empty seed says nothing either way", !Warns(""));
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

            // Measured with the search bar already up, because that is the state the reveal happens in -
            // the bar takes a couple of rows off the bottom of the list and the band moves with them.
            tree.ShowSearch();
            Pump();

            int visible = Math.Max(1, tree.TreeHeightForTesting / Math.Max(1, tree.RowHeightForTesting));
            int top = visible / 4;
            int bottom = Math.Max(top, visible * 3 / 4 - 1);
            bool ok = Check($"the filter pane is tall enough for a middle half to mean anything " +
                            $"({visible} rows, band {top}..{bottom})", visible >= 9);
            if (!ok) return false;

            int OffsetOf(string text) =>
                tree.VisibleFiltersForTesting.FindIndex(f => f.Match.Text == text);

            // Typing jumps to the first match, which is below the view.
            tree.TypeSearchForTesting("zulu");
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

    /// <summary>Where the find bar sits in the window. Opening it has to come out of the text area and
    /// nothing else: the filter pane keeps the size the user gave it, and the lines left in the log stay
    /// whole - which only works if the bar itself is a whole number of them.</summary>
    private static bool RunFindBarLayoutChecks()
    {
        Line("-- the find bar's place in the window --");

        var form = new MainForm(new AppSettings(), new MachineState(), [])
        {
            NoSavePrompt = true,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Opacity = 0
        };
        try
        {
            form.Show();
            Pump();

            int pitch = form.RowPitchForTesting;
            int settled = form.SplitterDistanceForTesting;
            bool ok = Check($"the window is laid out to start with (divider {settled}px, line {pitch}px)",
                            pitch > 1 && settled > 0);
            if (!ok) return false;

            if (!form.ClickMenuForTesting("Edit", "Find")) return Check("Edit > Find is there", false);
            Pump();
            int barHeight = form.FindBarHeightForTesting;
            ok &= Check($"the bar is open", form.FindBarIsOpenForTesting);
            ok &= Check($"and stands a whole number of lines tall ({barHeight}px of {pitch}px lines)",
                        barHeight % pitch == 0);
            // ...and no more of them than it needs: rounding up from a generously padded height once bought
            // a third line that was two thirds empty.
            ok &= Check($"and no taller than it has to be ({barHeight}px for a {form.FindBarForTesting.RowHeightForTesting}px row)",
                        barHeight - pitch < form.FindBarForTesting.RowHeightForTesting);

            // The report was that the filter pane shrank a little on every trip. It did: the bar was not a
            // whole number of lines, so the divider moved to make the remaining ones fit, and never moved back.
            var seen = new List<int> { form.SplitterDistanceForTesting };
            for (int i = 0; i < 4; i++)
            {
                form.CloseFindForTesting();
                Pump();
                seen.Add(form.SplitterDistanceForTesting);
                form.ClickMenuForTesting("Edit", "Find");
                Pump();
                seen.Add(form.SplitterDistanceForTesting);
            }
            ok &= Check($"opening and closing the bar never moves the divider [{string.Join(" ", seen)}]",
                        seen.All(d => d == settled));

            ok &= Check("the count redraws in one go, rather than clearing itself first",
                        form.FindBarRedrawsInOneGoForTesting);

            // Everything on the row reads as one line of controls, so they have to be one line of controls:
            // the same height, starting at the same y. Comparing centres alone is too forgiving - a control
            // that sizes itself grows the row without moving its centre far enough to notice.
            var findBar = form.FindBarForTesting;
            int middle = findBar.Height / 2;
            var boxes = new List<(string What, Rectangle R)>();
            foreach (var c in AllControls(findBar))
            {
                if (c is not (ComboBox or Button or CheckBox or Label)) continue;
                Rectangle inBar = findBar.RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));
                string what = c is Label l && l.Text.Length > 0 ? l.Text : c.AccessibleName ?? c.GetType().Name;
                boxes.Add((what, inBar));
            }
            Line("   (boxes: " + string.Join(", ",
                boxes.Select(b => $"{b.What} top={b.R.Top} h={b.R.Height}")) + ")");
            ok &= Check("every control on the row is the same height",
                        boxes.Count > 0 && boxes.Select(b => b.R.Height).Distinct().Count() == 1);
            ok &= Check("and starts on the same line",
                        boxes.Select(b => b.R.Top).Distinct().Count() == 1);
            ok &= Check($"which is the middle of the bar ({middle}px)",
                        boxes.Count > 0 && Math.Abs(boxes[0].R.Top + boxes[0].R.Height / 2 - middle) <= 1);

            // Boxes of the same height can still hold their text at different heights, and the text is what
            // the eye reads as a line. So measure the INK, off a render of the real bar. Comparing the TOP
            // of it works whatever the caption says: every one starts with a capital or a digit, so the top
            // is the cap line, and a descender in one of them cannot skew it.
            findBar.SetTermForTesting("Sample 123", 0, 0);
            findBar.SetMessage("Match 5 of 8");
            Pump();
            var ink = TextInk(findBar);
            Line("   (ink: " + string.Join(", ", ink.Select(i => $"{i.What} {i.Top}..{i.Bottom} {i.Font}")) + ")");

            // Two of them are not captions on the bar's surface and are left out on purpose: the term sits
            // inside its own framed box, and the close button is a symbol with no cap height to share.
            var captions = ink.Where(i => i.What is not ("Close find" or "Find what")).ToList();
            int highest = captions.Count > 0 ? captions.Min(i => i.Top) : 0;
            int lowest = captions.Count > 0 ? captions.Max(i => i.Top) : 0;
            ok &= Check($"every caption on the row sits on one line (tops {highest}-{lowest})",
                        captions.Count >= 6 && lowest - highest <= 1);
            ok &= Check("and every one of them is written at one size",
                        ink.Select(i => i.Font).Distinct().Count() == 1,
                        string.Join("/", ink.Select(i => i.Font).Distinct()));

            // A rule where the count starts, so the row reads as what is being looked for on one side and
            // what was found on the other.
            int rule = findBar.CountStartsAtForTesting;
            ok &= Check($"a separator marks where the count begins ({rule}px of {findBar.Width})",
                        rule > 0 && rule < findBar.Width);
            ok &= Check("and it is really drawn there", RuleIsDrawnAt(findBar, rule));

            // A pattern that will not compile says so where the count goes. The two can never both apply,
            // which is why they share the space.
            findBar.SetMessage("Match 5 of 8");
            findBar.SetTermForTesting("foo[", 0, 0);
            findBar.SetRegexForTesting(true);
            Pump();
            ok &= Check("a broken pattern is complained about where the count goes",
                        findBar.MessageForTesting().StartsWith("Invalid regex", StringComparison.Ordinal),
                        findBar.MessageForTesting());
            ok &= Check("and it is coloured as a problem rather than as a count",
                        findBar.MessageColourForTesting != SystemColors.GrayText);

            // ...and mending it hands the space straight back to the count.
            findBar.SetTermForTesting("foo", 0, 0);
            Pump();
            ok &= Check("mending the pattern gives the count its place back",
                        findBar.MessageForTesting() == "Match 5 of 8", findBar.MessageForTesting());
            findBar.SetRegexForTesting(false);
            return ok;
        }
        finally
        {
            form.Close();
            form.Dispose();
            Pump();
        }
    }

    /// <summary>Walking matches with the Enter key held down changes the count about thirty times a second.
    /// Only the count itself may be redrawn for that: repainting the row it sits in would erase the term,
    /// the options and the buttons and put them straight back, which is what a flicker is.</summary>
    /// <summary>The hint under the fields is rewritten on every keystroke of a half-written regex, because
    /// .NET puts the pattern into its own complaint. It must redraw itself and leave the dialog alone.</summary>
    private static bool RunFilterDialogRepaintChecks()
    {
        Line("-- the regex complaint changing does not disturb the filter dialog --");

        using var dlg = new FilterEditDialog(new Filter { Match = { Text = "" } }, isNew: true)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Opacity = 0,
        };
        dlg.Show();
        Pump();

        var dialogBody = dlg.Controls[0];
        int bodyPaints = 0;
        dialogBody.Paint += (_, _) => bodyPaints++;

        // The same call WinForms makes for Alt+R, so the option is ticked the way a user ticks it.
        typeof(Control).GetMethod("ProcessMnemonic", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dlg, new object[] { 'R' });
        Pump();

        const int steps = 12;

        // A control run first: pump exactly as often with the pattern untouched, so what follows measures
        // the complaint changing rather than whatever the message loop does anyway.
        bodyPaints = 0;
        for (int i = 0; i < steps; i++) Pump();
        int idlePaints = bodyPaints;

        bodyPaints = 0;
        int notesBefore = dlg.NotePaintsForTesting;
        var wordings = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < steps; i++)
        {
            dlg.SetTextForTesting("abc[" + new string('x', i));   // the set never closes, so it stays broken
            Pump();
            wordings.Add(dlg.NoteForTesting);
        }
        int notePaints = dlg.NotePaintsForTesting - notesBefore;

        bool ok = Check("a half-written pattern is reported as broken",
                        dlg.NoteForTesting.StartsWith("Invalid regex", StringComparison.Ordinal),
                        dlg.NoteForTesting);
        ok &= Check($"and the complaint really does change with every keystroke ({wordings.Count} of {steps})",
                    wordings.Count == steps);
        ok &= Check($"the complaint itself redraws as it changes ({notePaints} times over {steps})",
                    notePaints > 0);
        ok &= Check($"but the dialog around it is left alone (body repainted {bodyPaints} times while the " +
                    $"complaint changed, {idlePaints} while it did not)", bodyPaints <= idlePaints);
        ok &= Check("and the complaint redraws in one go", dlg.NoteRedrawsInOneGoForTesting);

        dlg.Close();
        Pump();
        return ok;
    }

    private static bool RunFindBarRepaintChecks()
    {
        Line("-- the count changing does not disturb the bar --");

        var bar = new FindBar((_, _) => { }) { Visible = true };
        var host = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Opacity = 0,
            ClientSize = new Size(1200, 80)
        };
        host.Controls.Add(bar);
        try
        {
            host.Show();
            Pump();

            const int steps = 30;

            // A control run first: pump exactly as often without touching the message, so what follows is
            // measuring the count changing rather than whatever the message loop does anyway.
            int idleBar = bar.BarPaintsForTesting;
            for (int i = 0; i < steps; i++) Pump();
            int idlePaints = bar.BarPaintsForTesting - idleBar;

            // ...and a run that redraws the count without changing a character of it, which separates "the
            // label was repainted" from "the text changed".
            int redrawBar = bar.BarPaintsForTesting;
            for (int i = 0; i < steps; i++) { bar.RepaintMessageForTesting(); Pump(); }
            int redrawPaints = bar.BarPaintsForTesting - redrawBar;
            Line($"   (bar repaints: {idlePaints} idle, {redrawPaints} redrawing the count unchanged)");

            int barBefore = bar.BarPaintsForTesting, messageBefore = bar.MessagePaintsForTesting;
            for (int i = 1; i <= steps; i++)
            {
                bar.SetMessage($"Match {i:N0} of 348 lines", $"On match {i:N0} of 348");
                Pump();
            }
            int barPaints = bar.BarPaintsForTesting - barBefore;
            int messagePaints = bar.MessagePaintsForTesting - messageBefore;

            bool ok = Check($"the count itself redraws as it changes ({messagePaints} times over {steps})",
                            messagePaints > 0);
            ok &= Check($"but the row around it is left alone (bar repainted {barPaints} times while the " +
                        $"count changed, {idlePaints} while it did not)", barPaints <= idlePaints);

            // Holding the key down never lets the message queue empty, and a paint only arrives when it
            // does - so the count has to be pushed out rather than waited for, or it sits at whatever it
            // read when the key went down until it is released. No Pump() here: that is the whole point.
            bar.SetMessage("Match 99 of 348 lines", "On match 99 of 348");
            int pushed = bar.MessagePaintsForTesting;
            bar.PaintNow();
            ok &= Check("the count can be painted without waiting for an idle moment",
                        bar.MessagePaintsForTesting > pushed);

            // The box grows with the window rather than staying the size a small one needed, but stops well
            // short of filling the row - a search field the width of a screen is no easier to read.
            var widths = new List<int>();
            var counts = new List<int>();
            foreach (int w in new[] { 700, 1400, 3200 })
            {
                host.ClientSize = new Size(w, host.ClientSize.Height);
                Pump();
                widths.Add(bar.TermBoxWidthForTesting);
                counts.Add(bar.MessageWidthForTesting);
            }
            Line($"   (term box: {string.Join(", ", widths)} / count: {string.Join(", ", counts)}"
                 + $" across 700, 1400 and 3200px)");
            ok &= Check("the term box grows with the window", widths[1] > widths[0]);
            ok &= Check("and stops before it takes over the row", widths[2] < 3200 / 2);
            ok &= Check("and never shrinks below something usable", widths[0] >= 200);
            ok &= Check("the count keeps room for a tally worth reading", counts[1] >= bar.CountWidthForTesting);
            return ok;
        }
        finally
        {
            host.Close();
            host.Dispose();
            Pump();
        }
    }

    /// <summary>A log line is as tall as the typeface says a line is, and no taller unless the reader asks.
    /// Two pixels used to be added to every row unasked, which on Consolas is a line in every eleven off the
    /// screen - the difference that made another viewer look like it fitted more in at the same size.</summary>
    private static bool RunLineSpacingChecks()
    {
        Line("-- how tall a line is --");

        var settings = new AppSettings();
        var grid = new LineGridControl { Dock = DockStyle.Fill };
        var host = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            ClientSize = new Size(700, 400),
            Opacity = 0,
            FormBorderStyle = FormBorderStyle.None
        };
        host.Controls.Add(grid);
        try
        {
            host.Show();
            Pump();

            bool ok = Check("nothing is added to a line by default", settings.ExtraLineSpacing == 0,
                            settings.ExtraLineSpacing.ToString());

            grid.ApplySettings(settings);
            int natural = grid.FontForTesting.Height;
            int pitch = grid.RowPitch;
            Line($"   ({settings.FontFamily} {settings.FontSize}pt: font line height {natural}px, row pitch {pitch}px)");
            ok &= Check("so a row is exactly the font's own line height", pitch == natural);

            // ...and asking for more gives exactly that much more, which is the point of the preference.
            foreach (int extra in new[] { 1, 3, 8 })
            {
                settings.ExtraLineSpacing = extra;
                grid.ApplySettings(settings);
                ok &= Check($"asking for {extra} more gives {natural + extra}px", grid.RowPitch == natural + extra,
                            grid.RowPitch.ToString());
            }

            // More room per line means fewer of them, which is the whole reason to care.
            settings.ExtraLineSpacing = 0;
            grid.ApplySettings(settings);
            int tight = grid.VisibleRowCountForTesting;
            settings.ExtraLineSpacing = 2;
            grid.ApplySettings(settings);
            int loose = grid.VisibleRowCountForTesting;
            ok &= Check($"and costs lines on screen ({tight} tight, {loose} with two pixels added)",
                        tight > loose);
            return ok;
        }
        finally { host.Close(); host.Dispose(); Pump(); }
    }

    /// <summary>The status bar used to repeat the caret's line number and the total beside it. The gutter
    /// already gives the first and the field beside it the second, and neither means much with several lines
    /// selected - so the space says which lines are being shown instead, which nothing else on screen does.
    /// </summary>
    private static bool RunStatusBarChecks()
    {
        Line("-- what the status bar says --");

        string log = Path.Combine(Path.GetTempPath(), "cascade_status_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(log, Enumerable.Range(1, 200).Select(i => $"line {i} of the log"));

        MainForm? form = null;
        try
        {
            string[] args = [log];
            form = new MainForm(new AppSettings(), new MachineState(), args)
            {
                NoSavePrompt = true,
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(1100, 700),
            };
            form.Show();
            Pump();
            for (int i = 0; i < 60 && form.DocForTesting.CompletedLineCount < 200; i++) { Thread.Sleep(20); Pump(); }

            form.GridForTesting.GoToLine(42);
            Pump();
            string status = form.StatusForTesting;
            Line($"   ({status})");

            bool ok = Check("the caret's line number is not repeated in the status bar",
                            !status.Contains("Ln:", StringComparison.Ordinal), status);
            ok &= Check("the line count is still there", status.Contains("Total: 200", StringComparison.Ordinal));
            ok &= Check("and so is the matched count", status.Contains("Fil:", StringComparison.Ordinal));
            ok &= Check("it says every line is being shown",
                        status.Contains("Showing: all lines", StringComparison.Ordinal), status);

            form.ClickMenuForTesting("View", "Show Only Filtered Lines");
            Pump();
            ok &= Check("and says so when only the matches are",
                        form.StatusForTesting.Contains("Showing: matches", StringComparison.Ordinal),
                        form.StatusForTesting);

            form.ClickMenuForTesting("View", "Show Only Filtered Lines");
            Pump();
            ok &= Check("and back again", form.StatusForTesting.Contains("Showing: all lines", StringComparison.Ordinal),
                        form.StatusForTesting);
            return ok;
        }
        finally
        {
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            Pump();
            try { File.Delete(log); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// The elapsed column and the elapsed slot, driven through a real window.
    ///
    /// <para>Two things are worth holding here that the engine's own tests cannot. One is that the margin
    /// and the status bar always AGREE - they answer the same question about the same line, and two
    /// displays of one number that can disagree are worse than one. The other is that the margin's width is
    /// fixed: it is what the text to its right is placed by, so a column that resized as it scrolled would
    /// slide the whole log sideways.</para>
    /// </summary>
    private static bool RunElapsedChecks()
    {
        Line("-- elapsed times --");

        string dir = Path.Combine(Path.GetTempPath(), "cascade_elapsed_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // Line i is written at i seconds, so every expected answer is arithmetic on the line number - and
        // every tenth line is a payment, which is what the filter below leaves showing.
        string timed = Path.Combine(dir, "timed.log");
        var start = new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc);
        File.WriteAllLines(timed, Enumerable.Range(0, 300).Select(i =>
            $"[{start.AddSeconds(i):yyyy-MM-ddTHH:mm:ss.fff}][{(i % 10 == 0 ? "payment-svc" : "api-gateway")}] request {i}"));
        string untimed = Path.Combine(dir, "untimed.log");
        File.WriteAllLines(untimed, Enumerable.Range(0, 300).Select(i => $"request {i} handled by worker {i % 8}"));

        MainForm? form = null;
        try
        {
            form = new MainForm(new AppSettings(), new MachineState(), [timed])
            {
                NoSavePrompt = true,
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(1100, 700),
            };
            form.Show();
            Pump();
            for (int i = 0; i < 100 && form.DocForTesting.CompletedLineCount < 300; i++) { Thread.Sleep(20); Pump(); }

            var grid = form.GridForTesting;
            var doc = form.DocForTesting;

            bool ok = Check("a log with a stamp at the front is read without being told anything",
                            doc.Clock is not null, doc.Clock?.Format.Source ?? "(none)");
            ok &= Check("and to the precision the log itself carries",
                        doc.Clock?.FractionDigits == 3, $"{doc.Clock?.FractionDigits}");

            ok &= Check("the margin says how long it was since the line above",
                        grid.ElapsedForTesting(40) == "1.000", grid.ElapsedForTesting(40));
            ok &= Check("and says nothing at all beside the first line there is",
                        grid.ElapsedForTesting(0).Length == 0, $"\"{grid.ElapsedForTesting(0)}\"");

            // Two right-aligned figures with nothing but air between them read as one ragged column. Off the
            // PIXELS: where the rule was meant to go says nothing about whether it was drawn.
            ok &= Check("a hairline separates the line numbers from the figures beside them",
                        RuleIsDrawnAt(grid, grid.ElapsedGutterLeftForTesting),
                        $"nothing darker at x={grid.ElapsedGutterLeftForTesting}");

            // The air either side of a line number has to MATCH, and has to scale. It did neither: the box
            // was padded by a raw 12 device pixels while the text was inset by a scaled 6, so at 150% the
            // numbers had 3 pixels to their left and 9 to their right and sat against the window edge.
            // Two things the reading depends on: the END of the file, where the numbers are as wide as the
            // box was sized for and so fill it (a short number is right-aligned and leaves more on the left,
            // which would let the fault through); and the focus elsewhere, because the accent bar runs down
            // the very edge the air is measured from and a focus indicator is not the margin.
            form.ClickMenuForTesting("View", "Focus Filter List");
            grid.GoToLine(299);
            Pump();
            string margins = grid.MarginLayoutForTesting;
            Line("   (" + margins + ")");
            int pad = Figure(margins, "pad="), numbers = Figure(margins, "numbers=");
            var (leftAir, rightAir) = MarginAir(grid, Figure(margins, "markers="), numbers);
            ok &= Check("and a line number has the same air either side of it, whatever the display's scale",
                        pad >= form.LogicalToDeviceUnits(6) && leftAir >= pad - 1 && rightAir >= pad - 1
                        && Math.Abs(leftAir - rightAir) <= pad,
                        $"{margins}; ink sits {leftAir} from the left and {rightAir} from the right");
            form.ClickMenuForTesting("View", "Focus Text Area");
            Pump();

            // The width is what the text to its right is placed by, so it has to come from the widest value
            // the format can EVER draw rather than from the ones on screen.
            int room = grid.ElapsedGutterWidthForTesting;
            int wide = TextRenderer.MeasureText("-9999.999", grid.Font).Width;
            ok &= Check("the margin is sized for the widest figure it could ever draw, not the ones on screen",
                        room >= wide, $"{room}px of room for {wide}px of figure");

            grid.GoToLine(41);
            Pump();
            ok &= Check("the status bar measures the same line the margin does",
                        form.ElapsedSlotForTesting == "\u0394 Prev: 1 s", form.ElapsedSlotForTesting);

            grid.SelectRowForAccessibility(40);
            for (int i = 0; i < 9; i++) grid.PressKeyForTesting(Keys.Down | Keys.Shift);
            Pump();
            ok &= Check("and calls a stretch of lines a span, not a difference from the line above",
                        form.ElapsedSlotForTesting == "Span: 9 s", form.ElapsedSlotForTesting);

            // The whole point of the column: with the noise filtered away it measures between one
            // interesting line and the next, which is ten times the number the same two lines give unfiltered.
            var filters = new FilterCollection { ShowOnlyFilteredLines = true };
            filters.Add(new Filter { Match = new FilterMatch { Text = "payment-svc" }, Enabled = true });
            doc.SetFilters(filters);
            for (int i = 0; i < 100 && !doc.IsFilterIdle; i++) { Thread.Sleep(20); Pump(); }
            Pump();
            ok &= Check("with the rest of the log hidden it measures between the lines still showing",
                        grid.ElapsedForTesting(40) == "10.000", grid.ElapsedForTesting(40));

            filters.ShowOnlyFilteredLines = false;
            doc.ApplyFilters();
            for (int i = 0; i < 100 && !doc.IsFilterIdle; i++) { Thread.Sleep(20); Pump(); }
            Pump();
            ok &= Check("and back to the line above once they are all showing again",
                        grid.ElapsedForTesting(40) == "1.000", grid.ElapsedForTesting(40));

            // A selection the filters then hide: the view falls back to showing the caret's line, and the
            // slot has to measure THAT - what is measured must be what is highlighted.
            grid.SelectRowForAccessibility(41);      // not a payment line, so filtering will hide it
            Pump();
            filters.ShowOnlyFilteredLines = true;
            doc.ApplyFilters();
            for (int i = 0; i < 100 && !doc.IsFilterIdle; i++) { Thread.Sleep(20); Pump(); }
            Pump();
            ok &= Check("a selection the filters have hidden is measured where the view puts it instead",
                        form.ElapsedSlotForTesting.StartsWith("\u0394 Prev: ", StringComparison.Ordinal)
                        && !form.ElapsedSlotForTesting.Contains('\u2014', StringComparison.Ordinal),
                        form.ElapsedSlotForTesting);
            filters.ShowOnlyFilteredLines = false;
            doc.ApplyFilters();
            for (int i = 0; i < 100 && !doc.IsFilterIdle; i++) { Thread.Sleep(20); Pump(); }
            Pump();

            // Turning the margin off gives its room back to the text, and nothing else moves.
            int with = grid.GutterWidthForTesting;
            form.ClickMenuForTesting("View", "Elapsed Time", "In the Margin");
            Pump();
            int without = grid.GutterWidthForTesting;
            ok &= Check("putting the margin away gives its room to the text",
                        without == with - room, $"{with} -> {without}, column was {room}");
            form.ClickMenuForTesting("View", "Elapsed Time", "In the Margin");
            Pump();
            ok &= Check("and bringing it back puts it exactly where it was",
                        grid.GutterWidthForTesting == with, $"{with} -> {grid.GutterWidthForTesting}");

            form.ClickMenuForTesting("View", "Elapsed Time", "In the Status Bar");
            Pump();
            ok &= Check("putting the slot away takes it off the bar rather than emptying it",
                        form.ElapsedSlotForTesting.Length == 0, form.ElapsedSlotForTesting);

            // The two paths are the only things on the bar that give way, so anything else given room is
            // taking it from them - and the sum that decides how generous the counts may be has to know it.
            // While it did not, the paths on a narrow window were worn down to a single character.
            int roomAlone = PathRoomOf(form.StatusLayoutForTesting);
            form.ClickMenuForTesting("View", "Elapsed Time", "In the Status Bar");
            Pump();
            string layout = form.StatusLayoutForTesting;
            Line("   (" + layout + ")");
            int roomShared = PathRoomOf(layout), slot = SlotOf(layout);
            ok &= Check("the room the paths are left counts the slot that took it",
                        slot > 0 && roomShared == roomAlone - slot, $"{roomAlone} -> {roomShared}, slot {slot}");

            // A log with no times: the feature is absent, and the menu says where to say otherwise.
            form.OpenForTesting(untimed);
            for (int i = 0; i < 100 && form.DocForTesting.CompletedLineCount < 300; i++) { Thread.Sleep(20); Pump(); }
            Pump();
            ok &= Check("a log with no times in it simply has no clock", doc.Clock is null,
                        doc.Clock?.Format.Source ?? "(none)");
            ok &= Check("so the margin takes no room", grid.ElapsedGutterWidthForTesting == 0,
                        $"{grid.ElapsedGutterWidthForTesting}px");
            ok &= Check("and the slot is off the bar entirely", form.ElapsedSlotForTesting.Length == 0,
                        form.ElapsedSlotForTesting);

            string menu = form.ElapsedMenuForTesting();
            Line("   (" + menu.Replace("\n", " / ", StringComparison.Ordinal) + ")");
            ok &= Check("the menu says it cannot do it rather than doing nothing",
                        menu.Contains("(unavailable)", StringComparison.Ordinal), menu);
            ok &= Check("and says where to say where the stamp is",
                        menu.Contains("Field Settings", StringComparison.Ordinal), menu);

            // A drop-down is as wide as the widest thing in it, and the item explaining a MISSING clock is
            // three times the width of the two entries. Hidden it must not still be paying for its room.
            form.OpenForTesting(timed);
            for (int i = 0; i < 100 && form.DocForTesting.Clock is null; i++) { Thread.Sleep(20); Pump(); }
            string widths = form.ElapsedMenuWidthForTesting();
            Line("   (" + widths + ")");
            int drop = Figure(widths, "drop="), widest = Figure(widths, "widest=");
            ok &= Check("the menu is no wider than the entries it is showing",
                        drop > 0 && drop <= widest + form.LogicalToDeviceUnits(80), widths);

            // Both displays have a key of their own. They are handled by the form rather than registered on
            // the menu items, because whether they may run depends on the log having a clock - and the items
            // only learn that when the View menu is opened.
            int margin = grid.ElapsedGutterWidthForTesting;
            ok &= Check("Ctrl+Shift+M takes the margin away",
                        form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.M)
                        && grid.ElapsedGutterWidthForTesting == 0, $"{grid.ElapsedGutterWidthForTesting}px");
            ok &= Check("and brings it back exactly as it was",
                        form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.M)
                        && grid.ElapsedGutterWidthForTesting == margin,
                        $"{margin} -> {grid.ElapsedGutterWidthForTesting}");
            form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.B);
            Pump();
            ok &= Check("Ctrl+Shift+B takes the slot off the status bar",
                        form.ElapsedSlotForTesting.Length == 0, form.ElapsedSlotForTesting);
            form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.B);
            Pump();
            ok &= Check("and puts it back", form.ElapsedSlotForTesting.Length > 0);

            // The rule the pair was chosen by, stated rather than written down twice: each key is the letter
            // its own entry underlines, so there is one letter to learn per entry.
            string advertised = form.ElapsedMenuKeysForTesting();
            Line("   (" + advertised + ")");
            bool matched = advertised.Split('|').Length == 2 && advertised.Split('|').All(entry =>
            {
                string[] parts = entry.Split('=');
                int amp = parts[0].IndexOf('&', StringComparison.Ordinal);
                return parts.Length == 2 && amp >= 0 && amp + 1 < parts[0].Length
                    && parts[1].StartsWith("Ctrl+Shift+", StringComparison.Ordinal)
                    && parts[1][^1] == char.ToUpperInvariant(parts[0][amp + 1]);
            });
            ok &= Check("and each key is the letter its own entry underlines", matched, advertised);

            // On a log with no clock the key must fall through rather than silently flip a setting nobody
            // can see the effect of.
            form.OpenForTesting(untimed);
            for (int i = 0; i < 100 && doc.Clock is not null; i++) { Thread.Sleep(20); Pump(); }
            bool wasOn = form.ShowElapsedGutterForTesting;
            ok &= Check("and neither key claims itself on a log with no clock",
                        !form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.M)
                        && !form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.B)
                        && form.ShowElapsedGutterForTesting == wasOn);

            ok &= CheckNamingTheTimeField();
            return ok;
        }
        finally
        {
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            Pump();
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static int PathRoomOf(string layout) => Figure(layout, "pathroom=");
    private static int SlotOf(string layout) => Figure(layout, "elapsed=");

    /// <summary>How far the widest line number's ink sits from each end of the box it is drawn in, read off
    /// a render of the grid. The arithmetic says how wide the box is; only the pixels say where in it the
    /// digits ended up. Rows carrying something other than a line number - the caret's row, and the focus
    /// accent down the left edge - are passed over: neither is the margin.</summary>
    private static (int Left, int Right) MarginAir(LineGridControl grid, int from, int width)
    {
        var area = grid.GutterAreaForTesting;
        if (width <= 0 || area.Height <= 0) return (-1, -1);
        using var bmp = new Bitmap(grid.Width, grid.Height);
        grid.DrawToBitmap(bmp, new Rectangle(0, 0, grid.Width, grid.Height));

        static bool Ink(Color c) => (c.R + c.G + c.B) / 3 < 200;
        int first = int.MaxValue, last = -1;
        for (int y = area.Top; y < area.Bottom && y < grid.Height; y++)
        {
            // A run reaching the very left edge is the focus accent, not a digit; a row inked end to end is
            // a fill rather than glyphs. Either way there is no number in it to measure.
            if (Ink(bmp.GetPixel(from, y))) continue;
            for (int x = from; x < from + width && x < grid.Width; x++)
            {
                if (!Ink(bmp.GetPixel(x, y))) continue;
                if (x < first) first = x;
                if (x > last) last = x;
            }
        }
        return last < 0 ? (-1, -1) : (first - from, from + width - 1 - last);
    }

    private static int Figure(string layout, string key)
    {
        int at = layout.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return -1;
        int from = at + key.Length, to = from;
        while (to < layout.Length && (char.IsAsciiDigit(layout[to]) || layout[to] == '-')) to++;
        return int.TryParse(layout[from..to], out int n) ? n : -1;
    }

    /// <summary>Saying where the stamp is, in the field settings. What matters here is that the format is    /// PROPOSED and shown reading the reader's own log back to them - a guess you can watch being right is
    /// a different thing from one you have to trust - and that saying it does not, on its own, turn the log
    /// into a table.</summary>
    private static bool CheckNamingTheTimeField()
    {
        // The level comes first, so nothing detection looks at could find this stamp: it has to be named.
        var start = new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc);
        var samples = Enumerable.Range(0, 40)
            .Select(i => $"INFO  [{start.AddSeconds(i):HH:mm:ss.fff}] worker-{i % 4} handled req-{i}")
            .ToList();

        var spec = new ColumnSpec { Enabled = false, Template = "{*} {[*]} {*}" };
        spec.Reset();

        using var dlg = new ColumnsDialog(spec, samples);
        dlg.Opacity = 0;
        dlg.StartPosition = FormStartPosition.Manual;
        dlg.Location = new Point(0, 0);
        dlg.Show();
        Pump();

        bool ok = Check("with no field named, the settings say where the figures are coming from",
                        dlg.TimeStatusForTesting.Contains("No field is the timestamp", StringComparison.Ordinal),
                        dlg.TimeStatusForTesting);

        dlg.PickTimeFieldForTesting(1);
        Pump();
        ok &= Check("naming a field proposes what reads it",
                    dlg.TimeFormatForTesting == "HH:mm:ss.fff", dlg.TimeFormatForTesting);
        ok &= Check("and the field it is reading is named where it was chosen",
                    dlg.TimeFieldForTesting == "Col 2", dlg.TimeFieldForTesting);
        ok &= Check("and shows it reading the reader's own log back to them",
                    dlg.TimeStatusForTesting.Contains("40 of 40", StringComparison.Ordinal)
                    && dlg.TimeStatusForTesting.Contains("09:00:00.000", StringComparison.Ordinal),
                    dlg.TimeStatusForTesting);

        dlg.ApplyForTesting();
        ok &= Check("the field is remembered", dlg.Result.TimePart == 1 && dlg.Result.HasTime,
                    $"part {dlg.Result.TimePart}, format \u201c{dlg.Result.TimeFormat}\u201d");
        ok &= Check("and saying where the stamp is does not lay the log out as a table",
                    !dlg.Result.Enabled, "splitting was switched on");

        // An edit that leaves the same NUMBER of fields still changes what the time field holds, so what
        // the format makes of it has to be read again rather than left saying what it said before.
        dlg.SetTemplateForTesting("{*} ZZZ{[*]} {*}");
        Pump();
        ok &= Check("editing the template re-reads what the time field now holds",
                    !dlg.TimeStatusForTesting.Contains("40 of 40", StringComparison.Ordinal),
                    dlg.TimeStatusForTesting);

        // Whereas writing a template plainly does mean "split it up".
        dlg.SetTemplateForTesting("{*} {[*]} {*} ");
        Pump();
        dlg.ApplyForTesting();
        ok &= Check("writing a template still means the reader wants to see the fields", dlg.Result.Enabled);

        // The first cell carries the field's colour, greyed when the field is not being shown. The fault it
        // guards against was that the cell only caught up when something ELSE made the grid repaint, so what
        // is watched is what the grid was ASKED to draw - a render would force it and prove nothing.
        dlg.SelectRowForTesting(0);
        dlg.SwatchPaintsForTesting();
        dlg.ForgetSwatchPaintsForTesting();
        dlg.SetCellForTesting(2, "show", false);
        int[] drawn = dlg.SwatchPaintsForTesting();
        ok &= Check("hiding a field redraws its colour there and then, without touching anything else",
                    drawn.Contains(2), drawn.Length == 0 ? "nothing redrawn" : "rows " + string.Join(",", drawn));

        // A button is several pixels taller than the box beside it, so a cell that anchors both to its TOP
        // leaves them on visibly different lines. Measured through the middle of each control rather than
        // off its box: what the eye reads as "level" is where the thing is, not how tall it is.
        // MEASURED, which is where the 1px allowance comes from: the reported fault is "TextBox 0+31,
        // Button 0+35" = 2px apart on the template row and 5px on the time row, while a combo box left at
        // its own height in a button-tall row rounds to 1px and is not something anyone can see.
        foreach (var row in dlg.MixedRowsForTesting)
        {
            var kids = row.Controls.Cast<Control>().Where(c => c.Visible).ToList();
            int highest = kids.Min(c => c.Top + c.Height / 2), lowest = kids.Max(c => c.Top + c.Height / 2);
            ok &= Check($"everything on the {(row == dlg.MixedRowsForTesting[0] ? "template" : "time")} row "
                      + $"is centred on one line (middles {highest}-{lowest})",
                        lowest - highest <= 1,
                        string.Join(", ", kids.Select(c => $"{c.GetType().Name} {c.Top}+{c.Height}")));
        }

        dlg.Close();
        Pump();
        return ok;
    }

    /// <summary>The Encoding menu. It is the only way to read a file whose bytes say nothing about how they
    /// were written - a code page, or UTF-16 with no mark - so what matters is that choosing an entry really
    /// re-reads the file, that the menu says which one is in effect, and that the choice survives a reload.
    /// </summary>
    private static bool RunEncodingMenuChecks()    {
        Line("-- the encoding menu --");

        string dir = Path.Combine(Path.GetTempPath(), "cascade_enc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // "café" is one byte in Windows-1252 and two in UTF-8, so the same bytes read one way or the other
        // are visibly different text - which is what makes every check below an observation, not a guess.
        string cp1252 = Path.Combine(dir, "cp1252.log");
        string utf16 = Path.Combine(dir, "utf16le.log");
        File.WriteAllBytes(cp1252, Encoding.GetEncoding(1252).GetBytes("INFO café ready\nWARN naïve façade\n"));
        var u16 = new UnicodeEncoding(false, true);
        File.WriteAllBytes(utf16, [.. u16.GetPreamble(), .. u16.GetBytes("INFO café ready\nWARN naïve façade\n")]);

        MainForm? form = null;
        try
        {
            string[] args = [cp1252];
            form = new MainForm(new AppSettings(), new MachineState(), args)
            {
                NoSavePrompt = true,
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(1100, 700),
            };
            form.Show();
            Pump();
            var doc = form.DocForTesting;
            Settle(doc);

            string menu = form.EncodingMenuForTesting();
            Line("   " + menu.Replace("\n", " | ", StringComparison.Ordinal));

            bool ok = Check("exactly one encoding is ticked", Ticked(menu).Count == 1, menu);
            ok &= Check("with nothing chosen, the ticked one is Auto-detect",
                        Ticked(menu).Contains("Auto-detect"), menu);
            ok &= Check("and Auto-detect says what it worked out",
                        menu.Contains("Auto-detect [UTF-8]", StringComparison.Ordinal), menu);
            // Guessed wrong, unavoidably: nothing in the bytes says which code page they are.
            ok &= Check("a code-page file guessed as UTF-8 comes out damaged",
                        doc.GetLineText(0).Contains('\uFFFD', StringComparison.Ordinal), doc.GetLineText(0));

            ok &= Check("choosing Windows-1252 re-reads the file",
                        form.ClickMenuForTesting("View", "Encoding", "Windows-1252")
                        && Settle(doc) && doc.GetLineText(0) == "INFO café ready", doc.GetLineText(0));
            menu = form.EncodingMenuForTesting();
            ok &= Check("and the tick moves to it", Ticked(menu).SequenceEqual(["Windows-1252"]), menu);
            ok &= Check("Auto-detect stops claiming to be in effect",
                        !menu.Contains("Auto-detect [", StringComparison.Ordinal), menu);

            ok &= Check("reloading keeps the chosen encoding",
                        form.ClickMenuForTesting("File", "Reload")
                        && Settle(doc) && doc.GetLineText(0) == "INFO café ready", doc.GetLineText(0));
            ok &= Check("and so does the tick",
                        Ticked(form.EncodingMenuForTesting()).SequenceEqual(["Windows-1252"]));

            ok &= Check("going back to Auto-detect guesses again",
                        form.ClickMenuForTesting("View", "Encoding", "Auto-detect")
                        && Settle(doc) && doc.GetLineText(0).Contains('\uFFFD', StringComparison.Ordinal),
                        doc.GetLineText(0));

            // A mark leaves nothing to guess at, and the menu has to say which encoding that turned out to be.
            form.OpenForTesting(utf16);
            Settle(doc);
            menu = form.EncodingMenuForTesting();
            ok &= Check("a marked file is detected and read", doc.GetLineText(0) == "INFO café ready", doc.GetLineText(0));
            ok &= Check("and Auto-detect names the encoding it found",
                        menu.Contains("Auto-detect [UTF-16 LE]", StringComparison.Ordinal), menu);
            ok &= Check("opening another file goes back to detecting", Ticked(menu).Contains("Auto-detect"), menu);

            // The mark says UTF-16, so this is the case where deferring to it left the menu doing nothing.
            ok &= Check("and a choice still overrules the mark",
                        form.ClickMenuForTesting("View", "Encoding", "Windows-1252")
                        && Settle(doc) && doc.GetLineText(0) != "INFO café ready", doc.GetLineText(0));
            return ok;

            static List<string> Ticked(string menu) =>
                [.. menu.Split('\n').Where(l => l.EndsWith(" *", StringComparison.Ordinal))
                        .Select(l => l[..^2] is var name && name.IndexOf(" [", StringComparison.Ordinal) is var b && b > 0
                                     ? name[..b] : name)];
        }
        finally
        {
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            Pump();
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    /// <summary>Waits for a freshly opened document to finish indexing, pumping so the window keeps up.</summary>
    private static bool Settle(CascadeDocument doc)
    {
        for (int i = 0; i < 200 && !doc.IsIndexComplete; i++) { Thread.Sleep(10); Pump(); }
        Pump();
        return doc.IsIndexComplete;
    }

    /// <summary>The bar appears above the log, so the room for it has to come off the TOP: the log is
    /// scrolled on by as much as the bar takes, which leaves every line still showing exactly where it was
    /// on screen. Keeping the top row instead slides the whole log down and drops its last lines, which
    /// reads as the text moving rather than as the bar covering it.</summary>
    private static bool RunFindBarRoomChecks()
    {
        Line("-- the bar takes its room off the top of the log --");

        string log = Path.Combine(Path.GetTempPath(), "cascade_room_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(log, Enumerable.Range(1, 4000).Select(i => $"line {i:0000} some text to read"));

        MainForm? form = null;
        try
        {
            form = new MainForm(new AppSettings(), new MachineState(), new[] { log })
            {
                NoSavePrompt = true,
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(1100, 800),
            };
            form.Show();
            Pump();
            var doc = form.DocForTesting;
            for (int i = 0; i < 60 && doc.CompletedLineCount < 4000; i++) { Thread.Sleep(20); Pump(); }

            var grid = form.GridForTesting;
            grid.GoToLine(2000);   // well away from either end, so nothing is clamped
            Pump();

            long firstBefore = grid.FirstRowForTesting;
            int rowsBefore = grid.VisibleRowCountForTesting;
            long lastBefore = firstBefore + rowsBefore - 1;
            int pitch = grid.RowPitch;

            // Where a line sits ON SCREEN is the thing that must not change - the grid's own coordinates
            // move with it when it is made shorter from the top, so they cannot tell.
            long watched = firstBefore + rowsBefore / 2;
            int ScreenYOf(long row) => grid.PointToScreen(new Point(0, grid.RowMiddleForTesting(row))).Y;
            int yBefore = ScreenYOf(watched);
            Rectangle mapAbove = grid.MapBoundsForTesting, barAbove = grid.ScrollBarBoundsForTesting;

            form.ClickMenuForTesting("Edit", "Find");
            Pump();
            int taken = form.FindBarHeightForTesting / pitch;
            long firstAfter = grid.FirstRowForTesting;
            long lastAfter = firstAfter + grid.VisibleRowCountForTesting - 1;

            bool ok = Check($"the bar stands {taken} lines tall", taken >= 1);
            ok &= Check($"opening it takes those lines off the top ({firstBefore} -> {firstAfter})",
                        firstAfter == firstBefore + taken);
            ok &= Check($"and none off the bottom ({lastBefore} -> {lastAfter})", lastAfter == lastBefore);
            ok &= Check($"so a line still showing has not moved on screen ({yBefore} -> {ScreenYOf(watched)})",
                        ScreenYOf(watched) == yBefore);

            // The bar sits inside the log view, so it stops short of the map and the scrollbar instead of
            // shoving them down - those two stand their full height whether it is open or not.
            var bar = form.FindBarForTesting;
            Rectangle mapNow = grid.MapBoundsForTesting, barNow = bar.Bounds;
            Line($"   (bar {barNow}, map {mapNow}, scrollbar {grid.ScrollBarBoundsForTesting}, grid {grid.Height} tall)");
            ok &= Check($"the bar stops short of the map and the scrollbar ({barNow.Right} of {grid.Width})",
                        mapNow.Width > 0 && barNow.Right <= mapNow.Left);
            ok &= Check("and the map still runs the whole height of the log view",
                        mapNow.Top == mapAbove.Top && mapNow.Height == mapAbove.Height,
                        $"{mapNow} was {mapAbove}");
            ok &= Check("and so does the scrollbar",
                        grid.ScrollBarBoundsForTesting.Top == barAbove.Top &&
                        grid.ScrollBarBoundsForTesting.Height == barAbove.Height,
                        $"{grid.ScrollBarBoundsForTesting} was {barAbove}");

            // The map draws the window the log is showing, so it has to have noticed the top rows going.
            var map = grid.MatchMapForTesting;
            ok &= Check("the log has a minimap to check", map is not null);
            if (map is not null)
            {
                var (top, height) = map.ViewportForTesting;
                int px = Math.Max(1, map.RowPixelsForTesting);
                int firstY = map.SlotOfForTesting(firstAfter) * px, lastY = map.SlotOfForTesting(lastAfter) * px;
                Line($"   (map window {top}..{top + height}px, rows {firstAfter}..{lastAfter} at {firstY}..{lastY}px, " +
                     $"{map.RowsPerPixelForTesting} rows a pixel)");
                ok &= Check("the map's window starts at the row the log now starts at",
                            Math.Abs(firstY - top) <= px);
                // Covers the last row the log shows. Not "ends exactly there": the rectangle has a minimum
                // height, which on a compressed map is several pixels more than the view really spans.
                ok &= Check("and covers the rest of what the log is showing", top + height >= lastY - px,
                            $"window ends {top + height}px, last row at {lastY}px");
                // The rows the bar took are fewer than one pixel of the map, so "not inside the window" is
                // the strongest claim the scale can carry.
                ok &= Check("so the rows the bar covered are not inside it",
                            map.SlotOfForTesting(firstBefore) * px <= top,
                            $"row {firstBefore} at {map.SlotOfForTesting(firstBefore) * px}px, window starts {top}px");
            }

            // Putting it away hands the rows back at the top, so the log goes back with them.
            form.CloseFindForTesting();
            Pump();
            ok &= Check($"closing it gives those lines back at the top ({grid.FirstRowForTesting})",
                        grid.FirstRowForTesting == firstBefore);
            ok &= Check($"and still nothing has moved on screen ({ScreenYOf(watched)})",
                        ScreenYOf(watched) == yBefore);

            // The ends of the file are where this could quietly fail: the scroll has nowhere to go unless
            // the room the bar took has changed how far the view is allowed to move.
            foreach (long line in new long[] { 1, 4000 })
            {
                grid.GoToLine(line);
                Pump();
                long was = grid.FirstRowForTesting;
                int wasRows = grid.VisibleRowCountForTesting;
                form.ClickMenuForTesting("Edit", "Find");
                Pump();
                long now = grid.FirstRowForTesting;
                ok &= Check($"at line {line} the bar still takes its room off the top ({was} -> {now})",
                            now == was + taken);
                ok &= Check($"and the last line showing does not change ({was + wasRows - 1})",
                            now + grid.VisibleRowCountForTesting - 1 == was + wasRows - 1);
                form.CloseFindForTesting();
                Pump();
                ok &= Check($"and closing puts it back ({grid.FirstRowForTesting})",
                            grid.FirstRowForTesting == was);
            }

            // Reported straight after this was first built: opening the bar on a file that was still being
            // read pushed the log down after all. While a file streams the view is pinned to a LINE, and
            // laying the window out again re-arms that pin at the row the view was showing - which pulls it
            // straight back. Reproduced here by arming the pin from inside the change, which is exactly
            // where it happens; on a file this size nothing is ever busy long enough to catch it live.
            grid.GoToLine(2000);
            Pump();
            long settled = grid.FirstRowForTesting;
            grid.KeepTextStillAcross(2, () =>
                grid.SetViewAnchor(new ViewAnchor(doc.RowToLine(settled), 0, -1)));
            Pump();
            ok &= Check($"a pin armed while the view is resized does not pull it back " +
                        $"({settled} -> {grid.FirstRowForTesting})",
                        grid.FirstRowForTesting == settled + 2);
            return ok;
        }
        finally
        {
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            Pump();
            try { File.Delete(log); } catch { /* ignore */ }
        }
    }

    /// <summary>Asking to find something with part of a line picked out means "find that". Whole lines do
    /// not: selecting them is how you copy or mark them, and a line's worth of log is no kind of term.</summary>
    private static bool RunFindSeedChecks()
    {
        Line("-- the find box takes what is picked out --");

        string log = Path.Combine(Path.GetTempPath(), "cascade_seed_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < 40; i++) sb.Append($"line {i:00} req-abc123 GET /v1/orders/99 -> 200 in 41ms\n");
        File.WriteAllText(log, sb.ToString(), new UTF8Encoding(false));

        MainForm? form = null;
        try
        {
            form = new MainForm(new AppSettings(), new MachineState(), new[] { log })
            {
                NoSavePrompt = true,
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(1100, 700),
            };
            form.Show();
            Pump();
            var doc = form.DocForTesting;
            for (int i = 0; i < 60 && doc.CompletedLineCount < 40; i++) { Thread.Sleep(20); Pump(); }
            bool ok = Check("the file is open", doc.CompletedLineCount >= 40, doc.CompletedLineCount.ToString());
            if (!ok) return false;

            var grid = form.GridForTesting;
            var bar = form.FindBarForTesting;
            string text = doc.GetLineText(2);
            int reqAt = text.IndexOf("req-abc123", StringComparison.Ordinal);

            grid.DragForTesting(2, grid.XForCharForTesting(2, reqAt), grid.XForCharForTesting(2, reqAt + 10));
            Pump();
            ok &= Check("a part of a line is picked out", grid.SelectedText == "req-abc123",
                        grid.SelectedText ?? "(none)");

            form.ClickMenuForTesting("Edit", "Find");
            Pump();
            ok &= Check("and asking to find takes it as the term", bar.TermForTesting() == "req-abc123",
                        bar.TermForTesting());

            // A whole line, on the other hand, must leave the box alone - not replace a perfectly good term
            // with fifty characters of log.
            form.CloseFindForTesting();
            Pump();
            grid.ClickForTesting(3, grid.XForCharForTesting(3, reqAt));
            Pump();
            ok &= Check("clicking selects the whole line instead", !grid.HasCharSelection);

            form.ClickMenuForTesting("Edit", "Find");
            Pump();
            ok &= Check("and asking to find leaves the term as it was", bar.TermForTesting() == "req-abc123",
                        bar.TermForTesting());
            return ok;
        }
        finally
        {
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            Pump();
            try { File.Delete(log); } catch { /* ignore */ }
        }
    }

    /// <summary>The letter Alt activates for a caption, or null when it declares none.</summary>
    private static char? MnemonicOf(string text)    {
        int i = text.IndexOf('&');
        return i >= 0 && i + 1 < text.Length && text[i + 1] != '&' ? char.ToLowerInvariant(text[i + 1]) : null;
    }

    /// <summary>Drawing handles have to be given back at a moment we choose, not whenever a collection
    /// happens to run. A font handed to a control is not disposed with it, and a finalizer will get there
    /// eventually - so counting handles proves nothing, and these ask the objects themselves instead.</summary>
    private static bool RunResourceChecks()
    {
        Line("-- drawing handles are given back --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_gdi_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, string.Concat(Enumerable.Range(0, 200).Select(i => $"line {i}\n")),
                          new UTF8Encoding(false));

        static bool LetGo(Font font)
        {
            try { _ = font.Height; return false; }
            catch (ArgumentException) { return true; }   // what a disposed Font answers
        }

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();
            var settings = new AppSettings();
            var grid = new LineGridControl { Dock = DockStyle.Fill };
            host = new Form { ClientSize = new Size(600, 400), Opacity = 0, FormBorderStyle = FormBorderStyle.None };
            host.Controls.Add(grid);
            grid.Attach(doc, settings);
            host.Show();
            Pump();

            // Zooming rebuilds every font. There are four of them and a family behind them, and a user
            // holding Ctrl and turning the wheel does this dozens of times a minute.
            var wasRegular = grid.FontForTesting;
            settings.ZoomPercent = 150;
            grid.RebuildFonts();
            // Only the faces are asked about. Disposing the family they were cut from is worth doing, but it
            // cannot be checked this way: GDI+ keeps it alive behind any font still holding it, so it
            // answers happily either way.
            bool ok = Check("rebuilding the fonts lets go of the ones it replaces", LetGo(wasRegular));
            ok &= Check("and makes a working one to draw with", grid.FontForTesting.Height > 0,
                        grid.FontForTesting.Height.ToString());

            // A log view is built per window, and closing one has to give its fonts back at that moment.
            var spare = new LineGridControl();
            spare.Attach(doc, settings);
            var spareFont = spare.FontForTesting;
            spare.Dispose();
            ok &= Check("and closing a log view lets go of its fonts", LetGo(spareFont));

            // The text is drawn straight onto the device context now, through handles of its own: a face
            // per style and a brush per colour. Those are GDI handles, not objects - nothing collects them,
            // and a process that loses ten thousand of them is killed. Zooming rebuilds every face and every
            // paint asks for brushes, so this does both, many times, and counts what the process holds.
            //
            // Every step a different size, not five sizes over and over: the faces are filed under the font
            // they were cut from, and a font that comes round again finds the one it had. Repeating five
            // sizes therefore leaks five handles however long the loop runs - the shape of a real leak but
            // not the size of one. Dragging the zoom the way a reader drags it is what tells them apart.
            int handlesBefore = GdiHandles();
            for (int i = 0; i < 40; i++)
            {
                settings.ZoomPercent = 60 + i * 5;
                grid.RebuildFonts();
                grid.Invalidate();
                grid.Update();
            }
            settings.ZoomPercent = 100;
            grid.RebuildFonts();
            Pump();
            int handlesAfter = GdiHandles();
            ok &= Check($"and drawing through GDI's own handles hands every one of them back " +
                        $"({handlesBefore} held before 40 rebuilds and repaints, {handlesAfter} after)",
                        handlesBefore == 0 || handlesAfter <= handlesBefore + 12);

            // The find bar used to cut a font of its own for the term box, at a different size from the rest
            // of the row. It draws everything in the ambient font now, so there is nothing for it to give
            // back - and nothing on the row may quietly go back to one of its own.
            var find = new FindBar((_, _) => { });
            var privately = AllControls(find).Where(c => !ReferenceEquals(c.Font, find.Font)).ToList();
            ok &= Check("the find bar draws its whole row in one font, which it does not own",
                        privately.Count == 0,
                        string.Join(", ", privately.Select(c => $"{c.GetType().Name} {c.Font.Name} {c.Font.SizeInPoints}pt")));
            find.Dispose();

            using var one = new FilterEditDialog(new Filter { Match = { Text = "x" } }, isNew: true, Array.Empty<Filter>());
            using var two = new FilterEditDialog(new Filter { Match = { Text = "y" } }, isNew: true, Array.Empty<Filter>());
            ok &= Check("and the filter dialog shares one font rather than making another each time",
                        ReferenceEquals(one.FontForTesting, two.FontForTesting));
            return ok;
        }
        finally
        {
            host?.Dispose();
            doc.Dispose();
            try { File.Delete(path); } catch { }
        }
    }

    /// <summary>The menu items a user reaches for, clicked through a real window on a real file. Almost
    /// everything else here builds a control directly; this is the only thing that exercises the wiring
    /// between the menu, the settings and the three panes - which is where a command that quietly stopped
    /// doing anything would hide.</summary>
    private static bool RunMenuActionChecks()
    {
        Line("-- the menus --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_menus_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < 400; i++)
            sb.Append(i % 5 == 0 ? $"ERROR line {i}\n" : $"plain line {i}\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        string filters = Path.Combine(Path.GetTempPath(), "cascade_st_menus_" + Guid.NewGuid().ToString("N") + ".cascade");
        File.WriteAllText(filters, """
            { "filters": [ { "id": "f1", "enabled": true, "matchType": "Text", "text": "ERROR",
                             "style": { "background": "#FFD0D0" } } ] }
            """, new UTF8Encoding(false));

        var settings = new AppSettings();
        var state = new MachineState();
        MainForm? form = null;
        try
        {
            form = new MainForm(settings, state, new[] { path, "/Filters:" + filters })
            {
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(1000, 760),
            };
            form.NoSavePrompt = true;
            form.Show();
            Pump();
            var doc = form.DocForTesting;
            for (int i = 0; i < 60 && doc.CompletedLineCount < 400; i++) { Thread.Sleep(20); Pump(); }

            bool ok = Check("the file is open and indexed", doc.CompletedLineCount == 400,
                            $"{doc.CompletedLineCount} lines");
            ok &= Check("and its filters came with it", doc.Filters.EnumerateDepthFirst().Count() == 1,
                        $"{doc.Filters.EnumerateDepthFirst().Count()} filters");
            for (int i = 0; i < 80 && doc.IsBusy; i++) { Thread.Sleep(20); Pump(); }
            ok &= Check("which match the lines they should", doc.MatchedLineCount == 80,
                        $"{doc.MatchedLineCount} of 400 matched");

            var grid = form.GridForTesting;

            // View: each of these has to change what is on screen, not merely tick a box.
            ok &= Check("View > Show Only Filtered Lines hides the rest",
                        form.ClickMenuForTesting("View", "Show Only Filtered Lines") && doc.FilteredMode &&
                        doc.RowCount == 80, $"filtered {doc.FilteredMode}, {doc.RowCount} rows");
            ok &= Check("and again puts them back",
                        form.ClickMenuForTesting("View", "Show Only Filtered Lines") && !doc.FilteredMode &&
                        doc.RowCount == 400, $"filtered {doc.FilteredMode}, {doc.RowCount} rows");

            int gutter = grid.GutterWidthForTesting;
            ok &= Check("View > Show Line Numbers takes the numbers away",
                        form.ClickMenuForTesting("View", "Show Line Numbers") &&
                        !settings.ShowLineNumbers && grid.GutterWidthForTesting < gutter,
                        $"gutter {gutter} -> {grid.GutterWidthForTesting}");
            ok &= Check("and brings them back",
                        form.ClickMenuForTesting("View", "Show Line Numbers") &&
                        settings.ShowLineNumbers && grid.GutterWidthForTesting == gutter,
                        $"gutter is {grid.GutterWidthForTesting}, was {gutter}");

            ok &= Check("View > Show Match Map takes the map away",
                        form.ClickMenuForTesting("View", "Show Match Map") &&
                        !settings.ShowMatchMap && grid.MapWidthForTesting == 0,
                        $"map {grid.MapWidthForTesting}px");
            ok &= Check("and the scrollbar stays behind", grid.VerticalScrollBarVisibleForTesting);
            ok &= Check("and it comes back",
                        form.ClickMenuForTesting("View", "Show Match Map") &&
                        settings.ShowMatchMap && grid.MapWidthForTesting > 0,
                        $"map {grid.MapWidthForTesting}px");

            ok &= Check("View > Word Wrap breaks the long lines up",
                        form.ClickMenuForTesting("View", "Word Wrap") && settings.WordWrap && grid.Wrapping);
            ok &= Check("and takes the sideways scrollbar away", !grid.HScrollBarForTesting.Visible);
            ok &= Check("and the log still holds a whole number of lines",
                        (form.SplitForTesting.SplitterDistance - grid.ChromeHeight) % grid.RowPitch == 0,
                        $"{form.SplitForTesting.SplitterDistance - grid.ChromeHeight}px of text, " +
                        $"a line is {grid.RowPitch}px");
            ok &= Check("and turning it off puts the scrollbar back",
                        form.ClickMenuForTesting("View", "Word Wrap") && !settings.WordWrap &&
                        grid.HScrollBarForTesting.Visible);

            int zoom = settings.ZoomPercent;
            ok &= Check("View > Zoom In makes the text bigger",
                        form.ClickMenuForTesting("View", "Zoom In") && settings.ZoomPercent > zoom,
                        $"{zoom}% -> {settings.ZoomPercent}%");
            ok &= Check("View > Reset Zoom puts it back",
                        form.ClickMenuForTesting("View", "Reset Zoom") && settings.ZoomPercent == 100,
                        $"{settings.ZoomPercent}%");

            // Docking: every side, and the log still measures in whole lines wherever the list is.
            foreach (string where in new[] { "Dock Left", "Dock Right", "Dock Top", "Dock Bottom" })
            {
                ok &= Check($"View > Filter List Location > {where}",
                            form.ClickMenuForTesting("View", "Filter List Location", where));
                Pump();
                bool sideways = where is "Dock Left" or "Dock Right";
                ok &= Check($"  turns the divider the right way for {where}",
                            form.SplitForTesting.Orientation ==
                            (sideways ? Orientation.Vertical : Orientation.Horizontal),
                            form.SplitForTesting.Orientation.ToString());
                if (!sideways)
                    ok &= Check($"  and leaves the log on a whole line with the list {where[5..]}",
                                (grid.Height - grid.ChromeHeight) % grid.RowPitch == 0,
                                $"{grid.Height - grid.ChromeHeight}px of text, a line is {grid.RowPitch}px");
                ok &= Check($"  and the log is still on screen with the list {where[5..]}",
                            grid.Width > 50 && grid.Height > 50, $"{grid.Width}x{grid.Height}");
            }
            form.ClickMenuForTesting("View", "Filter List Location", "Dock Bottom");
            Pump();

            // Filters: the two that touch every filter at once.
            ok &= Check("Filters > Disable All switches them all off",
                        form.ClickMenuForTesting("Filters", "Disable All") &&
                        doc.Filters.EnumerateDepthFirst().All(f => !f.Enabled));
            for (int i = 0; i < 80 && doc.IsBusy; i++) { Thread.Sleep(20); Pump(); }
            // With nothing to filter by, the view falls back to the whole file rather than to nothing -
            // which is the difference between a log viewer and a blank window.
            ok &= Check("and the whole file shows rather than none of it", doc.MatchedLineCount == 400,
                        doc.MatchedLineCount.ToString());
            ok &= Check("even with only matching lines on show",
                        form.ClickMenuForTesting("View", "Show Only Filtered Lines") && doc.RowCount == 400,
                        $"{doc.RowCount} rows");
            form.ClickMenuForTesting("View", "Show Only Filtered Lines");
            ok &= Check("Filters > Enable All switches them back on",
                        form.ClickMenuForTesting("Filters", "Enable All") &&
                        doc.Filters.EnumerateDepthFirst().All(f => f.Enabled));
            for (int i = 0; i < 80 && doc.IsBusy; i++) { Thread.Sleep(20); Pump(); }
            ok &= Check("and the matches are back", doc.MatchedLineCount == 80,
                        doc.MatchedLineCount.ToString());

            // Edit: copying takes what is selected, and the line numbers only when asked.
            // Edit: copying takes what is selected, and the line numbers only when asked. The clipboard is
            // shared with everything else on the machine, so when it cannot be read at all this says so
            // rather than reporting a failure it did not actually observe.
            grid.SelectRowForAccessibility(5);
            Pump();
            form.ClickMenuForTesting("Edit", "Copy");
            string plain = SafeClipboardText();
            form.ClickMenuForTesting("Edit", "Copy with Line Numbers");
            string numbered = SafeClipboardText();
            if (plain.Length == 0 && numbered.Length == 0)
                Line("   (the clipboard would not open; skipped the copy checks)");
            else
            {
                ok &= Check("Edit > Copy takes the line", plain.Trim() == "ERROR line 5", $"'{plain.Trim()}'");
                ok &= Check("Edit > Copy with Line Numbers puts the number in front of it",
                            numbered.Trim() == "6\tERROR line 5", $"'{numbered.Trim()}'");
            }

            // File > Close Filters empties the list and stops it being loaded again next time.
            ok &= Check("File > Close Filters empties the list",
                        form.ClickMenuForTesting("File", "Close Filters") &&
                        !doc.Filters.EnumerateDepthFirst().Any(),
                        $"{doc.Filters.EnumerateDepthFirst().Count()} filters left");
            ok &= Check("and forgets it for next time", state.LastFilterFile is null,
                        state.LastFilterFile ?? "(null)");
            ok &= Check("and the whole file is on show again", doc.RowCount == 400, doc.RowCount.ToString());

            form.Close();
            form = null;
            return ok;
        }
        finally
        {
            form?.Dispose();
            try { File.Delete(path); File.Delete(filters); } catch { }
        }
    }

    /// <summary>The clipboard is shared with everything else running, so a read can simply fail.</summary>
    private static string SafeClipboardText()
    {
        for (int i = 0; i < 5; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch { Thread.Sleep(60); }
        }
        return "";
    }

    /// <summary>The divider between the log and the filter list has to land where the log holds a whole
    /// number of lines. Anywhere else leaves a strip of dead space under the last one, which reads as a line
    /// that failed to draw rather than as a gap.</summary>
    private static bool RunSplitterChecks()
    {
        Line("-- the divider snaps to whole lines --");

        using var form = new MainForm(new AppSettings(), new MachineState(), Array.Empty<string>())
        {
            Opacity = 0,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Size = new Size(900, 700),
        };
        form.NoSavePrompt = true;
        form.Show();
        Pump();

        var grid = form.GridForTesting;
        var split = form.SplitForTesting;
        int pitch = grid.RowPitch, chrome = grid.ChromeHeight;
        int total = split.Height - split.SplitterWidth;

        bool ok = Check("the log is in the first panel, with the filter list under it",
                        split.Orientation == Orientation.Horizontal && !split.Panel1Collapsed,
                        $"{split.Orientation}, panel1 collapsed {split.Panel1Collapsed}");
        ok &= Check("a line has a height to snap to", pitch > 1, pitch.ToString());

        int Lines(int distance) => distance - chrome;
        ok &= Check("it opens holding a whole number of lines", Lines(split.SplitterDistance) % pitch == 0,
                    $"{Lines(split.SplitterDistance)}px of text, a line is {pitch}px");
        ok &= Check("and about seven tenths of the window, as it always has",
                    split.SplitterDistance > total * 0.6 && split.SplitterDistance < total * 0.85,
                    $"{split.SplitterDistance} of {total}");

        // Dragging lands wherever the pointer is; the divider has to settle on the nearest line boundary.
        int start = split.SplitterDistance;
        foreach (int nudge in new[] { 3, 7, -5, -11, 1 })
        {
            int asked = start + nudge;
            try { split.SplitterDistance = asked; } catch { continue; }
            Pump();
            int got = split.SplitterDistance;
            ok &= Check($"a drag to {nudge:+#;-#;0} settles on a line boundary", Lines(got) % pitch == 0,
                        $"asked {asked}, settled at {got}, which is {Lines(got)}px of text");
            ok &= Check($"and on the nearest one", Math.Abs(got - asked) <= pitch / 2 + 1,
                        $"asked {asked}, settled at {got}, a line is {pitch}px");
        }

        // Wrapping hides the sideways scrollbar, so the chrome the divider measures from changes under it.
        // Rounding the same way every time then hands the log a line on each toggle and never gives one
        // back, and the filter list is eaten a line at a time.
        int before = split.SplitterDistance;
        var walked = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            form.ClickMenuForTesting("View", "Word Wrap");
            Pump();
            walked.Add(split.SplitterDistance);
        }
        ok &= Check("toggling word wrap does not walk the divider",
                    walked.TrueForAll(d => Math.Abs(d - before) <= pitch),
                    $"started at {before}, went {string.Join(" -> ", walked)}");
        ok &= Check("and leaves it where it started", split.SplitterDistance == before,
                    $"{before} -> {split.SplitterDistance}");

        // Growing the window must not leave the log holding part of a line either.
        form.Size = new Size(900, 743);
        Pump();
        ok &= Check("resizing the window leaves it on a line boundary too",
                    Lines(split.SplitterDistance) % pitch == 0,
                    $"{Lines(split.SplitterDistance)}px of text at window height {form.Height}");

        form.Close();
        return ok;
    }

    /// <summary>Win+Left has to leave the window filling half the screen, the way it does for every other
    /// app - it used to leave it at MinimumSize in the corner instead.
    ///
    /// <para>Snapping un-maximises the window as the first half of what it does, and WinForms hands back any
    /// bounds that were set while the window WAS maximised at exactly that moment, over the top of the
    /// rectangle the snap just gave it. So the check is about what the constructor leaves behind, and the
    /// window is therefore built exactly as a user gets it and not adjusted afterwards. What the snap does
    /// to it is done here in one operation, which is the single WM_WINDOWPOSCHANGED the bug rode in on;
    /// driving the real hotkey would need the foreground and would snap whatever the reader was in.</para></summary>
    private static bool RunWindowSnapChecks()
    {
        Line("-- Win+Left snaps the window to half the screen --");

        using var form = ShippedWindow();
        form.Show();
        Pump();

        bool ok = Check($"the window opens maximised ({form.WindowState})",
                        form.WindowState == FormWindowState.Maximized);

        var work = Screen.FromControl(form).WorkingArea;
        // Windows will not hand back less than the window says it can be worked in.
        var want = new Rectangle(work.Location,
                                 new Size(Math.Max(work.Width / 2, form.MinimumSize.Width),
                                          Math.Max(work.Height, form.MinimumSize.Height)));

        var placement = new WINDOWPLACEMENT { Length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(form.Handle, ref placement);
        placement.ShowCmd = SW_SHOWNOACTIVATE;
        placement.NormalPosition = RECT.From(want);
        SetWindowPlacement(form.Handle, ref placement);
        Pump();

        ok &= Check($"and keeps the half of it that the snap gave it ({form.Bounds})",
                    form.Bounds == want, $"asked for {want}, work area {work}");
        form.Close();

        // The other half of having a size before being maximised: this is what Win+Down and a double-clicked
        // title bar restore to, and a window that opens at MinimumSize has nothing better to offer them.
        // A second window, because the snap above has already rewritten the first one's normal position.
        using var restored = ShippedWindow();
        restored.Show();
        Pump();
        restored.WindowState = FormWindowState.Normal;
        Pump();
        var room = Screen.FromControl(restored).WorkingArea;
        ok &= Check($"and un-maximises to a window worth having ({restored.Size} of {room.Size})",
                    restored.Width > room.Width / 2 && restored.Height > room.Height / 2,
                    $"minimum is {restored.MinimumSize}");
        restored.Close();

        return ok;
    }

    /// <summary>A main window exactly as a user gets one. The off-screen parking the UI suite asks for is
    /// stood down for it: parked beyond the last monitor there is nothing to snap to, and it is the
    /// untouched constructor that is on trial here. Nothing appears - the window is transparent.</summary>
    private static MainForm ShippedWindow()
    {
        string? offScreen = Environment.GetEnvironmentVariable("CASCADE_TEST_OFFSCREEN");
        Environment.SetEnvironmentVariable("CASCADE_TEST_OFFSCREEN", null);
        try { return new MainForm(new AppSettings(), new MachineState(), Array.Empty<string>()) { Opacity = 0, NoSavePrompt = true }; }
        finally { Environment.SetEnvironmentVariable("CASCADE_TEST_OFFSCREEN", offScreen); }
    }

    private const int SW_SHOWNOACTIVATE = 4;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public static RECT From(Rectangle r) => new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int Length, Flags, ShowCmd;
        public Point MinPosition, MaxPosition;
        public RECT NormalPosition;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);

    /// <summary>Leaves the maximised state and takes the new rectangle in one go, which is what snapping
    /// does to a maximised window and what an un-maximise followed by a move would not be.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);

    /// <summary>Letting go of a very large log costs the kernel two thirds of a second - it has to hand back
    /// every resident page of the mapping - and it happens on the thread that draws. So the window has to be
    /// down BEFORE that starts, or the reader sits looking at an app that will not close. WinForms disposes
    /// a top-level form while its window is still up, which is why closing hides it first.</summary>
    private static bool RunClosingChecks()
    {
        Line("-- the window goes before the file is let go --");

        string log = Path.Combine(Path.GetTempPath(), "cascade_closing_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(log, Enumerable.Range(1, 200).Select(i => $"line {i}"));

        bool ok;
        try
        {
            using var form = new MainForm(new AppSettings(), new MachineState(), new[] { log })
            {
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(900, 700),
            };
            form.NoSavePrompt = true;
            form.Show();
            Pump();

            var doc = form.DocForTesting;
            ok = Check("the file is open", doc.CompletedLineCount > 0, doc.CompletedLineCount.ToString());
            ok &= Check("and not let go while it is being read", !doc.IsDisposed);

            // Subscribed after the form's own handler, so it runs after it: this is the state the reader is
            // left in for however long the release takes.
            bool windowStillUp = true, alreadyLetGo = true;
            form.FormClosing += (_, _) => { windowStillUp = form.Visible; alreadyLetGo = doc.IsDisposed; };

            form.Close();
            Pump();

            ok &= Check("the window is down by the time closing finishes", !windowStillUp);
            ok &= Check("and the file has not been let go yet, which is the slow part", !alreadyLetGo);
            ok &= Check("but it is let go by the time the window is disposed", doc.IsDisposed);
        }
        finally
        {
            try { File.Delete(log); } catch { /* best effort */ }
        }

        return ok;
    }

    /// <summary>Dropping a log in from Explorer replaces the one on screen and keeps the filters - one filter
    /// set, several files to try it against. A drop target is registered per window and a child that has not
    /// asked for drops refuses them rather than passing them up, so which controls opt in is part of the
    /// behaviour and is checked here too.</summary>
    private static bool RunFileDropChecks()
    {
        Line("-- dropping files on the window --");

        string dir = Path.Combine(Path.GetTempPath(), "cascade_st_drop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string logA = Path.Combine(dir, "a.log"), logB = Path.Combine(dir, "b.log");
        File.WriteAllLines(logA, Enumerable.Range(1, 40).Select(i => $"alpha line {i}"));
        File.WriteAllLines(logB, Enumerable.Range(1, 90).Select(i => $"bravo line {i}"));

        string filterFile = Path.Combine(dir, "two.cascade");
        var saved = new FilterCollection();
        saved.Roots.Add(new Filter { Match = { Text = "alpha" }, Enabled = true });
        saved.Roots.Add(new Filter { Match = { Text = "bravo" }, Enabled = true });
        CascadeFile.Save(filterFile, saved);

        bool ok;
        try
        {
            using var form = new MainForm(new AppSettings(), new MachineState(), new[] { logA })
            {
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(900, 700),
            };
            form.NoSavePrompt = true;
            form.Show();
            Pump();

            var doc = form.DocForTesting;
            var grid = form.GridForTesting;
            var tree = form.FilterTreeForTesting;
            ok = Check($"the first file is open ({Path.GetFileName(doc.FilePath)})", doc.FilePath == logA);
            ok &= Check("the window accepts drops", form.AllowDrop);
            ok &= Check("and so does the log view, which is what covers it", grid.AllowDrop);

            ok &= Check("a dragged file is offered as a copy",
                        EffectOfDragOver(grid, Files(logB)) == DragDropEffects.Copy);
            ok &= Check("a drag carrying no file is refused",
                        EffectOfDragOver(grid, new DataObject(DataFormats.Text, "not a file")) == DragDropEffects.None);
            ok &= Check("so is a folder", EffectOfDragOver(grid, Files(dir)) == DragDropEffects.None);
            ok &= Check("so is a path that is not there",
                        EffectOfDragOver(grid, Files(Path.Combine(dir, "gone.log"))) == DragDropEffects.None);

            // The point of the gesture: the file changes, the filters do not.
            Drop(grid, Files(filterFile));
            Pump();
            int filtersBefore = doc.Filters.Roots.Count;
            ok &= Check($"dropping a filter file loads it ({filtersBefore} filters)", filtersBefore == 2);

            Drop(grid, Files(logB));
            Pump();
            doc.WaitForIndex();
            Pump();
            ok &= Check($"dropping a log replaces the one on screen ({Path.GetFileName(doc.FilePath)})", doc.FilePath == logB);
            ok &= Check($"with the new file's lines ({doc.CompletedLineCount})", doc.CompletedLineCount == 90);
            ok &= Check($"and the filters left alone ({doc.Filters.Roots.Count})", doc.Filters.Roots.Count == 2);
            ok &= Check("which the list still shows", tree.VisibleFiltersForTesting.Count == 2);

            // Both at once, which is how a filter set and the file to try it on tend to arrive.
            doc.Filters.Roots.Clear();
            tree.Rebuild();
            Drop(grid, Files(logA, filterFile));
            Pump();
            doc.WaitForIndex();
            Pump();
            ok &= Check($"dropping a log and a filter file together does both " +
                        $"({Path.GetFileName(doc.FilePath)}, {doc.Filters.Roots.Count} filters)",
                        doc.FilePath == logA && doc.Filters.Roots.Count == 2);

            // The filter pane is a drop target of its own, for reordering filters, so it has to answer for
            // files itself rather than letting them fall through to the window.
            ok &= Check("the filter pane offers a copy for a file",
                        tree.DragEffectForTesting(DragArgs(Files(logB))) == DragDropEffects.Copy);
            ok &= Check("and refuses a drag it has no use for",
                        tree.DragEffectForTesting(DragArgs(new DataObject(DataFormats.Text, "no"))) == DragDropEffects.None);
            tree.DropOnTreeForTesting(DragArgs(Files(logB)));
            Pump();
            doc.WaitForIndex();
            Pump();
            ok &= Check($"and opens a file dropped on it ({Path.GetFileName(doc.FilePath)})", doc.FilePath == logB);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }

        return ok;
    }

    private static DataObject Files(params string[] paths) => new(DataFormats.FileDrop, paths);

    private static DragEventArgs DragArgs(IDataObject data)
        => new(data, 0, 0, 0, DragDropEffects.All, DragDropEffects.None);

    /// <summary>What the control would do with this drag, asked the way Windows asks: DragOver is what
    /// decides the cursor while the pointer is over it.</summary>
    private static DragDropEffects EffectOfDragOver(Control target, IDataObject data)
    {
        var e = DragArgs(data);
        RaiseDragEvent(target, "OnDragOver", e);
        return e.Effect;
    }

    private static void Drop(Control target, IDataObject data) => RaiseDragEvent(target, "OnDragDrop", DragArgs(data));

    /// <summary>Drag events cannot be staged from a test - a real one comes from the shell through OLE - so
    /// the control is asked to raise its own, which runs the handlers the app attached to it.</summary>
    private static void RaiseDragEvent(Control target, string method, DragEventArgs e)
        => typeof(Control).GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
           .Invoke(target, [e]);

    /// <summary>An Alt key has to be unique within its own menu. Where two items claim the same letter
    /// Windows cycles between them rather than running either, so the key must be pressed twice and then
    /// Enter - and nothing complains, which is how five of these had quietly accumulated.</summary>
    /// <summary>The pattern box is drawn in the colours a matching line would take. That is the only place
    /// the effect of leaving a box unticked - inherit - can be seen, so it is worth pinning down.</summary>
    private static bool RunColorPreviewChecks()
    {
        Line("-- the filter dialog's colours --");

        var defaults = new ResolvedStyle(new RgbColor(0, 0, 0), new RgbColor(255, 255, 255), false, false);
        RgbColor yellow = new(0xFF, 0xFF, 0x00), navy = new(0x00, 0x00, 0x80), moss = new(0x20, 0x60, 0x20);
        var parent = new Filter
        {
            Match = { Text = "ERROR" },
            Style = { Foreground = yellow, Background = navy, Bold = true }
        };
        var child = new Filter { Match = { Text = "disk" } };
        // Wearing a colour the ring actually offers, so there is something for the palette to leave out. The
        // parent's own colours are picked for the inheritance checks and need not be in the palette at all.
        var worn = new Filter { Match = { Text = "net" }, Style = { Background = LuckyColors.At(0).Back } };

        using var dlg = new FilterEditDialog(child, isNew: true, new[] { parent, worn }, parent, defaults)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Opacity = 0
        };
        dlg.Show();
        Pump();

        static string Say((Color Fore, Color Back, bool Bold, bool Italic, bool Underline) p) =>
            $"#{p.Fore.R:x2}{p.Fore.G:x2}{p.Fore.B:x2} on #{p.Back.R:x2}{p.Back.G:x2}{p.Back.B:x2}" +
            $"{(p.Bold ? " bold" : "")}{(p.Italic ? " italic" : "")}{(p.Underline ? " underlined" : "")}";
        static bool Is(Color c, RgbColor want) => c.R == want.R && c.G == want.G && c.B == want.B;

        var p = dlg.PreviewForTesting;
        bool ok = Check("a filter that sets no colour of its own previews its parent's",
                        Is(p.Fore, yellow) && Is(p.Back, navy), Say(p));
        ok &= Check("and its parent's bold", p.Bold && !p.Italic, Say(p));

        dlg.SetColorsForTesting(fore: null, back: moss);
        Pump();
        p = dlg.PreviewForTesting;
        ok &= Check("setting one colour leaves the other coming down from above",
                    Is(p.Back, moss) && Is(p.Fore, yellow), Say(p));

        dlg.SetStyleForTesting(bold: false, italic: true);
        Pump();
        p = dlg.PreviewForTesting;
        ok &= Check("and a style turned off beats the parent having it on", !p.Bold && p.Italic, Say(p));

        dlg.SetStyleForTesting(bold: false, italic: true, underline: true);
        Pump();
        p = dlg.PreviewForTesting;
        ok &= Check("and underline is a style of its own", p.Underline && p.Italic && !p.Bold, Say(p));

        // With nothing above it there is nothing to inherit, so the view's own colours show through.
        using var orphan = new FilterEditDialog(new Filter { Match = { Text = "disk" } }, isNew: true,
                                                Array.Empty<Filter>(), null, defaults)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Opacity = 0
        };
        orphan.Show();
        Pump();
        var q = orphan.PreviewForTesting;
        ok &= Check("a filter with no parent previews the view's own colours",
                    Is(q.Fore, defaults.Foreground) && Is(q.Back, defaults.Background), Say(q));

        // The palette is the lucky button's offers laid out at once, so the two must agree about what is
        // still going spare.
        var free = dlg.PaletteForTesting;
        ok &= Check("the palette leaves out what is already worn",
                    free.Count is > 20 && free.Count < LuckyColors.Palette.Count,
                    $"{free.Count} of {LuckyColors.Palette.Count}");
        RgbColor[] inUse = [navy, yellow, LuckyColors.At(0).Back];
        double nearest = free.Count == 0 ? 0 : free.Min(f => inUse.Min(u => LuckyColors.Distance(f.Back, u)));
        ok &= Check("and offers nothing close to a colour in use", nearest > 11, $"{nearest:F1} away at the closest");
        var lucky = LuckyColors.At(LuckyColors.Next(-1, new[] { parent, worn }, child));
        ok &= Check("and covers what the lucky button would hand out next",
                    free.Any(f => LuckyColors.Distance(f.Back, lucky.Back) <= 11), $"#{lucky.Back.ToHex()}");

        // The set is packed offline so that nothing in it looks like anything else in it; taking entries
        // away cannot break that, so it holds for whatever is left once the worn ones go.
        double closest = double.MaxValue;
        var worstPair = (a: 0, b: 0);
        for (int i = 0; i < free.Count; i++)
            for (int j = i + 1; j < free.Count; j++)
            {
                double d = LuckyColors.Distance(free[i].Back, free[j].Back);
                if (d < closest) { closest = d; worstPair = (i, j); }
            }
        ok &= Check("and no two colours in it look alike", free.Count < 2 || closest >= 11,
                    free.Count < 2 ? "too few to say"
                                   : $"#{free[worstPair.a].Back.ToHex()} and #{free[worstPair.b].Back.ToHex()} are {closest:F1} apart");

        // Worked out once, so wearing a colour can only ever take an entry away. Thinning per call instead
        // would let an excluded colour promote a neighbour and shuffle everything after it. Measured
        // against the precomputed palette itself, not against another call - two calls agree with each
        // other however the answer is arrived at.
        var whole = LuckyColors.Palette;
        ok &= Check("and is the whole palette when nothing is worn",
                    LuckyColors.Free(Array.Empty<Filter>(), child).Count == whole.Count,
                    $"{LuckyColors.Free(Array.Empty<Filter>(), child).Count} of {whole.Count}");

        var places = free.Select(f => IndexOfPair(whole, f)).ToList();
        ok &= Check("with what is worn subtracted rather than reshuffled",
                    places.TrueForAll(i => i >= 0) && IsInOrder(places),
                    places.Contains(-1) ? "offered a colour that is not in the palette"
                                        : $"{free.Count} of {whole.Count} kept, in order");

        // Clicking must not move the grid under the pointer. A scrolling panel chases whatever takes focus,
        // and with the whole grid one tall control that means a jump on every click.
        using (var pal = new PaletteDialog(free, "sample text", null, visibleRows: 4)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Opacity = 0
        })
        {
            pal.Show();
            Pump();
            ok &= Check("the palette has more colours than it can show at once", pal.ScrollsForTesting);

            // Deliberately off a row boundary, so the bottom row is cut by the edge - which is the row a
            // click lands on when you click the last colour you can see.
            int cellH = pal.CellForTesting(0).Height;
            pal.ScrollToForTesting(cellH * 3 + cellH / 2);
            Pump();
            int before = pal.ScrollForTesting;
            ok &= Check("and can be scrolled down it", before > 0, before.ToString());

            int straddling = (before + pal.ViewportForTesting) / cellH * 5;
            ok &= Check("and the bottom row of it really is cut off",
                        pal.CellForTesting(straddling).Bottom > before + pal.ViewportForTesting,
                        $"row bottom {pal.CellForTesting(straddling).Bottom}, edge {before + pal.ViewportForTesting}");

            pal.ClickForTesting(straddling);
            Pump();
            ok &= Check("clicking a colour does not scroll the palette under the pointer",
                        pal.ScrollForTesting == before, $"{before} -> {pal.ScrollForTesting}");

            pal.CycleFocusForTesting();
            Pump();
            ok &= Check("nor does the keyboard leaving the grid and coming back",
                        pal.ScrollForTesting == before, $"{before} -> {pal.ScrollForTesting}");

            // Walking off the bottom edge, on the other hand, has to follow - there is nowhere else for the
            // selection to go.
            for (int i = 0; i < 8; i++) { pal.MoveForTesting(Keys.Down); Pump(); }
            ok &= Check("but arrowing off the bottom does scroll it", pal.ScrollForTesting > before,
                        $"{before} -> {pal.ScrollForTesting}");

            // The keys are POSTED rather than sent, because only a posted message goes through the
            // pre-processing that offers a key to the control first and to the dialog second - which is
            // exactly what a control claiming every key interferes with.
            var wasPicked = pal.Picked;
            PostMessage(pal.GridHandleForTesting, WM_KEYDOWN, (IntPtr)(int)Keys.Down, IntPtr.Zero);
            Pump();
            ok &= Check("a real arrow key reaches the grid", pal.Picked != wasPicked,
                        $"#{wasPicked.Back.ToHex()} -> #{pal.Picked.Back.ToHex()}");

            // A key the grid claims never reaches ProcessDialogKey, so claiming everything left the palette
            // with no way out but the mouse.
            PostMessage(pal.GridHandleForTesting, WM_KEYDOWN, (IntPtr)(int)Keys.Escape, IntPtr.Zero);
            Pump();
            ok &= Check("and escape puts the palette away",
                        pal.IsDisposed && pal.DialogResult == DialogResult.Cancel,
                        $"disposed {pal.IsDisposed}, result {pal.DialogResult}");
        }

        // Everything fits, so there must be nothing to scroll - a viewport a couple of pixels short of the
        // content leaves a scrollbar with a hair of travel in it, which reads as "there is more below".
        // The rows are asked for explicitly rather than left to the screen: how much of the palette fits by
        // default depends on how tall the monitor is, and CI's is short.
        var twoRows = free.Take(10).ToList();
        using (var everything = new PaletteDialog(twoRows, "sample text", null, visibleRows: 2)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Opacity = 0
        })
        {
            everything.Show();
            Pump();
            ok &= Check("with room for every colour the palette does not scroll at all",
                        !everything.ScrollsForTesting && everything.ScrollRangeForTesting == 0,
                        $"{everything.CountForTesting} colours in 2 rows, {everything.ScrollRangeForTesting}px of travel");
            ok &= Check("and shows a whole number of rows",
                        everything.ViewportForTesting % everything.CellForTesting(0).Height == 0,
                        $"viewport {everything.ViewportForTesting}px, a row is {everything.CellForTesting(0).Height}px");
            everything.Close();
            Pump();
        }

        // Picking a colour by hand previews it while the picker is open, and cancelling puts back what was
        // there - including the tick, which choosing a colour turns on.
        dlg.SetColorsForTesting(fore: null, back: null);
        Pump();
        var untouched = dlg.PreviewForTesting;
        dlg.PickColorForTesting(foreground: false, previewed: moss, accepted: null);
        Pump();
        ok &= Check("cancelling the colour picker puts back what was there",
                    Say(dlg.PreviewForTesting) == Say(untouched), $"{Say(untouched)} -> {Say(dlg.PreviewForTesting)}");

        RgbColor rust = new(0xB7, 0x41, 0x0E);
        dlg.PickColorForTesting(foreground: false, previewed: moss, accepted: rust);
        Pump();
        ok &= Check("and accepting it keeps what was chosen", Is(dlg.PreviewForTesting.Back, rust),
                    Say(dlg.PreviewForTesting));

        ok &= Check("the dialog is a fixed size",
                    dlg.FormBorderStyle == FormBorderStyle.FixedDialog && !dlg.MaximizeBox,
                    $"{dlg.FormBorderStyle}, maximise {dlg.MaximizeBox}");

        orphan.Close();
        dlg.Close();
        Pump();
        return ok;
    }

    /// <summary>Bold, italic and underline rest on "don't care", so the press that follows has to be the one
    /// being asked for. Windows' own three-state cycle offers "cleared" first, which from "don't care" is
    /// nobody's intention - it takes three presses to turn something on and land back where you started.</summary>
    private static bool RunStyleBoxChecks()
    {
        Line("-- bold, italic and underline --");

        var defaults = new ResolvedStyle(new RgbColor(0, 0, 0), new RgbColor(255, 255, 255), false, false);
        using var dlg = new FilterEditDialog(new Filter { Match = { Text = "declined" } }, isNew: true,
                                             Array.Empty<Filter>(), null, defaults)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Opacity = 0
        };
        dlg.Show();
        Pump();

        static string Say(CheckState s) => s switch
        {
            CheckState.Checked => "set",
            CheckState.Unchecked => "cleared",
            _ => "don't care"
        };
        CheckState[] wanted = [CheckState.Checked, CheckState.Unchecked, CheckState.Indeterminate, CheckState.Checked];

        bool ok = true;
        foreach (var box in dlg.StyleBoxesForTesting)
        {
            string name = box.Text.Replace("&", "", StringComparison.Ordinal).ToLowerInvariant();
            ok &= Check($"{name} starts out inheriting", box.CheckState == CheckState.Indeterminate,
                        Say(box.CheckState));

            var walk = new List<CheckState>();
            for (int i = 0; i < wanted.Length; i++) { box.PressForTesting(); Pump(); walk.Add(box.CheckState); }
            ok &= Check($"and pressing {name} goes set, cleared, don't care, and round again",
                        walk.SequenceEqual(wanted), string.Join(" -> ", walk.Select(Say)));
        }

        // The Alt key and the mouse must agree, or a filter says one thing to the hand and another to the
        // keyboard. This is the same call WinForms makes for Alt+letter, so it is the real dispatch.
        var underline = dlg.StyleBoxesForTesting[2];
        underline.CheckState = CheckState.Indeterminate;
        Pump();
        var altU = typeof(Control).GetMethod("ProcessMnemonic", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var byKey = new List<CheckState>();
        for (int i = 0; i < wanted.Length; i++)
        {
            altU.Invoke(dlg, ['u']);
            Pump();
            byKey.Add(underline.CheckState);
        }
        ok &= Check("and Alt+U walks the same way as a click", byKey.SequenceEqual(wanted),
                    string.Join(" -> ", byKey.Select(Say)));

        // Nothing above it, so "don't care" resolves to the view's own plain text - which is what makes the
        // first press worth having: one press is the whole difference between plain and bold.
        underline.CheckState = CheckState.Indeterminate;
        var bold = dlg.StyleBoxesForTesting[0];
        bold.CheckState = CheckState.Indeterminate;
        Pump();
        ok &= Check("a filter that leaves bold alone previews as it would draw", !dlg.PreviewForTesting.Bold);
        bold.PressForTesting();
        Pump();
        ok &= Check("and one press of it is bold", dlg.PreviewForTesting.Bold);

        dlg.Close();
        Pump();
        return ok;
    }

    /// <summary>The About box is two columns of text that have to read as one line each. A label centres its
    /// caption in the cell and a text box draws its own at the top of its box, so the two drifted apart -
    /// which is only visible in the pixels, never in the layout. It also has to be able to say something
    /// long, because the reason an update check failed is exactly what a bug report needs.</summary>
    private static bool RunAboutBoxChecks()
    {
        Line("-- the about box --");

        const string LongProblem =
            "Last check failed: GitHub answered 403 Forbidden for https://api.github.com/repos/owner/name/" +
            "releases/latest. The saved credential was refused and the anonymous retry ran out of requests.";

        AboutDialog.Row[] rows =
        [
            new("Version", "2026.8.60"),
            new("Location", @"C:\Users\someone\AppData\Local\Programs\Cascade\Cascade.exe"),
            new("Updates", "Up to date"),
            new("Last error", LongProblem, IsProblem: true)
        ];

        using var about = new AboutDialog(rows)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Opacity = 0
        };
        about.Show();
        Pump();

        // Where a control's caption really starts, in the coordinates of the row it shares with the other
        // column. Each control is rendered on its own rather than reading them off a picture of the whole
        // dialog: DrawToBitmap on a Form includes the window frame, so client coordinates would not line up
        // with the picture, and the error only shows as a difference between two rows.
        // What counts as ink is measured against that control's OWN darkest pixel rather than a fixed
        // threshold - a red message and a grey label cross a fixed one at different points in their
        // antialiasing, which is worth several pixels and would read as a misalignment that is not there.
        static int InkTop(Control c)
        {
            using var bmp = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height));
            c.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            var paper = SystemColors.Control;
            int Away(Color p) => Math.Abs(p.R - paper.R) + Math.Abs(p.G - paper.G) + Math.Abs(p.B - paper.B);

            int darkest = 0;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                    darkest = Math.Max(darkest, Away(bmp.GetPixel(x, y)));
            if (darkest < 100) return -1;

            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                    if (Away(bmp.GetPixel(x, y)) >= darkest / 2) return c.Top + y;
            return -1;
        }

        bool ok = true;
        var pairs = about.RowsForTesting.ToList();
        ok &= Check($"every row of the box is there ({pairs.Count})", pairs.Count == rows.Length);

        var offsets = new List<int>();
        foreach (var (label, value) in pairs)
        {
            int a = InkTop(label), b = InkTop(value);
            offsets.Add(a < 0 || b < 0 ? 99 : b - a);
            Line($"   ({label.Text}: label at {label.Top} ink at {a}, " +
                 $"value at {value.Top} ({value.Height}px tall) ink at {b})");
        }
        ok &= Check("the label and the value of a row are written on the same line",
                    offsets.Count > 0 && offsets.TrueForAll(d => Math.Abs(d) <= 1),
                    "offsets " + string.Join(", ", offsets));

        // A long message has to be readable, not clipped at the edge of the dialog. Its box is the one that
        // stands more than one line tall, and the row it is in has to be tall enough to hold it.
        var problem = pairs[^1].Value;
        int oneLine = pairs[0].Value.Height;
        ok &= Check($"a long message wraps rather than being cut off ({problem.Height}px against {oneLine}px)",
                    problem.Height >= oneLine * 2);
        ok &= Check("and all of it is really there", problem.Text == LongProblem);
        ok &= Check("and it is coloured as the problem it is", problem.ForeColor == Color.Firebrick,
                    problem.ForeColor.ToString());
        ok &= Check("while an ordinary value is not", pairs[0].Value.ForeColor != Color.Firebrick);

        // Selectable, so it can be pasted into a bug report - but out of the tab order, so the box does not
        // open with a value highlighted.
        ok &= Check("values can be selected and copied",
                    pairs.TrueForAll(p => p.Value is TextBox { ReadOnly: true, TabStop: false }));

        int cap = about.LogicalToDeviceUnits(620);
        ok &= Check($"and one long line does not stretch the box off the screen ({problem.Width}px)",
                    problem.Width <= cap && about.Width <= Screen.PrimaryScreen!.WorkingArea.Width,
                    $"value column capped at {cap}px, dialog {about.Width}px");

        // What the Updates rows say in each state updating can be in. A check that has not finished must not
        // be reported as "up to date" - that is a guess dressed up as a fact - and a failure has to hand over
        // the whole reason, which is the one thing a bug report needs and the one thing it used to swallow.
        static string Said(List<AboutDialog.Row> rows)
            => string.Join(" | ", rows.Select(r => $"{r.Label}: {r.Value}"));

        var running = AboutDialog.UpdateRows(finished: false, pending: null, error: null, note: null);
        ok &= Check("a check still running says so", running.Count == 1 && running[0].Value.StartsWith("Checking", StringComparison.Ordinal),
                    Said(running));

        var clean = AboutDialog.UpdateRows(finished: true, pending: null, error: null, note: null);
        ok &= Check("a check that found nothing newer says it is up to date",
                    clean.Count == 1 && clean[0].Value.Contains("Up to date", StringComparison.Ordinal), Said(clean));

        var waiting = AboutDialog.UpdateRows(finished: true, pending: new Version(2026, 8, 61), null, null);
        ok &= Check("an installed update says when it takes effect",
                    waiting.Count == 1 && waiting[0].Value.Contains("2026.8.61", StringComparison.Ordinal)
                        && waiting[0].Value.Contains("next time", StringComparison.Ordinal), Said(waiting));

        var failed = AboutDialog.UpdateRows(finished: true, pending: null, error: LongProblem, note: "carried on anonymously");
        ok &= Check("a failed check gives the reason a row of its own, marked as a problem",
                    failed.Count == 3 && failed[1].Value == LongProblem && failed[1].IsProblem, Said(failed));
        ok &= Check("and does not claim to be up to date at the same time",
                    !failed[0].Value.Contains("Up to date", StringComparison.Ordinal), failed[0].Value);
        ok &= Check("and a note is shown as a note, not as a problem",
                    failed[2].Label == "Note" && !failed[2].IsProblem, Said(failed));

        var quiet = AboutDialog.UpdateRows(finished: true, pending: null, error: null, note: "another copy is doing it");
        ok &= Check("standing aside for another copy is not reported as a failure",
                    quiet.Count == 2 && quiet.TrueForAll(r => !r.IsProblem), Said(quiet));

        about.Close();
        Pump();
        return ok;
    }

    private static int IndexOfPair(IReadOnlyList<LuckyColors.Pair> list, LuckyColors.Pair want)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == want) return i;
        return -1;
    }

    private static bool IsInOrder(List<int> values)
    {
        for (int i = 1; i < values.Count; i++)
            if (values[i] <= values[i - 1]) return false;
        return true;
    }

    /// <summary>The credential the updater borrows is the user's own git token, carrying the scopes of their
    /// git rather than of this app. CASCADE_UPDATE_API decides where the updater talks, so it decides where
    /// that token would be sent - which makes this rule the whole of its protection.</summary>
    private static bool RunUpdateCredentialChecks()
    {
        Line("-- the git credential only leaves for github, over https --");

        bool ok = Check("the address the app ships with may have it",
                        AppUpdater.MayUseGitCredential(AppUpdater.DefaultApi), AppUpdater.DefaultApi);
        ok &= Check("and that address is https", AppUpdater.DefaultApi.StartsWith("https://", StringComparison.Ordinal),
                    AppUpdater.DefaultApi);

        // The right host over plain http would put the token on the wire in clear.
        ok &= Check("the same host over http may not", !AppUpdater.MayUseGitCredential("http://api.github.com"));

        // Each of these reads as api.github.com at a glance and none of them is.
        ok &= Check("nor a host that merely starts with it",
                    !AppUpdater.MayUseGitCredential("https://api.github.com.example.org"));
        ok &= Check("nor github worn as a user name",
                    !AppUpdater.MayUseGitCredential("https://api.github.com@example.org"));
        ok &= Check("nor anywhere else at all", !AppUpdater.MayUseGitCredential("https://example.org"));

        // The end-to-end tests point the updater at a loopback stub, which is exactly why the credential
        // cannot simply always be offered: that stub is somebody else's process.
        ok &= Check("nor the loopback stub the tests use",
                    !AppUpdater.MayUseGitCredential("http://127.0.0.1:52000"));

        ok &= Check("case is not a way round it", AppUpdater.MayUseGitCredential("HTTPS://API.GITHUB.COM"));
        ok &= Check("and something that is not an address is refused",
                    !AppUpdater.MayUseGitCredential("api.github.com"));
        return ok;
    }

    /// <summary>The filter search bar: a thing you open on Ctrl+E, use, and dismiss - not a permanent box
    /// taking a line off the top of the list for ever.</summary>
    private static bool RunFilterSearchBarChecks()
    {
        Line("-- the filter search bar --");

        string path = Path.Combine(Path.GetTempPath(), "cascade_st_fsearch_" + Guid.NewGuid().ToString("N") + ".log");
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
                // Shorter than the list on purpose: a match at the end can only be hidden behind the bar
                // if the list has to scroll to reach it.
                ClientSize = new Size(320, 400),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(tree);
            tree.Attach(doc);
            host.Show();
            Pump();

            // Names chosen so each search below has one obvious answer, and the interesting one is last.
            string[] names = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel",
                              "india", "juliet", "kilo", "lima", "mike", "november", "oscar", "papa",
                              "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whisky",
                              "xray", "yankee", "zulu-last"];
            var filters = new FilterCollection();
            foreach (string n in names) filters.Roots.Add(new Filter { Match = new FilterMatch { Text = n } });
            doc.SetFilters(filters);
            tree.Attach(doc);
            Pump();

            var last = filters.Roots[^1];

            bool ok = Check("the list is built", tree.RowCountForTesting == names.Length,
                            tree.RowCountForTesting.ToString());
            ok &= Check("and the search bar starts out of the way", !tree.SearchOpen);
            int fullTree = tree.TreeHeightForTesting;

            // The header is the only thing on screen saying the list can be searched at all. Counted in the
            // band to the RIGHT of the word "Filter", because the two pictures also differ for dull reasons
            // - opening the bar re-lays the columns - and a check that only asks "did anything change" is
            // answered by that instead.
            int wordEnds = tree.HeaderWidthForTesting("Filter") + tree.ColumnsForTesting.FilterRight / 8;
            int InkAfterTheTitle(Bitmap header)
            {
                int ink = 0;
                for (int x = wordEnds; x < Math.Min(header.Width, tree.ColumnsForTesting.FilterRight); x++)
                    for (int y = 0; y < header.Height - 1; y++)      // last row is the rule under the header
                        if (header.GetPixel(x, y).ToArgb() != SystemColors.Control.ToArgb()) ink++;
                return ink;
            }

            int advertised;
            using (var closed = tree.HeaderPictureForTesting()) advertised = InkAfterTheTitle(closed);
            ok &= Check("the header advertises the key while the bar is away", advertised > 0,
                        $"{advertised} pixels of hint past x={wordEnds}");

            tree.ShowSearch();
            Pump();
            int stillThere;
            using (var open = tree.HeaderPictureForTesting()) stillThere = InkAfterTheTitle(open);
            // It stays: as much a reminder of how to get back to the box after clicking away from it as an
            // announcement that the list can be searched at all.
            ok &= Check("and keeps saying it while the bar is up", stillThere == advertised,
                        $"{advertised} pixels -> {stillThere}");

            ok &= Check("opening it shows the bar", tree.SearchOpen);
            ok &= Check("and puts the caret in it", tree.SearchBoxHasFocusForTesting);
            ok &= Check("and the list gives up exactly the bar's height",
                        tree.TreeHeightForTesting == fullTree - tree.SearchBarBoundsForTesting.Height,
                        $"{fullTree} -> {tree.TreeHeightForTesting}, bar is {tree.SearchBarBoundsForTesting.Height}px");
            ok &= Check("which is below the list, not above it",
                        tree.SearchBarBoundsForTesting.Top >= tree.TreeAreaForTesting.Bottom,
                        $"bar at {tree.SearchBarBoundsForTesting.Top}, list ends {tree.TreeAreaForTesting.Bottom}");

            // A rule along its top edge. Without one the bar's right-hand end reads as unmoored, because the
            // list's scrollbar stops short of it and there is nothing else to say where the list ended.
            using (var bar = tree.SearchBarPictureForTesting())
            {
                int ruled = 0;
                for (int x = 0; x < bar.Width; x++)
                {
                    var onTheRule = bar.GetPixel(x, 0);
                    var justBelow = bar.GetPixel(x, Math.Min(2, bar.Height - 1));
                    if (Luma(onTheRule) < Luma(justBelow) - 20) ruled++;
                }
                ok &= Check("with a rule along its top to part it from the list", ruled > bar.Width * 3 / 4,
                            $"{ruled} of {bar.Width} columns ruled");
            }

            tree.TypeSearchForTesting("charlie");
            Pump();
            ok &= Check("typing walks to the match", tree.SelectedFilter?.Match.Text == "charlie",
                        tree.SelectedFilter?.Match.Text ?? "(none)");

            // THE ONE THAT MATTERS: a match at the very end has to be somewhere it can be seen, not tucked
            // behind the bar that has just appeared.
            ok &= Check("the list is too short to show every filter at once",
                        tree.TreeHeightForTesting / Math.Max(1, tree.RowHeightForTesting) < names.Length,
                        $"{tree.TreeHeightForTesting / Math.Max(1, tree.RowHeightForTesting)} rows of {names.Length}");
            tree.TypeSearchForTesting("zulu-last");
            Pump();
            ok &= Check("a match at the end of the list is found", ReferenceEquals(tree.SelectedFilter, last),
                        tree.SelectedFilter?.Match.Text ?? "(none)");
            var row = tree.RowBoundsForTesting(last);
            ok &= Check("and is not left underneath the search bar",
                        row.Height > 0 && row.Top >= 0 && row.Bottom <= tree.TreeAreaForTesting.Height,
                        $"row {row.Top}..{row.Bottom}, the list is {tree.TreeAreaForTesting.Height}px tall");

            // Enter and Shift+Enter walk the matches.
            tree.TypeSearchForTesting("o");
            Pump();
            var firstHit = tree.SelectedFilter;
            tree.PressSearchKeyForTesting(Keys.Enter);
            Pump();
            var secondHit = tree.SelectedFilter;
            ok &= Check("Enter goes on to the next match", !ReferenceEquals(firstHit, secondHit),
                        $"{firstHit?.Match.Text} then {secondHit?.Match.Text}");
            tree.PressSearchKeyForTesting(Keys.Enter | Keys.Shift);
            Pump();
            ok &= Check("and Shift+Enter comes back", ReferenceEquals(tree.SelectedFilter, firstHit),
                        tree.SelectedFilter?.Match.Text ?? "(none)");

            tree.PressSearchKeyForTesting(Keys.Escape);
            Pump();
            ok &= Check("Escape in the box puts the bar away", !tree.SearchOpen);
            ok &= Check("and takes the term with it", tree.SearchTermForTesting.Length == 0,
                        tree.SearchTermForTesting);
            ok &= Check("and gives the list its height back", tree.TreeHeightForTesting == fullTree,
                        $"{tree.TreeHeightForTesting} of {fullTree}");
            ok &= Check("and hands the keyboard back to the list", tree.ListHasFocus);

            // Escape from the list, which is where walking the matches leaves you.
            tree.ShowSearch();
            tree.TypeSearchForTesting("delta");
            Pump();
            tree.FocusList();
            Pump();
            tree.PressKeyForTesting(Keys.Escape);
            Pump();
            ok &= Check("Escape from the list closes it too", !tree.SearchOpen);

            tree.ShowSearch();
            Pump();
            tree.ClickSearchCloseForTesting();
            Pump();
            ok &= Check("and so does the close button", !tree.SearchOpen);

            return ok;
        }
        finally
        {
            host?.Close();
            host?.Dispose();
            doc.Dispose();
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static int Luma(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000;

    /// <summary>Where Tab goes. Two areas, and a bar you have opened keeps it: tabbing out of a bar lands
    /// on whatever happens to be next in the window - the presets list, as it turned out - with no way
    /// back in, so the only way out of a bar is Escape.
    ///
    /// The "never leaves" checks here run through a seam that calls ProcessCmdKey with an empty message,
    /// so they cannot tell a real escape from a no-op. UiFeatureTests.Tab_has_two_stops_and_never_walks_
    /// out_of_an_open_bar posts real Tab keys and is what actually holds that line.</summary>
    private static bool RunTabStopChecks()
    {
        Line("-- what Tab does --");

        using var form = new MainForm(new AppSettings(), new MachineState(), Array.Empty<string>())
        {
            NoSavePrompt = true,
            Opacity = 0,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Size = new Size(1000, 720),
        };
        form.Show();
        Pump();

        string Area() => form.FocusedAreaForTesting;

        form.GridForTesting.Focus();
        Pump();
        bool ok = Check("it starts on the log", Area() == "log", Area());

        var walk = new List<string>();
        for (int i = 0; i < 4; i++) { form.PressCmdKeyForTesting(Keys.Tab); Pump(); walk.Add(Area()); }
        string[] alternating = ["filter list", "log", "filter list", "log"];
        ok &= Check("Tab alternates between the log and the filter list",
                    walk.SequenceEqual(alternating), string.Join(" -> ", walk));

        var backwards = new List<string>();
        for (int i = 0; i < 2; i++) { form.PressCmdKeyForTesting(Keys.Shift | Keys.Tab); Pump(); backwards.Add(Area()); }
        string[] bothWays = ["filter list", "log"];
        ok &= Check("and Shift+Tab does the same, there being only the two",
                    backwards.SequenceEqual(bothWays), string.Join(" -> ", backwards));

        // The find bar has five stops of its own; Tab must visit them and come back, never leave.
        form.ClickMenuForTesting("Edit", "Find");
        Pump();
        ok &= Check("opening find puts the keyboard in the bar", Area() == "find bar", Area());

        var inBar = new List<string>();
        for (int i = 0; i < 8; i++) { form.PressCmdKeyForTesting(Keys.Tab); Pump(); inBar.Add(Area()); }
        ok &= Check("Tab never leaves the find bar", inBar.TrueForAll(a => a == "find bar"),
                    string.Join(" -> ", inBar.Distinct()));
        ok &= Check("and it really does move about inside it",
                    form.FindBarForTesting.FocusedForTesting is { Length: > 0 },
                    form.FindBarForTesting.FocusedForTesting ?? "(none)");
        form.CloseFindForTesting();
        Pump();

        // Same rule for the filter search bar, which has one stop - so Tab stays put rather than falling out.
        form.FilterTreeForTesting.ShowSearch();
        Pump();
        ok &= Check("opening the filter search puts the keyboard in its bar", Area() == "filter search", Area());
        var inSearch = new List<string>();
        for (int i = 0; i < 4; i++) { form.PressCmdKeyForTesting(Keys.Tab); Pump(); inSearch.Add(Area()); }
        ok &= Check("Tab never leaves the filter search bar either",
                    inSearch.TrueForAll(a => a == "filter search"), string.Join(" -> ", inSearch.Distinct()));

        // ...and Escape, which is the way out, hands it back to the list rather than to nowhere.
        form.PressCmdKeyForTesting(Keys.Escape);
        Pump();
        ok &= Check("Escape leaves the bar and lands on the filter list", Area() == "filter list", Area());

        form.Close();
        Pump();
        return ok;
    }


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

        bool ok = Walk("menu", bar.Items);

        // A command with a key must say so where it is offered. These two run the same thing from different
        // menus, and only one of them registers the key, so only a check on the DISPLAYED string covers both.
        string[] shortcuts =
        [
            "Find Filter\tCtrl+E", "Split Lines Into Fields\tCtrl+Shift+C", "Field Settings\u2026\tCtrl+Shift+D"
        ];
        var keys = System.ComponentModel.TypeDescriptor.GetConverter(typeof(Keys));
        var advertised = AllMenuItems(bar.Items)
            .Select(m => (m.Text ?? "").Replace("&", "") + "\t" +
                         (m.ShortcutKeyDisplayString ??
                          (m.ShortcutKeys == Keys.None ? "" : keys.ConvertToString(m.ShortcutKeys))))
            .ToHashSet(StringComparer.Ordinal);
        foreach (string want in shortcuts)
            ok &= Check($"the menu offers \"{want.Replace('\t', ' ')}\"", advertised.Contains(want),
                        string.Join(" | ", advertised.Where(a => a.StartsWith(want.Split('\t')[0], StringComparison.Ordinal))));

        return ok;
    }

    private static IEnumerable<ToolStripMenuItem> AllMenuItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
            if (item is ToolStripMenuItem m)
            {
                yield return m;
                foreach (var d in AllMenuItems(m.DropDownItems)) yield return d;
            }
    }

    private static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in AllControls(c)) yield return d;
        }
    }

    /// <summary>Whether a vertical rule really was painted at <paramref name="x"/> - the position alone
    /// only says where it was meant to go. Reads a row through the middle of the bar and requires the
    /// column to be darker than the bar around it.</summary>
    private static bool RuleIsDrawnAt(Control bar, int x)
    {
        if (x <= 1 || x >= bar.Width - 1) return false;
        using var bmp = new Bitmap(bar.Width, bar.Height);
        bar.DrawToBitmap(bmp, new Rectangle(0, 0, bar.Width, bar.Height));
        int y = bar.Height / 2;
        static int Grey(Color c) => (c.R + c.G + c.B) / 3;
        int here = Grey(bmp.GetPixel(x, y));
        int left = Grey(bmp.GetPixel(x - 2, y)), right = Grey(bmp.GetPixel(x + 2, y));
        return here < left - 10 && here < right - 10;
    }

    /// <summary>Where each caption's ink really sits, read off a render of the row. A control's box says
    /// nothing about where it draws its text - a combo box's edit puts it where the native control wants it,
    /// a button centres its caption - so the pixels are the only honest measure of "these line up".
    /// The area scanned skips each control's border and the check box's glyph, leaving only the caption.</summary>
    private static List<(string What, int Top, int Bottom, string Font)> TextInk(Control bar)
    {
        using var bmp = new Bitmap(bar.Width, bar.Height);
        bar.DrawToBitmap(bmp, new Rectangle(0, 0, bar.Width, bar.Height));

        var found = new List<(string, int, int, string)>();
        foreach (var c in AllControls(bar))
        {
            if (c is not (ComboBox or Button or CheckBox or Label)) continue;
            int Dp(int v) => v * c.DeviceDpi / 96;
            Rectangle r = bar.RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));
            Rectangle area = c switch
            {
                ComboBox => new Rectangle(r.Left + Dp(4), r.Top + Dp(3), r.Width - Dp(26), r.Height - Dp(6)),
                CheckBox => new Rectangle(r.Left + Dp(17), r.Top, r.Width - Dp(17), r.Height),
                Button { FlatStyle: FlatStyle.Standard } => Rectangle.Inflate(r, -Dp(5), -Dp(5)),
                _ => r
            };
            area.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
            if (area.Width <= 0 || area.Height <= 0) continue;

            int top = -1, bottom = -1;
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    if ((p.R + p.G + p.B) / 3 >= 170) continue;   // anything darker than the backgrounds
                    if (top < 0) top = y;
                    bottom = y;
                    break;
                }
            if (top < 0) continue;

            string what = c is Label l && l.Text.Length > 0 ? l.Text
                        : c.AccessibleName ?? c.GetType().Name;
            found.Add((what, top, bottom, $"{c.Font.Name} {c.Font.SizeInPoints:0.##}pt"));
        }
        return found;
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
    /// self-test was spent sitting idle. The old wait is kept as the cap.
    /// <para>Quiet is not the same as finished: the last frame of a drag is drawn by a timer, and a timer
    /// arrives when Windows feels like it - up to two of its ticks later. So the view is asked whether it
    /// still owes a frame, rather than the wait being lengthened to cover the worst case, which would be
    /// paid by every one of the thousand calls this makes.</para></summary>
    private static void Pump()
    {
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 250;)
        {
            Application.DoEvents();
            if (PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE)) { Thread.Sleep(1); continue; }
            if (LineGridControl.AnyViewOwesAFrameForTesting) { Thread.Sleep(1); continue; }
            // Quiet once is not the same as settled - a timer may be about to post. Ask again after a pause.
            Thread.Sleep(15);
            Application.DoEvents();
            if (!PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE)) return;
        }
    }

    private const uint PM_NOREMOVE = 0;
    private const uint WM_KEYDOWN = 0x0100;

    /// <summary>How many GDI handles this process is holding. Drawing straight onto a device context means
    /// owning faces and brushes, which nothing collects.</summary>
    private static int GdiHandles()
        => (int)GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0);


    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

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

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>Posted, not sent: a sent message goes straight to WndProc and skips the key pre-processing
    /// where a control is asked whether it wants the key and the dialog gets it if not.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

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

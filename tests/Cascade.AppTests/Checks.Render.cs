using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Text;
using Cascade.App;
using Cascade.Core.Columns;
using Cascade.Core.Document;
using Cascade.Core.Find;
using Cascade.Core.Model;
using Cascade.Core.Persistence;

namespace Cascade.AppTests;

/// <summary>Part of <see cref="Checks"/>: what the log view paints and where: the margins, the sideways scroll, wrapping, the width it reports and the progress bar.</summary>
internal static partial class Checks
{

    /// <summary>
    /// Scrolling right must never paint line text over the marker or line-number margin.
    ///
    /// The margin does not scroll, so every pixel of it has to be identical whatever the horizontal offset
    /// is - which makes the check exact rather than a judgement about what looks wrong. It bites because
    /// TextRenderer draws through GDI and silently ignores the GDI+ clip region unless asked not to, so the
    /// SetClip guarding the text looked sufficient and was not.
    /// </summary>
    internal static bool RunRenderChecks()
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

            // Marking a term is bounded to the stretch of a line that can be drawn. A line is not bounded
            // by the window it is read in - a term matching every few characters of a very long one used to
            // cost a mark per character, per row, per repaint - so what has to be shown is that stopping
            // early stops nothing appearing.
            int wholeMarks = 0, windowedMarks = 0;
            foreach (int scrolledTo in (int[])[0, 300, 900])
            {
                grid.ScrollHorizontallyTo(scrolledTo);
                grid.RefreshView();
                grid.SetFindHighlight(FindEngine.CompileQuery(new FindQuery("e", Regex: false, CaseSensitive: false)));
                grid.MarkWholeLinesForTesting = true;
                Pump();
                using var whole = Capture(host);
                wholeMarks += grid.MarksMadeForTesting;
                grid.MarkWholeLinesForTesting = false;
                Pump();
                using var windowed = Capture(host);
                windowedMarks += grid.MarksMadeForTesting;
                var markDiff = FirstDifference(whole, windowed, new Rectangle(0, 0, whole.Width, whole.Height));
                ok &= Check("marking only the part of a line that shows marks what marking all of it does, " +
                            $"scrolled to {scrolledTo}" +
                            (markDiff is null ? "" : $" [first differs at x={markDiff.Value.X},y={markDiff.Value.Y}]"),
                            markDiff is null);

                // ...and the pictures agreeing says nothing unless marks are being drawn at all.
                grid.SetFindHighlight(null);
                Pump();
                using var unmarked = Capture(host);
                bool anyInk = FirstDifference(windowed, unmarked, new Rectangle(0, 0, whole.Width, whole.Height)) is not null;
                ok &= Check($"and there really are marks on screen to compare, scrolled to {scrolledTo}",
                            anyInk || scrolledTo == 0);   // at the far left this fixture has no "e" in view
            }
            ok &= Check($"and far fewer of them are worked out ({wholeMarks} -> {windowedMarks})",
                        windowedMarks > 0 && windowedMarks * 2 < wholeMarks);
            grid.SetFindHighlight(null);
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

            // Marks that touch are drawn as one, which is a great deal cheaper when a term matches every few
            // characters. Asked in a PROPORTIONAL face, because that is where drawing a run in one piece
            // could place its glyphs differently from drawing it in several.
            grid.SetFindHighlight(FindEngine.CompileQuery(new FindQuery("e", Regex: false, CaseSensitive: false)));
            grid.MergeMarksForTesting = false;
            Pump();
            using (var apiece = Capture(host))
            {
                grid.MergeMarksForTesting = true;
                Pump();
                using var joined = Capture(host);
                var joinDiff = FirstDifference(apiece, joined, new Rectangle(0, 0, apiece.Width, apiece.Height));
                ok &= Check("joining marks that touch draws what marking each on its own does" +
                            (joinDiff is null ? "" : $" [first differs at x={joinDiff.Value.X},y={joinDiff.Value.Y}]"),
                            joinDiff is null);
            }
            grid.SetFindHighlight(null);
            Pump();
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
    /// The sideways offset and the picture on screen have to agree once everything has settled.
    ///
    /// <para>The scrollbar's range is built from the rows on screen, because measuring every line of a
    /// 14GB file is not on offer - so scrolling away from a very long line leaves the view further right
    /// than anything now on screen goes, and the offset is pulled back to fit. That happens AFTER the paint
    /// that measured the new rows, so unless the pull-back redraws, the window keeps the frame it drew at
    /// the old offset: a blank screen. And Home then did nothing at all, because the offset it sets was
    /// already the one stored - it was only the pixels that were somewhere else.</para>
    /// </summary>
    internal static bool RunHorizontalScrollChecks()
    {
        Line("-- scrolling sideways --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_hscroll_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        sb.Append("[00:00:00] ").Append(string.Join(' ', Enumerable.Repeat("an-enormously-long-line", 400))).Append('\n');
        for (int i = 1; i < 200; i++) sb.Append($"[00:00:{i % 60:00}] short line {i}\n");
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
                ClientSize = new Size(520, 300),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(grid);
            grid.Attach(doc, settings);
            host.Show();
            Pump();

            grid.PressKeyForTesting(Keys.End);
            Pump();
            bool ok = Check("End takes the view out to the end of the long line",
                            grid.HScrollForTesting > 0, $"offset {grid.HScrollForTesting}");
            int far = grid.HScrollForTesting;
            ok &= Check("and that is what is drawn", grid.PaintedHScrollForTesting == far,
                        $"drawn from {grid.PaintedHScrollForTesting}, view at {far}");

            // Away to a stretch of the file where nothing is anywhere near that wide - scrolled the way a
            // wheel scrolls, which is the whole point: the offset is pulled back by what the PAINT finds,
            // long after whatever moved the view has finished.
            grid.ScrollToRow(60);
            Pump();
            ok &= Check("scrolling away from it brings the view back to where the text now is",
                        grid.HScrollForTesting < far, $"offset {grid.HScrollForTesting}, was {far}");
            ok &= Check("and the picture on screen is drawn from where the view now is, not where it was",
                        grid.PaintedHScrollForTesting == grid.HScrollForTesting,
                        $"drawn from {grid.PaintedHScrollForTesting}, view at {grid.HScrollForTesting}");

            // ...which is the left edge here, since no line on screen overflows the window at all.
            ok &= Check("with short lines there is nothing to scroll, so it is the left edge",
                        grid.HScrollForTesting == 0 && grid.HScrollBarForTesting.MaxValue == 0,
                        $"offset {grid.HScrollForTesting}, max {grid.HScrollBarForTesting.MaxValue}");

            // And the text is really there to be read, rather than the row being empty for another reason.
            using (var picture = Capture(host))
            {
                var strip = new Rectangle(grid.GutterWidthForTesting, grid.RowTopForTesting(60),
                                          host.ClientSize.Width - grid.GutterWidthForTesting - 20,
                                          grid.RowHeightForTesting);
                ok &= Check("and there is text in the window", HasInk(picture, strip, settings.Background),
                            $"nothing but background in {strip}");
            }

            // Home from here is a no-op, and has to leave the view exactly where it is rather than being
            // the only thing that ever puts the picture right.
            grid.PressKeyForTesting(Keys.Home);
            Pump();
            ok &= Check("Home leaves it at the left edge", grid.HScrollForTesting == 0 &&
                        grid.PaintedHScrollForTesting == 0, $"offset {grid.HScrollForTesting}");

            // Back to the long line: the room to scroll comes back with it.
            grid.ScrollToRow(0);
            Pump();
            grid.PressKeyForTesting(Keys.End);
            Pump();
            ok &= Check("and coming back to the long line gives it somewhere to go again",
                        grid.HScrollForTesting > 0 && grid.PaintedHScrollForTesting == grid.HScrollForTesting,
                        $"offset {grid.HScrollForTesting}, drawn from {grid.PaintedHScrollForTesting}");
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
    /// How wide a line is drawn decides how far right the view can be scrolled, and measuring it for every
    /// row of every frame cost a sixth of a repaint. It is now worked out arithmetically whenever it can be
    /// - a fixed-pitch face, plain ASCII - so what has to hold is that the shortcut and the measurement
    /// never disagree, in either face, on either kind of text.
    /// </summary>
    internal static bool RunTextWidthChecks()
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
    internal static bool RunWordWrapChecks()
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

    /// <summary>Windows slides a progress bar's fill towards a rising value over a few hundred milliseconds,
    /// so a job that finishes quickly is over long before the fill arrives - a bar crawled to a seventh full
    /// while the search itself was four fifths done. What is PAINTED is the only thing that matters here,
    /// and WM_PRINT (what DrawToBitmap uses) reports the slid position, not the value.
    ///
    /// The status bar's is now the only progress bar in the app, the find bar having taken its own to the
    /// status bar's when it stopped being a dialog.</summary>
    internal static bool RunProgressPaintChecks()
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

    /// <summary>A log line is as tall as the typeface says a line is, and no taller unless the reader asks.
    /// Two pixels used to be added to every row unasked, which on Consolas is a line in every eleven off the
    /// screen - the difference that made another viewer look like it fitted more in at the same size.</summary>
    internal static bool RunLineSpacingChecks()
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
}

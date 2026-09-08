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

/// <summary>Part of <see cref="Checks"/>: moving about the log - the caret, the keys that drive it, what is picked out, and what holds still when the view changes underneath it.</summary>
internal static partial class Checks
{

    /// <summary>
    /// The editing keys inside a text box belong to the text box.
    ///
    /// <para>A menu shortcut is dispatched by the form before the focused control is offered the key at
    /// all, so the log's own Ctrl+A and Ctrl+C reached over the find bar and selected and copied the whole
    /// LOG while the caret sat in the term - and the filter list's Ctrl+Z undid a filter edit in the middle
    /// of typing one. Driven through the form's own shortcut handling, which is the path a real keystroke
    /// takes.</para>
    /// </summary>
    internal static bool RunEditingKeyChecks()
    {
        Line("-- the editing keys go to the box being typed in --");

        string log = Path.Combine(Path.GetTempPath(), "cascade_st_keys_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(log, Enumerable.Range(1, 120).Select(i => $"line {i} of the log"));

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
            for (int i = 0; i < 60 && doc.CompletedLineCount < 120; i++) { Thread.Sleep(20); Pump(); }
            bool ok = Check("the file is open", doc.CompletedLineCount >= 120, doc.CompletedLineCount.ToString());
            if (!ok) return false;

            var grid = form.GridForTesting;
            var bar = form.FindBarForTesting;

            // With the log focused, the two keys are the log's own.
            grid.Focus();
            Pump();
            ok &= Check("the log has the focus", form.FocusedAreaForTesting == "log", form.FocusedAreaForTesting);
            form.PressCmdKeyForTesting(Keys.Control | Keys.A);
            Pump();
            ok &= Check("Ctrl+A selects the whole log when the log is where you are",
                        grid.SelectedCount == doc.CompletedLineCount, $"{grid.SelectedCount} lines");

            // ...and inside the find bar they are the box's. The log is left with ONE line picked out, so a
            // Ctrl+A that went to the log instead would be plain to see.
            grid.SelectRowForAccessibility(3);
            form.ClickMenuForTesting("Edit", "Find");
            Pump();
            bar.SetTermForTesting("timeout", 7, 0);
            bar.FocusInput();
            bar.SetTermForTesting("timeout", 7, 0);
            Pump();
            ok &= Check("the find bar has the focus", form.FocusedAreaForTesting == "find bar",
                        form.FocusedAreaForTesting);

            long selectedBefore = grid.SelectedCount;
            ok &= Check("with one line of the log picked out", selectedBefore == 1, $"{selectedBefore} lines");
            form.PressCmdKeyForTesting(Keys.Control | Keys.A);
            Pump();
            ok &= Check("Ctrl+A in the find bar picks out the term, not the log",
                        bar.SelectionForTesting() == (0, "timeout".Length),
                        $"selection {bar.SelectionForTesting()}");
            ok &= Check("and leaves the log's own selection alone", grid.SelectedCount == selectedBefore,
                        $"{grid.SelectedCount} lines, was {selectedBefore}");

            // Copy takes what is picked out in the box. The clipboard is a shared machine resource, so what
            // was on it goes back afterwards - and a busy clipboard is not a failure of this check.
            string? restore = null;
            try { restore = Clipboard.ContainsText() ? Clipboard.GetText() : null; } catch { /* busy */ }
            try
            {
                Clipboard.SetText("something else entirely");
                form.PressCmdKeyForTesting(Keys.Control | Keys.C);
                Pump();
                ok &= Check("Ctrl+C in the find bar copies the term, not the log",
                            Clipboard.GetText() == "timeout", Clipboard.GetText());
            }
            catch (System.Runtime.InteropServices.ExternalException) { Line("   (clipboard busy; copy not checked)"); }
            finally
            {
                try { if (restore is { Length: > 0 }) Clipboard.SetText(restore); else Clipboard.Clear(); }
                catch { /* busy */ }
            }

            // Undo is the filter list's everywhere except in a box, where it has to mean "undo my typing".
            // Checked one key at a time, and from both sides, or an undo that ran when it should not have
            // leaves the redo it should not have run either with nothing to do - and both look like passes.
            form.EditFilterForTesting(() => doc.Filters.Add(new Filter { Enabled = true, Match = { Text = "line 4" } }));
            Pump();
            int before = doc.Filters.Roots.Count;
            ok &= Check("there is a filter edit waiting to be undone", before > 0, $"{before} filters");
            form.PressCmdKeyForTesting(Keys.Control | Keys.Z);
            Pump();
            ok &= Check("Ctrl+Z while typing a term does not undo a filter edit",
                        doc.Filters.Roots.Count == before, $"{doc.Filters.Roots.Count} filters, was {before}");

            // Out of the box it is the filter list's again...
            form.CloseFindForTesting();
            grid.Focus();
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.Z);
            for (int i = 0; i < 100 && doc.IsBusy; i++) { Thread.Sleep(20); Pump(); }
            Pump();
            ok &= Check("and out of the box Ctrl+Z undoes the filter edit after all",
                        doc.Filters.Roots.Count == before - 1, $"{doc.Filters.Roots.Count} filters, was {before}");

            // ...and the same both ways round for redo, which now has something waiting for it.
            form.ClickMenuForTesting("Edit", "Find");
            bar.FocusInput();
            Pump();
            ok &= Check("back in the find bar", form.FocusedAreaForTesting == "find bar", form.FocusedAreaForTesting);
            form.PressCmdKeyForTesting(Keys.Control | Keys.Y);
            Pump();
            ok &= Check("Ctrl+Y while typing a term does not redo a filter edit either",
                        doc.Filters.Roots.Count == before - 1, $"{doc.Filters.Roots.Count} filters");

            form.CloseFindForTesting();
            grid.Focus();
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.Y);
            for (int i = 0; i < 100 && doc.IsBusy; i++) { Thread.Sleep(20); Pump(); }
            Pump();
            ok &= Check("and out of the box it redoes it", doc.Filters.Roots.Count == before,
                        $"{doc.Filters.Roots.Count} filters, was {before}");
            return ok;
        }
        finally
        {
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            Pump();
            try { File.Delete(log); } catch { /* ignore */ }
        }
    }

    /// <summary>Selecting part of a line. There is no caret and none is drawn, so every rule here is about
    /// what the mouse does: a click takes the whole line, a drag within one line takes a range, a drag off
    /// it goes back to whole lines, and moving away drops the range.</summary>
    internal static bool RunTextSelectionChecks()
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
    internal static bool RunSelectionFollowsTextChecks()
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

    /// <summary>Picking text out of a line works the same split into cells as whole: a click still takes the
    /// row, a drag takes what it covered, the same text is marked wherever else it shows, and a double-click
    /// carries the part picked out into a new filter. What it must not do is run out of the cell it began
    /// in - the text between two cells is not on screen, so a selection across them could not be honest.</summary>
    internal static bool RunColumnSelectionChecks()
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

    /// <summary>A filter started from a log line has to arrive holding that line. It used to keep only the
    /// first 200 characters, and the lines worth filtering on are exactly the long ones.</summary>
    internal static bool RunCopyBudgetChecks()
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

    /// <summary>The selection belongs to the reader, and only the reader changes it. Every change to the
    /// VISIBLE set - a filter switched on, Ctrl+H, a crop applied or lifted - re-maps which rows exist, and
    /// each one is a chance to quietly redefine what is chosen. What may change is where the choice is DRAWN:
    /// with every chosen line hidden the view stands a neighbour in for it, so the reader keeps their place.
    /// Put the lines back and the original must return, untouched.</summary>
    internal static bool RunSelectionStabilityChecks()
    {
        Line("-- the selection survives the view changing under it --");
        const int lines = 4_000;
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_selstable_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        // Only every fifth line matches, so a line picked at random is very likely to be one a filter hides.
        for (int i = 0; i < lines; i++)
            sb.Append(i % 5 == 0 ? "KEEP" : "drop").Append(" line ").Append(i).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        MainForm? form = null;
        try
        {
            var filters = new FilterCollection();
            var keep = new Filter { Enabled = true, Match = { Text = "KEEP" } };
            filters.Add(keep);
            string filterFile = Path.ChangeExtension(path, ".cascade");
            CascadeFile.Save(filterFile, filters);

            form = new MainForm(new AppSettings(), new MachineState(), [path, "/Filters:" + filterFile])
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(900, 600),
                Opacity = 0,
                NoSavePrompt = true,
            };
            form.Show();
            Pump();
            var doc = form.DocForTesting;
            var grid = form.GridForTesting;
            for (int i = 0; i < 400 && doc.CompletedLineCount < lines; i++) { Thread.Sleep(5); Pump(); }
            for (int i = 0; i < 400 && doc.IsBusy; i++) { Thread.Sleep(5); Pump(); }

            bool ok = Check("every line is on show to begin with", doc.RowCount == lines && !doc.FilteredMode);

            // 1,001 does not match, so switching to matches-only must hide it.
            grid.SelectLinesForTesting(1_001, 1_001);
            Pump();
            ok &= Check($"a line the filter does not match is chosen ({grid.CaretLine:N0})",
                        grid.CaretLine == 1_001 && grid.SelectionRangesForTesting is [(1_001, 1_001)]);

            form.PressCmdKeyForTesting(Keys.Control | Keys.H);
            Pump();
            ok &= Check("hiding the rest does not change what is chosen",
                        grid.SelectionRangesForTesting is [(1_001, 1_001)]);
            ok &= Check($"nor which line the caret belongs to ({grid.CaretTrueLineForTesting:N0})",
                        grid.CaretTrueLineForTesting == 1_001);
            ok &= Check($"a neighbour stands in for it on screen ({grid.StandInLineForTesting:N0})",
                        grid.StandInLineForTesting >= 0 && grid.StandInLineForTesting != 1_001
                        && doc.IsLineVisible(grid.StandInLineForTesting));
            ok &= Check("and the hidden line itself is not drawn as chosen",
                        !doc.IsLineVisible(1_001));

            form.PressCmdKeyForTesting(Keys.Control | Keys.H);
            Pump();
            ok &= Check("showing them again restores the selection",
                        grid.SelectionRangesForTesting is [(1_001, 1_001)]);
            ok &= Check($"and the caret with it ({grid.CaretLine:N0})", grid.CaretLine == 1_001);
            ok &= Check("with nothing standing in for anything", grid.StandInLineForTesting < 0);

            // The same, over a crop rather than a filter.
            grid.SelectLinesForTesting(2_000, 2_050);
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemOpenBrackets);
            Pump();
            ok &= Check($"cropping to the selection takes it, leaving nothing chosen ({grid.SelectedCount:N0} lines)",
                        grid.SelectionRangesForTesting.Length == 0);

            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();
            ok &= Check("lifting the crop hands it back exactly",
                        grid.SelectionRangesForTesting is [(2_000, 2_050)] && grid.CaretLine == 2_000);
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();
            ok &= Check("and putting the crop back takes it away again, rather than moving it",
                        grid.SelectionRangesForTesting.Length == 0);
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();

            // A crop that does not contain the selection at all: still only what is DRAWN may change.
            grid.SelectLinesForTesting(3_500, 3_502);
            Pump();
            grid.SelectLinesForTesting(100, 120);
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemOpenBrackets);
            Pump();
            grid.SelectLinesForTesting(105, 105);
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();
            ok &= Check("a selection made inside a crop outlives the crop",
                        grid.SelectionRangesForTesting is [(105, 105)] && grid.CaretLine == 105);

            // Switching a filter off and on again is a visible-set change like any other.
            grid.SelectLinesForTesting(1_501, 1_501);   // does not match KEEP
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.H);
            Pump();
            form.FilterTreeForTesting.ToggleCheckboxForTesting(keep, false);
            for (int i = 0; i < 400 && doc.IsBusy; i++) { Thread.Sleep(5); Pump(); }
            Pump();
            form.FilterTreeForTesting.ToggleCheckboxForTesting(keep, true);
            for (int i = 0; i < 400 && doc.IsBusy; i++) { Thread.Sleep(5); Pump(); }
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.H);
            Pump();
            ok &= Check("a filter switched off and on again leaves the selection alone",
                        grid.SelectionRangesForTesting is [(1_501, 1_501)] && grid.CaretLine == 1_501);

            // A crop cutting the selection away entirely: the two ways of hiding a line, one on top of the
            // other, and still only what is DRAWN may change.
            grid.SelectLinesForTesting(200, 210);
            Pump();
            doc.SetCrop(3_000, 3_100);
            form.GridForTesting.RefreshView();
            Pump();
            ok &= Check("a crop that excludes the selection does not change it",
                        grid.SelectionRangesForTesting is [(200, 210)]);
            ok &= Check($"and stands a line of the crop in for it ({grid.StandInLineForTesting:N0})",
                        grid.StandInLineForTesting >= 3_000 && grid.StandInLineForTesting < 3_100);
            doc.ClearCrop();
            form.GridForTesting.RefreshView();
            Pump();
            ok &= Check("lifting it brings the selection back",
                        grid.SelectionRangesForTesting is [(200, 210)] && grid.StandInLineForTesting < 0);

            // Ctrl+H inside a crop: two visible-set changes stacked, and the selection outlives both.
            grid.SelectLinesForTesting(1_002, 1_002);   // does not match KEEP
            Pump();
            doc.SetCrop(1_000, 1_100);
            form.GridForTesting.RefreshView();
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.H);
            Pump();
            ok &= Check("hiding non-matching lines inside a crop leaves the selection alone",
                        grid.SelectionRangesForTesting is [(1_002, 1_002)]);
            form.PressCmdKeyForTesting(Keys.Control | Keys.H);
            Pump();
            doc.ClearCrop();
            form.GridForTesting.RefreshView();
            Pump();
            ok &= Check("and unwinding both puts the caret back on it",
                        grid.SelectionRangesForTesting is [(1_002, 1_002)] && grid.CaretLine == 1_002);
            return ok;
        }
        finally
        {
            if (form is not null) { form.Close(); form.Dispose(); }
            Pump();
            try { File.Delete(path); } catch { /* ignore */ }
            try { File.Delete(Path.ChangeExtension(path, ".cascade")); } catch { /* ignore */ }
        }
    }

    /// <summary>Toggling what is shown and toggling it back must land the reader exactly where they were.
    /// The selection surviving is not enough on its own: a selection restored off-screen is a selection the
    /// reader has to go looking for.</summary>
    internal static bool RunViewportStabilityChecks()
    {
        Line("-- the viewport comes back to where it was --");
        const int lines = 4_000;
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_viewport_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
            sb.Append(i % 5 == 0 ? "KEEP" : "drop").Append(" line ").Append(i).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        MainForm? form = null;
        try
        {
            var filters = new FilterCollection();
            filters.Add(new Filter { Enabled = true, Match = { Text = "KEEP" } });
            string filterFile = Path.ChangeExtension(path, ".cascade");
            CascadeFile.Save(filterFile, filters);

            form = new MainForm(new AppSettings(), new MachineState(), [path, "/Filters:" + filterFile])
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(900, 600),
                Opacity = 0,
                NoSavePrompt = true,
            };
            form.Show();
            Pump();
            var doc = form.DocForTesting;
            var grid = form.GridForTesting;
            for (int i = 0; i < 400 && doc.CompletedLineCount < lines; i++) { Thread.Sleep(5); Pump(); }
            for (int i = 0; i < 400 && doc.IsBusy; i++) { Thread.Sleep(5); Pump(); }

            long TopLine() => doc.RowToLine(grid.FirstVisibleRow);
            void ShowOnlyMatches(bool on)
            {
                if (doc.FilteredMode == on) return;
                form.PressCmdKeyForTesting(Keys.Control | Keys.H);
                Pump();
            }

            // Parked well down the file, with the caret a few rows below the top so it is plainly on screen.
            grid.ScrollToRow(1_000);
            Pump();
            grid.SelectLinesForTesting(1_003, 1_003);
            Pump();
            grid.ScrollToRow(1_000);
            Pump();
            long top = TopLine();
            long caret = grid.CaretLine;
            bool ok = Check($"parked at line {top:N0} with the caret at {caret:N0}", top == 1_000 && caret == 1_003);

            form.PressCmdKeyForTesting(Keys.Control | Keys.H);
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.H);
            Pump();
            ok &= Check($"Ctrl+H and back leaves the top line where it was (line {TopLine():N0})", TopLine() == top);
            ok &= Check($"and the caret is still on screen (row {grid.CaretRowForTesting:N0} of the window at {grid.FirstVisibleRow:N0})",
                        grid.CaretRowForTesting >= grid.FirstVisibleRow
                        && grid.CaretRowForTesting < grid.FirstVisibleRow + grid.VisibleRows);

            // Four round trips, not one: an anchor that drifts by a line a time reads as steady once and
            // walks off the screen by the fifth.
            for (int i = 0; i < 4; i++)
            {
                form.PressCmdKeyForTesting(Keys.Control | Keys.H);
                Pump();
                form.PressCmdKeyForTesting(Keys.Control | Keys.H);
                Pump();
            }
            ok &= Check($"and it does not creep over repeated toggles (line {TopLine():N0})", TopLine() == top);
            ok &= Check($"with the caret still at the same height ({grid.CaretRowForTesting - grid.FirstVisibleRow} rows down, was 3)",
                        grid.CaretRowForTesting - grid.FirstVisibleRow == 3);

            // The harder case: the top line is itself one the filter hides, and the caret is nowhere near it.
            // There is then no caret to hold the view by, and the top line has to give way to its neighbour -
            // so this is where a re-derived anchor creeps and a remembered one does not.
            grid.SelectLinesForTesting(5, 5);
            Pump();
            grid.ScrollToRow(1_002);              // 1,002 does not match KEEP, and the caret is far above it
            Pump();
            long hiddenTopLine = TopLine();
            ok &= Check($"parked on a line the filter hides ({hiddenTopLine:N0})", hiddenTopLine == 1_002);
            for (int i = 0; i < 3; i++)
            {
                ShowOnlyMatches(true);
                ShowOnlyMatches(false);
            }
            ok &= Check($"toggling returns to that very line, not its neighbour (line {TopLine():N0})",
                        TopLine() == hiddenTopLine);

            // A crop taken with the caret on screen, which is the ordinary way one is taken.
            grid.ScrollToRow(1_100);
            Pump();
            grid.SelectLinesForTesting(1_100, 1_400);
            Pump();
            grid.ScrollToRow(doc.RowForLine(1_200));
            Pump();
            long cropTop = TopLine();
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemOpenBrackets);
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();
            ok &= Check($"a crop taken and lifted leaves the top line where it was (line {TopLine():N0}, was {cropTop:N0})",
                        TopLine() == cropTop);

            // And one whose caret sits on a line the filters hide, so it is drawn against a stand-in from the
            // moment the crop is taken to the moment it is lifted. Wide enough that the crop still has room
            // to scroll: one shorter than the window has no position left to hold, and lands on its first row
            // because that is the only row it can start at.
            grid.SelectLinesForTesting(2_002, 3_400);            // 2,002 does not match KEEP
            Pump();
            ShowOnlyMatches(true);                               // now the caret's own line is hidden
            grid.ScrollToRow(doc.RowForLine(2_500));
            Pump();
            long hiddenTop = TopLine();
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemOpenBrackets);
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();
            ok &= Check($"a crop round trip holds still with the caret on a hidden line (line {TopLine():N0}, was {hiddenTop:N0})",
                        TopLine() == hiddenTop);

            // Cropping to a selection the filters have hidden every line of takes the line standing in for it,
            // so a crop always has something to show - there is no way to crop the view into blankness.
            ShowOnlyMatches(false);
            grid.SelectLinesForTesting(2_502, 2_502);            // does not match KEEP
            Pump();
            ShowOnlyMatches(true);                               // so now every chosen line is hidden
            ok &= Check($"the chosen line is hidden, and a neighbour stands in ({grid.StandInLineForTesting:N0})",
                        grid.StandInLineForTesting == 2_505);
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemOpenBrackets);
            Pump();
            ok &= Check($"cropping to it crops to the line standing in for it ({doc.Crop})",
                        doc.RowCount == 1 && doc.Crop is { From: 2_505, ToExclusive: 2_506 });
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();
            ShowOnlyMatches(false);
            return ok;
        }
        finally
        {
            if (form is not null) { form.Close(); form.Dispose(); }
            Pump();
            try { File.Delete(path); } catch { /* ignore */ }
            try { File.Delete(Path.ChangeExtension(path, ".cascade")); } catch { /* ignore */ }
        }
    }

    /// <summary>Jumping to a particular line - Go To, find, per-filter find, marker navigation - lands it in
    /// the middle half of the view, so it arrives with context above and below instead of hard against an
    /// edge with nothing to read around it. Stepping about with the arrow keys keeps the old behaviour of
    /// scrolling as little as possible, which is why the two paths are separate.</summary>
    internal static bool RunNavigationChecks()
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
}

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

/// <summary>Part of <see cref="Checks"/>: times read out of the log itself, and the margin and status bar that report them.</summary>
internal static partial class Checks
{

    /// <summary>
    /// The elapsed column and the elapsed slot, driven through a real window.
    ///
    /// <para>Two things are worth holding here that the engine's own tests cannot. One is that the margin
    /// and the status bar always AGREE - they answer the same question about the same line, and two
    /// displays of one number that can disagree are worse than one. The other is that the margin's width is
    /// fixed: it is what the text to its right is placed by, so a column that resized as it scrolled would
    /// slide the whole log sideways.</para>
    /// </summary>
    internal static bool RunElapsedChecks()
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
            // the format can EVER draw rather than from the ones on screen - and from no MORE than that.
            // Counted in characters, exactly as the line numbers beside it are: asked of GDI without
            // NoPadding it quotes for glyph overhang that is never drawn, and the column carried a whole
            // character of margin nobody could put anything in.
            string figure = grid.ElapsedWidestFigureForTesting;
            int room = grid.ElapsedGutterWidthForTesting;
            int wide = figure.Length * Figure(margins, "char=");
            ok &= Check("the margin is sized for the widest figure it could ever draw, and no wider",
                        room == wide + 2 * pad,
                        $"{room}px of room for \u201c{figure}\u201d, which draws {wide}px with {pad}px of air either side");

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
            // its own entry underlines, so there is one letter to learn per entry. And an entry that opens a
            // submenu can advertise NOTHING - the arrow takes the column the shortcut would go in - so a key
            // parked on one is a key nobody can find.
            string advertised = form.ElapsedMenuKeysForTesting();
            Line("   (" + advertised + ")");
            bool matched = advertised.Split('|').Length == 5 && advertised.Split('|').All(entry =>
            {
                string[] parts = entry.Split('=');
                int amp = parts[0].IndexOf('&', StringComparison.Ordinal);
                return parts.Length == 2 && amp >= 0 && amp + 1 < parts[0].Length
                    && !parts[1].Contains("ARROW", StringComparison.Ordinal)
                    && parts[1].StartsWith("Ctrl+", StringComparison.Ordinal);
            });
            ok &= Check("every key the menu offers is on an entry that can show it", matched, advertised);

            // A StatusStrip turns item tooltips OFF by default, so every reason written on the bar - the
            // whole path behind a trimmed one, why a figure is negative, which line it is measured from -
            // was being thrown away silently.
            string tips = form.StatusTipsForTesting;
            ok &= Check("and the status bar will actually show the reasons written on it",
                        tips.StartsWith("shown=True", StringComparison.Ordinal)
                        && tips.Contains("after", StringComparison.Ordinal), tips);

            // On a log with no clock the key must fall through rather than silently flip a setting nobody
            // can see the effect of.
            form.OpenForTesting(untimed);
            for (int i = 0; i < 100 && doc.Clock is not null; i++) { Thread.Sleep(20); Pump(); }
            bool wasOn = form.ShowElapsedGutterForTesting;
            ok &= Check("and neither key claims itself on a log with no clock",
                        !form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.M)
                        && !form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.B)
                        && form.ShowElapsedGutterForTesting == wasOn);

            ok &= CheckWhereTheColumnMeasuresFrom();
            ok &= CheckNamingTheTimeField();
            ok &= CheckTheColumnHoldsWhileTheViewMoves();
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

    /// <summary>A frame resolves all its rows at once and then draws them one by one. Working the previous
    /// line on show out from the VIEW per row means a filter change landing in between is answered by the
    /// NEW view, where the rows being drawn may have no place at all - and every figure on screen goes blank
    /// for a frame. Read off the pixels of a real window, because that is the only place the symptom exists.
    /// MEASURED on an 18 GB log before the fix: 50 of the 53 figures went blank.</summary>
    private static bool CheckTheColumnHoldsWhileTheViewMoves()
    {
        string log = Path.Combine(Path.GetTempPath(), "cascade_gap_" + Guid.NewGuid().ToString("N") + ".log");
        var start = new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc);
        File.WriteAllLines(log, Enumerable.Range(0, 4_000).Select(i =>
            $"[{start.AddSeconds(i):yyyy-MM-ddTHH:mm:ss.fff}][{(i % 3 == 0 ? "payment-svc" : "api-gateway")}] request {i}"));

        MainForm? form = null;
        try
        {
            form = new MainForm(new AppSettings(), new MachineState(), [log])
            {
                NoSavePrompt = true, Opacity = 0, StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0), Size = new Size(1100, 700),
            };
            form.Show();
            Pump();
            var doc = form.DocForTesting;
            for (int i = 0; i < 200 && doc.CompletedLineCount < 4_000; i++) { Thread.Sleep(10); Pump(); }
            var grid = form.GridForTesting;

            var payments = new Filter { Enabled = true, Match = { Text = "payment-svc" } };
            var showing = new FilterCollection { ShowOnlyFilteredLines = true };
            showing.Add(payments);
            doc.SetFilters(showing);
            for (int i = 0; i < 400 && doc.IsBusy; i++) { Thread.Sleep(5); Pump(); }
            grid.GoToLine(1_500);
            Pump();

            using var onShow = new Bitmap(Math.Max(1, grid.Width), Math.Max(1, grid.Height));
            grid.DrawToBitmap(onShow, new Rectangle(0, 0, onShow.Width, onShow.Height));
            string settled = ElapsedFigures(onShow, grid);
            bool ok = Check($"a filtered view carries a figure beside its rows ({settled.Count(c => c == '#')} of them)",
                            settled.Count(c => c == '#') >= 5, settled);

            // The frame the report is about: its rows are resolved, and THEN the filters stop showing them.
            grid.AfterWindowForTesting = () =>
            {
                payments.Match.Text = "nothing in this log matches";
                doc.ApplyFilters();
                for (int i = 0; i < 5_000 && doc.IsBusy; i++) Thread.Sleep(1);
            };
            using var moved = new Bitmap(Math.Max(1, grid.Width), Math.Max(1, grid.Height));
            grid.DrawToBitmap(moved, new Rectangle(0, 0, moved.Width, moved.Height));
            grid.AfterWindowForTesting = null;
            ok &= Check("the view really did empty inside that frame", doc.RowCount == 0);
            string during = ElapsedFigures(moved, grid);
            ok &= Check("and every figure the frame had already worked out is still drawn",
                        during == settled, $"{settled} -> {during}");
            return ok;
        }
        finally
        {
            try { if (form is not null) form.GridForTesting.AfterWindowForTesting = null; } catch { /* ignore */ }
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            Pump();
            try { File.Delete(log); } catch { /* ignore */ }
        }
    }

    /// <summary>The column measures from one of three places, and which one it is has to be sayable from the
    /// margin, from the status bar's wording and from the menu at once - three displays of one fact, which
    /// is exactly where they can drift apart. Driven through the real keys and the real menu.</summary>
    private static bool CheckWhereTheColumnMeasuresFrom()    {
        string dir = Path.Combine(Path.GetTempPath(), "cascade_origin_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string log = Path.Combine(dir, "timed.log");
        var start = new DateTime(2026, 8, 5, 9, 0, 0, DateTimeKind.Utc);
        // A MINUTE a line, not a second: the file's span has to exceed the 9,999 seconds the column is sized
        // for by default, or a column that wrongly sized itself from the origin would come out the same
        // width either way and nothing would notice.
        File.WriteAllLines(log, Enumerable.Range(0, 300).Select(i =>
            $"[{start.AddMinutes(i):yyyy-MM-ddTHH:mm:ss.fff}] request {i}"));

        MainForm? form = null;
        try
        {
            form = new MainForm(new AppSettings(), new MachineState(), [log])
            {
                NoSavePrompt = true, Opacity = 0, StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0), Size = new Size(1100, 700),
            };
            form.Show();
            Pump();
            for (int i = 0; i < 100 && form.DocForTesting.CompletedLineCount < 300; i++) { Thread.Sleep(20); Pump(); }
            var grid = form.GridForTesting;

            bool ok = Check("a log opens measuring from the line above, as it always did",
                            form.ElapsedOriginForTesting.StartsWith("PreviousShown", StringComparison.Ordinal),
                            form.ElapsedOriginForTesting);

            // Nothing to go back to yet, so the key says so rather than moving the caret somewhere arbitrary
            // - and the menu offers the way back only once there is one.
            grid.GoToLine(10);
            Pump();
            bool refused = form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.G);
            Pump();
            ok &= Check("with no reference named, going to it is refused out loud",
                        refused && grid.CaretLine == 10
                        && form.FindMessageForTesting.Contains("No reference", StringComparison.Ordinal),
                        $"caret {grid.CaretLine} / {form.FindMessageForTesting}");
            ok &= Check("and the menu says the way back is not available",
                        form.ElapsedMenuForTesting().Contains("Go to the Reference (unavailable)", StringComparison.Ordinal),
                        form.ElapsedMenuForTesting().Replace("\n", " / ", StringComparison.Ordinal));

            // With no reference named there is nothing to see in that mode, so the key steps over it rather
            // than stopping on an empty column.
            form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.R);
            Pump();
            ok &= Check("the cycling key moves on to the start of the file",
                        form.ElapsedOriginForTesting.StartsWith("FileStart", StringComparison.Ordinal),
                        form.ElapsedOriginForTesting);
            ok &= Check("and the margin then says how far into the file each line is",
                        grid.ElapsedForTesting(40) == "2400.000", grid.ElapsedForTesting(40));
            ok &= Check("and the status bar names which end it is measuring from",
                        form.ElapsedSlotForTesting.StartsWith("\u0394 Start:", StringComparison.Ordinal),
                        form.ElapsedSlotForTesting);

            form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.R);
            Pump();
            ok &= Check("and with nothing to measure from it passes over the reference and comes back round",
                        form.ElapsedOriginForTesting.StartsWith("PreviousShown", StringComparison.Ordinal),
                        form.ElapsedOriginForTesting);

            // Naming a reference has to START measuring from it, or the key looks as though it did nothing.
            // The log is a minute a line, so every figure below is arithmetic on the line number.
            grid.GoToLine(100);
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.R);
            Pump();
            ok &= Check("naming a reference measures from it there and then",
                        form.ElapsedOriginForTesting == "Reference ref=100 said=\u201c\u0394 Ref\u201d",
                        form.ElapsedOriginForTesting);

            // The caret is ON the reference, so the reading is zero - the first thing anybody sees after
            // pressing the key. "Written 0 microseconds after line 101" picks a unit at random for an amount
            // every unit measures the same, and names the same line twice.
            Line("   (" + form.StatusTipsForTesting + ")");
            ok &= Check("and reading zero does not name a unit, or the same line twice",
                        form.ElapsedSlotForTesting == "\u0394 Ref: 0"
                        && form.StatusTipsForTesting.Contains(
                               "Line 101 is the reference everything is measured from.", StringComparison.Ordinal),
                        form.ElapsedSlotForTesting + " / " + form.StatusTipsForTesting);

            ok &= Check("a line after it reads as time since the reference",
                        grid.ElapsedForTesting(140) == "2400.000", grid.ElapsedForTesting(140));
            ok &= Check("and a line BEFORE it reads as a negative, which is what it is",
                        grid.ElapsedForTesting(40) == "-3600.000", grid.ElapsedForTesting(40));

            // ...and there is a way back to it. A reference is set on a line worth returning to and then
            // scrolled away from - that is what it is for - and the only record of where it was used to sit
            // in a tooltip on the entry that throws it away.
            grid.GoToLine(280);
            Pump();
            ok &= Check("the view can be taken a long way from the reference", grid.CaretLine == 280,
                        grid.CaretLine.ToString());
            bool went = form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.G);
            Pump();
            ok &= Check("and one key brings it back to the line everything is measured from",
                        went && grid.CaretLine == 100, grid.CaretLine.ToString());
            ok &= Check("which is on screen, not merely selected out of sight",
                        grid.FirstRowForTesting <= 100 &&
                        100 < grid.FirstRowForTesting + grid.RowsPaintedForTesting,
                        $"row 100, showing {grid.FirstRowForTesting}.." +
                        $"{grid.FirstRowForTesting + grid.RowsPaintedForTesting - 1}");
            ok &= Check("and the menu offers the same, now that there is somewhere to go",
                        !form.ElapsedMenuForTesting().Contains("Go to the Reference (unavailable)", StringComparison.Ordinal),
                        form.ElapsedMenuForTesting().Replace("\n", " / ", StringComparison.Ordinal));

            string offered = form.MeasuredFromMenuForTesting();
            Line("   (" + offered.Replace("\n", " / ", StringComparison.Ordinal) + ")");
            ok &= Check("the menu marks the one in force", offered.Contains("Reference Line *", StringComparison.Ordinal),
                        offered);

            // The width may not move with the origin, or changing it slides the log sideways under the hand
            // that changed it. Sized for the file's whole span, which bounds every origin.
            int wide = grid.ElapsedGutterWidthForTesting;
            form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.R);
            Pump();
            ok &= Check("changing what it measures from does not change how wide it is",
                        grid.ElapsedGutterWidthForTesting == wide,
                        $"{wide} -> {grid.ElapsedGutterWidthForTesting}");

            // Clearing it must not leave the column measuring from something that is gone. Put back FIRST:
            // the width check above cycles away from the reference, and clearing something already stepped
            // off proves nothing at all - which is exactly how this check passed while doing nothing.
            form.PressCmdKeyForTesting(Keys.Control | Keys.R);
            Pump();
            form.ClickMenuForTesting("View", "Elapsed Time", "Clear the Reference");
            Pump();
            ok &= Check("clearing the reference really lets go of it, and falls back",
                        form.ElapsedOriginForTesting == "PreviousShown ref=-1 said=\u201c\u0394 Prev\u201d"
                        && grid.ElapsedForTesting(40) == "60.000",
                        form.ElapsedOriginForTesting + " / " + grid.ElapsedForTesting(40));

            // EVERY press has to change what is on screen. An origin nothing can be measured from resolves
            // back to the previous line, so cycling ONTO it - or leaving the setting parked on it after the
            // reference is cleared - spends a keypress showing what was already there. The state BEFORE the
            // first press counts: the dead press is the first one when the setting was left dangling.
            var walk = new List<string> { form.ElapsedOriginForTesting.Split(' ')[0] };
            for (int i = 0; i < 6; i++)
            {
                form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.R);
                Pump();
                walk.Add(form.ElapsedOriginForTesting.Split(' ')[0]);
            }
            Line("   (" + string.Join(" -> ", walk) + ")");
            ok &= Check("and every press of the cycling key changes what is being measured from",
                        walk.Zip(walk.Skip(1)).All(p => p.First != p.Second), string.Join(" -> ", walk));

            // A line with no time cannot be measured from, so it is refused out loud.
            string gappy = Path.Combine(dir, "gappy.log");            File.WriteAllLines(gappy, Enumerable.Range(0, 300).Select(i => i == 50
                ? "  at Payments.Charge(order) in Payments.cs:line 42"
                : $"[{start.AddSeconds(i):yyyy-MM-ddTHH:mm:ss.fff}] request {i}"));
            form.OpenForTesting(gappy);
            for (int i = 0; i < 100 && form.DocForTesting.CompletedLineCount < 300; i++) { Thread.Sleep(20); Pump(); }
            grid.GoToLine(50);
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.R);
            Pump();
            ok &= Check("a line carrying no time is refused as a reference, and says why",
                        form.ElapsedOriginForTesting.StartsWith("PreviousShown", StringComparison.Ordinal)
                        && form.FindMessageForTesting.Contains("no time", StringComparison.OrdinalIgnoreCase),
                        form.ElapsedOriginForTesting + " / " + form.FindMessageForTesting);

            // Opening a file drops the reference without the SETTING hearing about it, so the first press
            // afterwards was moving off an origin the column had already stopped using - a key that did
            // nothing. Found by driving the real app, not here, which is why it is written down here now.
            grid.GoToLine(100);
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.R);
            Pump();
            form.OpenForTesting(log);
            for (int i = 0; i < 100 && form.DocForTesting.CompletedLineCount < 300; i++) { Thread.Sleep(20); Pump(); }
            string opened = form.ElapsedOriginForTesting;
            form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.R);
            Pump();
            ok &= Check("and the first press after opening another file still moves the column",
                        opened.StartsWith("PreviousShown", StringComparison.Ordinal)
                        && form.ElapsedOriginForTesting.StartsWith("FileStart", StringComparison.Ordinal),
                        opened + " -> " + form.ElapsedOriginForTesting);
            return ok;
        }
        finally
        {
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            Pump();
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
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

        // The drop-down offers the fields BY NAME, and the list behind it is only rebuilt when the NUMBER
        // of fields changes - so a rename left it offering a name nothing was called any more, until the
        // dialog was closed and opened again.
        dlg.SetCellForTesting(1, "name", "Timestamp");
        Pump();
        ok &= Check("renaming a field renames it on the drop-down too, there and then",
                    dlg.TimeFieldForTesting == "Timestamp", dlg.TimeFieldForTesting);
        ok &= Check("and the field it is reading is still the one that was chosen",
                    dlg.Result.TimePart == 1, $"part {dlg.Result.TimePart}");
        dlg.SetCellForTesting(1, "name", "Col 2");
        Pump();

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
}

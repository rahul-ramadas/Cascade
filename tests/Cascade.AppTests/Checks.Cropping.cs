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

/// <summary>Part of <see cref="Checks"/>: cropping the view to a stretch of the file, and what that does to everything keyed to a line.</summary>
internal static partial class Checks
{

    internal static bool RunCropChecks()
    {
        Line("-- cropping to a stretch of the log --");
        const int lines = 3_000;
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_crop_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
            sb.Append(i % 5 == 0 ? "ERROR" : "INFO").Append(" line ").Append(i).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        MainForm? form = null;
        try
        {
            form = new MainForm(new AppSettings { ShowLineNumbers = true, ShowMatchMap = true },
                                new MachineState(), [path])
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

            bool ok = Check($"the whole file is on show to begin with ({doc.RowCount:N0} rows)",
                            doc.RowCount == lines && doc.Crop is null);
            ok &= Check("and nothing in the menu bar says otherwise", !form.CropLabelVisibleForTesting);

            // Opening the View menu with nothing selected must not take Ctrl+[ down with it: a menu item's
            // shortcut is dispatched through the item, and a disabled one swallows it.
            grid.ClearSelection();
            Pump();
            form.OpenViewMenuForTesting();
            Pump();
            grid.SelectLinesForTesting(2_000, 2_099);
            Pump();
            ok &= Check("the key still works after the menu was opened with nothing selected",
                        form.PressCmdKeyForTesting(Keys.Control | Keys.OemOpenBrackets));
            Pump();
            ok &= Check($"and it cropped to what was picked out ({doc.RowCount:N0} rows)", doc.RowCount == 100);
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();

            // Crop to a stretch picked out the way a reader picks one: select it, then press the key.
            grid.SelectLinesForTesting(1_000, 1_299);
            Pump();
            ok &= Check("the key is refused when nothing would be cropped",
                        form.PressCmdKeyForTesting(Keys.Control | Keys.OemOpenBrackets));
            Pump();

            ok &= Check($"only the selected stretch is left ({doc.RowCount:N0} rows)", doc.RowCount == 300);
            ok &= Check("the file reads as being only that long", doc.DisplayLineCount == 300);
            ok &= Check($"row 0 keeps the line's own number ({doc.RowToLine(0):N0})", doc.RowToLine(0) == 1_000);
            ok &= Check($"and the last row too ({doc.RowToLine(299):N0})", doc.RowToLine(299) == 1_299);
            ok &= Check("a line outside it is not on show", !doc.IsLineVisible(999) && !doc.IsLineVisible(1_300));
            ok &= Check($"the menu bar says so ({form.CropLabelTextForTesting})",
                        form.CropLabelVisibleForTesting && form.CropLabelTextForTesting.Contains("1,001")
                        && form.CropLabelTextForTesting.Contains("1,300"));
            ok &= Check($"and the chip is centred in the bar ({form.CropLabelCentreOffsetForTesting:+0;-0;0} px off)",
                        Math.Abs(form.CropLabelCentreOffsetForTesting) <= 2);
            int narrowBar = form.CropLabelCentringForTesting.BarWidth;   // read while the chip is still up

            // And on a maximised window, which is how a log is usually read and a wider bar than the one
            // above. Taken once at that size, as a reader gets there - toggling repeatedly would nudge the
            // chip nearer each time and hide a fault that only shows on the first lay-out.
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();
            form.WindowState = FormWindowState.Maximized;
            Pump();
            Thread.Sleep(300);
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();
            // Wider than, or at least as wide as, the bar just measured. Not wider than some absolute number:
            // a build machine's screen is nothing like a reader's, and a window does not always come up the
            // size it was asked for. The centring itself is the check; this only says which bar it was made in.
            ok &= Check($"and centred on a maximised window too ({form.CropLabelCentringForTesting}, was {narrowBar})",
                        Math.Abs(form.CropLabelCentringForTesting.Offset) <= 2
                        && form.CropLabelCentringForTesting.BarWidth >= narrowBar);
            form.WindowState = FormWindowState.Normal;
            Pump();
            ok &= Check($"Total counts the crop ({form.StatusForTesting})",
                        form.StatusForTesting.Contains("Total: 300"));

            // Going to the ends stops at the crop's, not the file's. Sent to the grid: these are its own keys,
            // not menu shortcuts, so they never pass through the form.
            grid.PressKeyForTesting(Keys.Control | Keys.End);
            Pump();
            ok &= Check($"the end of the file is the end of the crop ({grid.CaretLine:N0})", grid.CaretLine == 1_299);
            grid.PressKeyForTesting(Keys.Control | Keys.Home);
            Pump();
            ok &= Check($"and the start of it is the start of the crop ({grid.CaretLine:N0})", grid.CaretLine == 1_000);

            form.PressCmdKeyForTesting(Keys.Control | Keys.A);
            Pump();
            ok &= Check($"selecting all takes the crop and no more ({grid.SelectedCount:N0} lines)",
                        grid.SelectedCount == 300);

            // Hidden, then brought back, with nothing picked out in between.
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();
            ok &= Check($"hiding the crop shows the whole file again ({doc.RowCount:N0} rows)",
                        doc.Crop is null && doc.RowCount == lines);
            ok &= Check("and the menu bar goes quiet", !form.CropLabelVisibleForTesting);

            grid.GoToLine(5);   // somewhere else entirely, and nothing selected of the old stretch
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();
            ok &= Check($"the same crop comes back without picking the lines out again ({doc.RowCount:N0} rows)",
                        doc.Crop is { From: 1_000, ToExclusive: 1_300 } && doc.RowCount == 300);
            ok &= Check("and it says so again", form.CropLabelVisibleForTesting);

            // A second crop replaces the first rather than nesting inside it.
            grid.SelectLinesForTesting(1_100, 1_149);
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemOpenBrackets);
            Pump();
            ok &= Check($"cropping again replaces the crop ({doc.RowCount:N0} rows)",
                        doc.Crop is { From: 1_100, ToExclusive: 1_150 });
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();
            ok &= Check("and one step goes back to the whole file, not to the crop before it",
                        doc.Crop is null && doc.RowCount == lines);

            // The map and the scrollbar draw markers by ROW. A crop offsets rows from lines, so reading a
            // line as a row put every mark inside the crop off the picture and marks from outside it onto it.
            doc.Markers.Toggle(1_050, 0);      // inside the crop
            doc.Markers.Toggle(50, 1);         // outside it
            grid.SelectLinesForTesting(1_000, 1_299);
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemOpenBrackets);
            Pump();
            ok &= Check($"a mark inside the crop sits on a row of it (row {doc.RowForLine(1_050)})",
                        doc.RowForLine(1_050) == 50);
            ok &= Check("and one outside it is on no row at all", doc.RowForLine(50) < 0);
            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();

            // Every number the find bar shows is counted within the crop, so moving the crop has to re-read
            // them there and then. Cropping moves no caret and edits no filter, and the fault was exactly
            // that: nothing the counts watched had changed, so they stood at the whole file's until Enter
            // happened to move the caret and refresh them for an unrelated reason. The search is left to
            // finish first, which switches off the ordinary "re-read while it is still moving" timer and
            // leaves the crop as the only thing that can put this right.
            grid.GoToLine(1_001);          // line 1,000 - inside the crop, so the caret survives it
            Pump();
            var findBar = form.FindBarForTesting;
            form.PressCmdKeyForTesting(Keys.Control | Keys.F);
            Pump();
            findBar.SetTermForTesting("ERROR", 0, 0);
            findBar.EnterForTesting();
            for (int i = 0; i < 600 && (doc.IsFindRunning || !doc.FindComplete || !doc.IsFilterIdle); i++)
            { Thread.Sleep(5); Pump(); }
            Pump();

            string whole = findBar.MessageForTesting();
            ok &= Check($"a search counts the whole file while the whole file is on show (\"{whole}\")",
                        whole == "Match 202 of 600 lines", $"{whole} at line {grid.CaretLine:N0}");

            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();
            string inCrop = findBar.MessageForTesting();
            ok &= Check($"and cropping re-counts it within the crop at once (\"{inCrop}\")",
                        doc.Crop is { From: 1_000, ToExclusive: 1_300 } && grid.CaretLine == 1_005
                        && inCrop == "Match 2 of 60 lines", $"{inCrop} at line {grid.CaretLine:N0}");
            ok &= Check("saying nothing about the matches outside it, which are not hidden but absent",
                        !inCrop.Contains("hidden", StringComparison.Ordinal), inCrop);

            form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
            Pump();
            string lifted = findBar.MessageForTesting();
            ok &= Check($"and lifting the crop puts the whole file's count back (\"{lifted}\")",
                        lifted == whole, lifted);
            form.PressCmdKeyForTesting(Keys.Escape);
            Pump();
            return ok;
        }
        finally
        {
            if (form is not null) { form.Close(); form.Dispose(); }
            Pump();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    /// <summary>A crop borrows the selection and gives it back. The lines picked out are only the way of
    /// naming the stretch; once the crop exists they say nothing, so it takes them and leaves the view as
    /// bare as a freshly opened file. Lifting the crop hands them back exactly. The arrangement lasts only as
    /// long as the reader leaves it alone: any choice of their own ends it, and from then on the crop does
    /// not touch what is chosen at all.</summary>
    internal static bool RunCropSelectionChecks()
    {
        Line("-- a crop borrows the selection, and gives it back --");
        const int lines = 3_000;
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_cropsel_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++) sb.Append("line ").Append(i).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        MainForm? form = null;
        try
        {
            form = new MainForm(new AppSettings(), new MachineState(), [path])
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

            void Crop() { form.PressCmdKeyForTesting(Keys.Control | Keys.OemOpenBrackets); Pump(); }
            void Toggle() { form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets); Pump(); }

            grid.SelectLinesForTesting(1_000, 1_199);
            Pump();
            bool ok = Check($"a stretch is picked out ({grid.SelectedCount:N0} lines)",
                            grid.SelectionRangesForTesting is [(1_000, 1_199)]);

            Crop();
            ok &= Check($"cropping to it leaves nothing chosen ({grid.SelectedCount:N0} lines)",
                        grid.SelectionRangesForTesting.Length == 0);
            ok &= Check($"and no caret either, as a file just opened has ({grid.CaretLine})", grid.CaretLine < 0);
            ok &= Check($"the crop is the stretch that was chosen ({doc.Crop})",
                        doc.Crop is { From: 1_000, ToExclusive: 1_200 });
            ok &= Check("and Crop to Selection has nothing left to act on", !form.CropToSelectionEnabledForTesting);

            Toggle();
            ok &= Check("hiding the crop hands the selection back exactly",
                        grid.SelectionRangesForTesting is [(1_000, 1_199)] && grid.CaretLine == 1_000);

            Toggle();
            ok &= Check($"putting it back takes them away again ({grid.SelectedCount:N0} lines)",
                        grid.SelectionRangesForTesting.Length == 0);

            // Round and round: the two states have to keep pairing up, not decay after the first exchange.
            for (int i = 0; i < 3; i++) { Toggle(); Toggle(); }
            ok &= Check("and it still pairs up after several rounds",
                        doc.Crop is not null && grid.SelectionRangesForTesting.Length == 0);
            Toggle();
            ok &= Check("with the selection still there to come back to",
                        grid.SelectionRangesForTesting is [(1_000, 1_199)]);
            Toggle();

            // The reader chooses for themselves while cropped: from here the crop keeps its hands off.
            grid.SelectLinesForTesting(1_050, 1_060);
            Pump();
            Toggle();
            ok &= Check("a choice made while cropped survives the crop being lifted",
                        grid.SelectionRangesForTesting is [(1_050, 1_060)]);
            Toggle();
            ok &= Check("and is not taken away when the crop comes back",
                        grid.SelectionRangesForTesting is [(1_050, 1_060)]);
            Toggle();

            // Cropping again, deliberately, always takes the lines it was given.
            grid.SelectLinesForTesting(2_000, 2_100);
            Pump();
            Crop();
            ok &= Check($"cropping again takes the new selection ({doc.Crop})",
                        doc.Crop is { From: 2_000, ToExclusive: 2_101 }
                        && grid.SelectionRangesForTesting.Length == 0);
            Toggle();
            ok &= Check("and hands that one back",
                        grid.SelectionRangesForTesting is [(2_000, 2_100)]);
            return ok;
        }
        finally
        {
            if (form is not null) { form.Close(); form.Dispose(); }
            Pump();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}

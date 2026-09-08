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

/// <summary>Part of <see cref="Checks"/>: splitting a line into columns: the header gestures, the layout arithmetic and the dialog that names the fields.</summary>
internal static partial class Checks
{

    /// <summary>
    /// The column header is where columns are laid out: dragging an edge sizes one, carrying a header
    /// moves one, double-clicking a name renames it and the header's own menu hides and shows them. All of
    /// it is driven here against a real control, because none of it can be driven through UI Automation -
    /// a drag needs a real mouse, and these gestures have no automation pattern to invoke.
    /// </summary>
    internal static bool RunColumnChecks()
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
                //
                // What "inside" means depends on the screen. This dialog is taller than a 768-pixel one can
                // show, and it is MEANT to be: the content is held to its floor and the panel around it
                // scrolls, or the list would be squeezed away and the OK button pushed out of reach. So the
                // claim is "nothing is unreachable", which on a roomy screen means nothing hangs off the
                // edge and on a short one means everything can be scrolled to. Checked BOTH ways here,
                // whatever this machine's screen is, because CI's is 1024x768 and a developer's is not.
                bool RunsInside(string where, Control edge)
                {
                    var over = AllControls(dlg).Where(c => c.Visible && c.Parent is not null)
                        .Select(c => (c, r: Where(c)))
                        .Where(t => t.r.Right > edge.ClientSize.Width + 1 || t.r.Bottom > edge.ClientSize.Height + 1)
                        .ToList();
                    return Check($"nothing on the dialog hangs off {where}", over.Count == 0,
                                 string.Join(", ", over.Select(t => $"{t.c.GetType().Name}'{t.c.Text}' {t.r}")));
                }

                var fits = dlg.ContentScrollForTesting;
                bool roomy = !fits.VerticalScroll.Visible;
                if (roomy)
                {
                    ok &= RunsInside("its edge", dlg);
                    // Opened at its own size it fits, so there must be no scroll bar: one that is always
                    // there takes width off the content and says the dialog is bigger than it is.
                    ok &= Check("and at its own size there is nothing to scroll",
                                !fits.HorizontalScroll.Visible,
                                $"content {fits.DisplayRectangle} in {fits.ClientSize}; {dlg.ContentFloorForTesting}");
                }
                else
                {
                    ok &= Check("on a screen too short for it, the content scrolls rather than being squeezed",
                                fits.DisplayRectangle.Height >= fits.ClientSize.Height,
                                $"content {fits.DisplayRectangle} in {fits.ClientSize}; {dlg.ContentFloorForTesting}");
                }

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
    internal static bool RunFieldSettingsChecks()
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

            // --- a dot stands in for one character, so one template can read punctuation that varies ---

            dlg.SetTemplateForTesting("{[*]}{[*]}{[*]}.{*}");
            Pump();
            ok &= Check($"a template with a dot in it is read as four fields (\"{dlg.StatusForTesting}\")",
                        dlg.StatusForTesting.Contains("4 fields", StringComparison.Ordinal), dlg.StatusForTesting);
            ok &= Check("and the sample fits it, the dot standing for the space", !preview.SaysWhyNotForTesting);
            dlg.SetTemplateForTesting(@"{[*]}{[*]}{[*]}\.{*}");
            Pump();
            ok &= Check("while an escaped dot is an ordinary full stop, which this line has not got there",
                        preview.SaysWhyNotForTesting);
            dlg.SetTemplateForTesting("{[*]}{[*]}{[*]} {*}");
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
            // The spare rows come out of whatever room is left once everything else on the dialog has had
            // its own, so a screen too short for the dialog has none to give - and the list is then held at
            // its floor rather than squeezed away, which is the promise that actually matters.
            bool spare = sized.ListRoomInRowsForTesting >= sized.RowCountForTesting + 2;
            ok &= Check($"the field list opens with room for the fields and a few spare " +
                        $"({sized.ListRoomInRowsForTesting} rows for {sized.RowCountForTesting} fields)",
                        spare || sized.ListIsAtItsFloorForTesting,
                        $"{sized.ClientSize}; {sized.ContentFloorForTesting}");
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

    /// <summary>Turning the column view on and off - from the keyboard, which is what it is here for, and
    /// without the log appearing to slide when the header takes a row off the top of it.</summary>
    internal static bool RunColumnModeChecks()
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
}

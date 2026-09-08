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

/// <summary>Part of <see cref="Checks"/>: carrying a filter about with the mouse - where it lands, what it nests under, and every way a drag can end.</summary>
internal static partial class Checks
{

    /// <summary>Where a dragged filter lands is decided by the pointer alone: vertical position picks the
    /// gap, horizontal picks the nesting. Every rule here is a judgement about how the list should feel.</summary>
    internal static bool RunDropPlacementChecks()
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
    internal static bool RunFilterExpandChecks()
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
            host = new HiddenForm
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
    internal static bool RunDragNestingChecks()
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
            host = new HiddenForm
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
    internal static bool RunFilterDragChecks()
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
            host = new HiddenForm
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
}

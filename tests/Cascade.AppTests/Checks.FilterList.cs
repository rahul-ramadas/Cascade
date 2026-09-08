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

/// <summary>Part of <see cref="Checks"/>: the filter pane: what a row says, what can be done to a filter, and the surgical updates that keep it from flashing.</summary>
internal static partial class Checks
{

    /// <summary>The filter list draws three columns into one owner-drawn row, and TextRenderer goes through
    /// GDI, which ignores the GDI+ clip the columns rely on unless told not to. The symptom was a long
    /// pattern painting straight across the description and the count.</summary>
    internal static bool RunFilterListChecks()
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
            host = new HiddenForm
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

    /// <summary>The hover tip is the only place the app answers "why is this line here, and why that
    /// colour?". It has to name every filter that matched - including switched-off ones, which are the whole
    /// point of asking - and spell out patterns in full, since a friendly description is exactly what stops
    /// being enough at that moment.</summary>
    internal static bool RunFilterTipChecks()
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
        ok &= Check("a regex says so in words, with the pattern left as it was typed",
                    tip.Contains("[0-9]+ms (regex") && !tip.Contains("/[0-9]+ms/"), tip);
        ok &= Check("case sensitivity is spelled out", tip.Contains("case-sensitive"), tip);

        var plainCase = new Filter { Enabled = true, Match = { Text = "Fdo::", CaseSensitive = true } };
        ok &= Check("a plain pattern that only cares about case says just that",
                    FilterTipText.Build(new[] { plainCase }) == "Fdo:: (case-sensitive)",
                    FilterTipText.Build(new[] { plainCase }));

        var plain = new Filter { Enabled = true, Match = { Text = "Fdo::" } };
        ok &= Check("and one with nothing to remark on is quoted and nothing more",
                    FilterTipText.Build(new[] { plain }) == "Fdo::", FilterTipText.Build(new[] { plain }));

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
            host = new HiddenForm
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

            // --- the tip follows the lines, and the mouse never moves ---
            // A tip answers "what is under my pointer". Cropping, hiding the filtered-out lines and
            // switching a filter all change what is under it without the pointer going anywhere, so
            // nothing but this would ever put the words right.
            grid.HoverRowForTesting(0);
            grid.ShowTipNowForTesting();
            Pump();
            string resting = grid.ShownTipForTesting;
            ok &= Check($"resting on a line puts up what matched it (\"{resting.Split('\n')[0]}\")",
                        resting.Contains("Errors", StringComparison.Ordinal), resting);

            // A crop is the cheapest of the three to drive and asks exactly the same question: the top row
            // is a different line now.
            doc.SetCrop(2, 3);
            grid.RefreshView();
            Pump();
            ok &= Check($"cropping to another line rewrites the tip on the spot (\"{grid.ShownTipForTesting}\")",
                        grid.ShownTipForTesting.Contains("heartbeat (off)", StringComparison.Ordinal),
                        grid.ShownTipForTesting);

            doc.ClearCrop();
            grid.RefreshView();
            Pump();
            ok &= Check("and taking the crop away puts the first line's tip back",
                        grid.ShownTipForTesting.Contains("Errors", StringComparison.Ordinal),
                        grid.ShownTipForTesting);

            // Re-describing a filter matches the same lines, so it never reaches RefreshView - and the tip
            // quotes the description, so it goes stale by a different road.
            doc.Filters.Roots[0].Description = "Failures";
            grid.RefreshColors();
            Pump();
            ok &= Check($"re-describing a filter rewrites the tip too (\"{grid.ShownTipForTesting.Split('\n')[0]}\")",
                        grid.ShownTipForTesting.Contains("Failures", StringComparison.Ordinal),
                        grid.ShownTipForTesting);

            doc.SetCrop(1, 2);   // "plain line", which nothing matches at all
            grid.RefreshView();
            Pump();
            ok &= Check("a line with nothing to say about it takes the tip down",
                        grid.ShownTipForTesting.Length == 0, grid.ShownTipForTesting);

            doc.ClearCrop();
            grid.RefreshView();
            Pump();
            ok &= Check("and the hover stays armed, so the words come back when there are any again",
                        grid.ShownTipForTesting.Contains("Failures", StringComparison.Ordinal),
                        grid.ShownTipForTesting);

            // The pointer off the text is not a pointer resting on a line, and a change to the view must
            // not conjure a tip for one it is not on.
            grid.HideTipForTesting();
            doc.SetCrop(2, 3);
            grid.RefreshView();
            Pump();
            ok &= Check("nothing is put up for a pointer that is not on a line",
                        grid.ShownTipForTesting.Length == 0, grid.ShownTipForTesting);
            doc.ClearCrop();
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
    internal static bool RunNewFilterChecks()
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
            host = new HiddenForm
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

            ok = Check("with nothing to measure from, the preference sends it to the top",
                       MainForm.NewFilterSpot(NewFilterPlacement.Default, null, addAtTop: true, filters) == (null, 0));
            ok &= Check("or to the end",
                        MainForm.NewFilterSpot(NewFilterPlacement.Default, null, addAtTop: false, filters) == (null, -1));
            ok &= Check("above a filter means exactly where that filter is now, which pushes it down",
                        MainForm.NewFilterSpot(NewFilterPlacement.Above, roots[3], addAtTop: true, filters) == (null, 3));
            ok &= Check("and asking for above beats the preference",
                        MainForm.NewFilterSpot(NewFilterPlacement.Above, roots[3], addAtTop: false, filters) == (null, 3));
            ok &= Check("above a nested filter counts among ITS siblings, and lands under ITS parent",
                        MainForm.NewFilterSpot(NewFilterPlacement.Above, kid, addAtTop: true, filters) == (roots[3], 0));
            ok &= Check("a child goes under the filter itself, at whichever end the preference names",
                        MainForm.NewFilterSpot(NewFilterPlacement.Child, roots[3], addAtTop: true, filters) == (roots[3], 0) &&
                        MainForm.NewFilterSpot(NewFilterPlacement.Child, roots[3], addAtTop: false, filters) == (roots[3], -1));
            ok &= Check("with nothing selected, above and child both fall back to the default place",
                        MainForm.NewFilterSpot(NewFilterPlacement.Above, null, addAtTop: true, filters) == (null, 0) &&
                        MainForm.NewFilterSpot(NewFilterPlacement.Child, null, addAtTop: true, filters) == (null, 0));

            // Nesting has a floor, and the answer is the default place rather than a level that cannot exist.
            var deep = new FilterCollection();
            var chain = new Filter { Match = new FilterMatch { Text = "deep 0" } };
            deep.Roots.Add(chain);
            for (int d = 1; d < FilterCollection.MaxDepth; d++)
            {
                var next = new Filter { Match = new FilterMatch { Text = $"deep {d}" } };
                deep.Add(next, chain);
                chain = next;
            }
            ok &= Check($"the deepest filter really is at the floor (depth {chain.Depth})",
                        chain.Depth == FilterCollection.MaxDepth - 1);
            ok &= Check("and a child of it falls back to the default place rather than nesting past it",
                        MainForm.NewFilterSpot(NewFilterPlacement.Child, chain, addAtTop: true, deep) == (null, 0));
            ok &= Check("while a sibling above it is still perfectly legal",
                        MainForm.NewFilterSpot(NewFilterPlacement.Above, chain, addAtTop: true, deep) == (chain.Parent, 0));

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
            var askedFor = NewFilterPlacement.Default;
            void CountAdds(NewFilterPlacement p) { asked++; askedFor = p; }
            tree.AddRequested += CountAdds;
            tree.RaiseDoubleClickEventForTesting(new Point(tree.TreeWidthForTesting / 2, tree.TreeHeightForTesting - 2));
            Pump();
            ok &= Check($"double-clicking it asks for a filter in the default place (raised {asked}, {askedFor})",
                        asked == 1 && askedFor == NewFilterPlacement.Default);
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
            asked = 0;
            tree.AddRequested += CountAdds;

            var menu = tree.FilterMenuForTesting;
            var addItem = (ToolStripMenuItem)menu.Items[0];
            var addChildItem = (ToolStripMenuItem)menu.Items[1];
            tree.ScrollToForTesting(roots[0]);
            Pump();
            tree.ClickFilterForTesting(roots[2], button: MouseButtons.Right);
            tree.OpenFilterMenuForTesting();
            ok &= Check($"right-clicking a filter offers to add one above it [{addItem.Text}]",
                        addItem.Text == "Add Filter Above\u2026");
            ok &= Check($"and says which key does that [{addItem.ShortcutKeyDisplayString}]",
                        addItem.ShortcutKeyDisplayString == "Ctrl+Shift+N");
            addItem.PerformClick();
            ok &= Check($"and that is the place it asks for ({askedFor})",
                        asked == 1 && askedFor == NewFilterPlacement.Above);
            // Right-clicking a row selects it, which is what "the selected filter" then means to the dialog.
            ok &= Check("having selected the filter it was opened over",
                        ReferenceEquals(tree.SelectedFilter, roots[2]));

            tree.OpenFilterMenuForTesting();
            ok &= Check($"the child entry says its own key too [{addChildItem.ShortcutKeyDisplayString}]",
                        addChildItem.ShortcutKeyDisplayString == "Ctrl+Alt+N");
            addChildItem.PerformClick();
            ok &= Check($"and asks to nest under the selected filter ({askedFor})",
                        asked == 2 && askedFor == NewFilterPlacement.Child);

            tree.MouseDownForTesting(new Point(20, tree.TreeAreaForTesting.Height - 2), button: MouseButtons.Right);
            tree.OpenFilterMenuForTesting();
            // The fixture fills the list, so the point above is a row - what matters is the empty case, and
            // the only place that is certain to be empty is the blank strip. Ask about no row at all.
            tree.MouseDownForTesting(new Point(20, tree.TreeAreaForTesting.Height + rowH), button: MouseButtons.Right);
            tree.OpenFilterMenuForTesting();
            ok &= Check($"right-clicking clear of every filter offers a plain one [{addItem.Text}]",
                        addItem.Text == "Add Filter\u2026");
            ok &= Check($"on the plain key [{addItem.ShortcutKeyDisplayString}]",
                        addItem.ShortcutKeyDisplayString == "Ctrl+N");
            addItem.PerformClick();
            ok &= Check($"and asks for the default place ({askedFor})",
                        asked == 3 && askedFor == NewFilterPlacement.Default);
            tree.AddRequested -= CountAdds;

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

    /// <summary>Choosing where a new filter goes, from inside the dialog that makes it.
    ///
    /// <para>The three places are the whole point of the row, so what is checked here is that all three are
    /// always offered and always in the same order, that each says the key that picks it, that the key really
    /// picks it while the dialog is open - the way a mind is changed after Ctrl+N has already opened it - and
    /// that pressing one leaves the keyboard, the caret and the selection in the pattern box, since the
    /// pattern is usually half typed at that moment.</para></summary>
    internal static bool RunFilterPlacementChecks()
    {
        Line("-- where a new filter goes --");

        var defaults = new ResolvedStyle(new RgbColor(0, 0, 0), new RgbColor(255, 255, 255), false, false);
        RgbColor amber = new(0xFF, 0xC0, 0x00), slate = new(0x30, 0x30, 0x40);
        var branch = new Filter { Match = { Text = "ERROR" }, Style = { Foreground = amber, Background = slate } };
        var twig = new Filter { Match = { Text = "payment" } };
        var tree = new FilterCollection();
        tree.Roots.Add(branch);
        tree.Add(twig, branch);

        static FilterEditDialog Open(Filter filter, bool isNew, IReadOnlyList<Filter> siblings, ResolvedStyle defaults)
        {
            var dlg = new FilterEditDialog(filter, isNew, siblings, null, defaults)
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Opacity = 0
            };
            dlg.Show();
            Pump();
            return dlg;
        }

        var all = tree.EnumerateDepthFirst().ToList();
        bool ok;
        using (var dlg = Open(new Filter { Match = { Text = "declined" } }, isNew: true, all, defaults))
        {
            dlg.OfferPlacements(NewFilterPlacement.Default, twig, addAtTop: true);
            Pump();

            var places = dlg.PlacementsForTesting;
            ok = Check($"a filter being made is offered three places [{string.Join(" | ", places.Select(p => p.Text.Replace("&", "")))}]",
                       places.Count == 3);
            ok &= Check($"the first one follows the preference [{places[0].Text.Replace("&", "")}]",
                        places[0].Text == "&At the top of the list");
            dlg.OfferPlacements(NewFilterPlacement.Default, twig, addAtTop: false);
            Pump();
            ok &= Check($"and says so the other way round when new filters go to the end " +
                        $"[{dlg.PlacementsForTesting[0].Text.Replace("&", "")}]",
                        dlg.PlacementsForTesting[0].Text == "&At the end of the list");
            dlg.OfferPlacements(NewFilterPlacement.Default, twig, addAtTop: true);
            Pump();

            // The keys are the reason the row is worth learning, so each must be written beside its own
            // choice - and be the key that actually asks for it.
            string[] keys = [.. dlg.PlacementsForTesting.Select(p => p.Key)];
            ok &= Check($"each place shows the key that picks it [{string.Join(" | ", keys)}]",
                        keys is ["Ctrl+N", "Ctrl+Shift+N", "Ctrl+Alt+N"]);
            ok &= Check("and says the same key to a screen reader, which never reaches the label beside it",
                        dlg.PlacementsForTesting.All(p => p.Said == "Shortcut " + p.Key),
                        string.Join(" | ", dlg.PlacementsForTesting.Select(p => p.Said)));

            // ---- the keys, pressed while the dialog is open ----
            dlg.FocusTextForTesting(2, 3);
            Pump();
            ok &= Check("the pattern box has the keyboard before any of them is pressed", dlg.TextHasFocusForTesting);

            bool took = dlg.PressKeyForTesting(NewFilterKeys.AddAbove);
            ok &= Check($"Ctrl+Shift+N moves the choice to above the selected filter ({dlg.Placement})",
                        took && dlg.Placement == NewFilterPlacement.Above);
            took = dlg.PressKeyForTesting(NewFilterKeys.AddChild);
            ok &= Check($"Ctrl+Alt+N moves it to a child of it ({dlg.Placement})",
                        took && dlg.Placement == NewFilterPlacement.Child);
            took = dlg.PressKeyForTesting(NewFilterKeys.Add);
            ok &= Check($"and Ctrl+N moves it back to the default place ({dlg.Placement})",
                        took && dlg.Placement == NewFilterPlacement.Default);
            ok &= Check("changing your mind leaves the keyboard in the pattern box", dlg.TextHasFocusForTesting);
            ok &= Check($"with the caret and selection where they were {dlg.TextSelectionForTesting}",
                        dlg.TextSelectionForTesting == (2, 3));

            // The same, from the Alt keys the row underlines - the mouse-free route for anyone who reads the
            // dialog rather than the menu that opened it.
            ok &= Check("Alt+V picks above too", AltKey(dlg, 'v') && dlg.Placement == NewFilterPlacement.Above);
            ok &= Check("Alt+H picks the child", AltKey(dlg, 'h') && dlg.Placement == NewFilterPlacement.Child);
            ok &= Check("Alt+A picks the default place", AltKey(dlg, 'a') && dlg.Placement == NewFilterPlacement.Default);
            ok &= Check("and none of the three takes the keyboard either", dlg.TextHasFocusForTesting);
            ok &= Check($"nor the caret and selection {dlg.TextSelectionForTesting}",
                        dlg.TextSelectionForTesting == (2, 3));

            // ---- the preview follows the choice ----
            static bool Is(Color c, RgbColor want) => c.R == want.R && c.G == want.G && c.B == want.B;
            dlg.PressKeyForTesting(NewFilterKeys.AddChild);
            Pump();
            var p = dlg.PreviewForTesting;
            ok &= Check("nesting it under a filter previews what it would inherit there",
                        ReferenceEquals(dlg.PreviewParentForTesting, twig) &&
                        Is(p.Fore, amber) && Is(p.Back, slate));
            dlg.PressKeyForTesting(NewFilterKeys.AddAbove);
            Pump();
            p = dlg.PreviewForTesting;
            ok &= Check("putting it above that filter inherits from ITS parent instead",
                        ReferenceEquals(dlg.PreviewParentForTesting, branch) &&
                        Is(p.Fore, amber) && Is(p.Back, slate));
            dlg.PressKeyForTesting(NewFilterKeys.Add);
            Pump();
            p = dlg.PreviewForTesting;
            ok &= Check("and the default place puts it at the top level, where the view's own colours show",
                        dlg.PreviewParentForTesting is null &&
                        Is(p.Fore, defaults.Foreground) && Is(p.Back, defaults.Background));
        }

        // ---- what cannot be done is shown, and shut off ----
        using (var dlg = Open(new Filter { Match = { Text = "declined" } }, isNew: true, all, defaults))
        {
            dlg.OfferPlacements(NewFilterPlacement.Above, null, addAtTop: true);
            Pump();
            var places = dlg.PlacementsForTesting;
            ok &= Check($"with no filter selected, only the default place can be picked " +
                        $"[{string.Join(" ", places.Select(q => q.Enabled))}]",
                        places[0].Enabled && !places[1].Enabled && !places[2].Enabled);
            ok &= Check($"and asking for one that cannot be had settles on the default ({dlg.Placement})",
                        dlg.Placement == NewFilterPlacement.Default);
            ok &= Check("while its key still does nothing else",
                        dlg.PressKeyForTesting(NewFilterKeys.AddChild) &&
                        dlg.Placement == NewFilterPlacement.Default);

            // Nesting runs out of levels; sitting beside the deepest filter never does.
            var deep = new FilterCollection();
            var chain = new Filter { Match = { Text = "deep 0" } };
            deep.Roots.Add(chain);
            for (int d = 1; d < FilterCollection.MaxDepth; d++)
            {
                var next = new Filter { Match = { Text = $"deep {d}" } };
                deep.Add(next, chain);
                chain = next;
            }
            dlg.OfferPlacements(NewFilterPlacement.Child, chain, addAtTop: true);
            Pump();
            places = dlg.PlacementsForTesting;
            ok &= Check($"at the deepest level a child is shut off but a sibling above is not " +
                        $"[{string.Join(" ", places.Select(q => q.Enabled))}]",
                        places[1].Enabled && !places[2].Enabled);
            ok &= Check($"and asking to nest there settles on the default place ({dlg.Placement})",
                        dlg.Placement == NewFilterPlacement.Default);
        }

        // An edit moves nothing, so it is not asked where the filter should go.
        using (var dlg = Open(branch, isNew: false, all, defaults))
        {
            ok &= Check("editing a filter offers no places at all", !dlg.OffersPlacementForTesting);
            ok &= Check("and answers with the default one", dlg.Placement == NewFilterPlacement.Default);
            ok &= Check("and its keys are left to whatever else wants them",
                        !dlg.PressKeyForTesting(NewFilterKeys.AddAbove));
        }

        return ok;
    }

    /// <summary>The call WinForms itself makes for Alt+letter, so a check exercises the real dispatch rather
    /// than looking for an ampersand in a caption.</summary>
    private static bool AltKey(Form form, char ch)
        => (bool)typeof(Control)
            .GetMethod("ProcessMnemonic", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(form, new object[] { ch })!;

    internal static bool RunFilterSyncChecks()
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
            host = new HiddenForm
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

    internal static bool RunFilterEnableChecks()
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
            host = new HiddenForm
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
            void CountAdds(NewFilterPlacement _) => asked++;
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
    internal static bool RunFilterSelectionChecks()
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
            host = new HiddenForm
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

            // Ctrl+A only belongs to the list when the list has the keyboard - in the search box it still
            // means "select this text". Said here because the window is shown without being activated, so
            // nothing has the focus until something asks for it.
            tree.FocusList();
            ok &= Check("the list has the keyboard, so ctrl+a is its own", tree.ListHasFocus);
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

    internal static bool RunNewFilterFromLineChecks()
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

    /// <summary>Searching the filter list has the same problem as jumping to a log line: a match pinned to
    /// the bottom edge hides the siblings that give it its meaning.</summary>
    internal static bool RunFilterSearchRevealChecks()
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
            host = new HiddenForm
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

    /// <summary>Where the filter list was left is where it comes back. The edge it is docked to, the share of
    /// the window it was given there and whether it was on screen at all are preferences like any other, so a
    /// window built from a settings file naming them has to OPEN that way - being able to set the layout up
    /// again by hand is not the same thing.
    ///
    /// <para>Checked on a form that is actually shown, because the layout is restored in OnLoad: the panes
    /// are worked out from the window's size, and asked any earlier they would measure the wrong one.</para>
    /// </summary>
    internal static bool RunFilterPaneMemoryChecks()
    {
        Line("-- the filter list comes back where it was left --");

        static MainForm Open(AppSettings settings)
        {
            var form = new MainForm(settings, new MachineState(), Array.Empty<string>())
            {
                NoSavePrompt = true,
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(900, 700),
            };
            form.Show();
            Pump();
            return form;
        }

        var saved = new AppSettings { FilterListDock = FilterDock.Left };
        bool ok;
        using (var form = Open(saved))
        {
            var split = form.SplitForTesting;
            ok = Check("a saved edge of Left turns the divider on its side",
                       split.Orientation == Orientation.Vertical, split.Orientation.ToString());
            ok &= Check("and puts the list in the panel on the left",
                        form.FilterListIsFirstPanelForTesting &&
                        split.Panel2.Controls.Contains(form.GridForTesting));
            ok &= Check("and the list is on screen", form.FilterListVisibleForTesting);
            ok &= Check("and opening the window has not changed what was saved",
                        saved.FilterListDock == FilterDock.Left && saved.ShowFilterList,
                        $"{saved.FilterListDock}, shown {saved.ShowFilterList}");
            form.Close();
        }
        Pump();

        // Hidden is the awkward one: the pane it collapses belongs to whichever side the dock just put the
        // list on, so the two settings have to be applied in that order or the wrong panel disappears and
        // the log goes with it.
        var hidden = new AppSettings { FilterListDock = FilterDock.Top, ShowFilterList = false };
        using (var form = Open(hidden))
        {
            ok &= Check("a list saved hidden opens hidden", !form.FilterListVisibleForTesting);
            ok &= Check("and it is the list that is gone, not the log",
                        form.GridForTesting.Width > 50 && form.GridForTesting.Height > 50,
                        $"{form.GridForTesting.Width}x{form.GridForTesting.Height}");
            ok &= Check("and it comes back on the edge it was saved on",
                        form.ClickMenuForTesting("View", "Filter List Location", "Show/Hide Filter List") &&
                        form.FilterListVisibleForTesting && form.FilterListIsFirstPanelForTesting &&
                        form.SplitForTesting.Orientation == Orientation.Horizontal,
                        $"first panel {form.FilterListIsFirstPanelForTesting}, {form.SplitForTesting.Orientation}");
            form.Close();
        }
        Pump();

        // And the default is the layout the app has always opened with, so a machine that has never moved
        // the list sees no change at all.
        using (var form = Open(new AppSettings()))
        {
            var split = form.SplitForTesting;
            int total = split.Height - split.SplitterWidth;
            ok &= Check("with nothing saved the list is still under the log",
                        split.Orientation == Orientation.Horizontal && !form.FilterListIsFirstPanelForTesting &&
                        form.FilterListVisibleForTesting);
            ok &= Check("and the divider still opens at about seven tenths",
                        split.SplitterDistance > total * 0.6 && split.SplitterDistance < total * 0.85,
                        $"{split.SplitterDistance} of {total}");
            form.Close();
        }
        Pump();

        // A saved share has to survive the window being a different size than it was on: the whole reason it
        // is kept as a fraction rather than as the pixel count the divider deals in.
        var sized = new AppSettings { FilterListHeightFraction = 0.55 };
        foreach (var size in new[] { new Size(900, 700), new Size(1300, 980) })
        {
            sized.FilterListHeightFraction = 0.55;
            using var form = new MainForm(sized, new MachineState(), Array.Empty<string>())
            {
                NoSavePrompt = true,
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = size,
            };
            form.Show();
            Pump();
            double share = Share(form.SplitForTesting, form.FilterListIsFirstPanelForTesting);
            // Half a line of slack: the divider is rounded to whole lines after the share is applied.
            ok &= Check($"a saved share of 0.55 opens at 0.55 in a {size.Width}x{size.Height} window",
                        Math.Abs(share - 0.55) < 0.03, $"{share:F3}");
            form.Close();
            Pump();
        }

        return ok;
    }

    /// <summary>The filter search bar: a thing you open on Ctrl+E, use, and dismiss - not a permanent box
    /// taking a line off the top of the list for ever.</summary>
    internal static bool RunFilterSearchBarChecks()
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
            host = new HiddenForm
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
}

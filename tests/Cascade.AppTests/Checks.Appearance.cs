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

/// <summary>Part of <see cref="Checks"/>: how a filter decides what a line looks like: styles, inheritance, and the colours offered for them.</summary>
internal static partial class Checks
{

    /// <summary>Underline is a style a filter can set, like bold and italic. What proves it is on screen is
    /// a long unbroken run of the filter's own colour across a scanline - glyphs never draw one, so the
    /// same measurement over a line coloured but NOT underlined is the control that makes it mean
    /// something.</summary>
    internal static bool RunUnderlineChecks()
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
    internal static bool RunRestyleChecks()
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
    internal static bool RunColourWhileFilteringChecks()
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
        // Enabled and matching nothing: it stands in the list purely so that something does.
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
            // Ticking the exclude has to really sweep the file, rather than be answered in one go from what
            // an earlier pass recorded: this check is about what a half-finished pass leaves on screen.
            doc.SkipFilterCacheForTesting = true;
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

    /// <summary>Same filter, allowing for the fact that undo hands back a fresh object with the same id.</summary>
    private static bool ReferenceEquals2(Filter? a, Filter? b, FilterCollection _)
        => a is null ? b is null : b is not null && a.Id == b.Id;

    /// <summary>The suggested colours have one job each: to be readable, and to not be a colour some other
    /// filter is already wearing. A near-miss is worse than a repeat - two filters you cannot tell apart are
    /// two you will confuse without noticing.</summary>
    internal static bool RunLuckyColorChecks()
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

    /// <summary>Changing the appearance of several filters at once. The claim that matters most is the
    /// negative one: pressing OK must not write a pattern, a description or a kind onto anything, since one
    /// box cannot stand for what several filters match.</summary>
    internal static bool RunAppearanceChecks()
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
    internal static bool RunColorPreviewChecks()
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
    internal static bool RunStyleBoxChecks()
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
}

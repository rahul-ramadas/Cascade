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

/// <summary>Part of <see cref="Checks"/>: the minimap: the log zoomed out, and the marks in the scrollbar beside it.</summary>
internal static partial class Checks
{

    /// <summary>The minimap is the log seen from far enough away that a row is a pixel, so what matters is
    /// that a pixel stands for the right row in the right colour - and that the window it shows follows the
    /// view without chasing it. The summary is checked directly, and separately that it is painted, that it
    /// repaints when it must, and that it stays cheap.</summary>
    internal static bool RunMatchMapChecks()
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
            host = new HiddenForm
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

                // ---- the thumb goes to the screen before the view is told where to go ----
                // The hand is watching the thumb, so it must not queue behind a repaint of the text. Asked
                // of the ORDER rather than of a duration: a stopwatch here would be measuring this machine,
                // where the same question over a remote desktop is worth a whole frame's round trip.
                //
                // Only the guarantee is checked, not its opposite. Drawing the thumb last did not reliably
                // leave it undrawn - updating the view flushes whatever paint the strip beside it is owed
                // often enough that a check on that would report the weather. What changed is that it is now
                // certain rather than incidental, and that is what this pins.
                grid.ScrollToRow(0);
                Pump();
                int paintedWhenTold = -1;
                Action<long> noteThumb = _ => paintedWhenTold = bar.PaintsForTesting;
                bar.Scrolled += noteThumb;
                try
                {
                    bar.GrabForTesting();
                    int thumbPaints = bar.PaintsForTesting;
                    bar.DragToForTesting(track.Top + thumb.Height / 2 + track.Height / 3);
                    bar.DropForTesting();
                    Pump();
                    ok &= Check("the thumb is on the screen before the view is told to move",
                                paintedWhenTold > thumbPaints,
                                $"{thumbPaints} paints before the report, {paintedWhenTold} when the view was told");
                }
                finally { bar.Scrolled -= noteThumb; }

                // A file of millions of lines moves the thumb by a fraction of a pixel per row, so most
                // reports of a slow drag would repaint it in exactly the place it is already in. Over a wire
                // that is a whole update sent to say nothing.
                grid.ScrollToRow(0);
                bar.Value = 0;
                Pump();
                var resting = bar.ThumbForTesting;
                int quiet = bar.PaintsForTesting;
                int stayedPut = 0;
                for (long row = 1; row <= 4; row++)
                {
                    bar.Value = row;
                    if (bar.ThumbForTesting == resting) stayedPut++;
                    Pump();
                }
                ok &= Check("and a value that does not move the thumb a whole pixel does not repaint it",
                            stayedPut == 4 && bar.PaintsForTesting == quiet,
                            $"{stayedPut} of 4 left it in place, {bar.PaintsForTesting - quiet} paints");

                // ---- the marks down the trough are worked out once, not once a frame ----
                // Which marker a pixel of the trough stands for depends on the marks and on which line each
                // row is; a drag changes neither. MEASURED on a 25-million-line log with five thousand marks,
                // re-deriving it per report cost 1.77 ms a report against 0.93.
                for (int i = 0; i < 400; i++) doc.Markers.Set(i * (lines / 400L), i % 8, true);
                grid.ScrollToRow(0);
                Pump();
                bar.RederiveMarksForTesting = true;
                bar.Invalidate();
                Pump();
                using (var everyFrame = CaptureControl(bar))
                {
                    bar.RederiveMarksForTesting = false;
                    bar.Invalidate();
                    Pump();
                    using var kept = CaptureControl(bar);
                    var markDiff = FirstDifference(everyFrame, kept, new Rectangle(0, 0, bar.Width, bar.Height));
                    ok &= Check("keeping the mark scale draws the marks working it out every frame drew" +
                                (markDiff is null ? "" : $" [first differs at x={markDiff.Value.X},y={markDiff.Value.Y}]"),
                                markDiff is null);
                }

                int builds = bar.MarkScaleBuildsForTesting;
                bar.GrabForTesting();
                for (int i = 0; i < 30; i++)
                    bar.DragToForTesting(track.Top + thumb.Height / 2 + i * (track.Height / 60));
                bar.DropForTesting();
                Pump();
                ok &= Check("and a drag does not work it out again",
                            bar.MarkScaleBuildsForTesting == builds,
                            $"{bar.MarkScaleBuildsForTesting - builds} rebuilds across 30 reports");

                // ...but marking another line must still show up, or the scale would be a stale picture.
                doc.Markers.Set(lines / 2 + 1, 0, true);
                bar.Invalidate();
                Pump();
                ok &= Check("while a new mark does make it work the scale out again",
                            bar.MarkScaleBuildsForTesting > builds);
                doc.Markers.Clear();
                bar.Invalidate();
                Pump();
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

    /// <summary>A file small enough for the map to hold is shown whole: compressed only as much as it takes
    /// to fit, anchored at the top, and never a window - so the map and the scrollbar agree about where you
    /// are, and scrolling the log never moves the map at all.</summary>
    internal static bool RunMapWholeFileChecks()    {
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
            host = new HiddenForm
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
}

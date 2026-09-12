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

/// <summary>Part of <see cref="Checks"/>: the find bar - one row, docked inside the log view - and its promise to move nothing else on screen.</summary>
internal static partial class Checks
{

    internal static bool RunFilterNavigationChecks()
    {
        string path = Path.Combine(Path.GetTempPath(), "cascade_navigation_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(path, Enumerable.Range(0, 30).Select(line =>
            $"[2026-09-11T09:31:{line:00}] " + (line % 3 == 0 ? $"TARGET line {line}" : $"plain line {line}")));
        using var form = new MainForm(new AppSettings(), new MachineState(), []) { NoSavePrompt = true, Opacity = 0 };
        using var context = new WindowsFormsSynchronizationContext();
        try
        {
            form.Show();
            form.OpenForTesting(path);
            var doc = form.DocForTesting;
            doc.WaitForIndex();
            var target = new Filter { Enabled = false, Match = { Text = "TARGET" } };
            var other = new Filter { Enabled = false, Match = { Text = "plain" } };
            doc.Filters.Add(target);
            doc.Filters.Add(other);
            doc.ApplyFilters();
            var tree = form.FilterTreeForTesting;
            var grid = form.GridForTesting;
            tree.Rebuild();
            tree.SelectForTesting(target);
            Pump();
            using var originalHeader = tree.HeaderPictureForTesting();
            bool Wait(Func<bool> ready)
            {
                var watch = Stopwatch.StartNew();
                while (!ready() && watch.ElapsedMilliseconds < 10_000) Pump();
                return ready();
            }

            bool Press(Keys keys)
            {
                var previous = SynchronizationContext.Current;
                try { SynchronizationContext.SetSynchronizationContext(context); return form.PressCmdKeyForTesting(keys); }
                finally { SynchronizationContext.SetSynchronizationContext(previous); }
            }

            bool ok = Check("F4 reaches the filter navigation command", Press(Keys.F4));
            ok &= Check("the first match is counted in the activity slot", Wait(() => form.ActivityTextForTesting == "1 of 10 matching lines"), form.ActivityTextForTesting);
            ok &= Check("the detail identifies the filter", form.ActivityDetailForTesting.Contains("TARGET", StringComparison.Ordinal));
            ok &= Check("the activity slot stays onscreen", form.ActivityIsOnscreenForTesting);
            ok &= Check("the filter header keeps its identity", tree.Controls.OfType<FilterListHeader>().Single().AccessibleName == "Filter list");
            using (var currentHeader = tree.HeaderPictureForTesting())
            {
                bool unchanged = originalHeader.Size == currentHeader.Size;
                for (int vertical = 0; unchanged && vertical < originalHeader.Height; vertical++)
                for (int horizontal = 0; unchanged && horizontal < originalHeader.Width; horizontal++)
                    unchanged = originalHeader.GetPixel(horizontal, vertical) == currentHeader.GetPixel(horizontal, vertical);
                ok &= Check("navigation does not replace any header pixels", unchanged);
            }
            for (int match = 2; match <= 4; match++)
            {
                Press(Keys.F4);
                ok &= Check($"F4 reaches match {match}", Wait(() => form.ActivityTextForTesting == $"{match} of 10 matching lines"), form.ActivityTextForTesting);
            }
            Press(Keys.Shift | Keys.F4);
            ok &= Check("Shift+F4 decrements the ordinal", Wait(() => form.ActivityTextForTesting == "3 of 10 matching lines"), form.ActivityTextForTesting);
            Press(Keys.Control | Keys.Shift | Keys.L);
            ok &= Check("the filter pane really is hidden", !form.FilterListVisibleForTesting);
            ok &= Check("hiding the pane leaves the tally in the same slot", form.ActivityTextForTesting == "3 of 10 matching lines", form.StatusForTesting);
            var previousSize = form.ClientSize;
            var previousWindowState = form.WindowState;
            form.WindowState = FormWindowState.Normal;
            form.ClientSize = new Size(form.LogicalToDeviceUnits(720), form.LogicalToDeviceUnits(560));
            Pump();
            ok &= Check("the narrow status includes the elapsed-time slot", doc.Clock is not null);
            ok &= Check("the status check really narrowed the window", form.ClientSize.Width == form.LogicalToDeviceUnits(720));
            ok &= Check("the activity's whole bounds stay onscreen", form.ActivityIsOnscreenForTesting, form.StatusLayoutForTesting);
            ok &= Check("the narrow activity slot keeps both numbers", form.StatusForTesting.Contains("3 of 10", StringComparison.Ordinal)
                || form.StatusForTesting.Contains("3/10", StringComparison.Ordinal), form.StatusForTesting);
            form.ClientSize = previousSize;
            form.WindowState = previousWindowState;
            Press(Keys.Control | Keys.Shift | Keys.L);
            ok &= Check("the tally remains in the status when the pane returns", form.ActivityTextForTesting == "3 of 10 matching lines");
            tree.SelectForTesting(other);
            ok &= Check("another selection clears the old filter tally", form.ActivityTextForTesting.Length == 0);
            tree.SelectForTesting(target);
            Press(Keys.Shift | Keys.F4);
            ok &= Check("reverse navigation starts the tally again", Wait(() => form.ActivityTextForTesting == "2 of 10 matching lines"), form.ActivityTextForTesting);
            Press(Keys.Shift | Keys.F4);
            ok &= Check("reverse reaches the first match", Wait(() => form.ActivityTextForTesting == "1 of 10 matching lines"), form.ActivityTextForTesting);
            Press(Keys.Shift | Keys.F4);
            ok &= Check("no-more feedback takes priority in the shared slot", Wait(() => form.ActivityTextForTesting == "No more matches"), form.ActivityTextForTesting);
            Press(Keys.F4);
            ok &= Check("cycling again replaces no-more feedback with the tally", Wait(() => form.ActivityTextForTesting == "2 of 10 matching lines"), form.ActivityTextForTesting);
            Press(Keys.Control | Keys.F);
            ok &= Check("text find retires the filter tally", form.ActivityTextForTesting.Length == 0);

            form.CloseFindForTesting();
            var cold = new Filter { Enabled = false, Match = { Text = "TARGET.*line", Regex = true } };
            doc.Filters.Add(cold);
            foreach (bool changeSelection in new[] { false, true })
            {
                cold.Match.Text = changeSelection ? "TARGET.+line" : "TARGET.*line";
                doc.ApplyFilters();
                tree.Rebuild();
                tree.SelectForTesting(cold);
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                doc.FilterFindCheckpointForTesting = _ => { entered.Set(); release.Wait(TimeSpan.FromSeconds(10)); };
                try
                {
                    Press(Keys.F4);
                    ok &= Check("the pending filter search reaches its gate", entered.Wait(TimeSpan.FromSeconds(5)));
                    ok &= Check("the pending search has a busy indicator", form.StatusForTesting.Contains("Searching", StringComparison.Ordinal));
                    if (changeSelection)
                    {
                        grid.SelectRowForAccessibility(1);
                        ok &= Check("selection cancels a search that is still running", form.ActivityTextForTesting.Length == 0);
                    }
                    release.Set();
                    ok &= Check("the worker completes before its UI continuation", SpinWait.SpinUntil(() => !doc.IsFindRunning, 5000));
                    if (!changeSelection)
                    {
                        ok &= Check("the UI continuation really is still queued", form.StatusForTesting.Contains("Searching", StringComparison.Ordinal));
                        ok &= Check("Escape is handled in the completion window", Press(Keys.Escape));
                    }
                    Pump();
                    ok &= Check("cancelling retires the search's busy indicator",
                        !form.StatusForTesting.Contains("Searching", StringComparison.Ordinal), form.StatusForTesting);
                    ok &= Check("the late result cannot put its tally back", form.ActivityTextForTesting.Length == 0,
                        $"activity={form.ActivityTextForTesting}; focus={form.FocusedAreaForTesting}; find={form.FindBarIsOpenForTesting}");
                    if (changeSelection) ok &= Check("the late result cannot replace the user's selection", grid.CaretLine == 1);
                }
                finally { release.Set(); doc.FilterFindCheckpointForTesting = null; }
            }

            tree.SelectForTesting(target);
            Press(Keys.F4);
            ok &= Check("navigation is active before the selection changes", Wait(() => form.ActivityTextForTesting.Contains("of 10", StringComparison.Ordinal)));
            var workProperty = typeof(CascadeDocument).GetProperty("FilterNavigationWorkForTesting", BindingFlags.Instance | BindingFlags.NonPublic)!;
            ok &= Check("the active navigation has a cached index", workProperty.GetValue(doc) is Task { IsCompletedSuccessfully: true });
            grid.SelectRowForAccessibility(1);
            ok &= Check("a manual log selection clears the tally immediately", form.ActivityTextForTesting.Length == 0, form.ActivityTextForTesting);
            ok &= Check("a manual log selection releases the navigation index", workProperty.GetValue(doc) is null);
            Pump();
            ok &= Check("refresh cannot resurrect the retired tally", form.ActivityTextForTesting.Length == 0, form.ActivityTextForTesting);

            foreach (bool sameCaret in new[] { true, false })
            {
                Press(Keys.F4);
                ok &= Check("F4 reactivates the tally after manual selection", Wait(() => form.ActivityTextForTesting.Contains("of 10", StringComparison.Ordinal)));
                var activeIndex = workProperty.GetValue(doc);
                long caret = grid.CaretLine;
                string tally = form.ActivityTextForTesting;
                grid.PressKeyForTesting(Keys.Control | Keys.Down);
                Pump();
                ok &= Check("scrolling alone keeps the tally and its cache", form.ActivityTextForTesting == tally
                    && ReferenceEquals(activeIndex, workProperty.GetValue(doc)));
                if (sameCaret)
                {
                    grid.SelectLinesForTesting(caret, caret + 1);
                    ok &= Check("the selection changed without moving the caret", grid.CaretLine == caret && grid.SelectedCount == 2);
                }
                else grid.PressKeyForTesting(Keys.Down);
                ok &= Check("range and keyboard selection changes clear the activity immediately", form.ActivityTextForTesting.Length == 0, form.ActivityTextForTesting);
                ok &= Check("range and keyboard selection changes release the cached index", workProperty.GetValue(doc) is null);
            }

            var font = form.Controls.OfType<StatusStrip>().Single().Font;
            foreach (int width in new[] { 220, 360, 640 })
            {
                int pixels = form.LogicalToDeviceUnits(width);
                string text = FindStatusText.FitNavigationText(new(99_999_999, 100_000_000, true), pixels, font);
                int measured = TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;
                ok &= Check($"large counts fit a {width}px slot", measured <= pixels, text);
                ok &= Check($"large counts keep both numbers at {width}px", text.Contains("99,999,999", StringComparison.Ordinal)
                    && text.Contains("100,000,000", StringComparison.Ordinal), text);
            }
            ok &= Check("unfinished counts are labelled honestly", FindStatusText.NavigationText(default).StartsWith("Counting", StringComparison.Ordinal));
            ok &= Check("an empty result has a complete readable label", FindStatusText.NavigationText(new(0, 0, true)) == "No matching lines");
            return ok;
        }
        finally
        {
            form.Close();
            form.Dispose();
            File.Delete(path);
        }
    }

    /// <summary>Every occurrence of the find term is marked on every visible line, and the line the search
    /// landed on is marked more strongly - which is how navigation can stay line-by-line without leaving you
    /// wondering which line it meant.</summary>
    internal static bool RunFindHighlightChecks()
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

    /// <summary>What a search reports. The rules are all about saying no more than there is to say: hit
    /// counts only when a line matched more than once, a hidden half only when the filters are keeping
    /// matches back, and a "+" on anything the sweep has not finished counting. One word per idea throughout
    /// - a "line" is a line that matched, a "hit" is one occurrence, and "hidden" is the filters and nothing
    /// else, a crop having been counted as the file long before the wording sees it.</summary>
    internal static bool RunFindStatusChecks()
    {
        Line("-- find status wording --");
        static FindTally T(long pos, long shown, long hidden, long shownOcc, long occ, bool complete = true,
                           HitCount hits = HitCount.Exact)
            => new(pos, shown, hidden, shownOcc, occ, complete, hits);

        string plain = FindStatusText.Short(T(12, 348, 0, 348, 348));
        bool ok = Check("the simple case says just where you are", plain == "Match 12 of 348 lines", plain);

        string multi = FindStatusText.Short(T(12, 348, 0, 1204, 1204));
        ok &= Check("hits appear only when a line matched more than once",
                    multi == "Match 12 of 348 lines, 1,204 hits", multi);

        string hiddenText = FindStatusText.Short(T(12, 252, 96, 891, 1204));
        ok &= Check("what is hidden is counted in lines AND in hits, so neither needs working out",
                    hiddenText == "Match 12 of 252 lines, 891 hits \u00b7 hidden: 96 lines, 313 hits", hiddenText);

        string hiddenPlain = FindStatusText.Short(T(12, 252, 96, 252, 348));
        ok &= Check("with one hit per line there is nothing for the hits to add",
                    hiddenPlain == "Match 12 of 252 lines \u00b7 hidden: 96 lines", hiddenPlain);

        string partial = FindStatusText.Short(T(12, 252, 96, 891, 1204, complete: false));
        ok &= Check("an unfinished sweep marks every count", partial.Count(c => c == '+') == 4, partial);

        string none = FindStatusText.Short(T(0, 0, 0, 0, 0));
        ok &= Check("nothing found says so", none == "No matches", none);

        string searching = FindStatusText.Short(T(0, 0, 0, 0, 0, complete: false));
        ok &= Check("nothing found yet does not", searching == "Searching\u2026", searching);

        string offMatch = FindStatusText.Short(T(0, 348, 0, 348, 348));
        ok &= Check("off a match the count says what it is a count of", offMatch == "348 lines", offMatch);

        string offMatchDetailed = FindStatusText.Short(T(0, 252, 96, 891, 1204));
        ok &= Check("and still splits the shown from the hidden",
                    offMatchDetailed == "252 lines, 891 hits \u00b7 hidden: 96 lines, 313 hits", offMatchDetailed);

        // Everything the term found is being filtered away. Saying "0 lines" would read as a failed search
        // next to a hidden count that plainly found something.
        string allHidden = FindStatusText.Short(T(0, 0, 96, 0, 313));
        ok &= Check("with every match filtered away the head says so rather than counting to zero",
                    allHidden == "No matches shown \u00b7 hidden: 96 lines, 313 hits", allHidden);

        ok &= Check("no bare number ever reaches the find bar",
                    !long.TryParse(offMatch.Replace(",", ""), out _) &&
                    !long.TryParse(plain.Replace(",", ""), out _), $"{offMatch} / {plain}");

        // The record of which lines matched more than once is capped. Losing it costs only the split between
        // shown and hidden, so the total is still worth saying - and is said as a total, not as a fraction
        // with a floor on it.
        string split = FindStatusText.Short(T(12, 252, 96, 891, 1204, hits: HitCount.SplitUnknown));
        ok &= Check("a split that cannot be counted falls back to the total, which can",
                    split == "Match 12 of 252 lines \u00b7 hidden: 96 lines \u00b7 1,204 hits in all", split);

        string splitLong = FindStatusText.Long(T(12, 252, 96, 891, 1204, hits: HitCount.SplitUnknown), "disk");
        ok &= Check("and says why in the long form",
                    splitLong.Contains("Too many lines matched more than once") && splitLong.Contains("1,204 hits"),
                    splitLong);

        // Inside a crop the total is worked out from that same capped record, so nothing about the hits can
        // be trusted and none of it is shown.
        string unknown = FindStatusText.Short(T(12, 252, 96, 891, 1204, hits: HitCount.TotalUnknown));
        ok &= Check("a total that cannot be counted is left out rather than guessed at",
                    unknown == "Match 12 of 252 lines \u00b7 hidden: 96 lines", unknown);

        // The reported bug: with nothing hidden every hit is on a shown line, so the count is exact whatever
        // the cap did, and marking it as anything else was simply wrong.
        string nothingHidden = FindStatusText.Short(T(12, 252, 0, 1204, 1204));
        ok &= Check("with nothing hidden the count is exact and is shown in full",
                    nothingHidden == "Match 12 of 252 lines, 1,204 hits", nothingHidden);

        string detail = FindStatusText.Long(T(12, 252, 96, 891, 1204), "disk");
        ok &= Check("the long form names the term and holds every number, totals included",
                    detail.Contains("disk") && detail.Contains("348") && detail.Contains("252") &&
                    detail.Contains("96") && detail.Contains("891") && detail.Contains("313") &&
                    detail.Contains("1,204") && detail.Contains("You are on match 12"), detail);

        // A crop is the file, so it can never reach the wording as something being hidden: with no filters
        // running, a cropped tally reads exactly as an uncropped one over a file that short.
        string cropped = FindStatusText.Short(T(3, 12, 0, 12, 12));
        ok &= Check("a crop with no filters says nothing about hiding at all",
                    cropped == "Match 3 of 12 lines" && !cropped.Contains("hidden"), cropped);

        return ok;
    }

    /// <summary>The find bar as a text box: pressing Enter is a request to search, not a reason to disturb
    /// what has been typed. Repeating a search must also cost nothing - it is held down.</summary>
    internal static bool RunFindBarChecks()
    {
        Line("-- the find bar --");
        var searched = new List<(FindQuery Query, bool Forward)>();
        var dlg = new FindBar((q, f) => searched.Add((q, f))) { Visible = true };
        var host = new HiddenForm { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), Opacity = 0, ClientSize = new Size(900, 60) };
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

            // When the counts get re-read. Three things move underneath them - the sweep gathering matches,
            // the filters deciding which of them can be reached, and the crop bounding what is counted at
            // all - and all have to be watched the same way.
            var fresh = TimeSpan.Zero;
            var old = TimeSpan.FromSeconds(1);
            bool Stale(bool swept = true, bool wasSwept = true, bool settled = true, bool wasSettled = true,
                       bool sameLine = true, bool sameFilters = true, bool sameHiding = true,
                       bool sameCrop = true, bool haveText = true, TimeSpan? age = null)
                => MainForm.TallyIsStale(swept, wasSwept, settled, wasSettled, sameLine, sameFilters,
                                         sameHiding, sameCrop, haveText, age ?? fresh);

            ok &= Check("a running sweep is re-read as it goes", Stale(swept: false, wasSwept: false, age: old));
            ok &= Check("but not faster than the eye", !Stale(swept: false, wasSwept: false, age: fresh));
            ok &= Check("the sweep finishing is a reason on its own", Stale(swept: true, wasSwept: false));
            ok &= Check("a settled search that nothing has touched is left alone", !Stale(age: old));
            ok &= Check("moving the caret changes which match you are on", Stale(sameLine: false));
            ok &= Check("and a filter edit changes what is hidden", Stale(sameFilters: false));
            ok &= Check("so does hiding or showing the lines that did not match", Stale(sameHiding: false));
            // Ctrl+] moves no caret and edits no filter, and every number in the tally is counted within the
            // crop - so without this the counts stand until the next Enter happens to move the caret.
            ok &= Check("cropping or uncropping changes what the counts are counted within",
                        Stale(sameCrop: false));
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

    /// <summary>Where the find bar sits in the window. Opening it has to come out of the text area and
    /// nothing else: the filter pane keeps the size the user gave it, and the lines left in the log stay
    /// whole - which only works if the bar itself is a whole number of them.</summary>
    internal static bool RunFindBarLayoutChecks()
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
            findBar.SetMessage("Match 5 of 8 lines");
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
            findBar.SetMessage("Match 5 of 8 lines");
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
                        findBar.MessageForTesting() == "Match 5 of 8 lines", findBar.MessageForTesting());
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

    internal static bool RunFindBarRepaintChecks()
    {
        Line("-- the count changing does not disturb the bar --");

        var bar = new FindBar((_, _) => { }) { Visible = true };
        var host = new HiddenForm
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
            bar.SetMessage("Match 99 of 348 lines", "\u201cdisk\u201d matches 348 lines. You are on match 99.");
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

    /// <summary>The bar appears above the log, so the room for it has to come off the TOP: the log is
    /// scrolled on by as much as the bar takes, which leaves every line still showing exactly where it was
    /// on screen. Keeping the top row instead slides the whole log down and drops its last lines, which
    /// reads as the text moving rather than as the bar covering it.</summary>
    internal static bool RunFindBarRoomChecks()
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
    internal static bool RunFindSeedChecks()
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
}

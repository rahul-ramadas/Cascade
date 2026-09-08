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

/// <summary>Part of <see cref="Checks"/>: the window's own furniture: the status bar, the divider, the frame, and what it accepts from outside.</summary>
internal static partial class Checks
{

    /// <summary>The status bar used to repeat the caret's line number and the total beside it. The gutter
    /// already gives the first and the field beside it the second, and neither means much with several lines
    /// selected - so the space says which lines are being shown instead, which nothing else on screen does.
    /// </summary>
    internal static bool RunStatusBarChecks()
    {
        Line("-- what the status bar says --");

        string log = Path.Combine(Path.GetTempPath(), "cascade_status_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(log, Enumerable.Range(1, 200).Select(i => $"line {i} of the log"));

        MainForm? form = null;
        try
        {
            string[] args = [log];
            form = new MainForm(new AppSettings(), new MachineState(), args)
            {
                NoSavePrompt = true,
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(1100, 700),
            };
            form.Show();
            Pump();
            for (int i = 0; i < 60 && form.DocForTesting.CompletedLineCount < 200; i++) { Thread.Sleep(20); Pump(); }

            form.GridForTesting.GoToLine(42);
            Pump();
            string status = form.StatusForTesting;
            Line($"   ({status})");

            bool ok = Check("the caret's line number is not repeated in the status bar",
                            !status.Contains("Ln:", StringComparison.Ordinal), status);
            ok &= Check("the line count is still there", status.Contains("Total: 200", StringComparison.Ordinal));
            ok &= Check("and so is the matched count", status.Contains("Fil:", StringComparison.Ordinal));
            ok &= Check("it says every line is being shown",
                        status.Contains("Showing: all lines", StringComparison.Ordinal), status);

            form.ClickMenuForTesting("View", "Show Only Filtered Lines");
            Pump();
            ok &= Check("and says so when only the matches are",
                        form.StatusForTesting.Contains("Showing: matches", StringComparison.Ordinal),
                        form.StatusForTesting);

            form.ClickMenuForTesting("View", "Show Only Filtered Lines");
            Pump();
            ok &= Check("and back again", form.StatusForTesting.Contains("Showing: all lines", StringComparison.Ordinal),
                        form.StatusForTesting);

            // Every measurement is parted from the one beside it. Without a divider of its own each count
            // ran into its neighbour - and since each box is sized for a file of a hundred million lines,
            // that neighbour is a long way from where the number before it ended.
            string dividers = form.StatusDividersForTesting;
            Line("   (" + dividers + ")");
            string[] parted = ["|sel", "|fil", "|total", "|show", "|zoom"];
            ok &= Check("every count on the bar is parted from the one beside it",
                        parted.All(p => dividers.Contains(p, StringComparison.Ordinal)), dividers);
            // ...and the first thing on the bar carries none: it has nothing to its left to be parted from.
            ok &= Check("and the first field carries no divider of its own",
                        !dividers.StartsWith('|'), dividers);
            return ok;
        }
        finally
        {
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            Pump();
            try { File.Delete(log); } catch { /* ignore */ }
        }
    }

    /// <summary>The divider between the log and the filter list has to land where the log holds a whole
    /// number of lines. Anywhere else leaves a strip of dead space under the last one, which reads as a line
    /// that failed to draw rather than as a gap.</summary>
    internal static bool RunSplitterChecks()
    {
        Line("-- the divider snaps to whole lines --");

        using var form = new MainForm(new AppSettings(), new MachineState(), Array.Empty<string>())
        {
            Opacity = 0,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Size = new Size(900, 700),
        };
        form.NoSavePrompt = true;
        form.Show();
        Pump();

        var grid = form.GridForTesting;
        var split = form.SplitForTesting;
        int pitch = grid.RowPitch, chrome = grid.ChromeHeight;
        int total = split.Height - split.SplitterWidth;

        bool ok = Check("the log is in the first panel, with the filter list under it",
                        split.Orientation == Orientation.Horizontal && !split.Panel1Collapsed,
                        $"{split.Orientation}, panel1 collapsed {split.Panel1Collapsed}");
        ok &= Check("a line has a height to snap to", pitch > 1, pitch.ToString());

        int Lines(int distance) => distance - chrome;
        ok &= Check("it opens holding a whole number of lines", Lines(split.SplitterDistance) % pitch == 0,
                    $"{Lines(split.SplitterDistance)}px of text, a line is {pitch}px");
        ok &= Check("and about seven tenths of the window, as it always has",
                    split.SplitterDistance > total * 0.6 && split.SplitterDistance < total * 0.85,
                    $"{split.SplitterDistance} of {total}");

        // Dragging lands wherever the pointer is; the divider has to settle on the nearest line boundary.
        int start = split.SplitterDistance;
        foreach (int nudge in new[] { 3, 7, -5, -11, 1 })
        {
            int asked = start + nudge;
            try { split.SplitterDistance = asked; } catch { continue; }
            Pump();
            int got = split.SplitterDistance;
            ok &= Check($"a drag to {nudge:+#;-#;0} settles on a line boundary", Lines(got) % pitch == 0,
                        $"asked {asked}, settled at {got}, which is {Lines(got)}px of text");
            ok &= Check($"and on the nearest one", Math.Abs(got - asked) <= pitch / 2 + 1,
                        $"asked {asked}, settled at {got}, a line is {pitch}px");
        }

        // Wrapping hides the sideways scrollbar, so the chrome the divider measures from changes under it.
        // Rounding the same way every time then hands the log a line on each toggle and never gives one
        // back, and the filter list is eaten a line at a time.
        int before = split.SplitterDistance;
        var walked = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            form.ClickMenuForTesting("View", "Word Wrap");
            Pump();
            walked.Add(split.SplitterDistance);
        }
        ok &= Check("toggling word wrap does not walk the divider",
                    walked.TrueForAll(d => Math.Abs(d - before) <= pitch),
                    $"started at {before}, went {string.Join(" -> ", walked)}");
        ok &= Check("and leaves it where it started", split.SplitterDistance == before,
                    $"{before} -> {split.SplitterDistance}");

        // Growing the window must not leave the log holding part of a line either.
        form.Size = new Size(900, 743);
        Pump();
        ok &= Check("resizing the window leaves it on a line boundary too",
                    Lines(split.SplitterDistance) % pitch == 0,
                    $"{Lines(split.SplitterDistance)}px of text at window height {form.Height}");

        form.Close();
        return ok;
    }

    /// <summary>Win+Left has to leave the window filling half the screen, the way it does for every other
    /// app - it used to leave it at MinimumSize in the corner instead.
    ///
    /// <para>Snapping un-maximises the window as the first half of what it does, and WinForms hands back any
    /// bounds that were set while the window WAS maximised at exactly that moment, over the top of the
    /// rectangle the snap just gave it. So the check is about what the constructor leaves behind, and the
    /// window is therefore built exactly as a user gets it and not adjusted afterwards. What the snap does
    /// to it is done here in one operation, which is the single WM_WINDOWPOSCHANGED the bug rode in on;
    /// driving the real hotkey would need the foreground and would snap whatever the reader was in.</para></summary>
    internal static bool RunWindowSnapChecks()
    {
        Line("-- Win+Left snaps the window to half the screen --");

        using var form = ShippedWindow();
        form.Show();
        Pump();

        bool ok = Check($"the window opens maximised ({form.WindowState})",
                        form.WindowState == FormWindowState.Maximized);

        var work = Screen.FromControl(form).WorkingArea;
        // Windows will not hand back less than the window says it can be worked in.
        var want = new Rectangle(work.Location,
                                 new Size(Math.Max(work.Width / 2, form.MinimumSize.Width),
                                          Math.Max(work.Height, form.MinimumSize.Height)));

        var placement = new WINDOWPLACEMENT { Length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
        GetWindowPlacement(form.Handle, ref placement);
        placement.ShowCmd = SW_SHOWNOACTIVATE;
        placement.NormalPosition = RECT.From(want);
        SetWindowPlacement(form.Handle, ref placement);
        Pump();

        ok &= Check($"and keeps the half of it that the snap gave it ({form.Bounds})",
                    form.Bounds == want, $"asked for {want}, work area {work}");
        form.Close();

        // The other half of having a size before being maximised: this is what Win+Down and a double-clicked
        // title bar restore to, and a window that opens at MinimumSize has nothing better to offer them.
        // A second window, because the snap above has already rewritten the first one's normal position.
        using var restored = ShippedWindow();
        restored.Show();
        Pump();
        restored.WindowState = FormWindowState.Normal;
        Pump();
        var room = Screen.FromControl(restored).WorkingArea;
        ok &= Check($"and un-maximises to a window worth having ({restored.Size} of {room.Size})",
                    restored.Width > room.Width / 2 && restored.Height > room.Height / 2,
                    $"minimum is {restored.MinimumSize}");
        restored.Close();

        return ok;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);

    /// <summary>Leaves the maximised state and takes the new rectangle in one go, which is what snapping
    /// does to a maximised window and what an un-maximise followed by a move would not be.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);

    /// <summary>Letting go of a very large log costs the kernel two thirds of a second - it has to hand back
    /// every resident page of the mapping - and it happens on the thread that draws. So the window has to be
    /// down BEFORE that starts, or the reader sits looking at an app that will not close. WinForms disposes
    /// a top-level form while its window is still up, which is why closing hides it first.</summary>
    internal static bool RunClosingChecks()
    {
        Line("-- the window goes before the file is let go --");

        string log = Path.Combine(Path.GetTempPath(), "cascade_closing_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllLines(log, Enumerable.Range(1, 200).Select(i => $"line {i}"));

        bool ok;
        try
        {
            using var form = new MainForm(new AppSettings(), new MachineState(), new[] { log })
            {
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(900, 700),
            };
            form.NoSavePrompt = true;
            form.Show();
            Pump();

            var doc = form.DocForTesting;
            ok = Check("the file is open", doc.CompletedLineCount > 0, doc.CompletedLineCount.ToString());
            ok &= Check("and not let go while it is being read", !doc.IsDisposed);

            // Subscribed after the form's own handler, so it runs after it: this is the state the reader is
            // left in for however long the release takes.
            bool windowStillUp = true, alreadyLetGo = true;
            form.FormClosing += (_, _) => { windowStillUp = form.Visible; alreadyLetGo = doc.IsDisposed; };

            form.Close();
            Pump();

            ok &= Check("the window is down by the time closing finishes", !windowStillUp);
            ok &= Check("and the file has not been let go yet, which is the slow part", !alreadyLetGo);
            ok &= Check("but it is let go by the time the window is disposed", doc.IsDisposed);
        }
        finally
        {
            try { File.Delete(log); } catch { /* best effort */ }
        }

        return ok;
    }

    /// <summary>Dropping a log in from Explorer replaces the one on screen and keeps the filters - one filter
    /// set, several files to try it against. A drop target is registered per window and a child that has not
    /// asked for drops refuses them rather than passing them up, so which controls opt in is part of the
    /// behaviour and is checked here too.</summary>
    internal static bool RunFileDropChecks()
    {
        Line("-- dropping files on the window --");

        string dir = Path.Combine(Path.GetTempPath(), "cascade_st_drop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string logA = Path.Combine(dir, "a.log"), logB = Path.Combine(dir, "b.log");
        File.WriteAllLines(logA, Enumerable.Range(1, 40).Select(i => $"alpha line {i}"));
        File.WriteAllLines(logB, Enumerable.Range(1, 90).Select(i => $"bravo line {i}"));

        string filterFile = Path.Combine(dir, "two.cascade");
        var saved = new FilterCollection();
        saved.Roots.Add(new Filter { Match = { Text = "alpha" }, Enabled = true });
        saved.Roots.Add(new Filter { Match = { Text = "bravo" }, Enabled = true });
        CascadeFile.Save(filterFile, saved);

        bool ok;
        try
        {
            using var form = new MainForm(new AppSettings(), new MachineState(), new[] { logA })
            {
                Opacity = 0,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                Size = new Size(900, 700),
            };
            form.NoSavePrompt = true;
            form.Show();
            Pump();

            var doc = form.DocForTesting;
            var grid = form.GridForTesting;
            var tree = form.FilterTreeForTesting;
            ok = Check($"the first file is open ({Path.GetFileName(doc.FilePath)})", doc.FilePath == logA);
            ok &= Check("the window accepts drops", form.AllowDrop);
            ok &= Check("and so does the log view, which is what covers it", grid.AllowDrop);

            ok &= Check("a dragged file is offered as a copy",
                        EffectOfDragOver(grid, Files(logB)) == DragDropEffects.Copy);
            ok &= Check("a drag carrying no file is refused",
                        EffectOfDragOver(grid, new DataObject(DataFormats.Text, "not a file")) == DragDropEffects.None);
            ok &= Check("so is a folder", EffectOfDragOver(grid, Files(dir)) == DragDropEffects.None);
            ok &= Check("so is a path that is not there",
                        EffectOfDragOver(grid, Files(Path.Combine(dir, "gone.log"))) == DragDropEffects.None);

            // The point of the gesture: the file changes, the filters do not.
            Drop(grid, Files(filterFile));
            Pump();
            int filtersBefore = doc.Filters.Roots.Count;
            ok &= Check($"dropping a filter file loads it ({filtersBefore} filters)", filtersBefore == 2);

            Drop(grid, Files(logB));
            Pump();
            doc.WaitForIndex();
            Pump();
            ok &= Check($"dropping a log replaces the one on screen ({Path.GetFileName(doc.FilePath)})", doc.FilePath == logB);
            ok &= Check($"with the new file's lines ({doc.CompletedLineCount})", doc.CompletedLineCount == 90);
            ok &= Check($"and the filters left alone ({doc.Filters.Roots.Count})", doc.Filters.Roots.Count == 2);
            ok &= Check("which the list still shows", tree.VisibleFiltersForTesting.Count == 2);

            // Both at once, which is how a filter set and the file to try it on tend to arrive.
            doc.Filters.Roots.Clear();
            tree.Rebuild();
            Drop(grid, Files(logA, filterFile));
            Pump();
            doc.WaitForIndex();
            Pump();
            ok &= Check($"dropping a log and a filter file together does both " +
                        $"({Path.GetFileName(doc.FilePath)}, {doc.Filters.Roots.Count} filters)",
                        doc.FilePath == logA && doc.Filters.Roots.Count == 2);

            // The filter pane is a drop target of its own, for reordering filters, so it has to answer for
            // files itself rather than letting them fall through to the window.
            ok &= Check("the filter pane offers a copy for a file",
                        tree.DragEffectForTesting(DragArgs(Files(logB))) == DragDropEffects.Copy);
            ok &= Check("and refuses a drag it has no use for",
                        tree.DragEffectForTesting(DragArgs(new DataObject(DataFormats.Text, "no"))) == DragDropEffects.None);
            tree.DropOnTreeForTesting(DragArgs(Files(logB)));
            Pump();
            doc.WaitForIndex();
            Pump();
            ok &= Check($"and opens a file dropped on it ({Path.GetFileName(doc.FilePath)})", doc.FilePath == logB);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }

        return ok;
    }

    private static int Luma(Color c) => (c.R * 299 + c.G * 587 + c.B * 114) / 1000;

    /// <summary>Where Tab goes. Two areas, and a bar you have opened keeps it: tabbing out of a bar lands
    /// on whatever happens to be next in the window - the presets list, as it turned out - with no way
    /// back in, so the only way out of a bar is Escape.
    ///
    /// The "never leaves" checks here run through a seam that calls ProcessCmdKey with an empty message,
    /// so they cannot tell a real escape from a no-op. UiFeatureTests.Tab_has_two_stops_and_never_walks_
    /// out_of_an_open_bar posts real Tab keys and is what actually holds that line.</summary>
    internal static bool RunTabStopChecks()
    {
        Line("-- what Tab does --");

        using var form = new MainForm(new AppSettings(), new MachineState(), Array.Empty<string>())
        {
            NoSavePrompt = true,
            Opacity = 0,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Size = new Size(1000, 720),
        };
        form.Show();
        Pump();

        string Area() => form.FocusedAreaForTesting;

        form.GridForTesting.Focus();
        Pump();
        bool ok = Check("it starts on the log", Area() == "log", Area());

        var walk = new List<string>();
        for (int i = 0; i < 4; i++) { form.PressCmdKeyForTesting(Keys.Tab); Pump(); walk.Add(Area()); }
        string[] alternating = ["filter list", "log", "filter list", "log"];
        ok &= Check("Tab alternates between the log and the filter list",
                    walk.SequenceEqual(alternating), string.Join(" -> ", walk));

        var backwards = new List<string>();
        for (int i = 0; i < 2; i++) { form.PressCmdKeyForTesting(Keys.Shift | Keys.Tab); Pump(); backwards.Add(Area()); }
        string[] bothWays = ["filter list", "log"];
        ok &= Check("and Shift+Tab does the same, there being only the two",
                    backwards.SequenceEqual(bothWays), string.Join(" -> ", backwards));

        // The find bar has five stops of its own; Tab must visit them and come back, never leave.
        form.ClickMenuForTesting("Edit", "Find");
        Pump();
        ok &= Check("opening find puts the keyboard in the bar", Area() == "find bar", Area());

        var inBar = new List<string>();
        for (int i = 0; i < 8; i++) { form.PressCmdKeyForTesting(Keys.Tab); Pump(); inBar.Add(Area()); }
        ok &= Check("Tab never leaves the find bar", inBar.TrueForAll(a => a == "find bar"),
                    string.Join(" -> ", inBar.Distinct()));
        ok &= Check("and it really does move about inside it",
                    form.FindBarForTesting.FocusedForTesting is { Length: > 0 },
                    form.FindBarForTesting.FocusedForTesting ?? "(none)");
        form.CloseFindForTesting();
        Pump();

        // Same rule for the filter search bar, which has one stop - so Tab stays put rather than falling out.
        form.FilterTreeForTesting.ShowSearch();
        Pump();
        ok &= Check("opening the filter search puts the keyboard in its bar", Area() == "filter search", Area());
        var inSearch = new List<string>();
        for (int i = 0; i < 4; i++) { form.PressCmdKeyForTesting(Keys.Tab); Pump(); inSearch.Add(Area()); }
        ok &= Check("Tab never leaves the filter search bar either",
                    inSearch.TrueForAll(a => a == "filter search"), string.Join(" -> ", inSearch.Distinct()));

        // ...and Escape, which is the way out, hands it back to the list rather than to nowhere.
        form.PressCmdKeyForTesting(Keys.Escape);
        Pump();
        ok &= Check("Escape leaves the bar and lands on the filter list", Area() == "filter list", Area());

        form.Close();
        Pump();
        return ok;
    }
}

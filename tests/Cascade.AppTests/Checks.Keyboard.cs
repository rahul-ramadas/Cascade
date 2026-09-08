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

/// <summary>Part of <see cref="Checks"/>: menus and dialogs as keyboard surfaces: one Alt key each, no clashes, and nothing that moves when a message appears.</summary>
internal static partial class Checks
{

    /// <summary>Every option in a dialog should be reachable with Alt+letter, and no two may claim the same
    /// letter - a duplicate silently makes one of them unreachable, which is invisible on screen because
    /// Windows only underlines the letters while Alt is held.</summary>
    internal static bool RunDialogKeyboardChecks()
    {
        Line("-- dialog keyboard access --");

        static IEnumerable<Control> Walk(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (var d in Walk(c)) yield return d;
            }
        }

        // The same call WinForms makes for Alt+letter, so this exercises the real dispatch rather than
        // just looking for an ampersand in the caption.
        static char? MnemonicOf(string text) => Checks.MnemonicOf(text);

        bool ok = true;

        bool CheckDialog(string name, Form dlg, params string[] mustBeReachable)
        {
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = new Point(0, 0);
            dlg.Opacity = 0;
            dlg.Show();
            Pump();

            var claimed = new Dictionary<char, string>();
            var clashes = new List<string>();
            foreach (var c in Walk(dlg))
            {
                if (c is not (ButtonBase or Label) || MnemonicOf(c.Text) is not { } m) continue;
                if (claimed.TryGetValue(m, out string? already)) clashes.Add($"'{m}' on both \"{already}\" and \"{c.Text}\"");
                else claimed[m] = c.Text;
            }
            bool good = Check($"{name}: no two controls claim the same Alt key" +
                              (clashes.Count > 0 ? " [" + string.Join("; ", clashes) + "]" : $" ({claimed.Count} keys)"),
                              clashes.Count == 0);

            // Every tick box has to be operable from the keyboard, and pressing its key has to move it.
            foreach (var box in Walk(dlg).OfType<CheckBox>())
            {
                if (MnemonicOf(box.Text) is not { } m)
                {
                    good &= Check($"{name}: \"{box.Text}\" has an Alt key", false);
                    continue;
                }
                var before = box.CheckState;
                bool handled = AltKey(dlg, m);
                good &= Check($"{name}: Alt+{char.ToUpperInvariant(m)} works {box.Text}",
                              handled && box.CheckState != before);
            }

            foreach (string caption in mustBeReachable)
            {
                var hit = Walk(dlg).FirstOrDefault(c => c.Text == caption);
                good &= Check($"{name}: \"{caption.Replace("&", "")}\" can be reached with Alt",
                              hit is not null && MnemonicOf(caption) is not null);
            }

            // Everything is sized from the font and from DPI-scaled values, so at any scaling the frame has
            // to be at least as big as what it holds. A fixed size would clip here first. A dialog that can
            // be RESIZED is exempt: its content is a minimum it grows past, and a wrapping row measured
            // against no width at all answers with everything stacked one item to a line.
            var content = dlg.Controls.Count > 0 ? dlg.Controls[0].PreferredSize : Size.Empty;
            good &= Check($"{name}: nothing is clipped at this DPI " +
                          $"(frame {dlg.ClientSize.Width}x{dlg.ClientSize.Height}, content {content.Width}x{content.Height})",
                          dlg.FormBorderStyle == FormBorderStyle.Sizable ||
                          (dlg.ClientSize.Width >= content.Width && dlg.ClientSize.Height >= content.Height));

            dlg.Close();
            dlg.Dispose();
            Pump();
            return good;
        }

        var filter = new Filter { Match = { Text = "sample text" } };
        ok &= CheckDialog("filter", new FilterEditDialog(filter, isNew: true),
                          "&Matches text", "Mar&ked by marker", "&Text:", "&Description:",
                          "&At the top of the list", "Abo&ve the selected filter",
                          "As a c&hild of the selected filter");

        var group = new List<Filter> { new() { Match = { Text = "one" } }, new() { Match = { Text = "two" } } };
        ok &= CheckDialog("appearance", new AppearanceDialog(group, group,
                              new ResolvedStyle(new RgbColor(0, 0, 0), new RgbColor(255, 255, 255), false, false)),
                          "Text col&or", "&Background", "Bo&ld", "&Italic");

        var fields = new ColumnSpec { Enabled = true, Template = "{[*]}{[*]} {*}" };
        fields.Reset();
        ok &= CheckDialog("fields", new ColumnsDialog(fields, ["[09:31][INFO] hello there", "[09:32][WARN] and again"]),
                          "&Template", "&Detect", "&Layout", "&Columns", "&Inline", "&Fields", "Move &up", "Move d&own");

        // Those keys are pressed while writing the pattern, so they must not take the keyboard out of the
        // box - the same rule the find bar's two options follow.
        using (var opts = new FilterEditDialog(new Filter { Match = { Text = "sample text" } }, isNew: true))
        {
            opts.StartPosition = FormStartPosition.Manual;
            opts.Location = new Point(0, 0);
            opts.Opacity = 0;
            opts.Show();
            Pump();
            opts.FocusTextForTesting(3, 4);
            Pump();
            ok &= Check("the pattern box has the keyboard to start with", opts.TextHasFocusForTesting);
            ok &= Check("and a caret and selection worth keeping", opts.TextSelectionForTesting == (3, 4),
                        opts.TextSelectionForTesting.ToString());

            foreach (char key in "RCEOBLI")
            {
                AltKey(opts, key);
                Pump();
            }
            ok &= Check("ticking the options leaves the keyboard in the box", opts.TextHasFocusForTesting);
            ok &= Check("and the caret and selection where they were",
                        opts.TextSelectionForTesting == (3, 4), opts.TextSelectionForTesting.ToString());
            ok &= Check("and what is inherited is still explained",
                        opts.NoteForTesting.Contains("parent filter", StringComparison.Ordinal),
                        opts.NoteForTesting);

            // A strip that wraps answers for a narrower width than it is then given, so its row reserves
            // lines that are never drawn - which is where the band of empty space down this dialog came
            // from. Every strip should be exactly as tall as the tallest thing in it.
            int spare = AllControls(opts).OfType<FlowLayoutPanel>()
                .Select(s => s.Height - s.Controls.Cast<Control>()
                                         .Select(c => c.Height + c.Margin.Vertical).DefaultIfEmpty(0).Max())
                .DefaultIfEmpty(0).Max();
            ok &= Check("no row of the filter dialog reserves space it never draws", spare <= 0, $"{spare}px over");

            // A tick and the swatch it owns must read as one thing, and the next pair as another: with the
            // gaps the other way round the eye binds a swatch to whatever follows it.
            var appearance = AllControls(opts).OfType<FlowLayoutPanel>()
                             .OrderByDescending(s => s.Controls.Count).First();
            var strip = appearance.Controls.Cast<Control>().OrderBy(c => c.Left).ToList();
            int[] gaps = [.. strip.Zip(strip.Skip(1), (a, b) => b.Left - a.Right)];
            ok &= Check("a swatch sits closer to its own tick than to what comes next",
                        gaps.Length >= 4 && gaps[0] < gaps[1] && gaps[2] < gaps[3],
                        string.Join(", ", gaps));

            // The swatch buttons are taller than the captions beside them, so the row's alignment is a
            // question of centres, not of tops.
            int mid = appearance.Height / 2;
            int worst = strip.Max(c => Math.Abs(c.Top + c.Height / 2 - mid));
            ok &= Check("and every caption is centred against them", worst <= 1,
                        $"{worst}px off centre in a {appearance.Height}px row");

            // The pattern box holds a whole log line copied in from the text, so it gets what is left after
            // the labels - not a share of it.
            ok &= Check("the pattern box runs the width of the dialog",
                        opts.PatternWidthForTesting > opts.ClientSize.Width * 3 / 4,
                        $"box {opts.PatternWidthForTesting} of {opts.ClientSize.Width}");

            opts.Close();
            Pump();
        }

        // The find bar claims exactly two Alt keys, for its two options. It lives in a window that owns a
        // menu bar, so the letters it takes must be ones no top-level menu wants - otherwise Alt+R would
        // cycle between the two instead of ticking the box.
        var bar = new FindBar((_, _) => { });
        var claimed = AllControls(bar).Select(c => c.Text).Select(MnemonicOf).OfType<char>()
                                      .Select(char.ToUpperInvariant).OrderBy(c => c).ToList();
        ok &= Check("the find bar claims Alt+R and Alt+C, and nothing else [" + string.Join(", ", claimed) + "]",
                    string.Concat(claimed) == "CR");

        using (var probe = new MainForm(new AppSettings(), new MachineState(), []))
        {
            var menuKeys = (probe.MainMenuStrip?.Items.OfType<ToolStripMenuItem>() ?? [])
                .Select(i => MnemonicOf(i.Text ?? "")).OfType<char>()
                .Select(char.ToUpperInvariant).ToList();
            ok &= Check("and none of them is a menu's [" + string.Join(", ", menuKeys) + "]",
                        !claimed.Intersect(menuKeys).Any());

            // ...and they really work, through the same call WinForms makes for Alt+letter. The bar has to
            // be up for it: a hidden control takes no mnemonic, which is exactly the wanted behaviour.
            probe.StartPosition = FormStartPosition.Manual;
            probe.Location = new Point(0, 0);
            probe.Opacity = 0;
            probe.NoSavePrompt = true;
            probe.Show();
            Pump();
            probe.ClickMenuForTesting("Edit", "Find");
            Pump();
            var live = probe.FindBarForTesting;
            bool wasRegex = live.RegexIsOnForTesting, wasCase = live.CaseIsOnForTesting;
            ok &= Check("Alt+R is taken by the bar", AltKey(probe, 'R'));
            ok &= Check("and ticks the regex box", live.RegexIsOnForTesting != wasRegex);
            ok &= Check("Alt+C is taken by the bar", AltKey(probe, 'C'));
            ok &= Check("and ticks the case box", live.CaseIsOnForTesting != wasCase);

            // The options are reached mid-term, so ticking one must leave the box exactly as it was - it
            // still has the keyboard, and the caret and selection have not moved. The stock check box
            // selects itself when its Alt key is pressed, which loses all three.
            live.FocusInput();
            live.SetTermForTesting("declined", 3, 2);
            Pump();
            var place = live.SelectionForTesting();
            ok &= Check($"the term box has the keyboard to start with (it is on {live.FocusedForTesting})",
                        live.TermBoxHasFocusForTesting);
            ok &= Check("and a caret and selection worth keeping", place == (3, 2), place.ToString());

            AltKey(probe, 'R');
            AltKey(probe, 'C');
            Pump();
            ok &= Check($"ticking the options leaves the keyboard in the box (it is on {live.FocusedForTesting})",
                        live.TermBoxHasFocusForTesting);
            ok &= Check("and the term untouched", live.TermForTesting() == "declined", live.TermForTesting());
            ok &= Check("and the caret and selection where they were",
                        live.SelectionForTesting() == place, $"{live.SelectionForTesting()} was {place}");
        }
        bar.Dispose();

        // A message appearing must not shove the rest of the dialog sideways. A TableLayoutPanel hands out
        // its cells to the VISIBLE controls in order, so a control that appears can take a cell meant for
        // something else and drag a whole column along with it.
        bool NothingShifts(string name, Form dlg, Action show)
        {
            dlg.StartPosition = FormStartPosition.Manual;
            dlg.Location = new Point(0, 0);
            dlg.Opacity = 0;
            dlg.Show();
            Pump();

            List<(string Label, Rectangle Bounds, bool Shown)> Snapshot()
            {
                var list = new List<(string, Rectangle, bool)>();
                int i = 0;
                foreach (var c in Walk(dlg))
                {
                    string text = c.Text.Replace("&", "");
                    list.Add(($"{c.GetType().Name}#{i++}{(text.Length > 0 ? $" \"{text}\"" : "")}", c.Bounds, c.Visible));
                }
                return list;
            }

            var before = Snapshot();
            show();
            Pump();
            var after = Snapshot();

            var moved = new List<string>();
            foreach (var (label, bounds, shown) in before)
            {
                if (!shown) continue;   // something appearing has to take up its place; the rest must not move
                var now = after.FirstOrDefault(a => a.Label == label);
                if (now.Label is null || now.Bounds.Location == bounds.Location) continue;
                moved.Add($"{label} {bounds.X},{bounds.Y}->{now.Bounds.X},{now.Bounds.Y}");
            }
            bool good = Check($"{name}: showing a message moves nothing that was already on screen" +
                              (moved.Count > 0 ? " [" + string.Join("; ", moved) + "]" : ""),
                              moved.Count == 0);
            dlg.Close();
            dlg.Dispose();
            Pump();
            return good;
        }

        // The find bar's count arrives beside a term box and two checkboxes and must not shove any of them
        // along. It is hosted in a form here only because that is what the check drives.
        var findHost = new HiddenForm { ClientSize = new Size(900, 60) };
        var findBar = new FindBar((_, _) => { }) { Visible = true };
        findHost.Controls.Add(findBar);
        ok &= NothingShifts("find bar", findHost,
            () => findBar.SetMessage("Match 12 of 252 lines, 891 hits \u00b7 hidden: 96 lines, 313 hits"));

        // The filter dialog's regex error line is the same shape of thing.
        var broken = new Filter { Match = { Text = "fine", Regex = true } };
        var editDlg = new FilterEditDialog(broken, isNew: true);
        ok &= NothingShifts("filter", editDlg, () => editDlg.SetTextForTesting("((unclosed"));
        return ok;
    }

    /// <summary>Walking matches with the Enter key held down changes the count about thirty times a second.
    /// Only the count itself may be redrawn for that: repainting the row it sits in would erase the term,
    /// the options and the buttons and put them straight back, which is what a flicker is.</summary>
    /// <summary>The hint under the fields is rewritten on every keystroke of a half-written regex, because
    /// .NET puts the pattern into its own complaint. It must redraw itself and leave the dialog alone.</summary>
    internal static bool RunFilterDialogRepaintChecks()
    {
        Line("-- the regex complaint changing does not disturb the filter dialog --");

        using var dlg = new FilterEditDialog(new Filter { Match = { Text = "" } }, isNew: true)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Opacity = 0,
        };
        dlg.Show();
        Pump();

        var dialogBody = dlg.Controls[0];
        int bodyPaints = 0;
        dialogBody.Paint += (_, _) => bodyPaints++;

        // The same call WinForms makes for Alt+R, so the option is ticked the way a user ticks it.
        typeof(Control).GetMethod("ProcessMnemonic", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dlg, new object[] { 'R' });
        Pump();

        const int steps = 12;

        // A control run first: pump exactly as often with the pattern untouched, so what follows measures
        // the complaint changing rather than whatever the message loop does anyway.
        bodyPaints = 0;
        for (int i = 0; i < steps; i++) Pump();
        int idlePaints = bodyPaints;

        bodyPaints = 0;
        int notesBefore = dlg.NotePaintsForTesting;
        var wordings = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < steps; i++)
        {
            dlg.SetTextForTesting("abc[" + new string('x', i));   // the set never closes, so it stays broken
            Pump();
            wordings.Add(dlg.NoteForTesting);
        }
        int notePaints = dlg.NotePaintsForTesting - notesBefore;

        bool ok = Check("a half-written pattern is reported as broken",
                        dlg.NoteForTesting.StartsWith("Invalid regex", StringComparison.Ordinal),
                        dlg.NoteForTesting);
        ok &= Check($"and the complaint really does change with every keystroke ({wordings.Count} of {steps})",
                    wordings.Count == steps);
        ok &= Check($"the complaint itself redraws as it changes ({notePaints} times over {steps})",
                    notePaints > 0);
        ok &= Check($"but the dialog around it is left alone (body repainted {bodyPaints} times while the " +
                    $"complaint changed, {idlePaints} while it did not)", bodyPaints <= idlePaints);
        ok &= Check("and the complaint redraws in one go", dlg.NoteRedrawsInOneGoForTesting);

        dlg.Close();
        Pump();
        return ok;
    }

    /// <summary>The Encoding menu. It is the only way to read a file whose bytes say nothing about how they
    /// were written - a code page, or UTF-16 with no mark - so what matters is that choosing an entry really
    /// re-reads the file, that the menu says which one is in effect, and that the choice survives a reload.
    /// </summary>
    internal static bool RunEncodingMenuChecks()    {
        Line("-- the encoding menu --");

        string dir = Path.Combine(Path.GetTempPath(), "cascade_enc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // "café" is one byte in Windows-1252 and two in UTF-8, so the same bytes read one way or the other
        // are visibly different text - which is what makes every check below an observation, not a guess.
        string cp1252 = Path.Combine(dir, "cp1252.log");
        string utf16 = Path.Combine(dir, "utf16le.log");
        File.WriteAllBytes(cp1252, Encoding.GetEncoding(1252).GetBytes("INFO café ready\nWARN naïve façade\n"));
        var u16 = new UnicodeEncoding(false, true);
        File.WriteAllBytes(utf16, [.. u16.GetPreamble(), .. u16.GetBytes("INFO café ready\nWARN naïve façade\n")]);

        MainForm? form = null;
        try
        {
            string[] args = [cp1252];
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
            var doc = form.DocForTesting;
            Settle(doc);

            string menu = form.EncodingMenuForTesting();
            Line("   " + menu.Replace("\n", " | ", StringComparison.Ordinal));

            bool ok = Check("exactly one encoding is ticked", Ticked(menu).Count == 1, menu);
            ok &= Check("with nothing chosen, the ticked one is Auto-detect",
                        Ticked(menu).Contains("Auto-detect"), menu);
            ok &= Check("and Auto-detect says what it worked out",
                        menu.Contains("Auto-detect [UTF-8]", StringComparison.Ordinal), menu);
            // Guessed wrong, unavoidably: nothing in the bytes says which code page they are.
            ok &= Check("a code-page file guessed as UTF-8 comes out damaged",
                        doc.GetLineText(0).Contains('\uFFFD', StringComparison.Ordinal), doc.GetLineText(0));

            ok &= Check("choosing Windows-1252 re-reads the file",
                        form.ClickMenuForTesting("View", "Encoding", "Windows-1252")
                        && Settle(doc) && doc.GetLineText(0) == "INFO café ready", doc.GetLineText(0));
            menu = form.EncodingMenuForTesting();
            ok &= Check("and the tick moves to it", Ticked(menu).SequenceEqual(["Windows-1252"]), menu);
            ok &= Check("Auto-detect stops claiming to be in effect",
                        !menu.Contains("Auto-detect [", StringComparison.Ordinal), menu);

            ok &= Check("reloading keeps the chosen encoding",
                        form.ClickMenuForTesting("File", "Reload")
                        && Settle(doc) && doc.GetLineText(0) == "INFO café ready", doc.GetLineText(0));
            ok &= Check("and so does the tick",
                        Ticked(form.EncodingMenuForTesting()).SequenceEqual(["Windows-1252"]));

            ok &= Check("going back to Auto-detect guesses again",
                        form.ClickMenuForTesting("View", "Encoding", "Auto-detect")
                        && Settle(doc) && doc.GetLineText(0).Contains('\uFFFD', StringComparison.Ordinal),
                        doc.GetLineText(0));

            // A mark leaves nothing to guess at, and the menu has to say which encoding that turned out to be.
            form.OpenForTesting(utf16);
            Settle(doc);
            menu = form.EncodingMenuForTesting();
            ok &= Check("a marked file is detected and read", doc.GetLineText(0) == "INFO café ready", doc.GetLineText(0));
            ok &= Check("and Auto-detect names the encoding it found",
                        menu.Contains("Auto-detect [UTF-16 LE]", StringComparison.Ordinal), menu);
            ok &= Check("opening another file goes back to detecting", Ticked(menu).Contains("Auto-detect"), menu);

            // The mark says UTF-16, so this is the case where deferring to it left the menu doing nothing.
            ok &= Check("and a choice still overrules the mark",
                        form.ClickMenuForTesting("View", "Encoding", "Windows-1252")
                        && Settle(doc) && doc.GetLineText(0) != "INFO café ready", doc.GetLineText(0));
            return ok;

            static List<string> Ticked(string menu) =>
                [.. menu.Split('\n').Where(l => l.EndsWith(" *", StringComparison.Ordinal))
                        .Select(l => l[..^2] is var name && name.IndexOf(" [", StringComparison.Ordinal) is var b && b > 0
                                     ? name[..b] : name)];
        }
        finally
        {
            try { form?.Close(); form?.Dispose(); } catch { /* ignore */ }
            Pump();
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    /// <summary>The menu items a user reaches for, clicked through a real window on a real file. Almost
    /// everything else here builds a control directly; this is the only thing that exercises the wiring
    /// between the menu, the settings and the three panes - which is where a command that quietly stopped
    /// doing anything would hide.</summary>
    internal static bool RunMenuActionChecks()
    {
        Line("-- the menus --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_menus_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < 400; i++)
            sb.Append(i % 5 == 0 ? $"ERROR line {i}\n" : $"plain line {i}\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        string filters = Path.Combine(Path.GetTempPath(), "cascade_st_menus_" + Guid.NewGuid().ToString("N") + ".cascade");
        File.WriteAllText(filters, """
            { "filters": [ { "id": "f1", "enabled": true, "matchType": "Text", "text": "ERROR",
                             "style": { "background": "#FFD0D0" } } ] }
            """, new UTF8Encoding(false));

        var settings = new AppSettings();
        var state = new MachineState();
        MainForm? form = null;
        try
        {
            form = new MainForm(settings, state, new[] { path, "/Filters:" + filters })
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
            for (int i = 0; i < 60 && doc.CompletedLineCount < 400; i++) { Thread.Sleep(20); Pump(); }

            bool ok = Check("the file is open and indexed", doc.CompletedLineCount == 400,
                            $"{doc.CompletedLineCount} lines");
            ok &= Check("and its filters came with it", doc.Filters.EnumerateDepthFirst().Count() == 1,
                        $"{doc.Filters.EnumerateDepthFirst().Count()} filters");
            for (int i = 0; i < 80 && doc.IsBusy; i++) { Thread.Sleep(20); Pump(); }
            ok &= Check("which match the lines they should", doc.MatchedLineCount == 80,
                        $"{doc.MatchedLineCount} of 400 matched");

            var grid = form.GridForTesting;

            // View: each of these has to change what is on screen, not merely tick a box.
            ok &= Check("View > Show Only Filtered Lines hides the rest",
                        form.ClickMenuForTesting("View", "Show Only Filtered Lines") && doc.FilteredMode &&
                        doc.RowCount == 80, $"filtered {doc.FilteredMode}, {doc.RowCount} rows");
            ok &= Check("and again puts them back",
                        form.ClickMenuForTesting("View", "Show Only Filtered Lines") && !doc.FilteredMode &&
                        doc.RowCount == 400, $"filtered {doc.FilteredMode}, {doc.RowCount} rows");

            int gutter = grid.GutterWidthForTesting;
            ok &= Check("View > Show Line Numbers takes the numbers away",
                        form.ClickMenuForTesting("View", "Show Line Numbers") &&
                        !settings.ShowLineNumbers && grid.GutterWidthForTesting < gutter,
                        $"gutter {gutter} -> {grid.GutterWidthForTesting}");
            ok &= Check("and brings them back",
                        form.ClickMenuForTesting("View", "Show Line Numbers") &&
                        settings.ShowLineNumbers && grid.GutterWidthForTesting == gutter,
                        $"gutter is {grid.GutterWidthForTesting}, was {gutter}");

            // And the key does it too. Preferences no longer carries a second copy of this setting, so the
            // View menu is the only place it is offered - which makes the key that saves opening the menu
            // worth pressing here rather than taking on trust.
            grid.Focus();
            Pump();
            form.PressCmdKeyForTesting(Keys.Control | Keys.L);
            Pump();
            ok &= Check("Ctrl+L takes the numbers away too",
                        !settings.ShowLineNumbers && grid.GutterWidthForTesting < gutter,
                        $"gutter {gutter} -> {grid.GutterWidthForTesting}");
            form.PressCmdKeyForTesting(Keys.Control | Keys.L);
            Pump();
            ok &= Check("and again brings them back",
                        settings.ShowLineNumbers && grid.GutterWidthForTesting == gutter,
                        $"gutter is {grid.GutterWidthForTesting}, was {gutter}");

            ok &= Check("View > Show Match Map takes the map away",
                        form.ClickMenuForTesting("View", "Show Match Map") &&
                        !settings.ShowMatchMap && grid.MapWidthForTesting == 0,
                        $"map {grid.MapWidthForTesting}px");
            ok &= Check("and the scrollbar stays behind", grid.VerticalScrollBarVisibleForTesting);
            ok &= Check("and it comes back",
                        form.ClickMenuForTesting("View", "Show Match Map") &&
                        settings.ShowMatchMap && grid.MapWidthForTesting > 0,
                        $"map {grid.MapWidthForTesting}px");

            ok &= Check("View > Word Wrap breaks the long lines up",
                        form.ClickMenuForTesting("View", "Word Wrap") && settings.WordWrap && grid.Wrapping);
            ok &= Check("and takes the sideways scrollbar away", !grid.HScrollBarForTesting.Visible);
            ok &= Check("and the log still holds a whole number of lines",
                        (form.SplitForTesting.SplitterDistance - grid.ChromeHeight) % grid.RowPitch == 0,
                        $"{form.SplitForTesting.SplitterDistance - grid.ChromeHeight}px of text, " +
                        $"a line is {grid.RowPitch}px");
            ok &= Check("and turning it off puts the scrollbar back",
                        form.ClickMenuForTesting("View", "Word Wrap") && !settings.WordWrap &&
                        grid.HScrollBarForTesting.Visible);

            int zoom = settings.ZoomPercent;
            ok &= Check("View > Zoom In makes the text bigger",
                        form.ClickMenuForTesting("View", "Zoom In") && settings.ZoomPercent > zoom,
                        $"{zoom}% -> {settings.ZoomPercent}%");
            ok &= Check("View > Reset Zoom puts it back",
                        form.ClickMenuForTesting("View", "Reset Zoom") && settings.ZoomPercent == 100,
                        $"{settings.ZoomPercent}%");

            // Docking: every side, and the log still measures in whole lines wherever the list is.
            foreach (string where in new[] { "Dock Left", "Dock Right", "Dock Top", "Dock Bottom" })
            {
                ok &= Check($"View > Filter List Location > {where}",
                            form.ClickMenuForTesting("View", "Filter List Location", where));
                Pump();
                bool sideways = where is "Dock Left" or "Dock Right";
                ok &= Check($"  turns the divider the right way for {where}",
                            form.SplitForTesting.Orientation ==
                            (sideways ? Orientation.Vertical : Orientation.Horizontal),
                            form.SplitForTesting.Orientation.ToString());
                if (!sideways)
                    ok &= Check($"  and leaves the log on a whole line with the list {where[5..]}",
                                (grid.Height - grid.ChromeHeight) % grid.RowPitch == 0,
                                $"{grid.Height - grid.ChromeHeight}px of text, a line is {grid.RowPitch}px");
                ok &= Check($"  and the log is still on screen with the list {where[5..]}",
                            grid.Width > 50 && grid.Height > 50, $"{grid.Width}x{grid.Height}");
                ok &= Check($"  and remembers that the list is {where[5..]}",
                            settings.FilterListDock == Enum.Parse<FilterDock>(where[5..]),
                            settings.FilterListDock.ToString());
            }
            form.ClickMenuForTesting("View", "Filter List Location", "Dock Bottom");
            Pump();

            // Docking to the edge it is already on has to leave the divider where it is. The same code runs
            // when Preferences is closed and when a settings file is imported, and either of those resetting
            // a divider the user had dragged would be a change nobody asked for.
            form.SplitForTesting.SplitterDistance = form.SplitterDistanceForTesting - 60;
            Pump();
            int dragged = form.SplitterDistanceForTesting;
            ok &= Check("docking where it already is leaves a dragged divider alone",
                        form.ClickMenuForTesting("View", "Filter List Location", "Dock Bottom") &&
                        form.SplitterDistanceForTesting == dragged,
                        $"{dragged} -> {form.SplitterDistanceForTesting}");

            ok &= Check("View > Filter List Location > Show/Hide Filter List takes the list away",
                        form.ClickMenuForTesting("View", "Filter List Location", "Show/Hide Filter List") &&
                        !form.FilterListVisibleForTesting && !settings.ShowFilterList,
                        $"on screen {form.FilterListVisibleForTesting}, remembered {settings.ShowFilterList}");
            ok &= Check("and again brings it back",
                        form.ClickMenuForTesting("View", "Filter List Location", "Show/Hide Filter List") &&
                        form.FilterListVisibleForTesting && settings.ShowFilterList,
                        $"on screen {form.FilterListVisibleForTesting}, remembered {settings.ShowFilterList}");

            // Dragging the divider is how the list is given its size, so that is what has to be recorded.
            double wasHeight = settings.FilterListHeightFraction;
            form.SplitForTesting.SplitterDistance = form.SplitterDistanceForTesting - 80;
            Pump();
            ok &= Check("dragging the divider records the list's new share of the window",
                        settings.FilterListHeightFraction > wasHeight + 0.02,
                        $"{wasHeight:F3} -> {settings.FilterListHeightFraction:F3}");
            ok &= Check("and it is the share the divider actually settled on",
                        Math.Abs(settings.FilterListHeightFraction -
                                 Share(form.SplitForTesting, form.FilterListIsFirstPanelForTesting)) < 0.01,
                        $"recorded {settings.FilterListHeightFraction:F3}, " +
                        $"on screen {Share(form.SplitForTesting, form.FilterListIsFirstPanelForTesting):F3}");

            // The two edges keep separate sizes: a strip along the bottom and a column down the side are not
            // the same shape, so a drag of one must not resize the other.
            double bottomShare = settings.FilterListHeightFraction;
            form.ClickMenuForTesting("View", "Filter List Location", "Dock Left");
            Pump();
            form.SplitForTesting.SplitterDistance = form.SplitterDistanceForTesting + 90;
            Pump();
            ok &= Check("a drag with the list down the side records the width, not the height",
                        settings.FilterListHeightFraction == bottomShare &&
                        settings.FilterListWidthFraction > 0.3,
                        $"height {settings.FilterListHeightFraction:F3} (was {bottomShare:F3}), " +
                        $"width {settings.FilterListWidthFraction:F3}");
            ok &= Check("and docking back to the bottom restores the height it had there",
                        form.ClickMenuForTesting("View", "Filter List Location", "Dock Bottom") &&
                        Math.Abs(Share(form.SplitForTesting, form.FilterListIsFirstPanelForTesting) - bottomShare) < 0.03,
                        $"wanted {bottomShare:F3}, got {Share(form.SplitForTesting, form.FilterListIsFirstPanelForTesting):F3}");

            // Filters: the two that touch every filter at once.
            ok &= Check("Filters > Disable All switches them all off",
                        form.ClickMenuForTesting("Filters", "Disable All") &&
                        doc.Filters.EnumerateDepthFirst().All(f => !f.Enabled));
            for (int i = 0; i < 80 && doc.IsBusy; i++) { Thread.Sleep(20); Pump(); }
            // With nothing to filter by, the view falls back to the whole file rather than to nothing -
            // which is the difference between a log viewer and a blank window.
            ok &= Check("and the whole file shows rather than none of it", doc.MatchedLineCount == 400,
                        doc.MatchedLineCount.ToString());
            ok &= Check("even with only matching lines on show",
                        form.ClickMenuForTesting("View", "Show Only Filtered Lines") && doc.RowCount == 400,
                        $"{doc.RowCount} rows");
            form.ClickMenuForTesting("View", "Show Only Filtered Lines");
            ok &= Check("Filters > Enable All switches them back on",
                        form.ClickMenuForTesting("Filters", "Enable All") &&
                        doc.Filters.EnumerateDepthFirst().All(f => f.Enabled));
            for (int i = 0; i < 80 && doc.IsBusy; i++) { Thread.Sleep(20); Pump(); }
            ok &= Check("and the matches are back", doc.MatchedLineCount == 80,
                        doc.MatchedLineCount.ToString());

            // Edit: copying takes what is selected, and the line numbers only when asked. The clipboard is
            // shared with everything else on the machine, so when it cannot be read at all this says so
            // rather than reporting a failure it did not actually observe.
            grid.SelectRowForAccessibility(5);
            Pump();
            form.ClickMenuForTesting("Edit", "Copy");
            string plain = SafeClipboardText();
            form.ClickMenuForTesting("Edit", "Copy with Line Numbers");
            string numbered = SafeClipboardText();
            if (plain.Length == 0 || numbered.Length == 0)
                Line("   (the clipboard would not open; skipped the copy checks)");
            else
            {
                ok &= Check("Edit > Copy takes the line", plain.Trim() == "ERROR line 5", $"'{plain.Trim()}'");
                ok &= Check("Edit > Copy with Line Numbers puts the number in front of it",
                            numbered.Trim() == "6\tERROR line 5", $"'{numbered.Trim()}'");
            }

            // File > Close Filters empties the list and stops it being loaded again next time.
            ok &= Check("File > Close Filters empties the list",
                        form.ClickMenuForTesting("File", "Close Filters") &&
                        !doc.Filters.EnumerateDepthFirst().Any(),
                        $"{doc.Filters.EnumerateDepthFirst().Count()} filters left");
            ok &= Check("and forgets it for next time", state.LastFilterFile is null,
                        state.LastFilterFile ?? "(null)");
            ok &= Check("and the whole file is on show again", doc.RowCount == 400, doc.RowCount.ToString());

            form.Close();
            form = null;
            return ok;
        }
        finally
        {
            form?.Dispose();
            try { File.Delete(path); File.Delete(filters); } catch { }
        }
    }

    /// <summary>The About box is two columns of text that have to read as one line each. A label centres its
    /// caption in the cell and a text box draws its own at the top of its box, so the two drifted apart -
    /// which is only visible in the pixels, never in the layout. It also has to be able to say something
    /// long, because the reason an update check failed is exactly what a bug report needs.</summary>
    internal static bool RunAboutBoxChecks()
    {
        Line("-- the about box --");

        const string LongProblem =
            "Last check failed: GitHub answered 403 Forbidden for https://api.github.com/repos/owner/name/" +
            "releases/latest. The saved credential was refused and the anonymous retry ran out of requests.";

        AboutDialog.Row[] rows =
        [
            new("Version", "2026.8.60"),
            new("Location", @"C:\Users\someone\AppData\Local\Programs\Cascade\Cascade.exe"),
            new("Updates", "Up to date"),
            new("Last error", LongProblem, IsProblem: true)
        ];

        using var about = new AboutDialog(rows)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Opacity = 0
        };
        about.Show();
        Pump();

        // Where a control's caption really starts, in the coordinates of the row it shares with the other
        // column. Each control is rendered on its own rather than reading them off a picture of the whole
        // dialog: DrawToBitmap on a Form includes the window frame, so client coordinates would not line up
        // with the picture, and the error only shows as a difference between two rows.
        // What counts as ink is measured against that control's OWN darkest pixel rather than a fixed
        // threshold - a red message and a grey label cross a fixed one at different points in their
        // antialiasing, which is worth several pixels and would read as a misalignment that is not there.
        static int InkTop(Control c)
        {
            using var bmp = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height));
            c.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            var paper = SystemColors.Control;
            int Away(Color p) => Math.Abs(p.R - paper.R) + Math.Abs(p.G - paper.G) + Math.Abs(p.B - paper.B);

            int darkest = 0;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                    darkest = Math.Max(darkest, Away(bmp.GetPixel(x, y)));
            if (darkest < 100) return -1;

            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                    if (Away(bmp.GetPixel(x, y)) >= darkest / 2) return c.Top + y;
            return -1;
        }

        bool ok = true;
        var pairs = about.RowsForTesting.ToList();
        ok &= Check($"every row of the box is there ({pairs.Count})", pairs.Count == rows.Length);

        var offsets = new List<int>();
        foreach (var (label, value) in pairs)
        {
            int a = InkTop(label), b = InkTop(value);
            offsets.Add(a < 0 || b < 0 ? 99 : b - a);
            Line($"   ({label.Text}: label at {label.Top} ink at {a}, " +
                 $"value at {value.Top} ({value.Height}px tall) ink at {b})");
        }
        ok &= Check("the label and the value of a row are written on the same line",
                    offsets.Count > 0 && offsets.TrueForAll(d => Math.Abs(d) <= 1),
                    "offsets " + string.Join(", ", offsets));

        // A long message has to be readable, not clipped at the edge of the dialog. Its box is the one that
        // stands more than one line tall, and the row it is in has to be tall enough to hold it.
        var problem = pairs[^1].Value;
        int oneLine = pairs[0].Value.Height;
        ok &= Check($"a long message wraps rather than being cut off ({problem.Height}px against {oneLine}px)",
                    problem.Height >= oneLine * 2);
        ok &= Check("and all of it is really there", problem.Text == LongProblem);
        ok &= Check("and it is coloured as the problem it is", problem.ForeColor == Color.Firebrick,
                    problem.ForeColor.ToString());
        ok &= Check("while an ordinary value is not", pairs[0].Value.ForeColor != Color.Firebrick);

        // Selectable, so it can be pasted into a bug report - but out of the tab order, so the box does not
        // open with a value highlighted.
        ok &= Check("values can be selected and copied",
                    pairs.TrueForAll(p => p.Value is TextBox { ReadOnly: true, TabStop: false }));

        int cap = about.LogicalToDeviceUnits(620);
        ok &= Check($"and one long line does not stretch the box off the screen ({problem.Width}px)",
                    problem.Width <= cap && about.Width <= Screen.PrimaryScreen!.WorkingArea.Width,
                    $"value column capped at {cap}px, dialog {about.Width}px");

        // What the Updates rows say in each state updating can be in. A check that has not finished must not
        // be reported as "up to date" - that is a guess dressed up as a fact - and a failure has to hand over
        // the whole reason, which is the one thing a bug report needs and the one thing it used to swallow.
        static string Said(List<AboutDialog.Row> rows)
            => string.Join(" | ", rows.Select(r => $"{r.Label}: {r.Value}"));

        var running = AboutDialog.UpdateRows(finished: false, pending: null, error: null, note: null);
        ok &= Check("a check still running says so", running.Count == 1 && running[0].Value.StartsWith("Checking", StringComparison.Ordinal),
                    Said(running));

        var clean = AboutDialog.UpdateRows(finished: true, pending: null, error: null, note: null);
        ok &= Check("a check that found nothing newer says it is up to date",
                    clean.Count == 1 && clean[0].Value.Contains("Up to date", StringComparison.Ordinal), Said(clean));

        var waiting = AboutDialog.UpdateRows(finished: true, pending: new Version(2026, 8, 61), null, null);
        ok &= Check("an installed update says when it takes effect",
                    waiting.Count == 1 && waiting[0].Value.Contains("2026.8.61", StringComparison.Ordinal)
                        && waiting[0].Value.Contains("next time", StringComparison.Ordinal), Said(waiting));

        var failed = AboutDialog.UpdateRows(finished: true, pending: null, error: LongProblem, note: "carried on anonymously");
        ok &= Check("a failed check gives the reason a row of its own, marked as a problem",
                    failed.Count == 3 && failed[1].Value == LongProblem && failed[1].IsProblem, Said(failed));
        ok &= Check("and does not claim to be up to date at the same time",
                    !failed[0].Value.Contains("Up to date", StringComparison.Ordinal), failed[0].Value);
        ok &= Check("and a note is shown as a note, not as a problem",
                    failed[2].Label == "Note" && !failed[2].IsProblem, Said(failed));

        var quiet = AboutDialog.UpdateRows(finished: true, pending: null, error: null, note: "another copy is doing it");
        ok &= Check("standing aside for another copy is not reported as a failure",
                    quiet.Count == 2 && quiet.TrueForAll(r => !r.IsProblem), Said(quiet));

        about.Close();
        Pump();
        return ok;
    }


    internal static bool RunMenuMnemonicChecks()
    {
        Line("-- menu keyboard access --");

        // Built by the constructor; arguments are only acted on once the form is shown, so constructing one
        // and never showing it opens no file and touches no settings.
        using var form = new MainForm(new AppSettings(), new MachineState(), Array.Empty<string>());
        if (form.MainMenuStrip is not { } bar) return Check("the menu bar was built", false);

        bool Walk(string path, ToolStripItemCollection items)
        {
            var claimed = new Dictionary<char, string>();
            var clashes = new List<string>();
            foreach (ToolStripItem item in items)
            {
                if (MnemonicOf(item.Text ?? "") is not { } m) continue;
                if (claimed.TryGetValue(m, out string? already)) clashes.Add($"'{m}' on \"{already}\" and \"{item.Text}\"");
                else claimed[m] = item.Text ?? "";
            }
            bool good = Check($"{path}: no two items claim the same Alt key" +
                              (clashes.Count > 0 ? " [" + string.Join("; ", clashes) + "]"
                                                 : $" ({string.Join(",", claimed.Keys.OrderBy(c => c))})"),
                              clashes.Count == 0);

            foreach (ToolStripItem item in items)
                if (item is ToolStripMenuItem sub && sub.DropDownItems.Count > 0)
                    good &= Walk($"{path} > {(sub.Text ?? "").Replace("&", "")}", sub.DropDownItems);
            return good;
        }

        bool ok = Walk("menu", bar.Items);

        // A command with a key must say so where it is offered. These two run the same thing from different
        // menus, and only one of them registers the key, so only a check on the DISPLAYED string covers both.
        string[] shortcuts =
        [
            "Find Filter\tCtrl+E", "Split Lines Into Fields\tCtrl+Shift+C", "Field Settings\u2026\tCtrl+Shift+D",
            "Show Line Numbers\tCtrl+L",
            // The three places a new filter can go, each on the key the dialog writes beside that choice.
            "Add Filter\u2026\tCtrl+N", "Add Filter Above Selected\u2026\tCtrl+Shift+N",
            "Add Child Filter\u2026\tCtrl+Alt+N"
        ];
        var keys = System.ComponentModel.TypeDescriptor.GetConverter(typeof(Keys));
        var advertised = AllMenuItems(bar.Items)
            .Select(m => (m.Text ?? "").Replace("&", "") + "\t" +
                         (m.ShortcutKeyDisplayString ??
                          (m.ShortcutKeys == Keys.None ? "" : keys.ConvertToString(m.ShortcutKeys))))
            .ToHashSet(StringComparer.Ordinal);
        foreach (string want in shortcuts)
            ok &= Check($"the menu offers \"{want.Replace('\t', ' ')}\"", advertised.Contains(want),
                        string.Join(" | ", advertised.Where(a => a.StartsWith(want.Split('\t')[0], StringComparison.Ordinal))));

        return ok;
    }

    /// <summary>Whether a vertical rule really was painted at <paramref name="x"/> - the position alone
    /// only says where it was meant to go. Reads a row through the middle of the bar and requires the
    /// column to be darker than the bar around it.</summary>
    private static bool RuleIsDrawnAt(Control bar, int x)
    {
        if (x <= 1 || x >= bar.Width - 1) return false;
        using var bmp = new Bitmap(bar.Width, bar.Height);
        bar.DrawToBitmap(bmp, new Rectangle(0, 0, bar.Width, bar.Height));
        int y = bar.Height / 2;
        static int Grey(Color c) => (c.R + c.G + c.B) / 3;
        int here = Grey(bmp.GetPixel(x, y));
        int left = Grey(bmp.GetPixel(x - 2, y)), right = Grey(bmp.GetPixel(x + 2, y));
        return here < left - 10 && here < right - 10;
    }

    /// <summary>Where each caption's ink really sits, read off a render of the row. A control's box says
    /// nothing about where it draws its text - a combo box's edit puts it where the native control wants it,
    /// a button centres its caption - so the pixels are the only honest measure of "these line up".
    /// The area scanned skips each control's border and the check box's glyph, leaving only the caption.</summary>
    private static List<(string What, int Top, int Bottom, string Font)> TextInk(Control bar)
    {
        using var bmp = new Bitmap(bar.Width, bar.Height);
        bar.DrawToBitmap(bmp, new Rectangle(0, 0, bar.Width, bar.Height));

        var found = new List<(string, int, int, string)>();
        foreach (var c in AllControls(bar))
        {
            if (c is not (ComboBox or Button or CheckBox or Label)) continue;
            int Dp(int v) => v * c.DeviceDpi / 96;
            Rectangle r = bar.RectangleToClient(c.Parent!.RectangleToScreen(c.Bounds));
            Rectangle area = c switch
            {
                ComboBox => new Rectangle(r.Left + Dp(4), r.Top + Dp(3), r.Width - Dp(26), r.Height - Dp(6)),
                CheckBox => new Rectangle(r.Left + Dp(17), r.Top, r.Width - Dp(17), r.Height),
                Button { FlatStyle: FlatStyle.Standard } => Rectangle.Inflate(r, -Dp(5), -Dp(5)),
                _ => r
            };
            area.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height));
            if (area.Width <= 0 || area.Height <= 0) continue;

            int top = -1, bottom = -1;
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    if ((p.R + p.G + p.B) / 3 >= 170) continue;   // anything darker than the backgrounds
                    if (top < 0) top = y;
                    bottom = y;
                    break;
                }
            if (top < 0) continue;

            string what = c is Label l && l.Text.Length > 0 ? l.Text
                        : c.AccessibleName ?? c.GetType().Name;
            found.Add((what, top, bottom, $"{c.Font.Name} {c.Font.SizeInPoints:0.##}pt"));
        }
        return found;
    }
}

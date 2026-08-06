using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;
using Cascade.Core.Columns;
using Cascade.Core.Document;
using Cascade.Core.Find;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// Headless screenshot harness (<c>Cascade.exe --screens &lt;outDir&gt; [file] [x.tat]</c>). Renders each
/// dialog with <c>DrawToBitmap</c> and captures the main window from the screen, so the UI can be
/// reviewed at the current DPI without manual interaction.
/// </summary>
internal static class UiShots
{
    public static int Run(string[] args)
    {
        string outDir = args.FirstOrDefault(a => Directory.Exists(a) || a.Contains("shots", StringComparison.OrdinalIgnoreCase))
                        ?? Path.Combine(Path.GetTempPath(), "cascade_shots");
        string? file = args.FirstOrDefault(a => File.Exists(a) && !a.EndsWith(".tat", StringComparison.OrdinalIgnoreCase));
        string? tat = args.FirstOrDefault(a => a.EndsWith(".tat", StringComparison.OrdinalIgnoreCase) && File.Exists(a));
        Directory.CreateDirectory(outDir);

        // Render against a THROWAWAY settings directory. Reading the real settings made the harness auto-load
        // the user's last filter file, which /demo then dirtied - so closing the window popped a modal
        // "Save changes to filters?" prompt that blocked this headless run forever (and answering "Yes" would
        // have overwritten their filter file with the demo state).
        string settingsDir = Path.Combine(Path.GetTempPath(), "cascade_screens_" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CASCADE_SETTINGS_DIR", settingsDir);

        Console.WriteLine($"DPI scaling test. Output: {outDir}");

        var demoFilter = new Filter
        {
            Enabled = true,
            Description = "declined charges",
            Match = { Text = @"\[payment-svc\s*\].+declined", Regex = true },
            Style = { Foreground = new RgbColor(0xFF, 0xFF, 0xFF), Background = new RgbColor(0x9C, 0x27, 0xB0) }
        };
        ShotDialog(new FilterEditDialog(demoFilter, isNew: false), outDir, "filter-edit");

        var badFilter = new Filter { Enabled = true, Match = { Text = @"\[payment-svc\s*\].+declined", Regex = true } };
        var filterError = new FilterEditDialog(badFilter, isNew: false);
        filterError.SetTextForTesting(@"\[payment-svc\s*\].+(declined");
        ShotDialog(filterError, outDir, "filter-edit-error");

        var free = LuckyColors.Free(new[] { demoFilter }, badFilter);
        ShotDialog(new PaletteDialog(free, @"\[payment-svc\s*\].+declined", null), outDir, "palette");

        // Three filters that agree on a background and disagree on everything else, which is the state the
        // dialog exists to show: one shared colour offered back, the rest saying so.
        var group = new List<Filter>
        {
            demoFilter,
            new() { Description = "timeouts", Match = { Text = "upstream timeout" }, Style = { Background = new RgbColor(0x9C, 0x27, 0xB0), Bold = true } },
            new() { Description = "retries", Match = { Text = "retrying inventory-svc" }, Style = { Background = new RgbColor(0x9C, 0x27, 0xB0) } },
        };
        ShotDialog(new AppearanceDialog(group, group,
                       new ResolvedStyle(new RgbColor(0x1F, 0x1F, 0x1F), new RgbColor(0xFF, 0xFF, 0xFF), false, false)),
                   outDir, "appearance");

        ShotFindBar(outDir, "find", "");
        ShotFindBar(outDir, "find-tally", "Match 12 of 348 lines \u00b7 96 hidden \u00b7 891 of 1,204 hits");
        ShotFindBar(outDir, "find-badregex", "", badPattern: "charge(declined");

        // Set up as a real one would be, so the grid in it shows the names that can be typed over rather
        // than a blank block that says nothing about the dialog.
        var cols = new ColumnSpec
        {
            Enabled = true,
            Mode = ColumnSplitMode.Template,
            Template = "[[Time]][[Service]][[Level]] [Message]"
        };
        cols.SyncColumnsFromTemplate();
        cols.Columns[2].Width = 90;
        ShotDialog(new ColumnsDialog(cols, "[2026-07-31T09:31:17][api-gateway][INFO] payment order message text"), outDir, "columns");
        ShotDialog(new PreferencesDialog(new AppSettings()), outDir, "preferences");
        ShotDialog(new GoToDialog(8_295_214, 1), outDir, "goto");
        ShotDialog(new AboutDialog(null), outDir, "about");

        ShotMainForm(outDir, file, tat);
        ShotGridStates(outDir);
        ShotMatchMap(outDir);
        ShotFilterSearch(outDir);
        ShotLuckyColors(outDir);

        try { if (Directory.Exists(settingsDir)) Directory.Delete(settingsDir, true); } catch { /* ignore */ }

        Console.WriteLine("done");
        return 0;
    }

    /// <summary>Renders the filter list with an active search term so the highlight (matching filters keep
    /// their color with the term bold; non-matching filters are colorless and dimmed) can be reviewed.</summary>
    private static void ShotFilterSearch(string dir)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 30; i++)
        {
            string lvl = i % 4 == 0 ? "ERROR" : i % 4 == 1 ? "WARN " : "INFO ";
            sb.Append($"[2026-07-31T09:00:{i:00}][api-gateway][{lvl}] payment order message {i}\n");
        }
        string path = Path.Combine(Path.GetTempPath(), "cascade_fsearch_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var settings = AppSettings.Load();
        var doc = new CascadeDocument();
        doc.Open(path);
        doc.WaitForIndex();
        doc.Filters.Add(new Filter { Enabled = true, Description = "errors", Match = { Text = "ERROR" }, Style = { Foreground = new RgbColor(0xC0, 0, 0), Bold = true } });
        doc.Filters.Add(new Filter { Enabled = true, Description = "warnings", Match = { Text = "WARN" }, Style = { Background = new RgbColor(0xFF, 0xF1, 0x9A) } });
        doc.Filters.Add(new Filter { Enabled = true, Description = "info", Match = { Text = "INFO" }, Style = { Foreground = new RgbColor(0x60, 0x60, 0x60) } });
        doc.Filters.Add(new Filter { Enabled = true, Description = "payments", Match = { Text = "payment" }, Style = { Foreground = new RgbColor(0, 0, 0xC0), Underline = true } });
        doc.Filters.Add(new Filter { Enabled = true, Description = "orders", Match = { Text = "order" }, Style = { Foreground = new RgbColor(0, 0x88, 0) } });
        doc.ApplyFilters();
        WaitIdle(doc);

        var tree = new FilterTreeControl { Dock = DockStyle.Fill };
        var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), ClientSize = new Size(560, 220), Opacity = 0, FormBorderStyle = FormBorderStyle.None };
        host.Controls.Add(tree);
        tree.SetSettings(settings);
        tree.Attach(doc);
        host.Show();
        Settle();
        // Closed, which is how the list normally stands - and the only place the key is advertised.
        CapControl(host, dir, "filter-list");

        tree.SetSearchText("ERROR"); // matches only the "errors" filter; the rest are dimmed
        Settle();
        CapControl(host, dir, "filter-search");

        // Several filters selected, two of them nested: the strip left of the text is shaded to show which
        // are in the group, and shaded harder on the row the keyboard is standing on. Nesting is in the
        // picture because it is what makes that strip wider or narrower.
        tree.HideSearch();
        var info = doc.Filters.Roots[2];
        doc.Filters.Move(doc.Filters.Roots[3], info, 0);
        doc.Filters.Move(doc.Filters.Roots[3], info, 1);
        tree.Rebuild();
        Settle();
        tree.ClickFilterForTesting(doc.Filters.Roots[1]);
        tree.ClickFilterForTesting(info.Children[1], Keys.Shift);
        Settle();
        CapControl(host, dir, "filter-list-selected");

        host.Close();
        host.Dispose();
        doc.Dispose();
        try { File.Delete(path); } catch { /* ignore */ }
    }

    /// <summary>Renders the log grid in a few key states (dim vs. filtered, with a colored match, a
    /// marker, and a selected line) so the coloring/selection/marker rendering can be reviewed.</summary>
    private static void ShotGridStates(string dir)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 40; i++)
        {
            string lvl = i % 4 == 0 ? "ERROR" : i % 4 == 1 ? "WARN " : "INFO ";
            sb.Append($"[2026-07-31T09:00:{i:00}.123][api-gateway][{i:000}][{lvl}] message number {i} with detail text\n");
        }
        string path = Path.Combine(Path.GetTempPath(), "cascade_states_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var settings = AppSettings.Load();
        var doc = new CascadeDocument();
        doc.Open(path);
        doc.WaitForIndex();
        doc.Filters.Add(new Filter { Enabled = true, Description = "errors", Match = { Text = "ERROR" }, Style = { Foreground = new RgbColor(0xC0, 0, 0), Bold = true } });
        doc.Filters.Add(new Filter { Enabled = true, Description = "warnings", Match = { Text = "WARN" }, Style = { Background = new RgbColor(0xFF, 0xF1, 0x9A), Underline = true } });
        doc.ApplyFilters();
        WaitIdle(doc);
        doc.Markers.Toggle(2, 0);
        doc.Markers.Toggle(6, 3);

        var grid = new LineGridControl { Dock = DockStyle.Fill };
        var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), ClientSize = new Size(1100, 470), Opacity = 0, FormBorderStyle = FormBorderStyle.None };
        host.Controls.Add(grid);
        grid.Attach(doc, settings);
        host.Show();
        Settle();
        grid.SelectRowForAccessibility(5);
        grid.RefreshView();
        Settle();
        CapControl(host, dir, "state-dim");

        doc.Filters.ShowOnlyFilteredLines = true;
        doc.ApplyFilters();
        WaitIdle(doc);
        grid.RefreshView();
        Settle();
        CapControl(host, dir, "state-filtered");

        // Columns enabled (display-only split into named columns).
        doc.Filters.ShowOnlyFilteredLines = false;
        doc.ApplyFilters();
        WaitIdle(doc);
        doc.Columns.Enabled = true;
        doc.Columns.Mode = ColumnSplitMode.Delimiter;
        doc.Columns.Delimiter = "]";
        doc.Columns.Columns.Clear();
        // The last one is left to size itself, which is how a log usually reads: fixed fields on the left
        // and the message running on to the right-hand edge.
        foreach (var (n, w) in new[] { ("Time", 190), ("Provider", 90), ("Id", 55), ("Level", 80), ("Message", 0) })
            doc.Columns.Columns.Add(new ColumnDef { Name = n, Width = w });
        grid.RefreshView();
        Settle();
        CapControl(host, dir, "state-columns");

        // Text picked out inside one cell, with a find term live: the selection must sit in the cell it was
        // dragged in and the marks must land on the glyphs, cell by cell.
        grid.SetFindHighlight(FindEngine.CompileQuery(new FindQuery("message", false, false)));
        grid.SelectPartOfCellForTesting(5, 1, 0, 11);
        grid.RefreshView();
        Settle();
        CapControl(host, dir, "state-columns-selected");
        grid.SetFindHighlight(null);

        // Scrolled right with long lines in view: nothing may be painted over the marker/line-number margin.
        // The host is narrowed first, otherwise the content fits and there is nothing to scroll.
        doc.Columns.Enabled = false;
        host.ClientSize = new Size(560, 470);
        grid.RefreshView();
        Settle();
        grid.ScrollHorizontallyTo(400);
        grid.RefreshView();
        Settle();
        CapControl(host, dir, "state-hscroll");
        grid.ScrollHorizontallyTo(0);

        host.Close();
        host.Dispose();
        doc.Dispose();
        try { File.Delete(path); } catch { /* ignore */ }
    }

    /// <summary>The match map over a file big enough for a band to cover many lines, which is the only scale
    /// at which it says anything: a dense block, an empty stretch, and a single line on its own.</summary>
    private static void ShotMatchMap(string dir)
    {
        const int lines = 60_000;
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
            sb.Append(i < 12_000 || (i > 30_000 && i < 33_000) ? "WARN " : i % 9_999 == 0 ? "ERROR " : "INFO ")
              .Append("service message ").Append(i).Append('\n');
        string path = Path.Combine(Path.GetTempPath(), "cascade_map_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var settings = AppSettings.Load();
        var doc = new CascadeDocument();
        doc.Open(path);
        doc.WaitForIndex();
        doc.Filters.Add(new Filter { Enabled = true, Description = "warnings", Match = { Text = "WARN" }, Style = { Background = new RgbColor(0xFF, 0xF1, 0x9A) } });
        doc.Filters.Add(new Filter { Enabled = true, Description = "errors", Match = { Text = "ERROR" }, Style = { Foreground = new RgbColor(0xFF, 0xFF, 0xFF), Background = new RgbColor(0xC0, 0x20, 0x20) } });
        doc.ApplyFilters();
        WaitIdle(doc);
        for (int i = 0; i < 6; i++) doc.Markers.Toggle(4_000 + i * 8_500, i % 3);

        var grid = new LineGridControl { Dock = DockStyle.Fill };
        var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), ClientSize = new Size(900, 520), Opacity = 0, FormBorderStyle = FormBorderStyle.None };
        host.Controls.Add(grid);
        grid.Attach(doc, settings);
        host.Show();
        Settle();
        grid.ScrollToRow(20_000);
        grid.RefreshView();
        Settle();
        CapControl(host, dir, "match-map");

        doc.Filters.ShowOnlyFilteredLines = true;
        doc.ApplyFilters();
        WaitIdle(doc);
        grid.InvalidateMatchMap();
        grid.RefreshView();
        Settle();
        CapControl(host, dir, "match-map-filtered");

        host.Close();
        host.Dispose();
        doc.Dispose();
        try { File.Delete(path); } catch { /* ignore */ }
    }

    private static void WaitIdle(CascadeDocument doc)
    {
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 5000;)
        {
            if (doc.IsIndexComplete && doc.IsFilterIdle) return;
            Thread.Sleep(5);
        }
    }

    /// <summary>Lets a form finish laying out and painting before it is captured, returning as soon as it
    /// goes quiet rather than always waiting a flat quarter-second - that wait, once per shot, was most of
    /// what a render run cost. The old wait remains the cap.</summary>
    private static void Settle()
    {
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 250;)
        {
            Application.DoEvents();
            if (PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE)) { Thread.Sleep(1); continue; }
            Thread.Sleep(15);
            Application.DoEvents();
            if (!PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE)) return;
        }
    }

    private const uint PM_NOREMOVE = 0;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool PeekMessage(out MSG message, IntPtr window, uint first, uint last, uint remove);

    private static void CapControl(Form host, string dir, string name)
    {
        using var bmp = new Bitmap(host.ClientSize.Width, host.ClientSize.Height);
        host.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        bmp.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
        Console.WriteLine($"{name}: {bmp.Width}x{bmp.Height}");
    }

    /// <summary>The first stretch of the suggested-colour ring, in the order the button offers them. Whether
    /// a palette is easy on the eyes is not something a number answers, so it has to be looked at.</summary>
    private static void ShotLuckyColors(string dir)
    {
        const int shown = 96, cols = 8, cellW = 190, cellH = 34;
        using var bmp = new Bitmap(cols * cellW, shown / cols * cellH);
        using (var g = Graphics.FromImage(bmp))
        using (var font = new Font("Segoe UI", 9f))
        {
            g.Clear(Color.White);
            for (int i = 0; i < shown; i++)
            {
                var pair = LuckyColors.At(i);
                var back = Color.FromArgb(pair.Back.R, pair.Back.G, pair.Back.B);
                var fore = Color.FromArgb(pair.Fore.R, pair.Fore.G, pair.Fore.B);
                var cell = new Rectangle(i % cols * cellW, i / cols * cellH, cellW - 2, cellH - 2);
                using (var b = new SolidBrush(back)) g.FillRectangle(b, cell);
                TextRenderer.DrawText(g, $"{i + 1}  {back.R:X2}{back.G:X2}{back.B:X2}  press me", font,
                    cell, fore, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            }
        }
        bmp.Save(Path.Combine(dir, "lucky-colours.png"), ImageFormat.Png);
        Console.WriteLine($"lucky-colours: {shown} of {LuckyColors.Count}");
    }

    /// <summary>The find bar is a strip in the main window rather than a dialog, so it is rendered in a host
    /// of roughly the width it really gets - a shot of it at its own natural size would say nothing about
    /// how the term, the options and the count share a full-width row.</summary>
    private static void ShotFindBar(string dir, string name, string message, string? badPattern = null)
    {
        var bar = new FindBar((_, _) => { }) { Visible = true };
        bar.SetMessage(message);
        using var host = new Form
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(60, 60),
            FormBorderStyle = FormBorderStyle.None,
            ClientSize = new Size(1500, 40),
            Opacity = 0
        };
        host.Controls.Add(bar);
        host.Show();
        Settle();
        bar.SnapHeightTo(24);   // as the app does: a whole number of log lines
        host.ClientSize = new Size(1500, bar.Height);
        if (badPattern is not null)
        {
            bar.SetTermForTesting(badPattern, badPattern.Length, 0);
            bar.SetRegexForTesting(true);
        }
        Settle();
        using var bmp = new Bitmap(host.ClientSize.Width, Math.Max(1, host.ClientSize.Height));
        host.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        bmp.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
        Console.WriteLine($"{name}: {bmp.Width}x{bmp.Height}");
        host.Close();
    }

    private static void ShotDialog(Form form, string dir, string name)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(60, 60);
        form.Show();
        Application.DoEvents();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 250) { Application.DoEvents(); Thread.Sleep(10); }

        using var bmp = new Bitmap(Math.Max(1, form.Width), Math.Max(1, form.Height));
        form.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        bmp.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
        Console.WriteLine($"{name}: {form.Width}x{form.Height}");
        form.Close();
        form.Dispose();
    }

    private static void ShotMainForm(string dir, string? file, string? tat)
    {
        var settings = AppSettings.Load();
        var argList = new List<string>();
        if (file is not null) argList.Add(file);
        if (tat is not null) argList.Add("/Filters:" + tat);
        argList.Add("/demo");

        var form = new MainForm(settings, MachineState.Load(), argList.ToArray())
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Size = new Size(1500, 950),
            WindowState = FormWindowState.Normal,
            Opacity = 0 // render off-screen; DrawToBitmap reads the control tree, not the screen
        };
        form.NoSavePrompt = true; // nobody is here to answer a modal prompt on close
        form.UpdateNoticeOverride = "Will update to v2026.8.1 on restart";
        form.Show();
        form.Activate();
        Application.DoEvents();

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000)
        {
            Application.DoEvents();
            Thread.Sleep(15);
            // Settled: nothing left to index or filter, and the window has had a moment to lay out.
            if (sw.ElapsedMilliseconds > 400 && !form.IsBusyForHarness) break;
        }

        using (var bmp = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
            bmp.Save(Path.Combine(dir, "main.png"), ImageFormat.Png);
        }
        Console.WriteLine($"main: {form.Width}x{form.Height}");
        form.Close();
        form.Dispose();
    }
}

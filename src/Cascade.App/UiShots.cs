using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Windows.Forms;
using Cascade.Core.Columns;
using Cascade.Core.Document;
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
            Description = "disk errors",
            Match = { Text = @"\[OrderService\].+Disk", Regex = true },
            Style = { Foreground = new RgbColor(0xFF, 0xFF, 0xFF), Background = new RgbColor(0x9C, 0x27, 0xB0) }
        };
        ShotDialog(new FilterEditDialog(demoFilter, isNew: false), outDir, "filter-edit");

        var badFilter = new Filter { Enabled = true, Match = { Text = @"\[OrderService\].+Disk", Regex = true } };
        var filterError = new FilterEditDialog(badFilter, isNew: false);
        filterError.SetTextForTesting(@"\[OrderService\].+(Disk");
        ShotDialog(filterError, outDir, "filter-edit-error");

        ShotDialog(new FindDialog((_, _) => { }), outDir, "find");

        var findMissed = new FindDialog((_, _) => { });
        findMissed.SetStatus("Not found.");
        ShotDialog(findMissed, outDir, "find-notfound");

        var findSearching = new FindDialog((_, _) => { });
        findSearching.SetSearching(true);
        findSearching.SetProgress(0.45);
        ShotDialog(findSearching, outDir, "find-searching");

        var cols = new ColumnSpec();
        ShotDialog(new ColumnsDialog(cols, "[2026-07-16T18:06:48][inventory-svc][3][2FA8][315C][util][Func][INFO][TFLAG] message text"), outDir, "columns");
        ShotDialog(new PreferencesDialog(new AppSettings()), outDir, "preferences");
        ShotDialog(new GoToDialog(8_295_214, 1), outDir, "goto");
        ShotDialog(new AboutDialog(null), outDir, "about");

        ShotMainForm(outDir, file, tat);
        ShotGridStates(outDir);
        ShotFilterSearch(outDir);

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
            sb.Append($"[2026-07-16T18:06:{i:00}][inventory-svc][{lvl}] disk network message {i}\n");
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
        doc.Filters.Add(new Filter { Enabled = true, Description = "disk io", Match = { Text = "disk" }, Style = { Foreground = new RgbColor(0, 0, 0xC0) } });
        doc.Filters.Add(new Filter { Enabled = true, Description = "network", Match = { Text = "network" }, Style = { Foreground = new RgbColor(0, 0x88, 0) } });
        doc.ApplyFilters();
        WaitIdle(doc);

        var tree = new FilterTreeControl { Dock = DockStyle.Fill };
        var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), ClientSize = new Size(560, 220), Opacity = 0, FormBorderStyle = FormBorderStyle.None };
        host.Controls.Add(tree);
        tree.SetSettings(settings);
        tree.Attach(doc);
        tree.SetSearchText("ERROR"); // matches only the "errors" filter; the rest are dimmed
        host.Show();
        Settle();
        CapControl(host, dir, "filter-search");

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
            sb.Append($"[2026-07-16T18:06:{i:00}.123][inventory-svc][{i:000}][{lvl}] message number {i} with detail text\n");
        }
        string path = Path.Combine(Path.GetTempPath(), "cascade_states_" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var settings = AppSettings.Load();
        var doc = new CascadeDocument();
        doc.Open(path);
        doc.WaitForIndex();
        doc.Filters.Add(new Filter { Enabled = true, Description = "errors", Match = { Text = "ERROR" }, Style = { Foreground = new RgbColor(0xC0, 0, 0), Bold = true } });
        doc.Filters.Add(new Filter { Enabled = true, Description = "warnings", Match = { Text = "WARN" }, Style = { Background = new RgbColor(0xFF, 0xF1, 0x9A) } });
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
        foreach (var (n, w) in new[] { ("Time", 190), ("Provider", 90), ("Id", 55), ("Level", 80), ("Message", 360) })
            doc.Columns.Columns.Add(new ColumnDef { Name = n, Width = w });
        grid.RefreshView();
        Settle();
        CapControl(host, dir, "state-columns");

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

    private static void WaitIdle(CascadeDocument doc)
    {
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 5000;)
        {
            if (doc.IsIndexComplete && doc.IsFilterIdle) return;
            Thread.Sleep(5);
        }
    }

    private static void Settle()
    {
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 250;) { Application.DoEvents(); Thread.Sleep(10); }
    }

    private static void CapControl(Form host, string dir, string name)
    {
        using var bmp = new Bitmap(host.ClientSize.Width, host.ClientSize.Height);
        host.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        bmp.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
        Console.WriteLine($"{name}: {bmp.Width}x{bmp.Height}");
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

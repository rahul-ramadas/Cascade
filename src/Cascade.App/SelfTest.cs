using System.Diagnostics;
using System.Drawing;
using System.Text;
using Cascade.Core.Columns;
using Cascade.Core.Document;
using Cascade.Core.Model;
using Cascade.Core.Persistence;

namespace Cascade.App;

/// <summary>
/// Headless end-to-end verification run via <c>Cascade.exe --selftest [file] [/Filters:x.tat]</c>.
/// Exercises open → stream-index → import/apply filters → row mapping and prints timings. Writes a
/// log file (and to the attached console) and returns a non-zero exit code on failure.
/// </summary>
internal static class SelfTest
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "cascade_selftest.log");
    private static StreamWriter _log = null!;

    public static int Run(string[] args)
    {
        _log = new StreamWriter(LogPath, false) { AutoFlush = true };
        try
        {
            Line("=== Cascade self-test ===");
            Line("Log: " + LogPath);

            string? file = args.FirstOrDefault(a => !a.StartsWith('/') && !a.StartsWith("--"));
            string? tat = args.FirstOrDefault(a => a.StartsWith("/Filters:", StringComparison.OrdinalIgnoreCase))?["/Filters:".Length..].Trim('"');

            bool ok = RunEngineChecks();
            ok &= RunSettingsChecks();
            ok &= RunRenderChecks();
            if (file is not null && File.Exists(file)) ok &= RunFileChecks(file, tat);
            else Line("(no real file supplied; skipped large-file checks)");

            Line(ok ? "RESULT: PASSED" : "RESULT: FAILED");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Line("EXCEPTION: " + ex);
            return 2;
        }
        finally { _log.Dispose(); }
    }

    /// <summary>
    /// Scrolling right must never paint line text over the marker or line-number margin.
    ///
    /// The margin does not scroll, so every pixel of it has to be identical whatever the horizontal offset
    /// is - which makes the check exact rather than a judgement about what looks wrong. It bites because
    /// TextRenderer draws through GDI and silently ignores the GDI+ clip region unless asked not to, so the
    /// SetClip guarding the text looked sufficient and was not.
    /// </summary>
    private static bool RunRenderChecks()
    {
        Line("-- rendering --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_render_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        // Long enough that the content is far wider than the viewport below, so there is room to scroll.
        for (int i = 0; i < 40; i++)
            sb.Append($"[2026-07-16T18:06:{i:00}.123][provider][{i:000}][INFO ] a deliberately long line " +
                      $"of message text {i} that runs well past the right hand edge of the viewport\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        var doc = new CascadeDocument();
        Form? host = null;
        try
        {
            doc.Open(path);
            doc.WaitForIndex();
            doc.Markers.Toggle(2, 0);   // a marker bar, so the marker gutter has content to compare too

            var settings = new AppSettings { MarkerVisibility = MarkerVisibilityMode.Always };
            var grid = new LineGridControl { Dock = DockStyle.Fill };
            host = new Form
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(0, 0),
                ClientSize = new Size(420, 320),
                Opacity = 0,
                FormBorderStyle = FormBorderStyle.None
            };
            host.Controls.Add(grid);
            grid.Attach(doc, settings);
            host.Show();
            Pump();

            int gutter = grid.GutterWidthForTesting;
            if (gutter <= 0) return Check("the grid has a margin to protect", false);
            var margin = grid.GutterAreaForTesting;

            grid.ScrollHorizontallyTo(0);
            grid.RefreshView();
            Pump();
            using var unscrolled = Capture(host);

            grid.ScrollHorizontallyTo(260);
            grid.RefreshView();
            Pump();
            using var scrolled = Capture(host);

            bool moved = !SameRegion(unscrolled, scrolled,
                             new Rectangle(gutter, margin.Top, unscrolled.Width - gutter - 20, margin.Height));
            bool ok = Check("scrolling right actually moved the text", moved);

            var diff = FirstDifference(unscrolled, scrolled, margin);
            ok &= Check($"scrolling right leaves the line-number margin (0..{gutter}) untouched" +
                        (diff is null ? "" : $" [first differs at x={diff.Value.X},y={diff.Value.Y}: " +
                                              $"{unscrolled.GetPixel(diff.Value.X, diff.Value.Y)} -> " +
                                              $"{scrolled.GetPixel(diff.Value.X, diff.Value.Y)}]"),
                        diff is null);

            // Columns are a different drawing path - per-cell text plus a header row - and had the same flaw.
            doc.Columns.Enabled = true;
            doc.Columns.Mode = ColumnSplitMode.Delimiter;
            doc.Columns.Delimiter = "]";
            doc.Columns.Columns.Clear();
            foreach (var (n, w) in new[] { ("Time", 190), ("Provider", 90), ("Id", 55), ("Message", 360) })
                doc.Columns.Columns.Add(new ColumnDef { Name = n, Width = w });

            grid.ScrollHorizontallyTo(0);
            grid.RefreshView();
            Pump();
            using var colUnscrolled = Capture(host);

            grid.ScrollHorizontallyTo(260);
            grid.RefreshView();
            Pump();
            using var colScrolled = Capture(host);

            var colMargin = grid.GutterAreaForTesting;
            ok &= Check("scrolling right actually moved the columns",
                        !SameRegion(colUnscrolled, colScrolled,
                            new Rectangle(gutter, colMargin.Top, colUnscrolled.Width - gutter - 20, colMargin.Height)));
            var colDiff = FirstDifference(colUnscrolled, colScrolled, colMargin);
            ok &= Check("scrolling right with columns leaves the margin untouched" +
                        (colDiff is null ? "" : $" [first differs at x={colDiff.Value.X},y={colDiff.Value.Y}]"),
                        colDiff is null);
            return ok;
        }
        finally
        {
            try { host?.Close(); host?.Dispose(); } catch { /* ignore */ }
            doc.Dispose();
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    private static Bitmap Capture(Form host)
    {
        var bmp = new Bitmap(host.ClientSize.Width, host.ClientSize.Height);
        host.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        return bmp;
    }

    private static bool SameRegion(Bitmap a, Bitmap b, Rectangle r)
        => FirstDifference(a, b, r) is null;

    private static Point? FirstDifference(Bitmap a, Bitmap b, Rectangle r)
    {
        for (int y = r.Top; y < r.Bottom && y < a.Height; y++)
            for (int x = r.Left; x < r.Right && x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return new Point(x, y);
        return null;
    }

    private static void Pump()
    {
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 250;) { Application.DoEvents(); Thread.Sleep(10); }
    }

    /// <summary>Exported settings must come back exactly, or carrying them to another machine silently
    /// loses whichever preference was forgotten. Every persisted property is compared, so a newly added one
    /// is covered automatically.</summary>
    private static bool RunSettingsChecks()
    {
        Line("-- settings export/import --");
        string dir = Path.Combine(Path.GetTempPath(), "cascade_st_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var original = new AppSettings
            {
                FontFamily = "Cascadia Mono",
                FontSize = 13.5f,
                ZoomPercent = 130,
                TabSize = 7,
                ShowLineNumbers = false,
                MarkerVisibility = MarkerVisibilityMode.Never,
                ForegroundArgb = Color.Teal.ToArgb(),
                BackgroundArgb = Color.Ivory.ToArgb(),
                AutoLoadLastFilterFile = false,
                LastFilterFile = @"C:\somewhere\filters.cascade"
            };
            original.AddRecentFile(@"C:\logs\a.log");
            original.AddRecentFilterFile(@"C:\logs\f.cascade");

            string file = Path.Combine(dir, "exported.json");
            original.ExportTo(file);

            var restored = new AppSettings();          // starts at defaults
            restored.ImportFrom(file);

            bool ok = true;
            foreach (var p in AppSettings.Persisted)
            {
                object? a = p.GetValue(original), b = p.GetValue(restored);
                bool same = a is IEnumerable<string> left && b is IEnumerable<string> right
                    ? left.SequenceEqual(right)
                    : Equals(a, b);
                ok &= Check($"round-trips {p.Name}", same);
            }

            // Something that is not a settings file must be refused, not allowed to wipe the preferences.
            string junk = Path.Combine(dir, "junk.json");
            File.WriteAllText(junk, "definitely not json");
            bool refused = false;
            try { new AppSettings().ImportFrom(junk); }
            catch (InvalidDataException) { refused = true; }
            ok &= Check("refuses a file that is not settings", refused);
            return ok;
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static bool RunEngineChecks()
    {
        Line("-- engine checks (temp file) --");
        string path = Path.Combine(Path.GetTempPath(), "cascade_st_" + Guid.NewGuid().ToString("N") + ".log");
        var sb = new StringBuilder();
        for (int i = 0; i < 5000; i++)
            sb.Append(i % 4 == 0 ? "ERROR disk " : i % 4 == 1 ? "ERROR net " : "info ").Append(i).Append('\n');
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        try
        {
            using var doc = new CascadeDocument();
            doc.Open(path);
            doc.WaitForIndex();
            bool ok = Check("indexed 5000 lines", doc.CompletedLineCount == 5000);

            var error = new Filter { Enabled = false, Match = { Text = "ERROR" } };
            var disk = new Filter { Enabled = true, Match = { Text = "disk" } };
            doc.Filters.Add(error);
            doc.Filters.Add(disk, error);
            doc.Filters.ShowOnlyFilteredLines = true;
            doc.ApplyFilters();
            WaitFilter(doc);

            // disabled parent still constrains: only "ERROR disk" (every 4th) match
            ok &= Check("hierarchical match = 1250", doc.MatchedLineCount == 1250);
            ok &= Check("row0 maps to line 0", doc.RowToLine(0) == 0);
            return ok;
        }
        finally { try { File.Delete(path); } catch { } }
    }

    private static bool RunFileChecks(string file, string? tat)
    {
        Line($"-- file checks: {file} --");
        var total = Stopwatch.StartNew();
        using var doc = new CascadeDocument();
        doc.Open(file);

        var first = Stopwatch.StartNew();
        while (doc.CompletedLineCount == 0 && first.ElapsedMilliseconds < 10000) Thread.Sleep(1);
        Line($"first lines available: {first.ElapsedMilliseconds} ms");

        doc.WaitForIndex();
        Line($"indexed {doc.CompletedLineCount:N0} lines in {total.ElapsedMilliseconds} ms");
        Line("first line: " + Truncate(doc.GetLineText(0), 100));
        Line($"encoding: {doc.Encoding.WebName}");

        int enabled;
        if (tat is not null && File.Exists(tat))
        {
            doc.SetFilters(TatImporter.Import(tat));
            int count = doc.Filters.EnumerateDepthFirst().Count();
            Line($"imported {count} filters from {tat}");
            foreach (var f in doc.Filters.Roots.Take(5)) f.Enabled = true;
            enabled = doc.Filters.EnumerateDepthFirst().Count(f => f.Enabled);
        }
        else
        {
            doc.Filters.Add(new Filter { Enabled = true, Match = { Text = "Error", CaseSensitive = false } });
            enabled = 1;
        }

        doc.Filters.ShowOnlyFilteredLines = true;
        var fsw = Stopwatch.StartNew();
        doc.ApplyFilters();
        WaitFilter(doc, 180000);
        Line($"filtered with {enabled} enabled filter(s): {doc.MatchedLineCount:N0} matches in {fsw.ElapsedMilliseconds} ms");

        bool ok = Check("matched count within total", doc.MatchedLineCount <= doc.CompletedLineCount);
        if (doc.RowCount > 0)
        {
            long l0 = doc.RowToLine(0);
            ok &= Check("row->line ascending", doc.RowCount < 2 || doc.RowToLine(1) > l0);
            Line("first match at line " + (l0 + 1) + ": " + Truncate(doc.GetLineText(l0), 100));
        }
        return ok;
    }

    private static void WaitFilter(CascadeDocument doc, int timeoutMs = 30000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (doc.IsIndexComplete && doc.IsFilterIdle) return;
            Thread.Sleep(3);
        }
    }

    private static bool Check(string name, bool condition)
    {
        Line((condition ? "[PASS] " : "[FAIL] ") + name);
        return condition;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static void Line(string text)
    {
        _log.WriteLine(text);
        try { Console.WriteLine(text); } catch { /* no console attached */ }
    }
}

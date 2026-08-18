using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Cascade.Core.Columns;
using Cascade.Core.Document;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// Renders the log view in a fixed set of states and writes each one to a PNG, so that two builds of this
/// program can be held against each other pixel for pixel. Run with <c>Cascade.exe --rendershots [dir]</c>.
///
/// <para>This exists because a change to how text is drawn was once proved harmless by a check that
/// compared the new drawing against the new drawing - self-consistent, and quite happy to be uniformly
/// wrong. What matters is not that the parts agree with each other but that the whole agrees with the
/// program people already have, so this compares against THAT: build the old one, build the new one, render
/// both, and require the pictures to be identical.</para>
///
/// <para>It uses nothing but the view's ordinary public surface, so the same file compiles against a build
/// from before any of this work and cannot itself change what is drawn.</para>
/// </summary>
internal static class RenderShots
{
    private const int Width = 900;
    private const int Height = 460;

    public static int Run(string[] args)
    {
        string dir = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
                     ?? Path.Combine(Path.GetTempPath(), "cascade_rendershots");
        Directory.CreateDirectory(dir);

        // A window writes preferences and recent-file lists as it goes; point that somewhere throwaway.
        string configDir = Path.Combine(Path.GetTempPath(), "cascade_shots_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDir);
        Environment.SetEnvironmentVariable("CASCADE_SETTINGS_DIR", configDir);

        string log = Fixture();
        var doc = new CascadeDocument();
        var manifest = new StringBuilder();
        try
        {
            doc.Open(log);
            doc.WaitForIndex();

            foreach (var (name, prepare) in Scenes())
            {
                var settings = new AppSettings { MarkerVisibility = MarkerVisibilityMode.Always };
                doc.Markers.Toggle(3, 0);
                doc.Markers.Toggle(7, 2);

                using var host = new Form
                {
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(0, 0),
                    ClientSize = new Size(Width, Height),
                    Opacity = 0,
                    ShowInTaskbar = false,
                    FormBorderStyle = FormBorderStyle.None
                };
                var grid = new LineGridControl { Dock = DockStyle.Fill };
                host.Controls.Add(grid);
                grid.Attach(doc, settings);
                host.Show();
                Settle();

                prepare(doc, grid, settings);
                grid.RefreshView();
                Settle();

                using var bmp = new Bitmap(Width, Height);
                host.DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                bmp.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
                manifest.Append(name).Append(' ').Append(Fingerprint(bmp)).Append('\n');
                Console.WriteLine($"  {name,-28} {Fingerprint(bmp)}");

                host.Controls.Remove(grid);
                grid.Dispose();
                doc.Markers.Clear();
            }
        }
        finally
        {
            doc.Dispose();
            try { File.Delete(log); } catch (IOException) { }
            try { Directory.Delete(configDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        File.WriteAllText(Path.Combine(dir, "manifest.txt"), manifest.ToString());
        Console.WriteLine($"\nwrote {dir}");
        return 0;
    }

    /// <summary>Every state worth holding still: each one reaches a different corner of the drawing.</summary>
    private static IEnumerable<(string Name, Action<CascadeDocument, LineGridControl, AppSettings> Prepare)> Scenes()
    {
        yield return ("plain", (doc, grid, s) => { });
        yield return ("no-line-numbers", (doc, grid, s) => { s.ShowLineNumbers = false; grid.ApplySettings(s); });
        yield return ("filters-styled", (doc, grid, s) => doc.SetFilters(Styled()));
        yield return ("filters-hiding", (doc, grid, s) =>
        {
            var f = Styled();
            f.ShowOnlyFilteredLines = true;
            doc.SetFilters(f);
        });
        yield return ("scrolled-right", (doc, grid, s) => { doc.SetFilters(Styled()); grid.ScrollHorizontallyTo(220); });
        yield return ("scrolled-far-right", (doc, grid, s) => { doc.SetFilters(Styled()); grid.ScrollHorizontallyTo(760); });
        yield return ("word-wrap", (doc, grid, s) => { doc.SetFilters(Styled()); s.WordWrap = true; grid.ApplySettings(s); });
        yield return ("proportional-font", (doc, grid, s) =>
        {
            doc.SetFilters(Styled());
            s.FontFamily = "Segoe UI";
            grid.ApplySettings(s);
        });
        yield return ("big-font", (doc, grid, s) => { doc.SetFilters(Styled()); s.ZoomPercent = 175; grid.ApplySettings(s); });
        yield return ("small-font", (doc, grid, s) => { doc.SetFilters(Styled()); s.ZoomPercent = 75; grid.ApplySettings(s); });
        yield return ("columns", (doc, grid, s) => { doc.SetFilters(Styled()); Fields(doc, FieldLayout.Columns); });
        yield return ("columns-aligned", (doc, grid, s) =>
        {
            doc.SetFilters(Styled());
            Fields(doc, FieldLayout.Columns);
            for (int i = 0; i < doc.Columns.Columns.Count; i++) doc.Columns.Columns[i].Align = (ColumnAlign)(i % 3);
        });
        yield return ("columns-narrow", (doc, grid, s) =>
        {
            doc.SetFilters(Styled());
            Fields(doc, FieldLayout.Columns);
            foreach (var c in doc.Columns.Columns) c.Width = 70;   // narrow enough to want the ellipsis
        });
        yield return ("columns-scrolled", (doc, grid, s) =>
        {
            doc.SetFilters(Styled());
            Fields(doc, FieldLayout.Columns);
            grid.ScrollHorizontallyTo(180);
        });
        yield return ("inline-fields", (doc, grid, s) => { doc.SetFilters(Styled()); Fields(doc, FieldLayout.Inline); });
        yield return ("line-selection", (doc, grid, s) =>
        {
            doc.SetFilters(Styled());
            grid.ClickForTesting(2, 300);
        });
        yield return ("char-selection", (doc, grid, s) =>
        {
            doc.SetFilters(Styled());
            grid.DragForTesting(4, 180, 380);
        });
        yield return ("no-markers", (doc, grid, s) =>
        {
            doc.SetFilters(Styled());
            s.MarkerVisibility = MarkerVisibilityMode.Never;
            grid.ApplySettings(s);
        });
        yield return ("scrolled-down", (doc, grid, s) => { doc.SetFilters(Styled()); grid.ScrollToRow(9); });
    }

    /// <summary>Colours, weights and slopes over a fair share of the lines, so the styled faces are drawn.</summary>
    private static FilterCollection Styled()
    {
        var c = new FilterCollection();
        c.Roots.Add(new Filter
        {
            Enabled = true,
            Match = new FilterMatch { Text = "ERROR" },
            Style = { Background = new RgbColor(0xFF, 0xD0, 0xD0), Foreground = new RgbColor(0x80, 0, 0), Bold = true }
        });
        c.Roots.Add(new Filter
        {
            Enabled = true,
            Match = new FilterMatch { Text = "WARN" },
            Style = { Background = new RgbColor(0xFF, 0xF0, 0xC0), Italic = true }
        });
        c.Roots.Add(new Filter
        {
            Enabled = true,
            Match = new FilterMatch { Text = "auth-svc" },
            Style = { Foreground = new RgbColor(0, 0x60, 0xA0), Underline = true }
        });
        c.Roots.Add(new Filter
        {
            Enabled = true,
            Match = new FilterMatch { Text = "payment" },
            Style = { Foreground = new RgbColor(0x00, 0x70, 0x30) }
        });
        return c;
    }

    private static void Fields(CascadeDocument doc, FieldLayout layout)
    {
        doc.Columns.Enabled = true;
        doc.Columns.Layout = layout;
        doc.Columns.Template = "{*} {*} {[*]} {*} {*} {*}";
        doc.Columns.Columns.Clear();
        string[] names = ["Date", "Time", "Thread", "Level", "Service", "Message"];
        for (int i = 0; i < names.Length; i++)
            doc.Columns.Columns.Add(new ColumnDef { Name = names[i], Source = i, Width = 130 });
    }

    /// <summary>Lines that between them reach every road the drawing can take: plain ASCII, scripts that
    /// need shaping, wide characters, something past the basic plane, a tab, and a line far wider than the
    /// window.</summary>
    private static string Fixture()
    {
        string path = Path.Combine(Path.GetTempPath(), "cascade_rendershots_" + Guid.NewGuid().ToString("N") + ".log");
        var lines = new List<string>();
        string[] levels = ["INFO ", "WARN ", "ERROR", "DEBUG"];
        string[] services = ["payment-svc", "auth-svc   ", "inventory  ", "gateway    "];
        for (int i = 0; i < 14; i++)
            lines.Add($"2026-05-17 09:{i:00}:{i * 3 % 60:00}.{i * 7 % 1000:000} [Thread-{i % 9 + 1:00}] " +
                      $"{levels[i % levels.Length]} {services[i % services.Length]} " +
                      $"request id={i:0000}aa{i:00} user={1000 + i * 37} elapsed={i * 13 % 900}ms");
        lines.Add("2026-05-17 09:14:00.000 [Thread-03] INFO  gateway     \u043a\u0438\u0440\u0438\u043b\u043b\u0438\u0446\u0430 " +
                  "\u4f60\u597d\u4e16\u754c caf\u00e9 na\u00efve");
        lines.Add("2026-05-17 09:15:00.000 [Thread-04] WARN  auth-svc    emoji \U0001F600 and \u0442\u0435\u043a\u0441\u0442 mixed with ascii");
        lines.Add("2026-05-17 09:16:00.000 [Thread-05] ERROR payment-svc tab\there and more text after it");
        lines.Add("2026-05-17 09:17:00.000 [Thread-06] INFO  inventory   " + new string('W', 300));
        lines.Add("2026-05-17 09:18:00.000 [Thread-07] DEBUG gateway     " +
                  string.Join(" ", Enumerable.Range(0, 30).Select(n => $"k{n}=v{n}")));
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        return path;
    }

    private static string Fingerprint(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly,
            PixelFormat.Format32bppRgb);
        try
        {
            int bytes = Math.Abs(data.Stride) * bmp.Height;
            var buffer = new byte[bytes];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, bytes);
            return Convert.ToHexString(SHA256.HashData(buffer))[..16];
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>Lets the window finish laying out and painting. Long enough for anything the view defers.</summary>
    private static void Settle()
    {
        for (int i = 0; i < 6; i++)
        {
            Application.DoEvents();
            Thread.Sleep(40);
        }
        Application.DoEvents();
    }
}

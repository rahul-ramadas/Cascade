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

/// <summary>Part of <see cref="Checks"/>: the helpers more than one subject needs: reading pixels back off a control, pumping the message queue until it goes quiet, waiting for a filter pass, and recording a check.</summary>
internal static partial class Checks
{

    /// <summary>Whether anything at all was drawn in a stretch of the window, as against the background it
    /// would be if nothing had been.</summary>
    private static bool HasInk(Bitmap picture, Rectangle area, Color background)
    {
        for (int y = area.Top; y < area.Bottom && y < picture.Height; y++)
            for (int x = area.Left; x < area.Right && x < picture.Width; x++)
            {
                var c = picture.GetPixel(x, y);
                if (Math.Abs(c.R - background.R) + Math.Abs(c.G - background.G) + Math.Abs(c.B - background.B) > 40)
                    return true;
            }
        return false;
    }

    private static Bitmap CaptureControl(Control c)
    {
        var bmp = new Bitmap(Math.Max(1, c.Width), Math.Max(1, c.Height));
        c.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        return bmp;
    }

    private static void SetFilters(CascadeDocument doc, FilterTreeControl tree, params Filter[] filters)
    {
        var collection = new FilterCollection();
        foreach (var f in filters) collection.Roots.Add(f);
        doc.SetFilters(collection);
        tree.Rebuild();
        Pump();
    }

    /// <summary>Scrolling by fewer rows than one pixel of the map stands for must not move the window: the
    /// pixels sit on a fixed grid of the file, and re-dividing the rows between them for every line scrolled
    /// is what makes a map crawl about while you read.</summary>
    private static bool StillAfterTinyScroll(LineGridControl grid, MiniMapControl map)
    {
        long was = map.TopRowForTesting;
        long from = grid.FirstVisibleRow;
        grid.ScrollToRow(from + Math.Max(1, map.RowsPerPixelForTesting / 4));
        map.RebuildForTesting();
        bool still = map.TopRowForTesting == was;
        grid.ScrollToRow(from);
        map.RebuildForTesting();
        return still;
    }

    /// <summary>Pixels of exactly a colour inside a control's own rectangle - taken from the control rather
    /// than worked out from docking order, which is exactly the kind of guess that makes a test lie. Exact,
    /// because the marks are drawn solid and the viewport rectangle over them is not: a tolerance would
    /// count its tint as a mark.</summary>
    private static int CountColour(Form host, Rectangle r, Color want, bool leftEdgeOnly)
    {
        if (r.Width <= 0 || r.Height <= 0) return 0;
        using var picture = Capture(host);
        int right = leftEdgeOnly ? Math.Min(r.Left + 4, r.Right) : r.Right;
        int n = 0;
        for (int y = r.Top; y < r.Bottom && y < picture.Height; y++)
            for (int x = r.Left; x < right && x < picture.Width; x++)
                if (picture.GetPixel(x, y).ToArgb() == want.ToArgb()) n++;
        return n;
    }


    private static bool RowHasColor(Bitmap bmp, int left, int width, int y, int r, int g, int b)
    {
        if (y < 0 || y >= bmp.Height) return false;
        for (int x = Math.Max(0, left); x < Math.Min(bmp.Width, left + width); x++)
        {
            var c = bmp.GetPixel(x, y);
            if (Math.Abs(c.R - r) < 24 && Math.Abs(c.G - g) < 24 && Math.Abs(c.B - b) < 24) return true;
        }
        return false;
    }

    /// <summary>Waits for a filter pass to finish, so the per-filter caches the map reads exist.</summary>
    private static void WaitForFiltering(CascadeDocument doc)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (doc.IsBusy && DateTime.UtcNow < until) { Pump(); Thread.Sleep(5); }
        Pump();
    }

    /// <summary>Random gestures, each followed by a filter change, checked against a reference worked out
    /// from the document rather than from the view: whatever was selected must still be selected, and the
    /// number reported must be however many of those lines the view is showing.</summary>
    private static bool SelectionStress(LineGridControl grid, CascadeDocument doc, int lines,
                                        Action<string?> filter)
    {
        var terms = new string?[] { null, "TARGET", "handled okay", "#1", "cache" };
        var rnd = new Random(20260813);
        int gestures = 0;
        for (int iter = 0; iter < 60; iter++)
        {
            long rows = doc.RowCount;
            if (rows > 2)
            {
                long a = rnd.Next((int)rows), b = rnd.Next((int)rows);
                switch (rnd.Next(4))
                {
                    case 0: grid.ClickForTesting(a, 5); break;
                    case 1:
                        grid.PressForTesting(a, 5);
                        grid.DragOverRowForTesting(b, 5);
                        grid.ReleaseForTesting(b, 5);
                        break;
                    case 2: grid.SelectAll(); break;
                    default:
                        grid.DragForTesting(a, grid.XForCharForTesting(a, 1), grid.XForCharForTesting(a, 6));
                        break;
                }
                gestures++;
            }

            var before = new bool[lines];
            for (long line = 0; line < lines; line++) before[line] = grid.IsLineSelectedForTesting(line);
            string? pickedOut = grid.SelectedText;

            filter(terms[rnd.Next(terms.Length)]);

            // Nothing a filter does narrows or moves what was chosen. It is held in lines, so hiding one is
            // a question of what gets drawn - and putting it back shows it again, still selected.
            for (long line = 0; line < lines; line++)
                if (grid.IsLineSelectedForTesting(line) != before[line])
                    return Check($"the same lines stay selected (iteration {iter})", false,
                                 $"line {line}: {before[line]} -> {grid.IsLineSelectedForTesting(line)}");

            long shown = 0;
            for (long line = 0; line < lines; line++) if (before[line] && doc.IsLineVisible(line)) shown++;
            long standIn = grid.StandInLineForTesting;
            bool anyChosen = Array.IndexOf(before, true) >= 0;

            // With every chosen line hidden, one line stands in for them so the reader keeps their place -
            // and it has to be a line actually on show.
            if (anyChosen && shown == 0 && standIn < 0)
                return Check($"a wholly hidden selection leaves a stand-in (iteration {iter})", false,
                             "nothing is highlighted at all");
            if (standIn >= 0 && !doc.IsLineVisible(standIn))
                return Check($"the stand-in is a line being shown (iteration {iter})", false,
                             $"line {standIn} is not on show");
            if (shown > 0 && standIn >= 0)
                return Check($"no stand-in while any chosen line is on show (iteration {iter})", false,
                             $"{shown} shown, yet line {standIn} stands in");

            long counted = shown > 0 ? shown : (standIn >= 0 ? 1 : 0);
            if (grid.SelectedCount != counted)
                return Check($"the count is of the selected lines being shown (iteration {iter})", false,
                             $"said {grid.SelectedCount}, wanted {counted}");

            if (pickedOut is not null && grid.SelectedText != pickedOut)
                return Check($"the part of a line stays picked out (iteration {iter})", false,
                             $"'{pickedOut}' -> '{grid.SelectedText ?? "(none)"}'");
        }
        return Check($"{gestures} random selections all survive a filter change", true);
    }

    /// <summary>Which rows have anything drawn in the selection colour, and how much of each. Read off the
    /// picture rather than asked of the control: where the highlight LANDS is the whole complaint.</summary>
    private static List<(long Row, int Pixels)> SelectionRuns(LineGridControl grid, Form host, Bitmap picture,
                                                             AppSettings settings)
    {
        var found = new List<(long Row, int Pixels)>();
        int left = grid.GutterWidthForTesting + 2;
        int right = host.ClientSize.Width - grid.MapWidthForTesting - grid.ScrollBarWidthForTesting - 2;
        for (int i = 0; i < grid.RowsPaintedForTesting; i++)
        {
            long row = grid.FirstRowForTesting + i;
            int y = grid.RowMiddleForTesting(row), pixels = 0;
            for (int x = left; x < right; x += 2)
                if (IsBackground(picture, x, y, settings.SelectionBack)) pixels += 2;
            if (pixels > 0) found.Add((row, pixels));
        }
        return found;
    }

    private static bool SameColour(Color a, Color b) => a.ToArgb() == b.ToArgb();

    /// <summary>The background each row was painted in, read as the commonest colour along its middle. The
    /// glyphs are a minority of any row, so the mode is the fill - and it is the fill that says whether a
    /// filter coloured the row or nothing did.</summary>
    private static List<Color> RowBackgrounds(Bitmap shot, LineGridControl grid)
    {
        var rows = new List<Color>();
        int top = grid.GutterAreaForTesting.Top;
        int pitch = Math.Max(1, grid.RowHeightForTesting);
        int x0 = grid.GutterWidthForTesting + 4;
        int x1 = Math.Min(shot.Width, grid.GutterWidthForTesting + grid.ContentWidthForTesting) - 4;
        var counts = new Dictionary<int, int>();
        for (int i = 0; i < grid.RowsPaintedForTesting; i++)
        {
            int y = top + i * pitch + pitch / 2;
            if (y >= shot.Height || x1 <= x0) break;
            counts.Clear();
            for (int x = x0; x < x1; x += 3)
            {
                int argb = shot.GetPixel(x, y).ToArgb();
                counts[argb] = counts.GetValueOrDefault(argb) + 1;
            }
            int best = 0, bestCount = -1;
            foreach (var (argb, n) in counts) if (n > bestCount) { best = argb; bestCount = n; }
            rows.Add(Color.FromArgb(best));
        }
        return rows;
    }

    /// <summary>Asks a real dialog, the way a client asks, for the two objects a window can be reached
    /// through - at the window itself and at a control nested inside it.</summary>
    private static (long Window, long Nested, long Msaa) AskDialogForProviders(bool automationWanted)
    {
        const uint WmGetObject = 0x003D;
        const int UiaRootObjectId = -25;
        const int ObjIdClient = -4;

        using var scope = Automation.ForTesting(automationWanted);
        using var dlg = new FilterEditDialog(new Filter { Match = { Text = "probe" } }, isNew: true)
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Opacity = 0,
        };
        dlg.Show();
        Pump();

        var nested = Nest(dlg) ?? throw new InvalidOperationException("the filter dialog has no text box to ask");
        long window = (long)SendMessage(dlg.Handle, WmGetObject, IntPtr.Zero, UiaRootObjectId);
        long inside = (long)SendMessage(nested.Handle, WmGetObject, IntPtr.Zero, UiaRootObjectId);
        long msaa = (long)SendMessage(dlg.Handle, WmGetObject, IntPtr.Zero, ObjIdClient);
        dlg.Close();
        return (window, inside, msaa);

        static Control? Nest(Control parent)
        {
            foreach (Control c in parent.Controls)
                if (c is TextBox) return c;
                else if (Nest(c) is { } found) return found;
            return null;
        }
    }

    /// <summary>What fraction of the columns across a span of a row show a colour. Not "is any pixel that
    /// colour": ClearType puts a warm fringe on every dark glyph, and one of those is a close enough match
    /// to a soft highlight to answer yes anywhere in the line. A real highlight fills its whole span.</summary>
    private static double PixelFraction(Bitmap bmp, int x0, int x1, int y, Color colour)
    {
        int from = Math.Max(0, x0), to = Math.Min(bmp.Width, x1);
        if (to <= from) return 0;
        int hits = 0;
        for (int x = from; x < to; x++)
            for (int dy = -3; dy <= 3; dy++)
            {
                int yy = Math.Clamp(y + dy, 0, bmp.Height - 1);
                var c = bmp.GetPixel(x, yy);
                if (Math.Abs(c.R - colour.R) < 24 && Math.Abs(c.G - colour.G) < 24 && Math.Abs(c.B - colour.B) < 24) { hits++; break; }
            }
        return (double)hits / (to - from);
    }

    /// <summary>Whether a pixel is (close to) a given colour. A scanline through a row crosses glyphs, so a
    /// check about the BACKGROUND has to look at more than one pixel and take the commonest answer.</summary>
    private static bool IsBackground(Bitmap bmp, int x, int y, Color colour)
    {
        if (x < 0 || x >= bmp.Width || y < 0 || y >= bmp.Height) return false;
        int hits = 0;
        for (int dy = -2; dy <= 2; dy++)
        {
            int yy = Math.Clamp(y + dy, 0, bmp.Height - 1);
            var c = bmp.GetPixel(x, yy);
            if (Math.Abs(c.R - colour.R) < 30 && Math.Abs(c.G - colour.G) < 30 && Math.Abs(c.B - colour.B) < 30) hits++;
        }
        return hits >= 3;
    }

    /// <summary>Which rows of the elapsed column carry a figure, off a render of the grid, as one character
    /// per row. Read per row rather than as a pixel total: the claim is that each row still says how long it
    /// was, and a total would move with whichever digits happen to be on screen.</summary>
    private static string ElapsedFigures(Bitmap shot, LineGridControl grid)
    {
        var area = grid.GutterAreaForTesting;
        int from = grid.ElapsedGutterLeftForTesting, width = grid.ElapsedGutterWidthForTesting;
        if (width <= 0 || area.Height <= 0) return "";
        var rows = new StringBuilder();
        int pitch = Math.Max(1, grid.RowPitch);
        for (int top = area.Top; top + pitch <= area.Bottom && top + pitch <= shot.Height; top += pitch)
        {
            bool inked = false;
            for (int y = top; y < top + pitch && !inked; y++)
                for (int x = from; x < from + width && x < shot.Width; x++)
                {
                    var c = shot.GetPixel(x, y);
                    if ((c.R + c.G + c.B) / 3 < 200) { inked = true; break; }
                }
            rows.Append(inked ? '#' : '.');
        }
        return rows.ToString();
    }

    /// <summary>How far the widest line number's ink sits from each end of the box it is drawn in, read off
    /// a render of the grid. The arithmetic says how wide the box is; only the pixels say where in it the
    /// digits ended up. Rows carrying something other than a line number - the caret's row, and the focus
    /// accent down the left edge - are passed over: neither is the margin.</summary>
    private static (int Left, int Right) MarginAir(LineGridControl grid, int from, int width)
    {
        var area = grid.GutterAreaForTesting;
        if (width <= 0 || area.Height <= 0) return (-1, -1);
        using var bmp = new Bitmap(grid.Width, grid.Height);
        grid.DrawToBitmap(bmp, new Rectangle(0, 0, grid.Width, grid.Height));

        static bool Ink(Color c) => (c.R + c.G + c.B) / 3 < 200;
        int first = int.MaxValue, last = -1;
        for (int y = area.Top; y < area.Bottom && y < grid.Height; y++)
        {
            // A run reaching the very left edge is the focus accent, not a digit; a row inked end to end is
            // a fill rather than glyphs. Either way there is no number in it to measure.
            if (Ink(bmp.GetPixel(from, y))) continue;
            for (int x = from; x < from + width && x < grid.Width; x++)
            {
                if (!Ink(bmp.GetPixel(x, y))) continue;
                if (x < first) first = x;
                if (x > last) last = x;
            }
        }
        return last < 0 ? (-1, -1) : (first - from, from + width - 1 - last);
    }

    private static int Figure(string layout, string key)
    {
        int at = layout.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return -1;
        int from = at + key.Length, to = from;
        while (to < layout.Length && (char.IsAsciiDigit(layout[to]) || layout[to] == '-')) to++;
        return int.TryParse(layout[from..to], out int n) ? n : -1;
    }

    /// <summary>Waits for a freshly opened document to finish indexing, pumping so the window keeps up.</summary>
    private static bool Settle(CascadeDocument doc)
    {
        for (int i = 0; i < 200 && !doc.IsIndexComplete; i++) { Thread.Sleep(10); Pump(); }
        Pump();
        return doc.IsIndexComplete;
    }

    /// <summary>The letter Alt activates for a caption, or null when it declares none.</summary>
    private static char? MnemonicOf(string text)
    {
        int i = text.IndexOf('&');
        return i >= 0 && i + 1 < text.Length && text[i + 1] != '&' ? char.ToLowerInvariant(text[i + 1]) : null;
    }

    /// <summary>The clipboard is shared with everything else running, so a read can simply fail - and it
    /// does not always fail LOUDLY: while another process has it open, ContainsText answers no rather than
    /// throwing. Believing that would report a copy that did happen as a copy that did not, which was
    /// measured as one run in six going red on nothing. So an empty answer is waited out too, and only an
    /// empty one that outlasts the wait is passed on - the caller treats that as "could not read".</summary>
    private static string SafeClipboardText()
    {
        for (int i = 0; i < 10; i++)
        {
            try { if (Clipboard.ContainsText()) return Clipboard.GetText(); }
            catch { /* another process has it open; the wait below is the whole remedy */ }
            Thread.Sleep(60);
        }
        return "";
    }

    /// <summary>The filter list's share of a divider's travel, worked out the way the app records it: from
    /// the list's own side, whichever of the two panels that happens to be on the edge it is docked to.
    /// </summary>
    private static double Share(SplitContainer split, bool listIsFirstPanel)
    {
        int total = (split.Orientation == Orientation.Vertical ? split.Width : split.Height) - split.SplitterWidth;
        if (total <= 0) return 0;
        return (listIsFirstPanel ? split.SplitterDistance : total - split.SplitterDistance) / (double)total;
    }

    /// <summary>A main window exactly as a user gets one. The off-screen parking the UI suite asks for is
    /// stood down for it: parked beyond the last monitor there is nothing to snap to, and it is the
    /// untouched constructor that is on trial here. Nothing appears - the window is transparent.</summary>
    private static MainForm ShippedWindow()
    {
        string? offScreen = Environment.GetEnvironmentVariable("CASCADE_TEST_OFFSCREEN");
        Environment.SetEnvironmentVariable("CASCADE_TEST_OFFSCREEN", null);
        try { return new MainForm(new AppSettings(), new MachineState(), Array.Empty<string>()) { Opacity = 0, NoSavePrompt = true }; }
        finally { Environment.SetEnvironmentVariable("CASCADE_TEST_OFFSCREEN", offScreen); }
    }

    private const int SW_SHOWNOACTIVATE = 4;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public static RECT From(Rectangle r) => new() { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int Length, Flags, ShowCmd;
        public Point MinPosition, MaxPosition;
        public RECT NormalPosition;
    }

    private static DataObject Files(params string[] paths) => new(DataFormats.FileDrop, paths);

    private static DragEventArgs DragArgs(IDataObject data)
        => new(data, 0, 0, 0, DragDropEffects.All, DragDropEffects.None);

    /// <summary>What the control would do with this drag, asked the way Windows asks: DragOver is what
    /// decides the cursor while the pointer is over it.</summary>
    private static DragDropEffects EffectOfDragOver(Control target, IDataObject data)
    {
        var e = DragArgs(data);
        RaiseDragEvent(target, "OnDragOver", e);
        return e.Effect;
    }

    private static int IndexOfPair(IReadOnlyList<LuckyColors.Pair> list, LuckyColors.Pair want)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == want) return i;
        return -1;
    }

    private static bool IsInOrder(List<int> values)
    {
        for (int i = 1; i < values.Count; i++)
            if (values[i] <= values[i - 1]) return false;
        return true;
    }

    private static IEnumerable<ToolStripMenuItem> AllMenuItems(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
            if (item is ToolStripMenuItem m)
            {
                yield return m;
                foreach (var d in AllMenuItems(m.DropDownItems)) yield return d;
            }
    }

    private static IEnumerable<Control> AllControls(Control root)
    {
        foreach (Control c in root.Controls)
        {
            yield return c;
            foreach (var d in AllControls(c)) yield return d;
        }
    }

    /// <summary>How much of a progress bar's width is actually coloured in, 0..1.</summary>
    private static double PaintedFraction(ProgressBar bar)
    {
        using var bmp = new Bitmap(Math.Max(1, bar.Width), Math.Max(1, bar.Height));
        bar.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        int y = bmp.Height / 2;
        var empty = bmp.GetPixel(bmp.Width - 2, y);   // the far end, which 80% never reaches
        int filled = 0;
        for (int x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, y);
            if (Math.Abs(c.R - empty.R) + Math.Abs(c.G - empty.G) + Math.Abs(c.B - empty.B) > 40) filled++;
        }
        return (double)filled / bmp.Width;
    }

    private static Bitmap Capture(Form host)    {
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

    private static void WriteRenderDiagnostics(string check, Form host, LineGridControl grid, Rectangle area,
        Point diff, Bitmap before, Bitmap after)
    {
        try
        {
            TryDiagnosticLine("  render diagnostics:");
            WriteDiagnosticValue("check", () => check);
            WriteDiagnosticValue("terminal server session", () => SystemInformation.TerminalServerSession.ToString());
            WriteDiagnosticValue("dpi", () =>
            {
                using var graphics = host.CreateGraphics();
                return $"device={host.DeviceDpi}, graphics={graphics.DpiX:F1}x{graphics.DpiY:F1}";
            });
            WriteDiagnosticValue("host", () => $"client={host.ClientSize.Width}x{host.ClientSize.Height}, bounds={host.Bounds}");
            WriteDiagnosticValue("grid metrics", () =>
                $"gutter={grid.GutterWidthForTesting}, gutterTop/headerHeight={area.Top}, " +
                $"rowHeight={PrivateInt(grid, "_rowHeight")?.ToString() ?? "unknown"}, gutterArea={area}");
            WriteDiagnosticValue("first difference", () =>
            {
                Color beforePixel = before.GetPixel(diff.X, diff.Y);
                Color afterPixel = after.GetPixel(diff.X, diff.Y);
                return $"x={diff.X}, y={diff.Y}, before=0x{beforePixel.ToArgb():X8} ({beforePixel}), " +
                       $"after=0x{afterPixel.ToArgb():X8} ({afterPixel})";
            });
            WriteScreenDiagnostics();
            WriteDiagnosticValue("font smoothing", FontSmoothingSettings);
        }
        catch (Exception ex)
        {
            TryDiagnosticLine("  render diagnostics unavailable: " + ExceptionSummary(ex));
        }
    }

    private static void WriteDiagnosticValue(string label, Func<string> value)
    {
        try { TryDiagnosticLine($"    {label}: {value()}"); }
        catch (Exception ex) { TryDiagnosticLine($"    {label}: diagnostics unavailable ({ExceptionSummary(ex)})"); }
    }

    private static void TryDiagnosticLine(string text)
    {
        try { Line(text); }
        catch { }
    }

    private static void WriteScreenDiagnostics()
    {
        Screen[] screens;
        try { screens = Screen.AllScreens; }
        catch (Exception ex)
        {
            TryDiagnosticLine("    screens: diagnostics unavailable (" + ExceptionSummary(ex) + ")");
            return;
        }

        for (int i = 0; i < screens.Length; i++)
        {
            Screen screen = screens[i];
            WriteDiagnosticValue("screen", () =>
                $"{screen.DeviceName}, primary={screen.Primary}, bounds={screen.Bounds}, bpp={screen.BitsPerPixel}");
        }
    }

    private static string ExceptionSummary(Exception ex) => ex.GetType().Name + ": " + ex.Message;

    private static int? PrivateInt(object target, string fieldName)
    {
        object? value = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);
        return value is int i ? i : null;
    }

    private static string FontSmoothingSettings()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            if (key is null) return "registry key unavailable";
            string Value(string name) => $"{name}={key.GetValue(name) ?? "unset"}";
            return string.Join(", ", Value("FontSmoothing"), Value("FontSmoothingType"),
                Value("FontSmoothingGamma"), Value("FontSmoothingOrientation"));
        }
        catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
    }

    /// <summary>Lets the UI finish whatever it has queued, and comes back as soon as it goes quiet. It used
    /// to wait a flat 250ms every time; the drag checks alone call it about 190 times, so nearly the whole
    /// self-test was spent sitting idle. The old wait is kept as the cap.
    /// <para>Quiet is not the same as finished: the last frame of a drag is drawn by a timer, and a timer
    /// arrives when Windows feels like it - up to two of its ticks later. So the view is asked whether it
    /// still owes a frame, rather than the wait being lengthened to cover the worst case, which would be
    /// paid by every one of the thousand calls this makes.</para></summary>
    private static void Pump()
    {
        // Almost nothing here activates a window, but the few things that must - a modal dialog, a common
        // dialog - would otherwise leave the foreground in this process for the rest of the run. This is
        // the one call every check makes often enough to put it back at once. See Infrastructure/Foreground.
        Foreground.Release();

        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 250;)
        {
            Application.DoEvents();
            if (PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE)) { Thread.Sleep(1); continue; }
            if (LineGridControl.AnyViewOwesAFrameForTesting) { Thread.Sleep(1); continue; }
            // Quiet once is not the same as settled - a timer may be about to post. Ask again after a pause.
            Thread.Sleep(15);
            Application.DoEvents();
            if (!PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE)) return;
        }
    }

    private const uint PM_NOREMOVE = 0;
    private const uint WM_KEYDOWN = 0x0100;

    /// <summary>How many GDI handles this process is holding. Drawing straight onto a device context means
    /// owning faces and brushes, which nothing collects.</summary>
    private static int GdiHandles()
        => (int)GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0);


    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
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
        Line((condition ? PassMarker : FailMarker) + name);
        return condition;
    }

    /// <summary>Same, but reports what was actually seen when it fails - which is the difference between a
    /// failure you can act on and one you have to reproduce first.</summary>
    private static bool Check(string name, bool condition, string detail)
    {
        Line((condition ? PassMarker : FailMarker) + name + (condition ? "" : $" [{detail}]"));
        return condition;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>Records what a check saw. Everything a group writes is kept and handed to xUnit, so a
    /// failure carries the whole story of the group rather than one assertion out of context.</summary>
    private static void Line(string text) => _captured?.Add(text);
}

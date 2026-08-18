using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Cascade.Core.Columns;
using Cascade.Core.Model;

namespace Cascade.App;

/// <summary>
/// What one hand-drag of the vertical scrollbar costs, measured rather than argued about. Run with
/// <c>Cascade.exe --scrollbench</c>.
///
/// <para>A drag is the worst thing that happens to this program on an ordinary afternoon: every mouse
/// report moves the view a page or more, and every one of those has to repaint the text, the minimap and
/// the bar itself before the next report is looked at. The interesting number is therefore not frames per
/// second but PROCESSOR TIME PER MOUSE REPORT - a mouse reports 125 times a second whatever the program
/// does with them, so that figure times 125 is the CPU the drag will burn.</para>
///
/// <para>The drag is driven the way the mouse drives it: one WM_MOUSEMOVE at a time, then the message
/// queue is run dry before the next one is posted. That is exactly what a real drag does, because Windows
/// coalesces mouse moves - a program that is slow to answer simply sees fewer of them - and it means one
/// iteration here is one whole frame, deferred repaints and all.</para>
/// </summary>
internal static class ScrollBench
{
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const uint PmNoRemove = 0;
    private const uint PmRemove = 1;

    public static int Run(string[] args)
    {
        int lines = IntArg(args, "--lines=", 1_000_000);
        int steps = IntArg(args, "--steps=", 400);
        int jump = IntArg(args, "--jump=", 24);
        int width = IntArg(args, "--width=", 1600);
        int height = IntArg(args, "--height=", 1200);
        int repeats = IntArg(args, "--repeat=", 3);
        int seconds = IntArg(args, "--seconds=", 0);
        int payload = IntArg(args, "--payload=", 0);
        // Mouse reports a second. Zero means "one at a time, each waited for", which measures what a report
        // costs; a real figure measures what a mouse of that speed does to the program.
        int rate = IntArg(args, "--rate=", 0);
        string only = Arg(args, "--only=") ?? "";
        bool parts = args.Any(a => a.Equals("--parts", StringComparison.OrdinalIgnoreCase));
        bool micro = args.Any(a => a.Equals("--micro", StringComparison.OrdinalIgnoreCase));
        // A drag over ground the minimap has never been over, which is what the first pass down a file is.
        bool cold = args.Any(a => a.Equals("--cold", StringComparison.OrdinalIgnoreCase));
        // Every piece of text through the general layout, as it went before the direct path existed. The
        // before half of a before-and-after.
        bool longWay = args.Any(a => a.Equals("--longway", StringComparison.OrdinalIgnoreCase));

        // Measuring against whatever else the machine feels like doing is how a change of 20% hides inside
        // the noise. This asks for the scheduler's attention for the couple of minutes it runs.
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; }
        catch (SystemException) { /* not allowed to; the numbers will simply be noisier */ }
        BeginTimePeriod(1);

        // A window of this program writes preferences and recent-file lists as it goes. Pointed at the
        // developer's own directory a benchmark would quietly rewrite them.
        string configDir = Path.Combine(Path.GetTempPath(), "cascade_bench_cfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDir);
        Environment.SetEnvironmentVariable("CASCADE_SETTINGS_DIR", configDir);

        string path = Fixture(lines, payload);
        Console.WriteLine($"file: {path} ({lines:N0} lines)");
        Console.WriteLine($"window: {width}x{height}, {steps} moves of {jump}px, {repeats} runs each");
        Console.WriteLine();

        var form = new MainForm(new AppSettings(), new MachineState(), [path]) { NoSavePrompt = true };
        try
        {
            // On a real monitor, but invisible: a window parked beyond the last screen is clipped away
            // entirely and Windows never sends it a WM_PAINT, so there would be nothing to measure. Fully
            // transparent it is composited exactly as an ordinary window is, and paints every frame.
            form.StartPosition = FormStartPosition.Manual;
            form.WindowState = FormWindowState.Normal;
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Location = new Point(0, 0);
            form.ClientSize = new Size(width, height);
            form.Show();
            Pump();
            form.ClientSize = new Size(width, height);
            form.PerformLayout();
            Pump();

            var doc = form.DocForTesting;
            for (var wait = Stopwatch.StartNew(); wait.ElapsedMilliseconds < 120_000 && doc.IsBusy;) Pump();

            var probe = form.GridForTesting;
            probe.DrawTextTheLongWayForTesting = longWay;
            string sample = doc.GetLineText(doc.CompletedLineCount / 2);
            Console.WriteLine($"indexed {doc.CompletedLineCount:N0} lines, {probe.VisibleRowCountForTesting} rows on screen, " +
                              $"row {probe.RowHeightForTesting}px tall");
            Console.WriteLine($"client {form.ClientSize}, grid {probe.Bounds}, gutter {probe.GutterWidthForTesting}px, " +
                              $"map {probe.MapWidthForTesting}px, bar {probe.ScrollBarWidthForTesting}px");
            Console.WriteLine($"a line is {sample.Length} characters and {probe.DrawnWidthForTesting(sample, 0)}px wide, " +
                              $"in {probe.Bounds.Width - probe.GutterWidthForTesting - probe.MapWidthForTesting - probe.ScrollBarWidthForTesting}px of room");
            Console.WriteLine();

            if (micro)
            {
                // Against filters, because that is the state a reader is in - and the paint costs more in it.
                foreach (var (name, prepare) in Scenarios(form))
                {
                    if (!name.Contains("dim", StringComparison.OrdinalIgnoreCase)) continue;
                    prepare();
                    for (var wait = Stopwatch.StartNew(); wait.ElapsedMilliseconds < 120_000 && doc.IsBusy;) Pump();
                    Pump();
                }
                DrawingWays(form);
                return 0;
            }

            foreach (var (name, prepare) in Scenarios(form))
            {
                if (only.Length > 0 && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
                prepare();
                for (var wait = Stopwatch.StartNew(); wait.ElapsedMilliseconds < 120_000 && doc.IsBusy;) Pump();
                Pump();

                Drag(form, steps: 40, jump);   // warm the caches this scenario will lean on
                if (cold) { form.GridForTesting.InvalidateMatchMap(); Pump(); }
                if (seconds > 0)
                {
                    // A profiler needs something to sample, so drag for as long as it is being watched.
                    Console.WriteLine($"  {name,-22} dragging for {seconds}s");
                    for (var clock = Stopwatch.StartNew(); clock.Elapsed.TotalSeconds < seconds;) Drag(form, steps, jump, rate);
                    continue;
                }
                var best = Measurement.Worst;
                for (int i = 0; i < repeats; i++)
                {
                    if (cold) { form.GridForTesting.InvalidateMatchMap(); Pump(); }
                    var run = Drag(form, steps, jump, rate);
                    Console.WriteLine($"  {name,-22} {run}");
                    if (run.CpuPerFrameMs < best.CpuPerFrameMs) best = run;
                }
                Console.WriteLine($"  {name,-22} BEST {best}");
                if (parts) Parts(form, steps);
                Console.WriteLine();
            }
        }
        finally
        {
            form.Dispose();
            EndTimePeriod(1);
            try { Directory.Delete(configDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return 0;
    }

    /// <summary>
    /// The ways a screenful of text can be put on a device context, timed against each other. The frame is
    /// dominated by GDI text, so what matters is how much of that is the glyphs themselves and how much is
    /// the road each call takes to reach them - a GDI+ Graphics hands its device context out and takes it
    /// back on every single call, and applies the clip region while it is at it.
    /// </summary>
    private static void DrawingWays(MainForm form)
    {
        var grid = form.GridForTesting;
        var doc = form.DocForTesting;
        var font = grid.FontForTesting;
        int rowHeight = grid.RowHeightForTesting;
        int rows = Math.Max(1, grid.VisibleRowCountForTesting);
        int width = Math.Max(1, grid.Bounds.Width), height = Math.Max(1, grid.Bounds.Height);
        int gutter = grid.GutterWidthForTesting;

        var lines = new string[rows];
        var numbers = new string[rows];
        for (int i = 0; i < rows; i++) { lines[i] = doc.GetLineText(i); numbers[i] = (i + 1).ToString(CultureInfo.InvariantCulture); }
        Console.WriteLine($"  a screenful is {rows} rows of {lines[0].Length} characters, {width}x{height}");

        // What a screenful costs before a pixel is drawn: decoding the lines out of the mapped file and
        // asking the filters what colour each of them is.
        Time("reading and colouring a screenful", () =>
        {
            var colouring = doc.ColouringSnapshot();
            for (int i = 0; i < rows; i++)
            {
                string text = doc.GetLineText(i);
                colouring.Evaluate(text, i);
            }
        });

        // And what the minimap asks for on a mouse report of a drag over ground it has not seen: the
        // colour of every row the window slid over, worked out across the cores.
        foreach (int many in (int[])[500, 5_500, 30_000])
        {
            var want = new long[many];
            var got = new Filter?[many];
            for (int i = 0; i < many; i++) want[i] = i * 7 % Math.Max(1, doc.CompletedLineCount);
            TimeWithCpu($"colouring {many:N0} rows the map's way", () => doc.ColouringFilters(want, many, got), 60);
        }

        Time("the whole paint, as it stands", () => { grid.Invalidate(); grid.Update(); });

        const TextFormatFlags Today = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.PreserveGraphicsClipping;
        const TextFormatFlags Plain = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        var fore = Color.Black;
        var back = Color.White;

        // On a Graphics made over the window's own device context, as a paint gets - not one made over a
        // Bitmap, where every GetHdc has to reconcile GDI+'s idea of the pixels with GDI's and costs ten
        // times what the drawing does.
        using var canvas = grid.CreateGraphics();
        using (var wipe = new SolidBrush(back)) canvas.FillRectangle(wipe, 0, 0, width, height);

        Time("as today (Graphics, clip preserved)", () =>
        {
            for (int i = 0; i < rows; i++)
            {
                canvas.SetClip(new Rectangle(gutter, i * rowHeight, width - gutter, rowHeight));
                TextRenderer.DrawText(canvas, lines[i], font, new Point(gutter, i * rowHeight), fore, Today);
                canvas.ResetClip();
                TextRenderer.DrawText(canvas, numbers[i], font, new Point(0, i * rowHeight), fore, Plain);
            }
        });

        Time("Graphics, no clip at all", () =>
        {
            for (int i = 0; i < rows; i++)
            {
                TextRenderer.DrawText(canvas, lines[i], font, new Point(gutter, i * rowHeight), fore, Plain);
                TextRenderer.DrawText(canvas, numbers[i], font, new Point(0, i * rowHeight), fore, Plain);
            }
        });

        // The same calls, but told what is behind the text instead of being left to blend with whatever it
        // finds there. ClearType over an unknown background has to read every destination pixel back.
        Time("Graphics, text over its own fill", () =>
        {
            for (int i = 0; i < rows; i++)
            {
                TextRenderer.DrawText(canvas, lines[i], font,
                    new Rectangle(gutter, i * rowHeight, width - gutter, rowHeight), fore, back, Plain);
                TextRenderer.DrawText(canvas, numbers[i], font,
                    new Rectangle(0, i * rowHeight, gutter, rowHeight), fore, back, Plain | TextFormatFlags.Right);
            }
        });

        IntPtr hfont = font.ToHfont();
        try
        {
            Time("one device context, DrawTextEx", () =>
            {
                IntPtr hdc = canvas.GetHdc();
                try
                {
                    IntPtr was = SelectObject(hdc, hfont);
                    SetBkMode(hdc, TransparentBackground);
                    SetTextColor(hdc, ColorRef(fore));
                    for (int i = 0; i < rows; i++)
                    {
                        var rect = new Rect(gutter, i * rowHeight, width, (i + 1) * rowHeight);
                        DrawTextEx(hdc, lines[i], lines[i].Length, ref rect, DtSingleLine | DtNoPrefix | DtNoClip, IntPtr.Zero);
                        var numberRect = new Rect(0, i * rowHeight, gutter, (i + 1) * rowHeight);
                        DrawTextEx(hdc, numbers[i], numbers[i].Length, ref numberRect, DtSingleLine | DtNoPrefix | DtRight, IntPtr.Zero);
                    }
                    SelectObject(hdc, was);
                }
                finally { canvas.ReleaseHdc(hdc); }
            });

            Time("one device context, ExtTextOut", () =>
            {
                IntPtr hdc = canvas.GetHdc();
                try
                {
                    IntPtr was = SelectObject(hdc, hfont);
                    SetBkMode(hdc, TransparentBackground);
                    SetTextColor(hdc, ColorRef(fore));
                    for (int i = 0; i < rows; i++)
                    {
                        ExtTextOut(hdc, gutter, i * rowHeight, 0, IntPtr.Zero, lines[i], (uint)lines[i].Length, IntPtr.Zero);
                        ExtTextOut(hdc, 0, i * rowHeight, 0, IntPtr.Zero, numbers[i], (uint)numbers[i].Length, IntPtr.Zero);
                    }
                    SelectObject(hdc, was);
                }
                finally { canvas.ReleaseHdc(hdc); }
            });

            Time("one device context, DrawTextEx over its own fill", () =>
            {
                IntPtr hdc = canvas.GetHdc();
                try
                {
                    IntPtr was = SelectObject(hdc, hfont);
                    SetBkMode(hdc, OpaqueBackground);
                    SetBkColor(hdc, ColorRef(back));
                    SetTextColor(hdc, ColorRef(fore));
                    for (int i = 0; i < rows; i++)
                    {
                        var rect = new Rect(gutter, i * rowHeight, width, (i + 1) * rowHeight);
                        DrawTextEx(hdc, lines[i], lines[i].Length, ref rect, DtSingleLine | DtNoPrefix | DtNoClip, IntPtr.Zero);
                        var numberRect = new Rect(0, i * rowHeight, gutter, (i + 1) * rowHeight);
                        DrawTextEx(hdc, numbers[i], numbers[i].Length, ref numberRect, DtSingleLine | DtNoPrefix | DtRight, IntPtr.Zero);
                    }
                    SelectObject(hdc, was);
                }
                finally { canvas.ReleaseHdc(hdc); }
            });

            Time("one device context, TextRenderer over its fill", () =>
            {
                IntPtr hdc = canvas.GetHdc();
                try
                {
                    var held = new HeldDc(hdc);
                    for (int i = 0; i < rows; i++)
                    {
                        TextRenderer.DrawText(held, lines[i], font,
                            new Rectangle(gutter, i * rowHeight, width - gutter, rowHeight), fore, back, Plain);
                        TextRenderer.DrawText(held, numbers[i], font,
                            new Rectangle(0, i * rowHeight, gutter, rowHeight), fore, back, Plain | TextFormatFlags.Right);
                    }
                }
                finally { canvas.ReleaseHdc(hdc); }
            });

            Time("one device context, text over its own fill", () =>
            {
                IntPtr hdc = canvas.GetHdc();
                try
                {
                    IntPtr was = SelectObject(hdc, hfont);
                    SetBkMode(hdc, OpaqueBackground);
                    SetBkColor(hdc, ColorRef(back));
                    SetTextColor(hdc, ColorRef(fore));
                    for (int i = 0; i < rows; i++)
                    {
                        var rect = new Rect(gutter, i * rowHeight, width, (i + 1) * rowHeight);
                        ExtTextOut(hdc, gutter, i * rowHeight, EtoOpaque, ref rect, lines[i], (uint)lines[i].Length, IntPtr.Zero);
                        var numberRect = new Rect(0, i * rowHeight, gutter, (i + 1) * rowHeight);
                        ExtTextOut(hdc, 0, i * rowHeight, EtoOpaque, ref numberRect, numbers[i], (uint)numbers[i].Length, IntPtr.Zero);
                    }
                    SelectObject(hdc, was);
                }
                finally { canvas.ReleaseHdc(hdc); }
            });
        }
        finally { DeleteObject(hfont); }

        using var brush = new SolidBrush(back);
        Time("the fills alone, Graphics", () =>
        {
            for (int i = 0; i < rows; i++)
            {
                canvas.FillRectangle(brush, 0, i * rowHeight, width, rowHeight);
                canvas.FillRectangle(brush, 0, i * rowHeight, gutter, rowHeight);
            }
        });

        IntPtr hbrush = CreateSolidBrush(ColorRef(back));
        try
        {
            Time("the fills alone, one device context", () =>
            {
                IntPtr hdc = canvas.GetHdc();
                try
                {
                    for (int i = 0; i < rows; i++)
                    {
                        var rect = new Rect(0, i * rowHeight, width, (i + 1) * rowHeight);
                        FillRect(hdc, ref rect, hbrush);
                        var gutterRect = new Rect(0, i * rowHeight, gutter, (i + 1) * rowHeight);
                        FillRect(hdc, ref gutterRect, hbrush);
                    }
                }
                finally { canvas.ReleaseHdc(hdc); }
            });
        }
        finally { DeleteObject(hbrush); }

        static void Time(string what, Action frame)
        {
            for (int i = 0; i < 20; i++) frame();
            var clock = Stopwatch.StartNew();
            const int Frames = 200;
            for (int i = 0; i < Frames; i++) frame();
            clock.Stop();
            Console.WriteLine($"      {what,-42} {clock.Elapsed.TotalMilliseconds / Frames,6:F2} ms/screenful");
        }

        /// <summary>The same, for work that is shared out across the cores - where the wall clock says how
        /// long the reader waits and the processor time says what it cost the machine.</summary>
        static void TimeWithCpu(string what, Action work, int times)
        {
            for (int i = 0; i < 10; i++) work();
            var process = Process.GetCurrentProcess();
            long cpu0 = process.TotalProcessorTime.Ticks;
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < times; i++) work();
            clock.Stop();
            double cpu = (process.TotalProcessorTime.Ticks - cpu0) / (double)TimeSpan.TicksPerMillisecond / times;
            Console.WriteLine($"      {what,-42} {clock.Elapsed.TotalMilliseconds / times,6:F2} ms wall | {cpu,6:F2} ms cpu");
        }
    }

    /// <summary>A device context someone else owns, handed to <see cref="TextRenderer"/> so it does not
    /// fetch and return one of its own on every call.</summary>
    private sealed class HeldDc(IntPtr hdc) : IDeviceContext
    {
        public IntPtr GetHdc() => hdc;
        public void ReleaseHdc() { }
        public void Dispose() { }
    }

    /// <summary>The states worth measuring, each set up through the same wiring the menus use.</summary>
    private static IEnumerable<(string Name, Action Prepare)> Scenarios(MainForm form)    {
        var doc = form.DocForTesting;
        yield return ("no filters", () =>
        {
            doc.Filters.ShowOnlyFilteredLines = false;
            doc.SetFilters(new FilterCollection());
        });
        yield return ("dim mode", () =>
        {
            doc.Filters.ShowOnlyFilteredLines = false;
            doc.SetFilters(Filters());
        });
        yield return ("filtered mode", () =>
        {
            var filters = Filters();
            filters.ShowOnlyFilteredLines = true;
            doc.SetFilters(filters);
        });
        yield return ("fields, in columns", () =>
        {
            doc.Filters.ShowOnlyFilteredLines = false;
            doc.SetFilters(Filters());
            SplitIntoFields(doc, FieldLayout.Columns);
            form.GridForTesting.RefreshView();
        });
        yield return ("fields, inline", () =>
        {
            SplitIntoFields(doc, FieldLayout.Inline);
            form.GridForTesting.RefreshView();
        });
        yield return ("word wrap", () =>
        {
            doc.Columns.Enabled = false;
            form.GridForTesting.ApplySettings(new AppSettings { WordWrap = true });
            form.GridForTesting.RefreshView();
        });
    }

    /// <summary>Splits the fixture's lines the way a reader would: a field per part of the line, under the
    /// template that describes it.</summary>
    private static void SplitIntoFields(Cascade.Core.Document.CascadeDocument doc, FieldLayout layout)
    {
        doc.Columns.Enabled = true;
        doc.Columns.Layout = layout;
        doc.Columns.Template = "{*} {*} {[*]} {*} {*} {*}";
        doc.Columns.Columns.Clear();
        string[] names = ["Date", "Time", "Thread", "Level", "Service", "Message"];
        for (int i = 0; i < names.Length; i++)
            doc.Columns.Columns.Add(new ColumnDef { Name = names[i], Source = i });   // 0: sized to fit
    }

    /// <summary>A filter set of the shape people actually keep: a few colours over a fair share of the
    /// file, one of them nested under another.</summary>
    private static FilterCollection Filters()
    {
        var collection = new FilterCollection();
        var error = new Filter
        {
            Enabled = true,
            Match = new FilterMatch { Text = "ERROR" },
            Style = { Background = new RgbColor(0xFF, 0xD0, 0xD0), Foreground = new RgbColor(0x80, 0x00, 0x00) }
        };
        var warn = new Filter
        {
            Enabled = true,
            Match = new FilterMatch { Text = "WARN" },
            Style = { Background = new RgbColor(0xFF, 0xF0, 0xC0) }
        };
        var payment = new Filter
        {
            Enabled = true,
            Match = new FilterMatch { Text = "payment-svc" },
            Style = { Foreground = new RgbColor(0x00, 0x60, 0xA0) }
        };
        var slow = new Filter
        {
            Enabled = true,
            Match = new FilterMatch { Text = "elapsed=9" },
            Style = { Bold = true }
        };
        payment.Children.Add(slow);
        collection.Roots.Add(error);
        collection.Roots.Add(warn);
        collection.Roots.Add(payment);
        return collection;
    }

    /// <summary>Drags the thumb up and down the bar, one mouse report at a time.</summary>
    private static Measurement Drag(MainForm form, int steps, int jump, int rate = 0)
    {
        var grid = form.GridForTesting;
        var bar = grid.ScrollBarForTesting;
        var map = grid.MatchMapForTesting;
        var track = bar.TroughForTesting;

        // Always from the top, so a run starts from the same place whatever the one before it left behind -
        // and so the press lands on the thumb rather than in the trough, which is a page-scroll, not a drag.
        grid.ScrollToRow(0);
        Pump();
        var thumb = bar.ThumbForTesting;
        int x = bar.Width / 2;
        int top = thumb.Top + thumb.Height / 2;
        int bottom = track.Bottom - thumb.Height / 2;
        if (bottom <= top) bottom = top + 1;

        int at = top;
        int direction = 1;
        Send(bar, WmLButtonDown, x, at);
        Pump();

        var process = Process.GetCurrentProcess();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long cpu0 = process.TotalProcessorTime.Ticks;
        long thread0 = ThreadCpuTicks();
        long alloc0 = GC.GetTotalAllocatedBytes(precise: true);
        int paints0 = grid.PaintsForTesting;
        int mapPaints0 = map?.PaintsForTesting ?? 0;
        int resolved0 = map?.ColoursResolvedForTesting ?? 0;
        var clock = Stopwatch.StartNew();

        for (int i = 0; i < steps; i++)
        {
            at += direction * jump;
            if (at >= bottom) { at = bottom; direction = -1; }
            else if (at <= top) { at = top; direction = 1; }
            // A real mouse posts its reports to the queue and Windows coalesces them: however fast it moves,
            // a program only ever has one report waiting for it. Posting rather than sending also keeps the
            // window's own message handling on the path it takes in life, rather than nested inside a
            // send from this loop.
            if (rate > 0)
            {
                if (!PeekMessage(out _, bar.Handle, WmMouseMove, WmMouseMove, PmNoRemove))
                    Post(bar, WmMouseMove, x, at);
                PumpUntil(clock.Elapsed.TotalMilliseconds + 1000.0 / rate, clock);
            }
            else
            {
                Send(bar, WmMouseMove, x, at);
                Pump();
            }
        }

        clock.Stop();
        var result = new Measurement(
            Steps: steps,
            WallMs: clock.Elapsed.TotalMilliseconds,
            CpuMs: (process.TotalProcessorTime.Ticks - cpu0) / (double)TimeSpan.TicksPerMillisecond,
            UiCpuMs: (ThreadCpuTicks() - thread0) / (double)TimeSpan.TicksPerMillisecond,
            Bytes: GC.GetTotalAllocatedBytes(precise: true) - alloc0,
            GridPaints: grid.PaintsForTesting - paints0,
            MapPaints: (map?.PaintsForTesting ?? 0) - mapPaints0,
            RowsColoured: (map?.ColoursResolvedForTesting ?? 0) - resolved0);

        Send(bar, WmLButtonUp, x, at);
        Pump();
        return result;
    }

    /// <summary>Where the time in a frame actually goes. Each window is asked to repaint on its own, at the
    /// same scattered positions a drag puts it through and with nothing else allowed to run in between, so
    /// the three figures add up to the frame rather than overlapping it.</summary>
    private static void Parts(MainForm form, int steps)
    {
        var grid = form.GridForTesting;
        var bar = grid.ScrollBarForTesting;
        var map = grid.MatchMapForTesting;
        long rows = Math.Max(1, form.DocForTesting.RowCount);
        long stride = Math.Max(1, rows / 40);   // a 24px move on a full-height bar is about a fortieth of the file

        Report("text", Measure(steps, rows, stride, row => { grid.ScrollToRow(row); grid.Invalidate(); grid.Update(); }));
        Report("text, standing still", Measure(steps, rows, stride, _ => { grid.Invalidate(); grid.Update(); }));
        if (map is not null)
        {
            map.Visible = false;
            Report("text, map hidden", Measure(steps, rows, stride, row => { grid.ScrollToRow(row); grid.Invalidate(); grid.Update(); }));
            map.Visible = true;
            Report("minimap", Measure(steps, rows, stride, row => { grid.ScrollToRow(row); map.Invalidate(); map.Update(); }));
        }
        Report("scrollbar", Measure(steps, rows, stride, row => { grid.ScrollToRow(row); bar.Invalidate(); bar.Update(); }));
        Report("nothing", Measure(steps, rows, stride, grid.ScrollToRow));

        void Report(string what, (double Wall, double Cpu, double Bytes) part)
            => Console.WriteLine($"      {what,-18} {part.Wall,6:F2} ms/move wall | {part.Cpu,6:F2} ms/move cpu | {part.Bytes / 1024,7:F1} KB/move");
    }

    private static (double Wall, double Cpu, double Bytes) Measure(int steps, long rows, long stride, Action<long> step)
    {
        long row = 0;
        int direction = 1;
        for (int i = 0; i < 20; i++) step(i * stride % rows);   // warm

        var process = Process.GetCurrentProcess();
        long cpu0 = process.TotalProcessorTime.Ticks;
        long alloc0 = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();        for (int i = 0; i < steps; i++)
        {
            row += direction * stride;
            if (row >= rows) { row = rows - 1; direction = -1; }
            else if (row < 0) { row = 0; direction = 1; }
            step(row);
        }
        clock.Stop();
        return (clock.Elapsed.TotalMilliseconds / steps,
                (process.TotalProcessorTime.Ticks - cpu0) / (double)TimeSpan.TicksPerMillisecond / steps,
                (GC.GetTotalAllocatedBytes(precise: true) - alloc0) / (double)steps);
    }

    private readonly record struct Measurement(int Steps, double WallMs, double CpuMs, double UiCpuMs, long Bytes,
        int GridPaints, int MapPaints, int RowsColoured)
    {
        public static Measurement Worst => new(1, double.MaxValue, double.MaxValue, 0, 0, 0, 0, 0);

        public double CpuPerFrameMs => CpuMs / Math.Max(1, Steps);

        public override string ToString()
            => $"{WallMs / Steps,6:F2} wall | {CpuMs / Steps,6:F2} cpu | {UiCpuMs / Steps,6:F2} ui | " +
               $"{(CpuMs - UiCpuMs) / Steps,5:F2} elsewhere (ms/move) | " +
               $"{Bytes / (double)Steps / 1024,7:F1} KB/move | " +
               $"{100 * CpuMs / WallMs,5:F0}% of a core | " +
               $"{1000 * Steps / WallMs,5:F0} moves, {1000 * GridPaints / WallMs,5:F0} text and " +
               $"{1000 * MapPaints / WallMs,5:F0} map frames a second | " +
               $"{RowsColoured / (double)Steps,6:F0} rows coloured/move";
    }

    /// <summary>Processor time this thread alone has had, so the work the UI thread does can be told apart
    /// from the work it hands to the other cores - they cost the same battery but not the same lag.</summary>
    private static long ThreadCpuTicks()
    {
        GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user);
        return kernel + user;
    }

    // ---- driving ----

    private static void Send(Control control, int message, int x, int y)
        => SendMessage(control.Handle, message, 1, (y << 16) | (x & 0xFFFF));

    private static void Post(Control control, int message, int x, int y)
        => PostMessage(control.Handle, message, (IntPtr)1, (IntPtr)((y << 16) | (x & 0xFFFF)));

    /// <summary>Runs the queue dry, which is what happens between two reports of a moving mouse.</summary>
    private static void Pump()
    {
        for (var clock = Stopwatch.StartNew(); clock.ElapsedMilliseconds < 2_000;)
        {
            if (!Dispatch()) return;
        }
    }

    /// <summary>Runs the queue until a moment, whether or not it empties before then - which is how a
    /// program that cannot keep up with a mouse actually behaves. Waits on the queue rather than spinning
    /// on it, or the measurement would be of this loop.</summary>
    private static void PumpUntil(double untilMs, Stopwatch clock)
    {
        while (true)
        {
            Dispatch();
            double left = untilMs - clock.Elapsed.TotalMilliseconds;
            if (left <= 0) return;
            MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, (uint)Math.Max(1, Math.Ceiling(left)), QsAllInput, 0);
        }
    }

    /// <summary>Takes everything waiting and delivers it, as a message loop does. Not
    /// <see cref="Application.DoEvents"/>: that sets up a visual-styles context on every call, which is
    /// eight percent of a core at a thousand mouse reports a second and belongs to the harness rather than
    /// to the program being measured.</summary>
    private static bool Dispatch()
    {
        bool any = false;
        while (PeekMessage(out var message, IntPtr.Zero, 0, 0, PmRemove))
        {
            any = true;
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        return any;
    }

    // ---- the file to drag through ----

    /// <summary>A log of the shape these are: a timestamp, a thread, a level, a service and a message,
    /// about 120 characters, with the levels and services in the proportions a real one has - and, if asked,
    /// a payload on the end, because a log whose lines run off the side of the window is the common case and
    /// not the same measurement at all. Built once and kept, so repeated runs measure the program rather
    /// than the disk.</summary>
    private static string Fixture(int lines, int payload)
    {
        string path = Path.Combine(Path.GetTempPath(), $"cascade_bench_{lines}_{payload}.log");
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;

        string[] services = ["payment-svc", "auth-svc", "inventory", "gateway", "search-idx", "mailer"];
        string[] levels = ["INFO", "INFO", "INFO", "INFO", "DEBUG", "DEBUG", "WARN", "ERROR"];
        var random = new Random(1729);
        var text = new StringBuilder(160);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20);
        var stamp = new DateTime(2024, 5, 17, 9, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < lines; i++)
        {
            text.Clear();
            text.Append(stamp.AddMilliseconds(i * 7L).ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            text.Append(" [Thread-").Append(random.Next(1, 64).ToString("00", CultureInfo.InvariantCulture)).Append("] ");
            text.Append(levels[random.Next(levels.Length)].PadRight(5)).Append(' ');
            text.Append(services[random.Next(services.Length)].PadRight(12)).Append(' ');
            text.Append("request id=").Append(Guid.NewGuid().ToString("N").AsSpan(0, 16));
            text.Append(" user=").Append(random.Next(1, 500_000));
            text.Append(" amount=").Append((random.Next(1, 100_000) / 100.0).ToString("F2", CultureInfo.InvariantCulture));
            text.Append(" elapsed=").Append(random.Next(1, 999)).Append("ms");
            while (text.Length < payload)
                text.Append(" ctx.").Append(text.Length).Append("={\"key\":\"value\",\"n\":").Append(random.Next(1000)).Append('}');
            writer.WriteLine(text);
        }
        return path;
    }

    // ---- arguments ----

    private static string? Arg(string[] args, string prefix)
        => args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim('"');

    private static int IntArg(string[] args, string prefix, int fallback)
        => int.TryParse(Arg(args, prefix), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    private const uint QsAllInput = 0x04FF;

    [DllImport("user32.dll")]
    private static extern void MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint milliseconds,
        uint wakeMask, uint flags);

    /// <summary>Asks Windows for a millisecond timer while the bench runs. Without it a wait of one
    /// millisecond can take fifteen, and a thousand-reports-a-second mouse cannot be imitated at all.</summary>
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern void BeginTimePeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern void EndTimePeriod(uint milliseconds);

    // ---- what the micro-benchmark draws through ----

    private const int TransparentBackground = 1;
    private const int OpaqueBackground = 2;
    private const uint EtoOpaque = 0x0002;
    private const uint DtRight = 0x0002;
    private const uint DtSingleLine = 0x0020;
    private const uint DtNoClip = 0x0100;
    private const uint DtNoPrefix = 0x0800;

    private static uint ColorRef(Color colour) => (uint)(colour.R | (colour.G << 8) | (colour.B << 16));

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
        public Rect(int left, int top, int right, int bottom)
        {
            Left = left; Top = top; Right = right; Bottom = bottom;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern void SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern void SetBkColor(IntPtr hdc, uint colour);

    [DllImport("gdi32.dll")]
    private static extern void SetTextColor(IntPtr hdc, uint colour);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint colour);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern void ExtTextOut(IntPtr hdc, int x, int y, uint options, IntPtr rect,
        string text, uint count, IntPtr spacing);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern void ExtTextOut(IntPtr hdc, int x, int y, uint options, ref Rect rect,
        string text, uint count, IntPtr spacing);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern void DrawTextEx(IntPtr hdc, string text, int count, ref Rect rect, uint format, IntPtr parameters);

    [DllImport("user32.dll")]
    private static extern void FillRect(IntPtr hdc, ref Rect rect, IntPtr brush);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long created, out long exited,
        out long kernel, out long user);

    private static IntPtr SendMessage(IntPtr hWnd, int message, int wParam, int lParam)
        => SendMessage(hWnd, message, (IntPtr)wParam, (IntPtr)lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Msg message, IntPtr hWnd, uint first, uint last, uint remove);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern void TranslateMessage(ref Msg message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern void DispatchMessage(ref Msg message);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public Point Point;
    }
}

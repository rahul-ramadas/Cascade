using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Cascade.Core.Columns;
using Cascade.Core.Model;
using Cascade.Core.Persistence;

namespace Cascade.App;

/// <summary>
/// The README's pictures (<c>Cascade.exe --docshots &lt;outDir&gt;</c>).
///
/// Every image ships with the repository, so every image has to be reproducible and has to be made from a
/// log this file generates - never from whatever happens to be open on the machine. <see cref="SampleLog"/>
/// writes a couple of million lines of invented traffic and <see cref="SampleFilters"/> the filter set that
/// colours it; nothing here reads the user's files, their settings or their filter sets.
///
/// Stills are taken with <c>DrawToBitmap</c> off an invisible window, so a run needs no one at the keyboard
/// and lands the same pixels whatever else is on screen. Animations are written as numbered frames for
/// <c>scripts/Build-DocImages.ps1</c> to hand to ffmpeg, because the thing worth showing about nesting or
/// about <c>Ctrl+N</c> is the change, and a still cannot carry it.
/// </summary>
internal static class DocShots
{
    /// <summary>Big enough that the match map has to compress, and that the counts in the filter list are
    /// worth reading. Small enough to generate and index in a few seconds.</summary>
    private const int LineCount = 2_000_000;

    /// <summary>The window every picture is taken through, in DEVICE pixels - so on a scaled display this
    /// is a smaller window than the number suggests, and the status bar is the first thing to feel it. The
    /// bar carries two paths, a slot for whatever the app is busy with, five counts and the elapsed
    /// measurement; the paths are the only part of it that gives way, so a window that cannot afford the
    /// rest wears them down to a character or two. MEASURED at 1480: 182px left for both paths, which is
    /// how they came out as a single letter after an ellipsis. Wide enough here that a file name fits.
    /// </summary>
    private const int WindowWidth = 1900, WindowHeight = 900;

    /// <summary>Frames a second the animations are written at; <c>Build-DocImages.ps1</c> must use the
    /// same number or every GIF plays at the wrong speed.</summary>
    private const int Fps = 10;

    private static string _dir = "";

    public static int Run(string[] args)
    {
        _dir = args.FirstOrDefault(a => !a.StartsWith('-')) ?? Path.Combine(Path.GetTempPath(), "cascade_docshots");
        Directory.CreateDirectory(_dir);

        // A throwaway settings directory, as the other harnesses use: this builds a real MainForm, and a
        // real MainForm writes preferences, recent files and the last filter file as it goes.
        string settingsDir = Path.Combine(Path.GetTempPath(), "cascade_docshots_cfg_" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CASCADE_SETTINGS_DIR", settingsDir);
        Environment.SetEnvironmentVariable("CASCADE_UPDATE", "off");

        Console.WriteLine($"Writing to {_dir}");
        var sw = Stopwatch.StartNew();
        string sampleDir = Path.Combine(Path.GetTempPath(), "cascade-sample-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sampleDir);
        // Named as a reader's own files would be named: the title bar and the status bar both show these,
        // and "cascade-sample-7f5444ea....log" in a screenshot says nothing except that a machine made it.
        string log = SampleLog(Path.Combine(sampleDir, "orders-2026-08-05.log"));
        string filters = SampleFilters(Path.Combine(sampleDir, "payments.cascade"));
        Console.WriteLine($"sample log: {new FileInfo(log).Length / (1024 * 1024)} MB in {sw.ElapsedMilliseconds} ms");

        var settings = new AppSettings
        {
            ShowLineNumbers = true,
            ShowMatchMap = true,
            ShowFilterPresets = true,
            ShowFilterTooltips = true,
        };

        var form = new MainForm(settings, new MachineState(), [log, "/Filters:" + filters])
        {
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
            Size = new Size(WindowWidth, WindowHeight),
            WindowState = FormWindowState.Normal,
            Opacity = 0,   // rendered through DrawToBitmap, which reads the control tree rather than the screen
            NoSavePrompt = true,
            UpdateNoticeOverride = "Will update to v2026.8.62 on restart",
        };
        form.Show();
        form.Activate();
        Ready(form);
        // Setting Size in the initializer is not enough: the window has no handle yet, and how much of it
        // survives handle creation depends on the display the run lands on - MEASURED 1600x1000 back from
        // a 1900x900 ask. Every crop here is cut at a fixed offset from an edge, so a window that is not
        // the size asked for silently moves what is in the pictures. Set it again once it is really there.
        if (form.Size != new Size(WindowWidth, WindowHeight))
        {
            Console.WriteLine($"  window came up {form.Size.Width}x{form.Size.Height}; sizing it again");
            form.Size = new Size(WindowWidth, WindowHeight);
            Ready(form);
        }
        if (form.Size != new Size(WindowWidth, WindowHeight))
            throw new InvalidOperationException($"window will not size to {WindowWidth}x{WindowHeight}");
        // The filter pane defaults to a third of the window; the list is the star of half these pictures, so
        // it gets enough room for every filter in the sample set and the presets beside them.
        form.SplitForTesting.SplitterDistance = form.SplitForTesting.Height - 350;

        Hero(form);
        FilterListShots(form);
        PresetShots(form);
        FindShots(form);
        MatchMapShot(form);
        HoverTipShot(form);
        FieldShots(form);
        ElapsedShot(form);
        EncodingShot(form);

        NestingAnimation(form);
        NewFilterAnimation(form);
        MarkerAnimation(form);
        DimOrHideAnimation(form);
        FieldAnimation(form);

        // Last of the shots that drive the window, so the state it leaves cannot reach any other picture.
        CropShot(form);

        DialogShots(form);

        form.Close();
        form.Dispose();

        try { File.Delete(log); } catch { /* ignore */ }
        try { File.Delete(filters); } catch { /* ignore */ }
        try { if (Directory.Exists(sampleDir)) Directory.Delete(sampleDir, true); } catch { /* ignore */ }
        try { if (Directory.Exists(settingsDir)) Directory.Delete(settingsDir, true); } catch { /* ignore */ }

        Console.WriteLine($"done in {sw.Elapsed.TotalSeconds:N1}s");
        return 0;
    }

    // ---------------------------------------------------------------- sample data

    /// <summary>Invented traffic through an invented shop: six services, three levels, and one bad quarter
    /// of an hour in the middle where the payment service starts declining charges. The incident is what
    /// makes the pictures worth looking at - it gives the match map a band, the filters a story, and the
    /// presets something to be named after.</summary>
    private static string SampleLog(string path)
    {
        string[] services = ["api-gateway  ", "payment-svc  ", "inventory-svc", "auth-svc     ", "search-svc   ", "notify-svc   "];
        string[] gateway =
        [
            "GET /v1/orders/{0} -> 200 in {1}ms", "POST /v1/checkout -> 201 in {1}ms",
            "GET /v1/catalog?page={2} -> 200 in {1}ms", "GET /v1/orders/{0}/status -> 200 in {1}ms",
        ];
        string[] payment = ["captured charge for order {0} ({1}ms)", "authorised card ending 4417 for order {0}", "refund settled for order {0}"];
        string[] inventory = ["reserved 2 of SKU-{2}9 for order {0}", "released hold on SKU-{2}9", "stock check for SKU-{2}9 took {1}ms"];
        string[] auth = ["issued token for session {0}", "refreshed token for session {0}", "revoked session {0}"];
        string[] search = ["indexed {2} documents in {1}ms", "query \"winter jacket\" matched {2} documents"];
        string[] notify = ["queued order-confirmation mail for order {0}", "delivered push notification for order {0}"];

        var rng = new Random(20260805);
        long ms = new DateTime(2026, 8, 5, 9, 31, 17, DateTimeKind.Utc).Ticks / TimeSpan.TicksPerMillisecond;

        int incidentFrom = LineCount / 2, incidentTo = incidentFrom + LineCount / 40;

        using var writer = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20);
        var line = new StringBuilder(128);
        for (int i = 0; i < LineCount; i++)
        {
            ms += rng.Next(1, 4);
            bool incident = i >= incidentFrom && i < incidentTo;

            int roll = rng.Next(1000);
            string level, service, message;
            int order = 40_000 + rng.Next(9_999), took = rng.Next(3, 240), n = rng.Next(100, 999);

            if (incident && roll < 260)
            {
                level = "ERROR";
                service = services[1];
                message = roll % 3 == 0
                    ? $"charge declined for order {order}: insufficient_funds"
                    : roll % 3 == 1 ? $"charge declined for order {order}: card_expired"
                                    : $"upstream timeout talking to acquirer after {took + 2000}ms";
            }
            else if (incident && roll < 520)
            {
                level = "WARN ";
                service = services[1];
                message = $"retrying charge for order {order} (attempt {roll % 3 + 2} of 3)";
            }
            else if (roll < 6)
            {
                level = "ERROR";
                service = services[rng.Next(services.Length)];
                message = roll % 2 == 0 ? $"upstream timeout talking to acquirer after {took + 2000}ms"
                                        : $"request for order {order} failed: connection reset";
            }
            else if (roll < 70)
            {
                level = "WARN ";
                service = services[rng.Next(services.Length)];
                message = roll % 2 == 0 ? $"slow query took {took + 900}ms for order {order}"
                                        : $"retrying charge for order {order} (attempt 2 of 3)";
            }
            else
            {
                level = "INFO ";
                int which = rng.Next(services.Length);
                service = services[which];
                string[] pool = which switch
                {
                    0 => gateway, 1 => payment, 2 => inventory, 3 => auth, 4 => search, _ => notify
                };
                message = string.Format(pool[rng.Next(pool.Length)], order, took, n);
            }

            var at = new DateTime(ms * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
            line.Clear();
            line.Append('[').Append(at.ToString("yyyy-MM-ddTHH:mm:ss.fff")).Append(']')
                .Append('[').Append(service).Append(']')
                .Append('[').Append(level).Append("] ")
                .Append(message);
            writer.Write(line);
            writer.Write('\n');
        }
        return path;
    }

    private static Filter Make(string text, string description, RgbColor? back = null, RgbColor? fore = null,
                               bool bold = false, bool enabled = true, bool exclude = false, bool regex = false)
    {
        var f = new Filter
        {
            Enabled = enabled,
            Description = description,
            Kind = exclude ? FilterKind.Exclude : FilterKind.Include,
            Match = { Text = text, Regex = regex },
        };
        if (back is { } b) f.Style.Background = b;
        if (fore is { } o) f.Style.Foreground = o;
        if (bold) f.Style.Bold = true;
        return f;
    }

    private static RgbColor Rgb(int rgb) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    /// <summary>The filter set the pictures are taken through: three levels of nesting down the errors
    /// branch, an exclude, a few unremarkable siblings, and presets over them.</summary>
    private static string SampleFilters(string path)
    {
        var filters = new FilterCollection();

        var errors = Make("[ERROR]", "errors", back: Rgb(0xC62828), fore: Rgb(0xFFFFFF), bold: true);
        var inPayments = Make("payment-svc", "in payments", back: Rgb(0xAD1457), fore: Rgb(0xFFFFFF));
        var declined = Make("declined", "declined charges", back: Rgb(0x6A1B9A), fore: Rgb(0xFFFFFF));
        var warnings = Make("[WARN ]", "warnings", back: Rgb(0xFFE082));
        var slow = Make("slow query", "slow queries", back: Rgb(0xA5D6A7));
        var inventory = Make("inventory-svc", "inventory", fore: Rgb(0x1565C0), enabled: false);
        var timeouts = Make("upstream timeout", "acquirer timeouts", back: Rgb(0x00838F), fore: Rgb(0xFFFFFF), enabled: false);
        var noise = Make("search-svc", "search chatter", exclude: true, enabled: false);

        filters.Add(errors);
        filters.Add(inPayments, errors);
        filters.Add(declined, inPayments);
        filters.Add(warnings);
        filters.Add(slow, warnings);
        filters.Add(timeouts);
        filters.Add(inventory);
        filters.Add(noise);

        filters.Presets.Add(new FilterPreset("payment incident", [errors.Id, inPayments.Id, declined.Id]));
        filters.Presets.Add(new FilterPreset("slow queries", [warnings.Id, slow.Id]));
        filters.Presets.Add(new FilterPreset("timeouts", [timeouts.Id]));

        var columns = new ColumnSpec { Template = "{[*]}{[*]}{[*]} {*}" };
        columns.Reset();
        string[] names = ["Time", "Service", "Level", "Message"];
        for (int i = 0; i < columns.Columns.Count && i < names.Length; i++) columns.Columns[i].Name = names[i];

        CascadeFile.Save(path, filters, columns);
        return path;
    }

    // ---------------------------------------------------------------- stills

    /// <summary>The first row of the bad quarter of an hour: the errors, the retries and the declines are all
    /// in one screenful there, which is what every picture of the log wants to be standing on.</summary>
    private const long Incident = LineCount / 2;

    private static void Hero(MainForm form)
    {
        form.GridForTesting.ScrollToRow(Incident + 4);
        form.FilterTreeForTesting.SelectForTesting(Root(form, 0));
        Ready(form);
        Save(Whole(form), "hero");
    }

    private static void FilterListShots(MainForm form)
    {
        var tree = form.FilterTreeForTesting;
        Ready(form);
        Save(Crop(form, Around(form, tree)), "filter-list");

        // Several filters picked out, two of them nested: the strip left of the text says which are in the
        // group, and harder on the row the keyboard is standing on.
        tree.ClickFilterForTesting(Root(form, 0));
        tree.ClickFilterForTesting(Root(form, 1).Children[0], Keys.Shift);
        Ready(form);
        Save(Crop(form, Around(form, tree)), "filter-multi");

        tree.SetSearchText("payment");
        Ready(form);
        Save(Crop(form, Around(form, tree)), "filter-search");
        tree.HideSearch();
        tree.ClickFilterForTesting(Root(form, 0));
        Ready(form);
    }

    private static void PresetShots(MainForm form)
    {
        var presets = form.PresetsForTesting;
        var doc = form.DocForTesting;
        var before = doc.Filters.EnumerateDepthFirst().ToDictionary(f => f.Id, f => f.Enabled);

        // One preset in effect and a different one highlighted, which is the distinction the pane exists to
        // draw and the one nothing else in the window says.
        presets.TickForTesting("payment incident");
        presets.SelectForTesting("slow queries");
        Ready(form);

        var pane = Around(form, presets);
        int rows = presets.RowBoundsForTesting(2).Bottom;
        Save(Crop(form, new Rectangle(pane.X, pane.Y, pane.Width, Math.Min(pane.Height, rows + 70))), "presets");

        foreach (var f in doc.Filters.EnumerateDepthFirst()) f.Enabled = before[f.Id];
        form.FilterTreeForTesting.RefreshCheckStates();
        doc.ApplyFilters();
        Ready(form);
    }

    private static void FindShots(MainForm form)
    {
        form.GridForTesting.ScrollToRow(Incident + 4);
        form.PressCmdKeyForTesting(Keys.Control | Keys.F);
        form.FindBarForTesting.SetTermForTesting("declined", 8, 0);
        form.FindBarForTesting.EnterForTesting();
        // The tally is half of what the bar is worth photographing, and it only lands once the sweep is done.
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 30_000;)
        {
            Ready(form, 2000);
            if (form.FindBarForTesting.MessageForTesting().Contains(" of ", StringComparison.Ordinal)) break;
        }
        Console.WriteLine("  find says: " + form.FindBarForTesting.MessageForTesting());
        form.GridForTesting.ScrollToRow(Incident + 4);
        Ready(form);
        Save(Crop(form, LogArea(form, rows: 18)), "find");
        form.CloseFindForTesting();
        Ready(form);
    }

    /// <summary>The map beside the log, at its full height: the whole file at a pixel a line, with the bad
    /// quarter of an hour showing as a band, markers down the left edge and find hits down the right. The
    /// filter list is put away for this one - the point of the thing is the shape of a file too big to
    /// scroll through, and half a map says nothing about that.</summary>
    /// <summary>The window cropped to one stretch of the log: the chip in the middle of the menu bar, the line
    /// numbers still the file's own, and every count of what is left.</summary>
    private static void CropShot(MainForm form)
    {
        var grid = form.GridForTesting;
        var doc = form.DocForTesting;

        grid.SelectLinesForTesting(Incident - 20, Incident + 260);
        Ready(form);
        form.PressCmdKeyForTesting(Keys.Control | Keys.OemOpenBrackets);
        Ready(form);
        Save(Whole(form), "crop");

        form.PressCmdKeyForTesting(Keys.Control | Keys.OemCloseBrackets);
        Ready(form);
        if (doc.Crop is not null) throw new InvalidOperationException("the crop did not lift");
        grid.SelectLinesForTesting(Incident, Incident);
        Ready(form);
    }

    private static void MatchMapShot(MainForm form)    {
        var grid = form.GridForTesting;
        var doc = form.DocForTesting;
        for (int i = 0; i < 9; i++) doc.Markers.Toggle(Incident + i * 180_000 - 800_000, i % 4);
        grid.InvalidateMatchMap();

        // The log pane borrows the whole window rather than the filter list being put away: hiding the list
        // relays the pane beside it, and every crop taken after this one would come out a few pixels wider.
        int was = form.SplitForTesting.SplitterDistance;
        form.SplitForTesting.SplitterDistance = form.SplitForTesting.Height - 40;

        form.PressCmdKeyForTesting(Keys.Control | Keys.F);
        form.FindBarForTesting.SetTermForTesting("card_expired", 12, 0);
        form.FindBarForTesting.EnterForTesting();
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 30_000;)
        {
            Ready(form, 2000);
            if (form.FindBarForTesting.MessageForTesting().Contains(" of ", StringComparison.Ordinal)) break;
        }
        grid.ScrollToRow(Incident + 4);
        Ready(form);

        // The strip is cut from the right-hand edge, so the log has to still be there when it gets to the
        // map - the point of the picture is that the two sit side by side. The window every other shot is
        // taken through is wider than this log's longest line, which would leave a band of empty rows
        // between them, so this one is taken through a window narrowed by exactly that slack.
        var full = form.Size;
        var pane = Placed(form, grid);
        int slack = pane.Right - pane.X - grid.ContentWidthForTesting;
        if (slack > 0)
        {
            form.Size = new Size(full.Width - slack, full.Height);
            Ready(form);
        }

        // Below the find bar: the bar has a picture of its own, and its close button at the top of this one
        // reads as a stray mark.
        var area = Placed(form, grid);
        int top = area.Y + form.FindBarHeightForTesting;
        Save(Crop(form, new Rectangle(area.Right - 330, top, 330, area.Bottom - top)), "match-map");

        form.Size = full;
        Ready(form);

        form.CloseFindForTesting();
        form.SplitForTesting.SplitterDistance = was;
        Ready(form);
    }

    private static void HoverTipShot(MainForm form)
    {
        var grid = form.GridForTesting;
        var doc = form.DocForTesting;

        // A line that several filters answer for, one of them switched off, since naming the filter you had
        // turned off is the whole reason the tip is worth a picture.
        var timeouts = Root(form, 2);
        timeouts.Enabled = false;
        long row = -1;
        for (long r = Incident; r < Incident + 20_000; r++)
            if (doc.GetLineText(doc.RowToLine(r)).Contains("upstream timeout", StringComparison.Ordinal)) { row = r; break; }
        if (row < 0) { Console.WriteLine("hover-tip: SKIPPED (no line to hover)"); return; }

        grid.ScrollToRow(row - 6);
        Ready(form);

        // The tip is a window of its own: DrawToBitmap on the form cannot see it, and a WinForms ToolTip
        // keeps itself hidden over a window that is not the active one - which no window here is.
        grid.ShowTipsWhenInactiveForTesting(true);

        (Bitmap Picture, Point Where)? tip = null;
        for (int attempt = 0; attempt < 5 && tip is null; attempt++)
        {
            grid.HideTipForTesting();
            grid.HoverRowForTesting(row, 420);
            grid.ShowTipNowForTesting();
            Application.DoEvents();
            Thread.Sleep(200);
            Application.DoEvents();
            tip = TooltipOver(form);
        }

        var shot = Whole(form);
        if (tip is null) Console.WriteLine($"hover-tip: no tip window (text was {grid.ShownTipForTesting.Replace('\n', '/')})");
        else
            using (tip.Value.Picture)
            using (var g = Graphics.FromImage(shot))
                g.DrawImage(tip.Value.Picture, tip.Value.Where);

        grid.HideTipForTesting();
        grid.ShowTipsWhenInactiveForTesting(false);
        Application.DoEvents();

        var area = LogArea(form, rows: 14);
        Save(Crop(shot, new Rectangle(area.X, area.Y, area.Width, area.Height)), "hover-tip");
        shot.Dispose();
        Ready(form);
    }

    private static void FieldShots(MainForm form)
    {
        var doc = form.DocForTesting;
        form.GridForTesting.ScrollToRow(Incident + 4);
        doc.Columns.Layout = FieldLayout.Columns;
        form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.C);
        Ready(form);
        form.GridForTesting.FitColumnsToWindow();
        Ready(form);
        Save(Crop(form, LogArea(form, rows: 18)), "fields-columns");

        form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.X);
        doc.Columns.Columns[0].Visible = false;
        doc.Columns.Columns[2].Visible = false;
        form.GridForTesting.RefreshView();
        Ready(form);
        Save(Crop(form, LogArea(form, rows: 18)), "fields-inline");

        foreach (var c in doc.Columns.Columns) c.Visible = true;
        form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.C);
        Ready(form);
    }

    /// <summary>The elapsed margin, photographed where it says something a reader could not get any other
    /// way: with the log filtered down to the errors, so the figures are the time between one error and the
    /// next rather than between one line and the next. The line numbers beside them skip, which is what
    /// makes that legible without a word of explanation.</summary>
    private static void ElapsedShot(MainForm form)
    {
        var doc = form.DocForTesting;
        var grid = form.GridForTesting;
        if (doc.Clock is null) { Console.WriteLine("elapsed: SKIPPED (no clock in the sample log)"); return; }

        var wasOn = doc.Filters.EnumerateDepthFirst().Where(f => f.Enabled).ToList();
        foreach (var f in wasOn) f.Enabled = false;
        var errors = doc.Filters.Roots.First(f => f.Description == "errors");
        errors.Enabled = true;
        form.FilterTreeForTesting.RefreshCheckStates();
        doc.ApplyFilters();
        Ready(form, 30_000);

        bool wasFiltered = doc.FilteredMode;
        if (!wasFiltered) form.PressCmdKeyForTesting(Keys.Control | Keys.H);
        Ready(form, 30_000);

        // Just before the incident: the errors are minutes apart, then seconds, then a flood of them.
        long row = Math.Max(0, doc.RowAtOrAfterLine(Incident) - 4);
        grid.ScrollToRow(row);
        Ready(form);
        Save(Crop(form, LogArea(form, rows: 16)), "elapsed");

        // ...and the same lines measured from one of them, which is the other question the column answers:
        // not "how fast is this going" but "how long after the thing I care about did each of these happen".
        // The caret is moved off the reference afterwards, or the selection would cover the very thing the
        // picture is of - the reference's own row picked out where the figures are.
        grid.GoToLine(doc.RowToLine(row + 4));
        form.PressCmdKeyForTesting(Keys.Control | Keys.R);
        grid.GoToLine(doc.RowToLine(row + 9));
        grid.ScrollToRow(row);
        Ready(form);
        Save(Crop(form, LogArea(form, rows: 16)), "elapsed-reference");
        form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.R);
        form.ClearReferenceForTesting();

        if (!wasFiltered) form.PressCmdKeyForTesting(Keys.Control | Keys.H);
        errors.Enabled = false;
        foreach (var f in wasOn) f.Enabled = true;
        form.FilterTreeForTesting.RefreshCheckStates();
        doc.ApplyFilters();
        Ready(form, 30_000);
    }

    /// <summary>The Encoding drop-down as Windows draws it, which is the only place the app says what it
    /// detected. Shown the way a mouse would show it, then drawn straight off the pop-up window.</summary>
    private static void EncodingShot(MainForm form)
    {
        if (Menu(form, "View", "Encoding") is not { } encoding) return;
        if (Menu(form, "View") is { } view) view.ShowDropDown();
        encoding.ShowDropDown();
        Settle();
        var drop = encoding.DropDown;
        if (drop.Width <= 1 || drop.Height <= 1) return;
        using var bmp = new Bitmap(drop.Width, drop.Height);
        drop.DrawToBitmap(bmp, new Rectangle(0, 0, drop.Width, drop.Height));
        Save((Bitmap)bmp.Clone(), "encoding");
        encoding.HideDropDown();
        if (Menu(form, "View") is { } v2) v2.HideDropDown();
        Settle();
    }

    // ---------------------------------------------------------------- animations

    /// <summary>One keystroke that nests a filter under another, and the count falling as it lands. Nesting
    /// is the one idea in the application a still picture cannot carry: <i>inventory-svc</i> on its own
    /// matches a fifth of the file; under <i>warnings</i> it matches inventory warnings and nothing else.
    /// The number saying so is the whole demonstration.</summary>
    private static void NestingAnimation(MainForm form)
    {
        var doc = form.DocForTesting;
        var tree = form.FilterTreeForTesting;
        var inventory = doc.Filters.Roots.First(f => f.Description == "inventory");
        var warnings = doc.Filters.Roots.First(f => f.Description == "warnings");

        // Directly below the warnings branch, which is where Alt+Right will take it in.
        int home = doc.Filters.Roots.IndexOf(inventory);
        doc.Filters.Move(inventory, null, doc.Filters.Roots.IndexOf(warnings) + 1);
        inventory.Enabled = true;
        tree.Rebuild();
        doc.ApplyFilters();
        Ready(form);

        var frames = new Frames("nesting");
        var area = Around(form, tree);
        frames.Hold(form, area, 16);

        tree.ClickFilterForTesting(inventory);
        Ready(form);
        frames.Hold(form, area, 12);

        tree.MoveSelected(Keys.Right);
        doc.ApplyFilters();
        Ready(form);
        frames.Hold(form, area, 32);
        frames.Done();

        doc.Filters.Move(inventory, null, home);
        inventory.Enabled = false;
        tree.Rebuild();
        doc.ApplyFilters();
        Ready(form);
    }

    /// <summary>Picking a request id out of a line and pressing Ctrl+N. The quickest way to chase one
    /// identifier through a log, and nothing in the window advertises it.
    ///
    /// <para>The dialog Ctrl+N raises is modal, and nobody is here to answer it - so it is built and drawn
    /// exactly as the command builds it, then laid over the frame. The pixels are the dialog's own; only the
    /// waiting for a click is left out.</para></summary>
    private static void NewFilterAnimation(MainForm form)
    {
        var grid = form.GridForTesting;
        var doc = form.DocForTesting;
        int before = doc.Filters.Roots.Count;

        long row = -1;
        for (long r = Incident + 20; r < Incident + 400; r++)
            if (grid.DisplayTextForTesting(r).Contains("declined for order ", StringComparison.Ordinal)) { row = r; break; }
        if (row < 0) { Console.WriteLine("new-filter: SKIPPED (no line to select in)"); return; }
        grid.ScrollToRow(row - 8);
        Ready(form);

        var frames = new Frames("new-filter");
        var area = Rectangle.Union(LogArea(form, rows: 13), Around(form, form.FilterTreeForTesting));
        frames.Hold(form, area, 10);

        // The order number inside the line, dragged over a character at a time so the selection grows on
        // screen the way a hand would grow it.
        string text = grid.DisplayTextForTesting(row);
        int at = text.IndexOf("declined for order ", StringComparison.Ordinal) + "declined for order ".Length;
        for (int len = 1; len <= 5; len++)
        {
            grid.DragForTesting(row, grid.XForCharForTesting(row, at), grid.XForCharForTesting(row, at + len));
            Ready(form);
            frames.Hold(form, area, 2);
        }
        frames.Hold(form, area, 6);

        string seed = MainForm.SeedPatternFromLine(grid.SelectedText ?? text.Substring(at, 5));
        var made = new Filter { Enabled = true, Description = "order " + seed, Match = { Text = seed } };
        var free = LuckyColors.Free(doc.Filters.EnumerateDepthFirst(), made);
        if (free.Count > 0) { made.Style.Background = free[0].Back; made.Style.Foreground = free[0].Fore; }

        using (var dialog = new FilterEditDialog(made.Clone(newIds: false), isNew: true,
                                                 doc.Filters.EnumerateDepthFirst().ToList()))
        {
            // The animation drops the filter at the end of the list with nothing selected, so the dialog is
            // shown offering exactly that - the picture would otherwise say one place and the list show
            // another.
            dialog.OfferPlacements(NewFilterPlacement.Default, null, addAtTop: false);
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(0, 0);
            dialog.Opacity = 0;
            dialog.Show();
            Settle();
            using var picture = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(picture, new Rectangle(0, 0, dialog.Width, dialog.Height));
            dialog.Close();

            var where = new Point(area.X + (area.Width - picture.Width) / 2, area.Y + 24);
            frames.HoldOver(form, area, 22, picture, where);
        }

        doc.Filters.Add(made, null, doc.Filters.Roots.Count);
        form.FilterTreeForTesting.SyncToModel();
        form.FilterTreeForTesting.RevealFilter(made);
        doc.ApplyFilters();
        Ready(form, 6000);
        frames.Hold(form, area, 26);
        frames.Done();

        while (doc.Filters.Roots.Count > before) doc.Filters.Remove(doc.Filters.Roots[^1]);
        grid.ClickForTesting(row, 4);
        form.FilterTreeForTesting.Rebuild();
        doc.ApplyFilters();
        Ready(form);
    }

    private static void MarkerAnimation(MainForm form)
    {
        var grid = form.GridForTesting;
        var doc = form.DocForTesting;
        doc.Markers.Clear();
        long top = Incident + 200;
        grid.ScrollToRow(top);
        Ready(form);

        var frames = new Frames("markers");
        var area = LogArea(form, rows: 16);
        frames.Hold(form, area, 8);

        long[] rows = [top + 2, top + 6, top + 11];
        for (int i = 0; i < rows.Length; i++)
        {
            grid.SelectRowForAccessibility(rows[i]);
            grid.RefreshView();
            Ready(form);
            frames.Hold(form, area, 3);
            doc.Markers.Toggle(doc.RowToLine(rows[i]), i);
            grid.RefreshView();
            grid.InvalidateMatchMap();
            Ready(form);
            frames.Hold(form, area, 7);
        }

        // ...then walked with the plain number keys, which is the half nobody finds.
        grid.ScrollToRow(top);
        grid.SelectRowForAccessibility(top);
        Ready(form);
        frames.Hold(form, area, 6);
        for (int i = 0; i < 3; i++)
        {
            grid.PressKeyForTesting(Keys.D1 + i);
            Ready(form);
            frames.Hold(form, area, 9);
        }
        frames.Done();

        doc.Markers.Clear();
        grid.RefreshView();
        grid.InvalidateMatchMap();
        Ready(form);
    }

    private static void DimOrHideAnimation(MainForm form)
    {
        var grid = form.GridForTesting;
        grid.ScrollToRow(Incident + 60);
        Ready(form);

        var frames = new Frames("dim-or-hide");
        var area = LogArea(form, rows: 18);
        for (int i = 0; i < 2; i++)
        {
            frames.Hold(form, area, 16);
            form.PressCmdKeyForTesting(Keys.Control | Keys.H);
            Ready(form, 3000);
            frames.Hold(form, area, 16);
            form.PressCmdKeyForTesting(Keys.Control | Keys.H);
            Ready(form, 3000);
        }
        frames.Done();
    }

    private static void FieldAnimation(MainForm form)
    {
        var doc = form.DocForTesting;
        var grid = form.GridForTesting;
        grid.ScrollToRow(Incident + 4);
        doc.Columns.Layout = FieldLayout.Columns;
        form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.C);
        Ready(form);
        grid.FitColumnsToWindow();
        Ready(form);

        var frames = new Frames("fields");
        var area = LogArea(form, rows: 16);
        frames.Hold(form, area, 16);

        // A column put away from the header's own menu: the punctuation goes with it, which is the part
        // worth seeing.
        doc.Columns.Columns[0].Visible = false;
        grid.RefreshView();
        Ready(form);
        frames.Hold(form, area, 14);

        form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.X);
        Ready(form);
        frames.Hold(form, area, 20);

        doc.Columns.Columns[2].Visible = false;
        grid.RefreshView();
        Ready(form);
        frames.Hold(form, area, 18);

        form.PressCmdKeyForTesting(Keys.Control | Keys.Shift | Keys.C);
        Ready(form);
        frames.Hold(form, area, 14);
        frames.Done();

        foreach (var c in doc.Columns.Columns) c.Visible = true;
        doc.Columns.Layout = FieldLayout.Columns;
        Ready(form);
    }

    // ---------------------------------------------------------------- dialogs

    private static void DialogShots(MainForm form)
    {
        var doc = form.DocForTesting;
        var declined = Root(form, 0).Children[0].Children[0];

        ShotDialog(new FilterEditDialog(declined, isNew: false), "filter-edit");

        var free = LuckyColors.Free(doc.Filters.EnumerateDepthFirst(), declined);
        ShotDialog(new PaletteDialog(free, "declined", null, visibleRows: 9), "paint-chips");

        var group = new List<Filter> { Root(form, 0), Root(form, 0).Children[0], Root(form, 1) };
        ShotDialog(new AppearanceDialog(group, doc.Filters.EnumerateDepthFirst().ToList(),
                       new ResolvedStyle(Rgb(0xFFFFFF), Rgb(0x1F1F1F), false, false)),
                   "appearance");

        string[] samples =
        [
            "[2026-08-05T09:44:02.118][payment-svc  ][ERROR] charge declined for order 48210: insufficient_funds",
            "[2026-08-05T09:44:02.204][api-gateway  ][INFO ] GET /v1/orders/48210/status -> 200 in 41ms",
            "[2026-08-05T09:44:02.377][inventory-svc][WARN ] slow query took 1180ms for order 48210",
        ];
        var spec = doc.Columns.Clone();
        spec.Enabled = true;
        // Handed the clock the app found for itself, as the real menu entry does - without it the dialog
        // says no times could be read at all, which is not what anyone opening it on this log would see.
        ShotDialog(new ColumnsDialog(spec, samples, 0, doc.Clock), "field-settings");

        ShotDialog(new PreferencesDialog(new AppSettings()), "preferences");
    }

    private static void ShotDialog(Form dialog, string name)
    {
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new Point(0, 0);
        dialog.Opacity = 0;
        dialog.Show();
        Settle();
        var bmp = new Bitmap(Math.Max(1, dialog.Width), Math.Max(1, dialog.Height));
        dialog.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        Save(bmp, name);
        dialog.Close();
        dialog.Dispose();
    }

    // ---------------------------------------------------------------- plumbing

    private static Filter Root(MainForm form, int index) => form.DocForTesting.Filters.Roots[index];

    private static ToolStripMenuItem? Menu(MainForm form, params string[] path)
    {
        ToolStripItemCollection? items = form.MainMenuStrip?.Items;
        ToolStripMenuItem? found = null;
        foreach (string want in path)
        {
            if (items is null) return null;
            found = items.OfType<ToolStripMenuItem>()
                         .FirstOrDefault(i => (i.Text ?? "").Replace("&", "").TrimEnd('.', '\u2026') == want);
            if (found is null) return null;
            items = found.DropDownItems;
        }
        return found;
    }

    /// <summary>A child control's rectangle in the coordinates <see cref="Control.DrawToBitmap"/> uses for a
    /// window - which start at the outside of the frame, not at the client area.</summary>
    private static Rectangle Placed(MainForm form, Control child)
    {
        var onScreen = child.RectangleToScreen(new Rectangle(Point.Empty, child.Size));
        return new Rectangle(onScreen.X - form.Bounds.X, onScreen.Y - form.Bounds.Y, child.Width, child.Height);
    }

    private static Rectangle Around(MainForm form, Control child, int left = 0, int top = 0, int right = 0, int bottom = 0)
    {
        var r = Placed(form, child);
        r = new Rectangle(r.X - left, r.Y - top, r.Width + left + right, r.Height + top + bottom);
        return Rectangle.Intersect(r, new Rectangle(0, 0, form.Width, form.Height));
    }

    /// <summary>The log view, cut to a whole number of rows so no picture ends on half a line. The cut is
    /// taken from what was actually painted, not from a row height multiplied out, since a wrapped row is
    /// taller than its neighbours.</summary>
    private static Rectangle LogArea(MainForm form, int rows)
    {
        var grid = form.GridForTesting;
        var r = Placed(form, grid);
        long first = grid.FirstPaintedRowForTesting >= 0 ? grid.FirstPaintedRowForTesting : grid.FirstRowForTesting;
        int take = Math.Max(1, Math.Min(rows, grid.RowsPaintedForTesting - 1));
        int height = grid.RowTopForTesting(first + take);
        if (height < grid.RowPitch * 2) height = rows * Math.Max(1, grid.RowPitch);
        return Rectangle.Intersect(new Rectangle(r.X, r.Y, r.Width, Math.Min(r.Height, height)),
                                   new Rectangle(0, 0, form.Width, form.Height));
    }

    private static Bitmap Whole(MainForm form)
    {
        var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
        return bmp;
    }

    private static Bitmap Crop(MainForm form, Rectangle area)
    {
        using var whole = Whole(form);
        return Crop(whole, area);
    }

    private static Bitmap Crop(Bitmap source, Rectangle area)
    {
        area = Rectangle.Intersect(area, new Rectangle(0, 0, source.Width, source.Height));
        if (area.Width <= 0 || area.Height <= 0) area = new Rectangle(0, 0, source.Width, source.Height);
        var bmp = new Bitmap(area.Width, area.Height);
        using (var g = Graphics.FromImage(bmp)) g.DrawImage(source, new Rectangle(0, 0, area.Width, area.Height), area, GraphicsUnit.Pixel);
        return bmp;
    }

    private static void Save(Bitmap bmp, string name)
    {
        using (bmp)
        {
            bmp.Save(Path.Combine(_dir, name + ".png"), ImageFormat.Png);
            Console.WriteLine($"{name}: {bmp.Width}x{bmp.Height}");
        }
    }

    /// <summary>A numbered run of PNGs for ffmpeg. Holding a state is written as repeated frames rather than
    /// as a duration, so the assembler needs to know nothing but the frame rate.</summary>
    private sealed class Frames
    {
        private readonly string _name, _dir;
        private int _n;

        public Frames(string name)
        {
            _name = name;
            _dir = Path.Combine(DocShots._dir, "frames", name);
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
            Directory.CreateDirectory(_dir);
        }

        public void Hold(MainForm form, Rectangle area, int frames) => HoldOver(form, area, frames, null, Point.Empty);

        /// <summary>As <see cref="Hold"/>, with a picture laid over the frame - a modal dialog drawn
        /// separately, since showing one for real would stop the run dead.</summary>
        public void HoldOver(MainForm form, Rectangle area, int frames, Bitmap? over, Point where)
        {
            using var shot = Crop(form, area);
            if (over is not null)
                using (var g = Graphics.FromImage(shot))
                    g.DrawImage(over, new Point(where.X - area.X, where.Y - area.Y));
            string first = Path.Combine(_dir, $"f{_n:0000}.png");
            shot.Save(first, ImageFormat.Png);
            _n++;
            for (int i = 1; i < frames; i++, _n++) File.Copy(first, Path.Combine(_dir, $"f{_n:0000}.png"));
        }

        public void Done() => Console.WriteLine($"{_name}: {_n} frames ({_n / (double)Fps:N1}s at {Fps} fps)");
    }

    /// <summary>Waits for indexing and filtering to finish and for the window to stop repainting, so a shot
    /// is of a settled window rather than of one halfway through the work.
    ///
    /// <para>Ends by drawing the window into a bitmap nobody keeps. The view records which rows it painted
    /// where as it paints them, and every crop below is worked out from that record - so it has to be a
    /// record of the state being photographed, not of the one before it.</para></summary>
    private static void Ready(MainForm form, int capMs = 20_000)
    {
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < capMs;)
        {
            Application.DoEvents();
            Thread.Sleep(10);
            if (sw.ElapsedMilliseconds > 120 && !form.IsBusyForHarness) break;
        }
        form.GridForTesting.RefreshView();
        Settle();
        Whole(form).Dispose();
    }

    private static void Settle()
    {
        for (var sw = Stopwatch.StartNew(); sw.ElapsedMilliseconds < 400;)
        {
            Application.DoEvents();
            Thread.Sleep(12);
            Application.DoEvents();
            if (!PeekMessage(out _, IntPtr.Zero, 0, 0, 0)) return;
        }
    }

    // ---------------------------------------------------------------- the tooltip

    /// <summary>The tip is a pop-up window of its own, so <c>DrawToBitmap</c> on the form cannot see it.
    /// This finds the one this process has up and asks Windows to draw it, which is the real thing rather
    /// than something drawn here to look like it.</summary>
    private static (Bitmap Picture, Point Where)? TooltipOver(MainForm form)
    {
        IntPtr found = IntPtr.Zero;
        var name = new char[128];
        uint mine = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (GetWindowThreadProcessId(hwnd, out uint owner) == 0 || owner != mine || !IsWindowVisible(hwnd)) return true;
            int taken = GetClassName(hwnd, name, name.Length);
            // WinForms superclasses the common control, so the name is
            // "WindowsForms10.tooltips_class32.app.0.<hash>" rather than the bare class.
            if (taken <= 0 || !new string(name, 0, taken).Contains("tooltips_class32", StringComparison.Ordinal)) return true;
            if (!GetWindowRect(hwnd, out var r) || r.Right - r.Left < 40) return true;
            found = hwnd;
            return false;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero)
        {
            if (Environment.GetEnvironmentVariable("CASCADE_DOCSHOTS_TRACE") == "1")
            {
                EnumWindows((hwnd, _) =>
                {
                    if (GetWindowThreadProcessId(hwnd, out uint owner) == 0 || owner != mine) return true;
                    int taken = GetClassName(hwnd, name, name.Length);
                    GetWindowRect(hwnd, out var r);
                    Console.WriteLine($"  window {new string(name, 0, Math.Max(0, taken))} visible={IsWindowVisible(hwnd)} {r.Right - r.Left}x{r.Bottom - r.Top}");
                    return true;
                }, IntPtr.Zero);
            }
            return null;
        }
        if (!GetWindowRect(found, out var box)) return null;

        int w = box.Right - box.Left, h = box.Bottom - box.Top;
        if (w <= 0 || h <= 0) return null;

        var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try { PrintWindow(found, hdc, 0); }
            finally { g.ReleaseHdc(hdc); }
        }
        return (bmp, new Point(box.Left - form.Bounds.X, box.Top - form.Bounds.Y));
    }

    private delegate bool EnumWindowProc(IntPtr window, IntPtr param);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, [Out] char[] name, int max);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT box);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr hdc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam, LParam;
        public uint Time;
        public int X, Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessage(out MSG message, IntPtr window, uint first, uint last, uint remove);
}
